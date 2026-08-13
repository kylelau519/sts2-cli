#!/usr/bin/env python3
"""
sts2-cli launcher: pick new game (character, ascension) or load save, then run python/play.py.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime

ROOT = os.path.dirname(os.path.abspath(__file__))
PLAY_PY = os.path.join(ROOT, "python", "play.py")
SAVE_DIR = os.path.join(ROOT, "saves")

# play.py --character 使用的英文名，顺序与官方角色选择一致
CLI_CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]


def _prompt_line(prompt: str) -> str:
    try:
        return input(prompt).strip()
    except (EOFError, KeyboardInterrupt):
        print()
        raise SystemExit(0) from None


def _pick_int(prompt: str, lo: int, hi: int, default: int | None = None) -> int:
    while True:
        raw = _prompt_line(prompt)
        if not raw and default is not None:
            return default
        try:
            v = int(raw)
        except ValueError:
            print(f"  Please enter an integer between {lo} and {hi}.")
            continue
        if lo <= v <= hi:
            return v
        print(f"  Please enter an integer between {lo} and {hi}.")


def _collect_save_entries() -> list[dict]:
    if not os.path.isdir(SAVE_DIR):
        return []
    out: list[dict] = []
    for name in os.listdir(SAVE_DIR):
        path = os.path.join(SAVE_DIR, name)
        if not os.path.isfile(path):
            continue
        st = os.stat(path)
        if name.endswith(".json"):
            try:
                with open(path, encoding="utf-8") as f:
                    d = json.load(f)
                out.append({
                    "kind": "replay",
                    "path": path,
                    "name": name,
                    "mtime": st.st_mtime,
                    "character": d.get("character", "?"),
                    "seed": d.get("seed", "?"),
                    "actions": len(d.get("actions", [])),
                })
            except (json.JSONDecodeError, OSError):
                pass
        elif name.endswith(".save"):
            try:
                with open(path, encoding="utf-8") as f:
                    data = json.load(f)
                seed = data.get("rng", {}).get("seed", "?")
                asc = data.get("ascension", 0)
                char_id = "?"
                pl = data.get("players", [])
                if pl:
                    char_id = pl[0].get("character_id", "?")
                out.append({
                    "kind": "native",
                    "path": path,
                    "name": name,
                    "mtime": st.st_mtime,
                    "seed": seed,
                    "ascension": asc,
                    "character_id": char_id,
                })
            except (json.JSONDecodeError, OSError):
                out.append({
                    "kind": "native",
                    "path": path,
                    "name": name,
                    "mtime": st.st_mtime,
                    "broken": True,
                })
    out.sort(key=lambda x: -x["mtime"])
    return out


def _format_entry(e: dict) -> str:
    ts = datetime.fromtimestamp(e["mtime"]).strftime("%Y-%m-%d %H:%M")
    if e["kind"] == "replay":
        ch = str(e.get("character", "?"))
        return f"{e['name']}  |  {ch}  |  Seed {e['seed']}  |  {e['actions']} steps  |  {ts}"
    if e.get("broken"):
        return f"{e['name']}  |  (File corrupted or unparseable)  |  {ts}"
    cid = str(e.get("character_id", "?"))
    ch = cid if cid != "?" else "?"
    return (
        f"{e['name']}  |  {ch}  |  Ascension {e['ascension']}  |  Seed {e['seed']}  |  {ts}"
    )


def _run_play(args: list[str]) -> int:
    cmd = [sys.executable, PLAY_PY, *args]
    # play.py 内 ROOT 由文件路径解析，不依赖 cwd；统一设为仓库根目录便于相对路径。
    r = subprocess.run(cmd, cwd=ROOT)
    return r.returncode


def _menu_new_game() -> None:
    print("\n── Select Character ──")
    for i, cli in enumerate(CLI_CHARACTERS, 0):
        print(f"  {i}  {cli}")
    idx = _pick_int("\nEnter number (0–4): ", 0, 4)
    character = CLI_CHARACTERS[idx]
    asc = _pick_int(
        "\nAscension level 0–10 (0 for standard mode, press Enter for 0): ",
        0,
        10,
        default=0,
    )
    print(f"\nStarting: {character}  |  Ascension {asc}\n")
    _run_play(["--character", character, "--ascension", str(asc)])


def _menu_load_save() -> None:
    entries = _collect_save_entries()
    if not entries:
        print("\n  No .save or .json files found in saves/. Please save during a run or save & quit first.\n")
        return

    print("\n── Load Save (Newest to Oldest) ──")
    print("  [Continue Game] = Native .save")
    print("  [Action Replay] = .json created via in-game save command\n")
    for i, e in enumerate(entries, 1):
        tag = "Continue Game" if e["kind"] == "native" else "Action Replay"
        print(f"  {i:2}  [{tag}]  {_format_entry(e)}")
    print(f"\n  0  Go Back")
    choice = _pick_int("\nEnter number: ", 0, len(entries))
    if choice == 0:
        return
    sel = entries[choice - 1]
    rel = os.path.relpath(sel["path"], ROOT)
    if sel["kind"] == "native":
        print(f"\nLoading as continue: {rel}\n")
        _run_play(["--continue", rel])
    else:
        print(f"\nLoading as replay: {rel}\n")
        _run_play(["--load", rel])


def _main_interactive() -> None:
    sys.path.insert(0, os.path.join(ROOT, "python"))
    import play as play_mod  # noqa: PLC0415

    play_mod.ensure_setup()

    while True:
        print(
            f"""
╔══════════════════════════════════════╗
║       Slay the Spire 2  CLI          ║
╚══════════════════════════════════════╝

  1  New Game
  2  Load Save
  0  Quit
"""
        )
        c = _prompt_line("Please select (0–2): ").lower()
        if c in ("0", "q", "quit", "exit", ""):
            print("Goodbye.")
            break
        if c == "1":
            _menu_new_game()
        elif c == "2":
            _menu_load_save()
        else:
            print("  Invalid input, please enter 0, 1 or 2.")


def main() -> None:
    parser = argparse.ArgumentParser(description="sts2-cli interactive launcher")
    parser.parse_args()
    _main_interactive()


if __name__ == "__main__":
    main()
