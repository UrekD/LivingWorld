using System.Collections.Generic;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Settlements bucketed by position on the planet (3D grid over tile centres), so "who is near" queries look at a
    /// few buckets instead of every settlement. Rebuilt with the daily indexes.
    /// </summary>
    public class SpatialIndex
    {
        public const float RangeTiles = 18f;
        private readonly Dictionary<long, List<CompSettlementLife>> buckets = new Dictionary<long, List<CompSettlementLife>>();
        private float cell = 1f;

        private static long Key(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (uint)(z & 0x1FFFFF);

        public void Rebuild(List<CompSettlementLife> settlements)
        {
            foreach (List<CompSettlementLife> b in buckets.Values) b.Clear();
            cell = Mathf.Max(0.01f, RangeTiles * Find.WorldGrid.AverageTileSize);
            foreach (CompSettlementLife s in settlements)
            {
                Vector3 p = Find.WorldGrid.GetTileCenter(s.Settlement.Tile);
                long k = Key(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));
                if (!buckets.TryGetValue(k, out List<CompSettlementLife> list)) buckets[k] = list = new List<CompSettlementLife>();
                list.Add(s);
            }
        }

        /// <summary>Settlements within maxTiles (≤ RangeTiles) of a tile, into results (cleared first).</summary>
        public void Near(PlanetTile tile, float maxTiles, List<CompSettlementLife> results)
        {
            results.Clear();
            Vector3 p = Find.WorldGrid.GetTileCenter(tile);
            int cx = Mathf.FloorToInt(p.x / cell), cy = Mathf.FloorToInt(p.y / cell), cz = Mathf.FloorToInt(p.z / cell);
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int y = cy - 1; y <= cy + 1; y++)
                    for (int z = cz - 1; z <= cz + 1; z++)
                        if (buckets.TryGetValue(Key(x, y, z), out List<CompSettlementLife> list))
                            foreach (CompSettlementLife s in list)
                                if (Find.WorldGrid.ApproxDistanceInTiles(tile, s.Settlement.Tile) <= maxTiles) results.Add(s);
        }
    }
}
