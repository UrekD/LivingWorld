using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Seeding and the daily step of a settlement: production, consumption, growth, prosperity, garrison.</summary>
    public static class SettlementEconomy
    {
        /// <summary>Recomputes what the settlement's land and people produce and use (derived, never saved).</summary>
        public static void RecomputeProfile(CompSettlementLife s)
        {
            Settlement st = s.Settlement;
            int n = Commodities.Count;
            if (s.productionPerCapita == null || s.productionPerCapita.Length != n)
            {
                s.productionPerCapita = new float[n];
                s.consumptionPerCapita = new float[n];
            }
            if (s.stock == null || s.stock.Length != n)
            {
                var old = s.stock ?? new float[0];
                s.stock = new float[n];
                System.Array.Copy(old, s.stock, Mathf.Min(old.Length, n));
            }
            Tile tile = st.Tile.Tile;
            BiomeDef biome = tile?.PrimaryBiome;
            TechLevel tech = st.Faction == null ? TechLevel.Neolithic : KnowHow.Effective(st.Faction); // includes what the faction has learned
            float fertility = Mathf.Clamp01((biome?.plantDensity ?? 0.3f) / 0.8f) * Mathf.Clamp01(0.4f + (tile?.rainfall ?? 600f) / 2000f);
            float wildlife = Mathf.Clamp01((biome?.animalDensity ?? 1f) / 5f);
            float hills = tile == null ? 0f : tile.hilliness switch
            {
                Hilliness.SmallHills => 0.33f,
                Hilliness.LargeHills => 0.66f,
                Hilliness.Mountainous => 1f,
                _ => 0f,
            };
            float coast = tile != null && tile.IsCoastal ? 1f : 0f;
            float river = tile is SurfaceTile surf && surf.Rivers != null && surf.Rivers.Count > 0 ? 1f : 0f;
            float bestRatio = 0f;
            s.specialty = null;
            foreach (CommodityDef c in Commodities.All)
            {
                // Drivers are centred on an average tile, so good land produces more than average and poor land less.
                float mult = 1f + c.fertilityWeight * (fertility - 0.5f) + c.wildlifeWeight * (wildlife - 0.5f) + c.hillsWeight * (hills - 0.4f)
                             + c.coastWeight * (coast - 0.3f) + c.riverWeight * (river - 0.3f);
                float prod = c.productionPerCapita * c.ProductionTechFactor(tech) * Mathf.Max(c.essential ? 0.8f : 0.3f, mult);
                float cons = c.consumptionPerCapita * c.ConsumptionTechFactor(tech);
                s.productionPerCapita[c.index] = prod;
                s.consumptionPerCapita[c.index] = cons;
                if (c.specialtyLabel != null && cons > 0f && prod / cons > bestRatio)
                {
                    bestRatio = prod / cons;
                    s.specialty = c;
                }
            }
            if (bestRatio < 1.15f) s.specialty = null;
            // The growing season: plants grow from about 2 °C and are at their best from about 14 °C.
            s.seasonRatio ??= new float[12];
            float sum = 0f;
            for (int q = 0; q < 12; q++)
            {
                float temp = Find.World.tileTemperatures.AverageTemperatureForTwelfth(st.Tile, (Twelfth)q);
                sum += s.seasonRatio[q] = Mathf.Clamp01((temp - 2f) / 12f);
            }
            s.growingMean = sum / 12f;
            float ratioSum = 0f;
            for (int q = 0; q < 12; q++)
                ratioSum += s.seasonRatio[q] = s.growingMean < 0.05f ? 1f : Mathf.Clamp(s.seasonRatio[q] / s.growingMean, 0.25f, 2f);
            s.seasonRatioMean = ratioSum / 12f;
            s.Notify_ProfileChanged();
            // How many people the land and the faction's know-how can carry.
            float habitability = Mathf.Clamp(biome?.settlementSelectionWeight ?? 0.5f, 0.1f, 1f);
            float techCap = tech switch
            {
                TechLevel.Animal or TechLevel.Neolithic => 160f,
                TechLevel.Medieval => 350f,
                TechLevel.Industrial => 600f,
                _ => 800f,
            };
            s.populationCapacity = Mathf.Min(Tuning.T.maxPopulation, techCap * (0.4f + 0.6f * habitability) * (1f + 0.3f * river + 0.2f * coast));
        }

        /// <summary>First-time state for a settlement that has none (new world, old save, or newly founded).</summary>
        public static void Seed(CompSettlementLife s, float? population = null)
        {
            RecomputeProfile(s);
            float pop = population ?? s.populationCapacity * Mathf.Clamp(FoodEquilibrium(s) * Rand.Range(0.6f, 1f), 0.05f, 0.9f);
            s.population = Mathf.Clamp(pop, Tuning.T.minPopulation, s.populationCapacity);
            s.prosperity = Rand.Range(0.35f, 0.7f);
            s.unrest = Rand.Range(0f, 0.15f);
            s.garrison = TargetGarrison(s, null);
            foreach (CommodityDef c in Commodities.All)
                s.stock[c.index] = s.TargetStock(c) * Rand.Range(0.6f, 1.4f);
            s.foundedTicks = SimClock.Now;
            s.originalFactionId = s.Settlement.Faction?.loadID ?? -1;
            s.famineDays = 0;
            s.initialized = true;
            s.schemaVersion = CompSettlementLife.Schema;
        }

        /// <summary>Share of its capacity at which the settlement feeds itself (crowding lowers food output per head).</summary>
        public static float FoodEquilibrium(CompSettlementLife s)
        {
            CommodityDef food = LWDefOf.LW_Food;
            float prod = s.productionPerCapita[food.index], cons = s.consumptionPerCapita[food.index];
            if (prod <= 0f || food.crowdingSensitivity <= 0f) return 0.3f;
            return Mathf.Clamp01(0.5f + (1f - cons / (prod * 0.95f)) / food.crowdingSensitivity);
        }

        public static float TargetGarrison(CompSettlementLife s, FactionState fs)
        {
            float mil = fs?.TraitFactor(t => t.militarization) ?? 1f;
            if (s.Settlement.Faction != null && s.Settlement.Faction.def.permanentEnemy) mil *= 1.3f;
            // An empty treasury cannot pay soldiers.
            if (fs != null && fs.treasury <= 1f) mil *= 0.6f;
            return s.population * s.Tier.garrisonPerCapita * mil;
        }

        /// <summary>One day of life. Returns the tier change (+1 grew, -1 shrank, 0 none) for the chronicle.</summary>
        public static int DailyStep(CompSettlementLife s, FactionState fs, float pace, bool atWar)
        {
            LivingWorldTuningDef t = Tuning.T;
            SettlementTierDef tierBefore = s.Tier;
            float pop = s.population;
            float hungerShare = 0f;
            float growth = fs?.TraitFactor(x => x.growth) ?? 1f;
            float efficiency = 0.88f + 0.3f * s.prosperity - 0.2f * s.unrest;
            float crowding = 0.5f - pop / Mathf.Max(1f, s.populationCapacity);
            if (s.producedDaily == null || s.producedDaily.Length != Commodities.Count) s.producedDaily = new float[Commodities.Count];
            Twelfth now = s.TwelfthNow;
            foreach (CommodityDef c in Commodities.All)
            {
                int i = c.index;
                float prod = s.productionPerCapita[i] * pop * efficiency * Mathf.Max(0.2f, 1f + c.crowdingSensitivity * crowding) * s.SeasonFactor(c, now);
                s.producedDaily[i] = prod;
                float cons = s.consumptionPerCapita[i] * pop * (atWar ? c.warDemandFactor : 1f);
                float st = s.stock[i] + (prod - cons) * pace;
                if (st < 0f)
                {
                    if (c.essential && cons > 0f) hungerShare = Mathf.Max(hungerShare, Mathf.Clamp01(-st / (cons * pace)));
                    st = 0f;
                }
                float cap = cons * c.maxStockDays;
                if (cap > 0f && st > cap) st = Mathf.Lerp(st, cap, 0.25f * pace); // spoilage and waste
                s.stock[i] = st;
            }
            // Famine
            if (hungerShare > 0.05f) s.famineDays++;
            else s.famineDays = 0;
            s.hunger = hungerShare;
            // Prosperity drifts toward a target set by food security, trade, peace and order.
            float foodSec = Mathf.Clamp01(s.Stock(LWDefOf.LW_Food) / s.TargetStock(LWDefOf.LW_Food));
            float trade = Mathf.Clamp01(s.recentTrade / Mathf.Max(1f, pop * 0.5f));
            float target = 0.38f + 0.32f * foodSec + 0.25f * trade - (atWar ? 0.15f : 0f) - 0.25f * s.unrest - 0.4f * hungerShare;
            s.prosperity = Mathf.Clamp01(Mathf.MoveTowards(s.prosperity, Mathf.Clamp01(target), t.prosperityDriftPerDay * pace));
            s.recentTrade *= Mathf.Pow(0.9f, pace);
            if (s.importsDaily != null)
                for (int i = 0; i < s.importsDaily.Length; i++) s.importsDaily[i] *= Mathf.Pow(0.9f, pace);
            // Population: logistic growth when fed and prosperous, decline in famine.
            float room = 1f - pop / Mathf.Max(1f, s.populationCapacity);
            // Growth needs food to spare: the year-round food output (no season) must beat consumption (full rate at a
            // 10 % surplus, none without) and the stores must not be empty. Towns settle where they can feed themselves
            // instead of growing to the edge where every winter starves them.
            CommodityDef food = LWDefOf.LW_Food;
            float foodCons = s.consumptionPerCapita[food.index] * pop;
            float foodYear = s.productionPerCapita[food.index] * pop * efficiency * Mathf.Max(0.2f, 1f + food.crowdingSensitivity * crowding);
            // Food brought in by merchants (and the colony's aid) counts: a town others keep fed can grow.
            if (s.importsDaily != null && food.index < s.importsDaily.Length) foodYear += s.importsDaily[food.index];
            float fed = foodCons <= 0f ? 1f : Mathf.Clamp01((foodYear / foodCons - 1f) / 0.1f) * Mathf.Clamp01(foodSec * 1.5f);
            // Growth and decline are separate non-negative terms (two negatives must not make growth).
            float rate = t.baseGrowthPerDay * growth * Mathf.Max(0f, s.prosperity - 0.25f) * 2f * Mathf.Max(0f, room) * fed;
            s.growthState = hungerShare > 0.05f ? GrowthState.Famine : room <= 0.02f ? GrowthState.Crowded : fed < 0.2f ? GrowthState.FedSize
                : s.prosperity <= 0.3f ? GrowthState.Poor : GrowthState.Growing;
            rate -= t.baseGrowthPerDay * Mathf.Max(0f, -room) * 3f;
            rate -= t.famineDeclinePerDay * Mathf.Max(0f, hungerShare - 0.05f);
            s.population = Mathf.Clamp(pop * (1f + rate * pace), t.minPopulation, t.maxPopulation);
            // Unrest: famine and war raise it, prosperity calms it.
            // Occupied towns resent their new masters for years.
            float occupation = s.Occupied && SimClock.Now - s.occupiedSinceTicks < GenDate.TicksPerYear * 3 ? 0.45f : 0f;
            float unrestTarget = Mathf.Clamp01(0.6f * hungerShare + (atWar ? 0.1f : 0f) + (0.5f - s.prosperity) * 0.3f + occupation);
            if (s.originalFactionId < 0) s.originalFactionId = s.Settlement.Faction?.loadID ?? -1;
            s.unrest = Mathf.Clamp01(Mathf.MoveTowards(s.unrest, unrestTarget, 0.02f * pace));
            // Garrison recovers toward its target (recruiting), or shrinks if over it.
            float gTarget = TargetGarrison(s, fs) * (atWar ? 1.25f : 1f);
            s.garrison = Mathf.MoveTowards(s.garrison, gTarget, Mathf.Max(5f, gTarget * t.garrisonRegenPerDay) * pace);
            SettlementTierDef tierAfter = s.Tier;
            return tierAfter.order.CompareTo(tierBefore.order);
        }
    }
}
