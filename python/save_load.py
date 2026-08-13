"""Save and load functionality for sts2-cli (replay saves & native saves)."""

import glob as globmod
import json
import os
import platform
import sys
from datetime import datetime

from display import c

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SAVE_DIR = os.path.join(ROOT, "saves")


def _find_native_save_dir():
    """Auto-detect the game's save directory."""
    system = platform.system()
    patterns = []
    if system == "Darwin":
        patterns = [
            os.path.expanduser("~/Library/Application Support/SlayTheSpire2/steam/*/profile*/saves"),
        ]
    elif system == "Linux":
        patterns = [
            os.path.expanduser("~/.local/share/SlayTheSpire2/steam/*/profile*/saves"),
            os.path.expanduser("~/.config/unity3d/MegaCrit/Slay the Spire 2/steam/*/profile*/saves"),
        ]
    elif system == "Windows":
        appdata = os.environ.get("APPDATA", "")
        localappdata = os.environ.get("LOCALAPPDATA", "")
        patterns = [
            os.path.join(appdata, "SlayTheSpire2", "steam", "*", "profile*", "saves"),
            os.path.join(localappdata, "SlayTheSpire2", "steam", "*", "profile*", "saves"),
        ]
    for pat in patterns:
        matches = globmod.glob(pat)
        for d in matches:
            if os.path.isfile(os.path.join(d, "current_run.save")):
                return d
        if matches:
            return matches[0]
    return None


def _id_to_name(model_id):
    """Convert model ID like 'CARD.STRIKE_NECROBINDER' to readable name."""
    if not model_id:
        return "?"
    parts = model_id.split(".", 1)
    name = parts[-1] if len(parts) > 1 else model_id
    return name.replace("_", " ").title()


def show_native_save(save_path):
    """Parse and display a native current_run.save file."""
    try:
        with open(save_path) as f:
            data = json.load(f)
    except json.JSONDecodeError as e:
        print(f"Error: Save file is not valid JSON: {save_path}")
        print(f"  {e}")
        sys.exit(1)

    print(f"\n{'═' * 60}")
    print(f"  Native Save File")
    print(f"  {save_path}")
    print(f"{'═' * 60}")
    seed = data.get("rng", {}).get("seed", "?")
    ascension = data.get("ascension", 0)
    act_idx = data.get("current_act_index", 0)
    acts = data.get("acts", [])
    act_name = _id_to_name(acts[act_idx]["id"]) if act_idx < len(acts) else "?"
    run_time = data.get("run_time", 0)
    run_min = run_time // 60
    run_sec = run_time % 60

    print(f"\n  Seed: {seed}")
    print(f"  Ascension: {ascension}")
    print(f"  Act: {act_idx + 1} ({act_name})")
    print(f"  Time: {run_min}m{run_sec:02d}s")
    print(f"  Schema: v{data.get('schema_version','?')}")
    room = data.get("pre_finished_room", {})
    if room:
        room_type = room.get("room_type", "?")
        enc = room.get("encounter_id") or room.get("event_id") or ""
        print(f"  Room: {room_type}" + (f" ({_id_to_name(enc)})" if enc else ""))

    visited = data.get("visited_map_coords", [])
    if visited:
        print(f"  Map: Floor {len(visited)} ({len(visited)} nodes visited)")

    for player in data.get("players", []):
        char_name = _id_to_name(player.get("character_id", "?"))
        hp = player.get("current_hp", 0)
        max_hp = player.get("max_hp", 0)
        gold = player.get("gold", 0)
        energy = player.get("max_energy", 3)

        print(f"\n  {'─' * 50}")
        print(f"  {char_name}  HP: {hp}/{max_hp}  Gold: {gold}  Energy: {energy}")

        deck = player.get("deck", [])
        print(f"\n  Deck ({len(deck)}):")
        card_counts = {}
        for card in deck:
            cid = _id_to_name(card.get("id", "?"))
            up = card.get("current_upgrade_level", 0)
            key = f"{cid}{'+'*up if up else ''}"
            card_counts[key] = card_counts.get(key, 0) + 1
        for name, cnt in sorted(card_counts.items()):
            print(f"    • {name}" + (f" x{cnt}" if cnt > 1 else ""))

        relics = player.get("relics", [])
        if relics:
            print(f"\n  Relics ({len(relics)}):")
            for r in relics:
                print(f"    🔶 {_id_to_name(r.get('id', '?'))}")

        potions = player.get("potions", [])
        if potions:
            print(f"\n  Potions ({len(potions)}):")
            for p in potions:
                print(f"    🧪 [{p.get('slot_index', '?')}] {_id_to_name(p.get('id', '?'))}")

    if acts:
        print(f"\n  {'─' * 50}")
        print(f"  Acts summary:")
        for i, act in enumerate(acts):
            act_id = _id_to_name(act.get("id", "?"))
            rooms_data = act.get("rooms", {})
            boss = _id_to_name(rooms_data.get("boss_id", ""))
            normals = rooms_data.get("normal_encounters_visited", 0)
            elites = rooms_data.get("elite_encounters_visited", 0)
            events = rooms_data.get("events_visited", 0)
            bosses = rooms_data.get("boss_encounters_visited", 0)
            marker = " ◀" if i == act_idx else ""
            print(f"    Act {i+1}: {act_id}  Boss: {boss}  "
                  f"[M{normals} E{elites} ?{events} B{bosses}]{marker}")

    print(f"\n{'═' * 60}\n")


def _save_game(save_path, character, seed, action_log):
    """Write action replay save file."""
    os.makedirs(os.path.dirname(save_path), exist_ok=True)
    data = {"character": character, "seed": seed, "actions": action_log}
    with open(save_path, "w") as f:
        json.dump(data, f, indent=2)


def _load_game(save_path):
    """Read action replay save file. Returns (character, seed, actions)."""
    try:
        with open(save_path) as f:
            data = json.load(f)
    except json.JSONDecodeError as e:
        print(f"Error: Save file is not valid JSON: {save_path}")
        print(f"  {e}")
        sys.exit(1)
    if "actions" not in data:
        print(f"Error: Not a replay save file (missing 'actions' key): {save_path}")
        sys.exit(1)
    return data["character"], data["seed"], data["actions"]


def _list_saves():
    """List available save files (replay .json and native .save files)."""
    if not os.path.isdir(SAVE_DIR):
        return []
    saves = []
    for f in sorted(os.listdir(SAVE_DIR)):
        path = os.path.join(SAVE_DIR, f)
        if f.endswith(".json"):
            # Only list .json files that are replay saves (have "actions" key)
            try:
                with open(path) as fh:
                    d = json.load(fh)
                if "actions" not in d:
                    continue  # skip non-replay JSON files
                saves.append({
                    "file": f, "path": path, "type": "replay",
                    "character": d.get("character", "?"),
                    "seed": d.get("seed", "?"),
                    "actions": len(d.get("actions", [])),
                })
            except Exception:
                pass
        elif f.endswith(".save"):
            # Native save files
            saves.append({
                "file": f, "path": path, "type": "native",
                "character": "?", "seed": "?", "actions": "—",
            })
    return saves


def _quit_with_save(native_save_path, character, seed):
    """Return the save path to use on quit, or None to quit without saving."""
    print()
    try:
        ans = input("  Save before quitting? (y/n): ").strip().lower()
    except (EOFError, KeyboardInterrupt):
        ans = "n"

    if ans not in ("y", "yes"):
        print("  Quitting without saving.")
        return None

    if native_save_path:
        return native_save_path

    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    char_tag = (character or "run").lower()
    seed_tag = seed or "random"
    return os.path.join(SAVE_DIR, f"{char_tag}_{seed_tag}_{ts}.save")


def _show_quit_save_result(result):
    """Print save confirmation or failure from a quit_result response."""
    save_result = result.get("save") if result else None
    if save_result and save_result.get("success"):
        sz = save_result.get("size", 0)
        save_path = save_result.get("path")
        print(f"  {c('Saved!', 'green')} ({sz // 1024}KB)")
        if save_path:
            print(f"  Save path: {c(save_path, 'cyan')}")
            print(f"  Continue later: python3 play.py --continue {save_path}")
    elif save_result:
        print(f"  {c('Save failed:', 'red')} {save_result.get('message', '?')}")


def _writeback_continue_save(send_fn, native_save_path):
    """Best-effort writeback for --continue sessions when a stable map checkpoint is reached."""
    if not native_save_path:
        return
    result = send_fn({"cmd": "write_continue_save", "path": native_save_path})
    if result and result.get("success"):
        sz = result.get("size", 0)
        print(f"  {c(f'Save written ({sz//1024}KB)', 'dim')}")
    elif result:
        print(f"  {c('Save failed:', 'red')} {result.get('message','?')}")
