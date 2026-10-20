using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// What a settlement thinks of the colony, from how its visitors and merchants were treated (goodwill gained or
    /// lost during a visit). Fame gives better prices there and is talked about in the chronicle.
    /// </summary>
    public static class Fame
    {
        public static void Notify_VisitEnded(IncidentContext ctx, CompSettlementLife home, int goodwillDelta)
        {
            if (goodwillDelta == 0) return;
            home.fame = Mathf.Clamp(home.fame + goodwillDelta / 60f, -1f, 1f);
            if (Mathf.Abs(goodwillDelta) < 4) return;
            ChronicleEventDef def = goodwillDelta > 0 ? LWChronicleDefOf.LW_GuestsPraised : LWChronicleDefOf.LW_GuestsComplained;
            Faction f = home.Settlement.Faction;
            LivingWorldComponent.Instance.chronicle.Record(def, def.text.Formatted((ctx.originName ?? home.Settlement.Label).Named("ORIGIN"),
                (ctx.mapLabel ?? "?").Named("COLONY"), f.Name.Named("FACTION")).Resolve(), f, Faction.OfPlayer, home.Settlement);
        }

        /// <summary>Price bonus for the player in this settlement's market: buy cheaper, sell dearer when famous there.</summary>
        public static float PriceMultiplier(CompSettlementLife home, bool playerBuys) =>
            home == null ? 1f : 1f + (playerBuys ? -0.08f : 0.08f) * home.fame;
    }
}
