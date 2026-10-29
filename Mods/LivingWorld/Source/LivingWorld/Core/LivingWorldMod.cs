using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    public enum LetterPolicy : byte { Off, Digest, All }

    /// <summary>What the world map overlay shows (one at a time). Saved as an int, so the order is a format: append only.</summary>
    public enum WorldOverlayMode : byte { Off, Borders, WarFronts, Trade, Prosperity, Shortage }

    public class LivingWorldSettings : ModSettings
    {
        // Subsystems
        public bool simulateEconomy = true;
        public bool simulateDiplomacy = true;
        public bool simulateWarfare = true;
        public bool allowConquest = true;
        public bool allowExpansion = true;
        /// <summary>After winning an assault the colony may hold the town as a free town instead of razing it (Story/Tributaries.cs).</summary>
        public bool tributaries = true;
        /// <summary>The colony may pay a faction to send a war band against a town of a common enemy (Story/HiredBands.cs); needs diplomacy, parties and warfare too.</summary>
        public bool hireWarBands = true;
        public bool worldParties = true;
        public bool raidsAsWarBands = true;
        public bool marketPrices = true;
        public bool figures = true;
        public bool borders = true;
        public bool roads = true;
        /// <summary>Maps of NPC towns are generated from the town's state (Settlements/TownMaps.cs).</summary>
        public bool townMaps = true;
        // Presentation
        public LetterPolicy letterPolicy = LetterPolicy.Digest;
        public int digestDays = 5;
        public bool showBorders = true;
        /// <summary>The overlay mode as saved ("overlayMode"): -1 on load means the file predates it (see <see cref="MigrateOverlayMode"/>).</summary>
        public int overlayModeRaw = (int)WorldOverlayMode.Borders;
        /// <summary>The commodity the shortage overlay shows, by defName ("" or an unknown one: any).</summary>
        public string overlayCommodity = "";
        // Scale
        public int historyYears = 2;
        public int maxParties = 40;
        public float pace = 1f;
        // Travel
        public float warBandMaxHours = 48f;
        public float merchantMaxHours = 48f;
        // Diagnostics
        public bool perfLog;

        /// <summary>The overlay mode, clamped to the enum.</summary>
        public WorldOverlayMode overlayMode
        {
            get => (WorldOverlayMode)Mathf.Clamp(overlayModeRaw, 0, (int)WorldOverlayMode.Shortage);
            set => overlayModeRaw = (int)value;
        }

        /// <summary>A settings file from before the overlay modes has no mode (-1): the old checkbox decides. A value outside the enum is treated the same.</summary>
        internal static int MigrateOverlayMode(int raw, bool showBorders) =>
            raw >= 0 && raw <= (int)WorldOverlayMode.Shortage ? raw : (int)(showBorders ? WorldOverlayMode.Borders : WorldOverlayMode.Off);

        public override void ExposeData()
        {
            Scribe_Values.Look(ref simulateEconomy, "simulateEconomy", true);
            Scribe_Values.Look(ref simulateDiplomacy, "simulateDiplomacy", true);
            Scribe_Values.Look(ref simulateWarfare, "simulateWarfare", true);
            Scribe_Values.Look(ref allowConquest, "allowConquest", true);
            Scribe_Values.Look(ref allowExpansion, "allowExpansion", true);
            Scribe_Values.Look(ref tributaries, "tributaries", true);
            Scribe_Values.Look(ref hireWarBands, "hireWarBands", true);
            Scribe_Values.Look(ref worldParties, "worldParties", true);
            Scribe_Values.Look(ref raidsAsWarBands, "raidsAsWarBands", true);
            Scribe_Values.Look(ref marketPrices, "marketPrices", true);
            Scribe_Values.Look(ref figures, "figures", true);
            Scribe_Values.Look(ref borders, "borders", true);
            Scribe_Values.Look(ref roads, "roads", true);
            Scribe_Values.Look(ref townMaps, "townMaps", true);
            Scribe_Values.Look(ref letterPolicy, "letterPolicy", LetterPolicy.Digest);
            Scribe_Values.Look(ref digestDays, "digestDays", 5);
            // Kept in step for older builds. Scribe_Values writes it only when false: an older build reads a missing one as true.
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                overlayModeRaw = (int)overlayMode;
                showBorders = overlayMode != WorldOverlayMode.Off;
            }
            Scribe_Values.Look(ref showBorders, "showBorders", true);
            Scribe_Values.Look(ref overlayModeRaw, "overlayMode", -1, forceSave: true);
            Scribe_Values.Look(ref overlayCommodity, "overlayCommodity", "");
            if (Scribe.mode == LoadSaveMode.LoadingVars) overlayModeRaw = MigrateOverlayMode(overlayModeRaw, showBorders);
            Scribe_Values.Look(ref historyYears, "historyYears", 2);
            Scribe_Values.Look(ref maxParties, "maxParties", 40);
            Scribe_Values.Look(ref pace, "pace", 1f);
            Scribe_Values.Look(ref warBandMaxHours, "warBandMaxHours", 48f);
            Scribe_Values.Look(ref merchantMaxHours, "merchantMaxHours", 48f);
            Scribe_Values.Look(ref perfLog, "perfLog");
        }
    }

    public class LivingWorldMod : Mod
    {
        private static LivingWorldSettings stored;

        /// <summary>Everything switched off: what a save prepared for removing the mod runs with.</summary>
        private static readonly LivingWorldSettings Off = new LivingWorldSettings
        {
            simulateEconomy = false, simulateDiplomacy = false, simulateWarfare = false, allowConquest = false, allowExpansion = false, tributaries = false, hireWarBands = false,
            worldParties = false, raidsAsWarBands = false, marketPrices = false, figures = false, borders = false, roads = false, townMaps = false, showBorders = false, letterPolicy = LetterPolicy.Off,
            overlayModeRaw = (int)WorldOverlayMode.Off,
        };

        /// <summary>The player's own settings (write through this; <see cref="Settings"/> may be the shared all-off set).</summary>
        public static LivingWorldSettings Stored => stored;

        /// <summary>
        /// The settings in effect: the player's, unless the current save was prepared for removing the mod. This holds from the moment a world is being
        /// loaded or played (maps are generated in MapInitializing too), not at the main menu; the component must belong to the current world, so
        /// one left over from a previous game never switches anything off.
        /// </summary>
        public static LivingWorldSettings Settings => PreparedForRemoval ? Off : stored;

        private static bool PreparedForRemoval =>
            Current.ProgramState != ProgramState.Entry && LivingWorldComponent.Instance is { } c && c.world == Find.World && c.preparedForRemoval;

        public LivingWorldMod(ModContentPack content) : base(content)
        {
            stored = GetSettings<LivingWorldSettings>();
        }

        public override string SettingsCategory() => "Living World";

        /// <summary>Starts the performance log (from the setting at startup, or when ticked in the settings).</summary>
        public static void StartPerfLog()
        {
            PerfLog.Enable(PerfLog.DefaultTargets, toFile: true, warnMissing: false);
            Messages.Message("LW_Settings_PerfLogStarted".Translate(PerfLog.FilePath), MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>
        /// Leaves a save that loads cleanly without the mod: world parties (our own world object class) and our letters
        /// are removed, and this save runs with every subsystem off (other saves keep the player's settings).
        /// Settlement data lives in fields the game ignores without the mod.
        /// </summary>
        public static void PrepareForRemoval()
        {
            Tributaries.Notify_PartiesLeaving(); // tribute on the road was paid already: it is set down at the colony before the parties go
            int n = 0;
            // Before the parties go: contracts under way end here, not as a band or an envoy lost on the road.
            HiredBands.Notify_PreparedForRemoval(LivingWorldComponent.Instance);
            foreach (WorldParty p in WorldParty.All.ToArray())
            {
                p.Destroy();
                n++;
            }
            // Letters of our own class would not load without the mod (the archive keeps them too).
            // Every letter class of this mod (tribute, ransom, audience, plea): marked answered so removing it fires nothing.
            foreach (Letter l in Find.LetterStack.LettersListForReading.ToArray())
                if (l.GetType().Assembly == typeof(LivingWorldMod).Assembly)
                {
                    if (l is ChoiceLetter_TributeDemand t) t.answered = true;
                    if (l is ChoiceLetter_RulerAudience r) r.answered = true;
                    Find.LetterStack.RemoveLetter(l);
                }
            foreach (IArchivable a in Find.Archive.ArchivablesListForReading.ToArray())
                if (a.GetType().Assembly == typeof(LivingWorldMod).Assembly) Find.Archive.Remove(a);
            Tributaries.Notify_PreparedForRemoval(LivingWorldComponent.Instance);
            LivingWorldComponent.Instance.preparedForRemoval = true;
            Territory.Clear();
            Roads.Notify_PreparedForRemoval();
            Messages.Message("LW_Settings_PreparedRemoval".Translate(n), MessageTypeDefOf.TaskCompletion, historical: false);
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            LivingWorldSettings s = stored;
            var list = new Listing_Standard { ColumnWidth = (inRect.width - 34f) / 2f };
            list.Begin(inRect);
            list.Label("LW_Settings_Simulation".Translate());
            bool economy = s.simulateEconomy;
            list.CheckboxLabeled("LW_Settings_Economy".Translate(), ref s.simulateEconomy, "LW_Settings_EconomyDesc".Translate());
            if (s.simulateEconomy != economy && Current.ProgramState == ProgramState.Playing) WorldOverlay.InvalidateAll(); // the heatmaps need it: drawn afresh either way
            list.CheckboxLabeled("LW_Settings_Diplomacy".Translate(), ref s.simulateDiplomacy, "LW_Settings_DiplomacyDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Warfare".Translate(), ref s.simulateWarfare, "LW_Settings_WarfareDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Conquest".Translate(), ref s.allowConquest, "LW_Settings_ConquestDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Tributaries".Translate(), ref s.tributaries, "LW_Settings_TributariesDesc".Translate());
            list.CheckboxLabeled("LW_Settings_HireBands".Translate(), ref s.hireWarBands, "LW_Settings_HireBandsDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Expansion".Translate(), ref s.allowExpansion, "LW_Settings_ExpansionDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Parties".Translate(), ref s.worldParties, "LW_Settings_PartiesDesc".Translate());
            list.CheckboxLabeled("LW_Settings_WarBands".Translate(), ref s.raidsAsWarBands, "LW_Settings_WarBandsDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Markets".Translate(), ref s.marketPrices, "LW_Settings_MarketsDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Figures".Translate(), ref s.figures, "LW_Settings_FiguresDesc".Translate());
            bool borders = s.borders;
            list.CheckboxLabeled("LW_Settings_Borders".Translate(), ref s.borders, "LW_Settings_BordersDesc".Translate());
            if (s.borders != borders) Territory.Notify_BordersSettingChanged();
            bool roads = s.roads;
            list.CheckboxLabeled("LW_Settings_Roads".Translate(), ref s.roads, "LW_Settings_RoadsDesc".Translate());
            if (s.roads != roads) Roads.Notify_RoadsSettingChanged();
            list.CheckboxLabeled("LW_Settings_TownMaps".Translate(), ref s.townMaps, "LW_Settings_TownMapsDesc".Translate());
            list.NewColumn();
            list.Label("LW_Settings_Presentation".Translate());
            if (list.ButtonTextLabeled("LW_Settings_Letters".Translate(), ("LW_LetterPolicy_" + s.letterPolicy).Translate()))
                s.letterPolicy = (LetterPolicy)(((int)s.letterPolicy + 1) % 3);
            list.Label("LW_Settings_DigestDays".Translate(s.digestDays));
            s.digestDays = (int)list.Slider(s.digestDays, 1, 15);
            if (list.ButtonTextLabeled("LW_Settings_ShowBorders".Translate(), WorldOverlay.ModeLabel(s.overlayMode, s.overlayMode == WorldOverlayMode.Shortage ? WorldOverlay.ShortageCommodity : null).CapitalizeFirst()))
                Find.WindowStack.Add(new FloatMenu(WorldOverlay.MenuOptions()));
            list.Gap();
            list.Label("LW_Settings_Scale".Translate());
            list.Label("LW_Settings_HistoryYears".Translate(s.historyYears), tooltip: "LW_Settings_HistoryYearsDesc".Translate());
            s.historyYears = (int)list.Slider(s.historyYears, 0, 10);
            list.Label("LW_Settings_MaxParties".Translate(s.maxParties));
            s.maxParties = (int)list.Slider(s.maxParties, 0, 120);
            list.Label("LW_Settings_Pace".Translate(s.pace.ToStringPercent()), tooltip: "LW_Settings_PaceDesc".Translate());
            s.pace = Mathf.Round(list.Slider(s.pace, 0.25f, 3f) * 20f) / 20f;
            list.Label("LW_Settings_WarBandHours".Translate(Mathf.RoundToInt(s.warBandMaxHours)), tooltip: "LW_Settings_WarBandHoursDesc".Translate());
            s.warBandMaxHours = Mathf.Round(list.Slider(s.warBandMaxHours, 6f, 120f));
            list.Label("LW_Settings_MerchantHours".Translate(Mathf.RoundToInt(s.merchantMaxHours)));
            s.merchantMaxHours = Mathf.Round(list.Slider(s.merchantMaxHours, 6f, 120f));
            list.Gap();
            bool perf = s.perfLog;
            list.CheckboxLabeled("LW_Settings_PerfLog".Translate(), ref s.perfLog, "LW_Settings_PerfLogDesc".Translate());
            if (s.perfLog && !perf) StartPerfLog();
            if (PerfLog.FilePath != null)
            {
                Text.Font = GameFont.Tiny;
                list.Label("LW_Settings_PerfLogFile".Translate(PerfLog.FilePath));
                Text.Font = GameFont.Small;
            }
            LivingWorldComponent comp = Current.ProgramState == ProgramState.Playing ? LivingWorldComponent.Instance : null;
            if (comp != null && !comp.preparedForRemoval && list.ButtonText("LW_Settings_PrepareRemoval".Translate()))
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("LW_Settings_PrepareRemovalConfirm".Translate(), PrepareForRemoval, destructive: true));
            if (comp != null && comp.preparedForRemoval && list.ButtonText("LW_Settings_ReEnable".Translate()))
            {
                comp.preparedForRemoval = false;
                Tributaries.ReconcileSafe(comp); // what happened to held towns meanwhile, before anything else acts on them
                Territory.Rebuild(comp); // a fresh picture before anything reads it
                Messages.Message("LW_Settings_ReEnabled".Translate(), MessageTypeDefOf.TaskCompletion, historical: false);
            }
            list.End();
        }
    }
}
