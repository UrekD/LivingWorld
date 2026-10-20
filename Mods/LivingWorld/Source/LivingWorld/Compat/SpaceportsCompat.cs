using RimWorld;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Soft integration with Rimsential Spaceports (zal.spaceports): when a colony runs a spaceport (landing pad and
    /// beacon), visitors and traders of spacefaring factions fly in by shuttle, so they don't also walk across the
    /// world map. Detection by defName only, no types.
    /// </summary>
    public static class SpaceportsCompat
    {
        private static bool? active;

        public static bool Active => active ??= ModsConfig.IsActive("zal.spaceports");

        /// <summary>Would Spaceports bring this faction's group by shuttle to this map?</summary>
        public static bool WouldFly(Map map, Faction faction)
        {
            if (!Active || map == null || faction == null || faction.def.techLevel <= TechLevel.Neolithic) return false;
            bool pad = false, beacon = false;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (b.def.defName == "Spaceports_ShuttleLandingPad") pad = true;
                else if (b.def.defName == "Spaceports_Beacon") beacon = true;
                if (pad && beacon) return true;
            }
            return false;
        }
    }
}
