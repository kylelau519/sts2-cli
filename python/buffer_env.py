"""In-process counterpart to env.py's Sts2Session, over the buffer protocol instead of JSON
stdin/stdout: no subprocess (RunSimulator runs directly in this Python process via pythonnet), and
no per-step dict/pydantic decode -- every call hands back a BufferDecision, a thin wrapper around a
numpy float32 array plus BufferSession-consistent offset math (see buffer_layout.py), not an object
graph. That object-graph decode step (JSON -> dict -> pydantic validate_python) was measured as the
actual bottleneck in whispering_earring's branch-worker/MCTS hot path -- see its README -- separate
from (and on top of) the subprocess-spawn cost env.py's own docstring already addresses. This session
type is for that hot path specifically; the main self-play line keeps using Sts2Session/pydantic
Decision, since it never touches combat_play directly (see facility.py's main_worker_loop).

Same public shape as Sts2Session (start/open_from_checkpoint/step/randomize_draw_pile/close,
action_log/character/seed/ascension) so callers built against one can switch to the other with
minimal churn -- the difference callers actually have to handle is what `step()`/`start()` return:
a BufferDecision (numpy-backed, buffer_layout accessors) instead of a pydantic Decision.
"""
from __future__ import annotations

import json

import numpy as np

import buffer_layout as bl
from actions import action
from clr_bridge import load_clr

_token_map: dict[int, str] | None = None  # int -> "CATEGORY:ID", loaded once per process


def _load_token_map() -> dict[int, str]:
    global _token_map
    if _token_map is None:
        raw = json.loads(load_clr().BufferSession.ExportTokenMap())
        _token_map = {int(k): v for k, v in raw.items()}
    return _token_map


def decode_token(token: int) -> str | None:
    """token -> the raw game entry id (e.g. 5 -> "STRIKE"), or None for UnknownToken (0) / an
    empty-slot sentinel (-1) / an unrecognized token. Builds on ExportTokenMap's "CATEGORY:ID"
    strings (see Tokenizer.cs) -- category is discarded here since every caller already knows
    which category a given buffer field holds (a card slot's token is always a card id, etc.), so
    there's nothing to disambiguate."""
    if token <= 0:
        return None
    key = _load_token_map().get(token)
    if key is None or key == "UNKNOWN":
        return None
    _, _, entry = key.partition(":")
    return entry


class BufferDecision:
    """arr: a numpy float32 array this instance owns exclusively (a fresh copy out of
    BufferSession's reused native buffer -- see Sts2BufferSession._decision(); never aliases the
    C# side's buffer, so a Node cached in mcts.py's tree stays valid across later step() calls on
    the same session, which reuse that native buffer in place)."""

    __slots__ = ("arr", "decision")

    def __init__(self, arr: np.ndarray):
        self.arr = arr
        self.decision = bl.decision_name(arr)

    @property
    def player_hp(self) -> int:
        return bl.player_hp(self.arr)

    @property
    def player_max_hp(self) -> int:
        return bl.player_max_hp(self.arr)

    @property
    def victory(self) -> bool:
        return bl.victory(self.arr) if self.decision == "game_over" else False


class Sts2BufferSession:
    def __init__(self, logger=None):
        self._session = load_clr().BufferSession()
        actual_size = int(self._session.BufferSize)
        assert actual_size == bl.TOTAL_SIZE, (
            f"buffer_layout.py ({bl.TOTAL_SIZE}) is out of sync with BufferSession.BufferSize "
            f"({actual_size}) -- RunSimulator.States.Buffer.cs's BufferLayout changed without "
            f"the Python mirror being updated to match"
        )
        self.logger = logger
        self.action_log: list[dict] = []
        self.character: str | None = None
        self.seed: str | None = None
        self.ascension: int = 0

    def _decision(self, raw) -> BufferDecision:
        """raw is the C# float[] BufferSession returns -- the SAME native array on every call
        (see BufferSession.cs), so this always copies into a fresh numpy array rather than
        wrapping it, unlike a zero-copy view would."""
        return BufferDecision(np.array(raw, dtype=np.float32))

    def start(self, character: str, seed: str, ascension: int = 0) -> BufferDecision:
        self.character, self.seed, self.ascension = character, seed, ascension
        self.action_log = []
        return self._decision(self._session.StartRun(character, seed, ascension))

    def open_from_checkpoint(self, character: str, seed: str, ascension: int,
                              actions: list[dict]) -> BufferDecision:
        """Same replay-to-reconstruct-state approach as Sts2Session.open_from_checkpoint -- see
        its docstring. Intermediate replayed decisions are never wrapped in a BufferDecision (no
        numpy copy, no accessor use): the caller only ever inspects the *final* decision reached,
        same as the JSON path's own `decision` local being overwritten every loop iteration."""
        self.character, self.seed, self.ascension = character, seed, ascension
        raw = self._session.StartRun(character, seed, ascension)
        for entry in actions:
            args = entry.get("args")
            raw = self._session.Action(entry["action"], json.dumps(args) if args else None)
        self.action_log = list(actions)
        return self._decision(raw)

    def step(self, action_name: str, **kwargs) -> BufferDecision:
        cmd = action(action_name, **kwargs)  # validates args the same way Sts2Session.step does
        self.action_log.append(cmd)
        if self.logger:
            self.logger.log_action(cmd)
        args = cmd.get("args")
        raw = self._session.Action(action_name, json.dumps(args) if args else None)
        decision = self._decision(raw)
        if self.logger:
            self.logger.log_state(decision)
        return decision

    def randomize_draw_pile(self, state: BufferDecision, seed: int | None = None) -> None:
        """See RunSimulator.RandomizeDrawPile -- an int-seeded in-place shuffle instead of the
        JSON protocol's card-id-list set_draw_order (no ids to round-trip in-process)."""
        if getattr(state, "decision", None) != "combat_play":
            return
        self._session.RandomizeDrawPile(seed if seed is not None else 0)

    def close(self) -> None:
        pass  # in-process -- no subprocess to tear down; the C# side is GC'd with this object
