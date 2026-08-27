// RunSimulator partial — BufferStateWriter: the high-speed, allocation-free decision writer.
//
// Same reads as JsonStateWriter (RunSimulator.Writers.Json.cs) and the same dispatch/side effects
// (RunSimulator.States.cs) -- this class only differs in where the result goes: a flat, fixed-width
// float[] instead of a Dictionary<string,object?>/JSON string. That's the whole point of this file:
// no dictionary allocation, no string keys, no JSON serialization, so a training loop can call
// RunSimulator.DetectDecisionPointToBuffer(buffer) directly in-process without paying for any of
// that on every step.
//
// Layout: buffer[0] is always the DecisionType tag. Every other section lives at a fixed absolute
// offset regardless of DecisionType -- see BufferLayout below. PlayerSummary and RunContext are
// refreshed on every single call (they're relevant to every decision); Map only refreshes on
// MapSelect; every decision-specific section (CombatPlay, Shop, CardReward, ...) only refreshes when
// its own DecisionType is the one currently active. A section that isn't refreshed this call keeps
// whatever was written there last time -- see BufferStateWriter's constructor for why that's safe
// and how a caller opts into (or out of) it.

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

namespace Sts2Headless;

public partial class RunSimulator
{
    /// <summary>Set (scoped, via ExecuteActionToBuffer) so a DetectDecisionPoint() call embedded
    /// inside DoEndTurn/DoPlayCard/etc.'s own return statements writes into a buffer instead of
    /// building a JSON dict the caller would just discard -- see DetectDecisionPoint in
    /// RunSimulator.States.cs.</summary>
    private BufferStateWriter? _bufferRedirect;

    /// <summary>Buffer counterpart to ExecuteAction -- runs the exact same action logic (DoEndTurn,
    /// DoPlayCard, ...; nothing here is reimplemented) but redirects the DetectDecisionPoint() call
    /// those methods make internally to a buffer writer, so a training loop calling this in a tight
    /// step loop isn't also paying to build and immediately discard a JSON dict every step.</summary>
    public float[] ExecuteActionToBuffer(string action, Dictionary<string, object?>? args, float[]? buffer = null)
    {
        buffer ??= new float[BufferLayout.TotalSize];
        var previous = _bufferRedirect;
        _bufferRedirect = new BufferStateWriter(this, buffer);
        try { ExecuteAction(action, args); }
        finally { _bufferRedirect = previous; }
        return buffer;
    }

    /// <summary>Tags buffer[0] so a consumer can dispatch to the right fixed layout without any
    /// other bookkeeping.</summary>
    public enum DecisionType
    {
        Error = 0,
        GameOver = 1,
        MapSelect = 2,
        CombatPlay = 3,
        CardReward = 4,
        EventChoice = 5,
        RestSite = 6,
        Shop = 7,
        BundleSelect = 8,
        EventCardReward = 9,
        CardSelect = 10,
        Unknown = 11,
    }

    /// <summary>Fixed absolute float offsets for every section of the buffer. Every section has one
    /// permanent address regardless of which DecisionType produced the record -- a consumer reads
    /// e.g. player HP at BufferLayout.PlayerSummary + 1 whether the current decision is combat_play
    /// or shop. Sizes are derived (not guessed): each *Size constant is the exact fixed width of
    /// that section's encoder, computed from the Max* constants in BufferStateWriter/RunSimulator.
    /// See README.md for the field-by-field breakdown of each section.</summary>
    public static class BufferLayout
    {
        public const int DecisionType = 0;
        private const int DecisionTypeSize = 1;

        public const int PlayerSummary = DecisionType + DecisionTypeSize;
        private const int PlayerSummarySize = 657; // 7 + 40*Relic(2) + 10*Potion(2) + 50*Card(11)

        public const int Map = PlayerSummary + PlayerSummarySize;
        private const int MapSize = 1269; // 20*7*MapPoint(9) + 1 boss MapPoint(9)

        public const int RunContext = Map + MapSize;
        private const int RunContextSize = 5;

        public const int CombatPlay = RunContext + RunContextSize;
        private const int CombatPlaySize = 3717; // +100 vs. the original 3617: one target_type
        // float and one "cards" HandCardStatKeys entry, each added per hand slot (50 slots), after
        // this constant was first set -- see the hand-card loop below and HandCardStatKeys above.

        public const int CardSelect = CombatPlay + CombatPlaySize;
        private const int CardSelectSize = 552; // minSelect + maxSelect + 50*Card(11)

        public const int Shop = CardSelect + CardSelectSize;
        private const int ShopSize = 244; // 15*(Card(11)+3) + 6*3 + 5*3 + removalCost

        public const int RestSite = Shop + ShopSize;
        private const int RestSiteSize = 12; // 6*2

        public const int CardReward = RestSite + RestSiteSize;
        private const int CardRewardSize = 68; // canSkip + goldDelta + 6*Card(11)

        public const int EventChoice = CardReward + CardRewardSize;
        private const int EventChoiceSize = 6; // eventId + 5 option-locked flags

        public const int EventCardReward = EventChoice + EventChoiceSize;
        private const int EventCardRewardSize = 66; // 6*Card(11)

        public const int BundleSelect = EventCardReward + EventCardRewardSize;
        private const int BundleSelectSize = 352; // 4*8*Card(11)

        public const int GameOver = BundleSelect + BundleSelectSize;
        private const int GameOverSize = 1; // victory flag only -- act/floor come from RunContext

        public const int TotalSize = GameOver + GameOverSize;
    }

    private class BufferStateWriter : IStateWriter
    {
        /// <summary>Exact fixed size of every record -- not headroom, the whole point of
        /// BufferLayout is that this never varies by DecisionType.</summary>
        public const int RecommendedSize = BufferLayout.TotalSize;

        private const int MaxEnemies = 5;
        private const int MaxEnemyPowers = 8;
        private const int MaxPlayerPowers = 16;
        private const int MaxIntentsPerEnemy = 2;
        private const int MaxOrbs = 10;
        private const int MaxBundles = 4;
        private const int MaxBundleCards = 8;
        private const int MaxRewardCards = 6;
        private const int MaxEventOptions = 5;
        private const int MaxRestOptions = 6;
        private const int MaxShopCards = 15;
        private const int MaxShopRelics = 6;
        private const int MaxShopPotions = 5;

        // A fixed subset of the DynamicVar names that matter most for combat decisions. JSON dumps
        // every DynamicVar generically (arbitrary names); a flat buffer can't, so this picks the
        // common numeric fields and pads the rest with -1. Unlike the JSON hand-card path, uncommon
        // vars are simply not represented here -- if that turns out to matter, extend this list
        // rather than widening the buffer per-card.
        private static readonly string[] HandCardStatKeys =
        {
            "damage", "block", "magicnumber", "secondarydamage", "secondarymagicnumber",
            "calculateddamage", "repeat", "heal",
            "cards", // draw count (e.g. Pommel Strike) -- Vakuu's mcts.py needs this to detect a
                     // card-drawing play as a chance node (see its draws_cards()).
        };

        private readonly RunSimulator _sim;
        private readonly float[] _buffer;
        public int Length { get; private set; }

        /// <summary>Deliberately does NOT clear the buffer. A freshly allocated float[] is already
        /// zero-initialized by the CLR, so a caller's first-ever buffer starts all zeros for free.
        /// Clearing here on every construction would destroy the entire point of BufferLayout: a
        /// caller that passes the *same* array back into every DetectDecisionPointToBuffer call gets
        /// carryover for sections that aren't relevant to the current decision (e.g. Map keeps
        /// showing the layout from the last MapSelect while the agent is mid-combat) instead of
        /// those sections zeroing out every time something else changes. A caller that wants a clean
        /// slate for a new episode/run must allocate a fresh array itself.</summary>
        public BufferStateWriter(RunSimulator sim, float[] buffer)
        {
            _sim = sim;
            _buffer = buffer;
            Length = BufferLayout.TotalSize;
        }

        private void RefreshRunContext()
        {
            int offset = BufferLayout.RunContext;
            _sim.RunContext(_buffer, ref offset);
        }

        private void RefreshPlayerSummary(Player player)
        {
            int offset = BufferLayout.PlayerSummary;
            PlayerSummary(player, _buffer, ref offset);
        }

        public void WriteError(string message)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.Error;
        }

        public void WriteUnknown(object? room)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.Unknown;
            RefreshRunContext();
            RefreshPlayerSummary(_sim._runState!.Players[0]);
        }

        public void WriteGameOver(bool victory)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.GameOver;
            RefreshRunContext();

            var player = _sim._runState!.Players[0];
            RefreshPlayerSummary(player);

            _buffer[BufferLayout.GameOver] = victory ? 1 : 0;

            // BUG-005: When player died, the engine resets HP to max. Use last known HP instead.
            if (!victory)
            {
                int hpSlot = BufferLayout.PlayerSummary + 1; // PlayerSummary's field order: character(0), hp(1), ...
                _buffer[hpSlot] = _sim._lastKnownHp > 0 ? 0 : (player.Creature?.CurrentHp ?? 0);
            }
        }

        public void WriteMapSelect()
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.MapSelect;
            RefreshRunContext();

            // Full board (all rows/cols + both bosses), not just the current point's immediate
            // children -- reuses the existing Map() encoder used by GetFullMap's buffer path, which
            // is strictly more information than the JSON writer's "choices" list. Act/floor context
            // for this decision comes from RunContext above; no need to duplicate it here.
            int mapOffset = BufferLayout.Map;
            _sim.Map(_buffer, ref mapOffset);

            RefreshPlayerSummary(_sim._runState!.Players[0]);
        }

        public void WriteCombatPlay(Player player)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.CombatPlay;
            RefreshRunContext();

            int offset = BufferLayout.CombatPlay;

            var pcs = player.PlayerCombatState;
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            var aliveEnemies = combatState?.Enemies?.Where(e => e != null && e.IsAlive).ToList() ?? new();
            var playerCreatures = combatState?.PlayerCreatures?.ToList();

            _buffer[offset++] = combatState?.RoundNumber ?? 0;
            _buffer[offset++] = pcs?.Energy ?? 0;
            _buffer[offset++] = pcs?.MaxEnergy ?? 0;

            // Hand: fixed MaxDeckCard slots. Base card fields reuse the Card() encoder; on top of
            // that, a fixed subset of the resolved (buff-adjusted) DynamicVars plus per-target
            // damage against the alive enemies -- both read once via ResolveHandCardStats /
            // ResolveHandCardDamageByTarget, the exact same reads JsonStateWriter uses.
            var handCards = pcs?.Hand?.Cards?.ToList();
            for (int i = 0; i < MaxDeckCard; i++)
            {
                var c = handCards != null && i < handCards.Count ? handCards[i] : null;
                Card(c, _buffer, ref offset);
                _buffer[offset++] = c != null && c.CanPlay(out _, out _) ? 1 : 0;
                _buffer[offset++] = c?.CurrentStarCost ?? 0;
                // Raw TargetType enum ordinal (None=0, Self=1, AnyEnemy=2, AllEnemies=3, ...) --
                // a policy needs this to know whether play_card requires target_index (only
                // AnyEnemy with >=2 alive enemies does; see actions.py's PlayCardArgs), which
                // damage_by_target below can't disambiguate on its own (AllEnemies cards also
                // populate a damage row per enemy). -1 for an empty slot.
                _buffer[offset++] = c?.TargetType != null ? (int)c.TargetType : -1;

                // Empty-slot fast path avoids allocating a throwaway Dictionary for the ~43 unused
                // hand slots every call -- an empty dict's TryGetValue always misses anyway, so the
                // result (-1 for every key) is identical, just without the allocation.
                if (c != null)
                {
                    var stats = ResolveHandCardStats(c);
                    foreach (var key in HandCardStatKeys)
                        _buffer[offset++] = stats.TryGetValue(key, out var v) ? v : -1;
                }
                else
                {
                    foreach (var _ in HandCardStatKeys)
                        _buffer[offset++] = -1;
                }

                var damageByTarget = c != null ? ResolveHandCardDamageByTarget(c, aliveEnemies, pcs?.Energy ?? 0) : null;
                for (int ti = 0; ti < MaxEnemies; ti++)
                {
                    var row = damageByTarget != null && ti < damageByTarget.Count ? damageByTarget[ti] : (HandCardTargetDamage?)null;
                    _buffer[offset++] = row?.Damage ?? -1;
                    _buffer[offset++] = row?.Repeat ?? -1;
                }
            }

            // Enemies: fixed MaxEnemies slots.
            for (int i = 0; i < MaxEnemies; i++)
            {
                var e = i < aliveEnemies.Count ? aliveEnemies[i] : null;
                _buffer[offset++] = e?.Monster != null ? _tokenizer.Encode("monsters", e.Monster.Id.Entry) : -1;
                _buffer[offset++] = e?.CurrentHp ?? 0;
                _buffer[offset++] = e?.MaxHp ?? 0;
                _buffer[offset++] = e?.Block ?? 0;
                _buffer[offset++] = e?.Monster?.IntendsToAttack == true ? 1 : 0;

                var nextIntents = e?.Monster?.NextMove?.Intents?.ToList();
                for (int ii = 0; ii < MaxIntentsPerEnemy; ii++)
                {
                    var intent = nextIntents != null && ii < nextIntents.Count ? nextIntents[ii] : null;
                    _buffer[offset++] = intent != null ? (int)intent.IntentType : -1;
                    int dmg = -1;
                    if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk && playerCreatures != null && e != null)
                        dmg = atk.GetTotalDamage(playerCreatures, e);
                    _buffer[offset++] = dmg;
                }

                var powers = e?.Powers?.ToList();
                for (int pi = 0; pi < MaxEnemyPowers; pi++)
                {
                    var pw = powers != null && pi < powers.Count ? powers[pi] : null;
                    _buffer[offset++] = pw != null ? _tokenizer.Encode("powers", pw.Id.Entry) : -1;
                    _buffer[offset++] = pw?.Amount ?? 0;
                }
            }

            // Player powers/buffs
            var playerPowers = player.Creature?.Powers?.ToList();
            for (int i = 0; i < MaxPlayerPowers; i++)
            {
                var pw = playerPowers != null && i < playerPowers.Count ? playerPowers[i] : null;
                _buffer[offset++] = pw != null ? _tokenizer.Encode("powers", pw.Id.Entry) : -1;
                _buffer[offset++] = pw?.Amount ?? 0;
            }

            _buffer[offset++] = pcs?.DrawPile?.Cards?.Count ?? 0;
            _buffer[offset++] = pcs?.DiscardPile?.Cards?.Count ?? 0;
            _buffer[offset++] = pcs?.ExhaustPile?.Cards?.Count ?? 0;

            WritePile(pcs?.DrawPile?.Cards, _buffer, ref offset);
            WritePile(pcs?.DiscardPile?.Cards, _buffer, ref offset);
            WritePile(pcs?.ExhaustPile?.Cards, _buffer, ref offset);

            // Character-specific mechanics
            var orbs = pcs?.OrbQueue?.Orbs?.ToList();
            for (int i = 0; i < MaxOrbs; i++)
            {
                var orb = orbs != null && i < orbs.Count ? orbs[i] : null;
                _buffer[offset++] = orb != null ? _tokenizer.Encode("orbs", orb.Id.Entry) : -1;
                _buffer[offset++] = (int)(orb?.PassiveVal ?? -1);
                _buffer[offset++] = (int)(orb?.EvokeVal ?? -1);
            }
            _buffer[offset++] = pcs?.OrbQueue?.Capacity ?? 0;

            _buffer[offset++] = (pcs != null && player.Character?.Id.Entry == "REGENT") ? pcs.Stars : -1;

            var osty = player.Osty;
            _buffer[offset++] = osty?.CurrentHp ?? -1;
            _buffer[offset++] = osty?.MaxHp ?? -1;
            _buffer[offset++] = osty?.Block ?? -1;
            _buffer[offset++] = osty != null ? (osty.IsAlive ? 1 : 0) : (player.Character?.Id.Entry == "NECROBINDER" ? 0 : -1);

            // Combat history counts (see HistoryCounts doc comment): exposes the scaling inputs for
            // cards like Supermassive/Spite/Murder even when those cards aren't currently in hand.
            //
            // Measured tradeoff (2026-08-23): skipping these reads entirely and zero-filling instead
            // (no RefreshHistoryCache call at all) measured ~528 steps/s vs ~393 steps/s with the
            // O(1) incremental cache below -- a further ~1.3x on top of the cache, at the cost of
            // these fields always reading 0 instead of real history-scaling data. Not applied: the
            // incremental cache already captured the overwhelming majority of the original win (26
            // steps/s -> 393 before this tradeoff), and zeroing the fields silently misrepresents
            // state for any card whose scaling depends on them. Revisit only if profiling shows
            // HistoryCounts is a bottleneck again after other optimizations land.
            _sim.HistoryCounts(player.Creature, combatState, _buffer, ref offset);
            _sim.WriteHistoryCount(player, "card_generated_by_creator", combatState, _buffer, ref offset);
            for (int i = 0; i < MaxEnemies; i++)
            {
                var e = i < aliveEnemies.Count ? aliveEnemies[i] : null;
                _sim.HistoryCounts(e, combatState, _buffer, ref offset);
            }

            RefreshPlayerSummary(player);
        }

        private static void WritePile(IReadOnlyList<CardModel>? cards, float[] buffer, ref int offset)
        {
            for (int i = 0; i < MaxDeckCard; i++)
                Card(cards != null && i < cards.Count ? cards[i] : null, buffer, ref offset);
        }

        public void WriteCardReward(Player player, CombatRoom? combatRoom)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.CardReward;
            RefreshRunContext();

            int offset = BufferLayout.CardReward;
            var pendingCardReward = _sim._pendingCardReward!;
            _buffer[offset++] = pendingCardReward.CanSkip ? 1 : 0;
            _buffer[offset++] = _sim._runState!.Players[0].Gold - _sim._goldBeforeCombat;

            var cards = pendingCardReward.Cards.ToList();
            for (int i = 0; i < MaxRewardCards; i++)
                Card(i < cards.Count ? cards[i] : null, _buffer, ref offset);

            RefreshPlayerSummary(_sim._runState.Players[0]);
        }

        public void WriteEventChoice(EventRoom eventRoom)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.EventChoice;
            RefreshRunContext();

            int offset = BufferLayout.EventChoice;
            var localEvent = RunManager.Instance.EventSynchronizer?.GetLocalEvent();
            _buffer[offset++] = localEvent?.Id != null ? _tokenizer.Encode("events", localEvent.Id.Entry) : -1;

            var options = localEvent?.CurrentOptions?.ToList();
            for (int i = 0; i < MaxEventOptions; i++)
            {
                var opt = options != null && i < options.Count ? options[i] : null;
                _buffer[offset++] = opt != null ? (opt.IsLocked ? 1 : 0) : -1;
            }

            RefreshPlayerSummary(_sim._runState!.Players[0]);
        }

        public void WriteRestSite(RestSiteRoom restRoom)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.RestSite;
            RefreshRunContext();

            int offset = BufferLayout.RestSite;
            var options = restRoom.Options?.ToList();
            for (int i = 0; i < MaxRestOptions; i++)
            {
                var opt = options != null && i < options.Count ? options[i] : null;
                _buffer[offset++] = opt != null ? 1 : -1;
                _buffer[offset++] = opt != null ? (opt.IsEnabled ? 1 : 0) : -1;
            }

            RefreshPlayerSummary(_sim._runState!.Players[0]);
        }

        public void WriteShop(MerchantRoom merchantRoom, Player player)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.Shop;
            RefreshRunContext();

            int offset = BufferLayout.Shop;
            var inv = merchantRoom.GetLocalInventory();
            var shopCards = inv?.CharacterCardEntries.Concat(inv.ColorlessCardEntries).ToList();
            for (int i = 0; i < MaxShopCards; i++)
            {
                var e = shopCards != null && i < shopCards.Count ? shopCards[i] : null;
                Card(e?.CreationResult?.Card, _buffer, ref offset);
                _buffer[offset++] = e?.Cost ?? -1;
                _buffer[offset++] = e != null ? (e.IsStocked ? 1 : 0) : -1;
                _buffer[offset++] = e != null ? (e.IsOnSale ? 1 : 0) : -1;
            }

            var relics = inv?.RelicEntries?.ToList();
            for (int i = 0; i < MaxShopRelics; i++)
            {
                var e = relics != null && i < relics.Count ? relics[i] : null;
                _buffer[offset++] = e?.Model != null ? _tokenizer.Encode("relics", e.Model.Id.Entry) : -1;
                _buffer[offset++] = e?.Cost ?? -1;
                _buffer[offset++] = e != null ? (e.IsStocked ? 1 : 0) : -1;
            }

            var potions = inv?.PotionEntries?.ToList();
            for (int i = 0; i < MaxShopPotions; i++)
            {
                var e = potions != null && i < potions.Count ? potions[i] : null;
                _buffer[offset++] = e?.Model != null ? _tokenizer.Encode("potions", e.Model.Id.Entry) : -1;
                _buffer[offset++] = e?.Cost ?? -1;
                _buffer[offset++] = e != null ? (e.IsStocked ? 1 : 0) : -1;
            }

            _buffer[offset++] = inv?.CardRemovalEntry?.Cost ?? -1;

            RefreshPlayerSummary(player);
        }

        public void WriteBundleSelect(IReadOnlyList<IReadOnlyList<CardModel>> bundles)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.BundleSelect;
            RefreshRunContext();

            int offset = BufferLayout.BundleSelect;
            for (int b = 0; b < MaxBundles; b++)
            {
                var bundle = b < bundles.Count ? bundles[b] : null;
                for (int i = 0; i < MaxBundleCards; i++)
                    Card(bundle != null && i < bundle.Count ? bundle[i] : null, _buffer, ref offset);
            }

            RefreshPlayerSummary(_sim._runState!.Players[0]);
        }

        public void WriteEventCardReward(IReadOnlyList<CardCreationResult> cards)
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.EventCardReward;
            RefreshRunContext();

            int offset = BufferLayout.EventCardReward;
            for (int i = 0; i < MaxRewardCards; i++)
                Card(i < cards.Count ? cards[i].Card : null, _buffer, ref offset);

            RefreshPlayerSummary(_sim._runState!.Players[0]);
        }

        public void WriteCardSelect()
        {
            _buffer[BufferLayout.DecisionType] = (float)DecisionType.CardSelect;
            RefreshRunContext();

            int offset = BufferLayout.CardSelect;
            var cardSelector = _sim._cardSelector;
            var opts = cardSelector.PendingOptions!;
            _buffer[offset++] = cardSelector.PendingMinSelect;
            _buffer[offset++] = cardSelector.PendingMaxSelect;
            for (int i = 0; i < MaxDeckCard; i++)
                Card(i < opts.Count ? opts[i] : null, _buffer, ref offset);

            RefreshPlayerSummary(_sim._runState!.Players[0]);
        }
    }
}
