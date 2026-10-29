using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Friendly towns near a colony with a good name send more merchants and visitors: fame and friendship mean
    /// traffic. The arrival runs the normal incident (so it travels as a world party) from the town that wants to come.
    /// </summary>
    public static class Neighbours
    {
        public const float Range = 20f;
        private const int MinGapTicks = GenDate.TicksPerDay * 6;
        private static readonly System.Collections.Generic.List<(CompSettlementLife s, float score)> candidates = new System.Collections.Generic.List<(CompSettlementLife, float)>();
        private static readonly System.Collections.Generic.Dictionary<int, bool> able = new System.Collections.Generic.Dictionary<int, bool>();
        public static int StatSent;

        public static void DailyCheck(LivingWorldComponent comp, bool force = false)
        {
            int now = Find.TickManager.TicksGame;
            if (now - comp.lastNeighbourVisitTick < MinGapTicks) return;
            Map home = Neighbourhood.Home;
            if (home == null || !LivingWorldMod.Settings.worldParties) return;
            // Not on the heels of the storyteller's own visitors or traders: the two never arrive the same week.
            if (!force && (RecentVanilla(home, IncidentDefOf.VisitorGroup, now) || RecentVanilla(home, IncidentDefOf.TraderCaravanArrival, now))) return;
            candidates.Clear();
            able.Clear();
            float bestScore = 0f, range = Neighbourhood.Radius(home.Tile, Range);
            foreach (CompSettlementLife s in comp.activeSettlements)
            {
                Faction f = s.Settlement.Faction;
                if (f.HostileTo(Faction.OfPlayer) || f.def.permanentEnemy || f.PlayerGoodwill < 0) continue;
                if (!able.TryGetValue(f.loadID, out bool can)) able[f.loadID] = can = CanSendAnyone(f, home);
                if (!can) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile);
                if (d > range) continue;
                // Daily chance: goodwill (neutral towns come now and then, friends often), the colony's name there, the town's
                // trade and how close it is.
                float score = 0.12f * ((f.PlayerGoodwill + 50f) / 125f) * (0.5f + Mathf.Max(0f, s.fame)) * s.Tier.tradeFactor * Mathf.Clamp01(1.3f - d / range) * (s.FairOn ? 2f : 1f)
                              * (comp.StateFor(f)?.tradePactUntil > now ? 1.5f : 1f) // a trade pact brings their merchants more often
                              * Roads.NeighbourFactor(home, s.Settlement); // so does a road joining the town to the colony
                candidates.Add((s, score));
                bestScore = Mathf.Max(bestScore, score);
            }
            // About one extra arrival every eight weeks from a neutral neighbour, every three to four weeks from friends,
            // every week and a half or two where the colony is famous (the 6-day gap included);
            // a colony renowned across the world draws them more often.
            float renown = Legends.RenownFactor(comp);
            if (Testing.AutoTest.Active && Testing.AutoTest.Plan == "life")
            {
                int inRange = 0, hostile = 0, cold = 0, cannot = 0;
                foreach (CompSettlementLife s in comp.activeSettlements)
                {
                    if (Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile) > range) continue;
                    inRange++;
                    Faction f = s.Settlement.Faction;
                    if (f.HostileTo(Faction.OfPlayer) || f.def.permanentEnemy) hostile++;
                    else if (f.PlayerGoodwill < 0) cold++;
                    else if (able.TryGetValue(f.loadID, out bool can) ? !can : !CanSendAnyone(f, home)) cannot++;
                }
                LWLog.Message($"[neighbours] {candidates.Count} towns can come (range {range:F0}; in range {inRange}: hostile {hostile}, goodwill<0 {cold}, cannot send {cannot}), best daily chance {Mathf.Min(bestScore * renown, 0.15f * renown):P1}");
            }
            if (candidates.Count == 0 || (!force && !Rand.Chance(Mathf.Min(bestScore * renown, 0.15f * renown)))) return;
            // Any of the friendly towns may come (the best-disposed most often), so the colony gets to know several.
            CompSettlementLife best = candidates.RandomElementByWeight(c => c.score).s;
            Faction fac = best.Settlement.Faction;
            comp.lastNeighbourVisitTick = now;
            // Merchants when they can come (some need a trade permit), visitors otherwise.
            bool trade = CanSendTraders(fac, home) && Rand.Chance(0.6f);
            bool ok = trade && Send(IncidentDefOf.TraderCaravanArrival, Bridge.PreferredTradeSource, fac, best, home);
            if (!ok && Bridge.CanSendVisitors(fac))
            {
                trade = false;
                ok = Send(IncidentDefOf.VisitorGroup, Bridge.PreferredVisitSource, fac, best, home);
            }
            if (ok) StatSent++;
            if (Testing.AutoTest.Active) LWLog.Message($"[neighbours] {best.Settlement.Label} ({fac.Name}) sends {(trade ? "merchants" : "visitors")}: {ok}");
        }

        /// <summary>The faction has a trader caravan it may send here (vanilla's own check: trader kinds, permits, ideology).</summary>
        public static bool CanSendTraders(Faction f, Map home) =>
            !f.def.caravanTraderKinds.NullOrEmpty() && IncidentDefOf.TraderCaravanArrival.Worker is IncidentWorker_TraderCaravanArrival w
            && w.FactionCanBeGroupSource(f, new IncidentParms { target = home, faction = f });

        public static bool CanSendAnyone(Faction f, Map home) => CanSendTraders(f, home) || Bridge.CanSendVisitors(f);

        private static bool Send(IncidentDef def, System.Collections.Generic.Dictionary<int, int> preferred, Faction fac, CompSettlementLife from, Map home)
        {
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, home);
            parms.faction = fac;
            if (!def.Worker.CanFireNow(parms)) return false; // e.g. their enemies are on the map
            preferred[fac.loadID] = from.Settlement.ID;
            bool ok = def.Worker.TryExecute(parms);
            preferred.Remove(fac.loadID);
            // Told to the storyteller like its own incidents (TryExecute alone does not), so refire rules see it.
            if (ok) home.StoryState.Notify_IncidentFired(new FiringIncident(def, null, parms));
            return ok;
        }

        private static bool RecentVanilla(Map home, IncidentDef def, int now) =>
            home.StoryState.lastFireTicks.TryGetValue(def, out int t) && now - t < GenDate.TicksPerDay * 3;
    }
}
