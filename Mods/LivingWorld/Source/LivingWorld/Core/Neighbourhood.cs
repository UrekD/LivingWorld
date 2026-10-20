using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// How far "nearby" reaches for a colony. On a crowded planet the base range of a feature holds; a remote colony's
    /// neighbours are simply farther away, so the range stretches to its few nearest towns (capped).
    /// </summary>
    public static class Neighbourhood
    {
        public const float MaxRadius = 40f;
        private const int Towns = 4;
        private static PlanetTile cachedTile = PlanetTile.Invalid;
        private static int cachedTick = -1;
        private static float cachedReach;
        private static readonly List<float> distances = new List<float>();

        /// <summary>The colony the world deals with: the first player home on the planet's surface (not orbit or space).</summary>
        public static Map Home
        {
            get
            {
                List<Map> maps = Find.Maps;
                if (maps == null) return null;
                foreach (Map m in maps)
                    if (m.IsPlayerHome && m.Tile.Valid && m.Tile.Layer != null && m.Tile.Layer.IsRootSurface) return m;
                return null;
            }
        }

        /// <summary>The feature's base range, stretched to reach the colony's nearest few towns.</summary>
        public static float Radius(PlanetTile home, float baseRange) => Mathf.Max(baseRange, Mathf.Min(MaxRadius, Reach(home)));

        /// <summary>Distance to the colony's few nearest NPC towns (a little beyond the farthest of them).</summary>
        public static float Reach(PlanetTile home)
        {
            int now = Find.TickManager.TicksGame;
            if (home == cachedTile && now - cachedTick < GenDate.TicksPerDay) return cachedReach;
            distances.Clear();
            foreach (Settlement s in Find.WorldObjects.Settlements)
                if (s.Faction != null && !s.Faction.IsPlayer && s.Tile.Layer == home.Layer)
                    distances.Add(Find.WorldGrid.ApproxDistanceInTiles(home, s.Tile));
            distances.Sort();
            cachedReach = distances.Count == 0 ? 0f : distances[Mathf.Min(Towns, distances.Count) - 1] * 1.05f;
            cachedTile = home;
            cachedTick = now;
            return cachedReach;
        }

        public static void Reset()
        {
            cachedTile = PlanetTile.Invalid;
            cachedTick = -1;
        }
    }
}
