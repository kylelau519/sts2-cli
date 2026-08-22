// RunSimulator partial — Decision point detection and state builders

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Sts2Headless;

public partial class RunSimulator
{
    #region Decision Point Detection

    private Dictionary<string, object?> DetectDecisionPoint()
    {
        if (_runState == null)
            return Error("No run in progress");

        var player = _runState.Players[0];

        // Check game over (death)
        if (player.Creature != null && player.Creature.IsDead)
        {
            return GameOverState(false);
        }

        // Check if there's a pending bundle selection (Scroll Boxes: pick 1 of N packs)
        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            var bundles = _pendingBundles.Select((bundle, i) => new Dictionary<string, object?>
            {
                ["index"] = i,
                ["cards"] = bundle.Select(card => CardToJson(card, includeId: false, includeAfterUpgrade: false)).ToList(),
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "bundle_select",
                ["context"] = RunContextJson(),
                ["bundles"] = bundles,
                ["player"] = PlayerSummaryJson(player),
            };
        }

        // Check if there's a pending card reward from event (GetSelectedCardReward blocking)
        if (_cardSelector.HasPendingReward)
        {
            var rewardCards = _cardSelector.PendingRewardCards!;
            var cards = rewardCards.Select((cr, i) =>
            {
                var cardInfo = CardToJson(cr.Card);
                cardInfo["index"] = i;
                return cardInfo;
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_reward",
                ["context"] = RunContextJson(),
                ["cards"] = cards,
                ["can_skip"] = true,
                ["from_event"] = true,
                ["player"] = PlayerSummaryJson(_runState!.Players[0]),
            };
        }

        // Check if there's a pending card selection (upgrade, remove, transform, start-of-turn powers)
        checkCardSelect:
        if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
        {
            var opts = _cardSelector.PendingOptions.Select((card, i) =>
            {
                var cardInfo = CardToJson(card, includeUpgraded: true);
                cardInfo["index"] = i;
                return cardInfo;
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["type"] = "decision",
                ["decision"] = "card_select",
                ["context"] = RunContextJson(),
                ["cards"] = opts,
                ["min_select"] = _cardSelector.PendingMinSelect,
                ["max_select"] = _cardSelector.PendingMaxSelect,
                ["player"] = PlayerSummaryJson(player),
                ["source_category"] = _cardSelector.PendingSourceCategory,
                ["source_id"] = _cardSelector.PendingSourceId,
                ["prompt_table"] = _cardSelector.PendingPromptTable,
                ["prompt_key"] = _cardSelector.PendingPromptKey,
            };
        }

        // Check if there's a pending card reward
        if (_pendingCardReward != null)
        {
            return CardRewardState(player, _runState.CurrentRoom as CombatRoom);
        }

        // Check if RunManager reports game over (victory)
        if (RunManager.Instance.IsGameOver)
        {
            return GameOverState(true);
        }

        var room = _runState.CurrentRoom;

        // Map room — need to select a node
        if (room is MapRoom || room == null)
        {
            return MapSelectState();
        }

        // Combat room
        if (room is CombatRoom combatRoom)
        {
            // With Task.Yield() patched, combat init should be synchronous
            _syncCtx.Pump();
            WaitForActionExecutor();

            // Re-check for pending card selections AFTER pump (BUG-024: start-of-turn effects
            // like Tools of Trade create card selections during Pump, AFTER the initial HasPending check)
            if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
            {
                goto checkCardSelect;  // Jump back to card_select handling
            }

            if (CombatManager.Instance.IsInProgress && IsPlayPhase())
            {
                return CombatPlayState(player);
            }
            if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
            {
                return DetectPostCombatState(player, combatRoom);
            }
            // Fallback: brief wait
            for (int i = 0; i < 20; i++)
            {
                _syncCtx.Pump();
                Thread.Sleep(5);
                if (IsPlayPhase()) return CombatPlayState(player);
                if (!CombatManager.Instance.IsInProgress) return DetectPostCombatState(player, combatRoom);
            }
            return CombatPlayState(player);
        }

        // Event room
        if (room is EventRoom eventRoom)
        {
            return EventChoiceState(eventRoom);
        }

        // Rest site
        if (room is RestSiteRoom restRoom)
        {
            return RestSiteState(restRoom);
        }

        // Merchant/Shop
        if (room is MerchantRoom merchantRoom)
        {
            return ShopState(merchantRoom, player);
        }

        // Treasure room
        if (room is TreasureRoom treasureRoom)
        {
            return TreasureState(treasureRoom);
        }

        // Fallback
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "unknown",
            ["context"] = RunContextJson(),
            ["room_type"] = room?.GetType().Name,
            ["message"] = "Unknown room type or state",
        };
    }

    private Dictionary<string, object?> MapSelectState()
    {
        var map = _runState?.Map;
        if (map == null)
        {
            Log("Map is null, generating...");
            try
            {
                RunManager.Instance.GenerateMap().GetAwaiter().GetResult();
                _syncCtx.Pump();
                map = _runState?.Map;
            }
            catch (Exception ex)
            {
                Log($"GenerateMap failed: {ex.Message}");
            }
            if (map == null)
                return Error("No map available");
        }
        var currentCoord = _runState!.CurrentMapCoord;

        List<Dictionary<string, object?>> choices;
        if (currentCoord.HasValue)
        {
            var currentPoint = map.GetPoint(currentCoord.Value);
            if (currentPoint == null)
            {
                Log($"GetPoint returned null for coord ({currentCoord.Value.col},{currentCoord.Value.row}), falling back to start");
                // Current coord is invalid (stale after forced room transition); treat as no position
                choices = new List<Dictionary<string, object?>>();
                var sp = map.StartingMapPoint;
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
            var startPoint = map.StartingMapPoint;
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

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "map_select",
            ["context"] = RunContextJson(),
            ["choices"] = choices,
            ["player"] = PlayerSummaryJson(_runState!.Players[0]),
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
        };
    }

    private Dictionary<string, object?> CombatPlayState(Player player)
    {
        var pcs = player.PlayerCombatState;
        var combatState = CombatManager.Instance.DebugOnlyGetState();

        // Track last known HP for accurate game_over reporting (BUG-005)
        if (player.Creature != null && player.Creature.CurrentHp > 0)
            _lastKnownHp = player.Creature.CurrentHp;

        // Alive enemies in the same order play_card's AnyEnemy targeting uses, so
        // damage_by_target[i].target_index aligns with the target_index clients pass.
        var aliveEnemiesForTargeting = combatState?.Enemies?
            .Where(e => e != null && e.IsAlive).ToList() ?? new();

        var hand = pcs?.Hand?.Cards?.Select((c, i) =>
        {
            // Export the *currently resolved* stat values, not the card base: refresh the
            // DynamicVar previews (mirrors NCard.UpdateVisuals) so damage reflects Strength/Weak,
            // block reflects Frail, calculateddamage reflects current Block, etc. ClearPreview
            // resets PreviewValue to BaseValue, and only damage/block/calculated vars override it,
            // so reading PreviewValue uniformly is safe. Issues #65 #69 #70 #71 #74 #75.
            var stats = new Dictionary<string, object?>();
            if (c.DynamicVars != null)
            {
                try
                {
                    c.DynamicVars.ClearPreview();
                    c.UpdateDynamicVarPreview(
                        MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.Normal,
                        c.CurrentTarget, c.DynamicVars);
                    foreach (var dv in c.DynamicVars.Values)
                    {
                        stats[dv.Name.ToLowerInvariant()] = (int)dv.PreviewValue;
                    }
                }
                finally
                {
                    // Restore the live card to base state. UpdateDynamicVarPreview mutates the
                    // card's preview (and for self-cost cards like Momentum Strike, leaving it in
                    // preview state corrupts the subsequent PlayCardAction — card stays in hand).
                    c.DynamicVars.ClearPreview();
                }
            }

            // Per-target resolved damage for attack cards: the scalar `stats` above use the
            // card's CurrentTarget, but a single value can't capture target-specific modifiers
            // (Vulnerable #60, Slow #77) or conditional hit counts (Dismantle #78, X-cost
            // Whirlwind #82). Re-run the preview per enemy via MultiCreatureTargeting, the same
            // path the game uses to draw multi-target previews, and read the resolved vars.
            List<Dictionary<string, object?>>? damageByTarget = null;
            if (c.Type == CardType.Attack && aliveEnemiesForTargeting.Count > 0
                && (c.TargetType == TargetType.AnyEnemy || c.TargetType == TargetType.AllEnemies)
                && c.DynamicVars != null)
            {
                damageByTarget = new List<Dictionary<string, object?>>();
                for (int ti = 0; ti < aliveEnemiesForTargeting.Count; ti++)
                {
                    var tgt = aliveEnemiesForTargeting[ti];
                    try
                    {
                        c.DynamicVars.ClearPreview();
                        c.UpdateDynamicVarPreview(
                            MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.MultiCreatureTargeting,
                            tgt, c.DynamicVars);
                        var tstats = new Dictionary<string, object?>();
                        foreach (var dv in c.DynamicVars.Values)
                            tstats[dv.Name.ToLowerInvariant()] = (int)dv.PreviewValue;

                        // Per-hit damage = calculateddamage (override cards) or damage.
                        int? perHit = tstats.TryGetValue("calculateddamage", out var cdv) && cdv is int cdi && cdi > 0
                            ? cdi
                            : (tstats.TryGetValue("damage", out var dv2) && dv2 is int di ? di : (int?)null);
                        // Hit count: explicit `repeat` var if present, else for X-cost attacks
                        // the hit count is the current X (= available energy), e.g. Whirlwind (#82).
                        int repeat = tstats.TryGetValue("repeat", out var rv) && rv is int ri && ri > 0 ? ri : 1;
                        if (repeat == 1 && c.EnergyCost?.CostsX == true && pcs != null)
                            repeat = pcs.Energy;
                        // Dismantle hits twice when the target is Vulnerable (#78). The doubled
                        // hit count lives in Dismantle.OnPlay, not in any DynamicVar preview or
                        // the Hook.ModifyAttackHitCount path (which needs an AttackCommand we
                        // don't have at preview time), so it's special-cased by card entry.
                        if (repeat == 1 && c.Id.Entry == "DISMANTLE" && tgt.Powers != null
                            && tgt.Powers.Any(p => p?.Id.Entry == "VULNERABLE_POWER"))
                            repeat = 2;

                        var row = new Dictionary<string, object?>
                        {
                            ["target_index"] = ti,
                            ["name"] = _loc.Monster(tgt.Monster?.Id.Entry ?? "UNKNOWN"),
                        };
                        if (perHit != null) row["damage"] = perHit;
                        if (repeat > 1)
                        {
                            row["repeat"] = repeat;
                            if (perHit != null) row["total_damage"] = perHit * repeat;
                        }
                        damageByTarget.Add(row);
                    }
                    finally { c.DynamicVars.ClearPreview(); }
                }
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
            ["context"] = RunContextJson(),
            ["round"] = combatState?.RoundNumber ?? 0,
            ["energy"] = pcs?.Energy ?? 0,
            ["max_energy"] = pcs?.MaxEnergy ?? 0,
            ["hand"] = hand,
            ["enemies"] = enemies,
            ["player"] = PlayerSummaryJson(player),
            ["player_powers"] = playerPowers?.Count > 0 ? playerPowers : null,
            ["draw_pile_count"] = pcs?.DrawPile?.Cards?.Count ?? 0,
            ["discard_pile_count"] = pcs?.DiscardPile?.Cards?.Count ?? 0,
            ["draw_pile"] = PileCardsToJson(pcs?.DrawPile),
            ["discard_pile"] = PileCardsToJson(pcs?.DiscardPile),
            ["exhaust_pile"] = PileCardsToJson(pcs?.ExhaustPile),
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

        return result;
    }

    private Dictionary<string, object?> DetectPostCombatState(Player player, CombatRoom combatRoom)
    {
        Log($"Post-combat: RoomType={combatRoom.RoomType}, IsPreFinished={combatRoom.IsPreFinished}");
        _syncCtx.Pump();

        // Generate rewards manually instead of using TestMode auto-accept
        if (_pendingRewards == null && !_rewardsProcessed)
        {
            _goldBeforeCombat = player.Gold;
            try
            {
                var rewardsSet = new RewardsSet(player).WithRewardsFromRoom(combatRoom);
                // build 23372702: GenerateWithoutOffering() now returns Task (void);
                // generated rewards live on rewardsSet.Rewards afterwards.
                rewardsSet.GenerateWithoutOffering().GetAwaiter().GetResult();
                var rewards = rewardsSet.Rewards;
                _syncCtx.Pump();

                // Auto-collect gold and potions, but present card choices to agent
                var cardRewards = new List<CardReward>();
                foreach (var reward in rewards)
                {
                    if (reward is GoldReward || reward is MegaCrit.Sts2.Core.Rewards.RelicReward
                        || reward is MegaCrit.Sts2.Core.Rewards.PotionReward)
                    {
                        reward.SelectUnsynchronized().GetAwaiter().GetResult();
                        _syncCtx.Pump();
                    }
                    else if (reward is CardReward cr)
                    {
                        cardRewards.Add(cr);
                    }
                }

                if (cardRewards.Count > 0)
                {
                    _pendingCardReward = cardRewards[0];
                    _pendingRewards = rewards;
                    return CardRewardState(player, combatRoom);
                }

                _pendingRewards = null;
            }
            catch (Exception ex)
            {
                Log($"Reward generation failed: {ex.Message}");
            }
        }

        // No more pending rewards — proceed
        _pendingCardReward = null;
        _pendingRewards = null;
        _rewardsProcessed = true;

        // Boss → next act, OR final victory after the last act's boss (#81). Act index is
        // 0-based and STS2 has 3 acts (0/1/2); killing the Act-3 (index 2) boss has no next
        // act — EnterNextAct NREs and DetectDecisionPoint falls through to an empty
        // map_select. Report victory directly in that case.
        if (combatRoom.RoomType == RoomType.Boss)
        {
            if (_runState != null && _runState.CurrentActIndex >= 2)
            {
                Log($"Final boss defeated (Act {_runState.CurrentActIndex + 1}), reporting victory");
                return GameOverState(true);
            }
            Log("Boss defeated, entering next act");
            RunManager.Instance.EnterNextAct().GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
            HealBetweenActs();
            if (_runState?.Map?.StartingMapPoint != null)
            {
                Log($"Entering starting Ancient map point ({_runState.Map.StartingMapPoint.coord.col},{_runState.Map.StartingMapPoint.coord.row})");
                RunManager.Instance.EnterMapCoord(_runState.Map.StartingMapPoint.coord).GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            return DetectDecisionPoint();
        }

        // Normal → go to map
        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> CardRewardState(Player player, CombatRoom? combatRoom)
    {
        if (_pendingCardReward == null)
            return DetectPostCombatState(player, combatRoom ?? (_runState?.CurrentRoom as CombatRoom)!);

        var cards = _pendingCardReward.Cards.Select((c, i) =>
        {
            var cardInfo = CardToJson(c);
            cardInfo["index"] = i;
            return cardInfo;
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "card_reward",
            ["context"] = RunContextJson(),
            ["cards"] = cards,
            ["can_skip"] = _pendingCardReward.CanSkip,
            ["gold_earned"] = _runState!.Players[0].Gold - _goldBeforeCombat,
            ["player"] = PlayerSummaryJson(_runState!.Players[0]),
        };
    }

    private void ForceToMap()
    {
        try
        {
            RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch { }

        if (_runState?.CurrentRoom is not MapRoom)
        {
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch (Exception ex) { Log($"ForceToMap: {ex.Message}"); }
        }
    }

    private Dictionary<string, object?> EventChoiceState(EventRoom eventRoom)
    {
        var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
        _syncCtx.Pump();

        // Reset the choice-tracking flag once we re-export the event state. Earlier this
        // block force-finished events whose option count was unchanged, but that incorrectly
        // killed legitimate loops like Slippery Bridge Hold On (#59). Rely on IsFinished
        // instead and let DetectDecisionPoint show the current options for the next choice.
        if (_eventOptionChosen) _eventOptionChosen = false;

        // If event is finished, proceed to map
        if (localEvent == null || localEvent.IsFinished)
        {
            Log($"Event {localEvent?.GetType().Name ?? "null"} finished, proceeding");
            try
            {
                RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch { }
            // Force to map if still in event room
            if (_runState?.CurrentRoom is EventRoom)
            {
                try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
                catch { }
            }
            return _runState?.CurrentRoom is MapRoom ? MapSelectState() : DetectDecisionPoint();
        }

        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options, auto-skipping");
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch { }
            return MapSelectState();
        }

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
                    var deck = _runState?.Players?[0]?.Deck?.Cards;
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

        // If all options are locked, the event is effectively waiting for a "Proceed" / "Leave".
        // The game UI might show a single generic proceed button, but headless should just auto-proceed.
        if (options.Count > 0 && options.All(o => (bool?)o["is_locked"] == true))
        {
            Log($"All event options are locked, auto-proceeding.");
            return DoLeaveRoom(_runState!.Players[0]);
        }
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

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "event_choice",
            ["context"] = RunContextJson(),
            ["event_id"] = eventEntry,
            ["event_name"] = eventName,
            ["description"] = eventDesc,
            ["options"] = options,
            ["player"] = PlayerSummaryJson(_runState!.Players[0]),
        };
    }

    private Dictionary<string, object?> RestSiteState(RestSiteRoom restRoom)
    {
        var options = restRoom.Options;
        var player = _runState!.Players[0];

        if (options == null || options.Count == 0)
        {
            // Options empty = choice already made (synchronizer cleared them), go to map
            Log("Rest site: options empty, proceeding to map");
            ForceToMap();
            return MapSelectState();
        }

        var optionList = options.Select((opt, i) => new Dictionary<string, object?>
        {
            ["index"] = i,
            ["option_id"] = opt.OptionId,
            ["name"] = opt.GetType().Name,
            ["is_enabled"] = opt.IsEnabled,
        }).ToList();

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "rest_site",
            ["context"] = RunContextJson(),
            ["options"] = optionList,
            ["player"] = PlayerSummaryJson(player),
        };
    }

    private Dictionary<string, object?> ShopState(MerchantRoom merchantRoom, Player player)
    {
        var inv = merchantRoom.GetLocalInventory();
        if (inv == null) { ForceToMap(); return MapSelectState(); }

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
                    cardInfo = CardToJson(card, statsOverride: stats, includeId: false);
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

        var removal = merchantRoom.GetLocalInventory().CardRemovalEntry;

        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "shop",
            ["context"] = RunContextJson(),
            ["cards"] = cards,
            ["relics"] = relics,
            ["potions"] = potions,
            ["card_removal_cost"] = removal?.Cost,
            ["player"] = PlayerSummaryJson(player),
        };
    }

    private Dictionary<string, object?> TreasureState(TreasureRoom treasureRoom)
    {
        // Treasure rooms give relics via TreasureRoomRelicSynchronizer
        Log("Treasure room — collecting rewards");

        WaitForActionExecutor();
        _syncCtx.Pump();

        // TreasureRoom.EnterInternal opens a relic-picking session (BeginRelicPicking) that the
        // headless must drive, otherwise (a) no relic is ever awarded and (b) the session stays
        // open so the NEXT treasure room's BeginRelicPicking throws "relic picking session while
        // one was already occurring!" (issue #56). Auto-pick the first offered relic: PickRelicLocally
        // enqueues a PickRelicAction whose execution awards the relic and ends the session
        // (OnPicked -> EndRelicVoting). An empty offer is closed with CompleteWithNoRelics.
        try
        {
            var relicSync = RunManager.Instance.TreasureRoomRelicSynchronizer;
            if (relicSync?.CurrentRelics != null)
            {
                // The actual relic grant normally lives in the UI node's RelicsAwarded handler
                // (RelicCmd.Obtain per result), which is absent in headless. Capture the awarded
                // results, then grant them ourselves after the pick resolves (granting inside the
                // event — mid action-execution — risks re-entrancy).
                List<MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult>? awarded = null;
                Action<List<MegaCrit.Sts2.Core.Entities.TreasureRelicPicking.RelicPickingResult>> capture = r => awarded = r;
                relicSync.RelicsAwarded += capture;
                try
                {
                    if (relicSync.CurrentRelics.Count > 0)
                    {
                        Log($"Auto-picking treasure relic 0 of {relicSync.CurrentRelics.Count}");
                        relicSync.PickRelicLocally(0);
                    }
                    else
                    {
                        relicSync.CompleteWithNoRelics();
                    }
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                    _syncCtx.Pump();
                }
                finally { relicSync.RelicsAwarded -= capture; }

                if (awarded != null)
                {
                    foreach (var res in awarded)
                    {
                        if (res.relic != null && res.player != null)
                            RelicCmd.Obtain(res.relic.ToMutable(), res.player).GetAwaiter().GetResult();
                    }
                    _syncCtx.Pump();
                    WaitForActionExecutor();
                    _syncCtx.Pump();
                }
            }
        }
        catch (Exception ex) { Log($"Treasure relic pick: {ex.Message}"); }

        try
        {
            treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
            _syncCtx.Pump();
            treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
            _syncCtx.Pump();
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("relic picking session"))
        {
            // BUG-013: Relic session conflict — wait for pending session then retry
            Log($"Relic session conflict, waiting and retrying: {ex.Message}");
            WaitForActionExecutor();
            _syncCtx.Pump();
            try
            {
                treasureRoom.DoNormalRewards().GetAwaiter().GetResult();
                _syncCtx.Pump();
                treasureRoom.DoExtraRewardsIfNeeded().GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
            catch (Exception retryEx) { Log($"Treasure rewards retry failed: {retryEx.Message}"); }
        }
        catch (Exception ex) { Log($"Treasure rewards: {ex.Message}"); }

        ForceToMap();
        return MapSelectState();
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        var player = _runState!.Players[0];
        var summary = PlayerSummaryJson(player);
        // BUG-005: When player died, the engine resets HP to max. Use last known HP instead.
        if (!isVictory)
            summary["hp"] = _lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);
        return new Dictionary<string, object?>
        {
            ["type"] = "decision",
            ["decision"] = "game_over",
            ["context"] = RunContextJson(),
            ["victory"] = isVictory,
            ["player"] = summary,
            ["act"] = _runState.CurrentActIndex + 1,
            ["floor"] = _runState.ActFloor,
        };
    }

    #endregion
}
