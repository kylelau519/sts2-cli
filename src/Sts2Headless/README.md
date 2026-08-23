# Buffer Protocol

`BufferStateWriter` ([RunSimulator.States.Buffer.cs](RunSimulator.States.Buffer.cs)) writes each decision
point into a flat `float[]` instead of the JSON path's `Dictionary<string,object?>` — no allocation, no
string keys, so a training loop can call `RunSimulator.DetectDecisionPointToBuffer(buffer)` in-process on
every step.

**Every record is exactly `RunSimulator.BufferLayout.TotalSize` (6850) floats, regardless of
`DecisionType`.** Unlike an earlier version of this format, section offsets are *fixed*: player HP always
lives at the same index whether the current decision is `combat_play` or `shop`. A consumer never needs to
branch on `DecisionType` to know where a given field lives — only to know which sections are *freshly
written this call* (see "Refresh semantics" below).

`buffer[0]` is always the `DecisionType` tag (see enum below).

## Buffer layout — `RunSimulator.BufferLayout`

All fixed offsets/sizes are public C# constants on `RunSimulator.BufferLayout` — read them from there
rather than hardcoding the numbers below if you're consuming this from C#. For other languages, these are
stable for the lifetime of this wire format:

| Section | Offset | Size (floats) | End (exclusive) |
|---|---|---|---|
| `DecisionType` | 0 | 1 | 1 |
| `PlayerSummary` | 1 | 657 | 658 |
| `Map` | 658 | 1269 | 1927 |
| `RunContext` | 1927 | 5 | 1932 |
| `CombatPlay` | 1932 | 3617 | 5549 |
| `CardSelect` | 5549 | 552 | 6101 |
| `Shop` | 6101 | 244 | 6345 |
| `RestSite` | 6345 | 12 | 6357 |
| `CardReward` | 6357 | 68 | 6425 |
| `EventChoice` | 6425 | 6 | 6431 |
| `EventCardReward` | 6431 | 66 | 6497 |
| `BundleSelect` | 6497 | 352 | 6849 |
| `GameOver` | 6849 | 1 | 6850 |
| **Total** | | **6850** | |

## Refresh semantics — what's fresh this call vs. carried over

`BufferStateWriter`'s constructor **deliberately never clears the buffer.** A freshly allocated
`float[6850]` starts all-zero for free (CLR guarantee), but on every call after that:

- `DecisionType`, `PlayerSummary`, and `RunContext` are refreshed **unconditionally, every call** —
  they're relevant to every decision (player HP/gold/relics/deck, current act/floor/boss).
- `Map` is refreshed **only when the current decision is `map_select`.** The rest of the time it holds
  whatever the last `map_select` wrote — still valid map data (the board doesn't change outside of
  navigating it), just not necessarily current if something about the map could change out from under the
  player (it can't, today).
- Every decision-specific section (`CombatPlay`, `CardSelect`, `Shop`, `RestSite`, `CardReward`,
  `EventChoice`, `EventCardReward`, `BundleSelect`, `GameOver`) is refreshed **only when its own
  `DecisionType` is the one currently active.** At every other time it holds stale data from the last time
  that decision type occurred (or all zeros, if it hasn't occurred yet this buffer's lifetime).

**This means the buffer instance matters.** `DetectDecisionPointToBuffer(buffer)` writes into whatever
array you pass it — the carryover behavior above only holds if you pass the *same* array back in on every
call. Passing a fresh/null buffer each call (`DetectDecisionPointToBuffer()` with no argument allocates a
new one) throws away all carryover and you'll only ever see zeros in sections not touched by the current
call. Conversely, allocate a **new** buffer at the start of each episode/run — reusing one across separate
runs will leak the previous run's stale sections (e.g. a `Map` from a run that's already over) into the
new run until each section gets naturally refreshed again.

## Padding conventions

Not uniform — check per field:
- **Card()** pads a missing card with id/cost/type/rarity/flags/keywords = `0`, but enchantment/affliction
  id+amount = `-1` (an id of `0` for those would collide with a real tokenizer id, so they use `-1`
  instead).
- **Relic() / Potion()** pad a missing slot with id = `0`, secondary field = `-1`.
- Most other "not applicable" scalars (intent type, per-target damage, shop entry fields, event-option
  flags, map-point type, history-count pairs for an absent enemy) use `-1`.
- Tokenizer lookups fall back to `0` (`Tokenizer.UnknownToken`) for an id the tokenizer doesn't recognize
  — see the character-token caveat below before treating `0` as "unknown" everywhere.

## Tokenizer

`_tokenizer` ([Tokenizer.cs](Tokenizer.cs)) assigns stable integer ids per category
(`afflictions, acts, card_keywords, cards, enchantments, encounters, events, intents, monsters, orbs,
potions, powers, relics`), loaded once at startup from `localization_eng/<category>.json`, sorted so ids
are stable across re-runs regardless of JSON key order. `0` is reserved as `UnknownToken` for every one of
those categories.

**Caveat — `characters` is not in that list.** `AddCharacterTokens()` assigns `IRONCLAD=0, SILENT=1,
DEFECT=2, REGENT=3, NECROBINDER=4` on its own 0-based scheme, added *after* the shared categories. This
means `_reverseTokenMap[0]` ends up `"CHARACTERS:IRONCLAD"`, clobbering the `"UNKNOWN"` entry set at
construction — `Tokenizer.Decode(0)` is broken (always reports Ironclad, not "unknown") for any category.
`Encode()` itself is unaffected (category-prefixed keys don't collide), but it means **PlayerSummary's
character field (offset `PlayerSummary + 0`) reads `0` for a completely valid, common case (the player is
Ironclad) — do not treat `0` there as "missing/unknown" the way you would for every other tokenized
field.**

## `DecisionType` (buffer[0])

| Value | Name | Written by | Refreshes (beyond DecisionType/PlayerSummary/RunContext) |
|---|---|---|---|
| 0 | Error | `WriteError` | nothing — state may be undefined mid-error, so PlayerSummary/RunContext aren't even touched |
| 1 | GameOver | `WriteGameOver` | `GameOver` |
| 2 | MapSelect | `WriteMapSelect` | `Map` |
| 3 | CombatPlay | `WriteCombatPlay` | `CombatPlay` |
| 4 | CardReward | `WriteCardReward` | `CardReward` |
| 5 | EventChoice | `WriteEventChoice` | `EventChoice` |
| 6 | RestSite | `WriteRestSite` | `RestSite` |
| 7 | Shop | `WriteShop` | `Shop` |
| 8 | BundleSelect | `WriteBundleSelect` | `BundleSelect` |
| 9 | EventCardReward | `WriteEventCardReward` | `EventCardReward` |
| 10 | CardSelect | `WriteCardSelect` | `CardSelect` |
| 11 | Unknown | `WriteUnknown` | nothing extra |

---

## Section contents

Offsets below are **local to the section** (add the section's base offset from the layout table above to
get the absolute buffer index).

### `PlayerSummary` (base `BufferLayout.PlayerSummary` = 1) — 657 floats

| Local offset | Field |
|---|---|
| 0 | character index — **`0`-based `characters` token, see the Tokenizer caveat above** |
| 1 | current HP |
| 2 | max HP |
| 3 | block |
| 4 | gold |
| 5 | max potion count (`MaxPotions = 10`: 3 base + 1 Phial Holster + 2 Potion Belt + 4 Alchemical Coffer) |
| 6 | deck size (non-null card count) |
| [7 – 86] | 40 × `Relic` slots (`MaxRelics = 40`) |
| [87 – 106] | 10 × `Potion` slots (`MaxPotions = 10`) |
| [107 – 656] | 50 × `Card` slots, i.e. the deck (`MaxHandCards = 50`, reused here as the deck cap) |

### `Map` (base `BufferLayout.Map` = 658) — 1269 floats

Full board (`MaxMapRows=20 × MaxMapCols=7` grid, row-major, one `MapPoint` per cell) plus **one** trailing
`MapPoint` for `map.BossMapPoint`: `20*7*9 + 9 = 1269`. Act/floor/boss-encounter context for whatever's
currently selectable lives in the always-fresh `RunContext` section, not duplicated here.

Note: `MapBufferSize` in the encoder code is a separate, unused constant that budgets for *two* boss nodes
— the actual `Map()` encoder only ever writes one (`map.BossMapPoint`), matching `GetFullMap()`'s JSON
output. The 1269 above is what's actually written; `MapBufferSize` looks like leftover/aspirational
sizing.

### `RunContext` (base `BufferLayout.RunContext` = 1927) — 5 floats

| Local offset | Field |
|---|---|
| 0 | current act index + 1 |
| 1 | act id (tokenizer `acts`) |
| 2 | act floor |
| 3 | boss encounter id (tokenizer `encounters`; `"NONE"` token if absent) |
| 4 | second boss encounter id (tokenizer `encounters`; `"NONE"` token if absent — branching-boss acts) |

### `CombatPlay` (base `BufferLayout.CombatPlay` = 1932) — 3617 floats

| Local offset | Field |
|---|---|
| 0 | round number |
| 1 | current energy |
| 2 | max energy |
| [3–1552] | hand: 50 slots (`MaxHandCards`) × 31 floats each — see "Hand card slot" below |
| [1553–1677] | enemies: 5 slots (`MaxEnemies`) × 25 floats each — see "Enemy slot" below |
| [1678–1709] | player powers: 16 slots (`MaxPlayerPowers`) × 2 floats (power id, amount) |
| 1710 | draw pile count |
| 1711 | discard pile count |
| 1712 | exhaust pile count |
| [1713–2262] | draw pile: 50 × `Card` (`WritePile`, always pads to `MaxHandCards` regardless of actual pile size) |
| [2263–2812] | discard pile: 50 × `Card` |
| [2813–3362] | exhaust pile: 50 × `Card` |
| [3363–3392] | orbs: 10 slots (`MaxOrbs`) × 3 floats (orb id, `PassiveVal`, `EvokeVal`) |
| 3393 | orb queue capacity |
| 3394 | Regent stars (only if character is REGENT, else `-1`) |
| 3395 | Osty current HP (`-1` if no Osty and not Necrobinder) |
| 3396 | Osty max HP |
| 3397 | Osty block |
| 3398 | Osty alive (1/0), or `0` if Necrobinder with dead/no Osty, or `-1` if not Necrobinder |
| [3399–3434] | `HistoryCounts` for the player |
| 3435 | count of cards this player generated this turn |
| 3436 | count of cards this player generated this combat |
| [3437–3616] | per enemy (5 slots): `HistoryCounts` each, 36 floats/slot — a dead/absent enemy slot still writes fixed `-1` padding for all 36, same convention as everywhere else (see "Fixed bug" note below) |

**Hand card slot — 31 floats:**

| Local offset | Field |
|---|---|
| [0–10] | `Card` |
| 11 | `CanPlay()` (1/0) |
| 12 | `CurrentStarCost` |
| [13–20] | resolved `DynamicVars`, fixed subset (`HandCardStatKeys`, in order): `damage, block, magicnumber, secondarydamage, secondarymagicnumber, calculateddamage, repeat, heal` — each `-1` if the card has no such var |
| [21–30] | per-target damage against up to 5 alive enemies (`MaxEnemies`): 2 floats each (damage, repeat), `-1,-1` if no such target |

**Enemy slot — 25 floats:**

| Local offset | Field |
|---|---|
| 0 | monster id (tokenizer `monsters`), `-1` if empty slot |
| 1 | current HP |
| 2 | max HP |
| 3 | block |
| 4 | `IntendsToAttack` (1/0) |
| [5–8] | up to 2 intents (`MaxIntentsPerEnemy`): 2 floats each (`IntentType` int, total attack damage if `AttackIntent` else `-1`), `-1,-1` if no such intent |
| [9–24] | up to 8 powers (`MaxEnemyPowers`): 2 floats each (power id via tokenizer `powers`, amount), `-1,0` if no such power |

### `CardSelect` (base `BufferLayout.CardSelect` = 5549) — 552 floats

| Local offset | Field |
|---|---|
| 0 | `PendingMinSelect` |
| 1 | `PendingMaxSelect` |
| [2–551] | 50 slots (`MaxHandCards`) × `Card` — the pending selection's option list |

### `Shop` (base `BufferLayout.Shop` = 6101) — 244 floats

| Local offset | Field |
|---|---|
| [0–209] | 15 slots (`MaxShopCards`) × 14 floats: `Card` (11) + cost + `IsStocked` (1/0) + `IsOnSale` (1/0) — character + colorless entries concatenated |
| [210–227] | 6 slots (`MaxShopRelics`) × 3 floats: relic id (tokenizer `relics`), cost, `IsStocked` |
| [228–242] | 5 slots (`MaxShopPotions`) × 3 floats: potion id (tokenizer `potions`), cost, `IsStocked` |
| 243 | card removal cost, `-1` if unavailable |

### `RestSite` (base `BufferLayout.RestSite` = 6345) — 12 floats

| Local offset | Field |
|---|---|
| [0–11] | 6 slots (`MaxRestOptions`) × 2 floats: option exists (1/`-1`), `IsEnabled` (1/0/`-1`) |

### `CardReward` (base `BufferLayout.CardReward` = 6357) — 68 floats

| Local offset | Field |
|---|---|
| 0 | `CanSkip` (1/0) |
| 1 | gold gained this combat (`player.Gold - _goldBeforeCombat`) |
| [2–67] | 6 slots (`MaxRewardCards`) × `Card` |

### `EventChoice` (base `BufferLayout.EventChoice` = 6425) — 6 floats

| Local offset | Field |
|---|---|
| 0 | event id (tokenizer `events`), `-1` if none |
| [1–5] | 5 slots (`MaxEventOptions`) × `IsLocked` (1/0), `-1` if no such option |

### `EventCardReward` (base `BufferLayout.EventCardReward` = 6431) — 66 floats

| Local offset | Field |
|---|---|
| [0–65] | 6 slots (`MaxRewardCards`) × `Card` |

### `BundleSelect` (base `BufferLayout.BundleSelect` = 6497) — 352 floats

| Local offset | Field |
|---|---|
| [0–351] | 4 bundles (`MaxBundles`) × 8 cards (`MaxBundleCards`) × `Card` — Scroll Box "pick 1 of N packs" |

### `GameOver` (base `BufferLayout.GameOver` = 6849) — 1 float

| Local offset | Field |
|---|---|
| 0 | victory (1/0) |

Act index/floor at the time of game-over are already in the always-fresh `RunContext` section, so they're
not duplicated here (an earlier version of this format wrote them twice).

**BUG-005:** when the player dies, the engine resets HP to max before this reads it, so `WriteGameOver`
patches `PlayerSummary`'s HP field (`PlayerSummary + 1`) directly with
`_lastKnownHp > 0 ? 0 : Creature.CurrentHp` after the normal `PlayerSummary` refresh. Don't trust a
`game_over` record's HP field as a literal read on defeat — it's a deliberate override, applied to the
*shared* `PlayerSummary` section (so it also affects what any other decision would see immediately after,
until `PlayerSummary` naturally refreshes again on the next call).

---

## Shared field encoders

Reusable, fixed-width encoders used inside the sections above.

### `Card` — 11 floats

| Local offset | Field |
|---|---|
| 0 | card id (tokenizer `cards`), `0` if empty slot |
| 1 | resolved energy cost |
| 2 | `CardType` (int) |
| 3 | `CardRarity` (int) |
| 4 | `IsUpgraded` (1/0) |
| 5 | `IsUpgradable` (1/0) |
| 6 | enchantment id (tokenizer `enchantments`), `-1` if none |
| 7 | enchantment amount, `-1` if none |
| 8 | affliction id (tokenizer `afflictions`), `-1` if none |
| 9 | affliction amount, `-1` if none |
| 10 | keyword bitmask: bit0 Exhaust, 1 Ethereal, 2 Innate, 3 Unplayable, 4 Retain, 5 Sly, 6 Eternal |

### `Relic` — 2 floats

| Local offset | Field |
|---|---|
| 0 | relic id (tokenizer `relics`), `0` if empty slot |
| 1 | `DisplayAmount` if `ShowCounter`, else `-1` |

### `Potion` — 2 floats

| Local offset | Field |
|---|---|
| 0 | potion id (tokenizer `potions`), `0` if empty slot |
| 1 | target type: `0`=Self, `1`=AnyAlly, `2`=AnyEnemy, `-1`=other/empty |

### `HistoryCounts` — 36 floats (18 counters × `this_turn, this_combat`)

Per-creature combat-history counts (exposes scaling inputs for cards like Supermassive/Spite/Murder even
when not currently in hand). A `null` creature (e.g. an absent enemy slot) writes `-1,-1` for every pair.

| Pair start | Counter |
|---|---|
| 0 | block_gained |
| 2 | card_afflicted |
| 4 | card_discarded |
| 6 | card_drawn |
| 8 | card_exhausted |
| 10 | card_generated |
| 12 | card_played |
| 14 | creature_attacked |
| 16 | damage_received |
| 18 | damage_dealt |
| 20 | energy_spent |
| 22 | monster_move_performed |
| 24 | orb_channeled |
| 26 | potion_used |
| 28 | power_received |
| 30 | power_applied |
| 32 | stars_modified |
| 34 | summoned |

### `MapPoint` — 9 floats

| Local offset | Field |
|---|---|
| 0 | `MapPointType` (int), `-1` if the grid cell is empty. Values per code comment: 0=Unassigned 1=Unknown 2=Shop 3=Treasure 4=RestSite 5=Monster 6=Elite 7=Boss 8=Ancient (the run's starting point is tagged Ancient, not Monster/Unknown) |
| 1 | visited (1/0) |
| 2 | current (1/0) |
| 3,4 | child 1 (col,row), `-1,-1` if none |
| 5,6 | child 2 (col,row), `-1,-1` if none |
| 7,8 | child 3 (col,row), `-1,-1` if none |

---

## Fixed bugs (history, for anyone diffing against an older copy of this doc)

`HistoryCounts` used to `return` before advancing its `ref int offset` when passed a `null` creature,
instead of writing padding like every other encoder here. Combined with the old per-`DecisionType`
variable-width layout, that meant `CombatPlay` records silently shifted every field after the per-enemy
history-counts loop whenever fewer than `MaxEnemies=5` enemies were alive — i.e. almost always. Fixed by
making `HistoryCounts` write `-1,-1` padding per counter for a `null` creature, same as `Card`/`Relic`/
`Potion`/`MapPoint`. Combined with the move to fixed section offsets (this doc), a `CombatPlay` record's
layout no longer depends on how many enemies happen to be alive.
