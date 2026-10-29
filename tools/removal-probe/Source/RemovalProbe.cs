using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RemovalProbe
{
    /// <summary>
    /// Test only (tools/removal-check.sh), never shipped, no Harmony. Started with <c>-removal-probe=SaveName</c>: from the main menu it loads that
    /// save from a game that does not have Living World, prints every error of the load (normalized, for the script to compare against the
    /// load of a save that held nothing), and, when the save has a sidecar file (<c>SaveName.info</c>: the held town's settlement id and
    /// its client's faction id), checks the held town: it is a settlement of that faction, which is not hostile; a trade session with it
    /// opens and has tradeables; its map can be made, attacked and removed. Every pawn's ideo is registered. Then it saves, goes to the main
    /// menu and loads its own save again: no error at all. It ends with <c>[LivingWorld:test] PASS|FAIL plan=removal</c>.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class Starter
    {
        static Starter()
        {
            if (!GenCommandLine.TryGetCommandLineArg("removal-probe", out string save) || save.NullOrEmpty()) return;
            Probe.Save = Probe.Original = save;
            Log.Message("[LivingWorld:removal] probe armed for the save " + save);
            Probe.QueueLoad();
        }
    }

    public static class Probe
    {
        private const string OwnSave = "LW-removal-probe";

        internal static string Save, Original;
        private static int phase;
        private static int frames;
        private static bool done;
        private static string step = "load";
        private static HashSet<string> seen = new HashSet<string>();
        private static readonly List<string> problems = new List<string>();

        /// <summary>Loads the current save when the main menu is up: an empty long event, whose callback runs once it and everything queued before it are over.</summary>
        internal static void QueueLoad() => LongEventHandler.QueueLongEvent(() => { }, "LoadingLongEvent", false, null, showExtraUIInfo: false, callback: Load);

        private static void Load()
        {
            frames = 0;
            Log.Message($"[LivingWorld:removal] loading '{Save}' (phase {phase})");
            GameDataSaveLoader.LoadGame(Save);
        }

        private static string Normalize(string text)
        {
            string line = (text ?? "").Split('\n')[0];
            var sb = new System.Text.StringBuilder();
            foreach (char c in line) sb.Append(char.IsDigit(c) ? '#' : c);
            return sb.Length > 200 ? sb.ToString(0, 200) : sb.ToString();
        }

        private static List<string> Errors() => Log.Messages.Where(m => m.type == LogMessageType.Error).Select(m => Normalize(m.text)).Distinct().ToList();

        private static void Expect(bool ok, string what)
        {
            if (!ok) problems.Add(what);
        }

        /// <summary>Called every frame by the probe's game component.</summary>
        internal static void OnFrame()
        {
            if (Save == null || done || LongEventHandler.AnyEventNowOrWaiting || Find.CurrentMap == null || ++frames < 60) return;
            try
            {
                if (phase == 0) FirstLoad();
                else SecondLoad();
            }
            catch (Exception e)
            {
                Log.Message($"[LivingWorld:removal] exception in the check '{step}' (phase {phase}): {e}");
                problems.Add($"{e.GetType().Name} in the check '{step}': {e.Message}");
                Finish();
            }
        }

        private static void FirstLoad()
        {
            step = "load errors";
            foreach (string e in Errors()) Log.Message("[LivingWorld:removal] load error: " + e);
            seen = new HashSet<string>(Errors());
            string info = Path.Combine(GenFilePaths.SavedGamesFolderPath, Save + ".info");
            if (File.Exists(info)) CheckHeldTown(info);
            else Log.Message("[LivingWorld:removal] no held town in this save: only the load and the ideos are checked");
            step = "ideos";
            if (ModsConfig.IdeologyActive)
                foreach (Pawn p in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
                    if (p.RaceProps.Humanlike && !p.DevelopmentalStage.Baby() && (p.ideo?.Ideo == null || !Find.IdeoManager.IdeosListForReading.Contains(p.ideo.Ideo)))
                        problems.Add($"{p.LabelShort} has no registered ideo");
            foreach (string e in Errors())
                if (!seen.Contains(e)) problems.Add("error after the load: " + e);
            if (problems.Count > 0)
            {
                Finish();
                return;
            }
            // Save, go back to the menu, load again: no error at all.
            step = "save";
            GameDataSaveLoader.SaveGame(OwnSave);
            seen = new HashSet<string>(Errors());
            phase = 1;
            step = "reload";
            Save = OwnSave;
            GenScene.GoToMainMenu();
            QueueLoad();
        }

        private static void CheckHeldTown(string info)
        {
            var values = new Dictionary<string, int>();
            foreach (string line in File.ReadAllLines(info))
            {
                string[] kv = line.Split('=');
                if (kv.Length == 2 && int.TryParse(kv[1], out int v)) values[kv[0]] = v;
            }
            if (!values.TryGetValue("settlement", out int settlementId) || !values.TryGetValue("faction", out int factionId))
            {
                problems.Add("the sidecar file does not name the town and its faction");
                return;
            }
            step = "the town and its client";
            Settlement s = Find.WorldObjects.Settlements.FirstOrDefault(x => x.ID == settlementId);
            Expect(s != null && s.Faction != null && s.Faction.loadID == factionId && !s.Faction.IsPlayer, "the held town is not a settlement of its client");
            if (s == null || s.Faction == null) return;
            Faction f = s.Faction;
            Expect(!f.HostileTo(Faction.OfPlayer) && !f.defeated && f.leader != null, $"the client is {f.PlayerRelationKind}, defeated {f.defeated}, leader {f.leader?.LabelShort ?? "none"}");
            CheckTrade(s);
            // The map as an attack makes it, then removed.
            step = "the town's map";
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(s.Tile, new IntVec3(150, 1, 150), null);
            Expect(map != null, "the town's map could not be made");
            step = "attacking the town";
            TaggedString letter = "";
            SettlementUtility.AffectRelationsOnAttacked(s, ref letter);
            step = "removing the town's map";
            if (map != null) Current.Game.DeinitAndRemoveMap(map, false);
        }

        /// <summary>
        /// A trade session with the town, as the game opens one: a settlement is traded with by a caravan (its <c>ColonyThingsWillingToBuy</c> asks the
        /// negotiator's caravan), so one is made at the town's tile with a pawn of its own, and both are gone again afterwards.
        /// </summary>
        private static void CheckTrade(Settlement s)
        {
            step = "the trade session: making a caravan";
            Pawn negotiator = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            Caravan caravan = CaravanMaker.MakeCaravan(new[] { negotiator }, Faction.OfPlayer, s.Tile, addToWorldPawnsIfNotAlready: true);
            try
            {
                step = "the trade session: setting it up";
                TradeSession.SetupWith(s, negotiator, giftMode: false);
                step = "the trade session: its tradeables";
                Expect(TradeSession.deal != null && TradeSession.deal.AllTradeables.Count > 0, "a trade session with the town has no tradeables");
            }
            finally
            {
                TradeSession.Close();
                caravan.RemoveAllPawns();
                Find.WorldObjects.Remove(caravan);
                if (Find.WorldPawns.Contains(negotiator)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(negotiator);
            }
        }

        private static void SecondLoad()
        {
            step = "the probe's own save";
            foreach (string e in Errors())
                if (!seen.Contains(e)) problems.Add("error loading the probe's own save: " + e);
            Finish();
        }

        private static void Finish()
        {
            done = true;
            Log.Message($"[LivingWorld:test] {(problems.Count == 0 ? "PASS" : "FAIL")} plan=removal save={Original} {(problems.Count > 0 ? "PROBLEMS: " + string.Join("; ", problems) : "")}");
            Root.Shutdown();
        }
    }

    /// <summary>The probe's frame hook: a game component exists in every game, also one loaded from a save made without it.</summary>
    public class ProbeGameComponent : GameComponent
    {
        public ProbeGameComponent(Game game)
        {
        }

        public override void GameComponentUpdate() => Probe.OnFrame();
    }
}
