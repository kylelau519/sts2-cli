"""Thin subprocess-driven session over sts2-cli's JSON protocol, decoupled from the
terminal UI in play.py. Returns typed Decision objects (models.parse_decision) and takes
action names/kwargs instead of raw dicts -- this is the library entry point external
callers (Vakuu) are meant to drive directly, alongside models.py/actions.py.

Caller is responsible for calling installation.ensure_setup() once (not per session) before
creating any Sts2Session, to avoid redundant filesystem checks when spawning many sessions.
"""
from __future__ import annotations

import json
import random
import select
import subprocess
import time

from actions import action
from installation import DOTNET, PROJECT
from models import Decision, parse_decision

# Per-engine-round-trip budget: generous enough to absorb a slow response under heavy concurrent
# load (many worker processes' dotnet subprocesses contending for the same CPU cores), but finite
# -- see Sts2EngineTimeoutError's own docstring for why finite matters at all. Comfortably below
# facility_common.py's own 120s _wait_for_turn_result budget for a whole turn (which spans
# multiple engine calls across cfg.branches_per_turn branches), so a single stuck call trips this
# first rather than silently eating that entire outer budget.
_DEFAULT_ENGINE_TIMEOUT = 90.0


class Sts2ActionError(RuntimeError):
    ...


class Sts2EngineTimeoutError(RuntimeError):
    """The sts2-cli engine didn't respond to one call within `timeout` seconds. Confirmed live
    (not hypothetical): a training_claws.sh run stalled for 18+ minutes with every worker at 0%
    CPU, one main-worker process already an unreaped zombie, and zero new output -- traced to
    Sts2Session._read_raw()'s readline() blocking forever with no timeout at all, so a single
    wedged sts2-cli subprocess (e.g. from a load/save-divergence edge case putting RunSimulator in
    an unexpected state) permanently ate one worker-pool slot with no way to notice or recover.

    Raising this doesn't undo whatever half-finished state the stuck call left behind -- treat the
    session as unrecoverable once this fires, not just slow: Sts2Session's subprocess is killed
    before this is raised (see _kill()), so a caller should drop the session and get a fresh one.
    Sts2BufferSession has no subprocess to kill (see its own docstring on this exception) -- the
    stuck CLR call keeps running forever on an abandoned thread, so the entire OWNING PROCESS is
    what's actually unrecoverable there, not just the session object."""


def _to_decision(raw: dict) -> Decision:
    if raw.get("type") == "error":
        raise Sts2ActionError(raw.get("message", "unknown error"))
    return parse_decision(raw)


class Sts2Session:
    def __init__(self, logger=None, timeout: float = _DEFAULT_ENGINE_TIMEOUT):
        dotnet = str(DOTNET or "dotnet")
        self.proc = subprocess.Popen(
            [dotnet, "run", "--no-build", "--project", str(PROJECT)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, bufsize=1,
        )
        self.logger = logger
        self.timeout = timeout
        self.action_log: list[dict] = []
        self.character: str | None = None
        self.seed: str | None = None
        self.ascension: int = 0
        self._map_cache: dict | None = None
        self._read_raw()  # consume the initial "ready" line

    def _kill(self) -> None:
        """Emergency teardown for a subprocess that's just been declared hung -- unlike close(),
        doesn't bother trying to send "quit" first (the whole point is this subprocess isn't
        responding to writes either)."""
        try:
            self.proc.kill()
            self.proc.wait(timeout=5)
        except Exception:
            pass

    def _read_raw(self) -> dict:
        deadline = time.monotonic() + self.timeout
        while True:
            remaining = deadline - time.monotonic()
            # select() on the pipe's fd first, rather than calling readline() directly, is what
            # makes this interruptible at all -- readline() itself has no timeout parameter and
            # would just block forever past the deadline once called.
            if remaining <= 0 or not select.select([self.proc.stdout], [], [], remaining)[0]:
                self._kill()
                raise Sts2EngineTimeoutError(
                    f"sts2-cli subprocess didn't respond within {self.timeout}s -- treating it "
                    f"as hung and killing it"
                )
            line = self.proc.stdout.readline()
            if line == "":
                raise RuntimeError("sts2-cli subprocess closed unexpectedly")
            line = line.strip()
            if line.startswith("{"):
                return json.loads(line)

    def _send_raw(self, cmd: dict) -> dict:
        self.proc.stdin.write(json.dumps(cmd) + "\n")
        self.proc.stdin.flush()
        return self._read_raw()

    def start(self, character: str, seed: str, ascension: int = 0) -> Decision:
        self.character, self.seed, self.ascension = character, seed, ascension
        self._map_cache = None
        raw = self._send_raw({
            "cmd": "start_run", "character": character, "seed": seed, "ascension": ascension,
        })
        return _to_decision(raw)

    def open_from_checkpoint(self, character: str, seed: str, ascension: int,
                              actions: list[dict]) -> Decision:
        """Reconstruct a state reached by another session, by replaying its action log
        against a fresh subprocess. This is how branching works: there's no in-memory
        snapshot/fork of a combat state, so a branch is a new process that walks the same
        path and then diverges."""
        decision = self.start(character, seed, ascension)
        for cmd in actions:
            raw = self._send_raw(cmd)
            decision = _to_decision(raw)
        self.action_log = list(actions)
        return decision

    def load_and_resume_combat(self, checkpoint_path: str, entry_action: dict,
                                actions: list[dict]) -> Decision:
        """Faster alternative to open_from_checkpoint for reconstructing a state inside a
        combat: load_save(checkpoint_path) resumes from a native checkpoint taken just before
        the room-entry action that started this combat, instead of replaying the whole run from
        StartRun. entry_action re-enters the same room/encounter; actions are the in-combat
        actions already taken this combat, bringing the session up to the branch point.
        action_log ends up as [entry_action, *actions] -- this session has no memory of anything
        before the checkpoint, so a caller that needs the full history for output is responsible
        for prepending its own pre-combat prefix onto action_log afterward. See
        Sts2BufferSession.load_and_resume_combat (buffer_env.py) for the in-process counterpart
        this mirrors -- unlike that one, no manual error check is needed per action here, since
        _to_decision already raises Sts2ActionError on an error response."""
        self.load_save(checkpoint_path)
        combined = [entry_action, *actions]
        decision = None
        for cmd in combined:
            raw = self._send_raw(cmd)
            decision = _to_decision(raw)
        self.action_log = combined
        return decision

    def step(self, action_name: str, **kwargs) -> Decision:
        cmd = action(action_name, **kwargs)
        self.action_log.append(cmd)
        if self.logger:
            self.logger.log_action(cmd)
        raw = self._send_raw(cmd)
        decision = _to_decision(raw)
        if self.logger:
            self.logger.log_state(decision)
        # select_map_node is the only action that moves current_coord -- and it's also what
        # carries a run from one act's boss into the next act's freshly generated map, so this
        # one invalidation rule covers both "moved a floor" and "map is a wholly different act"
        # without needing to key the cache by act separately.
        if action_name == "select_map_node":
            self._map_cache = None
        return decision

    def randomize_draw_pile(self, state: Decision, seed: int | None = None) -> None:
        """
        Creates an explicit chance node by explicitly reshuffling the draw pile 
        in the C# game engine. Call this at the branch point before playing a card.
        """
        if getattr(state, "decision", None) != "combat_play" or not getattr(state, "draw_pile", None):
            return
        rng = random.Random(seed)
        card_ids = [
            card.id.split(":")[-1] if card.id and ":" in card.id else card.id 
            for card in state.draw_pile
        ]
        rng.shuffle(card_ids)
        self._send_raw({
            "cmd": "set_draw_order",
            "cards": card_ids
        })

    def get_map(self) -> dict:
        if self._map_cache is None:
            self._map_cache = self._send_raw({"cmd": "get_map"})
        return self._map_cache

    def save(self, path: str) -> dict:
        """Write a native sts2 checkpoint at `path` (RunSimulator.SaveCheckpoint) -- resumable
        with load_save/open_from_checkpoint, unlike the replay-log format _save_game writes.
        Raises Sts2ActionError if there's no active run or the engine-side write fails."""
        result = self._send_raw({"cmd": "save", "path": path})
        if not result.get("success"):
            raise Sts2ActionError(result.get("message", "save failed"))
        return result

    def load_save(self, path: str) -> Decision:
        """Load a native checkpoint written by save() and resume into it -- counterpart to
        save(). Unlike open_from_checkpoint (replays an action list against a fresh StartRun),
        this resumes directly from the save's own embedded state/RNG, so it doesn't need
        character/seed/ascension or a prior action log; action_log is reset (empty) since there's
        no history to speak of, only the state the checkpoint captured. Raises Sts2ActionError if
        the file is missing, unparseable, or fails schema validation."""
        self._map_cache = None
        self.action_log = []
        raw = self._send_raw({"cmd": "load_save", "path": path})
        return _to_decision(raw)

    def close(self):
        try:
            self._send_raw({"cmd": "quit"})
        except Exception:
            pass
        try:
            self.proc.terminate()
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()
