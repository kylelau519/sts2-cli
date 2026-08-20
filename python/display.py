"""Formatting and display helpers for sts2-cli."""

import json
import os
import re
from rich.console import Console
from rich.panel import Panel
from rich.columns import Columns
from rich.text import Text

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# ─── Colors & ANSI helpers ───

COLORS = {
    "red": "\033[91m", "green": "\033[92m", "yellow": "\033[93m",
    "blue": "\033[94m", "magenta": "\033[95m", "cyan": "\033[96m",
    "bold": "\033[1m", "dim": "\033[2m", "reset": "\033[0m",
}


def c(text, color):
    return f"{COLORS.get(color, '')}{text}{COLORS['reset']}"


def bar(current, maximum, width=20):
    filled = int(current / max(maximum, 1) * width)
    return c("█" * filled, "red") + c("░" * (width - filled), "dim")


# ─── Text & formatting helpers ───

def n(obj):
    """Extract display name."""
    return str(obj) if obj is not None else "?"

def desc(obj):
    """Extract description, strip BBCode tags, clean SmartFormat vars."""
    if obj and isinstance(obj, str):
        text = obj
        text = re.sub(r'\[/?[^\]]+\]', '', text)  # strip BBCode [tags]

        def smart_replace(m):
            full = m.group(1)
            # Handle conditional: {IfUpgraded:show:textA|textB}
            if full.startswith("IfUpgraded:show:"):
                parts = full[len("IfUpgraded:show:"):].split("|")
                return parts[1] if len(parts) > 1 else parts[0]  # show non-upgraded
            if full.startswith("IfUpgraded:"):
                parts = full[len("IfUpgraded:"):].split("|")
                return parts[1] if len(parts) > 1 else parts[0]
            # {InCombat:text|alt} → show combat text
            if full.startswith("InCombat:"):
                parts = full[len("InCombat:"):].split("|")
                return parts[0].lstrip("\n")  # show combat version
            # Energy icons: {Energy:energyIcons()} → [Energy]E
            if "energyIcons" in full:
                var = full.split(":")[0]
                return f"[{var}]E"
            # Star icons: {Stars:starIcons()} → [Stars]⭐
            if "starIcons" in full:
                var = full.split(":")[0]
                return f"[{var}]⭐"
            # Plural: {Cards:plural:card|cards} → card/cards based on value
            if ":plural:" in full:
                parts = full.split(":")
                var = parts[0]
                plural_parts = ":".join(parts[2:]).split("|")
                return f"[{var}:{plural_parts[0]}|{plural_parts[1] if len(plural_parts) > 1 else plural_parts[0]}]"
            # Conditional: {IsMultiplayer:textA|textB} → textB (single player)
            if ":" in full and "|" in full:
                parts_after = ":".join(full.split(":")[1:]).split("|")
                return parts_after[-1]  # take the false/last branch
            # Simple var with format: {Damage:diff()} → [Damage]
            var = full.split(":")[0]
            return f"[{var}]"

        # Process from innermost braces outward (handle nesting)
        for _ in range(3):  # max 3 nesting levels
            text = re.sub(r'\{([^{}]+)\}', smart_replace, text)
        return text.strip()
    return ""


# End of title line (restrictive / rules)
CARD_KW_SUFFIX_ORDER = ("Exhaust", "Unplayable", "Eternal")
# Before description as [A/B/C]
CARD_KW_PREFIX_ORDER = ("Innate", "Ethereal", "Retain", "Sly")


def resolve_template(text, vars_dict):
    """Replace [VarName] in text with actual values from vars dict.
    Matches case-insensitively against the vars dict keys.
    Also handles special vars like energyPrefix."""
    if not text:
        return text
    # Build case-insensitive lookup from stats + special vars
    lower_vars = {}
    if vars_dict:
        lower_vars = {k.lower(): v for k, v in vars_dict.items()}

    def replacer(m):
        key = m.group(1)
        # Handle plural: [Cards:card|cards]
        if ':' in key and '|' in key:
            var_name, plural_spec = key.split(':', 1)
            val = lower_vars.get(var_name.lower())
            if val is not None:
                forms = plural_spec.split('|')
                return forms[0] if int(val) == 1 else (forms[1] if len(forms) > 1 else forms[0])
            return f"[{key}]"
        kl = key.lower()
        val = lower_vars.get(kl)
        if val is not None:
            return str(val)
        # Special vars
        if kl == "energyprefix":
            return ""  # prefix only, unit already added by energyIcons handler in desc()
        return f"[{key}]"
    return re.sub(r'\[([^\]]+)\]', replacer, text)


def card_desc(card):
    """Get resolved card description using stats as template vars."""
    d = desc(card.get("description", {}))
    stats = card.get("stats") or {}
    return resolve_template(d, stats)  # always resolve (handles energyPrefix etc.)


def _card_kw_label(kw):
    return kw


def split_card_keywords(keywords):
    """Split into (prefix, suffix) for layout; uses live ``keywords`` from state each call."""
    raw = [k for k in (keywords or []) if k]
    if not raw:
        return [], []
    suffix_set = set(CARD_KW_SUFFIX_ORDER)
    suffix = [k for k in raw if k in suffix_set]
    prefix_rest = [k for k in raw if k not in suffix_set]

    prefix_ordered = []
    used = set()
    for k in CARD_KW_PREFIX_ORDER:
        if k in prefix_rest:
            prefix_ordered.append(k)
            used.add(k)
    for k in prefix_rest:
        if k not in used:
            prefix_ordered.append(k)
            used.add(k)

    suffix_ordered = sorted(suffix, key=lambda k: CARD_KW_SUFFIX_ORDER.index(k))
    return prefix_ordered, suffix_ordered


def format_card_suffix_keywords(suffix_list):
    if not suffix_list:
        return ""
    inner = " ".join(c(_card_kw_label(k), "dim") for k in suffix_list)
    return f" [{inner}]"


def format_card_prefix_tag(prefix_list):
    if not prefix_list:
        return ""
    return "[" + "/".join(_card_kw_label(k) for k in prefix_list) + "]"


def card_description_display_lines(card):
    """Lines under the title row; [prefix tag] merges into first line, then remaining loc lines."""
    cd_d = card_desc(card)
    prefix, _suf = split_card_keywords(card.get("keywords"))
    tag = format_card_prefix_tag(prefix)
    if not cd_d:
        return [tag] if tag else []

    lines = [ln.strip() for ln in cd_d.split("\n") if ln.strip()]
    if not lines:
        return [tag] if tag else []

    if len(lines) == 1:
        return [f"{tag}{lines[0]}" if tag else lines[0]]

    out = []
    if tag:
        out.append(f"{tag}{lines[0]}")
        out.extend(lines[1:])
    else:
        out.extend(lines)
    return out


def combat_hand_inline_stat_str(stats, *, card=None, osty=None):
    """Title-row dmg/blk from RunSimulator ``stats`` (DynamicVars, keys lowercased).

    Plain ``damage`` is used for Strike-like cards; many attacks use ``calculateddamage``
    or companion hits use ``ostydamage``. Some Necrobinder cards add Osty HP to the card
    total in text but only expose the base in ``stats``—merge using combat ``osty`` blob.
    """
    if not stats:
        stats = {}
    parts = []
    cid = (card or {}).get("id") or ""
    osty_ok = bool(osty and osty.get("alive"))

    dmg = None
    if cid == "CARD.UNLEASH" and osty_ok:
        base = stats.get("calculateddamage")
        if base is None:
            base = stats.get("damage")
        if base is not None:
            hp = osty.get("hp")
            dmg = int(base) + int(hp) if isinstance(hp, (int, float)) else int(base)
    elif cid == "CARD.PROTECTOR" and osty_ok:
        base = stats.get("calculateddamage")
        if base is None:
            base = stats.get("damage")
        if base is not None:
            mhp = osty.get("max_hp")
            dmg = int(base) + int(mhp) if isinstance(mhp, (int, float)) else int(base)

    if dmg is None:
        v = stats.get("damage")
        if v is None:
            v = stats.get("calculateddamage")
        if v is None:
            v = stats.get("ostydamage")
        if v is not None:
            dmg = int(v)

    if dmg is not None:
        parts.append(c(f"{dmg}dmg", "red"))
    blk = stats.get("block")
    if blk is not None:
        parts.append(c(f"{blk}blk", "blue"))
    return " ".join(parts)


def relic_str(r):
    """Format a relic with name, counter (if any), and resolved description."""
    if isinstance(r, dict) and "name" in r:
        name = c(n(r["name"]), "bold")
        counter = r.get("counter")
        counter_str = f" {c(f'×{counter}', 'yellow')}" if counter is not None else ""
        d = desc(r.get("description", {}))
        # Resolve template vars with actual values
        vars_dict = r.get("vars") or {}
        d = resolve_template(d, vars_dict)
        return f"{name}{counter_str}" + (f": {c(d, 'dim')}" if d else "")
    return n(r)


def print_relics_grid(relics):
    """Display relics as a grid of panels, mirroring print_cards_grid's card layout."""
    c_console = Console()
    panels = []
    for r in relics:
        if not r:
            continue
        name = n(r.get("name", "?"))
        counter = r.get("counter")
        counter_str = f" {c(f'×{counter}', 'yellow')}" if counter is not None else ""
        title_str = f"🔶 {c(name, 'bold')}{counter_str}"

        d = desc(r.get("description", {}))
        vars_dict = r.get("vars") or {}
        d = resolve_template(d, vars_dict)
        body = c(d, "dim") if d else ""

        panels.append(Panel(Text.from_ansi(body), title=Text.from_ansi(title_str), width=35))

    if panels:
        c_console.print(Columns(panels))


def potion_str(p):
    """Format a potion with name and resolved description."""
    if isinstance(p, dict) and "name" in p:
        name = n(p["name"])
        d = desc(p.get("description", {}))
        vars_dict = p.get("vars") or {}
        d = resolve_template(d, vars_dict) if vars_dict else d
        idx = p.get("index", "?")
        return f"[{idx}] {name}" + (f": {c(d, 'dim')}" if d else "")
    return n(p)


def _format_upgrade_preview(stats, aug, current_cost=None):
    """Format upgrade preview string."""
    if not aug:
        return None
    aug_stats = aug.get("stats") or {}
    parts = []
    # Cost change
    aug_cost = aug.get("cost")
    if current_cost is not None and aug_cost is not None and aug_cost != current_cost:
        disp_curr = "X" if current_cost == -1 else "-" if current_cost == -2 else current_cost
        disp_aug = "X" if aug_cost == -1 else "-" if aug_cost == -2 else aug_cost
        parts.append(c(f"cost {disp_curr}→{disp_aug}", "dim"))
    # Compare all stats, show changed values with readable names
    all_keys = set(list(stats.keys()) + list(aug_stats.keys()))
    for k in sorted(all_keys):
        old = stats.get(k, 0)
        new_val = aug_stats.get(k, old)
        if new_val != old:
            if k == "damage":
                parts.append(c(f"dmg {old}→{new_val}", "dim"))
            elif k == "block":
                parts.append(c(f"blk {old}→{new_val}", "dim"))
            else:
                parts.append(c(f"{old}→{new_val}", "dim"))
    # Keyword changes (e.g., Discovery removes Exhaust)
    for kw in (aug.get("removed_keywords") or []):
        parts.append(c(f"-{_card_kw_label(kw)}", "dim"))
    for kw in (aug.get("added_keywords") or []):
        parts.append(c(f"+{_card_kw_label(kw)}", "dim"))
    return parts



def print_cards_grid(cards, show_index=False, inline_stats=False, show_playable=False, osty=None, is_shop=False, player_gold=0):
    c_console = Console()
    panels = []
    for cd in cards:
        idx = cd.get("index")
        idx_str = f"[{idx}] " if show_index and idx is not None else ""
        playable = cd.get("can_play", True)
        mark = c("● ", "green") if show_playable and playable else (c("○ ", "dim") if show_playable else "")
        type_color = {"Attack": "red", "Skill": "blue", "Power": "magenta", "Status": "dim", "Curse": "dim"}.get(cd.get("type", ""), "reset")
        up = c("+", "green") if cd.get("upgraded") else ""
        name = n(cd.get("name", "?"))
        ench = cd.get("enchantment")
        ench_str = f" {c(n(ench), 'magenta')}" if ench else ""
        title_str = f"{mark}{idx_str}{c(name, type_color)}{ench_str}{up}"
        
        body_lines = []
        cost_val = cd.get("cost", "?")
        cost = "X" if cost_val == -1 else "-" if cost_val == -2 else cost_val
        star_cost = cd.get("star_cost", 0)
        cost_str = c(str(cost), "cyan") + (f"+{c(f'{star_cost}⭐', 'yellow')}" if star_cost > 0 else "")
        rare = cd.get("rarity")
        rare_part = f" {c(rare, 'dim')}" if rare else ""
        _pre, suf = split_card_keywords(cd.get("keywords"))
        suf_part = format_card_suffix_keywords(suf)
        stat_str = ""
        if inline_stats:
            stat_str = combat_hand_inline_stat_str(cd.get("stats") or {}, card=cd, osty=osty)
            if stat_str: stat_str = " " + stat_str
                
        body_lines.append(f"({cost_str}) {c(cd.get('type',''), type_color)}{rare_part}{stat_str}{suf_part}")
        
        for line in card_description_display_lines(cd):
            if line: body_lines.append(line)
        
        stats = cd.get("stats") or {}
        aug_parts = _format_upgrade_preview(stats, cd.get("after_upgrade"), cd.get("cost"))
        if aug_parts:
            body_lines.append(f"{c('upgrade:', 'dim')} {', '.join(aug_parts)}")
            
        target = cd.get("target_type", "")
        if target == "AnyEnemy":
             body_lines.append(c('→ Any Enemy', 'yellow'))

        if is_shop:
            price = cd.get("shop_price", 0)
            affordable = c(str(price), "green") if price <= player_gold else c(str(price), "red")
            sale = c(" SALE", "yellow") if cd.get("on_sale") else ""
            body_lines.append(f"💰 {affordable}g{sale}")
             
        panels.append(Panel(Text.from_ansi("\n".join(body_lines)), title=Text.from_ansi(title_str), width=35))
        
    c_console.print(Columns(panels))


def card_pick_quantity_hint(mn, mx):
    """Short hint for prompts / help (N–M cards)."""
    if mn == mx:
        if mn == 1:
            return "pick 1 card"
        return f"pick exactly {mn} cards"
    if mn == 0:
        return f"pick 0–{mx} cards (or s to skip)"
    return f"pick {mn}–{mx} cards"


# ─── Localization ───

def _load_loc():
    """Load localization data for resolving event option names."""
    if not hasattr(_load_loc, '_cache'):
        _load_loc._cache = {}
        base = ROOT
        d = os.path.join(base, 'localization_eng')
        if os.path.isdir(d):
            for f in os.listdir(d):
                if f.endswith('.json'):
                    try:
                        data = json.load(open(os.path.join(d, f), encoding="utf-8"))
                        table = f[:-5]
                        if table not in _load_loc._cache:
                            _load_loc._cache[table] = {}
                        for k, v in data.items():
                            key = f"{table}:{k}"
                            if key not in _load_loc._cache:
                                _load_loc._cache[key] = v
                    except Exception:
                        pass
    return _load_loc._cache


def loc_resolve(key):
    """Resolve a loc key like 'NEOW.pages.INITIAL.options.PRECISE_SCISSORS.title' to readable text."""
    cache = _load_loc()
    # Try direct lookup in relevant tables
    for table in ['events', 'relics', 'ancients', 'cards', 'potions', 'monsters']:
        val_en = cache.get(f"{table}:{key}")
        if val_en:
            return val_en
    # Extract meaningful part from key
    parts = key.split('.')
    for p in reversed(parts):
        if p not in ('title', 'description', 'options', 'pages', 'INITIAL'):
            relic_en = cache.get(f"relics:{p}.title")
            desc_en = cache.get(f"relics:{p}.description", "")
            if relic_en:
                d = desc(desc_en)
                return f"{relic_en}" + (f" — {c(d, 'dim')}" if d else "")
            return p.replace('_', ' ').title()
    return key


# ─── Game Views & Screens ───

def show_player(p, show_deck=False):
    hp, mhp = p.get("hp", 0), p.get("max_hp", 1)
    blk = p.get("block", 0)
    gold = p.get("gold", 0)
    deck = p.get("deck_size", 0)
    name = n(p.get("name", "?"))

    print(f"  {c(name, 'bold')}  HP {bar(hp, mhp)} {c(f'{hp}/{mhp}', 'red')}"
          + (f"  {c(str(blk), 'blue')} blk" if blk > 0 else "")
          + f"  Gold {c(str(gold), 'yellow')}  Deck {deck}")
    for r in p.get("relics", []):
        print(f"    🔶 {relic_str(r)}")
    for pot in p.get("potions", []):
        if pot:
            print(f"    🧪 {potion_str(pot)}")
    if show_deck:
        cards = p.get("deck", [])
        if cards:
            print(f"  {c('Deck:', 'bold')}")
            print_cards_grid(cards, show_index=False, inline_stats=False, show_playable=False)


def show_combat(state):
    rnd = state.get("round", 0)
    energy = state.get("energy", 0)
    max_energy = state.get("max_energy", 0)
    draw = state.get("draw_pile_count", 0)
    discard = state.get("discard_pile_count", 0)

    print(f"\n{'─' * 60}")
    print(f"  {c(f'Round {rnd}', 'bold')}  Energy {c(f'{energy}/{max_energy}', 'cyan')}  Draw {draw}  Discard {discard}")
    show_player(state.get("player", {}))

    # Player powers/buffs/debuffs
    ppowers = state.get("player_powers") or []
    if ppowers:
        for pw in ppowers:
            amt = pw.get("amount", 0)
            amt_str = f" {amt}" if amt and amt != 0 else ""
            pw_desc = desc(pw.get("description", ""))
            if pw_desc and amt:
                pw_desc = resolve_template(pw_desc, {"Amount": abs(amt) if isinstance(amt, (int, float)) else amt})
            is_debuff = isinstance(amt, (int, float)) and amt < 0
            color = "red" if is_debuff else "green"
            label = "Debuff" if is_debuff else "Buff"
            desc_str = f": {c(pw_desc, 'dim')}" if pw_desc else ""
            pw_name = n(pw.get('name', '?'))
            print(f"    {c(label, color)} {c(f'{pw_name}{amt_str}', color)}{desc_str}")

    # Character-specific: Necrobinder's Osty (show near player)
    osty = state.get("osty")
    if osty:
        if osty.get("alive"):
            ohp, omhp = osty.get("hp", 0), osty.get("max_hp", 1)
            oblk = osty.get("block", 0)
            print(f"    🦴 {n(osty.get('name','Osty'))}  {bar(ohp, omhp)} {ohp}/{omhp}"
                  + (f"  {c(str(oblk), 'blue')}blk" if oblk else ""))
        else:
            print(f"    🦴 {c('Osty (dead)', 'dim')}")

    # Character-specific: Defect's Orbs
    orbs = state.get("orbs")
    if orbs:
        orb_icons = {"Lightning": "⚡", "Frost": "❄", "Dark": "🌑", "Plasma": "🔆", "Glass": "💠"}
        orb_parts = []
        for orb in orbs:
            otype = orb.get("type", "?")
            icon = orb_icons.get(otype, "○")
            pv, ev = orb.get("passive", 0), orb.get("evoke", 0)
            orb_parts.append(f"{icon}{n(orb.get('name', otype))}({pv}/{ev})")
        slots = state.get("orb_slots", len(orbs))
        print(f"    Orbs [{len(orbs)}/{slots}]: {' '.join(orb_parts)}")

    # Character-specific: Regent's Stars
    stars = state.get("stars")
    if stars is not None:
        print(f"    ⭐ Stars: {c(str(stars), 'yellow')}")

    print()
    for e in state.get("enemies", []):
        hp, mhp = e.get("hp", 0), e.get("max_hp", 1)
        blk = e.get("block", 0)

        # Build intent string from detailed intents
        intents = e.get("intents") or []
        intent_parts = []
        for it in intents:
            itype = it.get("type", "")
            dmg = it.get("damage")
            hits = it.get("hits")
            if itype == "Attack":
                if dmg is not None:
                    if hits and hits > 1:
                        intent_parts.append(c(f"⚔{dmg}x{hits}", "red"))
                    else:
                        intent_parts.append(c(f"⚔{dmg}", "red"))
                else:
                    intent_parts.append(c("⚔ATK", "red"))
            elif itype == "Defend":
                intent_parts.append(c("🛡DEF", "blue"))
            elif itype in ("Buff", "Heal"):
                intent_parts.append(c(f"⬆{itype}", "magenta"))
            elif itype == "Debuff":
                intent_parts.append(c("⬇ Debuff", "yellow"))
            elif itype == "DebuffStrong":
                intent_parts.append(c("⬇ Strong", "yellow"))
            elif itype in ("CardDebuff", "StatusCard"):
                intent_parts.append(c("⬇ Cards", "yellow"))
            elif itype == "DeathBlow":
                if dmg is not None:
                    intent_parts.append(c(f"💀{dmg}", "red"))
                else:
                    intent_parts.append(c("💀 KILL", "red"))
            elif itype == "Escape":
                intent_parts.append(c("🏃 Escape", "dim"))
            elif itype == "Summon":
                intent_parts.append(c("📢 Summon", "magenta"))
            elif itype == "Sleep":
                intent_parts.append(c("💤 Sleep", "dim"))
            elif itype == "Stun":
                intent_parts.append(c("⚡ Stun", "yellow"))
            elif itype == "Hidden":
                intent_parts.append(c("? ???", "dim"))
            elif itype:
                intent_parts.append(c(itype, "dim"))
        intent_str = " ".join(intent_parts) if intent_parts else c("? ???", "dim")

        # Enemy powers
        powers = e.get("powers") or []
        power_str = ""
        if powers:
            pw_parts = [f"{n(pw['name'])} {pw.get('amount','')}" for pw in powers]
            power_str = "  " + c(", ".join(pw_parts), "dim")

        print(f"  [{e['index']}] {n(e['name'])}  {bar(hp, mhp)} {hp}/{mhp}"
              + (f"  {c(str(blk), 'blue')} blk" if blk else "")
              + f"  {intent_str}{power_str}")

    print()
    hand = state.get("hand", [])
    if hand:
        print_cards_grid(hand, show_index=True, inline_stats=True, show_playable=True, osty=state.get("osty"))


def _draw_conn(buf, from_col, to_col, W):
    """Draw a connection between two columns on one line.
    from_col = lower row node, to_col = upper row node.
    Single char at midpoint: | for straight, / for up-right, \\ for up-left."""
    fc = from_col * W + W // 2
    tc = to_col * W + W // 2
    if from_col == to_col:
        if 0 <= fc < len(buf):
            buf[fc] = "|"
    else:
        mid = (fc + tc) // 2
        ch = "/" if from_col < to_col else "\\"
        if 0 <= mid < len(buf):
            buf[mid] = ch


def _render_map(map_data, choice_set=None, choice_indices=None):
    """Render map as a grid with connection lines between rows."""
    if choice_set is None:
        choice_set = set()
    if choice_indices is None:
        choice_indices = {}

    ctx = map_data.get("context", {})
    act = n(ctx.get("act_name", "?"))
    floor_n = ctx.get("floor", "?")
    cur = map_data.get("current_coord")

    ICONS = {
        "Monster": "M", "Elite": "E", "Boss": "B",
        "RestSite": "R", "Shop": "$", "Treasure": "T",
        "Event": "?", "Unknown": "?", "Ancient": "A",
    }

    rows = map_data.get("rows", [])
    if not rows:
        return

    # Collect nodes and edges
    node_map = {}
    max_col = 0
    row_numbers = set()
    # edges_up[lower_row] = [(from_col, to_col), ...] where to is in the row above
    edges_up = {}
    for row in rows:
        for nd in row:
            col, rn = nd.get("col", 0), nd.get("row", 0)
            node_map[(col, rn)] = nd
            max_col = max(max_col, col)
            row_numbers.add(rn)
            for ch in (nd.get("children") or []):
                edges_up.setdefault(rn, []).append((col, ch["col"]))

    row_numbers = sorted(row_numbers)
    total_cols = max_col + 1
    W = 4  # chars per column cell
    # Center of column c = c*W + W//2 = c*4 + 2

    width = W * total_cols + 6
    print(f"\n{'═' * width}")
    boss_name = ctx.get("boss", {}).get("name")
    if not boss_name:
        boss_name = map_data.get("boss", {}).get("name")
    boss_str = f" — {c(n(boss_name), 'red')}" if boss_name else ""
    print(f"  {c(act, 'bold')} — Floor {floor_n}{boss_str}")
    # Show current position if it's not on the map grid (e.g., starting row 0)
    if cur and cur.get("row", -1) not in row_numbers:
        print(f"  {c('You are at the start', 'green')}")
    print()

    # Boss row
    boss = map_data.get("boss", {})
    boss_col = boss.get("col", 0)
    boss_row = boss.get("row", -1)
    buf = list(" " * (W * total_cols))
    buf[boss_col * W + W // 2] = "B"
    line = "".join(buf)
    line = line[:boss_col * W + W // 2] + c("B", "red") + line[boss_col * W + W // 2 + 1:]
    print(f"  {c('B','dim')} | {line}")

    # Connection from top row to boss
    top_rn = row_numbers[-1] if row_numbers else -1
    conn = list(" " * (W * total_cols))
    for fc, tc in edges_up.get(top_rn, []):
        if tc == boss_col:  # this edge's target row should be boss
            pass
    # Actually, edges_up[top_rn] has edges from top_rn to its children.
    # Children of top row nodes go to boss.
    for nd_row in rows:
        for nd in nd_row:
            if nd.get("row") == top_rn:
                for ch in (nd.get("children") or []):
                    if ch.get("row") == boss_row:
                        fc, tc = nd["col"], ch["col"]
                        _draw_conn(conn, fc, tc, W)
    print(f"    | {c(''.join(conn), 'dim')}")

    # Map rows (top to bottom)
    for idx in range(len(row_numbers) - 1, -1, -1):
        rn = row_numbers[idx]

        # --- Node line ---
        buf = list(" " * (W * total_cols))
        color_subs = []  # (start_pos, end_pos, colored_str)
        for col in range(total_cols):
            nd = node_map.get((col, rn))
            if not nd:
                continue
            icon = ICONS.get(nd.get("type", "?"), "·")
            is_cur = (cur and cur["col"] == col and cur["row"] == rn)
            is_choice = (col, rn) in choice_set
            visited = nd.get("visited", False)

            center = col * W + W // 2
            choice_idx = choice_indices.get((col, rn))
            if is_cur:
                buf[center - 1] = "["
                buf[center] = icon
                buf[center + 1] = "]"
                color_subs.append((center - 1, center + 2, c(f"[{icon}]", "green")))
            elif choice_idx is not None:
                buf[center] = icon
                color_subs.append((center, center + 1, c(icon, "yellow")))
            elif visited:
                buf[center] = icon
                color_subs.append((center, center + 1, c(icon, "dim")))
            else:
                buf[center] = icon

        line = "".join(buf)
        # Apply colors right-to-left
        for start, end, colored in sorted(color_subs, key=lambda x: -x[0]):
            line = line[:start] + colored + line[end:]
        print(f"  {rn:>2}| {line}")

        # --- Choice index annotation line ---
        row_choices = {col: choice_indices[(col, rn)] for col in range(total_cols) if (col, rn) in choice_indices}
        if row_choices:
            ann = list(" " * (W * total_cols))
            ann_subs = []
            for col, idx in row_choices.items():
                label = f"[{idx}]"
                start = col * W + W // 2 - 1
                for j, ch in enumerate(label):
                    if 0 <= start + j < len(ann):
                        ann[start + j] = ch
                ann_subs.append((start, start + len(label), c(label, "yellow")))
            ann_line = "".join(ann)
            for start, end, colored in sorted(ann_subs, key=lambda x: -x[0]):
                ann_line = ann_line[:start] + colored + ann_line[end:]
            print(f"    | {ann_line}")

        # --- Connection line below this row (edges from row below going up to this row) ---
        if idx > 0:
            below_rn = row_numbers[idx - 1]
            conn = list(" " * (W * total_cols))
            for nd_row in rows:
                for nd in nd_row:
                    if nd.get("row") != below_rn:
                        continue
                    for ch in (nd.get("children") or []):
                        if ch.get("row") == rn:
                            _draw_conn(conn, nd["col"], ch["col"], W)
            print(f"    | {c(''.join(conn), 'dim')}")

    # Legend
    print(f"  {'─' * width}")
    legend = (f"  M=Monster E=Elite R=Rest "
              f"$=Shop T=Treasure ?=Event "
              f"{c('[x]','green')}=You {c('0','yellow')}=Choice")
    print(legend)
    # Show choice details
    if choice_indices:
        inv = {v: k for k, v in choice_indices.items()}
        parts = []
        for i in sorted(inv.keys()):
            col, row = inv[i]
            nd = node_map.get((col, row))
            if nd:
                ntype = nd.get("type", "?")
                parts.append(f"{c(str(i), 'yellow')}={ntype}")
        print(f"  {' '.join(parts)}")
    print()


def show_map(state, send_fn=None):
    """Show map at map_select. Fetches full map if send_fn available."""
    choices = state.get("choices", [])
    choice_set = {(ch["col"], ch["row"]) for ch in choices}

    # Try to fetch full map for richer display
    if send_fn:
        map_data = send_fn({"cmd": "get_map"})
        if map_data and map_data.get("type") == "map":
            # Build index map: (col,row) → choice index
            choice_indices = {(ch["col"], ch["row"]): i for i, ch in enumerate(choices)}
            _render_map(map_data, choice_set, choice_indices)
            return

    # Fallback: simple list
    ctx = state.get("context", {})
    act_name = n(ctx.get("act_name", "?"))
    floor = ctx.get("floor", "?")
    print(f"\n{'═' * 60}")
    boss_name = ctx.get("boss", {}).get("name")
    boss_str = f" — {c(n(boss_name), 'red')}" if boss_name else ""
    print(f"  {c(f'{act_name}', 'bold')} — Floor {floor}{boss_str}")
    show_player(state.get("player", {}))
    print()
    type_icons = {
        "Monster": "⚔", "Elite": "💀", "Boss": "👹",
        "RestSite": "🏕", "Shop": "🏪", "Treasure": "💎",
        "Event": "❓", "Unknown": "❓", "Ancient": "🏛",
    }
    for i, ch in enumerate(choices):
        icon = type_icons.get(ch["type"], "?")
        ntype = ch["type"]
        print(f"  [{i}] {icon} {ntype}")


def show_card_reward(state):
    print(f"\n{'─' * 60}")
    gold_earned = state.get("gold_earned", 0)
    if gold_earned > 0:
        print(f"  {c('Combat won!', 'green')} +{c(str(gold_earned), 'yellow')}g")
    print(f"  {c('Card Reward', 'bold')} — choose one (or skip)")
    show_player(state.get("player", {}))
    print()
    cards = state.get("cards", [])
    if cards:
        print_cards_grid(cards, show_index=True, inline_stats=False, show_playable=False)

    print()
    if cards:
        hi = len(cards) - 1
        print(f"  {c(f'Pick one card: type index 0–{hi}, or s to skip.', 'yellow')}")
    else:
        print(f"  {c('No cards to pick.', 'dim')}")


def show_shop(state):
    print(f"\n{'─' * 60}")
    print(f"  {c('Shop', 'bold')}")
    show_player(state.get("player", {}))
    gold = state.get("player", {}).get("gold", 0)

    print(f"\n  {c('Cards:', 'bold')}")
    shop_cards = []
    for card in state.get("cards", []):
        if not card.get("is_stocked"): continue
        cc = dict(card)
        cc["shop_price"] = cc["cost"]
        cc["cost"] = cc.get("card_cost", "?")
        shop_cards.append(cc)
    if shop_cards:
        print_cards_grid(shop_cards, show_index=True, inline_stats=False, show_playable=False, is_shop=True, player_gold=gold)

    print(f"\n  {c('Relics:', 'bold')}")
    for r in state.get("relics", []):
        if not r.get("is_stocked"): continue
        cost = r.get("cost", 0)
        affordable = c(str(cost), "green") if cost <= gold else c(str(cost), "red")
        r_desc = desc(r.get("description", ""))
        print(f"  [r{r['index']}] {n(r['name'])} — {affordable}g")
        if r_desc:
            print(f"      {c(r_desc, 'dim')}")

    print(f"\n  {c('Potions:', 'bold')}")
    for p in state.get("potions", []):
        if not p.get("is_stocked"): continue
        cost = p.get("cost", 0)
        affordable = c(str(cost), "green") if cost <= gold else c(str(cost), "red")
        p_desc = desc(p.get("description", ""))
        print(f"  [p{p['index']}] {n(p['name'])} — {affordable}g")
        if p_desc:
            print(f"      {c(p_desc, 'dim')}")

    removal_cost = state.get("card_removal_cost")
    if removal_cost:
        affordable = c(str(removal_cost), "green") if removal_cost <= gold else c(str(removal_cost), "red")
        print(f"\n  [rm] Remove a card — {affordable}g")

    print(f"\n  [leave] Leave shop")


def show_rest_site(state):
    print(f"\n{'─' * 60}")
    ctx = state.get("context", {})
    if ctx:
        print(f"  {c(n(ctx.get('act_name','?')), 'dim')} Floor {ctx.get('floor','?')}")
    print(f"  {c('Rest Site', 'bold')}")
    show_player(state.get("player", {}))
    print()
    for opt in state.get("options", []):
        enabled = opt.get("is_enabled", True)
        mark = c("●", "green") if enabled else c("○", "dim")
        opt_id = opt.get("option_id", "?")
        opt_name = opt_id
        opt_desc = opt.get("name", "")
        print(f"  {mark} [{opt['index']}] {opt_name}" + (f" — {opt_desc}" if opt_desc and opt_desc != opt_id else ""))


def show_event(state):
    print(f"\n{'─' * 60}")
    event_name = state.get("event_name", "?")
    event_display = n(event_name) if isinstance(event_name, dict) else event_name
    # Show context
    ctx = state.get("context", {})
    if ctx:
        act = n(ctx.get("act_name", "?"))
        floor = ctx.get("floor", "?")
        print(f"  {c(act, 'dim')} Floor {floor}")
    print(f"  {c(f'Event: {event_display}', 'bold')}")
    show_player(state.get("player", {}))
    print()
    for opt in state.get("options", []):
        locked = opt.get("is_locked", False)
        mark = c("○", "dim") if locked else c("●", "green")
        raw_title = opt.get("title", opt.get("text_key", f"Option {opt['index']}"))
        if isinstance(raw_title, dict):
            title = n(raw_title)
        else:
            title = loc_resolve(raw_title) if '.' in str(raw_title) or str(raw_title).isupper() else raw_title
        # Show option description with resolved template vars
        raw_desc = opt.get("description")
        opt_desc = desc(raw_desc) if raw_desc else ""
        # Resolve template vars like [MaxHp], [Gold], {Cards}
        opt_vars = opt.get("vars") or {}
        if opt_vars and opt_desc:
            opt_desc = resolve_template(opt_desc, opt_vars)
        desc_str = f" — {c(opt_desc, 'dim')}" if opt_desc else ""
        print(f"  {mark} [{opt['index']}] {title}{desc_str}")
