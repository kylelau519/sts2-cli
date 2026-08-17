"""Tests for CLI play helpers."""

from __future__ import annotations

import importlib.util
import pathlib
import sys


ROOT = pathlib.Path(__file__).resolve().parents[1]
PLAY_PATH = ROOT / "python" / "play.py"

sys.path.insert(0, str(ROOT / "python"))
spec = importlib.util.spec_from_file_location("play_module_for_tests", PLAY_PATH)
play = importlib.util.module_from_spec(spec)
assert spec and spec.loader
spec.loader.exec_module(play)


def test_quit_save_defaults_to_save_dir(monkeypatch):
    monkeypatch.setattr("builtins.input", lambda prompt="": "y")

    path = play._quit_with_save(None, "Ironclad", "seed123")

    assert path is not None
    assert path.startswith(play.SAVE_DIR)
    assert path.endswith(".save")


def test_get_input_load_command(monkeypatch, tmp_path):
    test_save = tmp_path / "quick_load.json"
    test_save.write_text('{"character": "Ironclad", "seed": "123", "actions": []}', encoding="utf-8")

    from terminal import get_input, _LoadRequested

    inputs = iter([f"load {test_save}"])
    monkeypatch.setattr("builtins.input", lambda prompt="": next(inputs))

    try:
        get_input("Choose option")
        assert False, "Expected _LoadRequested exception"
    except _LoadRequested as e:
        assert e.path == str(test_save)

