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
import subprocess

from actions import action
from installation import DOTNET, PROJECT
from models import Decision, parse_decision
from save_load import _save_game


class Sts2ActionError(RuntimeError):
    ...

def _to_decision(raw: dict) -> Decision:
    if raw.get("type") == "error":
        raise Sts2ActionError(raw.get("message", "unknown error"))
    return parse_decision(raw)


class Sts2Session:
    def __init__(self, logger=None):
        dotnet = str(DOTNET or "dotnet")
        self.proc = subprocess.Popen(
            [dotnet, "run", "--no-build", "--project", str(PROJECT)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, bufsize=1,
        )
        self.logger = logger
        self.action_log: list[dict] = []
        self.character: str | None = None
        self.seed: str | None = None
        self.ascension: int = 0
        self._map_cache: dict | None = None
        self._read_raw()  # consume the initial "ready" line

    def _read_raw(self) -> dict:
        while True:
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

    def save(self, path: str) -> None:
        _save_game(path, self.character, self.seed, self.action_log)

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
