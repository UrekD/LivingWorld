using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>A settlement in need asks the colony for a commodity; selling it to them (or their merchants) fulfils it.</summary>
    public class AidRequest : IExposable
    {
        public int id;
        public int settlementId = -1;
        public string settlementName;
        public CommodityDef commodity;
        /// <summary>Silver worth of goods wanted.</summary>
        public float wanted;
        public float delivered;
        public int deadlineTicks;
        public bool done;

        public bool Expired => !done && Find.TickManager.TicksGame > deadlineTicks;

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref settlementId, "settlement", -1);
            Scribe_Values.Look(ref settlementName, "settlementName");
            Scribe_Defs.Look(ref commodity, "commodity");
            Scribe_Values.Look(ref wanted, "wanted");
            Scribe_Values.Look(ref delivered, "delivered");
            Scribe_Values.Look(ref deadlineTicks, "deadline");
            Scribe_Values.Look(ref done, "done");
        }
    }

    /// <summary>
    /// The colony-facing story loop: towns near you that starve or run short (war, famine) send a messenger asking for
    /// help; relief earns goodwill, fame and a reward, and is remembered in the chronicle.
    /// </summary>
    public static class AidRequests
    {
        public const float MaxRange = 15f;

        public static AidRequest ActiveFor(int settlementId)
        {
            foreach (AidRequest r in LivingWorldComponent.Instance.aidRequests)
                if (!r.done && r.settlementId == settlementId) return r;
            return null;
        }

        public static void DailyCheck(LivingWorldComponent comp)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = comp.aidRequests.Count - 1; i >= 0; i--)
            {
                AidRequest r = comp.aidRequests[i];
                // The town was razed, or fell to a faction hostile to the colony: nobody left to help.
                if (!r.done && (LivingWorldComponent.WorldObjectById(r.settlementId) is not Settlement st || st.Destroyed || st.Faction == null
                                || st.Faction.HostileTo(Faction.OfPlayer)))
                {
                    r.done = true;
                    r.deadlineTicks = now;
                    continue;
                }
                if (r.Expired)
                {
                    r.done = true;
                    Messages.Message("LW_AidExpired".Translate(r.settlementName, r.commodity.label), MessageTypeDefOf.NeutralEvent);
                }
                if (r.done && now - r.deadlineTicks > GenDate.TicksPerDay * 30) comp.aidRequests.RemoveAt(i);
            }
            int active = 0;
            foreach (AidRequest r in comp.aidRequests)
                if (!r.done) active++;
            if (active > 0 || now - comp.lastAidRequestTick < GenDate.TicksPerDay * 12 || !Rand.Chance(0.25f)) return;
            Map home = Neighbourhood.Home;
            if (home == null || !home.Tile.Valid) return;
            CompSettlementLife best = null;
            CommodityDef bestGood = null;
            float bestNeed = 0f, range = Neighbourhood.Radius(home.Tile, MaxRange);
            foreach (CompSettlementLife s in comp.activeSettlements)
            {
                if (!s.Active) continue;
                Faction f = s.Settlement.Faction;
                if (f.HostileTo(Faction.OfPlayer) || f.def.permanentEnemy || f.PlayerGoodwill < -10) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile);
                if (d > range) continue;
                foreach (CommodityDef c in Commodities.All)
                {
                    float need = s.PriceFactor(c) * (c.essential && s.hunger > 0.2f ? 1.5f : 1f) / (d + 5f);
                    if (s.PriceFactor(c) >= 1.9f && need > bestNeed)
                    {
                        bestNeed = need;
                        best = s;
                        bestGood = c;
                    }
                }
            }
            if (best == null) return;
            var req = new AidRequest
            {
                id = comp.NextId(),
                settlementId = best.Settlement.ID,
                settlementName = best.Settlement.Label,
                commodity = bestGood,
                wanted = Mathf.Round(Mathf.Clamp(best.DailyConsumption(bestGood) * 5f * bestGood.silverPerUnit, 300f, 2500f) / 50f) * 50f,
                deadlineTicks = now + GenDate.TicksPerDay * 15,
            };
            comp.aidRequests.Add(req);
            comp.lastAidRequestTick = now;
            Faction fac = best.Settlement.Faction;
            string why = bestGood.essential && best.hunger > 0.2f ? "LW_AidWhy_Famine".Translate().Resolve() : "LW_AidWhy_Shortage".Translate(bestGood.label).Resolve();
            Find.LetterStack.ReceiveLetter("LW_AidLabel".Translate(best.Settlement.Label),
                "LW_AidText".Translate(best.Settlement.Label.Named("SETTLEMENT"), fac.NameColored.Named("FACTION"), why.Named("WHY"), bestGood.label.Named("GOOD"),
                    Mathf.RoundToInt(req.wanted).Named("AMOUNT"), 15.Named("DAYS"), best.PriceFactor(bestGood).ToString("F1").Named("PRICE")),
                LetterDefOf.NeutralEvent, best.Settlement, fac);
        }

        /// <summary>The player sold goods to a settlement's market (directly or via its merchants).</summary>
        public static void Notify_Sold(CompSettlementLife market, CommodityDef c, float silver)
        {
            AidRequest r = ActiveFor(market.Settlement.ID);
            if (r == null || r.commodity != c) return;
            r.delivered += silver;
            if (r.delivered < r.wanted) return;
            r.done = true;
            Faction f = market.Settlement.Faction;
            f.TryAffectGoodwillWith(Faction.OfPlayer, 12, canSendMessage: true, canSendHostilityLetter: true, reason: LWHistoryDefOf.LW_SentAid);
            market.fame = Mathf.Clamp(market.fame + 0.3f, -1f, 1f);
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            ChronicleEventDef def = LWChronicleDefOf.LW_AidDelivered;
            comp.chronicle.Record(def, def.text.Formatted(market.Settlement.Label.Named("SETTLEMENT"), c.label.Named("GOOD")).Resolve(), Faction.OfPlayer, f, market.Settlement);
            Roots.Notify(market.Settlement, HomeNews.Helped);
        }
    }

    [DefOf]
    public static class LWHistoryDefOf
    {
        public static HistoryEventDef LW_SentAid;
        public static HistoryEventDef LW_FoughtOurEnemies;
        public static HistoryEventDef LW_IgnoredOurCall;
        public static HistoryEventDef LW_MadePeace;
        public static HistoryEventDef LW_DemandedTribute;
        public static HistoryEventDef LW_AcceptedProposal;
        public static HistoryEventDef LW_DeclinedProposal;

        static LWHistoryDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWHistoryDefOf));
    }
}
