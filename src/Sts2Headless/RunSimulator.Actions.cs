// RunSimulator partial — Game actions and player commands

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace Sts2Headless;

public partial class RunSimulator
{
    public Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, object?>? args)
    {
        try
        {
            if (_runState == null)
                return Error("No run in progress");

            var player = _runState.Players[0];

            switch (action)
            {
                case "select_map_node":
                    return DoMapSelect(player, args);
                case "play_card":
                    return DoPlayCard(player, args);
                case "end_turn":
                    return DoEndTurn(player);
                case "choose_option":
                    return DoChooseOption(player, args);
                case "select_card_reward":
                    return DoSelectCardReward(player, args);
                case "skip_card_reward":
                    return DoSkipCardReward(player);
                case "buy_card":
                    return DoBuyCard(player, args);
                case "buy_relic":
                    return DoBuyRelic(player, args);
                case "buy_potion":
                    return DoBuyPotion(player, args);
                case "remove_card":
                    return DoRemoveCard(player);
                case "select_bundle":
                    return DoSelectBundle(player, args);
                case "select_cards":
                    return DoSelectCards(player, args);
                case "skip_select":
                    return DoSkipSelect(player);
                case "use_potion":
                    return DoUsePotion(player, args);
                case "discard_potion":
                    return DoDiscardPotion(player, args);
                case "leave_room":
                    return DoLeaveRoom(player);
                case "proceed":
                    return DoProceed(player);
                default:
                    return Error($"Unknown action: {action}");
            }
        }
        catch (Exception ex)
        {
            return ErrorWithTrace($"Action '{action}' failed", ex);
        }
    }

    #region Actions

    private Dictionary<string, object?> DoMapSelect(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("col") || !args.ContainsKey("row"))
            return Error("select_map_node requires 'col' and 'row'");

        // Reset tracking for new room
        _rewardsProcessed = false;
        _pendingCardReward = null;
        _eventOptionChosen = false;
        _lastEventOptionCount = 0;
        _pendingRewards = null;
        _lastKnownHp = player.Creature?.CurrentHp ?? 0;

        var col = Convert.ToInt32(args["col"]);
        var row = Convert.ToInt32(args["row"]);
        var coord = new MapCoord((byte)col, (byte)row);

        Log($"Moving to map coord ({col},{row})");

        // BUG-013: Wait for any pending actions (relic sessions, etc.) to complete before entering new room
        WaitForActionExecutor();
        _syncCtx.Pump();

        // Call EnterMapCoord directly (same as what MoveToMapCoordAction does in TestMode)
        // This avoids the action executor which can swallow errors silently.
        RunManager.Instance.EnterMapCoord(coord).GetAwaiter().GetResult();
        _syncCtx.Pump();
        WaitForActionExecutor();

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoPlayCard(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("card_index"))
            return Error("play_card requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var pcs = player.PlayerCombatState;
        if (pcs == null)
            return Error("Not in combat");

        var hand = pcs.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return Error($"Invalid card index {cardIndex}, hand has {hand.Count} cards");

        var card = hand[cardIndex];

        // Determine target based on card's TargetType first
        // Self/None/All cards: target = null (game handles internally)
        // AnyEnemy cards: use target_index or auto-pick first alive enemy
        Creature? target = null;
        var cardTargetType = card.TargetType;
        if (cardTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided
            if (args.TryGetValue("target_index", out var targetObj) && targetObj != null)
            {
                var targetIndex = Convert.ToInt32(targetObj);
                var state = CombatManager.Instance.DebugOnlyGetState();
                if (state != null)
                {
                    var enemies = state.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIndex >= 0 && targetIndex < enemies.Count)
                        target = enemies[targetIndex];
                }
            }
            // No target_index given: only auto-target when the choice is unambiguous
            // (a single alive enemy). With multiple enemies, picking one is a real game
            // decision — return an error instead of silently targeting enemy 0 (#79).
            if (target == null)
            {
                var state = CombatManager.Instance.DebugOnlyGetState();
                var alive = state?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
                if (alive.Count == 1)
                    target = alive[0];
                else if (alive.Count > 1)
                    return Error($"Card {card.Id.Entry} targets a single enemy (AnyEnemy); " +
                                 $"'target_index' is required when multiple enemies are alive ({alive.Count}).");
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        // Check if card can be played
        if (!card.CanPlay(out var reason, out var _))
        {
            return Error($"Cannot play card {card.GetType().Name}: {reason}");
        }

        Log($"Playing card {card.GetType().Name} (index {cardIndex}) targeting {(target != null ? target.Monster?.GetType().Name ?? "creature" : "none")}");

        var handCountBefore = hand.Count;

        var playAction = new PlayCardAction(card, target);
        RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(playAction);
        WaitForActionExecutor();

        // Check if card play had no effect (hand unchanged, same card still at same index)
        var handAfter = pcs.Hand.Cards;
        if (handAfter.Count == handCountBefore && cardIndex < handAfter.Count && handAfter[cardIndex] == card)
        {
            return Error($"Card could not be played (still in hand after action): {card.GetType().Name} [{card.Id}]");
        }

        return DetectDecisionPoint();
    }

    // STS2 build 23372702 removed CombatManager.IsPlayPhase (global) in favor of a
    // per-player PlayerCombatState.Phase. Headless is single-player, so the local
    // player (Players[0]) being in the Play phase is the equivalent signal.
    private bool IsPlayPhase()
    {
        var p = (_runState != null && _runState.Players.Count > 0) ? _runState.Players[0] : null;
        return p?.PlayerCombatState?.Phase == MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play;
    }

    private Dictionary<string, object?> DoEndTurn(Player player)
    {
        // A pending card / card-reward / bundle selection is an unresolved prompt; ending
        // the turn here would silently mutate combat instead. Surface the prompt unchanged
        // and let the caller resolve it first (#61).
        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
        {
            Log("end_turn ignored: a card selection is pending");
            return DetectDecisionPoint();
        }

        if (!IsPlayPhase())
        {
            // Might be between phases — pump and check
            _syncCtx.Pump();
            if (!IsPlayPhase())
            {
                if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                    return DetectDecisionPoint();
                // Brief wait for ThreadPool if sync context didn't catch it
                Thread.Sleep(100);
                _syncCtx.Pump();
                if (!IsPlayPhase())
                    return DetectDecisionPoint();
            }
        }

        // Ensure no actions are still running before ending turn
        WaitForActionExecutor();

        Log($"Ending turn (round={CombatManager.Instance.DebugOnlyGetState()?.RoundNumber ?? 0})");
        _turnStarted.Reset();
        _combatEnded.Reset();

        // Enable SuppressYield so Task.Yield() runs inline during enemy turn processing.
        // This prevents deadlocks during boss fights (e.g., Vantom) where continuations
        // would otherwise be posted to ThreadPool and never complete.
        // Keep SuppressYield=true through the initial fallback wait loop — multi-hit
        // attacks (e.g., 10x2) have continuations between hits that also need suppression.
        YieldPatches.SuppressYield = true;
        try
        {
            PlayerCmd.EndTurn(player, canBackOut: false);
            _syncCtx.Pump();

            // Fallback: if turn didn't complete synchronously, keep pumping with SuppressYield on
            if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
            {
                for (int i = 0; i < 50; i++)
                {
                    _syncCtx.Pump();
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (IsPlayPhase()) break;
                    Thread.Sleep(5);
                }
            }
        }
        finally
        {
            YieldPatches.SuppressYield = false;
        }

        // Second fallback: if still stuck after SuppressYield window, cancel and retry.
        // The WaitUntilQueue TCS is likely deadlocked.
        if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
        {
            Log("EndTurn stuck, cancelling and retrying with SuppressYield...");
            try
            {
                RunManager.Instance.ActionExecutor.Cancel();
                _syncCtx.Pump();
                Thread.Sleep(50);
                _syncCtx.Pump();

                // Reset the player ready state and try again with SuppressYield
                CombatManager.Instance.UndoReadyToEndTurn(player);
                _syncCtx.Pump();

                YieldPatches.SuppressYield = true;
                try
                {
                    PlayerCmd.EndTurn(player, canBackOut: false);
                    _syncCtx.Pump();
                }
                finally
                {
                    YieldPatches.SuppressYield = false;
                }

                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                    if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                    if (IsPlayPhase()) break;
                    Thread.Sleep(10);
                }
            }
            catch (Exception ex) { Log($"Cancel retry: {ex.Message}"); }

            // NUCLEAR OPTION: If STILL stuck after 2 attempts, use ThreadPool to force
            // the enemy turn processing to complete with SuppressYield permanently on.
            if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
            {
                var stuckState = CombatManager.Instance.DebugOnlyGetState();
                var stuckEnemies = stuckState?.Enemies?.Where(e => e != null && e.IsAlive)
                    .Select(e => $"{e.Monster?.GetType().Name}(hp={e.CurrentHp})").ToList();
                Log($"EndTurn STILL stuck after retry — nuclear fallback. Round={stuckState?.RoundNumber}, " +
                    $"Enemies=[{string.Join(",", stuckEnemies ?? new())}], " +
                    $"IsPlayPhase={IsPlayPhase()}, " +
                    $"IsInProgress={CombatManager.Instance.IsInProgress}, " +
                    $"ActionExecutor.IsRunning={RunManager.Instance.ActionExecutor.IsRunning}");
                try
                {
                    // Cancel again and undo
                    RunManager.Instance.ActionExecutor.Cancel();
                    _syncCtx.Pump();
                    CombatManager.Instance.UndoReadyToEndTurn(player);
                    _syncCtx.Pump();
                    Thread.Sleep(50);

                    // Run EndTurn on ThreadPool with SuppressYield permanently on
                    YieldPatches.SuppressYield = true;
                    var endTurnTask = Task.Run(() =>
                    {
                        PlayerCmd.EndTurn(player, canBackOut: false);
                    });

                    // Aggressively pump sync context while waiting (up to 5 seconds)
                    for (int i = 0; i < 500; i++)
                    {
                        _syncCtx.Pump();
                        if (endTurnTask.IsCompleted) break;
                        if (_turnStarted.IsSet || _combatEnded.IsSet) break;
                        if (!CombatManager.Instance.IsInProgress || player.Creature.IsDead) break;
                        if (IsPlayPhase()) break;
                        Thread.Sleep(10);
                    }
                    YieldPatches.SuppressYield = false;

                    // If still not play phase, try just waiting a bit more
                    if (CombatManager.Instance.IsInProgress && !IsPlayPhase() && !player.Creature.IsDead)
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            _syncCtx.Pump();
                            Thread.Sleep(10);
                            if (IsPlayPhase() || !CombatManager.Instance.IsInProgress || player.Creature.IsDead)
                                break;
                        }
                    }

                    if (IsPlayPhase())
                        Log("Nuclear fallback SUCCEEDED — play phase resumed");
                    else
                    {
                        Log("Nuclear fallback FAILED — forcing game_over to escape deadlock");
                        return GameOverState(false);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Nuclear fallback error: {ex.Message}");
                    YieldPatches.SuppressYield = false;
                }
            }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCardReward(Player player, Dictionary<string, object?>? args)
    {
        // Handle event-triggered card reward (blocking GetSelectedCardReward)
        if (_cardSelector.HasPendingReward)
        {
            if (args == null || !args.ContainsKey("card_index"))
                return Error("select_card_reward requires 'card_index'");
            var idx = Convert.ToInt32(args["card_index"]);
            Log($"Resolving event card reward: index {idx}");
            _cardSelector.ResolveReward(idx);
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }

        if (_pendingCardReward == null)
            return Error("No pending card reward");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("select_card_reward requires 'card_index'");

        var cardIndex = Convert.ToInt32(args["card_index"]);
        var cards = _pendingCardReward.Cards.ToList();
        if (cardIndex < 0 || cardIndex >= cards.Count)
            return Error($"Invalid card index {cardIndex}, {cards.Count} cards available");

        var card = cards[cardIndex];
        Log($"Selected card reward: {card.GetType().Name}");

        // Add card to deck
        MegaCrit.Sts2.Core.Commands.CardPileCmd
            .Add(card, MegaCrit.Sts2.Core.Entities.Cards.PileType.Deck)
            .GetAwaiter().GetResult();
        _syncCtx.Pump();
        RunManager.Instance.RewardSynchronizer.SyncLocalObtainedCard(card);

        _pendingCardReward = null;
        // Check if more rewards pending
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipCardReward(Player player)
    {
        if (_cardSelector.HasPendingReward)
        {
            Log("Skipping event card reward");
            _cardSelector.SkipReward();
            Thread.Sleep(50);
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        if (_pendingCardReward != null)
        {
            Log("Skipping card reward");
            _pendingCardReward.OnSkipped();
            _pendingCardReward = null;
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyCard(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("card_index"))
            return Error("buy_card requires 'card_index'");

        var idx = Convert.ToInt32(args["card_index"]);
        var allEntries = merchantRoom.GetLocalInventory().CharacterCardEntries
            .Concat(merchantRoom.GetLocalInventory().ColorlessCardEntries).ToList();
        if (idx < 0 || idx >= allEntries.Count)
            return Error($"Invalid card index {idx}");

        var entry = allEntries[idx];
        if (!entry.IsStocked) return Error("Card already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        entry.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()).GetAwaiter().GetResult();
        _syncCtx.Pump();
        Log($"Bought card: {entry.CreationResult?.Card?.GetType().Name ?? "?"} for {entry.Cost}g");

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyRelic(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("relic_index"))
            return Error("buy_relic requires 'relic_index'");

        var idx = Convert.ToInt32(args["relic_index"]);
        var entries = merchantRoom.GetLocalInventory().RelicEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid relic index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Relic already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        // Captured before the purchase resolves: a successful purchase clears entry.Model
        // to null (or replaces it with a newly-restocked item) via MerchantEntry.
        // OnTryPurchaseWrapper, so reading entry.Model for logging after that point is
        // unreliable -- see the same fix applied to DoBuyPotion below.
        var boughtRelicName = entry.Model?.GetType().Name ?? "?";

        // The pickup effect can open a card_select (e.g. KIFUDA → enchant up to 3 with
        // Adroit, #80). Run the purchase on a background task and yield as soon as a
        // pending selection appears so the caller can resolve it; the background task
        // continues once the selector's TCS is fed by select_cards.
        var inv = merchantRoom.GetLocalInventory();
        var task = Task.Run(() => entry.OnTryPurchaseWrapper(inv));
        for (int i = 0; i < 100; i++)
        {
            _syncCtx.Pump();
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
            if (_pendingBundles != null) break;
            if (task.IsCompleted) break;
            Thread.Sleep(10);
        }
        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
        {
            Log($"Buy relic {boughtRelicName}: yielded for pending selection");
            return DetectDecisionPoint();
        }
        var swRelic = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && swRelic.ElapsedMilliseconds < 3000)
        {
            _syncCtx.Pump();
            Thread.Sleep(10);
        }
        _syncCtx.Pump();
        Log($"Bought relic: {boughtRelicName} for {entry.Cost}g");

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoBuyPotion(Player player, Dictionary<string, object?>? args)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("buy_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var entries = merchantRoom.GetLocalInventory().PotionEntries;
        if (idx < 0 || idx >= entries.Count) return Error($"Invalid potion index {idx}");

        var entry = entries[idx];
        if (!entry.IsStocked) return Error("Potion already purchased");
        if (player.Gold < entry.Cost) return Error("Not enough gold");

        // Captured before the purchase resolves: a successful purchase clears entry.Model
        // to null (or replaces it with a newly-restocked item) via MerchantEntry.
        // OnTryPurchaseWrapper, so reading entry.Model for logging after that point NREs
        // (the bug this fixes -- entry.Model was null by the time the old code below read
        // it, on every non-restocking purchase).
        var boughtPotionName = entry.Model?.GetType().Name ?? "?";

        entry.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()).GetAwaiter().GetResult();
        _syncCtx.Pump();
        Log($"Bought potion: {boughtPotionName} for {entry.Cost}g");

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoRemoveCard(Player player)
    {
        if (_runState?.CurrentRoom is not MerchantRoom merchantRoom)
            return Error("Not in a shop");

        var removal = merchantRoom.GetLocalInventory().CardRemovalEntry;
        if (removal == null) return Error("No card removal available");
        if (_usedCardRemovals.Contains(removal)) return Error("Card removal already used this shop visit");
        if (player.Gold < removal.Cost) return Error("Not enough gold");

        // See _usedCardRemovals' docstring -- entity-side Used/IsStocked never flips in headless
        // mode, so this is the actual gate. Recorded at commit (gold check passed), not at the
        // real game's post-selection completion signal, since Vakuu's policy (the only headless
        // caller so far) never cancels a pending removal select once started.
        _usedCardRemovals.Add(removal);

        // Run on background thread so card selection can pause (same pattern as event options)
        var task = Task.Run(() => removal.OnTryPurchaseWrapper(merchantRoom.GetLocalInventory()));
        for (int i = 0; i < 100; i++)
        {
            _syncCtx.Pump();
            if (_cardSelector.HasPending) break;
            if (task.IsCompleted) break;
            Thread.Sleep(10);
        }
        if (_cardSelector.HasPending)
        {
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        var swCard = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && swCard.ElapsedMilliseconds < 3000)
        {
            _syncCtx.Pump();
            Thread.Sleep(10);
        }
        _syncCtx.Pump();
        Log($"Removed card for {removal.Cost}g");

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectBundle(Player player, Dictionary<string, object?>? args)
    {
        if (_pendingBundleTcs == null || _pendingBundles == null)
            return Error("No pending bundle selection");
        if (args == null || !args.ContainsKey("bundle_index"))
            return Error("select_bundle requires 'bundle_index'");

        var idx = Convert.ToInt32(args["bundle_index"]);
        Log($"Bundle selection: pack {idx}");
        var bundles = _pendingBundles;
        var tcs = _pendingBundleTcs;
        _pendingBundles = null;
        _pendingBundleTcs = null;

        // Set result directly (no ContinueWith/ThreadPool)
        var selected = (idx >= 0 && idx < bundles.Count) ? bundles[idx] : bundles[0];
        tcs.TrySetResult(selected);

        _syncCtx.Pump();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSelectCards(Player player, Dictionary<string, object?>? args)
    {
        if (!_cardSelector.HasPending)
            return Error("No pending card selection");
        if (args == null || !args.ContainsKey("indices"))
            return Error("select_cards requires 'indices' (comma-separated card indices)");

        var indicesStr = args["indices"]?.ToString() ?? "";
        var indices = indicesStr.Split(',')
            .Select(s => int.TryParse(s.Trim(), out var v) ? v : -1)
            .Where(i => i >= 0)
            .ToArray();

        Log($"Card selection: indices [{string.Join(",", indices)}]");
        _cardSelector.ResolvePendingByIndices(indices);
        _syncCtx.Pump();
        WaitForActionExecutor();

        // Extra wait for rest-site SMITH: the background ChooseLocalOption task
        // needs time to complete the upgrade after card selection resolves.
        if (_runState?.CurrentRoom is RestSiteRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            // Force to map after SMITH completes (same pattern as HEAL)
            Log("Card selection in rest site (SMITH), forcing to map");
            ForceToMap();
            return MapSelectState();
        }

        // Extra wait for shop card removal: the purchase task needs to finish
        if (_runState?.CurrentRoom is MerchantRoom)
        {
            Thread.Sleep(200);
            _syncCtx.Pump();
            WaitForActionExecutor();
            Log("Card selection in shop (card removal), refreshing shop state");
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoSkipSelect(Player player)
    {
        if (_cardSelector.HasPending)
        {
            Log("Skipping card selection");
            _cardSelector.CancelPending();
            _syncCtx.Pump();
            WaitForActionExecutor();
        }
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoUsePotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("use_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        // Determine target based on potion's TargetType first, then fall back to target_index
        Creature? target = null;
        var potionTargetType = potion.TargetType;

        // Self-targeting potions (Flex, Fortifier, etc.) ALWAYS target the player
        // regardless of any target_index the caller provides
        if (potionTargetType == TargetType.Self || potionTargetType == TargetType.TargetedNoCreature)
        {
            target = player.Creature;
        }
        else if (potionTargetType == TargetType.AnyEnemy)
        {
            // Use caller's target_index if provided, otherwise pick first alive enemy
            if (args.TryGetValue("target_index", out var tObj) && tObj != null)
            {
                var targetIdx = Convert.ToInt32(tObj);
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                if (combatState != null)
                {
                    var enemies = combatState.Enemies.Where(e => e != null && e.IsAlive).ToList();
                    if (targetIdx >= 0 && targetIdx < enemies.Count)
                        target = enemies[targetIdx];
                }
            }
            // Same single-enemy rule as play_card: only auto-target when unambiguous (#79).
            if (target == null && CombatManager.Instance.IsInProgress)
            {
                var combatState = CombatManager.Instance.DebugOnlyGetState();
                var alive = combatState?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
                if (alive.Count == 1)
                    target = alive[0];
                else if (alive.Count > 1)
                    return Error($"Potion {potion.Id.Entry} targets a single enemy (AnyEnemy); " +
                                 $"'target_index' is required when multiple enemies are alive ({alive.Count}).");
            }
        }
        // All other target types (None, All, etc.) → leave target as null

        Log($"Using potion: {potion.GetType().Name} at slot {idx} target={target?.GetType().Name ?? "none"}");
        try
        {
            var action = new MegaCrit.Sts2.Core.GameActions.UsePotionAction(potion, target, CombatManager.Instance.IsInProgress);
            RunManager.Instance.ActionQueueSet.EnqueueWithoutSynchronizing(action);
            WaitForActionExecutor();
            _syncCtx.Pump();

            // Effect may require card_select before the potion slot clears — do not discard as "stuck".
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
                return DetectDecisionPoint();

            // Verify potion was consumed
            var afterPotions = player.Potions?.ToList() ?? new();
            if (afterPotions.Contains(potion))
            {
                // Potion wasn't consumed — manually discard it
                Log("Potion not consumed by action, manually discarding");
                MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
                _syncCtx.Pump();
            }
        }
        catch (Exception ex)
        {
            Log($"Use potion failed: {ex.Message}");
            // Try manual discard as fallback
            try { MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult(); } catch { }
        }

        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoDiscardPotion(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("potion_index"))
            return Error("discard_potion requires 'potion_index'");

        var idx = Convert.ToInt32(args["potion_index"]);
        var potionsList = player.Potions?.ToList() ?? new();
        if (idx < 0 || idx >= potionsList.Count) return Error($"Invalid potion index {idx}");
        var potion = potionsList[idx];
        if (potion == null) return Error($"No potion at index {idx}");

        MegaCrit.Sts2.Core.Commands.PotionCmd.Discard(potion).GetAwaiter().GetResult();
        _syncCtx.Pump();
        return DetectDecisionPoint();
    }

    private Dictionary<string, object?> DoChooseOption(Player player, Dictionary<string, object?>? args)
    {
        if (args == null || !args.ContainsKey("option_index"))
            return Error("choose_option requires 'option_index'");

        var optionIndex = Convert.ToInt32(args["option_index"]);
        Log($"Choosing option {optionIndex}");

        // Dispatch based on ROOM TYPE (not event state) to avoid cross-contamination
        if (_runState?.CurrentRoom is RestSiteRoom restSiteRoom)
        {
            Log($"Rest site: choosing option {optionIndex}");
            try
            {
                // Run on background thread so Smith card selection can pause
                var task = Task.Run(() => RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(optionIndex));
                for (int i = 0; i < 100; i++)
                {
                    _syncCtx.Pump();
                    if (_cardSelector.HasPending) break;
                    if (task.IsCompleted) break;
                    Thread.Sleep(10);
                }
                if (_cardSelector.HasPending)
                {
                    WaitForActionExecutor();
                    return DetectDecisionPoint();
                }
                var swRest = System.Diagnostics.Stopwatch.StartNew();
                while (!task.IsCompleted && swRest.ElapsedMilliseconds < 3000)
                {
                    _syncCtx.Pump();
                    Thread.Sleep(10);
                }
                _syncCtx.Pump();
            }
            catch (Exception ex)
            {
                Log($"Rest site ChooseLocalOption failed: {ex.Message}");
            }

            // After non-Smith rest site options (HEAL, etc.), the options may not clear.
            // Wait for the action to complete (heal/dig), then force transition to map.
            if (!_cardSelector.HasPending)
            {
                Log("Rest site: option chosen (non-Smith), waiting for action then forcing to map");
                // Give the action time to complete (heal HP, dig for relic, etc.)
                WaitForActionExecutor();
                _syncCtx.Pump();
                Thread.Sleep(200);
                _syncCtx.Pump();
                WaitForActionExecutor();
                ForceToMap();
                return MapSelectState();
            }
        }
        // For events — use EventSynchronizer
        // Run Chosen() on a background thread so card selections can pause
        else if (_runState?.CurrentRoom is EventRoom)
        {
            var eventSync = RunManager.Instance.EventSynchronizer;
            var localEvent = eventSync?.GetLocalEvent();
            if (localEvent != null && !localEvent.IsFinished)
            {
                var options = localEvent.CurrentOptions;
                var optCountBefore = options?.Count ?? 0;
                if (options != null && optionIndex >= 0 && optionIndex < options.Count)
                {
                    try
                    {
                        _eventOptionChosen = true;
                        _lastEventOptionCount = options.Count;
                        // Run on thread pool so GetSelectedCards/GetSelectedCardReward can block
                        var task = Task.Run(() => options[optionIndex].Chosen());
                        for (int i = 0; i < 100; i++)
                        {
                            _syncCtx.Pump();
                            if (_cardSelector.HasPending || _cardSelector.HasPendingReward) break;
                            if (_pendingBundles != null) break;
                            if (task.IsCompleted) break;
                            Thread.Sleep(10);
                        }
                        if (_cardSelector.HasPending || _cardSelector.HasPendingReward || _pendingBundles != null)
                        {
                            WaitForActionExecutor();
                            return DetectDecisionPoint();
                        }
                        var swEvent = System.Diagnostics.Stopwatch.StartNew();
                        while (!task.IsCompleted && swEvent.ElapsedMilliseconds < 3000)
                        {
                            _syncCtx.Pump();
                            Thread.Sleep(10);
                        }
                        _syncCtx.Pump();
                        WaitForActionExecutor();
                        _syncCtx.Pump();
                    }
                    catch (Exception ex) { Log($"Event choose: {ex.Message}"); }
                }

                // Note: do NOT force-finish on `optCountAfter == optCountBefore`. Events can
                // legitimately stay interactive with the same option count (Slippery Bridge
                // Hold On loops until Overcome is chosen, #59). Trust IsFinished and let the
                // next DetectDecisionPoint return the current options for the next choice.
            }
        }

        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    private void DoLeaveRoomCore(Player player)
    {
        Log("Leaving room");
        try { RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult(); }
        catch { }
        _syncCtx.Pump();
        WaitForActionExecutor();

        // If still in a non-combat room, force to map
        var room = _runState?.CurrentRoom;
        if (room is RestSiteRoom || room is MerchantRoom || room is EventRoom || room is TreasureRoom)
        {
            Log("Force leaving non-combat room to map");
            try
            {
                RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult();
                _syncCtx.Pump();
                WaitForActionExecutor();
            }
            catch (Exception ex) { Log($"Force leave: {ex.Message}"); }
        }
        DetectDecisionPointCore();
    }

    private Dictionary<string, object?> DoLeaveRoom(Player player)
    {
        _writer = _jsonWriter;
        _jsonWriter.Reset();
        DoLeaveRoomCore(player);
        return _jsonWriter.Dict;
    }

    /// <summary>
    /// Headless mode skips Godot transition callbacks that normally trigger between-act healing
    /// (AncientEventModel.BeforeEventStarted). Replicates the original sts2.dll formula:
    ///   healAmount = MaxHp - CurrentHp  (i.e. heal to full)
    ///   if Ascension >= 2: healAmount *= 0.8
    /// No-op if the engine already healed (missingHp &lt;= 0), so this is safe alongside any
    /// future engine path that does fire the callback.
    /// Adapted from PR #83 commit cf75bec by @tianyumyum.
    /// </summary>
    private void HealBetweenActs()
    {
        if (_runState == null) return;
        var player = _runState.Players[0];
        if (player.Creature == null) return;

        var currentHp = player.Creature.CurrentHp;
        var maxHp = player.Creature.MaxHp;
        var missingHp = maxHp - currentHp;
        if (missingHp <= 0) return;

        decimal healAmount = missingHp;
        if (RunManager.Instance.HasAscension((AscensionLevel)2))
            healAmount *= 0.8m;

        var newHp = currentHp + (int)Math.Ceiling(healAmount);
        if (newHp > maxHp) newHp = maxHp;
        SetField(player.Creature, "_currentHp", newHp);
        Log($"Between-act heal: {currentHp} → {newHp} (missing={missingHp}, ascension2+={RunManager.Instance.HasAscension((AscensionLevel)2)})");
    }

    private Dictionary<string, object?> DoProceed(Player player)
    {
        Log("Proceeding");

        // Check if we need to move to next act (boss defeated)
        var room = _runState?.CurrentRoom;
        if (room is CombatRoom combatRoom && combatRoom.RoomType == RoomType.Boss)
        {
            if (combatRoom.IsPreFinished || !CombatManager.Instance.IsInProgress)
            {
                // Final act boss → victory (same rule as DetectPostCombatState, #81).
                if (_runState != null && _runState.CurrentActIndex >= 2)
                {
                    Log($"Final boss defeated via Proceed (Act {_runState.CurrentActIndex + 1}), reporting victory");
                    return GameOverState(true);
                }
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
        }

        RunManager.Instance.ProceedFromTerminalRewardsScreen().GetAwaiter().GetResult();
        WaitForActionExecutor();
        return DetectDecisionPoint();
    }

    #endregion
}
