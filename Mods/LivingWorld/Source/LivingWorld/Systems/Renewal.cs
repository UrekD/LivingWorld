using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// The world refills: when it has fewer peoples than it started with (wars, the colony's doing), or a whole band of
    /// ages is missing (no tribes left, or nobody industrial or beyond), a new people arrives from beyond the known lands
    /// and settles empty land. At most one arrival a year, so the change is slow and noticeable.
    /// </summary>
    public static class Renewal
    {
        public const int GapTicks = GenDate.TicksPerYear;
        public static int StatArrivals;

        private static bool Simple(Faction f) => KnowHow.Effective(f) <= TechLevel.Medieval;
        private static bool Advanced(Faction f) => KnowHow.Effective(f) >= TechLevel.Industrial;

        public static void DailyStep(LivingWorldComponent comp)
        {
            if (!LivingWorldMod.Settings.allowExpansion || SimClock.Now - comp.lastRenewalTick < GapTicks) return;
            int alive = 0;
            bool simple = false, advanced = false;
            foreach (FactionState fs in comp.factionStates)
            {
                Faction f = fs.faction;
                if (f == null || f.defeated || fs.settlements.Count == 0) continue;
                alive++;
                if (f.def.permanentEnemy) continue; // pirates do not stand for a band of ages
                simple |= Simple(f);
                advanced |= Advanced(f);
            }
            if (comp.initialFactionCount <= 0) comp.initialFactionCount = alive;
            bool fewer = alive < comp.initialFactionCount;
            if (!fewer && simple && advanced) return;
            // Whatever band is missing comes first; otherwise anyone.
            FactionDef def = PickKind(need: !simple ? 1 : !advanced ? 2 : 0);
            if (def != null) Arrive(comp, def);
        }

        /// <summary>A kind of people to arrive: need 1 = simple (up to medieval), 2 = advanced (industrial+), 0 = any.</summary>
        private static FactionDef PickKind(int need)
        {
            var kinds = new List<FactionDef>();
            foreach (FactionDef d in DefDatabase<FactionDef>.AllDefsListForReading)
            {
                if (!Rebellion.CanSplit(d) || d.permanentEnemy || d.pawnGroupMakers.NullOrEmpty()) continue;
                if (need == 1 && d.techLevel > TechLevel.Medieval) continue;
                if (need == 2 && d.techLevel < TechLevel.Industrial) continue;
                kinds.Add(d);
            }
            return kinds.TryRandomElementByWeight(d => d.settlementGenerationWeight, out FactionDef pick) ? pick : null;
        }

        public static Faction Arrive(LivingWorldComponent comp, FactionDef def)
        {
            comp.lastRenewalTick = SimClock.Now;
            Faction f = Rebellion.CreateFaction(def);
            // Empty land, away from everyone (and from the colony).
            Map home = Neighbourhood.Home;
            PlanetTile tile = TileFinder.RandomSettlementTileFor(f, mustBeAutoChoosable: false, t =>
                (home == null || Find.WorldGrid.ApproxDistanceInTiles(t, home.Tile) > 12f) && !Find.WorldObjects.AnySettlementBaseAtOrAdjacent(t));
            // The finder returns tile 0 (not "invalid") when it gives up: check the site for real.
            bool ok = tile.Valid && TileFinder.IsValidTileForNewSettlement(tile) && !Find.WorldObjects.AnyWorldObjectAt(tile)
                      && !Find.WorldObjects.AnySettlementBaseAtOrAdjacent(tile) && (home == null || Find.WorldGrid.ApproxDistanceInTiles(tile, home.Tile) > 12f);
            if (!ok)
            {
                // Nowhere to go: they never come (the faction stays without towns and is ignored).
                f.defeated = true;
                return null;
            }
            float people = def.techLevel <= TechLevel.Neolithic ? 70f : 140f;
            Settlement first = SettlementOps.Found(f, tile, people);
            // The second town through the settlers' path (it clears a ruin it settles on).
            if (Expansion.TryFindSite(tile, out PlanetTile second)) Expansion.Settle(comp, f, first, second, people * 0.6f);
            // Strangers: wary of everyone, neither friend nor enemy at first (a natural enemy's −80 needs 0 to stop
            // being hostile, then −10 stays neutral). Permanent enemies stay enemies.
            foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
            {
                if (other == f || other.def.hidden || other.def.permanentEnemy || f.RelationWith(other, allowNull: true) == null) continue;
                if (f.HostileTo(other)) f.TryAffectGoodwillWith(other, -f.BaseGoodwillWith(other), canSendMessage: false, canSendHostilityLetter: false);
                int delta = -10 - f.BaseGoodwillWith(other);
                if (delta != 0) f.TryAffectGoodwillWith(other, delta, canSendMessage: false, canSendHostilityLetter: false);
            }
            comp.RebuildIndexes(seedMissing: true);
            Rulers.InitNewFaction(comp, f);
            StatArrivals++;
            ChronicleEventDef ce = LWChronicleDefOf.LW_NewPeopleArrive;
            comp.chronicle.Record(ce, ce.text.Formatted(f.Name.Named("FACTION"), def.techLevel.ToStringHuman().Named("TECH"), first.Label.Named("SETTLEMENT")).Resolve(), f, wo: first);
            LWLog.Message($"renewal: {f.Name} ({def.defName}) arrived at {first.Label}");
            return f;
        }
    }
}
