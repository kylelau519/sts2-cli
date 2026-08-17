"""Regression tests for native save/load behavior."""

from conftest import Game


def test_load_map_save_does_not_retrigger_neow(tmp_path):
    save_path = tmp_path / "map_select.save"

    game = Game()
    try:
        state = game.start(seed="sl1")
        state = game.skip_neow(state)
        assert state["decision"] == "map_select"

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["type"] == "save_result"
        assert save_result["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "map_select"
    finally:
        game.close()


def test_load_pre_neow_save_preserves_neow_choice(tmp_path):
    save_path = tmp_path / "pre_neow.save"

    game = Game()
    try:
        state = game.start(seed="sl2")
        assert state["decision"] == "event_choice"

        save_result = game.send({"cmd": "write_continue_save", "path": str(save_path)})
        assert save_result["type"] == "save_result"
        assert save_result["success"] is True
    finally:
        game.close()

    game = Game()
    try:
        state = game.send({"cmd": "load_save", "path": str(save_path)})
        assert state["decision"] == "event_choice"
    finally:
        game.close()


def test_load_save_in_same_process(tmp_path):
    save1 = tmp_path / "run1.save"
    save2 = tmp_path / "run2.save"

    game = Game()
    try:
        # Start run 1 and save
        state1 = game.start(seed="sameproc1", character="Ironclad")
        state1 = game.skip_neow(state1)
        res1 = game.send({"cmd": "write_continue_save", "path": str(save1)})
        assert res1["success"] is True

        # Start run 2 on the same simulator process without closing
        state2 = game.start(seed="sameproc2", character="Silent")
        assert state2["decision"] == "event_choice"
        res2 = game.send({"cmd": "write_continue_save", "path": str(save2)})
        assert res2["success"] is True

        # Load run 1 back into the same process without restarting
        loaded1 = game.send({"cmd": "load_save", "path": str(save1)})
        assert loaded1["decision"] == "map_select"
        assert loaded1["player"]["character_id"] == "CHARACTER.IRONCLAD"

        # Load run 2 back into the same process without restarting
        loaded2 = game.send({"cmd": "load_save", "path": str(save2)})
        assert loaded2["decision"] == "event_choice"
        assert loaded2["player"]["character_id"] == "CHARACTER.SILENT"
    finally:
        game.close()

