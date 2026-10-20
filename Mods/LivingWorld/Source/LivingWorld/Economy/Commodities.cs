using System.Collections.Generic;
using RimWorld;
using Verse;

namespace LivingWorld
{
    /// <summary>Commodity registry: def indices and the ThingDef → commodity mapping used by trade.</summary>
    public static class Commodities
    {
        public static List<CommodityDef> All { get; private set; } = new List<CommodityDef>();
        public static int Count => All.Count;
        private static readonly Dictionary<ThingDef, CommodityDef> byThing = new Dictionary<ThingDef, CommodityDef>();

        /// <summary>Called once at startup (after defs load).</summary>
        public static void Init()
        {
            // Def.index is each def's position in its DefDatabase list: usable as an array index.
            All = DefDatabase<CommodityDef>.AllDefsListForReading;
            byThing.Clear();
        }

        /// <summary>The commodity a thing trades as, or null (silver, corpses, buildings, unknown items).</summary>
        public static CommodityDef Of(ThingDef def)
        {
            if (def == null || def == ThingDefOf.Silver) return null;
            if (byThing.TryGetValue(def, out CommodityDef c)) return c;
            c = Resolve(def);
            byThing[def] = c;
            return c;
        }

        private static CommodityDef Resolve(ThingDef def)
        {
            // Explicit thing lists win over categories (gold is a luxury although it is a raw resource).
            foreach (CommodityDef c in All)
                if (c.thingDefs.Contains(def)) return c;
            if (def.thingCategories == null) return null;
            // The most specific category wins: walk up from each of the thing's categories.
            foreach (ThingCategoryDef cat in def.thingCategories)
                for (ThingCategoryDef k = cat; k != null; k = k.parent)
                    foreach (CommodityDef c in All)
                        if (c.thingCategories.Contains(k)) return c;
            return null;
        }
    }
}
