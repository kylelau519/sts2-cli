import os

from display import (
    c,
    card_pick_quantity_hint,
    n,
    potion_str,
    print_cards_grid,
    print_relics_grid,
    show_player,
    _render_map,
)
from installation import ROOT
from save_load import _list_saves


class _QuitRequested(Exception):
    pass


class _LoadRequested(Exception):
    def __init__(self, path):
        self.path = path
        super().__init__(f"Load requested: {path}")


_PILE_TYPE_ORDER = {"Attack": 0, "Skill": 1, "Power": 2}


def get_input(prompt, valid_options=None, state=None, multi_select=False, multi_min=1, multi_max=1,
              redisplay_fn=None):
    """Get user input with validation. Supports meta-commands: help, map, deck, potions, load.

    If multi_select is True, accept comma-separated tokens; each must be in valid_options,
    and the count must be between multi_min and multi_max (inclusive).

    redisplay_fn, if given, is called with no args to reprint the current prompt's
    screen (event/shop/rest/etc.) in response to the "look" meta-command.
    """
    while True:
        try:
            line = input(f"\n{c('>', 'green')} {prompt}: ").strip()
        except (EOFError, KeyboardInterrupt):
            raise _QuitRequested()

        if not line:
            continue

        raw = line.lower()

        # Meta-commands available at any prompt
        if raw == "help":
            print(f"""
  {c('Commands:', 'bold')}
    {c('help', 'cyan')}         — show this help
    {c('look', 'cyan')}         — redisplay the current screen/options
    {c('map', 'cyan')}          — show map
    {c('deck', 'cyan')}         — show deck
    {c('potions', 'cyan')}      — show potions
    {c('relics', 'cyan')}       — show relics
    {c('draw', 'cyan')}         — show draw pile (in combat)
    {c('discard', 'cyan')}      — show discard pile (in combat)
    {c('exhaust', 'cyan')}      — show exhaust pile (in combat)
    {c('load [path]', 'cyan')}  — load a save file directly without restarting
    {c('branch [choice]', 'cyan')} — save a branch with choice executed on load
    {c('quit', 'cyan')}         — quit
    {c('abandon', 'cyan')}      — abandon run (forfeit)
    {c('save', 'cyan')}         — save game
    {c('saves', 'cyan')}        — list saves


  {c('Actions:', 'bold')}
    Map:     path number (0, 1, 2)
    Combat:  card index / {c('e', 'yellow')} end turn / {c('p0', 'yellow')} use potion
    Reward:  card index / {c('s', 'yellow')} skip
    Multi:   when prompted for N–M cards (or 0–M optional), comma-separate indices, e.g. {c('0,1,2', 'yellow')}
    Rest:    option index
    Event:   option index / {c('leave', 'yellow')} leave
    Shop:    {c('c0', 'yellow')} card / {c('r0', 'yellow')} relic / {c('p0', 'yellow')} potion / {c('rm', 'yellow')} remove / {c('leave', 'yellow')} leave
""")
            continue
        if raw == "look":
            if redisplay_fn:
                redisplay_fn()
            else:
                print("  Nothing to redisplay here.")
            continue
        if raw == "deck" and state:
            p = state.get("player", {})
            show_player(p, show_deck=True)
            continue
        if raw == "potions" and state:
            p = state.get("player", {})
            pots = p.get("potions", [])
            if pots:
                for pot in pots:
                    if pot:
                        print(f"  🧪 {potion_str(pot)}")
            else:
                print("  No potions.")
            continue
        if raw == "relics" and state:
            p = state.get("player", {})
            relics = p.get("relics", [])
            if relics:
                print(f"\n  {c('Relics', 'bold')} ({len(relics)}):")
                print_relics_grid(relics)
            else:
                print("  No relics.")
            continue
        if raw in ("draw", "discard", "exhaust") and state:
            pile = state.get(f"{raw}_pile")
            label = f"{raw.capitalize()} pile"
            if pile is None:
                print(f"  {label} not available (not in combat).")
            elif not pile:
                print(f"  {label} is empty.")
            else:
                shown = pile
                if raw == "draw":
                    # The draw pile's real order tells you exactly what you'll draw next —
                    # group by type then sort alphabetically within each group, so neither the
                    # grouping nor the order within it leaks the real (future) draw sequence.
                    shown = sorted(
                        pile,
                        key=lambda cd: (_PILE_TYPE_ORDER.get(cd.get("type"), 3), n(cd.get("name", "?"))),
                    )
                print(f"\n  {c(label, 'bold')} ({len(pile)} cards):")
                print_cards_grid(shown)
            continue
        if raw == "map":
            # Fetch full map from CLI
            if hasattr(get_input, '_send'):
                map_data = get_input._send({"cmd": "get_map"})
                if map_data and map_data.get("type") == "map":
                    _render_map(map_data)
                else:
                    print("  Map not available.")
            elif state:
                ctx = state.get("context", {})
                print(f"  {c(n(ctx.get('act_name','?')), 'bold')} Floor {ctx.get('floor','?')}")
            continue
        if raw == "save":
            if hasattr(get_input, '_save_fn'):
                get_input._save_fn(state)
            else:
                print("  Save not available.")
            continue
        if raw.startswith("branch "):
            arg = line[7:].strip()
            if not arg:
                print("  Usage: branch <choice> (e.g. branch 1, branch leave, branch e)")
                continue
            if hasattr(get_input, '_branch_fn'):
                get_input._branch_fn(arg, state)
            else:
                print("  Branch not available.")
            continue
        if raw == "saves":
            saves = _list_saves()
            if saves:
                print(f"\n  {c('Saved games:', 'bold')}")
                for s in saves:
                    print(f"    {c(s['file'], 'cyan')}  {s['character']}  Seed:{s['seed']}  Actions:{s['actions']}")
                print(f"\n  Load with: python3 play.py --load saves/{saves[0]['file']}")
            else:
                print("  No saves found.")
            continue
        if raw.startswith("load "):
            p = line[5:].strip()
            if not os.path.isabs(p):
                p = os.path.join(ROOT, p)
            if not os.path.isfile(p):
                print(f"  Save file not found: {line[5:].strip()}")
                continue
            raise _LoadRequested(p)
        if raw == "quit":
            raise _QuitRequested()
        if raw == "abandon":
            confirm = input("  Abandon this run? (y/n): ")
            if confirm.strip().lower() in ("y", "yes"):
                raise KeyboardInterrupt("abandon")
            continue

        if valid_options:
            if multi_select and multi_max > 1:
                if multi_min == 0 and raw == "s" and "s" in valid_options:
                    return raw
                parts = [p.strip() for p in raw.split(",") if p.strip()]
                if not parts:
                    print(f"  Invalid. Options: {', '.join(sorted(valid_options))}")
                    continue
                if len(parts) < multi_min or len(parts) > multi_max:
                    q = card_pick_quantity_hint(multi_min, multi_max)
                    print(f"  {q} — Use {multi_min}-{multi_max} comma-separated indices (e.g. 0,1).")
                    continue
                if len(parts) != len(set(parts)):
                    print("  Duplicate indices.")
                    continue
                bad = [p for p in parts if p not in valid_options]
                if bad:
                    print(f"  Invalid. Options: {', '.join(sorted(valid_options))}")
                    continue
                return ",".join(parts)
            if raw not in valid_options:
                print(f"  Invalid. Options: {', '.join(sorted(valid_options))}")
                continue
        return raw
