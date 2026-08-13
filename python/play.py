#!/usr/bin/env python3
"""
sts2-cli interactive player — play Slay the Spire 2 in your terminal.

Usage:
    python3 play.py                    # Interactive mode (you play)
    python3 play.py --auto             # Auto-play with simple AI
    python3 play.py --seed myseed      # Fixed seed for reproducibility
    python3 play.py --character Silent  # Choose character
"""

import argparse
import json
import os
import random
import subprocess
import sys

from display import (
    CARD_KW_PREFIX_ORDER,
    CARD_KW_SUFFIX_ORDER,
    COLORS,
    SPECIAL_VARS,
    bar,
    c,
    card_desc,
    card_description_display_lines,
    card_pick_quantity_hint,
    combat_hand_inline_stat_str,
    desc,
    format_card_prefix_tag,
    format_card_suffix_keywords,
    loc_resolve,
    n,
    potion_str,
    print_card_detail_extension,
    print_cards_grid,
    relic_str,
    resolve_template,
    short_n,
    show_card_reward,
    show_combat,
    show_event,
    show_map,
    show_player,
    show_rest_site,
    show_shop,
    split_card_keywords,
    _card_kw_label,
    _draw_conn,
    _format_upgrade_preview,
    _load_loc,
    _render_map,
)
from game_log import GameLogger
from installation import (
    DOTNET,
    LIB_DIR,
    PROJECT,
    ROOT,
    ensure_setup,
    _build,
    _copy_dlls,
    _find_dotnet,
    _find_game_dir,
    _is_wsl,
    _patch_dll,
)
from save_load import (
    SAVE_DIR,
    show_native_save,
    _find_native_save_dir,
    _id_to_name,
    _list_saves,
    _load_game,
    _quit_with_save,
    _save_game,
    _show_quit_save_result,
    _writeback_continue_save,
)
from terminal import (
    get_input,
    _QuitRequested,
)


# ─── Main game loop ───

def play(character="Ironclad", seed=None, auto=False, ascension=0, log=True,
         load_path=None, native_save_path=None):
    actual_seed = seed or f"cli_{random.randint(1000,9999)}"
    replay_actions = None
    restart_requested = False
    quit_sent = False

    if load_path:
        character, actual_seed, replay_actions = _load_game(load_path)
        print(f"\n{c('Loading save...', 'yellow')} {os.path.basename(load_path)}")
        print(f"  Character: {character}  Seed: {actual_seed}  Actions: {len(replay_actions)}")

    logger = GameLogger(character, actual_seed, enabled=log)
    action_log = []
    proc = subprocess.Popen(
        [DOTNET, "run", "--no-build", "--project", PROJECT],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, bufsize=1,
    )

    def read():
        while True:
            l = proc.stdout.readline().strip()
            if not l:
                return None
            if l.startswith("{"):
                resp = json.loads(l)
                logger.log_state(resp)
                return resp

    def send(cmd, record=True):
        logger.log_action(cmd)
        if record and cmd.get("cmd") == "action":
            action_log.append(cmd)
        proc.stdin.write(json.dumps(cmd) + "\n")
        proc.stdin.flush()
        return read()

    # Wire send into get_input for map command
    get_input._send = send

    def do_save():
        from datetime import datetime
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        fname = f"{character}_{actual_seed}_{ts}.json"
        save_path = os.path.join(SAVE_DIR, fname)
        _save_game(save_path, character, actual_seed, action_log)
        print(f"  {c('Saved!', 'green')} {fname} ({len(action_log)} actions)")
        print(f"  Load with: python3 play.py --load {os.path.relpath(save_path, ROOT)}")

    get_input._save_fn = do_save
    try:
        ready = read()
        if not ready:
            print("Failed to start simulator")
            return

        if native_save_path:
            print("  Loading game save...")
            state = send(
                {"cmd": "load_save", "path": native_save_path},
                record=False,
            )
            if state and state.get("type") == "error":
                print(f"  {c('Error:', 'red')} {state.get('message', '?')}")
                return
            p = state.get("player", {}) if state else {}
            char_name = p.get("name", {})
            if isinstance(char_name, dict):
                character = char_name.get("en", character)
            print(f"  {c('Save loaded!', 'green')}")
        else:
            state = send({
                "cmd": "start_run",
                "character": character,
                "seed": actual_seed,
                "ascension": ascension,
            }, record=False)
            if state and state.get("type") == "error":
                print(f"  {c('Error:', 'red')} {state.get('message', '?')}")
                return

            # Replay saved actions silently
            if replay_actions:
                total = len(replay_actions)
                for i, cmd in enumerate(replay_actions):
                    state = send(cmd, record=True)
                    pct = (i + 1) * 100 // total
                    print(f"\r  Replaying... {pct}% ({i+1}/{total})", end="", flush=True)
                    if not state:
                        print(f"\n{c('Replay failed at action', 'red')} {i+1}")
                        return
                    print(f"\r  {c('Replay complete!', 'green')}" + " " * 30)
                print()
        print(f"\n{c('Slay the Spire 2 — Headless CLI', 'bold')}")
        if native_save_path:
            p = state.get("player", {}) if state else {}
            ctx = state.get("context", {}) if state else {}
            print(f"Character: {n(p.get('name','?'))}  "
                  f"Act: {ctx.get('act','?')} ({n(ctx.get('act_name','?'))})  "
                  f"HP: {p.get('hp','?')}/{p.get('max_hp','?')}  "
                  f"Gold: {p.get('gold','?')}")
        else:
            asc_str = f"  Ascension: {ascension}" if ascension > 0 else ""
            print(f"Character: {character}  Seed: {actual_seed}{asc_str}")
        print(f"Type {c('help', 'cyan')} for available commands.\n")

        _auto_last_fingerprint = None
        _auto_stuck_count = 0

        while True:
            if not state:
                print("Connection lost.")
                break

            if state.get("type") == "error":
                print(f"  {c('Error:', 'red')} {state.get('message', '?')}")
                state = send({"cmd": "action", "action": "proceed"})
                continue

            dec = state.get("decision", "")

            if dec == "game_over":
                victory = state.get("victory", False)
                p = state.get("player", {})
                ctx = state.get("context", {})

                print(f"\n{'═' * 60}")
                if victory:
                    print(f"  {c('★ ★ ★', 'yellow')}  {c('VICTORY!', 'green')}  {c('★ ★ ★', 'yellow')}")
                else:
                    print(f"  {c('DEFEAT', 'red')}")
                print()

                act_name = n(ctx.get("act_name", "?"))
                floor = state.get("floor", "?")
                print(f"  Act: {state.get('act','?')} ({act_name})  Floor: {floor}")
                print(f"  Character: {n(p.get('name','?'))}")
                print(f"  HP: {p.get('hp','?')}/{p.get('max_hp','?')}  Gold: {p.get('gold','?')}")

                deck = p.get("deck", [])
                if deck:
                    print(f"  Deck: {len(deck)} cards")

                relics = p.get("relics", [])
                if relics:
                    relic_names = [n(r.get("name", "?")) for r in relics]
                    print(f"  Relics ({len(relics)}): {', '.join(relic_names)}")

                print(f"{'═' * 60}")

                if auto:
                    break

                print(f"\n  {c('q', 'cyan')} Quit    {c('n', 'cyan')} New run")
                choice = input(f"  > ").strip().lower()
                if choice == "n":
                    restart_requested = True
                    break
                break

            elif dec == "map_select":
                _writeback_continue_save(send, native_save_path)
                show_map(state, send_fn=send)
                choices = state.get("choices", [])

                if auto:
                    if len(choices) == 1:
                        pick = choices[0]
                    else:
                        p = state.get("player", {})
                        hp_ratio = p.get("hp", 1) / max(p.get("max_hp", 1), 1)
                        if hp_ratio < 0.4:
                            pick = next((ch for ch in choices if ch["type"] == "RestSite"), choices[0])
                        else:
                            pick = choices[0]
                else:
                    valid = {str(i): ch for i, ch in enumerate(choices)}
                    key = get_input("Choose path [number]", set(valid.keys()), state=state)
                    pick = valid[key]

                state = send({"cmd": "action", "action": "select_map_node",
                             "args": {"col": pick["col"], "row": pick["row"]}})

            elif dec == "combat_play":
                show_combat(state)
                hand = state.get("hand", [])
                enemies = state.get("enemies", [])
                energy = state.get("energy", 0)

                valid = {"e": "end_turn"}
                for card in hand:
                    if card.get("can_play") and card.get("cost", 99) <= energy:
                        valid[str(card["index"])] = card
                # Add potion shortcuts
                for pot in state.get("player", {}).get("potions", []):
                    if pot:
                        valid[f"p{pot['index']}"] = f"potion_{pot['index']}"

                if auto:
                    # Auto: play first playable card, or end turn
                    playable = [c_card for c_card in hand if c_card.get("can_play") and c_card.get("cost", 99) <= energy]
                    if playable:
                        card = playable[0]
                        choice = str(card["index"])
                    else:
                        choice = "e"

                    # Stuck detection: if state fingerprint repeats, force end_turn
                    fp = (tuple(c_card.get("index") for c_card in hand), energy)
                    if fp == _auto_last_fingerprint:
                        _auto_stuck_count += 1
                        if _auto_stuck_count >= 5:
                            print(f"  {c('[auto] Stuck state detected, forcing end_turn', 'yellow')}")
                            choice = "e"
                            _auto_stuck_count = 0
                    else:
                        _auto_last_fingerprint = fp
                        _auto_stuck_count = 0
                else:
                    choice = get_input("Play card [index], (e)nd turn, (p0) potion", set(valid.keys()) | {"help"}, state=state)
                    if choice == "help":
                        print("  Enter card index, e=end turn, p0=use potion 0")
                        continue

                if choice == "e":
                    state = send({"cmd": "action", "action": "end_turn"})
                    # Show status cards added (new cards in hand/discard that weren't there)
                    if state and state.get("decision") == "combat_play":
                        new_hand = state.get("hand", [])
                        status_cards = [n(cd.get("name","?")) for cd in new_hand if cd.get("type") in ("Status", "Curse")]
                        if status_cards:
                            sc_str = ", ".join(f"{c(name, 'red')}" for name in status_cards)
                            print(f"  ⚠ Status cards in hand: {sc_str}")
                elif choice.startswith("p") and choice[1:].isdigit():
                    # Use potion
                    pidx = int(choice[1:])
                    args = {"potion_index": pidx}
                    pots = state.get("player", {}).get("potions", [])
                    pot_meta = next((p for p in pots if p and p.get("index") == pidx), None)
                    if pot_meta and pot_meta.get("target_type") == "AnyEnemy" and enemies:
                        tgt = get_input(
                            "Target enemy [index]",
                            {str(e["index"]) for e in enemies},
                            state=state,
                        )
                        args["target_index"] = int(tgt)
                    state = send({"cmd": "action", "action": "use_potion", "args": args})
                else:
                    card = valid[choice]
                    args = {"card_index": card["index"]}
                    if card.get("target_type") == "AnyEnemy":
                        if len(enemies) == 1:
                            args["target_index"] = enemies[0]["index"]
                        elif auto:
                            args["target_index"] = min(enemies, key=lambda e: e.get("hp", 999))["index"]
                        else:
                            tgt = get_input("Target enemy [index]",
                                           {str(e["index"]) for e in enemies})
                            args["target_index"] = int(tgt)
                    state = send({"cmd": "action", "action": "play_card", "args": args})

            elif dec == "card_reward":
                show_card_reward(state)
                cards = state.get("cards", [])
                valid = {str(c_card["index"]): c_card for c_card in cards}
                valid["s"] = None  # skip

                if auto:
                    choice = "0" if cards else "s"
                else:
                    choice = get_input(
                        "Reward: card index 0–n or (s)kip — see list above",
                        set(valid.keys()),
                        state=state,
                    )

                if choice == "s":
                    state = send({"cmd": "action", "action": "skip_card_reward"})
                else:
                    state = send({"cmd": "action", "action": "select_card_reward",
                                 "args": {"card_index": int(choice)}})

            elif dec == "bundle_select":
                print(f"\n{'─' * 60}")
                ctx = state.get("context", {})
                if ctx:
                    print(f"  {c(n(ctx.get('act_name','?')), 'dim')} Floor {ctx.get('floor','?')}")
                print(f"  {c('Choose a card pack', 'bold')}")
                show_player(state.get("player", {}))
                print()
                bundles = state.get("bundles", [])
                for b in bundles:
                    bidx = b["index"]
                    print(f"  {c(f'Pack [{bidx}]:', 'yellow')}")
                    if b.get("cards"):
                        print_cards_grid(b.get("cards"), show_index=False, inline_stats=False, show_playable=False)
                valid = {str(b["index"]): b for b in bundles}
                if auto:
                    choice = "0"
                else:
                    choice = get_input("Choose pack [index]", set(valid.keys()), state=state)
                state = send({"cmd": "action", "action": "select_bundle",
                             "args": {"bundle_index": int(choice)}})

            elif dec == "card_select":
                print(f"\n{'─' * 60}")
                ctx = state.get("context", {})
                if ctx:
                    print(f"  {c(n(ctx.get('act_name','?')), 'dim')} Floor {ctx.get('floor','?')}")
                min_sel = state.get("min_select", 1)
                max_sel = state.get("max_select", 1)
                print(f"  {c('Choose cards', 'bold')} — {card_pick_quantity_hint(min_sel, max_sel)}")
                show_player(state.get("player", {}))
                print()
                cards = state.get("cards", [])
                if cards:
                    print_cards_grid(cards, show_index=True, inline_stats=False, show_playable=False)

                valid = {str(cd["index"]): cd for cd in cards}
                if min_sel == 0:
                    valid["s"] = None

                # Save state before selection to show diff
                old_deck_cards = [n(cd.get("name","?")) for cd in state.get("player",{}).get("deck",[])]

                if auto:
                    if not cards:
                        choice = "s" if min_sel == 0 else "0"
                    else:
                        n_pick = min(max_sel, len(cards))
                        n_pick = max(n_pick, min(min_sel, len(cards)))
                        choice = ",".join(str(cards[i]["index"]) for i in range(n_pick))
                else:
                    multi = max_sel > 1 or min_sel > 1
                    qhint = card_pick_quantity_hint(min_sel, max_sel)
                    choice = get_input(
                        f"Card indices, comma — {qhint} or (s)kip",
                        set(valid.keys()),
                        state=state,
                        multi_select=multi,
                        multi_min=min_sel,
                        multi_max=max_sel,
                    )

                if choice == "s":
                    state = send({"cmd": "action", "action": "skip_select"})
                else:
                    # Support comma-separated indices
                    state = send({"cmd": "action", "action": "select_cards",
                                 "args": {"indices": choice}})

                # Show what changed
                if state and state.get("player"):
                    from collections import Counter
                    new_deck_cards = [n(cd.get("name","?")) for cd in state["player"].get("deck",[])]
                    added = Counter(new_deck_cards) - Counter(old_deck_cards)
                    removed = Counter(old_deck_cards) - Counter(new_deck_cards)
                    if added or removed:
                        parts = []
                        for card_name, cnt in removed.items():
                            parts.append(c(f"-{card_name}" + (f"x{cnt}" if cnt > 1 else ""), "red"))
                        for card_name, cnt in added.items():
                            parts.append(c(f"+{card_name}" + (f"x{cnt}" if cnt > 1 else ""), "green"))
                        print(f"\n  {c('Changes:', 'yellow')} Deck: {' '.join(parts)}")

            elif dec == "shop":
                show_shop(state)

                if auto:
                    choice = "leave"
                else:
                    choice = get_input("Buy [index/r0/p0/rm] or (leave)", state=state)

                if choice == "leave":
                    state = send({"cmd": "action", "action": "leave_room"})
                elif choice == "rm":
                    state = send({"cmd": "action", "action": "remove_card"})
                elif choice.startswith("r"):
                    state = send({"cmd": "action", "action": "buy_relic",
                                 "args": {"relic_index": int(choice[1:])}})
                elif choice.startswith("p"):
                    state = send({"cmd": "action", "action": "buy_potion",
                                 "args": {"potion_index": int(choice[1:])}})
                else:
                    state = send({"cmd": "action", "action": "buy_card",
                                 "args": {"card_index": int(choice)}})

            elif dec == "rest_site":
                show_rest_site(state)
                options = state.get("options", [])
                enabled = [o for o in options if o.get("is_enabled")]
                valid = {str(o["index"]): o for o in enabled}

                if auto:
                    hp = state.get("player", {}).get("hp", 1)
                    mhp = state.get("player", {}).get("max_hp", 1)
                    heal = next((o for o in enabled if o.get("option_id") == "HEAL"), None)
                    smith = next((o for o in enabled if o.get("option_id") == "SMITH"), None)
                    pick = (heal if hp < mhp * 0.7 else smith) or (heal or (enabled[0] if enabled else None))
                    choice = str(pick["index"]) if pick else "0"
                else:
                    choice = get_input("Choose option [index]", set(valid.keys()), state=state)

                state = send({"cmd": "action", "action": "choose_option",
                             "args": {"option_index": int(choice)}})
                if state and state.get("type") == "error":
                    state = send({"cmd": "action", "action": "leave_room"})

            elif dec == "event_choice":
                show_event(state)
                options = state.get("options", [])
                unlocked = [o for o in options if not o.get("is_locked")]
                valid = {str(o["index"]): o for o in unlocked}
                valid["leave"] = None

                # Save state before choice to show diff
                old_relics = set(n(r.get("name","?")) for r in state.get("player",{}).get("relics",[]))
                old_deck_cards = [n(cd.get("name","?")) for cd in state.get("player",{}).get("deck",[])]
                old_deck = state.get("player",{}).get("deck_size", 0)
                old_hp = state.get("player",{}).get("hp", 0)
                old_max_hp = state.get("player",{}).get("max_hp", 0)
                old_gold = state.get("player",{}).get("gold", 0)

                if auto:
                    choice = str(unlocked[0]["index"]) if unlocked else "leave"
                else:
                    choice = get_input("Choose option [index] or (leave)", set(valid.keys()), state=state)

                if choice == "leave":
                    state = send({"cmd": "action", "action": "leave_room"})
                else:
                    state = send({"cmd": "action", "action": "choose_option",
                                 "args": {"option_index": int(choice)}})
                    if state and state.get("type") == "error":
                        state = send({"cmd": "action", "action": "leave_room"})

                # Show what changed
                if state and state.get("player"):
                    new_p = state["player"]
                    new_relics = set(n(r.get("name","?")) for r in new_p.get("relics",[]))
                    gained_relics = new_relics - old_relics
                    new_deck_cards = [n(cd.get("name","?")) for cd in new_p.get("deck",[])]
                    new_deck = new_p.get("deck_size", 0)
                    new_hp = new_p.get("hp", 0)
                    new_max_hp = new_p.get("max_hp", 0)
                    new_gold = new_p.get("gold", 0)
                    changes = []
                    if gained_relics:
                        changes.append(f"Relic: {', '.join(gained_relics)}")
                    # Show specific card changes
                    from collections import Counter
                    old_counts = Counter(old_deck_cards)
                    new_counts = Counter(new_deck_cards)
                    added = new_counts - old_counts
                    removed = old_counts - new_counts
                    if added or removed:
                        parts = []
                        for card_name, cnt in removed.items():
                            parts.append(c(f"-{card_name}" + (f"x{cnt}" if cnt > 1 else ""), "red"))
                        for card_name, cnt in added.items():
                            parts.append(c(f"+{card_name}" + (f"x{cnt}" if cnt > 1 else ""), "green"))
                        changes.append(f"Deck: {' '.join(parts)}")
                    elif new_deck != old_deck:
                        changes.append(f"Deck: {old_deck} → {new_deck}")
                    if new_hp != old_hp or new_max_hp != old_max_hp:
                        changes.append(f"HP: {old_hp}/{old_max_hp} → {new_hp}/{new_max_hp}")
                    if new_gold != old_gold:
                        diff = new_gold - old_gold
                        changes.append(f"Gold: {'+' if diff > 0 else ''}{diff}")
                    if changes:
                        print(f"\n  {c('Changes:', 'yellow')} {'; '.join(changes)}")

            else:
                print(f"  Unknown state: {dec}")
                state = send({"cmd": "action", "action": "proceed"})

    except _QuitRequested:
        quit_sent = False
        quit_save_path = _quit_with_save(native_save_path, character, actual_seed)
        # Retry loop: if save fails the process stays alive so we can try a different path.
        quit_sent = True
        while True:
            quit_cmd = {"cmd": "quit"}
            if quit_save_path:
                quit_cmd["path"] = quit_save_path
            result = send(quit_cmd)
            if result and result.get("type") == "save_error":
                save_detail = result.get("save") or {}
                msg = save_detail.get("message", "?")
                print(f"  {c('Save failed:', 'red')} {msg}")
                try:
                    ans = input("  New path (Enter = quit without saving): ").strip()
                except (EOFError, KeyboardInterrupt):
                    ans = ""
                quit_save_path = ans if ans else None
            else:
                _show_quit_save_result(result)
                break
    except KeyboardInterrupt:
        print(f"\n  {c('Run abandoned.', 'yellow')}")
    finally:
        if not quit_sent:
            try:
                result = send({"cmd": "quit"})
                _show_quit_save_result(result)
            except Exception:
                pass
        logger.close()
        if logger.path:
            print(f"\n  [log] Game log saved to {logger.path}")
        try:
            proc.terminate()
            proc.wait(timeout=5)
        except Exception:
            proc.kill()

    return restart_requested


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Play Slay the Spire 2 in your terminal")
    parser.add_argument("--auto", action="store_true", help="Auto-play with simple AI")
    parser.add_argument("--seed", type=str, default=None, help="Random seed")
    parser.add_argument("--character", type=str, default="Ironclad",
                       choices=["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"],
                       help="Character to play")
    parser.add_argument("--ascension", type=int, default=0,
                       choices=range(0, 11), metavar="0-10",
                       help="Ascension level (0-10)")
    parser.add_argument("--no-log", action="store_true",
                       help="Disable game logging")
    parser.add_argument("--load", type=str, default=None,
                       help="Load a save file (action replay)")
    parser.add_argument("--saves", action="store_true",
                       help="List available saves and exit")
    parser.add_argument("--save-info", type=str, default=None,
                       help="Show info from a save file (provide path)")
    parser.add_argument("--continue", dest="continue_save", type=str, default=None,
                       help="Continue playing from a save file (provide path)")
    args = parser.parse_args()

    # Mutual exclusion: conflicting flags
    if args.load is not None:
        if args.saves:
            parser.error("Cannot combine --load with --saves")
        if args.save_info is not None:
            parser.error("Cannot combine --load with --save-info")
        if args.continue_save is not None:
            parser.error("Cannot combine --load with --continue")
    if args.saves:
        if args.save_info is not None:
            parser.error("Cannot combine --saves with --save-info")
        if args.continue_save is not None:
            parser.error("Cannot combine --saves with --continue")
    if args.save_info is not None:
        if args.continue_save is not None:
            parser.error("Cannot combine --save-info with --continue")

    if args.save_info is not None:
        p = args.save_info
        if not os.path.isabs(p):
            p = os.path.join(ROOT, p)
        if not os.path.isfile(p):
            print(f"Save file not found: {p}")
            sys.exit(1)
        show_native_save(p)
        sys.exit(0)

    if args.saves:
        saves = _list_saves()
        if saves:
            print(f"\n{'─' * 50}")
            for s in saves:
                stype = s.get("type", "replay")
                if stype == "native":
                    print(f"  {s['file']}  [native save]")
                else:
                    print(f"  {s['file']}  {s['character']}  seed:{s['seed']}  actions:{s['actions']}")
            print(f"{'─' * 50}")
            print("  Replay saves: python3 play.py --load saves/<file>")
            print("  Native saves: python3 play.py --continue saves/<file>")
        else:
            print("No saves found.")
        sys.exit(0)

    load_path = None
    if args.load:
        p = args.load
        if not os.path.isabs(p):
            p = os.path.join(ROOT, p)
        if not os.path.isfile(p):
            print(f"Save file not found: {args.load}")
            sys.exit(1)
        load_path = p

    native_save_path = None
    if args.continue_save is not None:
        native_save_path = args.continue_save
        if not os.path.isabs(native_save_path):
            native_save_path = os.path.join(ROOT, native_save_path)
        if not os.path.isfile(native_save_path):
            print(f"Save file not found: {native_save_path}")
            sys.exit(1)
        show_native_save(native_save_path)

    ensure_setup()
    next_seed = args.seed
    next_auto = args.auto
    next_load_path = load_path
    next_native_save_path = native_save_path
    while True:
        restart = play(character=args.character, seed=next_seed, auto=next_auto,
                       ascension=args.ascension, log=not args.no_log,
                       load_path=next_load_path,
                       native_save_path=next_native_save_path)
        if not restart:
            break
        next_seed = None
        next_auto = False
        next_load_path = None
        next_native_save_path = None
