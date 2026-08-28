// RunSimulator partial — JsonStateWriter: builds the stdio JSON protocol's decision dicts.
//
// This is a behavior-preserving move of what used to be inline dict-building code in
// RunSimulator.States.cs. The game-state reads happen in RunSimulator.States.cs (and the shared
// ResolveHandCardStats/ResolveHandCardDamageByTarget helpers there); this class only shapes that
// data into the existing JSON dict format, unchanged from before the writer-interface refactor.

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

public partial class RunSimulator
{
    private class JsonStateWriter : IStateWriter
    {
        private readonly RunSimulator _sim;
        public Dictionary<string, object?> Dict { get; private set; } = new();

        public JsonStateWriter(RunSimulator sim) { _sim = sim; }

        public void Reset() => Dict = new Dictionary<string, object?>();

        public void WriteError(string message)
        {
            Dict = new Dictionary<string, object?> { ["type"] = "error", ["message"] = message };
        }

        public void WriteUnknown(object? room)
        {
            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "unknown",
                ["context"] = _sim.RunContextJson(),
                ["room_type"] = room?.GetType().Name,
                ["message"] = "Unknown room type or state",
            };
        }

        public void WriteGameOver(bool victory)
        {
            var player = _sim._runState!.Players[0];
            var summary = _sim.PlayerSummaryJson(player);
            // BUG-005: When player died, the engine resets HP to max. Use last known HP instead.
            if (!victory)
                summary["hp"] = _sim._lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "game_over",
                ["context"] = _sim.RunContextJson(),
                ["victory"] = victory,
                ["player"] = summary,
                ["act"] = _sim._runState.CurrentActIndex + 1,
                ["floor"] = _sim._runState.ActFloor,
            };
        }

        public void WriteBundleSelect(IReadOnlyList<IReadOnlyList<CardModel>> bundles)
        {
            var bundleJson = bundles.Select((bundle, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = bundle.Select(card => _sim.CardToJson(card, includeId: false, includeAfterUpgrade: false)).ToList(),
            }).ToList();

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "bundle_select",
                ["context"] = _sim.RunContextJson(),
                ["bundles"] = bundleJson,
                ["player"] = _sim.PlayerSummaryJson(_sim._runState!.Players[0]),
            };
        }

        public void WriteEventCardReward(IReadOnlyList<CardCreationResult> cards)
        {
            var cardsJson = cards.Select((cr, i) =>
            {
                var cardInfo = _sim.CardToJson(cr.Card);
                cardInfo["index"] = i;
                return cardInfo;
            }).ToList();

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = _sim.RunContextJson(),
                ["cards"] = cardsJson,
                ["can_skip"] = true,
                ["from_event"] = true,
                ["player"] = _sim.PlayerSummaryJson(_sim._runState!.Players[0]),
            };
        }

        public void WriteCardSelect()
        {
            var cardSelector = _sim._cardSelector;
            var opts = cardSelector.PendingOptions!.Select((card, i) =>
            {
                var cardInfo = _sim.CardToJson(card, includeUpgraded: true);
                cardInfo["index"] = i;
                return cardInfo;
            }).ToList();

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_select",
                ["context"] = _sim.RunContextJson(),
                ["cards"] = opts,
                ["min_select"] = cardSelector.PendingMinSelect,
                ["max_select"] = cardSelector.PendingMaxSelect,
                ["player"] = _sim.PlayerSummaryJson(_sim._runState!.Players[0]),
                ["source_category"] = cardSelector.PendingSourceCategory,
                ["source_id"] = cardSelector.PendingSourceId,
                ["prompt_table"] = cardSelector.PendingPromptTable,
                ["prompt_key"] = cardSelector.PendingPromptKey,
            };
        }

        public void WriteMapSelect()
        {
            var map = _sim._runState?.Map;
            var currentCoord = _sim._runState!.CurrentMapCoord;

            List<Dictionary<string, object?>> choices;
            if (currentCoord.HasValue)
            {
                var currentPoint = map?.GetPoint(currentCoord.Value);
                if (currentPoint == null)
                {
                    Log($"GetPoint returned null for coord ({currentCoord.Value.col},{currentCoord.Value.row}), falling back to start");
                    // Current coord is invalid (stale after forced room transition); treat as no position
                    choices = new List<Dictionary<string, object?>>();
                    var sp = map?.StartingMapPoint;
                    if (sp?.Children != null)
                    {
                        foreach (var child in sp.Children)
                        {
                            choices.Add(new Dictionary<string, object?>
                            {
                                ["col"] = (int)child.coord.col,
                                ["row"] = (int)child.coord.row,
                                ["type"] = child.PointType.ToString(),
                            });
                        }
                    }
                }
                else
                {
                    choices = (currentPoint.Children ?? Enumerable.Empty<MapPoint>())
                        .Select(child => new Dictionary<string, object?>
                        {
                            ["col"] = (int)child.coord.col,
                            ["row"] = (int)child.coord.row,
                            ["type"] = child.PointType.ToString(),
                        })
                        .ToList();
                }
            }
            else
            {
                // Starting point — only the StartingMapPoint (Ancient) is available before entering the act
                var startPoint = map!.StartingMapPoint;
                choices = new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["col"] = (int)startPoint.coord.col,
                        ["row"] = (int)startPoint.coord.row,
                        ["type"] = startPoint.PointType.ToString(),
                    }
                };
            }

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "map_select",
                ["context"] = _sim.RunContextJson(),
                ["choices"] = choices,
                ["player"] = _sim.PlayerSummaryJson(_sim._runState!.Players[0]),
                ["act"] = _sim._runState.CurrentActIndex + 1,
                ["act_name"] = _loc.Act(_sim._runState.Act?.Id.Entry ?? "OVERGROWTH"),
                ["floor"] = _sim._runState.ActFloor,
            };
        }

        public void WriteCombatPlay(Player player)
        {
            var pcs = player.PlayerCombatState;
            var combatState = CombatManager.Instance.DebugOnlyGetState();

            // Alive enemies in the same order play_card's AnyEnemy targeting uses, so
            // damage_by_target[i].target_index aligns with the target_index clients pass.
            var aliveEnemiesForTargeting = combatState?.Enemies?
                .Where(e => e != null && e.IsAlive).ToList() ?? new();

            var hand = pcs?.Hand?.Cards?.Select((c, i) =>
            {
                var rawStats = ResolveHandCardStats(c);
                var stats = rawStats.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

                var damageByTargetRaw = ResolveHandCardDamageByTarget(c, aliveEnemiesForTargeting, pcs?.Energy ?? 0);
                List<Dictionary<string, object?>>? damageByTarget = null;
                if (damageByTargetRaw != null)
                {
                    damageByTarget = damageByTargetRaw.Select(row =>
                    {
                        var r = new Dictionary<string, object?>
                        {
                            ["target_index"] = row.TargetIndex,
                            ["name"] = _loc.Monster(row.Target.Monster?.Id.Entry ?? "UNKNOWN"),
                        };
                        if (row.Damage != null) r["damage"] = row.Damage;
                        if (row.Repeat > 1)
                        {
                            r["repeat"] = row.Repeat;
                            if (row.Damage != null) r["total_damage"] = row.Damage * row.Repeat;
                        }
                        return r;
                    }).ToList();
                }

                // Use CurrentStarCost (combat-modified) for UI/can_play; BaseStarCost ignores temporary reductions.
                var starCost = c.CurrentStarCost;
                var cardInfo = new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = c.Id.ToString(),
                    ["name"] = _loc.Card(c.Id.Entry),
                    ["cost"] = c.EnergyCost?.GetResolved() ?? 0,
                    ["type"] = c.Type.ToString(),
                    ["rarity"] = c.Rarity.ToString(),
                    ["can_play"] = c.CanPlay(out _, out _),
                    ["upgraded"] = c.IsUpgraded,
                    ["target_type"] = c.TargetType.ToString(),
                    ["stats"] = stats.Count > 0 ? stats : null,
                    ["description"] = _loc.GetText("cards", c.Id.Entry + ".description"),
                };
                if (starCost > 0)
                {
                    cardInfo["star_cost"] = starCost;
                    // BUG-007: Override can_play for star-cost cards when player lacks stars
                    if (pcs != null && pcs.Stars < starCost)
                        cardInfo["can_play"] = false;
                }
                var kws = c.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
                if (kws?.Count > 0) cardInfo["keywords"] = kws;
                if (c.Enchantment != null)
                {
                    cardInfo["enchantment"] = _loc.GetText("enchantments", c.Enchantment.Id.Entry + ".title");
                    if (c.Enchantment.Amount != 0) cardInfo["enchantment_amount"] = c.Enchantment.Amount;
                }
                if (c.Affliction != null)
                {
                    cardInfo["affliction"] = _loc.GetText("afflictions", c.Affliction.Id.Entry + ".title");
                    if (c.Affliction.Amount != 0) cardInfo["affliction_amount"] = c.Affliction.Amount;
                }
                if (damageByTarget != null && damageByTarget.Count > 0)
                    cardInfo["damage_by_target"] = damageByTarget;
                return cardInfo;
            }).ToList() ?? new();

            var playerCreatures = combatState?.PlayerCreatures?.ToList();

            var enemies = combatState?.Enemies?
                .Where(e => e != null && e.IsAlive)
                .Select((e, i) =>
                {
                    // Extract detailed intent info
                    var intents = new List<Dictionary<string, object?>>();
                    var nextIntents = e.Monster?.NextMove?.Intents;
                    if (nextIntents != null)
                    {
                        foreach (var intent in nextIntents)
                        {
                            if (intent == null) continue;
                            var intentInfo = new Dictionary<string, object?>
                            {
                                ["type"] = intent.IntentType.ToString(),
                            };
                            // Get damage for attack intents
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk && playerCreatures != null)
                            {
                                // For multi-hit attacks, expose per-hit `damage` (matching the
                                // game's intent description, which pairs GetSingleDamage with
                                // Repeat) plus an explicit `total_damage`. Reporting the total in
                                // `damage` while also reporting `hits` let clients compute
                                // damage*hits and double-count incoming damage (#67).
                                var hits = atk.Repeats;
                                if (hits > 1)
                                {
                                    intentInfo["damage"] = atk.GetSingleDamage(playerCreatures, e);
                                    intentInfo["hits"] = hits;
                                    intentInfo["total_damage"] = atk.GetTotalDamage(playerCreatures, e);
                                }
                                else
                                {
                                    intentInfo["damage"] = atk.GetTotalDamage(playerCreatures, e);
                                }
                            }
                            intents.Add(intentInfo);
                        }
                    }

                    // Enemy powers
                    var ePowers = e.Powers?.Select(pw => new Dictionary<string, object?>
                    {
                        ["id"] = pw.Id.Entry,
                        ["name"] = _loc.Power(pw.Id.Entry),
                        ["description"] = _loc.GetText("powers", pw.Id.Entry + ".description"),
                        ["amount"] = pw.Amount,
                    }).ToList();

                    return new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["id"] = e.Monster?.Id.Entry,
                        ["name"] = _loc.Monster(e.Monster?.Id.Entry ?? "UNKNOWN"),
                        ["hp"] = e.CurrentHp,
                        ["max_hp"] = e.MaxHp,
                        ["block"] = e.Block,
                        ["intents"] = intents.Count > 0 ? intents : null,
                        ["intends_attack"] = e.Monster?.IntendsToAttack ?? false,
                        ["powers"] = ePowers?.Count > 0 ? ePowers : null,
                    };
                }).ToList() ?? new();

            // Player powers/buffs
            var playerPowers = player.Creature?.Powers?.Select(pw => new Dictionary<string, object?>
            {
                ["id"] = pw.Id.Entry,
                ["name"] = _loc.Power(pw.Id.Entry),
                ["description"] = _loc.GetText("powers", pw.Id.Entry + ".description"),
                ["amount"] = pw.Amount,
            }).ToList();

            var result = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "combat_play",
                ["context"] = _sim.RunContextJson(),
                ["round"] = combatState?.RoundNumber ?? 0,
                ["energy"] = pcs?.Energy ?? 0,
                ["max_energy"] = pcs?.MaxEnergy ?? 0,
                ["hand"] = hand,
                ["enemies"] = enemies,
                ["player"] = _sim.PlayerSummaryJson(player),
                ["player_powers"] = playerPowers?.Count > 0 ? playerPowers : null,
                ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
                ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
                ["draw_pile"] = _sim.PileCardsToJson(pcs?.DrawPile),
                ["discard_pile"] = _sim.PileCardsToJson(pcs?.DiscardPile),
                ["exhaust_pile"] = _sim.PileCardsToJson(pcs?.ExhaustPile),
            };

            // Character-specific mechanics
            try
            {
                // Defect: Orbs
                var orbQueue = pcs?.OrbQueue;
                if (orbQueue?.Orbs?.Count > 0)
                {
                    result["orbs"] = orbQueue.Orbs.Select((orb, i) => new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["id"] = orb.Id.Entry,
                        ["name"] = _loc.GetText("orbs", orb.Id.Entry + ".title"),
                        ["type"] = orb.GetType().Name.Replace("Orb", ""),
                        ["passive"] = (int)orb.PassiveVal,
                        ["evoke"] = (int)orb.EvokeVal,
                    }).ToList();
                    result["orb_slots"] = orbQueue.Capacity;
                }

                // Regent: Stars
                if (pcs != null && pcs.Stars >= 0 && player.Character?.Id.Entry == "REGENT")
                {
                    result["stars"] = pcs.Stars;
                }

                // Necrobinder: Osty (minion)
                var osty = player.Osty;
                if (osty != null)
                {
                    result["osty"] = new Dictionary<string, object?>
                    {
                        ["name"] = _loc.Monster(osty.Monster?.Id.Entry ?? "OSTY"),
                        ["hp"] = osty.CurrentHp,
                        ["max_hp"] = osty.MaxHp,
                        ["block"] = osty.Block,
                        ["alive"] = osty.IsAlive,
                    };
                }
                else if (player.Character?.Id.Entry == "NECROBINDER")
                {
                    result["osty"] = new Dictionary<string, object?> { ["alive"] = false };
                }
            }
            catch (Exception ex)
            {
                Log($"Character-specific data: {ex.Message}");
            }

            // Combat history counts (see HistoryCounts doc comment): exposes the scaling inputs for
            // cards like Supermassive/Spite/Murder even when those cards aren't currently in hand.
            try
            {
                var playerHistory = HistoryCountsJson(player.Creature, combatState);
                var historyEntries = CombatManager.Instance.History.Entries;
                playerHistory["card_created_by_player"] = new Dictionary<string, object?>
                {
                    ["this_turn"] = historyEntries.OfType<CardGeneratedEntry>()
                        .Count(ge => ge.Creator == player && ge.HappenedThisTurn(combatState)),
                    ["this_combat"] = historyEntries.OfType<CardGeneratedEntry>()
                        .Count(ge => ge.Creator == player),
                };

                result["history"] = new Dictionary<string, object?>
                {
                    ["player"] = playerHistory,
                    ["enemies"] = aliveEnemiesForTargeting.Select((c, i) =>
                    {
                        var h = new Dictionary<string, object?> { ["index"] = i };
                        foreach (var kv in HistoryCountsJson(c, combatState)) h[kv.Key] = kv.Value;
                        return h;
                    }).ToList(),
                };
            }
            catch (Exception ex)
            {
                Log($"Combat history counts: {ex.Message}");
            }

            Dict = result;
        }

        public void WriteCardReward(Player player, CombatRoom? combatRoom)
        {
            var pendingCardReward = _sim._pendingCardReward!;
            var cards = pendingCardReward.Cards.Select((c, i) =>
            {
                var cardInfo = _sim.CardToJson(c);
                cardInfo["index"] = i;
                return cardInfo;
            }).ToList();

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = _sim.RunContextJson(),
                ["cards"] = cards,
                ["can_skip"] = pendingCardReward.CanSkip,
                ["gold_earned"] = _sim._runState!.Players[0].Gold - _sim._goldBeforeCombat,
                ["player"] = _sim.PlayerSummaryJson(_sim._runState!.Players[0]),
            };
        }

        public void WriteEventChoice(EventRoom eventRoom)
        {
            var localEvent = RunManager.Instance.EventSynchronizer!.GetLocalEvent()!;
            var currentOptions = localEvent.CurrentOptions!;

            var options = currentOptions
                .Select((opt, i) =>
                {
                    // Try to resolve title via loc tables
                    string? title = null;
                    if (opt.Title != null)
                    {
                        var t = _loc.GetText(opt.Title.LocTable, opt.Title.LocEntryKey);
                        // Check if we actually found a translation (not just the key echoed back)
                        if (t != opt.Title.LocEntryKey)
                            title = t;
                    }
                    // Fallback: try to extract option ID from the key and look up as relic/card/potion
                    if (title == null && opt.TextKey != null)
                    {
                        // TextKey like "NEOW.pages.INITIAL.options.STONE_HUMIDIFIER" → extract "STONE_HUMIDIFIER"
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        // Try relic, then card, then just use the optionId
                        var relic = _loc.Relic(optionId);
                        if (relic != optionId + ".title")
                            title = relic;
                        else
                        {
                            var card = _loc.Card(optionId);
                            if (card != optionId + ".title")
                                title = card;
                            else
                                title = optionId.Replace("_", " ");
                        }
                    }
                    title ??= $"option_{i}";

                    // Description: try loc table first
                    string? optDesc = null;
                    if (opt.Description != null && !string.IsNullOrEmpty(opt.Description.LocEntryKey))
                    {
                        var d = _loc.GetText(opt.Description.LocTable, opt.Description.LocEntryKey);
                        if (d != opt.Description.LocEntryKey)
                            optDesc = d;
                    }
                    // Fallback: try relic/card description
                    if (optDesc == null && opt.TextKey != null)
                    {
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        var rd = _loc.GetText("relics", optionId + ".description");
                        if (rd != optionId + ".description")
                            optDesc = rd;
                    }

                    // Extract vars: try event's own DynamicVars first, then relic
                    Dictionary<string, object?>? optVars = null;
                    // Event's DynamicVars (covers Gold, HpLoss, Heal, etc.)
                    if (localEvent.DynamicVars?.Values != null)
                    {
                        optVars = new Dictionary<string, object?>();
                        foreach (var dv in localEvent.DynamicVars.Values)
                        {
                            if (dv != null && !string.IsNullOrEmpty(dv.Name))
                                optVars[dv.Name] = (int)dv.BaseValue;
                        }
                    }

                    // Also try relic vars (for Neow options)
                    if (opt.TextKey != null)
                    {
                        var parts = opt.TextKey.Split('.');
                        var optionId = parts.Length > 0 ? parts[^1] : opt.TextKey;
                        var relicModel = ModelDb.GetByIdOrNull<RelicModel>(new ModelId("RELIC", optionId));
                        if (relicModel != null)
                        {
                            var mutable = relicModel.ToMutable();
                            if (mutable.DynamicVars?.Values != null)
                            {
                                optVars ??= new Dictionary<string, object?>();
                                foreach (var dv in mutable.DynamicVars.Values)
                                {
                                    if (dv != null && !string.IsNullOrEmpty(dv.Name))
                                        optVars[dv.Name] = (int)dv.BaseValue;
                                }
                            }
                        }
                    }

                    // `RandomCard` (Slippery Bridge / Overcome) carries a *deck index*, but the
                    // description template `{RandomCard}` should render that deck card's name. Resolve
                    // it to the localized name so clients can substitute it (#58). Scoped to this var
                    // name on purpose: other card vars (e.g. Wood Carvings' BirdCard/ToricCard) index
                    // a transform-target pool, not the deck, so a generic rule would mis-resolve them.
                    if (optVars != null && optVars.TryGetValue("RandomCard", out var rcVal) && rcVal is int rcIdx)
                    {
                        var deck = _sim._runState?.Players?[0]?.Deck?.Cards;
                        if (deck != null && rcIdx >= 0 && rcIdx < deck.Count && deck[rcIdx] != null)
                            optVars["RandomCard"] = _loc.Card(deck[rcIdx].Id.Entry);
                    }

                    return new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["title"] = title,
                        ["description"] = optDesc,
                        ["text_key"] = opt.TextKey,
                        ["is_locked"] = opt.IsLocked,
                        ["vars"] = optVars?.Count > 0 ? optVars : null,
                    };
                }).ToList();

            // Resolve event name — try ancients table first (for Neow), then events
            var eventEntry = localEvent.Id?.Entry ?? localEvent.GetType().Name.ToUpperInvariant();
            var eventName = _loc.GetText("ancients", eventEntry + ".title");
            if (eventName == eventEntry + ".title")
                eventName = _loc.Event(eventEntry);

            // Resolve event description, suppress if key not found
            string? eventDesc = null;
            if (localEvent.Description != null)
            {
                var d = _loc.GetText(localEvent.Description.LocTable, localEvent.Description.LocEntryKey);
                if (d != localEvent.Description.LocEntryKey)
                    eventDesc = d;
            }

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "event_choice",
                ["context"] = _sim.RunContextJson(),
                ["event_id"] = eventEntry,
                ["event_name"] = eventName,
                ["description"] = eventDesc,
                ["options"] = options,
                ["player"] = _sim.PlayerSummaryJson(_sim._runState!.Players[0]),
            };
        }

        public void WriteRestSite(RestSiteRoom restRoom)
        {
            var options = restRoom.Options!;
            var player = _sim._runState!.Players[0];

            var optionList = options.Select((opt, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["option_id"] = opt.OptionId,
                ["name"] = opt.GetType().Name,
                ["is_enabled"] = opt.IsEnabled,
            }).ToList();

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "rest_site",
                ["context"] = _sim.RunContextJson(),
                ["options"] = optionList,
                ["player"] = _sim.PlayerSummaryJson(player),
            };
        }

        public void WriteShop(MerchantRoom merchantRoom, Player player)
        {
            var inv = merchantRoom.GetLocalInventory()!;

            var cards = inv.CharacterCardEntries.Concat(inv.ColorlessCardEntries)
                .Select((e, i) =>
                {
                    var card = e.CreationResult?.Card;
                    Dictionary<string, object?> cardInfo;
                    if (card != null)
                    {
                        // The shop entry's card can have uninitialized DynamicVars (stats: null while
                        // after_upgrade is populated, #68). Read base stats from a fresh ModelDb clone
                        // at the card's current upgrade level, like GetUpgradedInfo.
                        Dictionary<string, object?> stats = new();
                        var cardCanonical = ModelDb.GetById<CardModel>(card.Id);
                        if (cardCanonical != null)
                        {
                            var fresh = cardCanonical.ToMutable();
                            for (int u = 0; u < card.CurrentUpgradeLevel; u++)
                            {
                                fresh.UpgradeInternal();
                                fresh.FinalizeUpgradeInternal();
                            }
                            stats = StatsFromBaseVars(fresh);
                        }
                        cardInfo = _sim.CardToJson(card, statsOverride: stats, includeId: false);
                        cardInfo["card_cost"] = cardInfo["cost"];   // energy cost, renamed to avoid colliding with shop price
                    }
                    else
                    {
                        cardInfo = new Dictionary<string, object?>
                        {
                            ["name"] = _loc.Card("?"), ["type"] = "?", ["rarity"] = "?",
                            ["card_cost"] = 0, ["description"] = _loc.GetText("cards", "?.description"),
                        };
                    }
                    cardInfo["index"] = i;
                    cardInfo["cost"] = e.Cost;           // gold price, overwrites CardToJson's energy-cost value
                    cardInfo["is_stocked"] = e.IsStocked;
                    cardInfo["on_sale"] = e.IsOnSale;
                    return cardInfo;
                }).ToList();

            var relics = inv.RelicEntries.Select((e, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = _loc.Relic(e.Model?.Id.Entry ?? "?"),
                ["description"] = _loc.GetText("relics", (e.Model?.Id.Entry ?? "?") + ".description"),
                ["cost"] = e.Cost,
                ["is_stocked"] = e.IsStocked,
            }).ToList();

            var potions = inv.PotionEntries.Select((e, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["name"] = _loc.Potion(e.Model?.Id.Entry ?? "?"),
                ["description"] = _loc.GetText("potions", (e.Model?.Id.Entry ?? "?") + ".description"),
                ["cost"] = e.Cost,
                ["is_stocked"] = e.IsStocked,
            }).ToList();

            var removal = merchantRoom.GetLocalInventory()!.CardRemovalEntry;

            Dict = new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "shop",
                ["context"] = _sim.RunContextJson(),
                ["cards"] = cards,
                ["relics"] = relics,
                ["potions"] = potions,
                ["card_removal_cost"] = removal != null && !_sim._usedCardRemovals.Contains(removal) ? removal.Cost : null,
                ["player"] = _sim.PlayerSummaryJson(player),
            };
        }
    }
}
