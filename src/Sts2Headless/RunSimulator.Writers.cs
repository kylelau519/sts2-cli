using System.Collections.Generic;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;

namespace Sts2Headless;

public partial class RunSimulator
{
    public interface IStateWriter
    {
        void WriteGameOver(bool victory);
        void WriteMapSelect();
        void WriteCombatPlay(Player player);
        void WriteCardReward(Player player, CombatRoom? combatRoom);
        void WriteEventChoice(EventRoom eventRoom);
        void WriteRestSite(RestSiteRoom restRoom);
        void WriteShop(MerchantRoom merchantRoom, Player player);
        
        void WriteBundleSelect(IReadOnlyList<IReadOnlyList<CardModel>> bundles);
        void WriteEventCardReward(IReadOnlyList<CardCreationResult> cards);
        void WriteCardSelect();
        void WriteUnknown(object? room);
        void WriteError(string message);
    }
}
