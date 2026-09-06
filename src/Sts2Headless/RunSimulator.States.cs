// RunSimulator partial — Decision point detection and state builders
//
// This file drives detection of "what does the agent need to decide right now" and, once it knows,
// dispatches to whichever IStateWriter is currently active (_writer) to serialize it:
// JsonStateWriter for the stdio JSON protocol (RunSimulator.Writers.Json.cs), BufferStateWriter for
// the high-speed in-process path (RunSimulator.States.Buffer.cs). The reads below -- and all their
// side effects (map generation, reward rolling, act transitions, etc.) -- happen exactly once
// regardless of which writer is active; only the final serialization step differs.
//
// The *Core methods are writer-agnostic and recurse into each other directly. The few public/private
// non-Core wrappers (DetectDecisionPoint, MapSelectState, GameOverState; DoLeaveRoom lives in
// RunSimulator.Actions.cs) exist because callers elsewhere in the file expect a JSON Dictionary back
// -- they pin _writer to _jsonWriter, reset it, run the Core logic, and return the resulting dict.
// Always recurse via the *Core method, never the wrapper: calling the wrapper mid-flow would reset
// _writer and stomp on whatever output (e.g. a BufferStateWriter) is currently being built.

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
    #region Combat card resolution (shared by writers)

    private readonly record struct HandCardTargetDamage(int TargetIndex, Creature Target, int? Damage, int Repeat);

    /// <summary>Resolved *current* stat values for a hand card (mirrors NCard.UpdateVisuals):
    /// refreshes the DynamicVar preview against the card's CurrentTarget so damage reflects
    /// Strength/Weak, block reflects Frail, calculateddamage reflects current Block, etc. Read once
    /// here and shared by both JsonStateWriter and BufferStateWriter -- ClearPreview/
    /// UpdateDynamicVarPreview mutate the live card's preview state, so this must not run twice per
    /// card per decision.</summary>
    private static Dictionary<string, int> ResolveHandCardStats(CardModel c)
    {
        var stats = new Dictionary<string, int>();
        if (c.DynamicVars == null) return stats;
        try
        {
            c.DynamicVars.ClearPreview();
            c.UpdateDynamicVarPreview(CardPreviewMode.Normal, c.CurrentTarget, c.DynamicVars);
            foreach (var dv in c.DynamicVars.Values)
                stats[dv.Name.ToLowerInvariant()] = (int)dv.PreviewValue;
        }
        finally
        {
            // Restore the live card to base state -- leaving it in preview state corrupts the
            // subsequent PlayCardAction for self-cost cards like Momentum Strike (card stays in hand).
            c.DynamicVars.ClearPreview();
        }
        return stats;
    }

    /// <summary>Resolved per-target damage for attack cards: re-runs the preview per enemy via
    /// MultiCreatureTargeting (the same path the game uses to draw multi-target previews) since a
    /// card's single CurrentTarget preview can't capture target-specific modifiers (Vulnerable #60,
    /// Slow #77) or conditional hit counts (Dismantle #78, X-cost Whirlwind #82). Shared by both
    /// writers so the read happens once.</summary>
    private static List<HandCardTargetDamage>? ResolveHandCardDamageByTarget(CardModel c, List<Creature> aliveEnemies, int currentEnergy)
    {
        if (c.Type != CardType.Attack || aliveEnemies.Count == 0
            || (c.TargetType != TargetType.AnyEnemy && c.TargetType != TargetType.AllEnemies)
            || c.DynamicVars == null)
            return null;

        var result = new List<HandCardTargetDamage>();
        for (int ti = 0; ti < aliveEnemies.Count; ti++)
        {
            var tgt = aliveEnemies[ti];
            try
            {
                c.DynamicVars.ClearPreview();
                c.UpdateDynamicVarPreview(CardPreviewMode.MultiCreatureTargeting, tgt, c.DynamicVars);
                var tstats = new Dictionary<string, int>();
                foreach (var dv in c.DynamicVars.Values)
                    tstats[dv.Name.ToLowerInvariant()] = (int)dv.PreviewValue;

                // Per-hit damage = calculateddamage (override cards) or damage.
                int? perHit = tstats.TryGetValue("calculateddamage", out var cdi) && cdi > 0
                    ? cdi
                    : (tstats.TryGetValue("damage", out var di) ? di : (int?)null);
                // Hit count: explicit `repeat` var if present, else for X-cost attacks the hit
                // count is the current X (= available energy), e.g. Whirlwind (#82).
                int repeat = tstats.TryGetValue("repeat", out var ri) && ri > 0 ? ri : 1;
                if (repeat == 1 && c.EnergyCost?.CostsX == true)
                    repeat = currentEnergy;
                // Dismantle hits twice when the target is Vulnerable (#78) -- special-cased by
                // card entry since the doubled hit count isn't in any DynamicVar preview.
                if (repeat == 1 && c.Id.Entry == "DISMANTLE" && tgt.Powers != null
                    && tgt.Powers.Any(p => p?.Id.Entry == "VULNERABLE_POWER"))
                    repeat = 2;

                result.Add(new HandCardTargetDamage(ti, tgt, perHit, repeat));
            }
            finally { c.DynamicVars.ClearPreview(); }
        }
        return result;
    }

    #endregion

    #region Decision Point Detection

    public Dictionary<string, object?> DetectDecisionPoint()
    {
        // ExecuteActionToBuffer (RunSimulator.States.Buffer.cs) redirects this call -- which is
        // embedded inside DoEndTurn/DoPlayCard/etc.'s own return statements -- to a buffer writer
        // instead of building a JSON dict, so a buffer-mode caller doesn't pay for a JSON dict it
        // would immediately discard.
        if (_bufferRedirect != null)
        {
            _writer = _bufferRedirect;
            DetectDecisionPointCore();
            return _jsonWriter.Dict;
        }

        _writer = _jsonWriter;
        _jsonWriter.Reset();
        DetectDecisionPointCore();
        return _jsonWriter.Dict;
    }

    /// <summary>Buffer counterpart to DetectDecisionPoint -- identical detection/dispatch logic
    /// (and identical side effects), written into a flat float buffer instead of a JSON dict. See
    /// BufferStateWriter for the per-decision-type layout.</summary>
    public float[] DetectDecisionPointToBuffer(float[]? buffer = null)
    {
        buffer ??= new float[BufferStateWriter.RecommendedSize];
        var writer = new BufferStateWriter(this, buffer);
        var previous = _writer;
        _writer = writer;
        try { DetectDecisionPointCore(); }
        finally { _writer = previous; }
        return buffer;
    }

    private void DetectDecisionPointCore()
    {
        if (_runState == null)
        {
            _writer.WriteError("No run in progress");
            return;
        }

        var player = _runState.Players[0];

        // Check game over (death)
        if (player.Creature != null && player.Creature.IsDead)
        {
            GameOverStateCore(false);
            return;
        }

        // Check if there's a pending bundle selection (Scroll Boxes: pick 1 of N packs)
        if (_pendingBundles != null && _pendingBundleTcs != null && !_pendingBundleTcs.Task.IsCompleted)
        {
            _writer.WriteBundleSelect(_pendingBundles);
            return;
        }

        // Check if there's a pending card reward from event (GetSelectedCardReward blocking)
        if (_cardSelector.HasPendingReward)
        {
            _writer.WriteEventCardReward(_cardSelector.PendingRewardCards!);
            return;
        }

        // Check if there's a pending card selection (upgrade, remove, transform, start-of-turn powers)
        checkCardSelect:
        if (_cardSelector.HasPending && _cardSelector.PendingOptions != null)
        {
            _writer.WriteCardSelect();
            return;
        }

        // Check if there's a pending card reward
        if (_pendingCardReward != null)
        {
            CardRewardStateCore(player, _runState.CurrentRoom as CombatRoom);
            return;
        }

        // Check if RunManager reports game over (victory)
        if (RunManager.Instance.IsGameOver)
        {
            GameOverStateCore(true);
            return;
        }

        var room = _runState.CurrentRoom;

        // Map room — need to select a node
        if (room is MapRoom || room == null)
        {
            MapSelectStateCore();
            return;
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
                CombatPlayStateCore(player);
                return;
            }
            if (!CombatManager.Instance.IsInProgress || (player.Creature != null && player.Creature.IsDead))
            {
                DetectPostCombatStateCore(player, combatRoom);
                return;
            }
            // Fallback: brief wait
            for (int i = 0; i < 20; i++)
            {
                _syncCtx.Pump();
                Thread.Sleep(5);
                if (IsPlayPhase()) { CombatPlayStateCore(player); return; }
                if (!CombatManager.Instance.IsInProgress) { DetectPostCombatStateCore(player, combatRoom); return; }
            }
            CombatPlayStateCore(player);
            return;
        }

        // Event room
        if (room is EventRoom eventRoom)
        {
            EventChoiceStateCore(eventRoom);
            return;
        }

        // Rest site
        if (room is RestSiteRoom restRoom)
        {
            RestSiteStateCore(restRoom);
            return;
        }

        // Merchant/Shop
        if (room is MerchantRoom merchantRoom)
        {
            ShopStateCore(merchantRoom, player);
            return;
        }

        // Treasure room
        if (room is TreasureRoom treasureRoom)
        {
            TreasureStateCore(treasureRoom);
            return;
        }

        // Fallback
        _writer.WriteUnknown(room);
    }

    private void MapSelectStateCore()
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
            {
                _writer.WriteError("No map available");
                return;
            }
        }
        _writer.WriteMapSelect();
    }

    private Dictionary<string, object?> MapSelectState()
    {
        // Same _bufferRedirect check as DetectDecisionPoint() above, for the same reason -- see
        // DoLeaveRoom's comment (RunSimulator.Actions.cs) for the buffer-mode failure mode this
        // avoids. Reached via DoSelectCards (rest site SMITH) and DoChooseOption (rest site
        // HEAL/etc.)'s own force-to-map paths.
        if (_bufferRedirect != null)
        {
            _writer = _bufferRedirect;
            MapSelectStateCore();
            return _jsonWriter.Dict;
        }
        _writer = _jsonWriter;
        _jsonWriter.Reset();
        MapSelectStateCore();
        return _jsonWriter.Dict;
    }

    private void CombatPlayStateCore(Player player)
    {
        // Track last known HP for accurate game_over reporting (BUG-005)
        if (player.Creature != null && player.Creature.CurrentHp > 0)
            _lastKnownHp = player.Creature.CurrentHp;

        _writer.WriteCombatPlay(player);
    }

    private void DetectPostCombatStateCore(Player player, CombatRoom combatRoom)
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
                    CardRewardStateCore(player, combatRoom);
                    return;
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
                GameOverStateCore(true);
                return;
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
            DetectDecisionPointCore();
            return;
        }

        // Normal → go to map
        ForceToMap();
        MapSelectStateCore();
    }

    private void CardRewardStateCore(Player player, CombatRoom? combatRoom)
    {
        if (_pendingCardReward == null)
        {
            DetectPostCombatStateCore(player, combatRoom ?? (_runState?.CurrentRoom as CombatRoom)!);
            return;
        }
        _writer.WriteCardReward(player, combatRoom);
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

    private void EventChoiceStateCore(EventRoom eventRoom)
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
            if (_runState?.CurrentRoom is MapRoom) MapSelectStateCore(); else DetectDecisionPointCore();
            return;
        }

        var currentOptions = localEvent.CurrentOptions;
        if (currentOptions == null || currentOptions.Count == 0)
        {
            Log($"Event {localEvent.GetType().Name} has no options, auto-skipping");
            try { RunManager.Instance.EnterRoom(new MapRoom()).GetAwaiter().GetResult(); _syncCtx.Pump(); }
            catch { }
            MapSelectStateCore();
            return;
        }

        // If all options are locked, the event is effectively waiting for a "Proceed" / "Leave".
        // The game UI might show a single generic proceed button, but headless should just auto-proceed.
        if (currentOptions.Count > 0 && currentOptions.All(o => o.IsLocked))
        {
            Log($"All event options are locked, auto-proceeding.");
            DoLeaveRoomCore(_runState!.Players[0]);
            return;
        }

        _writer.WriteEventChoice(eventRoom);
    }

    private void RestSiteStateCore(RestSiteRoom restRoom)
    {
        var options = restRoom.Options;

        if (options == null || options.Count == 0)
        {
            // Options empty = choice already made (synchronizer cleared them), go to map
            Log("Rest site: options empty, proceeding to map");
            ForceToMap();
            MapSelectStateCore();
            return;
        }

        _writer.WriteRestSite(restRoom);
    }

    private void ShopStateCore(MerchantRoom merchantRoom, Player player)
    {
        var inv = merchantRoom.GetLocalInventory();
        if (inv == null) { ForceToMap(); MapSelectStateCore(); return; }

        _writer.WriteShop(merchantRoom, player);
    }

    private void TreasureStateCore(TreasureRoom treasureRoom)
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
        MapSelectStateCore();
    }

    private void GameOverStateCore(bool isVictory)
    {
        _writer.WriteGameOver(isVictory);
    }

    private Dictionary<string, object?> GameOverState(bool isVictory)
    {
        // Same _bufferRedirect check as DetectDecisionPoint()/MapSelectState() -- see DoLeaveRoom's
        // comment (RunSimulator.Actions.cs) for the buffer-mode failure this avoids. Without it, a
        // buffer-mode caller reaching death/victory through this path (rather than through
        // DetectDecisionPointCore's own game-over checks) would never see "game_over" in its
        // buffer, since the write would go to the discarded _jsonWriter.Dict instead.
        if (_bufferRedirect != null)
        {
            _writer = _bufferRedirect;
            GameOverStateCore(isVictory);
            return _jsonWriter.Dict;
        }
        _writer = _jsonWriter;
        _jsonWriter.Reset();
        GameOverStateCore(isVictory);
        return _jsonWriter.Dict;
    }

    #endregion
}
