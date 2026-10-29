using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// The goods in a tribute payment: a town's surplus of a commodity made into things. The plan is fixed by the commodity, the payer's technology and
    /// the value (the same thing, its default stuff, normal quality), so what is debited from the town's stock is exactly what arrives.
    /// </summary>
    public static class TributeGoods
    {
        public struct GoodsPlan
        {
            public ThingDef def;
            public ThingDef stuff;
            public int count;
            /// <summary>Market value of the things planned (at most the value asked for).</summary>
            public float madeValue;

            public bool Any => def != null && count > 0;
        }

        /// <summary>
        /// The things worth at most <paramref name="value"/> silver of a commodity: of its commission goods the most advanced one the payer's technology
        /// can make (the first listed on a tie), else the least advanced. Nothing when the value does not reach one item (or the commodity has no goods).
        /// </summary>
        public static GoodsPlan Plan(CommodityDef c, float value, TechLevel tech)
        {
            var none = new GoodsPlan();
            if (c == null || c.commissionGoods.NullOrEmpty() || value <= 0f) return none;
            ThingDef best = null, lowest = null;
            foreach (CommissionGood g in c.commissionGoods)
            {
                ThingDef d = g.thing;
                if (d == null) continue;
                if (lowest == null || d.techLevel < lowest.techLevel) lowest = d;
                if (d.techLevel <= tech && (best == null || d.techLevel > best.techLevel)) best = d;
            }
            ThingDef def = best ?? lowest;
            if (def == null) return none;
            ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
            float each = def.GetStatValueAbstract(StatDefOf.MarketValue, stuff);
            if (each <= 0f) return none;
            // A hair over, so a value made of whole items (what a payment carries) gives back all of them.
            int count = Mathf.FloorToInt(value / each + 0.001f);
            if (count <= 0) return none;
            return new GoodsPlan { def = def, stuff = stuff, count = count, madeValue = count * each };
        }

        /// <summary>The things of a plan, in stacks, at normal quality.</summary>
        public static List<Thing> Make(GoodsPlan plan)
        {
            var things = new List<Thing>();
            if (!plan.Any) return things;
            int left = plan.count;
            while (left > 0)
            {
                Thing t = ThingMaker.MakeThing(plan.def, plan.stuff);
                t.stackCount = Mathf.Min(left, Mathf.Max(1, plan.def.stackLimit));
                left -= t.stackCount;
                t.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Normal, ArtGenerationContext.Outsider);
                things.Add(t);
            }
            return things;
        }
    }
}
