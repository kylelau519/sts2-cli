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


def test_choice_to_action_map_select():
    state = {
        "decision": "map_select",
        "choices": [{"col": 0, "row": 1, "type": "Monster"}, {"col": 1, "row": 1, "type": "Event"}],
    }
    cmd = play.choice_to_action("map_select", "1", state)
    assert cmd == {"cmd": "action", "action": "select_map_node", "args": {"col": 1, "row": 1}}


def test_choice_to_action_combat_play():
    state = {
        "decision": "combat_play",
        "hand": [{"index": 0, "name": "Strike"}, {"index": 1, "name": "Defend"}],
        "enemies": [{"index": 0, "hp": 50}],
    }
    assert play.choice_to_action("combat_play", "e", state) == {"cmd": "action", "action": "end_turn"}
    assert play.choice_to_action("combat_play", "0", state) == {"cmd": "action", "action": "play_card", "args": {"card_index": 0}}


def test_do_branch_creates_file_without_advancing_actions(tmp_path, monkeypatch):
    import json
    monkeypatch.setattr(play, "SAVE_DIR", str(tmp_path))

    loop = play.MainLoop(character="Ironclad", seed="branch_test")
    loop.action_log = [{"cmd": "action", "action": "start_node"}]

    state = {
        "decision": "rest_site",
        "context": {"act": 1, "floor": 6},
    }

    loop.do_branch("0", state)

    # Main action log should not be modified
    assert len(loop.action_log) == 1

    # Check that the branched file was created
    saved_files = list(tmp_path.glob("Ironclad_branch_test_A1_F6_rest_site_branch_0_*.json"))
    assert len(saved_files) == 1

    with open(saved_files[0]) as f:
        data = json.load(f)

    assert data["character"] == "Ironclad"
    assert data["seed"] == "branch_test"
    assert len(data["actions"]) == 2
    assert data["actions"][-1] == {"cmd": "action", "action": "choose_option", "args": {"option_index": 0}}


