using System.Diagnostics;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Entities.Ascension;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;

namespace Sts2Headless;

/// <summary>
/// Synchronization context that executes continuations inline immediately.
/// Task.Yield() posts to SynchronizationContext.Current — by executing inline,
/// the yield becomes a no-op and the entire async chain runs synchronously.
/// Uses a recursion guard to queue nested posts and drain them after.
/// </summary>
internal class InlineSynchronizationContext : SynchronizationContext
{
    private readonly Queue<(SendOrPostCallback, object?)> _queue = new();
    private bool _executing;

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        d(state);
    }

    public void Pump()
    {
        // Drain all queued callbacks, taking care to allow re-entrant queueing
        while (_queue.Count > 0)
        {
            var (cb, st) = _queue.Dequeue();
            cb(st);
        }
    }
}

/// <summary>
/// English localization lookup — loads eng JSON files for display names.
/// </summary>
internal class LocLookup
{
    private readonly Dictionary<string, Dictionary<string, string>> _eng = new();

    public LocLookup()
    {
        Load(RepoPaths.FindContaining("localization_eng"), _eng);
    }

    private static void Load(string dir, Dictionary<string, Dictionary<string, string>> target)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
                if (data != null) target[name] = data;
            }
            catch { }
        }
    }

    /// <summary>Strip BBCode tags like [gold], [/blue], [b], [sine], etc.</summary>
    private static string StripBBCode(string text)
    {
        return System.Text.RegularExpressions.Regex.Replace(text, @"\[/?[a-zA-Z_][a-zA-Z0-9_=]*\]", "");
    }

    /// <summary>Return localized string for JSON output.</summary>
    public string GetText(string table, string key)
    {
        var rawText = _eng.GetValueOrDefault(table)?.GetValueOrDefault(key) ?? key;
        return StripBBCode(rawText);
    }
    // Convenience helpers using ModelId
    public string Card(string entry) => GetText("cards", entry + ".title");
    public string Monster(string entry)
    {
        var key = entry + ".name";
        var result = GetText("monsters", key);
        // If no dedicated entry, fall back to the base segment key (e.g. DECIMILLIPEDE_SEGMENT_FRONT → DECIMILLIPEDE_SEGMENT)
        if (result == key)
        {
            var lastUnderscore = entry.LastIndexOf('_');
            if (lastUnderscore > 0)
            {
                var baseEntry = entry[..lastUnderscore];
                var baseKey = baseEntry + ".name";
                var baseResult = GetText("monsters", baseKey);
                if (baseResult != baseKey) return baseResult;
            }
        }
        return result;
    }
    public string Relic(string entry) => GetText("relics", entry + ".title");
    public string Potion(string entry) => GetText("potions", entry + ".title");
    public string Power(string entry) => GetText("powers", entry + ".title");
    public string Event(string entry) => GetText("events", entry + ".title");
    public string Act(string entry) => GetText("acts", entry + ".title");

}

/// <summary>
/// Full run simulator — manages the game lifecycle from character selection
/// through map navigation, combat, events, rest sites, shops, and act transitions.
/// Drives the engine forward until it hits a "decision point" requiring external input.
/// </summary>
public partial class RunSimulator
{
    private static int? _expectedSaveSchemaVersion;
    private static bool _expectedSaveSchemaVersionReady;
    private static readonly object _expectedSaveSchemaVersionLock = new();

    private RunState? _runState;
    private static bool _modelDbInitialized;
    private static readonly InlineSynchronizationContext _syncCtx = new();
    private readonly ManualResetEventSlim _turnStarted = new(false);
    private readonly ManualResetEventSlim _combatEnded = new(false);
    private static readonly LocLookup _loc = new();
    private bool _eventOptionChosen;
    private int _lastEventOptionCount;

    // Pending rewards for card selection (populated after combat, before proceeding)
    private List<Reward>? _pendingRewards;
    private CardReward? _pendingCardReward;
    private bool _rewardsProcessed;
    private int _goldBeforeCombat;
    private int _lastKnownHp;
    private readonly HeadlessCardSelector _cardSelector = new();
    // MerchantCardRemovalEntry.Used (which gates its own IsStocked) is only ever set by
    // NMerchantCardRemoval, the shop screen's Godot UI node -- unreachable in headless mode,
    // which drives the entity's OnTryPurchaseWrapper directly with no UI node in the scene
    // tree. Tracked here instead, keyed by entry reference identity (a fresh
    // MerchantCardRemovalEntry per shop visit, per MerchantInventory's own construction) so a
    // single shop can't sell unlimited card removals -- see DoRemoveCard and the JSON/buffer
    // writers' card_removal_cost export.
    private readonly HashSet<MerchantCardRemovalEntry> _usedCardRemovals = new();
    // Pending bundle selection (Scroll Boxes: pick 1 of N packs)
    private IReadOnlyList<IReadOnlyList<CardModel>>? _pendingBundles;
    private TaskCompletionSource<IEnumerable<CardModel>>? _pendingBundleTcs;

    private static bool _cardSelectorRegistered;
    private static bool _combatEventsRegistered;

    private static readonly Tokenizer _tokenizer = new();
    public static string ExportTokenMap() => _tokenizer.ExportJson();

    // Decision-point output sink. DetectDecisionPointCore (and everything it dispatches to) reads
    // game state exactly once and hands it to whichever writer is active -- _jsonWriter for the
    // stdio JSON protocol, a BufferStateWriter for the high-speed in-process path. See
    // RunSimulator.Writers.cs / RunSimulator.Writers.Json.cs / RunSimulator.States.Buffer.cs.
    private readonly JsonStateWriter _jsonWriter;
    private IStateWriter _writer = null!;

    public RunSimulator()
    {
        _jsonWriter = new JsonStateWriter(this);
        _writer = _jsonWriter;
    }

    #region Runner lifecycle

    public Dictionary<string, object?> StartRun(string character, int ascension = 0, string? seed = null)
    {
        try
        {
            CleanUp();
            EnsureModelDbInitialized();

            var player = CreatePlayer(character);
            if (player == null)
                return Error($"Unknown character: {character}");

            var seedStr = seed != null
                ? MegaCrit.Sts2.Core.Helpers.SeedHelper.CanonicalizeSeed(seed)
                : "headless_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Log($"Creating RunState with seed={seedStr}");

            // Use CreateForTest which properly handles mutable copies internally
            _runState = RunState.CreateForTest(
                players: new[] { player },
                ascensionLevel: ascension,
                seed: seedStr
            );
            typeof(RunState).GetField("<UnlockState>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_runState, MegaCrit.Sts2.Core.Unlocks.UnlockState.all);

            // Set up RunManager with test mode
            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;

            // Force Neow event (blessing selection at start)
            _runState.ExtraFields.StartedWithNeow = true;

            // Generate rooms for all acts
            RunManager.Instance.GenerateRooms();
            Log("Rooms generated");

            // Launch the run
            RunManager.Instance.Launch();
            Log("Run launched");

            // Register event handlers for combat turn transitions
            if (!_combatEventsRegistered)
            {
                CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
                CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();
                _combatEventsRegistered = true;
            }

            // Finalize starting relics
            RunManager.Instance.FinalizeStartingRelics().GetAwaiter().GetResult();
            Log("Starting relics finalized");

            // Enter first act (generates map)
            RunManager.Instance.EnterAct(0, doTransition: false).GetAwaiter().GetResult();
            Log("Entered Act 0");

            // Register card selector for cards that need player choice
            if (!_cardSelectorRegistered)
            {
                CardSelectCmd.UseSelector(_cardSelector);
                _cardSelectorRegistered = true;
            }
            BundlePatches._bundleSimRef = this;

            // Now we should be at the map — detect decision point
            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRun failed", ex);
        }
    }

    /// <summary>
    /// Fast-path combat-only reset for training loops: everything StartRun+EnterRoom("combat")
    /// does, minus the one part they don't need -- generating a real map/room layout for every
    /// act (measured ~15-30ms of Reset()'s cost This is potentially useful if restarting become a blocking wall.
    /// </summary>
    public Dictionary<string, object?> StartRunIntoCombat(string character, int ascension, string? seed, string encounterId)
    {
        try
        {
            CleanUp();
            EnsureModelDbInitialized();

            var player = CreatePlayer(character);
            if (player == null)
                return Error($"Unknown character: {character}");

            var seedStr = seed != null
                ? MegaCrit.Sts2.Core.Helpers.SeedHelper.CanonicalizeSeed(seed)
                : "headless_" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Log($"Creating RunState with seed={seedStr} (fast combat-only path)");

            _runState = RunState.CreateForTest(
                players: new[] { player },
                ascensionLevel: ascension,
                seed: seedStr
            );
            typeof(RunState).GetField("<UnlockState>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_runState, MegaCrit.Sts2.Core.Unlocks.UnlockState.all);

            var netService = new NetSingleplayerGameService();
            RunManager.Instance.SetUpTest(_runState, netService);
            LocalContext.NetId = netService.NetId;

            // The mock map: CurrentActIndex is already 0 from CreateForTest, and Act resolves as
            // Acts[CurrentActIndex] without needing a generated map, so this is the only map-side
            // setup EnterRoomInternal/CombatRoom.Enter need.
            _runState.Map = new MockSinglePointActMap();

            RunManager.Instance.GenerateRooms();
            RunManager.Instance.Launch();

            if (!_combatEventsRegistered)
            {
                CombatManager.Instance.TurnStarted += _ => _turnStarted.Set();
                CombatManager.Instance.CombatEnded += _ => _combatEnded.Set();
                _combatEventsRegistered = true;
            }

            RunManager.Instance.FinalizeStartingRelics().GetAwaiter().GetResult();

            if (!_cardSelectorRegistered)
            {
                CardSelectCmd.UseSelector(_cardSelector);
                _cardSelectorRegistered = true;
            }
            BundlePatches._bundleSimRef = this;

            var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounterId));
            if (encModel == null) return Error($"Unknown encounter: {encounterId}");
            var room = new CombatRoom(encModel.ToMutable(), _runState);
            RunManager.Instance.EnterRoom(room).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();

            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("StartRunIntoCombat failed", ex);
        }
    }

    /// <summary>
    /// Cheap episode-boundary reset for training loops that farm one encounter repeatedly: heals
    /// the player to full HP on the *same* run (same map/act/relics/deck) and re-enters the given
    /// encounter, instead of tearing down the whole run via StartRun. This could be useful if .reset() becomes a blocking wall.
    /// </summary>
    public Dictionary<string, object?> HealAndEnterCombat(string encounterId)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var creature = player.Creature;
            if (creature == null) return Error("Player has no creature");

            if (creature.CurrentHp < creature.MaxHp)
                creature.HealInternal((decimal)(creature.MaxHp - creature.CurrentHp));

            var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounterId));
            if (encModel == null) return Error($"Unknown encounter: {encounterId}");
            var room = new CombatRoom(encModel.ToMutable(), _runState);
            RunManager.Instance.EnterRoom(room).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();

            return DetectDecisionPoint();
        }
        catch (Exception ex)
        {
            return ErrorWithTrace("HealAndEnterCombat failed", ex);
        }
    }

    private Player? CreatePlayer(string characterName)
    {
        return characterName.ToLowerInvariant() switch
        {
            "ironclad" => Player.CreateForNewRun<Ironclad>(UnlockState.all, 1uL),
            "silent" => Player.CreateForNewRun<Silent>(UnlockState.all, 1uL),
            "defect" => Player.CreateForNewRun<Defect>(UnlockState.all, 1uL),
            "regent" => Player.CreateForNewRun<Regent>(UnlockState.all, 1uL),
            "necrobinder" => Player.CreateForNewRun<Necrobinder>(UnlockState.all, 1uL),
            _ => null
        };
    }
    #endregion

    #region Debug commands

    private static readonly System.Reflection.BindingFlags NonPublic =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    /// <summary>Get the backing List&lt;T&gt; behind an IReadOnlyList property via reflection.</summary>
    private static List<T>? GetBackingList<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        return field?.GetValue(obj) as List<T>;
    }

    private static void SetField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName, NonPublic);
        field?.SetValue(obj, value);
    }

    public Dictionary<string, object?> SetPlayer(Dictionary<string, System.Text.Json.JsonElement> args)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];

            if (args.TryGetValue("hp", out var hpEl) && player.Creature != null)
                SetField(player.Creature, "_currentHp", hpEl.GetInt32());
            if (args.TryGetValue("max_hp", out var mhpEl) && player.Creature != null)
                SetField(player.Creature, "_maxHp", mhpEl.GetInt32());
            if (args.TryGetValue("gold", out var goldEl))
                player.Gold = goldEl.GetInt32();

            if (args.TryGetValue("max_potions", out var maxPotionsEl))
            {
                int newMax = maxPotionsEl.GetInt32();
                if (newMax > player.MaxPotionCount)
                    player.AddToMaxPotionCount(newMax - player.MaxPotionCount);
                else if (newMax < player.MaxPotionCount)
                    player.SubtractFromMaxPotionCount(player.MaxPotionCount - newMax);
            }

            if (args.TryGetValue("relics", out var relicsEl))
            {
                var list = GetBackingList<RelicModel>(player, "_relics");
                if (list != null)
                {
                    list.Clear();
                    foreach (var rEl in relicsEl.EnumerateArray())
                    {
                        var id = rEl.GetString();
                        if (id == null) continue;
                        var model = ModelDb.GetById<RelicModel>(new ModelId("RELIC", id));
                        if (model != null) list.Add(model.ToMutable());
                    }
                }
            }
            if (args.TryGetValue("deck", out var deckEl))
            {
                // Remove existing cards from RunState tracking
                foreach (var c in player.Deck.Cards.ToList())
                    _runState.RemoveCard(c);
                player.Deck.Clear(silent: true);
                // Add new cards via RunState.CreateCard (sets Owner + registers)
                foreach (var cEl in deckEl.EnumerateArray())
                {
                    var id = cEl.GetString();
                    if (id == null) continue;
                    var canonical = ModelDb.GetById<CardModel>(new ModelId("CARD", id));
                    if (canonical != null)
                    {
                        var card = _runState.CreateCard(canonical, player);
                        player.Deck.AddInternal(card, silent: true);
                    }
                }
            }
            if (args.TryGetValue("potions", out var potionsEl))
            {
                var slots = GetBackingList<PotionModel>(player, "_potionSlots")
                         ?? GetBackingList<PotionModel?>(player, "_potionSlots") as System.Collections.IList;
                if (slots != null)
                {
                    for (int i = 0; i < slots.Count; i++) slots[i] = null;
                    int idx = 0;
                    foreach (var pEl in potionsEl.EnumerateArray())
                    {
                        if (idx >= slots.Count) break;
                        var id = pEl.GetString();
                        if (id != null)
                        {
                            var model = ModelDb.GetById<PotionModel>(new ModelId("POTION", id));
                            // Inject a mutable instance (not the canonical model — that throws
                            // CanonicalModelException when the game reads potion.Owner) and set its
                            // Owner, or UsePotionAction fails with "without an owner!".
                            if (model != null)
                            {
                                var mutable = model.ToMutable();
                                mutable.Owner = player;
                                slots[idx] = mutable;
                            }
                        }
                        idx++;
                    }
                }
            }

            Log($"SetPlayer: hp={player.Creature?.CurrentHp} gold={player.Gold} relics={player.Relics.Count} deck={player.Deck?.Cards?.Count}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["player"] = PlayerSummaryJson(player),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetPlayer failed", ex); }
    }

    public Dictionary<string, object?> EnterRoom(string roomType, string? encounter, string? eventId)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var runState = _runState;
            Log($"EnterRoom: type={roomType} encounter={encounter} event={eventId}");

            AbstractRoom room;
            switch (roomType.ToLowerInvariant())
            {
                case "combat":
                case "monster":
                case "elite":
                {
                    if (string.IsNullOrEmpty(encounter))
                        encounter = "SHRINKER_BEETLE_WEAK"; // default encounter
                    var encModel = ModelDb.GetById<EncounterModel>(new ModelId("ENCOUNTER", encounter));
                    if (encModel == null) return Error($"Unknown encounter: {encounter}");
                    room = new CombatRoom(encModel.ToMutable(), runState);
                    break;
                }
                case "shop":
                    room = new MerchantRoom();
                    break;
                case "rest":
                case "rest_site":
                    room = new RestSiteRoom();
                    break;
                case "event":
                {
                    if (string.IsNullOrEmpty(eventId))
                        return Error("event requires 'event' parameter (e.g. CHANGELING_GROVE)");
                    var evModel = ModelDb.GetById<EventModel>(new ModelId("EVENT", eventId));
                    if (evModel == null) return Error($"Unknown event: {eventId}");
                    room = new EventRoom(evModel);
                    break;
                }
                case "treasure":
                    room = new TreasureRoom(_runState.CurrentActIndex);
                    break;
                default:
                    return Error($"Unknown room type: {roomType}");
            }

            RunManager.Instance.EnterRoom(room).GetAwaiter().GetResult();
            _syncCtx.Pump();
            WaitForActionExecutor();
            return DetectDecisionPoint();
        }
        catch (Exception ex) { return ErrorWithTrace("EnterRoom failed", ex); }
    }

    public Dictionary<string, object?> SetDrawOrder(List<string> cardIds)
    {
        try
        {
            if (_runState == null) return Error("No run in progress");
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;
            if (pcs?.DrawPile == null) return Error("Not in combat");

            var drawList = GetBackingList<CardModel>(pcs.DrawPile, "_cards");
            if (drawList == null) return Error("Cannot access draw pile");

            var newOrder = new List<CardModel>();
            var available = new List<CardModel>(drawList);
            foreach (var cardId in cardIds)
            {
                var match = available.FirstOrDefault(c =>
                    c.Id.Entry.Equals(cardId, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    newOrder.Add(match);
                    available.Remove(match);
                }
            }
            newOrder.AddRange(available);

            drawList.Clear();
            drawList.AddRange(newOrder);

            Log($"SetDrawOrder: {newOrder.Count} cards, top={newOrder.FirstOrDefault()?.Id.Entry}");
            return new Dictionary<string, object?>
            {
                ["type"] = "ok",
                ["draw_pile_count"] = drawList.Count,
                ["top_cards"] = newOrder.Take(5).Select(c => _loc.Card(c.Id.Entry)).ToList(),
            };
        }
        catch (Exception ex) { return ErrorWithTrace("SetDrawOrder failed", ex); }
    }

    public void RandomizeDrawPile(int seed)
    {
        if (_runState == null) return;
        var pcs = _runState.Players[0].PlayerCombatState;
        if (pcs?.DrawPile == null) return;

        var drawList = GetBackingList<CardModel>(pcs.DrawPile, "_cards");
        if (drawList == null) return;

        var rng = new Random(seed);
        for (int i = drawList.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (drawList[i], drawList[j]) = (drawList[j], drawList[i]);
        }
    }
    // Save/Load   → RunSimulator.SaveLoad.cs
    // Actions     → RunSimulator.Actions.cs
    // States      → RunSimulator.States.cs
    #endregion

    #region Helpers

    private void WaitForActionExecutor()
    {
        try
        {
            // Ensure sync context is set for this thread
            SynchronizationContext.SetSynchronizationContext(_syncCtx);

            // Pump the synchronization context to execute any pending continuations
            _syncCtx.Pump();

            // Executor may stay "running" while the game awaits headless card selection / reward (e.g. Attack Potion).
            // Spinning here would time out and downstream code could mis-handle an in-flight potion use (BUG-026).
            if (_cardSelector.HasPending || _cardSelector.HasPendingReward)
                return;

            var executor = RunManager.Instance.ActionExecutor;
            if (executor.IsRunning)
            {
                // Pump while waiting for executor. Task.Yield()/Cmd.Wait() are patched to be
                // synchronous (see PatchTaskYield/PatchCmdWait), so anything still running here
                // is either genuine background-thread work (a Task.Run -- see the comment below)
                // or CPU-bound game logic; either way it usually clears in well under a
                // millisecond. Spin/yield first so that common case costs microseconds instead of
                // the OS scheduler's ~1ms sleep granularity, and only fall back to real sleeping
                // once it's clearly not a fast case, so a genuinely-stuck executor still times out
                // at roughly the same 1s wall-clock bound as before.
                var sw = Stopwatch.StartNew();
                while (executor.IsRunning && sw.ElapsedMilliseconds < 1000)
                {
                    _syncCtx.Pump();
                    if (!executor.IsRunning) break;
                    SpinOrSleep(sw.ElapsedMilliseconds);
                }
            }

            // executor.IsRunning going false doesn't guarantee every effect has actually landed:
            // some paths (e.g. an enemy's on-turn-end status/curse application) fire via a
            // Task.Run whose continuation posts back onto _syncCtx from a background thread --
            // that Post can arrive *after* this method already returned, so state gets captured
            // mid-update and the same seed+actions can replay to a different hand next time
            // (observed: opening-hand draw pile counts differing across replays of an identical
            // action log). Re-check a cheap state fingerprint until it stops changing before
            // trusting it -- bounded so a genuinely-stuck fingerprint can't hang forever. Same
            // spin-then-sleep rationale as the executor wait above: the 5-consecutive-match
            // requirement is unchanged, only the per-iteration wait cost is adaptive now.
            int? last = null;
            int stableStreak = 0;
            const int requiredStable = 5;
            var swStable = Stopwatch.StartNew();
            while (stableStreak < requiredStable && swStable.ElapsedMilliseconds < 400)
            {
                SpinOrSleep(swStable.ElapsedMilliseconds);
                _syncCtx.Pump();
                var fp = StabilityFingerprint();
                if (fp == last) stableStreak++;
                else { stableStreak = 1; last = fp; }
            }
        }
        catch (Exception ex)
        {
            Log($"WaitForActionExecutor exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Backs off from a tight Pump() spin to a real OS sleep as elapsed time grows, so a
    /// same-thread continuation that's already available resolves in microseconds while a
    /// genuinely outstanding background-thread continuation doesn't peg a CPU core for its
    /// whole wait.
    /// </summary>
    private static void SpinOrSleep(long elapsedMs)
    {
        if (elapsedMs < 2) Thread.SpinWait(50);
        else if (elapsedMs < 10) Thread.Yield();
        else Thread.Sleep(1);
    }

    /// <summary>
    /// Cheap snapshot of state that can still be in flux from a straggling background
    /// continuation (see the stabilization loop in WaitForActionExecutor above). Read between
    /// arbitrary decision points, not just combat, so every accessor here is defensive --
    /// something throwing (e.g. mid-transition) degrades to "unknown" rather than blowing up
    /// the calling action. Hashcode is used to speed up, low collision risk.
    /// </summary>
    private int? StabilityFingerprint()
    {
        try
        {
            if (_runState == null) return null;
            var player = _runState.Players[0];
            var pcs = player.PlayerCombatState;

            var hash = new HashCode();
            hash.Add(player.Creature?.CurrentHp);
            hash.Add(player.Gold);

            if (player.Deck?.Cards != null)
                foreach (var c in player.Deck.Cards)
                    if (c != null) hash.Add(c.Id);

            if (pcs != null)
            {
                hash.Add(pcs.Energy);
                if (pcs.Hand?.Cards != null)
                    foreach (var c in pcs.Hand.Cards) hash.Add(c.Id);
                hash.Add(pcs.DrawPile?.Cards?.Count);
                hash.Add(pcs.DiscardPile?.Cards?.Count);
                hash.Add(pcs.ExhaustPile?.Cards?.Count);

                var combatState = CombatManager.Instance.DebugOnlyGetState();
                if (combatState?.Enemies != null)
                    foreach (var e in combatState.Enemies)
                    {
                        if (e == null || !e.IsAlive) continue;
                        hash.Add(e.Monster?.Id);
                        hash.Add(e.CurrentHp);
                        hash.Add(e.Block);
                    }

                try
                {
                    var entries = CombatManager.Instance.History?.Entries;
                    if (entries != null) hash.Add(entries.Count());
                }
                catch { /* not always available, e.g. right as combat ends */ }
            }

            return hash.ToHashCode();
        }
        catch
        {
            return null;  // can't fingerprint right now -- treat as "unknown", not "changed"
        }
    }


    private static Dictionary<string, object?> StatsFromBaseVars(CardModel card)
    {
        var stats = new Dictionary<string, object?>();
        if (card?.DynamicVars?.Values != null)
        {
            foreach (var dv in card.DynamicVars.Values)
            {
                if (dv != null && !string.IsNullOrEmpty(dv.Name))
                    stats[dv.Name.ToLowerInvariant()] = (int)dv.BaseValue;
            }
        }
        return stats;
    }
    #endregion

    #region Json parsers
    private static List<string>? KeywordsToJson(CardModel card)
    {
        var kws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToList();
        return kws?.Count > 0 ? kws : null;
    }

    private Dictionary<string, object?> CardToJson(
        CardModel card,
        Dictionary<string, object?>? statsOverride = null,
        bool includeId = true,
        bool includeUpgraded = false,
        bool includeAfterUpgrade = true,
        bool includeEnchantmentAffliction = false)
    {
        var result = new Dictionary<string, object?>();
        if (includeId) result["id"] = card.Id.ToString();
        result["name"] = _loc.Card(card.Id.Entry);
        result["cost"] = card.EnergyCost?.GetResolved() ?? 0;
        result["type"] = card.Type.ToString();
        result["rarity"] = card.Rarity.ToString();
        if (includeUpgraded) result["upgraded"] = card.IsUpgraded;
        result["description"] = _loc.GetText("cards", card.Id.Entry + ".description");
        var stats = statsOverride ?? StatsFromBaseVars(card);
        result["stats"] = stats.Count > 0 ? stats : null;
        result["keywords"] = KeywordsToJson(card);
        if (includeAfterUpgrade) result["after_upgrade"] = GetUpgradedInfo(card);
        if (includeEnchantmentAffliction)
        {
            if (card.Enchantment != null)
            {
                result["enchantment"] = _loc.GetText("enchantments", card.Enchantment.Id.Entry + ".title");
                if (card.Enchantment.Amount != 0) result["enchantment_amount"] = card.Enchantment.Amount;
            }
            if (card.Affliction != null)
            {
                result["affliction"] = _loc.GetText("afflictions", card.Affliction.Id.Entry + ".title");
                if (card.Affliction.Amount != 0) result["affliction_amount"] = card.Affliction.Amount;
            }
        }
        return result;
    }

    private List<Dictionary<string, object?>>? PileCardsToJson(CardPile? pile)
    {
        if (pile?.Cards == null) return null;
        return pile.Cards.Select((c, i) =>
        {
            var cardInfo = CardToJson(c, includeUpgraded: true, includeEnchantmentAffliction: true);
            cardInfo["index"] = i;
            return cardInfo;
        }).ToList();
    }

    /// <summary>Compute what a card would look like after upgrading (stats + cost + description).</summary>
    private Dictionary<string, object?>? GetUpgradedInfo(CardModel card)
    {
        if (!card.IsUpgradable) return null;
        var canonical = ModelDb.GetById<CardModel>(card.Id);
        if (canonical == null) return null;

        var clone = canonical.ToMutable();
        // Apply existing upgrades first
        for (int i = 0; i < card.CurrentUpgradeLevel; i++)
        {
            clone.UpgradeInternal();
            clone.FinalizeUpgradeInternal();
        }
        // Apply one more upgrade
        clone.UpgradeInternal();
        clone.FinalizeUpgradeInternal();

        var stats = StatsFromBaseVars(clone);

        // Compare keywords before/after upgrade
        var oldKws = card.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
        var newKws = clone.Keywords?.Where(k => k != CardKeyword.None).Select(k => k.ToString()).ToHashSet() ?? new();
        var addedKws = newKws.Except(oldKws).ToList();
        var removedKws = oldKws.Except(newKws).ToList();

        return new Dictionary<string, object?>
        {
            ["cost"] = clone.EnergyCost?.GetResolved() ?? 0,
            ["stats"] = stats.Count > 0 ? stats : null,
            ["description"] = _loc.GetText("cards", card.Id.Entry + ".description"),
            ["added_keywords"] = addedKws.Count > 0 ? addedKws : null,
            ["removed_keywords"] = removedKws.Count > 0 ? removedKws : null,
        };
    }

    /// <summary>Per-creature counts of combat history events, both for the current turn and the
    /// whole combat so far.</summary>
    private static Dictionary<string, object?> HistoryCountsJson(Creature? creature, ICombatState? combatState)
    {
        if (creature == null) return new();
        var entries = CombatManager.Instance.History.Entries;

        Dictionary<string, object?> CountsOf<T>(Func<T, bool> matches) where T : CombatHistoryEntry
        {
            var list = entries.OfType<T>().Where(matches).ToList();
            return new Dictionary<string, object?>
            {
                ["this_turn"] = list.Count(e => e.HappenedThisTurn(combatState)),
                ["this_combat"] = list.Count,
            };
        }

        return new Dictionary<string, object?>
        {
            ["block_gained"] = CountsOf<BlockGainedEntry>(e => e.Actor == creature),
            ["card_afflicted"] = CountsOf<CardAfflictedEntry>(e => e.Actor == creature),
            ["card_discarded"] = CountsOf<CardDiscardedEntry>(e => e.Actor == creature),
            ["card_drawn"] = CountsOf<CardDrawnEntry>(e => e.Actor == creature),
            ["card_exhausted"] = CountsOf<CardExhaustedEntry>(e => e.Actor == creature),
            ["card_generated"] = CountsOf<CardGeneratedEntry>(e => e.Actor == creature),
            ["card_played"] = CountsOf<CardPlayFinishedEntry>(e => e.Actor == creature),
            ["creature_attacked"] = CountsOf<CreatureAttackedEntry>(e => e.Actor == creature),
            ["damage_received"] = CountsOf<DamageReceivedEntry>(e => e.Receiver == creature),
            ["damage_dealt"] = CountsOf<DamageReceivedEntry>(e => e.Dealer == creature),
            ["energy_spent"] = CountsOf<EnergySpentEntry>(e => e.Actor == creature),
            ["monster_move_performed"] = CountsOf<MonsterPerformedMoveEntry>(e => e.Actor == creature),
            ["orb_channeled"] = CountsOf<OrbChanneledEntry>(e => e.Actor == creature),
            ["potion_used"] = CountsOf<PotionUsedEntry>(e => e.Actor == creature),
            ["power_received"] = CountsOf<PowerReceivedEntry>(e => e.Actor == creature),
            ["power_applied"] = CountsOf<PowerReceivedEntry>(e => e.Applier == creature),
            ["stars_modified"] = CountsOf<StarsModifiedEntry>(e => e.Actor == creature),
            ["summoned"] = CountsOf<SummonedEntry>(e => e.Actor == creature),
        };
    }
    internal Dictionary<string, object?> PlayerSummaryJson(Player player)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = _loc.GetText("characters", (player.Character?.Id.Entry ?? "IRONCLAD") + ".title"),
            ["hp"] = player.Creature?.CurrentHp ?? 0,
            ["max_hp"] = player.Creature?.MaxHp ?? 0,
            ["block"] = player.Creature?.Block ?? 0,
            ["gold"] = player.Gold,
            ["max_potions"] = player.MaxPotionCount,
            ["relics"] = player.Relics?.Where(r => r != null).Select(r =>
            {
                var vars = new Dictionary<string, object?>();
                if (r?.DynamicVars?.Values != null)
                {
                    foreach (var dv in r.DynamicVars.Values)
                    {
                        if (dv != null && !string.IsNullOrEmpty(dv.Name))
                            vars[dv.Name] = (int)dv.BaseValue;
                    }
                }
                return new Dictionary<string, object?>
                {
                    ["id"] = r.Id.Entry,
                    ["name"] = _loc.Relic(r.Id.Entry),
                    ["description"] = _loc.GetText("relics", r.Id.Entry + ".description"),
                    ["vars"] = vars.Count > 0 ? vars : null, //what is this value?
                    ["counter"] = r.ShowCounter ? r.DisplayAmount : null,
                };
            }).ToList(),
            ["potions"] = player.Potions?.Select((p, i) =>
            {
                if (p == null) return null;
                var pvars = new Dictionary<string, object?>();
                if (p.DynamicVars?.Values != null)
                {
                    foreach (var dv in p.DynamicVars.Values)
                    {
                        if (dv != null && !string.IsNullOrEmpty(dv.Name))
                            pvars[dv.Name] = (int)dv.BaseValue;
                    }
                }
                return new Dictionary<string, object?>
                {
                    ["index"] = i,
                    ["id"] = p.Id.Entry,
                    ["name"] = _loc.Potion(p.Id.Entry),
                    ["description"] = _loc.GetText("potions", p.Id.Entry + ".description"),
                    ["vars"] = pvars.Count > 0 ? pvars : null,
                    ["target_type"] = p.TargetType.ToString(),
                };
            }).Where(x => x != null).ToList(),
            ["deck_size"] = player.Deck?.Cards?.Count(c => c != null) ?? 0,
            // Note: as of this change, deck cards carry "rarity" (via the shared CardToJson
            // helper), which they previously omitted -- a confirmed quirk, not a preserved one.
            ["deck"] = player.Deck?.Cards?.Where(c => c != null).Select(c =>
                CardToJson(c!, includeUpgraded: true, includeEnchantmentAffliction: true)
            ).ToList(),
        };
    }

    /// <summary>Common context added to every decision point.</summary>
    private Dictionary<string, object?> RunContextJson()
    {
        if (_runState == null) return new();
        var ctx = new Dictionary<string, object?>
        {
            ["act"] = _runState.CurrentActIndex + 1,
            ["act_name"] = _loc.Act(_runState.Act?.Id.Entry ?? "OVERGROWTH"),
            ["floor"] = _runState.ActFloor,
            ["room_type"] = _runState.CurrentRoom?.RoomType.ToString(),
        };

        // Boss encounter info — use BossEncounter?.Id?.Entry
        var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
        if (!string.IsNullOrEmpty(bossIdEntry))
        {
            var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
            // Handle special mappings
            if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
            ctx["boss"] = new Dictionary<string, object?>
            {
                ["id"] = bossIdEntry,
                ["name"] = _loc.Monster(monsterKey),
            };
        }

        return ctx;
    }
    // Change to GetFullMapJson later
    public Dictionary<string, object?> GetFullMap()
    {
        if (_runState?.Map == null)
            return Error("No map available");

        var map = _runState.Map;
        var rows = new List<List<Dictionary<string, object?>>>();
        var currentCoord = _runState.CurrentMapCoord;
        var visited = _runState.VisitedMapCoords;

        for (int row = 0; row < map.GetRowCount(); row++)
        {
            var rowNodes = new List<Dictionary<string, object?>>();
            foreach (var point in map.GetPointsInRow(row))
            {
                if (point == null) continue;
                var children = point.Children?.Select(ch => new Dictionary<string, object?>
                {
                    ["col"] = (int)ch.coord.col,
                    ["row"] = (int)ch.coord.row,
                }).ToList();

                var isVisited = visited?.Any(v => v.col == point.coord.col && v.row == point.coord.row) ?? false;
                var isCurrent = currentCoord.HasValue &&
                    currentCoord.Value.col == point.coord.col && currentCoord.Value.row == point.coord.row;

                rowNodes.Add(new Dictionary<string, object?>
                {
                    ["col"] = (int)point.coord.col,
                    ["row"] = (int)point.coord.row,
                    ["type"] = point.PointType.ToString(),
                    ["children"] = children,
                    ["visited"] = isVisited,
                    ["current"] = isCurrent,
                });
            }
            if (rowNodes.Count > 0)
                rows.Add(rowNodes);
        }

        // Boss node
        var bossNode = new Dictionary<string, object?>
        {
            ["col"] = (int)map.BossMapPoint.coord.col,
            ["row"] = (int)map.BossMapPoint.coord.row,
            ["type"] = map.BossMapPoint.PointType.ToString(),
        };

        // Add boss name/id — use BossEncounter?.Id?.Entry
        try
        {
            var bossIdEntry = _runState.Act?.BossEncounter?.Id?.Entry;
            if (!string.IsNullOrEmpty(bossIdEntry))
            {
                var monsterKey = bossIdEntry.EndsWith("_BOSS") ? bossIdEntry[..^5] : bossIdEntry;
                if (monsterKey == "THE_KIN") monsterKey = "KIN_PRIEST";
                bossNode["id"] = bossIdEntry;
                bossNode["name"] = _loc.Monster(monsterKey);
            }
        }
        catch { }

        return new Dictionary<string, object?>
        {
            ["type"] = "map",
            ["context"] = RunContextJson(),
            ["rows"] = rows,
            ["boss"] = bossNode,
            ["current_coord"] = currentCoord.HasValue ? new Dictionary<string, object?>
            {
                ["col"] = (int)currentCoord.Value.col,
                ["row"] = (int)currentCoord.Value.row,
            } : null,
        };
    }

    #endregion

    #region buffers readers
    private static readonly string[] HistoryCounterNames =
    {
        "block_gained", "card_afflicted", "card_discarded", "card_drawn", "card_exhausted",
        "card_generated", "card_played", "creature_attacked", "damage_received", "damage_dealt",
        "energy_spent", "monster_move_performed", "orb_channeled", "potion_used",
        "power_received", "power_applied", "stars_modified", "summoned",
    };
    // Incremental history-count cache -- HistoryCounts used to rescan the *entire* combat history
    // (entries.OfType<T>().Where(predicate)) for all 18 categories, for every creature, on every
    // single decision.
    private int _historyProcessedCount;
    private (int Round, int Side, int Turn) _historyTurnIdentity = (-1, -1, -1);
    private readonly Dictionary<(object Actor, string Category), int> _historyTotalCounts = new();
    private readonly Dictionary<(object Actor, string Category), int> _historyThisTurnCounts = new();

    private void RefreshHistoryCache(ICombatState? combatState)
    {
        var entries = (IReadOnlyList<CombatHistoryEntry>)CombatManager.Instance.History.Entries;
        int n = entries.Count;

        if (n < _historyProcessedCount)
        {
            _historyProcessedCount = 0;
            _historyTurnIdentity = (-1, -1, -1);
            _historyTotalCounts.Clear();
            _historyThisTurnCounts.Clear();
        }

        // "This turn" is finer than "this round" (CombatHistoryEntry.HappenedThisTurn also checks
        // CurrentSide and each player's TurnNumber), so any of those changing -- not just a round
        // bump -- invalidates every previously-cached this-turn count.
        var turnNumber = _runState != null && _runState.Players.Count > 0
            ? _runState.Players[0].PlayerCombatState?.TurnNumber ?? -1
            : -1;
        var identity = (combatState?.RoundNumber ?? -1, (int)(combatState?.CurrentSide ?? 0), turnNumber);
        if (!identity.Equals(_historyTurnIdentity))
        {
            _historyThisTurnCounts.Clear();
            _historyTurnIdentity = identity;
        }

        void Bump(object? actor, string category, bool thisTurn)
        {
            if (actor == null) return;
            var key = (actor, category);
            _historyTotalCounts[key] = _historyTotalCounts.GetValueOrDefault(key) + 1;
            if (thisTurn) _historyThisTurnCounts[key] = _historyThisTurnCounts.GetValueOrDefault(key) + 1;
        }

        for (int i = _historyProcessedCount; i < n; i++)
        {
            var entry = entries[i];
            bool thisTurn = entry.HappenedThisTurn(combatState);
            switch (entry)
            {
                case BlockGainedEntry e: Bump(e.Actor, "block_gained", thisTurn); break;
                case CardAfflictedEntry e: Bump(e.Actor, "card_afflicted", thisTurn); break;
                case CardDiscardedEntry e: Bump(e.Actor, "card_discarded", thisTurn); break;
                case CardDrawnEntry e: Bump(e.Actor, "card_drawn", thisTurn); break;
                case CardExhaustedEntry e: Bump(e.Actor, "card_exhausted", thisTurn); break;
                case CardGeneratedEntry e:
                    Bump(e.Actor, "card_generated", thisTurn);
                    Bump(e.Creator, "card_generated_by_creator", thisTurn);
                    break;
                case CardPlayFinishedEntry e: Bump(e.Actor, "card_played", thisTurn); break;
                case CreatureAttackedEntry e: Bump(e.Actor, "creature_attacked", thisTurn); break;
                case DamageReceivedEntry e:
                    Bump(e.Receiver, "damage_received", thisTurn);
                    Bump(e.Dealer, "damage_dealt", thisTurn);
                    break;
                case EnergySpentEntry e: Bump(e.Actor, "energy_spent", thisTurn); break;
                case MonsterPerformedMoveEntry e: Bump(e.Actor, "monster_move_performed", thisTurn); break;
                case OrbChanneledEntry e: Bump(e.Actor, "orb_channeled", thisTurn); break;
                case PotionUsedEntry e: Bump(e.Actor, "potion_used", thisTurn); break;
                case PowerReceivedEntry e:
                    Bump(e.Actor, "power_received", thisTurn);
                    Bump(e.Applier, "power_applied", thisTurn);
                    break;
                case StarsModifiedEntry e: Bump(e.Actor, "stars_modified", thisTurn); break;
                case SummonedEntry e: Bump(e.Actor, "summoned", thisTurn); break;
            }
        }
        _historyProcessedCount = n;
    }

    private void HistoryCounts(Creature? creature, ICombatState? combatState, float[] buffer, ref int offset)
    {
        RefreshHistoryCache(combatState);

        // A null creature (e.g. an enemy slot with no alive enemy in it) still writes its two
        // padding floats, matching every other empty-slot convention in this file (Card/Relic/
        // Potion/MapPoint) -- desyncing `offset` here would corrupt every field written after.
        if (creature == null)
        {
            offset += HistoryCounterNames.Length * 2;
            return;
        }

        foreach (var name in HistoryCounterNames)
        {
            var key = ((object)creature, name);
            buffer[offset++] = _historyThisTurnCounts.GetValueOrDefault(key);
            buffer[offset++] = _historyTotalCounts.GetValueOrDefault(key);
        }
    }

    /// <summary>Direct cache read for the "cards generated by this creator" counts in
    /// WriteCombatPlay -- keyed by CardGeneratedEntry.Creator (a Player), not Actor/Creature like
    /// everything else HistoryCounts tracks, so it can't reuse the per-creature loop above, but it
    /// reads from the same incremental cache instead of its own O(n) scan.</summary>
    private void WriteHistoryCount(object actor, string category, ICombatState? combatState, float[] buffer, ref int offset)
    {
        RefreshHistoryCache(combatState);
        var key = (actor, category);
        buffer[offset++] = _historyThisTurnCounts.GetValueOrDefault(key);
        buffer[offset++] = _historyTotalCounts.GetValueOrDefault(key);
    }

    private static void Potion(PotionModel? potion, float[] buffer, ref int offset)
    {
        buffer[offset++] = potion != null ? _tokenizer.Encode("potions", potion.Id.Entry) : 0;
        buffer[offset++] = potion?.TargetType switch
        {
            TargetType.Self => 0,
            TargetType.AnyAlly => 1,
            TargetType.AnyEnemy => 2,
            _ => -1
        };
    }

    private static void Relic(RelicModel? relic, float[] buffer, ref int offset)
    {
        buffer[offset++] = relic != null ? _tokenizer.Encode("relics", relic.Id.Entry) : 0;
        buffer[offset++] = relic is { ShowCounter: true } ? relic.DisplayAmount : -1;
    }

    private static void Card(CardModel? card, float[] buffer, ref int offset)
    {
        buffer[offset++] = card != null ? _tokenizer.Encode("cards", card.Id.Entry) : 0;
        buffer[offset++] = card?.EnergyCost?.GetResolved() ?? 0;
        buffer[offset++] = card != null ? (int)card.Type : 0;
        buffer[offset++] = card != null ? (int)card.Rarity : 0;
        buffer[offset++] = card is { IsUpgraded: true } ? 1 : 0;
        buffer[offset++] = card is { IsUpgradable: true } ? 1 : 0;
        buffer[offset++] = card?.Enchantment != null ? _tokenizer.Encode("enchantments", card.Enchantment.Id.Entry) : -1;
        buffer[offset++] = card?.Enchantment?.Amount ?? -1;
        buffer[offset++] = card?.Affliction != null ? _tokenizer.Encode("afflictions", card.Affliction.Id.Entry) : -1;
        buffer[offset++] = card?.Affliction?.Amount ?? -1;
        int kwMask = 0;
        if (card != null)
        {
            if (card.Keywords.Contains(CardKeyword.Exhaust))    kwMask |= 1 << 0;
            if (card.Keywords.Contains(CardKeyword.Ethereal))   kwMask |= 1 << 1;
            if (card.Keywords.Contains(CardKeyword.Innate))     kwMask |= 1 << 2;
            if (card.Keywords.Contains(CardKeyword.Unplayable)) kwMask |= 1 << 3;
            if (card.Keywords.Contains(CardKeyword.Retain))     kwMask |= 1 << 4;
            if (card.Keywords.Contains(CardKeyword.Sly))        kwMask |= 1 << 5;
            if (card.Keywords.Contains(CardKeyword.Eternal))    kwMask |= 1 << 6;
        }
        buffer[offset++] = kwMask;
    }

    private const int MaxRelics = 40;
    private const int MaxPotions = 10;
    private const int MaxDeckCard = 50;
    private const int MaxHandSize = 10;
    private static void PlayerSummary(Player player, float[] buffer, ref int offset)
    {
        if (player == null) return;
        buffer[offset++] = _tokenizer.Encode("characters", player.Character?.Id.Entry ?? "IRONCLAD");
        buffer[offset++] = player.Creature?.CurrentHp ?? 0;
        buffer[offset++] = player.Creature?.MaxHp ?? 0;
        buffer[offset++] = player.Creature?.Block ?? 0;
        buffer[offset++] = player.Gold;
        buffer[offset++] = player.MaxPotionCount;
        buffer[offset++] = player.Deck?.Cards?.Count(c => c != null) ?? 0;

        for (int i = 0; i < MaxRelics; i++)
            Relic(player.Relics != null && i < player.Relics.Count ? player.Relics[i] : null, buffer, ref offset);

        for (int i = 0; i < MaxPotions; i++)
            Potion(player.PotionSlots != null && i < player.PotionSlots.Count ? player.PotionSlots[i] : null, buffer, ref offset);

        for (int i = 0; i < MaxDeckCard; i++)
            Card(player.Deck?.Cards != null && i < player.Deck.Cards.Count ? player.Deck.Cards[i] : null, buffer, ref offset);
    }
    private void RunContext(float[] buffer, ref int offset)
    {
        if (_runState == null) return;
        buffer[offset++] = _runState.CurrentActIndex + 1;
        buffer[offset++] = _tokenizer.Encode("acts", _runState.Act?.Id.Entry ?? "OVERGROWTH");
        buffer[offset++] = _runState.ActFloor;
        buffer[offset++] = _tokenizer.Encode("encounters", _runState.Act?.BossEncounter?.Id?.Entry ?? "NONE");
        buffer[offset++] = _tokenizer.Encode("encounters", _runState.Act?.SecondBossEncounter?.Id?.Entry ?? "NONE");
    }

    private const int MaxMapCols = 7;
    private const int MaxMapRows = 20;
    private const int MaxMapChildren = 3;
    private const int MapPointFloats = 3 + MaxMapChildren * 2; // type, visited, current, then (col,row) per child
    private const int MapBufferSize = MaxMapRows * MaxMapCols * MapPointFloats + 2 * MapPointFloats; // Two boss nodes

    /// <summary>Writes one MapPoint's fixed-width slot: type, visited, current, then up to
    /// MaxMapChildren (col,row) child pairs (-1,-1) padded. A null point (empty grid cell) writes
    /// an all-empty slot: type=-1, both flags 0, all children (-1,-1).</summary>
    private static void MapPointToBuffer(MapPoint? point, bool isVisited, bool isCurrent, float[] buffer, ref int offset)
    {
        // MapPointType (MegaCrit.Sts2.Core.Map): 0=Unassigned 1=Unknown 2=Shop 3=Treasure
        // 4=RestSite 5=Monster 6=Elite 7=Boss 8=Ancient -- Ancient is also what StartingMapPoint
        // is tagged as (confirmed against GetFullMap's JSON output: the row-0 slot marked
        // visited+current came back as type 8, not a Monster/Unknown room type).
        buffer[offset++] = point != null ? (int)point.PointType : -1;
        buffer[offset++] = isVisited ? 1 : 0;
        buffer[offset++] = isCurrent ? 1 : 0;

        int written = 0;
        if (point?.Children != null)
        {
            foreach (var child in point.Children.OrderBy(c => c.coord.col))
            {
                if (written >= MaxMapChildren) break;
                buffer[offset++] = child.coord.col;
                buffer[offset++] = child.coord.row;
                written++;
            }
        }
        for (; written < MaxMapChildren; written++)
        {
            buffer[offset++] = -1;
            buffer[offset++] = -1;
        }
    }

    /// <summary>Buffer counterpart to GetFullMap</summary>
    private void Map(float[] buffer, ref int offset)
    {
        var map = _runState?.Map;
        var currentCoord = _runState?.CurrentMapCoord;
        var visited = _runState?.VisitedMapCoords;
        int rowCount = map?.GetRowCount() ?? 0;

        for (int row = 0; row < MaxMapRows; row++)
        {
            for (int col = 0; col < MaxMapCols; col++)
            {
                var point = row < rowCount ? map!.GetPoint(col, row) : null;
                bool isVisited = point != null && (visited?.Any(v => v.col == point.coord.col && v.row == point.coord.row) ?? false);
                bool isCurrent = point != null && currentCoord.HasValue &&
                    currentCoord.Value.col == point.coord.col && currentCoord.Value.row == point.coord.row;
                MapPointToBuffer(point, isVisited, isCurrent, buffer, ref offset);
            }
        }

        var boss = map?.BossMapPoint;
        bool bossVisited = boss != null && (visited?.Any(v => v.col == boss.coord.col && v.row == boss.coord.row) ?? false);
        bool bossCurrent = boss != null && currentCoord.HasValue &&
            currentCoord.Value.col == boss.coord.col && currentCoord.Value.row == boss.coord.row;
        MapPointToBuffer(boss, bossVisited, bossCurrent, buffer, ref offset);
    }

    #endregion
    #region patches
    private static void PatchCmdWait()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cmdwait");
            // Find Cmd.Wait(float) — it's in MegaCrit.Sts2.Core.Commands namespace
            // Find Cmd type via CardPileCmd's assembly (both are in same namespace)
            var cmdPileType = typeof(MegaCrit.Sts2.Core.Commands.CardPileCmd);
            var cmdAsm = cmdPileType.Assembly;
            Type? cmdType = cmdAsm.GetType("MegaCrit.Sts2.Core.Commands.Cmd");
            // If not found by exact name, search by namespace + "Wait" method
            if (cmdType == null)
            {
                foreach (var t in cmdAsm.GetTypes())
                {
                    if (t.Namespace == "MegaCrit.Sts2.Core.Commands")
                    {
                        var waitM = t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                            .Where(m => m.Name == "Wait").ToList();
                        if (waitM.Count > 0)
                        {
                            cmdType = t;
                            Console.Error.WriteLine($"[INFO] Found Wait() in {t.FullName}");
                            break;
                        }
                    }
                }
            }
            if (cmdType != null)
            {
                var waitMethod = cmdType.GetMethod("Wait",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new[] { typeof(float) }, null);
                if (waitMethod != null)
                {
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (prefix != null)
                    {
                        harmony.Patch(waitMethod, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Cmd.Wait() to no-op (prevents boss fight deadlocks)");
                    }
                }
                else
                {
                    // Try to find any Wait method
                    var methods = cmdType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                        .Where(m => m.Name == "Wait").ToList();
                    foreach (var m in methods)
                    {
                        Console.Error.WriteLine($"[INFO] Found Cmd.Wait({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})");
                        var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.CmdWaitPrefix),
                            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                        if (prefix != null)
                        {
                            harmony.Patch(m, new HarmonyMethod(prefix));
                            Console.Error.WriteLine($"[INFO] Patched Cmd.Wait variant");
                        }
                    }
                }
            }
            else
            {
                Console.Error.WriteLine("[WARN] Could not find MegaCrit.Sts2.Core.Commands.Cmd type");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Cmd.Wait: {ex.Message}");
        }
    }

    private static void PatchTalkCmd()
    {
        try
        {
            var harmony = new Harmony("sts2headless.talkpatch");
            var talkType = typeof(CombatManager).Assembly.GetType("MegaCrit.Sts2.Core.Commands.TalkCmd");
            var playMethod = talkType?.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "Play");
            if (playMethod == null)
            {
                Console.Error.WriteLine("[WARN] Could not find TalkCmd.Play to patch");
                return;
            }
            var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.TalkCmdPlayPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (prefix != null)
            {
                harmony.Patch(playMethod, new HarmonyMethod(prefix));
                Console.Error.WriteLine("[INFO] Patched TalkCmd.Play() to no-op (prevents enemy-move VFX crash, issue #64)");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch TalkCmd.Play: {ex.Message}");
        }
    }

    /// <summary>
    /// Patches every CardSelectCmd.FromXxx overload that takes a CardSelectorPrefs and/or an
    /// AbstractModel `source` -- covers card-, relic-, power-, and potion-triggered selects
    /// (FromHand/FromCombatPile/FromDeck*), plus the rest-site/card-upgrade path
    /// (FromHandForUpgrade, source only, no prefs in the headless branch). Not patched:
    /// FromChooseACardScreen (obscure mod-support path, not exercised by any known card/relic)
    /// and FromChooseABundleScreen (bundle_select is unambiguous already -- Scroll Boxes only).
    /// </summary>
    private static void PatchCardSelectContext()
    {
        try
        {
            var harmony = new Harmony("sts2headless.cardselectcontext");
            var type = typeof(CardSelectCmd);
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;

            var prefsAndSourcePrefix = new HarmonyMethod(
                typeof(CardSelectContextPatches).GetMethod(nameof(CardSelectContextPatches.PrefsAndSourcePrefix), flags));
            var prefsOnlyPrefix = new HarmonyMethod(
                typeof(CardSelectContextPatches).GetMethod(nameof(CardSelectContextPatches.PrefsOnlyPrefix), flags));
            var sourceOnlyPrefix = new HarmonyMethod(
                typeof(CardSelectContextPatches).GetMethod(nameof(CardSelectContextPatches.SourceOnlyPrefix), flags));

            int patched = 0;
            void Patch(string methodName, HarmonyMethod prefix, params Type[] paramTypes)
            {
                var method = type.GetMethod(methodName, flags, binder: null, types: paramTypes, modifiers: null);
                if (method == null)
                {
                    Console.Error.WriteLine($"[WARN] CardSelectCmd.{methodName}({string.Join(", ", paramTypes.Select(t => t.Name))}) not found -- skipping cause-capture patch");
                    return;
                }
                harmony.Patch(method, prefix);
                patched++;
            }

            // FromHand carries both prefs and source; FromHandForDiscard delegates straight
            // into FromHand with the same args, so patching FromHand covers it too.
            Patch(nameof(CardSelectCmd.FromHand), prefsAndSourcePrefix,
                typeof(PlayerChoiceContext), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(AbstractModel));

            // FromHandForUpgrade's headless branch calls Selector.GetSelectedCards directly
            // with no CardSelectorPrefs built at all -- source is the only signal available.
            Patch(nameof(CardSelectCmd.FromHandForUpgrade), sourceOnlyPrefix,
                typeof(PlayerChoiceContext), typeof(Player), typeof(AbstractModel));

            Patch(nameof(CardSelectCmd.FromSimpleGridForRewards), prefsOnlyPrefix,
                typeof(PlayerChoiceContext), typeof(List<CardCreationResult>), typeof(Player), typeof(CardSelectorPrefs));
            Patch(nameof(CardSelectCmd.FromSimpleGrid), prefsOnlyPrefix,
                typeof(PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(CardSelectorPrefs));
            Patch(nameof(CardSelectCmd.FromCombatPile), prefsOnlyPrefix,
                typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs));
            Patch(nameof(CardSelectCmd.FromCombatPile), prefsOnlyPrefix,
                typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>));
            Patch(nameof(CardSelectCmd.FromDeckForUpgrade), prefsOnlyPrefix,
                typeof(Player), typeof(CardSelectorPrefs));
            Patch(nameof(CardSelectCmd.FromDeckForTransformation), prefsOnlyPrefix,
                typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, CardTransformation>));
            Patch(nameof(CardSelectCmd.FromDeckForEnchantment), prefsOnlyPrefix,
                typeof(Player), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs));
            Patch(nameof(CardSelectCmd.FromDeckForEnchantment), prefsOnlyPrefix,
                typeof(Player), typeof(EnchantmentModel), typeof(int), typeof(Func<CardModel, bool>), typeof(CardSelectorPrefs));
            Patch(nameof(CardSelectCmd.FromDeckForEnchantment), prefsOnlyPrefix,
                typeof(IReadOnlyList<CardModel>), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs));
            // FromDeckForRemoval delegates straight into FromDeckGeneric with the same prefs --
            // patching FromDeckGeneric covers it too.
            Patch(nameof(CardSelectCmd.FromDeckGeneric), prefsOnlyPrefix,
                typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(Func<CardModel, int>));

            Console.Error.WriteLine($"[INFO] Patched {patched} CardSelectCmd overload(s) for card_select cause-capture");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch CardSelectCmd for cause-capture: {ex.Message}");
        }
    }

    /// <summary>
    /// Harmony prefixes for PatchCardSelectContext -- parameter names must match CardSelectCmd's
    /// exactly (`prefs`, `source`), since Harmony injects prefix args by name, not position.
    /// </summary>
    internal static class CardSelectContextPatches
    {
        public static void PrefsAndSourcePrefix(CardSelectorPrefs prefs, AbstractModel source)
        {
            HeadlessCardSelector.PendingCausePromptTable = prefs.Prompt.LocTable;
            HeadlessCardSelector.PendingCausePromptKey = prefs.Prompt.LocEntryKey;
            HeadlessCardSelector.PendingCauseSource = source;
        }

        public static void PrefsOnlyPrefix(CardSelectorPrefs prefs)
        {
            HeadlessCardSelector.PendingCausePromptTable = prefs.Prompt.LocTable;
            HeadlessCardSelector.PendingCausePromptKey = prefs.Prompt.LocEntryKey;
        }

        public static void SourceOnlyPrefix(AbstractModel source)
        {
            HeadlessCardSelector.PendingCauseSource = source;
        }
    }

    /// <summary>
    /// .Player was called by almost everything repeatedly to read the information about the player.
    /// This created a blocking wall in fast simulations. A cache was added to CombatState to store the player information and avoid recomputing it every time. 
    /// This patch redirects the calls to .Player to use the cache instead of recomputing it.
    /// </summary>
    private static void PatchCombatRosterCache()
    {
        try
        {
            var harmony = new Harmony("sts2headless.combatrostercache");

            var playerCreaturesGetter = typeof(CombatState).GetProperty(nameof(CombatState.PlayerCreatures))?.GetGetMethod();
            var playersGetter = typeof(CombatState).GetProperty(nameof(CombatState.Players))?.GetGetMethod();
            var playerCreaturesPrefix = typeof(CombatRosterCachePatches).GetMethod(nameof(CombatRosterCachePatches.PlayerCreaturesPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var playersPrefix = typeof(CombatRosterCachePatches).GetMethod(nameof(CombatRosterCachePatches.PlayersPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

            if (playerCreaturesGetter != null && playerCreaturesPrefix != null)
                harmony.Patch(playerCreaturesGetter, new HarmonyMethod(playerCreaturesPrefix));
            if (playersGetter != null && playersPrefix != null)
                harmony.Patch(playersGetter, new HarmonyMethod(playersPrefix));

            if (playerCreaturesGetter != null && playersGetter != null)
                Console.Error.WriteLine("[INFO] Patched CombatState.PlayerCreatures/Players to cache instead of recomputing via LINQ every access");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch CombatState roster cache: {ex.Message}");
        }
    }

    private static void PatchTaskYield()
    {
        try
        {
            var harmony = new Harmony("sts2headless.yieldpatch");

            // Patch YieldAwaitable.YieldAwaiter.IsCompleted to return true
            // This makes `await Task.Yield()` execute synchronously (continuation runs inline)
            var yieldAwaiterType = typeof(System.Runtime.CompilerServices.YieldAwaitable)
                .GetNestedType("YieldAwaiter");
            if (yieldAwaiterType != null)
            {
                var isCompletedProp = yieldAwaiterType.GetProperty("IsCompleted");
                if (isCompletedProp != null)
                {
                    var getter = isCompletedProp.GetGetMethod();
                    var prefix = typeof(YieldPatches).GetMethod(nameof(YieldPatches.IsCompletedPrefix),
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    if (getter != null && prefix != null)
                    {
                        harmony.Patch(getter, new HarmonyMethod(prefix));
                        Console.Error.WriteLine("[INFO] Patched Task.Yield() to be synchronous");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Failed to patch Task.Yield: {ex.Message}");
        }
    }

    /// <summary>
    /// Card selector for headless mode — picks first available card for any selection prompt.
    /// Used by cards like Headbutt, Armaments, etc. that need player to choose a card.
    /// </summary>
    /// <summary>
    /// Card selector that creates a pending selection decision point.
    /// When the game needs the player to choose cards (upgrade, remove, transform, bundle pick),
    /// this stores the options and waits for the main loop to provide the answer.
    /// </summary>
    internal class HeadlessCardSelector : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        // Pending card selection — set by game engine, read by main loop
        public List<CardModel>? PendingOptions { get; private set; }
        public int PendingMinSelect { get; private set; }
        public int PendingMaxSelect { get; private set; }
        public string PendingPrompt { get; private set; } = "";
        private TaskCompletionSource<IEnumerable<CardModel>>? _pendingTcs;

        // "Cause" of the pending selection -- ICardSelector.GetSelectedCards(options, min, max)
        // carries no signal on its own for *why* the game is asking (True Grit's exhaust-select
        // and Wish's deck-tutor-select hit this exact same call shape). CardSelectCmd.FromXxx
        // knows -- it takes a CardSelectorPrefs (prompt) and often an AbstractModel `source` --
        // but drops both before calling into ICardSelector. CardSelectContextPatches (below)
        // captures them via Harmony prefixes on CardSelectCmd itself, into these two static
        // fields, immediately before the call chain reaches GetSelectedCards.
        internal static AbstractModel? PendingCauseSource;
        internal static string? PendingCausePromptTable;
        internal static string? PendingCausePromptKey;

        public string? PendingSourceCategory { get; private set; }
        public string? PendingSourceId { get; private set; }
        public string? PendingPromptTable { get; private set; }
        public string? PendingPromptKey { get; private set; }

        public bool HasPending => _pendingTcs != null && !_pendingTcs.Task.IsCompleted;

        public Task<IEnumerable<CardModel>> GetSelectedCards(
            IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            // Consume the ambient cause immediately, win or lose (auto-select paths below
            // still clear it) -- otherwise a later GetSelectedCards call that goes through an
            // overload without a `source`/`prefs` param could pick up stale context from a
            // previous, unrelated selection.
            var causeSource = PendingCauseSource;
            var causePromptTable = PendingCausePromptTable;
            var causePromptKey = PendingCausePromptKey;
            PendingCauseSource = null;
            PendingCausePromptTable = null;
            PendingCausePromptKey = null;

            var optList = options.ToList();
            if (optList.Count == 0)
                return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());

            // If only one option and minSelect requires it, auto-select
            if (optList.Count == 1 && minSelect >= 1)
                return Task.FromResult<IEnumerable<CardModel>>(optList);

            // Store pending selection and wait
            PendingOptions = optList;
            PendingMinSelect = minSelect;
            PendingMaxSelect = maxSelect;
            PendingSourceCategory = causeSource?.Id.Category;
            PendingSourceId = causeSource?.Id.Entry;
            PendingPromptTable = causePromptTable;
            PendingPromptKey = causePromptKey;
            _pendingTcs = new TaskCompletionSource<IEnumerable<CardModel>>();

            Console.Error.WriteLine($"[SIM] Card selection pending: {optList.Count} options, select {minSelect}-{maxSelect}"
                + (causeSource != null ? $", source={causeSource.Id.Category}:{causeSource.Id.Entry}" : "")
                + (causePromptKey != null ? $", prompt={causePromptTable}.{causePromptKey}" : ""));

            // Return the task — the main loop will complete it
            return _pendingTcs.Task;
        }

        public void ResolvePending(IEnumerable<CardModel> selected)
        {
            _pendingTcs?.TrySetResult(selected);
            PendingOptions = null;
            PendingSourceCategory = null;
            PendingSourceId = null;
            PendingPromptTable = null;
            PendingPromptKey = null;
            _pendingTcs = null;
        }

        public void ResolvePendingByIndices(int[] indices)
        {
            if (PendingOptions == null) return;
            var selected = indices
                .Where(i => i >= 0 && i < PendingOptions.Count)
                .Select(i => PendingOptions[i])
                .ToList();
            ResolvePending(selected);
        }

        public void CancelPending()
        {
            _pendingTcs?.TrySetResult(Array.Empty<CardModel>());
            PendingOptions = null;
            PendingSourceCategory = null;
            PendingSourceId = null;
            PendingPromptTable = null;
            PendingPromptKey = null;
            _pendingTcs = null;
        }

        // Pending card reward from events (GetSelectedCardReward blocks until resolved)
        public List<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult>? PendingRewardCards { get; private set; }
        private ManualResetEventSlim? _rewardWait;
        private int _rewardChoice = -1;

        // NOTE: STS2 build 23372702 changed ICardSelector.GetSelectedCardReward to return
        // a CardRewardSelection struct { CardModel card; CardRewardAlternative alternative }.
        // CardReward.OnSelect interprets: alternative != null → pick that alternative
        // (Skip/Reroll); else card != null → take that card; both null → skip (no card kept).
        public MegaCrit.Sts2.Core.TestSupport.CardRewardSelection GetSelectedCardReward(
            IReadOnlyList<MegaCrit.Sts2.Core.Entities.Cards.CardCreationResult> options,
            IReadOnlyList<CardRewardAlternative> alternatives)
        {
            if (options.Count == 0) return default;  // Skip

            // Store pending and block until main loop resolves
            PendingRewardCards = options.ToList();
            _rewardChoice = -1;
            _rewardWait = new ManualResetEventSlim(false);

            Console.Error.WriteLine($"[SIM] Card reward pending: {options.Count} cards (blocking)");
            _rewardWait.Wait(TimeSpan.FromSeconds(300)); // Wait up to 5 min

            var choice = _rewardChoice;
            PendingRewardCards = null;
            _rewardWait = null;

            if (choice >= 0 && choice < options.Count)
                return new MegaCrit.Sts2.Core.TestSupport.CardRewardSelection { card = options[choice].Card };
            return default;  // Skip (card=null, alternative=null)
        }

        public bool HasPendingReward => PendingRewardCards != null && _rewardWait != null;

        public void ResolveReward(int index)
        {
            _rewardChoice = index;
            _rewardWait?.Set();
        }

        public void SkipReward()
        {
            _rewardChoice = -1;
            _rewardWait?.Set();
        }
    }

    /// <summary>
    /// CombatState.PlayerCreatures/Players are plain properties, not cached fields:
    ///   Creatures       => _allies.Concat(_enemies).ToList()
    ///   PlayerCreatures => Creatures.Where(c => c.IsPlayer).ToList()
    ///   Players         => PlayerCreatures.Select(c => c.Player).ToList()
    /// so a single `.Players` read is three chained LINQ allocations, every single time it's
    /// read. dotnet-trace showed this at ~40% of wall time in a combat-heavy benchmark -- the
    /// game's own PlayCardAction/AttackCommand/hook-listener pipeline reads .Players repeatedly
    /// per card resolution (once per hook dispatch), not anything this headless layer calls
    /// directly.
    ///
    /// Caches the two expensive properties (not Creatures itself -- see below) per CombatState
    /// instance, invalidated by CombatState.CreaturesChanged, which AddCreature/RemoveCreature
    /// are the only two places in CombatState.cs that fire (confirmed by reading the decompiled
    /// source) -- the only two places _allies/_enemies membership can change. A creature dying
    /// doesn't remove it from the roster (only "escaping" does, via RemoveCreature), so the cache
    /// stays valid across the vast majority of combat -- damage, block, powers, turns.
    ///
    /// Rebuilds from the original (unpatched) Creatures getter on a cache miss rather than reading
    /// Allies directly, so this can't silently diverge from the original semantics even if that
    /// invariant (player creatures are always on the Allies side) ever changes. Creatures itself
    /// is deliberately left unpatched: CombatState.cs also has an `_enemies.Sort(...)` call that
    /// reorders without firing CreaturesChanged, which would make a Creatures-level cache return
    /// stale ordering -- PlayerCreatures/Players are never called during that reorder, so they
    /// aren't exposed to it.
    /// </summary>
    internal static class CombatRosterCachePatches
    {
        private class RosterCache
        {
            public List<Creature>? PlayerCreatures;
            public List<Player>? Players;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CombatState, RosterCache> _caches = new();

        private static RosterCache GetOrCreateCache(CombatState state)
        {
            if (_caches.TryGetValue(state, out var existing)) return existing;
            var cache = new RosterCache();
            _caches.Add(state, cache);
            state.CreaturesChanged += _ =>
            {
                if (_caches.TryGetValue(state, out var c))
                {
                    c.PlayerCreatures = null;
                    c.Players = null;
                }
            };
            return cache;
        }

        public static bool PlayerCreaturesPrefix(CombatState __instance, ref IReadOnlyList<Creature> __result)
        {
            var cache = GetOrCreateCache(__instance);
            cache.PlayerCreatures ??= __instance.Creatures.Where(c => c.IsPlayer).ToList();
            __result = cache.PlayerCreatures;
            return false;
        }

        public static bool PlayersPrefix(CombatState __instance, ref IReadOnlyList<Player> __result)
        {
            var cache = GetOrCreateCache(__instance);
            cache.PlayerCreatures ??= __instance.Creatures.Where(c => c.IsPlayer).ToList();
            cache.Players ??= cache.PlayerCreatures.Select(c => c.Player).ToList();
            __result = cache.Players;
            return false;
        }
    }

    internal static class YieldPatches
    {
        // Only suppress Task.Yield() when this flag is set (during end_turn processing)
        public static volatile bool SuppressYield;

        public static bool IsCompletedPrefix(ref bool __result)
        {
            if (SuppressYield)
            {
                __result = true;
                return false;
            }
            return true; // Let normal Yield behavior run
        }

        /// <summary>Harmony prefix: make Cmd.Wait() return completed task immediately (no-op in headless).</summary>
        public static bool CmdWaitPrefix(ref Task __result)
        {
            __result = Task.CompletedTask;
            return false; // Skip original method
        }

        /// <summary>
        /// Harmony prefix: no-op TalkCmd.Play (issue #64). The speech-bubble VFX
        /// (NSpeechBubbleVfx.Create + GetVfxContainer().AddChildSafely) NREs in headless,
        /// which derails enemy moves like BygoneEffigy.WakeMove mid enemy turn and forces
        /// the EndTurn nuclear fallback / false game_over. The bubble is purely cosmetic and
        /// its return value is ignored by callers, so returning null is safe.
        /// </summary>
        public static bool TalkCmdPlayPrefix(ref MegaCrit.Sts2.Core.Nodes.Vfx.NSpeechBubbleVfx? __result)
        {
            __result = null;
            return false; // Skip original method
        }
    }

    private static void InitLocManager()
    {
        // Create a LocManager instance with stub tables via reflection.
        // LocManager.Initialize() fails because PlatformUtil isn't available,
        // and Harmony can't patch some LocString methods due to JIT issues.
        // Solution: create an uninitialized LocManager, set its _tables, and
        // use Harmony only for the simple LocTable.GetRawText fallback.
        try
        {
            // Create uninitialized LocManager and set Instance
            var instanceProp = typeof(LocManager).GetProperty("Instance",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LocManager));
            instanceProp!.SetValue(null, instance);

            // Load REAL localization data from localization_eng/ JSON files
            var tablesField = typeof(LocManager).GetField("_tables",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var tables = new Dictionary<string, LocTable>(StringComparer.OrdinalIgnoreCase);

            var locDir = RepoPaths.FindContaining("localization_eng");
            if (Directory.Exists(locDir))
            {
                foreach (var file in Directory.GetFiles(locDir, "*.json"))
                {
                    try
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                            File.ReadAllText(file));
                        if (data != null)
                            tables[name] = new LocTable(name, data);
                    }
                    catch { }
                }
                Console.Error.WriteLine($"[INFO] Loaded {tables.Count} localization tables from {locDir}");
            }
            else
            {
                Console.Error.WriteLine($"[WARN] Localization dir not found: {locDir}");
                // Fallback: empty tables
                var tableNames = new[] {
                    "achievements","acts","afflictions","ancients","ascension",
                    "bestiary","card_keywords","card_library","card_reward_ui",
                    "card_selection","cards","characters","combat_messages",
                    "credits","enchantments","encounters","epochs","eras",
                    "events","ftues","game_over_screen","gameplay_ui",
                    "inspect_relic_screen","intents","main_menu_ui","map",
                    "merchant_room","modifiers","monsters","orbs","potion_lab",
                    "potions","powers","relic_collection","relics","rest_site_ui",
                    "run_history","settings_ui","static_hover_tips","stats_screen",
                    "timeline","vfx"
                };
                foreach (var name in tableNames)
                    tables[name] = new LocTable(name, new Dictionary<string, string>());
            }
            tablesField!.SetValue(instance, tables);

            // Set Language
            var langProp = typeof(LocManager).GetProperty("Language",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { langProp?.SetValue(instance, "eng"); } catch { }

            // Set CultureInfo
            var cultureProp = typeof(LocManager).GetProperty("CultureInfo",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { cultureProp?.SetValue(instance, System.Globalization.CultureInfo.InvariantCulture); } catch { }

            // Initialize _smartFormatter — the game uses `new SmartFormatter()`
            try
            {
                var sfField = typeof(LocManager).GetField("_smartFormatter",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (sfField != null)
                {
                    try
                    {
                        var ctors = sfField.FieldType.GetConstructors(
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                        var bestCtor = ctors.OrderBy(c => c.GetParameters().Length).First();
                        var args2 = bestCtor.GetParameters().Select(p =>
                            p.HasDefaultValue ? p.DefaultValue :
                            p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null
                        ).ToArray();
                        var sf = bestCtor.Invoke(args2);
                        sfField.SetValue(null, sf);

                        // Register extensions using the game's own LoadLocFormatters logic
                        try
                        {
                            var loadMethod = typeof(LocManager).GetMethod("LoadLocFormatters",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                            loadMethod?.Invoke(instance, null);
                            Console.Error.WriteLine("[INFO] SmartFormatter initialized via LoadLocFormatters");
                        }
                        catch (Exception lfEx)
                        {
                            Console.Error.WriteLine($"[WARN] LoadLocFormatters failed: {lfEx.InnerException?.Message ?? lfEx.Message}");
                        }
                    }
                    catch (Exception sfEx)
                    {
                        Console.Error.WriteLine($"[WARN] SmartFormatter create failed: {sfEx.GetType().Name}: {sfEx.Message}");
                        if (sfEx.InnerException != null)
                            Console.Error.WriteLine($"  Inner: {sfEx.InnerException.GetType().Name}: {sfEx.InnerException.Message}");
                    }
                }
                else
                {
                    Console.Error.WriteLine("[WARN] _smartFormatter field not found in LocManager");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine($"[WARN] _smartFormatter init: {ex.GetType().Name}: {ex.Message}\n{ex.InnerException?.Message}"); }

            // Initialize _engTables to point to _tables (avoid null ref in fallback)
            try
            {
                var engTablesField = typeof(LocManager).GetField("_engTables",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                engTablesField?.SetValue(instance, tables);
            }
            catch { }

            Console.Error.WriteLine("[INFO] LocManager initialized with stub tables");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] InitLocManager failed: {ex.Message}");
        }
    }

    private static void PatchBundleScreen()
    {
        try
        {
            var harmony = new Harmony("sts2headless.bundlepatch");
            var bundleMethod = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod("FromChooseABundleScreen",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            var bundlePrefix = typeof(BundlePatches).GetMethod(nameof(BundlePatches.BundleScreenPrefix),
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            if (bundleMethod != null && bundlePrefix != null)
            {
                harmony.Patch(bundleMethod, new HarmonyMethod(bundlePrefix));
                Console.Error.WriteLine("[INFO] Patched FromChooseABundleScreen");
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] Bundle patch: {ex.Message}"); }
    }

    internal static class BundlePatches
    {
        /// <summary>
        /// Intercept bundle selection — store bundles and wait for player to pick a pack index.
        /// </summary>
        public static bool BundleScreenPrefix(
            MegaCrit.Sts2.Core.Entities.Players.Player player,
            IReadOnlyList<IReadOnlyList<CardModel>> bundles,
            ref Task<IEnumerable<CardModel>> __result)
        {
            if (bundles.Count == 0)
            {
                __result = Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
                return false;
            }

            // Store pending bundles for the main loop to present
            var sim = _bundleSimRef;
            if (sim != null)
            {
                sim._pendingBundles = bundles;
                sim._pendingBundleTcs = new TaskCompletionSource<IEnumerable<CardModel>>();
                Console.Error.WriteLine($"[SIM] Bundle selection pending: {bundles.Count} packs");

                __result = sim._pendingBundleTcs.Task;
                return false;
            }

            __result = Task.FromResult<IEnumerable<CardModel>>(bundles[0]);
            return false;
        }

        // Static reference so Harmony patch can access the simulator instance
        internal static RunSimulator? _bundleSimRef;
    }

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[SIM] {message}");
    }

    private static Dictionary<string, object?> Error(string message) =>
        new() { ["type"] = "error", ["message"] = message };

    private static Dictionary<string, object?> ErrorWithTrace(string context, Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return new Dictionary<string, object?>
        {
            ["type"] = "error",
            ["message"] = $"{context}: {inner.GetType().Name}: {inner.Message}",
            ["stack_trace"] = inner.StackTrace,
        };
    }

    private static void EnsureModelDbInitialized()
    {
        if (_modelDbInitialized) return;
        _modelDbInitialized = true;

        TestMode.IsOn = true;

        // Initialize ModManager state for headless/test mode
        try
        {
            var stateProp = typeof(MegaCrit.Sts2.Core.Modding.ModManager).GetProperty("State",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            stateProp?.SetValue(null, MegaCrit.Sts2.Core.Modding.ModManagerState.Skipped);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModManager init: {ex.Message}");
        }

        // Install inline sync context on main thread
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        // Initialize PlatformServices before anything touches PlatformUtil
        try
        {
            // Try to access PlatformUtil to trigger its static init
            // If it fails, it won't be available but most code checks SteamInitializer.Initialized
            var _ = MegaCrit.Sts2.Core.Platform.PlatformUtil.PrimaryPlatform;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] PlatformUtil init: {ex.Message}");
        }

        // Initialize SaveManager with a dummy profile for save/load support
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitProfileId: {ex.Message}"); }

        // Initialize PrefsSave (FastMode etc.). build 23372702 reads PrefsSave.FastMode
        // from many gameplay paths (e.g. Slice.OnPlay anim delay); without this the
        // PrefsSave getter returns null and those paths NRE.
        try { SaveManager.Instance.InitPrefsDataForTest(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] SaveManager.InitPrefsDataForTest: {ex.Message}"); }

        // Initialize progress data for epoch/timeline tracking
        try { SaveManager.Instance.InitProgressData(); }
        catch (Exception ex) { Console.Error.WriteLine($"[WARN] InitProgressData: {ex.Message}"); }

        // Install the Task.Yield patch but keep SuppressYield=false by default.
        // SuppressYield is toggled to true only during EndTurn to prevent boss fight deadlocks.
        PatchTaskYield();

        // Patch Cmd.Wait to be a no-op in headless mode.
        // Cmd.Wait(duration) is used for UI animations (e.g., PreviewCardPileAdd during
        // Vantom's Dismember move adding Wounds). In headless mode, these never complete
        // because there's no Godot scene tree, causing the ActionExecutor to deadlock.
        PatchCmdWait();

        // Patch TalkCmd.Play to a no-op (issue #64). Monster speech-bubble VFX during
        // moves (e.g. BygoneEffigy.WakeMove) NRE in headless and break the enemy turn.
        PatchTalkCmd();

        // Patch CardSelectCmd.FromXxx to capture the prefs/source it drops before calling
        // ICardSelector.GetSelectedCards, so HeadlessCardSelector can report *why* a
        // card_select fired (e.g. True Grit's exhaust-select vs Wish's deck-tutor-select).
        PatchCardSelectContext();

        // Initialize localization system (needed for events, cards, etc.)
        InitLocManager();

        // Patch FromChooseABundleScreen to use our card selector
        PatchBundleScreen();

        // Cache CombatState.PlayerCreatures/Players -- both recompute via LINQ (Where/Select +
        // ToList) on every single access with no memoization. Profiling (dotnet-trace) showed
        // this at ~40% of wall time in a combat-heavy benchmark: the game's own card-play/hook
        // pipeline hits .Players repeatedly per card resolution. See CombatRosterCachePatches.
        PatchCombatRosterCache();

        var subtypes = MegaCrit.Sts2.Core.Models.AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        for (int i = 0; i < subtypes.Count; i++)
        {
            try
            {
                ModelDb.Inject(subtypes[i]);
                registered++;
            }
            catch (Exception ex)
            {
                failed++;
                // Only log first few failures to reduce noise
                if (failed <= 5)
                    Console.Error.WriteLine($"[WARN] Failed to register {subtypes[i].Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"[INFO] ModelDb: {registered} registered, {failed} failed out of {subtypes.Count}");

        // Ensure UnlockState.all has all epoch IDs unlocked so Neow options generate properly
        try
        {
            var unlockedEpochsField = typeof(MegaCrit.Sts2.Core.Unlocks.UnlockState).GetField("_unlockedEpochIds",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var epochsHashSet = unlockedEpochsField?.GetValue(MegaCrit.Sts2.Core.Unlocks.UnlockState.all) as HashSet<string>;
            if (epochsHashSet != null)
            {
                foreach (var epochId in MegaCrit.Sts2.Core.Timeline.EpochModel.AllEpochIds)
                {
                    epochsHashSet.Add(epochId);
                }
            }
        }
        catch { }

        // Initialize net ID serialization cache (needed for combat actions)
        try
        {
            MegaCrit.Sts2.Core.Modding.AssemblyInfo.Init();
            ModelIdSerializationCache.Init();
            Console.Error.WriteLine("[INFO] ModelIdSerializationCache initialized");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] ModelIdSerializationCache.Init: {ex.Message}");
        }
    }



    public void CleanUp()
    {
        try
        {
            if (RunManager.Instance.IsInProgress)
                RunManager.Instance.CleanUp(graceful: true);
            _runState = null;
        }
        catch (Exception ex)
        {
            Log($"CleanUp exception: {ex.Message}");
        }
    }

    #endregion
}
