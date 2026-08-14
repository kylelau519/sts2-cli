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
from collections import Counter
from datetime import datetime
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


class MainLoop:
    """Main controller for headless Slay the Spire 2 gameplay and terminal UI."""

    def __init__(self, character="Ironclad", seed=None, auto=False, ascension=0, log=True,
                 load_path=None, native_save_path=None):
        self.character = character
        self.actual_seed = seed or f"cli_{random.randint(1000, 9999)}"
        self.auto = auto
        self.ascension = ascension
        self.log_enabled = log
        self.load_path = load_path
        self.native_save_path = native_save_path

        self.replay_actions = None
        self.restart_requested = False
        self.quit_sent = False
        self.action_log = []
        self.logger = None
        self.proc = None

        self._auto_last_fingerprint = None
        self._auto_stuck_count = 0

        self.handlers = {
            "map_select": self.on_map_select,
            "combat_play": self.on_combat_play,
            "card_reward": self.on_card_reward,
            "bundle_select": self.on_bundle_select,
            "card_select": self.on_card_select,
            "shop": self.on_shop,
            "rest_site": self.on_rest_site,
            "event_choice": self.on_event_choice,
        }

    # ─── Process & Communication ───

    def read(self):
        """Read next JSON line from the subprocess stdout."""
        while True:
            line = self.proc.stdout.readline().strip()
            if not line:
                return None
            if line.startswith("{"):
                resp = json.loads(line)
                if self.logger:
                    self.logger.log_state(resp)
                return resp

    def send(self, cmd, record=True):
        """Send JSON command to subprocess stdin and read the response."""
        if self.logger:
            self.logger.log_action(cmd)
        if record and cmd.get("cmd") == "action":
            self.action_log.append(cmd)
        self.proc.stdin.write(json.dumps(cmd) + "\n")
        self.proc.stdin.flush()
        return self.read()

    def do_save(self):
        """Save action replay JSON file."""
        ts = datetime.now().strftime("%Y%m%d_%H%M%S")
        fname = f"{self.character}_{self.actual_seed}_{ts}.json"
        save_path = os.path.join(SAVE_DIR, fname)
        _save_game(save_path, self.character, self.actual_seed, self.action_log)
        print(f"  {c('Saved!', 'green')} {fname} ({len(self.action_log)} actions)")
        print(f"  Load with: python3 play.py --load {os.path.relpath(save_path, ROOT)}")

    # ─── Game Setup & Lifecycle ───

    def setup(self):
        """Initialize process, load save or start new run, replay actions if needed."""
        if self.load_path:
            self.character, self.actual_seed, self.replay_actions = _load_game(self.load_path)
            print(f"\n{c('Loading save...', 'yellow')} {os.path.basename(self.load_path)}")
            print(f"  Character: {self.character}  Seed: {self.actual_seed}  Actions: {len(self.replay_actions)}")

        self.logger = GameLogger(self.character, self.actual_seed, enabled=self.log_enabled)

        dotnet = str(DOTNET or "dotnet")
        self.proc = subprocess.Popen(
            [dotnet, "run", "--no-build", "--project", str(PROJECT)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, text=True, bufsize=1,
        )

        get_input._send = self.send
        get_input._save_fn = self.do_save

        ready = self.read()
        if not ready:
            print("Failed to start simulator")
            return None

        if self.native_save_path:
            print("  Loading game save...")
            state = self.send({"cmd": "load_save", "path": self.native_save_path}, record=False)
            if state and state.get("type") == "error":
                print(f"  {c('Error:', 'red')} {state.get('message', '?')}")
                return None
            p = state.get("player", {}) if state else {}
            char_name = p.get("name", {})
            if isinstance(char_name, dict):
                self.character = char_name.get("en", self.character)
            print(f"  {c('Save loaded!', 'green')}")
        else:
            state = self.send({
                "cmd": "start_run",
                "character": self.character,
                "seed": self.actual_seed,
                "ascension": self.ascension,
            }, record=False)
            if state and state.get("type") == "error":
                print(f"  {c('Error:', 'red')} {state.get('message', '?')}")
                return None

            if self.replay_actions:
                total = len(self.replay_actions)
                for i, cmd in enumerate(self.replay_actions):
                    state = self.send(cmd, record=True)
                    pct = (i + 1) * 100 // total
                    print(f"\r  Replaying... {pct}% ({i+1}/{total})", end="", flush=True)
                    if not state:
                        print(f"\n{c('Replay failed at action', 'red')} {i+1}")
                        return None
                    print(f"\r  {c('Replay complete!', 'green')}" + " " * 30)
                print()

        print(f"\n{c('Slay the Spire 2 — Headless CLI', 'bold')}")
        if self.native_save_path:
            p = state.get("player", {}) if state else {}
            ctx = state.get("context", {}) if state else {}
            print(f"Character: {n(p.get('name','?'))}  "
                  f"Act: {ctx.get('act','?')} ({n(ctx.get('act_name','?'))})  "
                  f"HP: {p.get('hp','?')}/{p.get('max_hp','?')}  "
                  f"Gold: {p.get('gold','?')}")
        else:
            asc_str = f"  Ascension: {self.ascension}" if self.ascension > 0 else ""
            print(f"Character: {self.character}  Seed: {self.actual_seed}{asc_str}")
        print(f"Type {c('help', 'cyan')} for available commands.\n")

        return state

    # ─── Decision Handlers ───

    def on_game_over(self, state):
        """Display run conclusion and prompt for restart."""
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

        if not self.auto:
            print(f"\n  {c('q', 'cyan')} Quit    {c('n', 'cyan')} New run")
            choice = input(f"  > ").strip().lower()
            if choice == "n":
                self.restart_requested = True

    def on_map_select(self, state):
        _writeback_continue_save(self.send, self.native_save_path)
        show_map(state, send_fn=self.send)
        choices = state.get("choices", [])

        if self.auto:
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

        return self.send({
            "cmd": "action",
            "action": "select_map_node",
            "args": {"col": pick["col"], "row": pick["row"]},
        })

    def on_combat_play(self, state):
        show_combat(state)
        hand = state.get("hand", [])
        enemies = state.get("enemies", [])
        energy = state.get("energy", 0)

        valid = {"e": "end_turn"}
        for card in hand:
            if card.get("can_play") and card.get("cost", 99) <= energy:
                valid[str(card["index"])] = card
        for pot in state.get("player", {}).get("potions", []):
            if pot:
                valid[f"p{pot['index']}"] = f"potion_{pot['index']}"

        if self.auto:
            playable = [c_card for c_card in hand if c_card.get("can_play") and c_card.get("cost", 99) <= energy]
            if playable:
                card = playable[0]
                choice = str(card["index"])
            else:
                choice = "e"

            fp = (tuple(c_card.get("index") for c_card in hand), energy)
            if fp == self._auto_last_fingerprint:
                self._auto_stuck_count += 1
                if self._auto_stuck_count >= 5:
                    print(f"  {c('[auto] Stuck state detected, forcing end_turn', 'yellow')}")
                    choice = "e"
                    self._auto_stuck_count = 0
            else:
                self._auto_last_fingerprint = fp
                self._auto_stuck_count = 0
        else:
            choice = get_input("Play card [index], (e)nd turn, (p0) potion", set(valid.keys()) | {"help"}, state=state)
            if choice == "help":
                print("  Enter card index, e=end turn, p0=use potion 0")
                return state

        if choice == "e":
            state = self.send({"cmd": "action", "action": "end_turn"})
            if state and state.get("decision") == "combat_play":
                new_hand = state.get("hand", [])
                status_cards = [n(cd.get("name", "?")) for cd in new_hand if cd.get("type") in ("Status", "Curse")]
                if status_cards:
                    sc_str = ", ".join(f"{c(name, 'red')}" for name in status_cards)
                    print(f"  ⚠ Status cards in hand: {sc_str}")
            return state
        elif choice.startswith("p") and choice[1:].isdigit():
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
            return self.send({"cmd": "action", "action": "use_potion", "args": args})
        else:
            card = valid[choice]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy":
                if len(enemies) == 1:
                    args["target_index"] = enemies[0]["index"]
                elif self.auto:
                    args["target_index"] = min(enemies, key=lambda e: e.get("hp", 999))["index"]
                else:
                    tgt = get_input("Target enemy [index]", {str(e["index"]) for e in enemies})
                    args["target_index"] = int(tgt)
            return self.send({"cmd": "action", "action": "play_card", "args": args})

    def on_card_reward(self, state):
        show_card_reward(state)
        cards = state.get("cards", [])
        valid = {str(c_card["index"]): c_card for c_card in cards}
        valid["s"] = None

        if self.auto:
            choice = "0" if cards else "s"
        else:
            choice = get_input(
                "Reward: card index 0–n or (s)kip — see list above",
                set(valid.keys()),
                state=state,
            )

        if choice == "s":
            return self.send({"cmd": "action", "action": "skip_card_reward"})
        else:
            return self.send({"cmd": "action", "action": "select_card_reward",
                             "args": {"card_index": int(choice)}})

    def on_bundle_select(self, state):
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
        if self.auto:
            choice = "0"
        else:
            choice = get_input("Choose pack [index]", set(valid.keys()), state=state)
        return self.send({"cmd": "action", "action": "select_bundle",
                         "args": {"bundle_index": int(choice)}})

    def on_card_select(self, state):
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

        old_deck_cards = [n(cd.get("name", "?")) for cd in state.get("player", {}).get("deck", [])]

        if self.auto:
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
            next_state = self.send({"cmd": "action", "action": "skip_select"})
        else:
            next_state = self.send({"cmd": "action", "action": "select_cards",
                                    "args": {"indices": choice}})

        if next_state and next_state.get("player"):
            new_deck_cards = [n(cd.get("name", "?")) for cd in next_state["player"].get("deck", [])]
            added = Counter(new_deck_cards) - Counter(old_deck_cards)
            removed = Counter(old_deck_cards) - Counter(new_deck_cards)
            if added or removed:
                parts = []
                for card_name, cnt in removed.items():
                    parts.append(c(f"-{card_name}" + (f"x{cnt}" if cnt > 1 else ""), "red"))
                for card_name, cnt in added.items():
                    parts.append(c(f"+{card_name}" + (f"x{cnt}" if cnt > 1 else ""), "green"))
                print(f"\n  {c('Changes:', 'yellow')} Deck: {' '.join(parts)}")

        return next_state

    def on_shop(self, state):
        show_shop(state)

        if self.auto:
            choice = "leave"
        else:
            choice = get_input("Buy [index/r0/p0/rm] or (leave)", state=state)

        if choice == "leave":
            return self.send({"cmd": "action", "action": "leave_room"})
        elif choice == "rm":
            return self.send({"cmd": "action", "action": "remove_card"})
        elif choice.startswith("r"):
            return self.send({"cmd": "action", "action": "buy_relic",
                             "args": {"relic_index": int(choice[1:])}})
        elif choice.startswith("p"):
            return self.send({"cmd": "action", "action": "buy_potion",
                             "args": {"potion_index": int(choice[1:])}})
        else:
            return self.send({"cmd": "action", "action": "buy_card",
                             "args": {"card_index": int(choice)}})

    def on_rest_site(self, state):
        show_rest_site(state)
        options = state.get("options", [])
        enabled = [o for o in options if o.get("is_enabled")]
        valid = {str(o["index"]): o for o in enabled}

        if self.auto:
            hp = state.get("player", {}).get("hp", 1)
            mhp = state.get("player", {}).get("max_hp", 1)
            heal = next((o for o in enabled if o.get("option_id") == "HEAL"), None)
            smith = next((o for o in enabled if o.get("option_id") == "SMITH"), None)
            pick = (heal if hp < mhp * 0.7 else smith) or (heal or (enabled[0] if enabled else None))
            choice = str(pick["index"]) if pick else "0"
        else:
            choice = get_input("Choose option [index]", set(valid.keys()), state=state)

        next_state = self.send({"cmd": "action", "action": "choose_option",
                                "args": {"option_index": int(choice)}})
        if next_state and next_state.get("type") == "error":
            next_state = self.send({"cmd": "action", "action": "leave_room"})
        return next_state

    def on_event_choice(self, state):
        show_event(state)
        options = state.get("options", [])
        unlocked = [o for o in options if not o.get("is_locked")]
        valid = {str(o["index"]): o for o in unlocked}
        valid["leave"] = None

        old_relics = set(n(r.get("name", "?")) for r in state.get("player", {}).get("relics", []))
        old_deck_cards = [n(cd.get("name", "?")) for cd in state.get("player", {}).get("deck", [])]
        old_deck = state.get("player", {}).get("deck_size", 0)
        old_hp = state.get("player", {}).get("hp", 0)
        old_max_hp = state.get("player", {}).get("max_hp", 0)
        old_gold = state.get("player", {}).get("gold", 0)

        if self.auto:
            choice = str(unlocked[0]["index"]) if unlocked else "leave"
        else:
            choice = get_input("Choose option [index] or (leave)", set(valid.keys()), state=state)

        if choice == "leave":
            next_state = self.send({"cmd": "action", "action": "leave_room"})
        else:
            next_state = self.send({"cmd": "action", "action": "choose_option",
                                    "args": {"option_index": int(choice)}})
            if next_state and next_state.get("type") == "error":
                next_state = self.send({"cmd": "action", "action": "leave_room"})

        if next_state and next_state.get("player"):
            new_p = next_state["player"]
            new_relics = set(n(r.get("name", "?")) for r in new_p.get("relics", []))
            gained_relics = new_relics - old_relics
            new_deck_cards = [n(cd.get("name", "?")) for cd in new_p.get("deck", [])]
            new_deck = new_p.get("deck_size", 0)
            new_hp = new_p.get("hp", 0)
            new_max_hp = new_p.get("max_hp", 0)
            new_gold = new_p.get("gold", 0)
            changes = []
            if gained_relics:
                changes.append(f"Relic: {', '.join(gained_relics)}")

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

        return next_state

    # ─── Quit & Cleanup ───

    def handle_quit(self):
        """Handle interactive quit sequence with optional save."""
        self.quit_sent = False
        quit_save_path = _quit_with_save(self.native_save_path, self.character, self.actual_seed)
        self.quit_sent = True
        while True:
            quit_cmd = {"cmd": "quit"}
            if quit_save_path:
                quit_cmd["path"] = quit_save_path
            result = self.send(quit_cmd)
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

    def cleanup(self):
        """Clean up process and logger resources."""
        if not self.quit_sent and self.proc:
            try:
                result = self.send({"cmd": "quit"})
                _show_quit_save_result(result)
            except Exception:
                pass
        if self.logger:
            self.logger.close()
            if self.logger.path:
                print(f"\n  [log] Game log saved to {self.logger.path}")
        if self.proc:
            try:
                self.proc.terminate()
                self.proc.wait(timeout=5)
            except Exception:
                self.proc.kill()

    # ─── Main Entry ───

    def run(self):
        """Execute the full interactive game loop."""
        try:
            state = self.setup()
            if not state:
                return self.restart_requested

            while state:
                if state.get("type") == "error":
                    print(f"  {c('Error:', 'red')} {state.get('message', '?')}")
                    state = self.send({"cmd": "action", "action": "proceed"})
                    continue

                dec = state.get("decision", "")

                if dec == "game_over":
                    self.on_game_over(state)
                    break

                handler = self.handlers.get(dec)
                if handler:
                    state = handler(state)
                else:
                    print(f"  Unknown state: {dec}")
                    state = self.send({"cmd": "action", "action": "proceed"})

        except _QuitRequested:
            self.handle_quit()
        except KeyboardInterrupt:
            print(f"\n  {c('Run abandoned.', 'yellow')}")
        finally:
            self.cleanup()

        return self.restart_requested


# Aliases for backward compatibility
MainGameLoop = MainLoop


def play(character="Ironclad", seed=None, auto=False, ascension=0, log=True,
         load_path=None, native_save_path=None):
    """Play a game of Slay the Spire 2."""
    loop = MainLoop(
        character=character,
        seed=seed,
        auto=auto,
        ascension=ascension,
        log=log,
        load_path=load_path,
        native_save_path=native_save_path,
    )
    return loop.run()


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
    save_group = parser.add_mutually_exclusive_group()
    save_group.add_argument("--load", type=str, help="Load a specific save file")
    save_group.add_argument("--saves", action="store_true",
                            help="List all saved games")
    save_group.add_argument("--save-info", type=str,
                            help="Show details for a save file")
    save_group.add_argument("--continue", dest="continue_save",
                        action="store_true", help="Continue the latest save")
    args = parser.parse_args()

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
        loop = MainLoop(
            character=args.character,
            seed=next_seed,
            auto=next_auto,
            ascension=args.ascension,
            log=not args.no_log,
            load_path=next_load_path,
            native_save_path=next_native_save_path,
        )
        restart = loop.run()
        if not restart:
            break
        next_seed = None
        next_auto = False
        next_load_path = None
        next_native_save_path = None
