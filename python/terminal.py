"""Terminal interaction and input handling for sts2-cli."""

from display import (
    c,
    card_pick_quantity_hint,
    n,
    potion_str,
    relic_str,
    show_player,
    _render_map,
)
from save_load import _list_saves


class _QuitRequested(Exception):
    pass


def get_input(prompt, valid_options=None, state=None, multi_select=False, multi_min=1, multi_max=1):
    """Get user input with validation. Supports meta-commands: help, map, deck, potions.

    If multi_select is True, accept comma-separated tokens; each must be in valid_options,
    and the count must be between multi_min and multi_max (inclusive).
    """
    while True:
        try:
            raw = input(f"\n{c('>', 'green')} {prompt}: ").strip().lower()
        except (EOFError, KeyboardInterrupt):
            raise _QuitRequested()

        if not raw:
            continue

        # Meta-commands available at any prompt
        if raw == "help":
            print(f"""
  {c('Commands:', 'bold')}
    {c('help', 'cyan')}     — show this help
    {c('map', 'cyan')}      — show map
    {c('deck', 'cyan')}     — show deck
    {c('potions', 'cyan')}  — show potions
    {c('relics', 'cyan')}   — show relics
    {c('quit', 'cyan')}     — quit
    {c('abandon', 'cyan')}  — abandon run (forfeit)
    {c('save', 'cyan')}     — save game
    {c('saves', 'cyan')}    — list saves

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
            for r in p.get("relics", []):
                print(f"  🔶 {relic_str(r)}")
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
                get_input._save_fn()
            else:
                print("  Save not available.")
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
