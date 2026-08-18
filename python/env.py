"""Thin subprocess-driven session over sts2-cli's JSON protocol, decoupled from the
terminal UI in play.py. Returns typed Decision objects (models.parse_decision) and takes
action names/kwargs instead of raw dicts -- this is the library entry point external
callers (Vakuu) are meant to drive directly, alongside models.py/actions.py.

Caller is responsible for calling installation.ensure_setup() once (not per session) before
creating any Sts2Session, to avoid redundant filesystem checks when spawning many sessions.
"""
from __future__ import annotations

import json
import subprocess

from actions import action
from installation import DOTNET, PROJECT
from models import Decision, parse_decision
from save_load import _save_game


class Sts2ActionError(RuntimeError):
    """Raised when sts2-cli responds with {"type": "error", ...} instead of a decision --
    e.g. an invalid action for the current state, or an internal engine exception
    (Program.cs's catch-all handler wraps both the same way). play.py's MainLoop checks
    for this by hand (`state.get("type") == "error"`); this is the same check, centralized
    so every Sts2Session caller gets a clear exception instead of parse_decision's opaque
    ValidationError on a shape it was never meant to handle."""


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
        return decision

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
