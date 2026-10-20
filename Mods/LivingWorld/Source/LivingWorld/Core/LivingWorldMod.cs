using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    public enum LetterPolicy : byte { Off, Digest, All }

    public class LivingWorldSettings : ModSettings
    {
        // Subsystems
        public bool simulateEconomy = true;
        public bool simulateDiplomacy = true;
        public bool simulateWarfare = true;
        public bool allowConquest = true;
        public bool allowExpansion = true;
        public bool worldParties = true;
        public bool raidsAsWarBands = true;
        public bool marketPrices = true;
        public bool figures = true;
        // Presentation
        public LetterPolicy letterPolicy = LetterPolicy.Digest;
        public int digestDays = 5;
        // Scale
        public int historyYears = 2;
        public int maxParties = 40;
        public float pace = 1f;
        // Travel
        public float warBandMaxHours = 48f;
        public float merchantMaxHours = 48f;
        // Diagnostics
        public bool perfLog;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref simulateEconomy, "simulateEconomy", true);
            Scribe_Values.Look(ref simulateDiplomacy, "simulateDiplomacy", true);
            Scribe_Values.Look(ref simulateWarfare, "simulateWarfare", true);
            Scribe_Values.Look(ref allowConquest, "allowConquest", true);
            Scribe_Values.Look(ref allowExpansion, "allowExpansion", true);
            Scribe_Values.Look(ref worldParties, "worldParties", true);
            Scribe_Values.Look(ref raidsAsWarBands, "raidsAsWarBands", true);
            Scribe_Values.Look(ref marketPrices, "marketPrices", true);
            Scribe_Values.Look(ref figures, "figures", true);
            Scribe_Values.Look(ref letterPolicy, "letterPolicy", LetterPolicy.Digest);
            Scribe_Values.Look(ref digestDays, "digestDays", 5);
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
            simulateEconomy = false, simulateDiplomacy = false, simulateWarfare = false, allowConquest = false, allowExpansion = false,
            worldParties = false, raidsAsWarBands = false, marketPrices = false, figures = false, letterPolicy = LetterPolicy.Off,
        };

        /// <summary>The player's own settings (write through this; <see cref="Settings"/> may be the shared all-off set).</summary>
        public static LivingWorldSettings Stored => stored;

        /// <summary>The settings in effect: the player's, unless the current save was prepared for removing the mod.</summary>
        public static LivingWorldSettings Settings =>
            Current.ProgramState == ProgramState.Playing && LivingWorldComponent.Instance?.preparedForRemoval == true ? Off : stored;

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
            int n = 0;
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
            LivingWorldComponent.Instance.preparedForRemoval = true;
            Messages.Message("LW_Settings_PreparedRemoval".Translate(n), MessageTypeDefOf.TaskCompletion, historical: false);
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            LivingWorldSettings s = stored;
            var list = new Listing_Standard { ColumnWidth = (inRect.width - 34f) / 2f };
            list.Begin(inRect);
            list.Label("LW_Settings_Simulation".Translate());
            list.CheckboxLabeled("LW_Settings_Economy".Translate(), ref s.simulateEconomy, "LW_Settings_EconomyDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Diplomacy".Translate(), ref s.simulateDiplomacy, "LW_Settings_DiplomacyDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Warfare".Translate(), ref s.simulateWarfare, "LW_Settings_WarfareDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Conquest".Translate(), ref s.allowConquest, "LW_Settings_ConquestDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Expansion".Translate(), ref s.allowExpansion, "LW_Settings_ExpansionDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Parties".Translate(), ref s.worldParties, "LW_Settings_PartiesDesc".Translate());
            list.CheckboxLabeled("LW_Settings_WarBands".Translate(), ref s.raidsAsWarBands, "LW_Settings_WarBandsDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Markets".Translate(), ref s.marketPrices, "LW_Settings_MarketsDesc".Translate());
            list.CheckboxLabeled("LW_Settings_Figures".Translate(), ref s.figures, "LW_Settings_FiguresDesc".Translate());
            list.NewColumn();
            list.Label("LW_Settings_Presentation".Translate());
            if (list.ButtonTextLabeled("LW_Settings_Letters".Translate(), ("LW_LetterPolicy_" + s.letterPolicy).Translate()))
                s.letterPolicy = (LetterPolicy)(((int)s.letterPolicy + 1) % 3);
            list.Label("LW_Settings_DigestDays".Translate(s.digestDays));
            s.digestDays = (int)list.Slider(s.digestDays, 1, 15);
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
                Messages.Message("LW_Settings_ReEnabled".Translate(), MessageTypeDefOf.TaskCompletion, historical: false);
            }
            list.End();
        }
    }
}
