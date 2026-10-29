using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// How the territory tint layer looks in a mode: per source whether its land is drawn and in which fill and edge colours, and
    /// what the band along a border looks like against a given neighbour. Pure presentation: nothing here is read by the sim.
    /// </summary>
    internal interface ITerritoryPaint
    {
        /// <summary>False: the source's land is not drawn in this mode. <paramref name="group"/> decides where edge bands appear (where the neighbour's group differs).</summary>
        bool Paint(int sourceIndex, in TerritorySource src, out int group, out Color32 fill, out Color32 edge, out float edgeWidth);

        /// <summary>The band between a tile and a neighbour of another group: by default the tile's own edge colour and width.</summary>
        Color32 EdgeBetween(int group, Color32 ownEdge, float ownWidth, int neighbourGroup, out float width);
    }

    /// <summary>The territory picture as it has always been: the faction colour as a translucent fill, a stronger band along each border.</summary>
    internal sealed class BordersPaint : ITerritoryPaint
    {
        public static readonly BordersPaint Instance = new BordersPaint();

        public const float FillAlpha = 0.18f, EdgeAlpha = 0.7f, EdgeWidth = 0.2f;

        public bool Paint(int sourceIndex, in TerritorySource src, out int group, out Color32 fill, out Color32 edge, out float edgeWidth)
        {
            group = src.factionId;
            fill = Tint(src.color, 1f, FillAlpha);
            edge = Tint(src.color, 0.85f, EdgeAlpha);
            edgeWidth = EdgeWidth;
            return true;
        }

        public Color32 EdgeBetween(int group, Color32 ownEdge, float ownWidth, int neighbourGroup, out float width)
        {
            width = ownWidth;
            return ownEdge;
        }

        internal static Color32 Tint(Color c, float shade, float alpha) =>
            new Color32((byte)Mathf.RoundToInt(c.r * shade * 255f), (byte)Mathf.RoundToInt(c.g * shade * 255f), (byte)Mathf.RoundToInt(c.b * shade * 255f), (byte)Mathf.RoundToInt(alpha * 255f));
    }

    /// <summary>
    /// War fronts: the faction colours dimmed (a little less so for a faction that is at war), and a red band, a little wider, along every border
    /// between two factions that are at war with each other (<see cref="WorldOverlay.AtWar"/>: real <see cref="War"/> records only, a pirate's standing
    /// hostility is not a front). Settlements of every faction are drawn, the colonies and sites faint like the rest.
    /// </summary>
    internal sealed class WarPaint : ITerritoryPaint
    {
        public static readonly WarPaint Instance = new WarPaint();

        public const float FillAtWarAlpha = 0.14f, FillAlpha = 0.05f, EdgeAlpha = 0.3f, EdgeShade = 0.85f, EdgeWidth = 0.2f, FrontWidth = 0.45f;

        /// <summary>The band along a front: a bright red (255,40,35), opaque, 0.45 of the way to each tile's centre so it reads at the playing zoom.</summary>
        public static readonly Color32 FrontColour = new Color32(255, 40, 35, 255);

        public bool Paint(int sourceIndex, in TerritorySource src, out int group, out Color32 fill, out Color32 edge, out float edgeWidth)
        {
            group = src.factionId;
            fill = BordersPaint.Tint(src.color, 1f, WorldOverlay.InWar(src.factionId) ? FillAtWarAlpha : FillAlpha);
            edge = BordersPaint.Tint(src.color, EdgeShade, EdgeAlpha);
            edgeWidth = EdgeWidth;
            return true;
        }

        public Color32 EdgeBetween(int group, Color32 ownEdge, float ownWidth, int neighbourGroup, out float width) =>
            EdgeBetween(group, ownEdge, ownWidth, neighbourGroup, out width, out _);

        /// <summary>The band, and whether it is a front (the two groups are at war): what the layer counts, from the one lookup that decides the band.</summary>
        public Color32 EdgeBetween(int group, Color32 ownEdge, float ownWidth, int neighbourGroup, out float width, out bool front)
        {
            front = WorldOverlay.AtWar(group, neighbourGroup);
            if (front)
            {
                width = FrontWidth;
                return FrontColour;
            }
            width = ownWidth;
            return ownEdge;
        }
    }

    /// <summary>
    /// A heatmap: each simulated town's land in the colour of one band of a value (prosperity, a price factor), the faction colour only in the
    /// band along its border. Player colonies, sites and settlements outside the simulation are not drawn. A town's band comes from the value with
    /// hysteresis: the band it showed last is kept until the value is past the band's edge by the mode's margin, so a town sitting at an edge does
    /// not flicker or dirty its region every day. The last band of each town is kept in a table that is not saved.
    /// </summary>
    internal abstract class HeatPaint : ITerritoryPaint
    {
        public const float FillAlpha = 0.32f, EdgeAlpha = 0.35f, EdgeShade = 0.85f, EdgeWidth = 0.2f;
        private const float Eps = 1e-5f;

        private readonly Dictionary<int, int> buckets;

        protected HeatPaint(Dictionary<int, int> buckets) => this.buckets = buckets;

        public abstract int BucketCount { get; }

        public abstract Color32 FillOf(int bucket);

        public abstract string NameOf(int bucket);

        /// <summary>The town's band; <paramref name="previous"/> is the band it showed before, -1 for none (a fresh start).</summary>
        protected abstract int Compute(CompSettlementLife life, int previous);

        /// <summary>The town's band for the value, from the band it had (-1: none, so the plain band of the value). Moves up when the value is at least the margin above an edge, down when at least the margin below one.</summary>
        internal static int Hysteresis(float value, float[] edges, int current, float margin)
        {
            int n = edges.Length, c = current;
            if (c < 0 || c > n)
            {
                c = 0;
                while (c < n && value >= edges[c]) c++;
                return c;
            }
            while (c < n && value >= edges[c] + margin - Eps) c++;
            while (c > 0 && value <= edges[c - 1] - margin + Eps) c--;
            return c;
        }

        public bool Paint(int sourceIndex, in TerritorySource src, out int group, out Color32 fill, out Color32 edge, out float edgeWidth)
        {
            group = src.factionId;
            fill = edge = default;
            edgeWidth = 0f;
            if (src.player || src.obj == null) return false;
            CompSettlementLife life = src.obj.GetComponent<CompSettlementLife>();
            if (life == null || !life.Active) return false;
            bool had = buckets.TryGetValue(src.objectId, out int previous);
            int bucket = Compute(life, had ? previous : -1);
            if (!had || bucket != previous)
            {
                buckets[src.objectId] = bucket;
                if (had) WorldOverlay.NoteBucketChange();
                else WorldOverlay.NoteBucketSeed();
            }
            fill = FillOf(bucket);
            edge = BordersPaint.Tint(src.color, EdgeShade, EdgeAlpha);
            edgeWidth = EdgeWidth;
            return true;
        }

        public Color32 EdgeBetween(int group, Color32 ownEdge, float ownWidth, int neighbourGroup, out float width)
        {
            width = ownWidth;
            return ownEdge;
        }

        /// <summary>How many of the remembered towns are in each band.</summary>
        internal string Histogram()
        {
            var counts = new int[BucketCount];
            foreach (int b in buckets.Values)
                if ((uint)b < (uint)counts.Length) counts[b]++;
            var sb = new StringBuilder();
            for (int i = 0; i < counts.Length; i++) sb.Append(i > 0 ? " " : "").Append(NameOf(i)).Append('=').Append(counts[i]);
            return sb.ToString();
        }
    }

    /// <summary>Prosperity in the five bands of the inspect labels (edges 0.2/0.4/0.6/0.8), and famine as a band of its own (exact: a town in famine is always in it).</summary>
    internal sealed class ProsperityPaint : HeatPaint
    {
        public const int Famine = 5;

        public static readonly float[] Edges = { 0.2f, 0.4f, 0.6f, 0.8f };

        /// <summary>Destitute, poor, modest, prosperous, thriving, famine (opaque here; the fill is drawn at <see cref="HeatPaint.FillAlpha"/>).</summary>
        public static readonly Color32[] Palette =
        {
            new Color32(140, 25, 25, 255), new Color32(215, 100, 40, 255), new Color32(230, 205, 65, 255),
            new Color32(140, 205, 65, 255), new Color32(40, 180, 75, 255), new Color32(175, 70, 205, 255), // famine: a light violet, apart from the dark red of destitute
        };

        private static readonly string[] LabelKeys =
        {
            "LW_Prosperity_Destitute", "LW_Prosperity_Poor", "LW_Prosperity_Modest", "LW_Prosperity_Prosperous", "LW_Prosperity_Thriving", "LW_Overlay_Famine",
        };

        public static readonly ProsperityPaint Instance = new ProsperityPaint(WorldOverlay.HeatBuckets);

        internal ProsperityPaint(Dictionary<int, int> buckets) : base(buckets)
        {
        }

        public override int BucketCount => Palette.Length;

        public override Color32 FillOf(int bucket) => new Color32(Palette[bucket].r, Palette[bucket].g, Palette[bucket].b, (byte)Mathf.RoundToInt(FillAlpha * 255f));

        public override string NameOf(int bucket) => LabelKeys[bucket].Translate();

        /// <summary>The band of a town: famine when it has gone hungry, else the prosperity band (leaving famine starts afresh).</summary>
        internal static int BucketOf(float prosperity, int famineDays, int previous, float margin) =>
            famineDays > 0 ? Famine : Hysteresis(prosperity, Edges, previous == Famine ? -1 : previous, margin);

        protected override int Compute(CompSettlementLife life, int previous) =>
            BucketOf(life.prosperity, life.famineDays, previous, Tuning.T.overlayHeatHysteresis);
    }

    /// <summary>The price factor of one good (or, for any good, the consumption-weighted mean over the goods the town consumes, at least 1) from surplus blue to desperate red.</summary>
    internal sealed class ShortagePaint : HeatPaint
    {
        public const int Balanced = 2;

        /// <summary>Surplus up to 0.7, plenty up to 0.9, balanced below 1.15, short below 1.5, scarce below 2.0, desperate from there.</summary>
        public static readonly float[] Edges = { System.MathF.BitIncrement(0.7f), System.MathF.BitIncrement(0.9f), 1.15f, 1.5f, 2.0f };

        /// <summary>Surplus, plenty, balanced (faint), short, scarce, desperate.</summary>
        public static readonly Color32[] Palette =
        {
            new Color32(50, 115, 230, 255), new Color32(115, 180, 240, 255), new Color32(150, 150, 150, 255),
            new Color32(240, 150, 50, 255), new Color32(230, 65, 40, 255), new Color32(140, 15, 25, 255),
        };

        private const float BalancedAlpha = 0.10f;

        private static readonly string[] LabelKeys =
        {
            "LW_Overlay_Surplus", "LW_Overlay_Plenty", "LW_Overlay_Balanced", "LW_Overlay_Short", "LW_Overlay_Scarce", "LW_Overlay_Desperate",
        };

        private static ShortagePaint cached;

        /// <summary>The good, null for any.</summary>
        public readonly CommodityDef commodity;

        internal ShortagePaint(Dictionary<int, int> buckets, CommodityDef commodity) : base(buckets) => this.commodity = commodity;

        /// <summary>The shared paint for a good (null: any), made again when the good changes.</summary>
        public static ShortagePaint For(CommodityDef commodity)
        {
            if (cached == null || cached.commodity != commodity) cached = new ShortagePaint(WorldOverlay.HeatBuckets, commodity);
            return cached;
        }

        public override int BucketCount => Palette.Length;

        public override Color32 FillOf(int bucket) =>
            new Color32(Palette[bucket].r, Palette[bucket].g, Palette[bucket].b, (byte)Mathf.RoundToInt((bucket == Balanced ? BalancedAlpha : FillAlpha) * 255f));

        public override string NameOf(int bucket) => LabelKeys[bucket].Translate();

        /// <summary>The first band the mode can show: any good never shows the blue ones (a town short of nothing is balanced, however well stocked).</summary>
        internal static int FirstBucket(bool any) => any ? Balanced : 0;

        /// <summary>
        /// The price factor of the good for the town; for any good the mean of the price factors of the goods it consumes, each weighted by its share
        /// of the town's consumption (in silver of value), never below 1. One scarce good among many shows up in proportion to how much the town needs it.
        /// </summary>
        internal static float ValueOf(CompSettlementLife life, CommodityDef commodity)
        {
            if (commodity != null) return life.PriceFactor(commodity);
            float total = 0f, weighted = 0f;
            foreach (CommodityDef c in Commodities.All)
            {
                float consumption = life.DailyConsumption(c);
                if (consumption <= 0f) continue;
                total += consumption;
                weighted += consumption * life.PriceFactor(c);
            }
            return total > 0f ? Mathf.Max(1f, weighted / total) : 1f;
        }

        internal static int BucketOf(float priceFactor, int previous, float margin) => Hysteresis(priceFactor, Edges, previous, margin);

        protected override int Compute(CompSettlementLife life, int previous) =>
            Mathf.Max(FirstBucket(commodity == null), BucketOf(ValueOf(life, commodity), previous, Tuning.T.overlayPriceHysteresis));
    }

    /// <summary>
    /// The world map overlay's state: which mode is showing (a global presentation setting, <see cref="LivingWorldSettings.overlayMode"/>),
    /// what each mode needs, and how a change reaches the two draw layers (the territory tint and the marks). Main thread only.
    /// Modes become usable by joining <see cref="ImplementedModes"/>.
    /// </summary>
    public static class WorldOverlay
    {
        // In the order of the menu.
        private static readonly WorldOverlayMode[] implemented = { WorldOverlayMode.Off, WorldOverlayMode.Borders, WorldOverlayMode.WarFronts, WorldOverlayMode.Trade, WorldOverlayMode.Prosperity, WorldOverlayMode.Shortage };

        /// <summary>How many draw layers the last mode change or invalidation marked (tests: a change made outside a running game marks none).</summary>
        internal static int LayerCalls;

        /// <summary>The band each town's land showed last in the heat modes (by world object id), kept for the hysteresis. Not saved; dropped with a mode change and a new world.</summary>
        internal static readonly Dictionary<int, int> HeatBuckets = new Dictionary<int, int>();

        // The same for the prosperity bands while another mode is showing: autotests watch how many towns change band in a day.
        private static readonly Dictionary<int, int> shadowBuckets = new Dictionary<int, int>();
        private static ProsperityPaint shadowPaint;

        /// <summary>
        /// The last pass that counted (<see cref="Notify_DayDone"/>): towns that changed band, how many towns it looked at, how many of those had no band
        /// yet (a seeding pass: they cannot have changed, so they are not in the changes) and the regions the tint layer has marked for redrawing (-1: not a
        /// heat mode) of how many it has. The cost of the daily notification (metrics).
        /// </summary>
        internal static int LastBucketChanges, LastBucketSources, LastBucketSeeded, LastRegionsMarked, LastRegionCount;
        internal static bool LastRegionsFull;
        internal static double LastNotifyMs;
        private static int bucketChanges, bucketSeeded;

        // The fronts: the factions' pairs at war (Diplomacy.Key), and the factions in any of them. Worked out from the wars at the end of each simulated day (and
        // when first needed), so a change is noticed and the picture redrawn; two sets that are swapped so a day's check allocates nothing. Not saved.
        private static HashSet<long> warPairs = new HashSet<long>(), nextPairs = new HashSet<long>();
        private static HashSet<int> warFactions = new HashSet<int>(), nextFactions = new HashSet<int>();
        private static readonly HashSet<long> forcedPairs = new HashSet<long>();
        private static bool warSetValid;

        /// <summary>How many times the war picture has changed since the world began (tests).</summary>
        internal static int LastWarSetChanges;

        private static readonly Dictionary<(WorldOverlayMode, CommodityDef), string> labels = new Dictionary<(WorldOverlayMode, CommodityDef), string>();
        private static readonly Dictionary<(WorldOverlayMode, CommodityDef), string> tooltips = new Dictionary<(WorldOverlayMode, CommodityDef), string>();
        private static LoadedLanguage labelLanguage;

        /// <summary>The mode in effect: the player's, Off on a save prepared for removing the mod.</summary>
        public static WorldOverlayMode Mode => LivingWorldMod.Settings.overlayMode;

        /// <summary>The commodity the shortage overlay shows, null for any (an unknown defName counts as any).</summary>
        public static CommodityDef ShortageCommodity
        {
            get
            {
                string name = LivingWorldMod.Settings.overlayCommodity;
                return name.NullOrEmpty() ? null : DefDatabase<CommodityDef>.GetNamedSilentFail(name);
            }
        }

        public static IReadOnlyList<WorldOverlayMode> ImplementedModes => implemented;

        /// <summary>More than Off and Borders can be chosen: the button opens a menu instead of switching.</summary>
        public static bool UsesMenu => implemented.Length > 2;

        internal static bool IsImplemented(WorldOverlayMode mode) => System.Array.IndexOf(implemented, mode) >= 0;

        /// <summary>How many pairs of factions are at war now (real wars; autotests may add pairs, see <see cref="ForceWarPairForTest"/>).</summary>
        internal static int WarCount
        {
            get
            {
                EnsureWarSet();
                return warPairs.Count;
            }
        }

        /// <summary>The two factions (by load id) are at war: an active <see cref="War"/> between them. A pirate's standing hostility is not.</summary>
        internal static bool AtWar(int factionA, int factionB)
        {
            if (factionA == factionB) return false;
            EnsureWarSet();
            return warPairs.Contains(Diplomacy.Key(factionA, factionB));
        }

        /// <summary>The faction is a side in some war now.</summary>
        internal static bool InWar(int factionId)
        {
            EnsureWarSet();
            return warFactions.Contains(factionId);
        }

        /// <summary>Autotests: counts the pair as at war (or not) without touching diplomacy, and redraws the tint layer when the war picture is the mode showing.</summary>
        internal static void ForceWarPairForTest(int a, int b, bool on)
        {
            long key = Diplomacy.Key(a, b);
            if (on ? !forcedPairs.Add(key) : !forcedPairs.Remove(key)) return;
            if (RefreshWarSet(LivingWorldComponent.Instance) && Mode == WorldOverlayMode.WarFronts) TouchTint();
        }

        private static void EnsureWarSet()
        {
            if (!warSetValid) RefreshWarSet(LivingWorldComponent.Instance);
        }

        /// <summary>Works out the war pairs from the wars (O(wars)); true when they differ from the last time (or there was none).</summary>
        private static bool RefreshWarSet(LivingWorldComponent comp)
        {
            nextPairs.Clear();
            nextFactions.Clear();
            if (comp != null)
                foreach (War w in comp.wars)
                {
                    if (!w.Active) continue;
                    nextPairs.Add(Diplomacy.Key(w.factionA, w.factionB));
                    nextFactions.Add(w.factionA);
                    nextFactions.Add(w.factionB);
                }
            foreach (long key in forcedPairs)
            {
                nextPairs.Add(key);
                nextFactions.Add((int)(key >> 32));
                nextFactions.Add((int)(uint)key);
            }
            bool changed = !warSetValid || !nextPairs.SetEquals(warPairs);
            (warPairs, nextPairs) = (nextPairs, warPairs);
            (warFactions, nextFactions) = (nextFactions, warFactions);
            warSetValid = comp != null && Scribe.mode == LoadSaveMode.Inactive; // not while a save is being read: the wars are not all there yet
            if (changed) LastWarSetChanges++;
            return changed;
        }

        private static bool IsHeat(WorldOverlayMode m) => m == WorldOverlayMode.Prosperity || m == WorldOverlayMode.Shortage;

        /// <summary>The territory tint layer draws in the current mode (the layer also needs a territory snapshot).</summary>
        public static bool TintMode
        {
            get
            {
                WorldOverlayMode m = Mode;
                if (!IsImplemented(m)) return false;
                return m == WorldOverlayMode.Borders || m == WorldOverlayMode.WarFronts || (IsHeat(m) && Blocker(m) == OverlayBlock.None);
            }
        }

        /// <summary>The marks layer (markers, ribbons, lines) draws in the current mode.</summary>
        public static bool MarksMode
        {
            get
            {
                WorldOverlayMode m = Mode;
                return IsImplemented(m) && (m == WorldOverlayMode.WarFronts || m == WorldOverlayMode.Trade) && Blocker(m) == OverlayBlock.None;
            }
        }

        /// <summary>A heatmap is showing: the tint layer draws a value of each town.</summary>
        internal static bool HeatMode => IsHeat(Mode) && TintMode;

        /// <summary>The button is shown (in the world view, in a running game): the component exists and a mode other than Off can draw.</summary>
        public static bool ButtonVisible
        {
            get
            {
                if (LivingWorldComponent.Instance == null) return false;
                foreach (WorldOverlayMode m in implemented)
                    if (m != WorldOverlayMode.Off && Blocker(m) == OverlayBlock.None) return true;
                return false;
            }
        }

        private static void CheckLanguage()
        {
            if (ReferenceEquals(labelLanguage, LanguageDatabase.activeLanguage)) return;
            labels.Clear();
            tooltips.Clear();
            labelLanguage = LanguageDatabase.activeLanguage;
        }

        /// <summary>
        /// The mode's name ("shortage of food"); a mode this build cannot draw reads as off. Cached: the legend and the button ask on every
        /// OnGUI event.
        /// </summary>
        public static string ModeLabel(WorldOverlayMode mode, CommodityDef commodity = null)
        {
            if (!IsImplemented(mode)) mode = WorldOverlayMode.Off;
            if (mode != WorldOverlayMode.Shortage) commodity = null;
            CheckLanguage();
            if (labels.TryGetValue((mode, commodity), out string label)) return label;
            label = commodity != null ? "LW_Overlay_Mode_ShortageOf".Translate(commodity.label).Resolve() : ("LW_Overlay_Mode_" + mode).Translate().Resolve();
            labels[(mode, commodity)] = label;
            return label;
        }

        /// <summary>The button's tooltip, cached like <see cref="ModeLabel"/>.</summary>
        public static string ModeTooltip(WorldOverlayMode mode, CommodityDef commodity = null)
        {
            if (!IsImplemented(mode)) mode = WorldOverlayMode.Off;
            if (mode != WorldOverlayMode.Shortage) commodity = null;
            string label = ModeLabel(mode, commodity); // also checks the language
            if (tooltips.TryGetValue((mode, commodity), out string tip)) return tip;
            tip = "LW_Overlay_Toggle".Translate(label).Resolve();
            if (mode == WorldOverlayMode.Shortage && commodity == null) tip += "\n\n" + "LW_Overlay_Mode_ShortageAnyTip".Translate().Resolve();
            tooltips[(mode, commodity)] = tip;
            return tip;
        }

        /// <summary>What stands in the way of a mode, without a string (the legend asks on every OnGUI event).</summary>
        internal static OverlayBlock Blocker(WorldOverlayMode mode)
        {
            LivingWorldSettings s = LivingWorldMod.Settings;
            if (!ReferenceEquals(s, LivingWorldMod.Stored)) return OverlayBlock.Prepared; // the shared all-off set: every mode is unusable
            switch (mode)
            {
                case WorldOverlayMode.Borders:
                    return s.borders ? OverlayBlock.None : OverlayBlock.NeedsTerritory;
                case WorldOverlayMode.Trade:
                    return s.simulateEconomy ? OverlayBlock.None : OverlayBlock.NeedsEconomy;
                case WorldOverlayMode.Prosperity:
                case WorldOverlayMode.Shortage:
                    if (!s.borders) return OverlayBlock.NeedsTerritory;
                    return s.simulateEconomy ? OverlayBlock.None : OverlayBlock.NeedsEconomy;
                default:
                    return OverlayBlock.None;
            }
        }

        /// <summary>Null when the mode can draw, else the reason (shown on a disabled menu entry).</summary>
        public static string CannotUse(WorldOverlayMode mode)
        {
            switch (Blocker(mode))
            {
                case OverlayBlock.Prepared: return "LW_Overlay_Prepared".Translate();
                case OverlayBlock.NeedsTerritory: return "LW_Overlay_NeedsTerritory".Translate();
                case OverlayBlock.NeedsEconomy: return "LW_Overlay_NeedsEconomy".Translate();
                default: return null;
            }
        }

        /// <summary>The paint for the tint layer in the current mode (the borders picture where a heatmap cannot draw).</summary>
        internal static ITerritoryPaint Paint
        {
            get
            {
                WorldOverlayMode m = Mode;
                if (m == WorldOverlayMode.WarFronts && TintMode) return WarPaint.Instance;
                if (m == WorldOverlayMode.Prosperity && TintMode) return ProsperityPaint.Instance;
                if (m == WorldOverlayMode.Shortage && TintMode) return ShortagePaint.For(ShortageCommodity);
                return BordersPaint.Instance;
            }
        }

        /// <summary>
        /// Chooses the mode (button, settings window, tour): writes the player's settings, and redraws when a game is running. Safe at
        /// the main menu, where there is no world and only the settings change.
        /// </summary>
        public static void SetMode(WorldOverlayMode mode, CommodityDef commodity = null)
        {
            LivingWorldSettings stored = LivingWorldMod.Stored;
            if (stored == null) return;
            ApplyMode(stored, mode, commodity, Current.ProgramState == ProgramState.Playing);
            LoadedModManager.GetMod<LivingWorldMod>().WriteSettings();
        }

        /// <summary>
        /// The part of <see cref="SetMode"/> that tests can run on a scratch settings object: layers are only touched while playing. The shortage
        /// good is set with the shortage mode only (null: any); another mode keeps it, so Shortage of steel, Off, Shortage still means steel.
        /// </summary>
        internal static void ApplyMode(LivingWorldSettings s, WorldOverlayMode mode, CommodityDef commodity, bool playing)
        {
            s.overlayMode = mode;
            if (mode == WorldOverlayMode.Shortage) s.overlayCommodity = commodity?.defName ?? "";
            HeatBuckets.Clear();
            if (playing) TouchLayers();
        }

        /// <summary>The war pairs are worked out again when next asked (the world has just been loaded: whatever was worked out during the load is not to be trusted).</summary>
        internal static void ForgetWarSet() => warSetValid = false;

        /// <summary>Everything is drawn afresh when next visible (a new picture of the whole world: the end of history, a setting the modes depend on).</summary>
        public static void InvalidateAll()
        {
            HeatBuckets.Clear();
            warSetValid = false;
            TouchLayers();
        }

        /// <summary>
        /// The end of each simulated day outside history, after the sim. The war pairs are worked out again: if they changed while the war picture shows, the
        /// tint layer is redrawn in full (a pair's band colour depends on its neighbours). In a heatmap: the towns' bands are worked out again, and if any town
        /// changed band the tint layer is told about the picture (it marks the regions of the tiles that look different, and redraws them when next visible).
        /// While another mode shows, autotests still count how many towns would change prosperity band, as the numbers behind the hysteresis. In a mode
        /// with markers the marks layer is redrawn when what it shows (or the day, as markers fade) changed.
        /// </summary>
        public static void Notify_DayDone(LivingWorldComponent comp)
        {
            LastBucketChanges = LastBucketSources = LastBucketSeeded = 0;
            LastRegionsMarked = -1;
            LastRegionCount = 0;
            LastRegionsFull = false;
            LastNotifyMs = 0d;
            var sw = Stopwatch.StartNew();
            if (RefreshWarSet(comp) && Mode == WorldOverlayMode.WarFronts) TouchTint();
            NotifyHeat();
            NotifyMarks(comp);
            LastNotifyMs = sw.Elapsed.TotalMilliseconds;
        }

        private static void NotifyHeat()
        {
            TerritorySnapshot snap = Territory.Current;
            if (snap == null) return;
            bool heat = HeatMode;
            if (!heat && !Testing.AutoTest.Active) return;
            CountPass(snap, heat);
            Dictionary<int, int> table = heat ? HeatBuckets : shadowBuckets;
            if (table.Count > snap.indexByObject.Count + 64) Prune(table, snap);
            if (heat && Find.World?.grid?.Surface?.GetLayer<WorldDrawLayer_LWBorders>() is WorldDrawLayer_LWBorders tint)
            {
                if ((bucketChanges > 0 || bucketSeeded > 0) && tint.Notify_Published(snap)) tint.SetDirty();
                LastRegionsMarked = tint.DirtyRegions;
                LastRegionCount = tint.RegionCount;
                LastRegionsFull = tint.NeedsFull;
            }
        }

        private static void NotifyMarks(LivingWorldComponent comp)
        {
            if (!MarksMode) return;
            if (Find.World?.grid?.Surface?.GetLayer<WorldDrawLayer_LWOverlayMarks>() is WorldDrawLayer_LWOverlayMarks marks && marks.ContentChanged(comp)) marks.SetDirty();
        }

        /// <summary>One paint pass over the sources of the picture, counting the towns that changed band, the ones that got a band for the first time and those drawn.</summary>
        private static void CountPass(TerritorySnapshot snap, bool heat)
        {
            ITerritoryPaint paint = heat ? Paint : (shadowPaint ??= new ProsperityPaint(shadowBuckets));
            bucketChanges = bucketSeeded = 0;
            int drawn = 0;
            TerritorySource[] sources = snap.sources;
            for (int i = 0; i < sources.Length; i++)
                if (sources[i].faction != null && paint.Paint(i, in sources[i], out _, out _, out _, out _)) drawn++;
            LastBucketChanges = bucketChanges;
            LastBucketSeeded = bucketSeeded;
            LastBucketSources = drawn;
        }

        /// <summary>
        /// Autotests: gives the prosperity bands that are watched while another mode shows a first value (after the world is loaded and the picture built), so
        /// the first day is a real sample of how many towns change band and not a seeding pass. Counts nothing.
        /// </summary>
        internal static void SeedShadow()
        {
            if (!Testing.AutoTest.Active) return;
            TerritorySnapshot snap = Territory.Current;
            if (snap == null || HeatMode) return;
            CountPass(snap, heat: false);
            LastBucketChanges = LastBucketSources = LastBucketSeeded = 0;
            LastRegionsMarked = -1;
        }

        /// <summary>Forgets the towns that no longer hold land (razed, gone).</summary>
        private static void Prune(Dictionary<int, int> table, TerritorySnapshot snap)
        {
            var gone = new List<int>();
            foreach (int id in table.Keys)
                if (!snap.indexByObject.ContainsKey(id)) gone.Add(id);
            foreach (int id in gone) table.Remove(id);
        }

        /// <summary>A town changed band (called by a heat paint when it had one before).</summary>
        internal static void NoteBucketChange() => bucketChanges++;

        /// <summary>A town got its first band (called by a heat paint when it had none): a seed, not a change.</summary>
        internal static void NoteBucketSeed() => bucketSeeded++;

        /// <summary>For the tour and the log: how many towns are in each band of the heatmap showing, null when none is.</summary>
        internal static string HeatSummary() => HeatMode ? ((HeatPaint)Paint).Histogram() : null;

        /// <summary>A new world: nothing from the previous one may leak through statics.</summary>
        public static void Reset()
        {
            LayerCalls = 0;
            HeatBuckets.Clear();
            shadowBuckets.Clear();
            warPairs.Clear();
            warFactions.Clear();
            forcedPairs.Clear();
            warSetValid = false;
            LastWarSetChanges = 0;
            LastBucketChanges = LastBucketSources = LastBucketSeeded = 0;
            LastRegionsMarked = -1;
            LastRegionCount = 0;
            LastRegionsFull = false;
            LastNotifyMs = 0d;
            bucketChanges = bucketSeeded = 0;
        }

        /// <summary>One line of a menu: its text, what it does (null: disabled), and whether it is the current choice.</summary>
        internal readonly struct MenuEntry
        {
            public readonly string label;
            public readonly System.Action action;
            public readonly bool ticked;

            public MenuEntry(string label, System.Action action, bool ticked)
            {
                this.label = label;
                this.action = action;
                this.ticked = ticked;
            }

            public bool Disabled => action == null;
        }

        /// <summary>The menu the button and the settings window open: every implemented mode, the current one ticked, one that cannot draw disabled with its reason. Shortage opens a second menu for the good.</summary>
        internal static List<MenuEntry> MenuEntries()
        {
            var list = new List<MenuEntry>();
            LivingWorldSettings stored = LivingWorldMod.Stored;
            WorldOverlayMode current = stored?.overlayMode ?? WorldOverlayMode.Off;
            foreach (WorldOverlayMode mode in implemented)
            {
                WorldOverlayMode m = mode;
                string label = m == WorldOverlayMode.Shortage ? "LW_Overlay_Menu_Shortage".Translate().Resolve() : ModeLabel(m).CapitalizeFirst();
                bool ticked = m == current;
                if (CannotUse(m) is string why) list.Add(new MenuEntry($"{label} ({why})", null, ticked));
                else if (m == WorldOverlayMode.Shortage) list.Add(new MenuEntry(label, () => Find.WindowStack.Add(new FloatMenu(ShortageMenuOptions())), ticked));
                else list.Add(new MenuEntry(label, () => { if (LivingWorldMod.Stored?.overlayMode != m) SetMode(m); }, ticked));
            }
            return list;
        }

        /// <summary>The second menu: any good, then one entry per commodity.</summary>
        internal static List<MenuEntry> ShortageMenuEntries()
        {
            var list = new List<MenuEntry>();
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool inShortage = stored != null && stored.overlayMode == WorldOverlayMode.Shortage;
            CommodityDef now = inShortage ? ShortageCommodity : null;
            list.Add(new MenuEntry("LW_Overlay_AnyGood".Translate(), () => ChooseShortage(null), inShortage && now == null));
            foreach (CommodityDef c in Commodities.All)
            {
                CommodityDef good = c;
                list.Add(new MenuEntry(good.LabelCap, () => ChooseShortage(good), inShortage && now == good));
            }
            return list;
        }

        internal static List<FloatMenuOption> MenuOptions() => ToOptions(MenuEntries());

        internal static List<FloatMenuOption> ShortageMenuOptions() => ToOptions(ShortageMenuEntries());

        private static void ChooseShortage(CommodityDef good)
        {
            LivingWorldSettings stored = LivingWorldMod.Stored;
            if (stored != null && stored.overlayMode == WorldOverlayMode.Shortage && ShortageCommodity == good) return;
            SetMode(WorldOverlayMode.Shortage, good);
        }

        /// <summary>The entries as menu options, the current one with a check mark at its right end (a drawn box, not a glyph the fonts may lack), greyed when the entry cannot be used.</summary>
        private static List<FloatMenuOption> ToOptions(List<MenuEntry> entries)
        {
            var list = new List<FloatMenuOption>(entries.Count);
            foreach (MenuEntry e in entries)
            {
                if (!e.ticked)
                {
                    list.Add(new FloatMenuOption(e.label, e.action));
                    continue;
                }
                bool disabled = e.Disabled;
                list.Add(new FloatMenuOption(e.label, e.action, extraPartWidth: 24f, extraPartOnGUI: rect =>
                {
                    Widgets.CheckboxDraw(rect.x, rect.y + (rect.height - 24f) / 2f, true, disabled);
                    return false;
                })
                {
                    extraPartRightJustified = true,
                });
            }
            return list;
        }

        /// <summary>The tint layer is redrawn in full when next visible (null-safe: no world, no layer).</summary>
        private static void TouchTint()
        {
            WorldDrawLayer_LWBorders tint = Find.World?.grid?.Surface?.GetLayer<WorldDrawLayer_LWBorders>();
            if (tint == null) return;
            tint.Invalidate();
            LayerCalls++;
        }

        private static void TouchLayers()
        {
            PlanetLayer surface = Find.World?.grid?.Surface;
            if (surface == null) return;
            WorldDrawLayer_LWBorders tint = surface.GetLayer<WorldDrawLayer_LWBorders>();
            if (tint != null)
            {
                tint.Invalidate();
                LayerCalls++;
            }
            WorldDrawLayer_LWOverlayMarks marks = surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            if (marks != null)
            {
                marks.SetDirty();
                LayerCalls++;
            }
        }
    }

    /// <summary>Why a mode cannot draw (the reason's text is made only when someone shows it).</summary>
    internal enum OverlayBlock : byte { None, Prepared, NeedsTerritory, NeedsEconomy }
}
