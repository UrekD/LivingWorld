using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Crowded, prosperous settlements send settlers to found new ones (ruins are resettled first).</summary>
    public static class Expansion
    {
        public static void DailyStep(LivingWorldComponent comp, float pace)
        {
            if (!LivingWorldMod.Settings.allowExpansion) return;
            foreach (FactionState fs in comp.factionStates)
            {
                Faction f = fs.faction;
                if (f == null || f.defeated || fs.settlements.Count == 0 || fs.treasury < 600f) continue;
                float exp = fs.TraitFactor(t => t.expansion);
                // Room to grow relative to the world's typical faction size.
                if (fs.settlements.Count >= Cap(comp, fs) || PartyBudget.CountOf(LWPartyDefOf.LW_Settlers, f) > 0) continue;
                if (!Rand.Chance(0.02f * pace * exp * (fs.goal == RulerGoalKind.Expand ? 2f : 1f))) continue;
                CompSettlementLife origin = null;
                foreach (CompSettlementLife s in fs.settlements)
                    if (s.population > s.populationCapacity * 0.45f && s.prosperity > 0.38f && s.famineDays == 0 && (origin == null || s.population > origin.population)) origin = s;
                if (origin == null || !TryFindSite(origin.Settlement.Tile, out PlanetTile site)) continue;
                float people = Mathf.Max(Tuning.T.minPopulation, origin.population * 0.12f);
                origin.population -= people;
                fs.treasury -= 500f;
                if (PartyBudget.CanLaunchNpc())
                {
                    WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_Settlers, f, origin.Settlement, null, site);
                    p.population = people;
                    p.strength = people;
                }
                else Settle(comp, f, origin.Settlement, site, people);
            }
        }

        /// <summary>How many towns a faction may found up to: the world's starting faction size, scaled by its temperament.</summary>
        public static float Cap(LivingWorldComponent comp, FactionState fs) =>
            Mathf.Max(4f, comp.initialAvgSettlements * (0.7f + 0.4f * fs.TraitFactor(t => t.expansion)));

        public static bool TryFindSite(PlanetTile near, out PlanetTile site)
        {
            // Ruins nearby are the first choice.
            foreach (DestroyedSettlement ruin in Find.WorldObjects.DestroyedSettlements)
            {
                if (ruin.HasMap || ruin.Tile.Layer != near.Layer) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(near, ruin.Tile);
                if (d >= 3f && d <= 12f && Find.WorldReachability.CanReach(near, ruin.Tile))
                {
                    site = ruin.Tile;
                    return true;
                }
            }
            // The fast (job-based) tile finder: a breadth-first search here costs ~30 ms.
            List<PlanetTile> candidates = near.Layer.FastTileFinder.Query(new FastTileFinder.TileQueryParams(near, 4f, 11f));
            candidates.Shuffle();
            foreach (PlanetTile t in candidates)
                if (TileFinder.IsValidTileForNewSettlement(t) && !Find.WorldObjects.AnySettlementBaseAtOrAdjacent(t) && !Find.WorldObjects.AnyWorldObjectAt(t))
                {
                    site = t;
                    return true;
                }
            site = PlanetTile.Invalid;
            return false;
        }

        public static Settlement Settle(LivingWorldComponent comp, Faction f, Settlement origin, PlanetTile site, float people)
        {
            DestroyedSettlement ruin = Find.WorldObjects.DestroyedSettlementAt(site);
            if (ruin != null && !ruin.HasMap) ruin.Destroy();
            if (Find.WorldObjects.AnySettlementBaseAtOrAdjacent(site) || (!TileFinder.IsValidTileForNewSettlement(site) && ruin == null)) return null;
            Settlement s = SettlementOps.Found(f, site, people);
            Rulers.Notify_Founded(comp, f);
            ChronicleEventDef def = ruin != null ? LWChronicleDefOf.LW_RuinResettled : LWChronicleDefOf.LW_SettlementFounded;
            comp.chronicle.Record(def, def.text.Formatted(f.Name.Named("FACTION"), s.Label.Named("SETTLEMENT"), (origin?.Label ?? "?").Named("ORIGIN")).Resolve(), f, wo: s);
            return s;
        }
    }

    public class PartyWorker_Settlers : PartyWorker
    {
        public override bool ArrivedAtDestination(WorldParty p)
        {
            if (p.Faction == null || p.Faction.defeated) return true;
            Settlement s = Expansion.Settle(LivingWorldComponent.Instance, p.Faction, p.Origin, p.Tile, p.population);
            if (s != null) return true;
            p.TurnBack(); // the site was taken: go home
            return false;
        }

        public override void ArrivedHome(WorldParty p)
        {
            CompSettlementLife life = p.Origin?.GetComponent<CompSettlementLife>();
            if (life != null && life.initialized) life.population += p.population;
        }

        public override void DestinationLost(WorldParty p) => p.TurnBack();
    }
}
