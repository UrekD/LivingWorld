using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// The tour's town map shots: the nearest town's map as it would be generated for an attack, once with healthy stores and once sacked and
    /// starving. The map is made the way an assault makes it, shown as the current map, and taken away again (with the pawns it made) before the
    /// next shot and at the end of the tour. The town's state is put back as soon as the map exists.
    /// </summary>
    internal static class TownShots
    {
        private static Map map;
        private static Settlement settlement;
        private static Map home;
        private static TownMapChecks.PawnBaseline baseline;
        private static readonly List<Pawn> made = new List<Pawn>();
        private static TimeSpeed speed;

        /// <summary>The sacked shot is a scar at its full (severity 1, one day ago), the most the post pass shows.</summary>
        private const float FullScar = 1f;
        /// <summary>Half the height of the sacked shot, in cells (the camera's closest zoom is 11): 22 rows by about 39 columns, near enough to see broken walls, ash and rubble.</summary>
        private const float CloseSize = 11f;
        private const int CloseHalfWidth = 18;
        private const int CloseHalfHeight = 10;

        /// <summary>A town map is on screen. The tour runs no ticks meanwhile: on its first tick the game removes an NPC map that holds no colonist of ours.</summary>
        internal static bool Held => settlement != null;

        internal static void Show(bool sacked)
        {
            Clear();
            home = Find.AnyPlayerHomeMap;
            CompSettlementLife town = Nearest(home);
            string name = sacked ? "sacked" : "healthy";
            if (town == null)
            {
                Log.Message($"[LivingWorld:ui] town map {name}: no town can be attacked in the tour (skipped)");
                return;
            }
            settlement = town.Settlement;
            baseline = TownMapChecks.PawnBaseline.Take();
            made.Clear();
            speed = Find.TickManager.CurTimeSpeed;
            TownMaps.Last = null;
            TownMaps.LastSkip = null;
            var state = new TownMapChecks.TownState(town);
            try
            {
                if (sacked) state.Sacked(daysAgo: 1f, severity: FullScar, famine: 10, foodShare: 0.1f);
                else state.Healthy();
                map = GetOrGenerateMapUtility.GetOrGenerateMap(settlement.Tile, new IntVec3(150, 1, 150), null);
            }
            finally
            {
                state.Restore();
                map ??= settlement.HasMap ? settlement.Map : null;
            }
            if (map == null) return;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                if (p.Faction == settlement.Faction) made.Add(p);
            map.fogGrid.ClearAllFog();
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            TownMapStats st = TownMaps.Last;
            CellRect rect = st != null ? st.rect : CellRect.CenteredOn(map.Center, 36, 36);
            IntVec3 focus = rect.CenterCell;
            float size = Mathf.Max(14f, rect.Height * 0.55f);
            int damageInView = 0;
            if (sacked && st != null && Worst(st, out IntVec3 worst, out damageInView))
            {
                focus = worst;
                size = CloseSize;
            }
            CameraJumper.TryJump(focus, map, CameraJumper.MovementMode.Cut);
            Find.CameraDriver.SetRootPosAndSize(focus.ToVector3Shifted(), size);
            Log.Message($"[LivingWorld:ui] town map {name}: {settlement.LabelCap} ({town.Tier.defName}), rect {rect.Width}x{rect.Height}, defenders {made.Count}" +
                        (st == null ? ", vanilla (no town map state)" :
                            $", camera {focus.x},{focus.z} size {size:F0} ({damageInView} damage spots in view), scar {st.scar:F2}, destroyed {st.destroyed}, ash {st.ash}, burned {st.burned}, roof holders {st.roofHoldersBefore}/{st.roofHoldersAfter}, " +
                            $"stockpile {st.stockpileValueBefore:F0} -> {st.stockpileValueAfter:F0}, hungry {st.hungry}"));
        }

        /// <summary>The map of the last shot, its pawns and what it left in the world are taken away; the home map is current again and time runs as before.</summary>
        internal static void Clear()
        {
            if (settlement == null) return;
            Settlement s = settlement;
            settlement = null;
            if (home != null && Find.Maps.Contains(home) && Find.CurrentMap != home) Current.Game.CurrentMap = home;
            TownMapChecks.Dispose(map, s, baseline, made);
            Find.TickManager.CurTimeSpeed = speed;
            map = null;
            baseline = null;
            made.Clear();
        }

        /// <summary>
        /// The middle of the most damaged window of the shot's size: the one whose damage spots (weighted: fallen buildings most) add up highest, centred
        /// on the weighted mean of the spots in it so the damage sits in the middle of the frame.
        /// </summary>
        private static bool Worst(TownMapStats st, out IntVec3 centre, out int spotsInView)
        {
            centre = IntVec3.Invalid;
            spotsInView = 0;
            int bestWeight = 0;
            foreach ((IntVec3 candidate, int _) in st.damageSpots)
            {
                int weight = 0, count = 0;
                foreach ((IntVec3 cell, int w) in st.damageSpots)
                {
                    if (Mathf.Abs(cell.x - candidate.x) > CloseHalfWidth || Mathf.Abs(cell.z - candidate.z) > CloseHalfHeight) continue;
                    weight += w;
                    count++;
                }
                if (weight <= bestWeight) continue;
                bestWeight = weight;
                centre = candidate;
                spotsInView = count;
            }
            if (bestWeight == 0) return false;
            long sumX = 0, sumZ = 0, total = 0;
            foreach ((IntVec3 cell, int w) in st.damageSpots)
            {
                if (Mathf.Abs(cell.x - centre.x) > CloseHalfWidth || Mathf.Abs(cell.z - centre.z) > CloseHalfHeight) continue;
                sumX += (long)cell.x * w;
                sumZ += (long)cell.z * w;
                total += w;
            }
            centre = new IntVec3((int)(sumX / total), 0, (int)(sumZ / total));
            return true;
        }

        /// <summary>The nearest town to the colony that an attack could generate a map for without touching anything of the colony's.</summary>
        private static CompSettlementLife Nearest(Map colony)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            CompSettlementLife best = null;
            float bestDistance = float.MaxValue;
            foreach (CompSettlementLife life in comp.activeSettlements)
            {
                Settlement s = life.Settlement;
                Faction owner = s.Faction;
                if (!life.Active || owner == null || owner == Faction.OfEmpire || s.HasMap || SettlementOps.Protected(s) || !TownMaps.Eligible(s) || TownMaps.PlannedPoints(life) == null
                    || comp.roots.Exists(r => r.settlementId == s.ID)) continue;
                float distance = colony == null ? 0f : Find.WorldGrid.ApproxDistanceInTiles(colony.Tile, s.Tile);
                if (distance >= bestDistance) continue;
                best = life;
                bestDistance = distance;
            }
            return best;
        }
    }
}
