using System.Collections.Generic;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// The living state of an NPC settlement: people, wealth, defenders, goods. Attached in code to every
    /// Settlement-class world object def (see <see cref="SettlementCompInjector"/>); authoritative for its settlement.
    /// Seeded once after load/creation (initialized flag), stepped daily by <see cref="SettlementEconomy"/>.
    /// </summary>
    /// <summary>What limits a settlement's population right now.</summary>
    public enum GrowthState : byte { Unknown, Growing, FedSize, Crowded, Poor, Famine }

    public class CompSettlementLife : WorldObjectComp
    {
        public const int Schema = 1;

        public bool initialized;
        public int schemaVersion;
        public float population;
        /// <summary>0..1: wealth and well-being.</summary>
        public float prosperity = 0.5f;
        /// <summary>Defender strength in raid points.</summary>
        public float garrison;
        /// <summary>0..1: discontent (famine, war losses, occupation).</summary>
        public float unrest;
        /// <summary>Goods by commodity index, in value units.</summary>
        public float[] stock = new float[0];
        public int foundedTicks = -1;
        public int famineDays;
        /// <summary>Share of food needs that went unmet yesterday (0..1).</summary>
        public float hunger;
        public bool famineAnnounced;
        /// <summary>-1..1: the colony's reputation here, from how its people were treated as guests.</summary>
        public float fame;
        /// <summary>Faction that founded or first held the town (loadID); differs from the owner while occupied.</summary>
        public int originalFactionId = -1;
        public int occupiedSinceTicks = -1;
        /// <summary>Recent trade volume (decays daily), drives prosperity.</summary>
        public float recentTrade;
        public int fairUntilTicks = -1;
        /// <summary>Sim time of the last fair and of the last "fair coming" notice to the colony.</summary>
        public int lastFairTicks = FactionState.Never, fairNoticeTicks = FactionState.Never;
        /// <summary>What the town last heard about its people held by the colony, and when (Prisoners).</summary>
        public CaptiveNews captiveNews;
        public int captiveNewsTick = FactionState.Never;

        /// <summary>The town's yearly fair falls in one of its six best growing twelfths (the warm half year), fixed per town.</summary>
        public Twelfth FairTwelfth => fairTwelfth ??= ComputeFairTwelfth();
        [Unsaved] private Twelfth? fairTwelfth;

        private Twelfth ComputeFairTwelfth()
        {
            int pick = (parent.ID & 0x7fffffff) % 6;
            if (seasonRatio == null) return (Twelfth)((parent.ID & 0x7fffffff) % 12);
            var order = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 };
            order.SortByDescending(q => seasonRatio[q] * 100f - q * 0.01f);
            return (Twelfth)order[pick];
        }

        /// <summary>The growing season changed (profile recomputed): the fair date follows it.</summary>
        public void Notify_ProfileChanged() => fairTwelfth = null;

        /// <summary>Big and prosperous enough to hold its yearly fair.</summary>
        public bool HoldsFairs => Active && Tier.order >= 2 && prosperity > 0.5f;
        /// <summary>Growing conditions per twelfth relative to the yearly mean (clamped 0.25..2), and the mean of those.</summary>
        [Unsaved] public float[] seasonRatio;
        [Unsaved] public float seasonRatioMean = 1f;
        /// <summary>Yearly mean of the raw growing conditions (0..1): low in cold places.</summary>
        [Unsaved] public float growingMean = 1f;
        /// <summary>Why the population moves the way it does (set daily, shown on the settlement tab).</summary>
        [Unsaved] public GrowthState growthState;

        /// <summary>Output factor of a seasonal commodity now: above 1 at harvest, below in winter; the yearly mean is exactly 1.</summary>
        public float SeasonFactor(CommodityDef c, Twelfth now)
        {
            if (c.seasonality <= 0f || seasonRatio == null) return 1f;
            return Mathf.Max(0f, 1f + c.seasonality * (seasonRatio[(int)now] - 1f)) / Mathf.Max(0.1f, 1f + c.seasonality * (seasonRatioMean - 1f));
        }

        public Twelfth TwelfthNow => GenDate.Twelfth(SimClock.TicksAbs, Find.WorldGrid.LongLatOf(parent.Tile).x);

        /// <summary>A great fair is on: traders from all around, better prices for visitors.</summary>
        public bool FairOn => fairUntilTicks > Find.TickManager.TicksGame;

        // Derived (not saved): recomputed from tile and faction.
        [Unsaved] public float[] productionPerCapita;
        /// <summary>Recent imports per day (decaying), part of supply in prices.</summary>
        [Unsaved] public float[] importsDaily;
        /// <summary>What the town actually produced yesterday (after crowding and efficiency).</summary>
        [Unsaved] public float[] producedDaily;
        [Unsaved] public float[] consumptionPerCapita;
        [Unsaved] public CommodityDef specialty;
        [Unsaved] public float populationCapacity;
        [Unsaved] private SettlementTierDef tierCache;

        public Settlement Settlement => (Settlement)parent;

        /// <summary>Takes part in the simulation: initialized, on the surface, owned by a simulated faction.</summary>
        public bool Active => initialized && parent.Spawned && IsSimulatable(Settlement);

        public static bool IsSimulatable(Settlement s)
        {
            Faction f = s.Faction;
            return f != null && !f.IsPlayer && s.Tile.Valid && s.Tile.Layer != null && s.Tile.Layer.IsRootSurface && FactionState.IsSimulated(f);
        }

        public SettlementTierDef Tier
        {
            get
            {
                if (tierCache == null || !TierFits(tierCache)) tierCache = TierFor(population);
                return tierCache;
            }
        }

        private bool TierFits(SettlementTierDef t)
        {
            SettlementTierDef next = null;
            foreach (SettlementTierDef d in DefDatabase<SettlementTierDef>.AllDefsListForReading)
                if (d.order == t.order + 1) next = d;
            return population >= t.minPopulation && (next == null || population < next.minPopulation);
        }

        public static SettlementTierDef TierFor(float pop)
        {
            SettlementTierDef best = null;
            foreach (SettlementTierDef d in DefDatabase<SettlementTierDef>.AllDefsListForReading)
                if (pop >= d.minPopulation && (best == null || d.order > best.order)) best = d;
            return best;
        }

        public float Stock(CommodityDef c) => c.index < stock.Length ? stock[c.index] : 0f;

        public float DailyConsumption(CommodityDef c) =>
            consumptionPerCapita == null || c.index >= consumptionPerCapita.Length ? 0f : consumptionPerCapita[c.index] * population;

        /// <summary>Stores the town wants: a seasonal good's reserve grows with the length of its lean season.</summary>
        public float TargetStock(CommodityDef c) =>
            Mathf.Max(1f, DailyConsumption(c) * (c.targetStockDays + 30f * c.seasonality * Mathf.Clamp01(1f - growingMean)));

        public float DailySupply(CommodityDef c)
        {
            if (productionPerCapita == null || c.index >= productionPerCapita.Length) return 0f;
            float imports = importsDaily != null && c.index < importsDaily.Length ? importsDaily[c.index] : 0f;
            float produced = producedDaily != null && c.index < producedDaily.Length ? producedDaily[c.index] : productionPerCapita[c.index] * population;
            return produced + imports;
        }

        /// <summary>
        /// Price multiplier (above 1: short of it, pays more; below 1: plenty, sells cheap): the flow of supply against
        /// demand, adjusted by how full the stores are.
        /// </summary>
        public float PriceFactor(CommodityDef c)
        {
            float cons = DailyConsumption(c);
            if (cons <= 0f) return 1f;
            float flow = Mathf.Pow(cons / Mathf.Max(DailySupply(c), cons * 0.2f), c.elasticity);
            float target = TargetStock(c);
            float stores = Mathf.Pow(target / Mathf.Max(Stock(c), target * 0.2f), c.elasticity * 0.3f);
            return Mathf.Clamp(flow * stores, Tuning.T.minPriceFactor, Tuning.T.maxPriceFactor);
        }

        public void AddImport(CommodityDef c, float amount)
        {
            if (importsDaily == null || importsDaily.Length != Commodities.Count) importsDaily = new float[Commodities.Count];
            importsDaily[c.index] += amount / 10f;
        }

        public bool Occupied => originalFactionId >= 0 && parent.Faction != null && parent.Faction.loadID != originalFactionId;

        public override string CompInspectStringExtra()
        {
            if (!Active) return null;
            var sb = new StringBuilder();
            sb.Append("LW_Inspect_Settlement".Translate(Tier.LabelCap, Mathf.RoundToInt(population), ProsperityLabel(prosperity)));
            if (specialty != null) sb.Append(" · ").Append(specialty.specialtyLabel ?? specialty.label);
            CommodityDef shortest = null;
            float worst = 1.3f;
            foreach (CommodityDef c in Commodities.All)
            {
                float f = PriceFactor(c);
                if (f > worst)
                {
                    worst = f;
                    shortest = c;
                }
            }
            if (Occupied && LivingWorldComponent.Instance.FactionById(originalFactionId) is Faction orig)
                sb.Append("\n").Append("LW_Inspect_Occupied".Translate(orig.Name));
            if (famineDays > 0) sb.Append("\n").Append("LW_Inspect_Famine".Translate(famineDays));
            if (unrest >= 0.3f) sb.Append("\n").Append("LW_Inspect_Restless".Translate(unrest.ToStringPercent()));
            if (FairOn) sb.Append("\n").Append("LW_Inspect_Fair".Translate((fairUntilTicks - Find.TickManager.TicksGame).ToStringTicksToPeriod()));
            AidRequest aid = AidRequests.ActiveFor(parent.ID);
            if (aid != null) sb.Append("\n").Append("LW_Inspect_Aid".Translate(aid.commodity.label, Mathf.RoundToInt(aid.wanted - aid.delivered), (aid.deadlineTicks - Find.TickManager.TicksGame).ToStringTicksToPeriod()));
            else if (shortest != null) sb.Append("\n").Append("LW_Inspect_ShortOf".Translate(shortest.label, worst.ToStringByStyle(ToStringStyle.FloatTwo)));
            if (Prefs.DevMode) sb.Append($"\n[dev] garrison {garrison:F0} unrest {unrest:P0} trade {recentTrade:F0}");
            return sb.ToString();
        }

        public static string ProsperityLabel(float p) =>
            (p < 0.2f ? "LW_Prosperity_Destitute" : p < 0.4f ? "LW_Prosperity_Poor" : p < 0.6f ? "LW_Prosperity_Modest" : p < 0.8f ? "LW_Prosperity_Prosperous" : "LW_Prosperity_Thriving").Translate();

        public override void PostExposeData()
        {
            Scribe_Values.Look(ref initialized, "lw_initialized");
            Scribe_Values.Look(ref schemaVersion, "lw_schema");
            Scribe_Values.Look(ref population, "lw_population");
            Scribe_Values.Look(ref prosperity, "lw_prosperity", 0.5f);
            Scribe_Values.Look(ref garrison, "lw_garrison");
            Scribe_Values.Look(ref unrest, "lw_unrest");
            Scribe_Values.Look(ref foundedTicks, "lw_founded", -1);
            Scribe_Values.Look(ref famineDays, "lw_famineDays");
            Scribe_Values.Look(ref hunger, "lw_hunger");
            Scribe_Values.Look(ref famineAnnounced, "lw_famineAnnounced");
            Scribe_Values.Look(ref fame, "lw_fame");
            Scribe_Values.Look(ref originalFactionId, "lw_originalFaction", -1);
            Scribe_Values.Look(ref occupiedSinceTicks, "lw_occupiedSince", -1);
            Scribe_Values.Look(ref recentTrade, "lw_recentTrade");
            Scribe_Values.Look(ref fairUntilTicks, "lw_fairUntil", -1);
            Scribe_Values.Look(ref lastFairTicks, "lw_lastFair", FactionState.Never);
            Scribe_Values.Look(ref fairNoticeTicks, "lw_fairNotice", FactionState.Never);
            Scribe_Values.Look(ref captiveNews, "lw_captiveNews");
            Scribe_Values.Look(ref captiveNewsTick, "lw_captiveNewsTick", FactionState.Never);
            // Stock by commodity defName, so adding/removing commodity defs keeps saves loadable.
            CommodityScribe.Look(ref stock, "lw_stock");
        }
    }

    /// <summary>Adds <see cref="CompSettlementLife"/> to every Settlement-class world object def (incl. modded ones).</summary>
    public static class SettlementCompInjector
    {
        public static void Inject()
        {
            int n = 0;
            foreach (WorldObjectDef def in DefDatabase<WorldObjectDef>.AllDefsListForReading)
            {
                if (def.worldObjectClass == null || !typeof(Settlement).IsAssignableFrom(def.worldObjectClass)) continue;
                def.comps ??= new List<WorldObjectCompProperties>();
                if (def.comps.Exists(p => p.compClass == typeof(CompSettlementLife))) continue;
                def.comps.Add(new WorldObjectCompProperties { compClass = typeof(CompSettlementLife) });
                // The "Living world" inspect tab (defs resolved their tabs already: add to both lists).
                def.inspectorTabs ??= new List<System.Type>();
                def.inspectorTabsResolved ??= new List<InspectTabBase>();
                if (!def.inspectorTabs.Contains(typeof(WITab_SettlementLife)))
                {
                    def.inspectorTabs.Add(typeof(WITab_SettlementLife));
                    def.inspectorTabsResolved.Add(InspectTabManager.GetSharedInstance(typeof(WITab_SettlementLife)));
                }
                n++;
            }
            LWLog.Message($"settlement comp attached to {n} world object defs");
        }
    }
}
