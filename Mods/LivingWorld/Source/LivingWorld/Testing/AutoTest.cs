using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Unattended gameplay test: start the game with <c>-livingworld-test[=days]</c> (or env LIVINGWORLD_TEST=days) and
    /// optionally <c>-livingworld-plan=name</c>. From the main menu it starts a quick game (fixed seed, small map), runs the
    /// given number of days as fast as the machine allows, saves and reloads the game halfway (so every ExposeData is
    /// exercised), logs daily metrics (<c>[LivingWorld:metrics]</c>) and every letter, checks invariants, prints
    /// <c>[LivingWorld:test] PASS|FAIL</c> and quits.
    /// </summary>
    public static class AutoTest
    {
        private enum Phase { Off, WaitMenu, Starting, Settle, Running, ToMenu, Reloading, Done }

        private const string SaveName = "LivingWorldAutotest";

        private static Phase phase = Phase.Off;
        private static int days = 10;
        private static int settleFrames;
        private static int lastDay = -1;
        private static int lastHour = -1;
        private static int startTick;
        private static bool reloaded;
        private static readonly Stopwatch wall = new Stopwatch();
        public static int ErrorCount, UpstreamErrorCount;
        public static readonly List<string> FirstErrors = new List<string>();

        public static bool Active => phase != Phase.Off;
        /// <summary>Between the halfway save and the reload: the old game is being torn down for the main menu.</summary>
        internal static bool TearingDown => phase == Phase.ToMenu;

        /// <summary>normal (default) | quiet (no extra checks) — plans add scenario setup and checks.</summary>
        public static string Plan = "normal";

        /// <summary>Systems register daily metric lines and end-of-run invariant checks here.</summary>
        public static readonly List<Func<string>> DailyMetrics = new List<Func<string>>();
        public static readonly List<Action> Hourly = new List<Action>();
        public static readonly List<Func<IEnumerable<string>>> Checks = new List<Func<IEnumerable<string>>>();
        /// <summary>Runs once the game is up, before the first tick (plan setup).</summary>
        public static readonly List<Action> OnBegin = new List<Action>();
        /// <summary>Runs right before the halfway save, and once the reloaded game is up (round-trip checks).</summary>
        public static readonly List<Action> BeforeSave = new List<Action>();
        public static readonly List<Action> AfterReload = new List<Action>();

        /// <summary>
        /// A save finished loading (before any tick): the test keeps the game paused, so nothing moves between loading and
        /// the round-trip check (the loaded game otherwise runs a few ticks at the saved speed first).
        /// </summary>
        public static void Notify_Loaded()
        {
            if (Active && reloaded) Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
        }

        public static void CheckCommandLine()
        {
            string arg = null;
            if (GenCommandLine.TryGetCommandLineArg("livingworld-test", out string v)) arg = v ?? "";
            else if (Environment.GetEnvironmentVariable("LIVINGWORLD_TEST") is { } env) arg = env;
            if (arg == null) return;
            if (int.TryParse(arg, out int d) && d > 0) days = d;
            if (GenCommandLine.TryGetCommandLineArg("livingworld-plan", out string plan) && !plan.NullOrEmpty()) Plan = plan;
            else if (Environment.GetEnvironmentVariable("LIVINGWORLD_PLAN") is { Length: > 0 } envPlan) Plan = envPlan;
            if (Plan == "history") LivingWorldMod.Stored.historyYears = 10;
            if (Plan == "stress") LivingWorldMod.Stored.historyYears = 5;
            // Short tests: parties reach the colony within a day (in memory only, settings are not saved).
            LivingWorldMod.Stored.warBandMaxHours = 24f;
            LivingWorldMod.Stored.merchantMaxHours = 24f;
            phase = Phase.WaitMenu;
            LWLog.Message($"autotest armed: {days} days, plan {Plan}");
        }

        /// <summary>Called every frame (Root.Update postfix).</summary>
        public static void Update()
        {
            try
            {
                switch (phase)
                {
                    case Phase.WaitMenu:
                        if (Current.ProgramState == ProgramState.Entry && !LongEventHandler.AnyEventNowOrWaiting)
                        {
                            phase = Phase.Starting;
                            // "load": continue the last autotest save in a fresh process (isolates save/load problems).
                            if (Plan == "load")
                            {
                                reloaded = true;
                                GameDataSaveLoader.LoadGame(SaveName);
                            }
                            else StartGame();
                        }
                        break;
                    case Phase.ToMenu:
                        // Play→Play scene reloads never finish under the headless engine; go through the main menu.
                        if (Current.ProgramState == ProgramState.Entry && !LongEventHandler.AnyEventNowOrWaiting)
                        {
                            phase = Phase.Reloading;
                            GameDataSaveLoader.LoadGame(SaveName);
                        }
                        break;
                    case Phase.Starting:
                    case Phase.Reloading:
                        if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null && !LongEventHandler.AnyEventNowOrWaiting)
                        {
                            if (phase == Phase.Reloading) LWLog.Message("[LivingWorld:test] reloaded save");
                            phase = Phase.Settle;
                            settleFrames = 0;
                        }
                        break;
                    case Phase.Settle:
                        if (++settleFrames >= 30) BeginRun();
                        break;
                    case Phase.Running:
                        if (Plan == "uitour")
                        {
                            UiTour.Update();
                            if (!BridgeFiredForTour) RunTicksFor(GenDate.TicksPerHour * 3);
                        }
                        else RunTicks();
                        break;
                }
            }
            catch (Exception e)
            {
                LWLog.Error("autotest crashed: " + e);
                Finish(false, "exception: " + e.Message);
            }
        }

        private static void StartGame()
        {
            // Vanilla's quick test setup with a fixed seed and a small map; the scene change runs InitNewGame.
            Current.ProgramState = ProgramState.Entry;
            Game.ClearCaches();
            Current.Game = new Game();
            Current.Game.InitData = new GameInitData();
            Current.Game.Scenario = ScenarioDefOf.Crashlanded.scenario;
            Find.Scenario.PreConfigure();
            // "life": no threats, so the colony lives long enough for visitors, orders and contracts to come on their own.
            DifficultyDef difficulty = Plan == "life" ? DefDatabase<DifficultyDef>.GetNamed("Peaceful") : DifficultyDefOf.Rough;
            Current.Game.storyteller = new Storyteller(StorytellerDefOf.Cassandra, difficulty);
            // stress: a full planet (about three times the settlements).
            float coverage = Plan == "stress" ? 1f : 0.3f;
            Current.Game.World = WorldGenerator.GenerateWorld(coverage, "livingworld", OverallRainfall.Normal, OverallTemperature.Normal,
                Plan == "stress" ? OverallPopulation.High : OverallPopulation.Normal, LandmarkDensity.Normal);
            ChooseStartingTile();
            Find.GameInitData.mapSize = 100;
            Find.Scenario.PostIdeoChosen();
            PageUtility.InitGameStart();
        }

        /// <summary>
        /// The same colony site every run (seeded), and one with neighbours: at least 4 towns of 2+ factions reachable within
        /// 25 tiles. A random site sometimes landed on an island, where nothing can walk to the colony.
        /// </summary>
        private static void ChooseStartingTile()
        {
            Rand.PushState(20261017);
            try
            {
                for (int attempt = 0; attempt < 60; attempt++)
                {
                    Find.GameInitData.ChooseRandomStartingTile();
                    PlanetTile tile = Find.GameInitData.startingTile;
                    int towns = 0, friendly = 0;
                    var factions = new HashSet<Faction>();
                    foreach (RimWorld.Planet.Settlement s in Find.WorldObjects.Settlements)
                    {
                        if (s.Faction == null || s.Faction.IsPlayer || Find.WorldGrid.ApproxDistanceInTiles(tile, s.Tile) > 25f || !Find.WorldReachability.CanReach(tile, s.Tile)) continue;
                        towns++;
                        factions.Add(s.Faction);
                        // Not just non-hostile: able to send someone (the Empire needs a permit for traders and sends no visitors).
                        if (!s.Faction.HostileTo(Faction.OfPlayer) && (Bridge.CanSendVisitors(s.Faction) || !s.Faction.def.caravanTraderKinds.NullOrEmpty())) friendly++;
                    }
                    // "life" watches visits from the neighbourhood: some of the towns must be willing to come.
                    if (towns >= 4 && factions.Count >= 2 && (Plan != "life" || friendly >= 2))
                    {
                        LWLog.Message($"autotest start tile {tile} after {attempt + 1} tries: {towns} towns of {factions.Count} factions in reach");
                        return;
                    }
                }
                LWLog.Warning("autotest: no starting tile with reachable neighbours found; using the last one");
            }
            finally
            {
                Rand.PopState();
            }
        }

        private static void BeginRun()
        {
            Prefs.DevMode = true;
            Prefs.AutosaveIntervalDays = 1000f;
            if (reloaded && Plan != "load")
                foreach (Action a in AfterReload) a();
            if (!reloaded || (Plan == "load" && startTick == 0))
            {
                // Like a player's colony: a painted home area (some mods, e.g. Hospitality, use it for guests).
                Map m = Find.CurrentMap;
                Pawn anyColonist = m.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                if (anyColonist != null)
                    foreach (IntVec3 c in CellRect.CenteredOn(anyColonist.Position, 12))
                        if (c.InBounds(m)) m.areaManager.Home[c] = true;
                foreach (Action a in OnBegin) a();
                startTick = Find.TickManager.TicksGame;
                wall.Restart();
                LWLog.Message($"[LivingWorld:test] start: tick {startTick}, factions {Find.FactionManager.AllFactionsListForReading.Count}, settlements {Find.WorldObjects.SettlementBases.Count}");
            }
            Find.TickManager.CurTimeSpeed = Plan == "uitour" ? TimeSpeed.Normal : TimeSpeed.Paused;
            lastDay = GenDate.DaysPassed;
            phase = Phase.Running;
        }

        private static void RunTicks()
        {
            // Never tick through a long event (autosave, loading): vanilla doesn't, and tearing the world down while
            // one is pending kills the headless game.
            if (LongEventHandler.AnyEventNowOrWaiting) return;
            TickManager tm = Find.TickManager;
            tm.CurTimeSpeed = TimeSpeed.Paused;
            var frame = Stopwatch.StartNew();
            while (frame.ElapsedMilliseconds < 80)
            {
                tm.DoSingleTick();
                if (LongEventHandler.AnyEventNowOrWaiting) return;
                int hour = tm.TicksGame / GenDate.TicksPerHour;
                if (hour != lastHour)
                {
                    lastHour = hour;
                    foreach (Action a in Hourly) a();
                }
                if (GenDate.DaysPassed != lastDay)
                {
                    lastDay = GenDate.DaysPassed;
                    Metrics();
                }
                int elapsed = tm.TicksGame - startTick;
                if (!reloaded && elapsed >= days * GenDate.TicksPerDay / 2)
                {
                    SaveAndReload();
                    return;
                }
                if (elapsed >= days * GenDate.TicksPerDay)
                {
                    Evaluate();
                    return;
                }
            }
        }

        public static bool BridgeFiredForTour => Bridge.StatWarBands > 0 || Find.TickManager.TicksGame - startTick > GenDate.TicksPerHour * 3;

        /// <summary>uitour: tick quickly through the first hours (history, bridge test incidents), then play normally.</summary>
        private static void RunTicksFor(int ticks)
        {
            if (LongEventHandler.AnyEventNowOrWaiting) return;
            TickManager tm = Find.TickManager;
            var frame = Stopwatch.StartNew();
            while (frame.ElapsedMilliseconds < 80 && tm.TicksGame - startTick < ticks)
            {
                tm.DoSingleTick();
                int hour = tm.TicksGame / GenDate.TicksPerHour;
                if (hour != lastHour)
                {
                    lastHour = hour;
                    foreach (Action a in Hourly) a();
                }
            }
        }

        private static void SaveAndReload()
        {
            reloaded = true;
            foreach (Action a in BeforeSave) a();
            if (Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_STRIP") == "parties")
                foreach (WorldParty p in WorldParty.All.ToArray()) p.Destroy();
            LWLog.Message($"[LivingWorld:test] saving at day {GenDate.DaysPassed}");
            GameDataSaveLoader.SaveGame(SaveName);
            phase = Phase.ToMenu;
            GenScene.GoToMainMenu();
        }

        private static void Metrics()
        {
            double tps = (Find.TickManager.TicksGame - startTick) / Math.Max(0.001, wall.Elapsed.TotalSeconds);
            Log.Message($"[LivingWorld:metrics] day={GenDate.DaysPassed} errors={ErrorCount} tps={tps:F0}");
            foreach (Func<string> m in DailyMetrics)
            {
                string line = m();
                if (!line.NullOrEmpty()) Log.Message("[LivingWorld:metrics] " + line);
            }
        }

        private static void Evaluate()
        {
            var problems = new List<string>();
            if (ErrorCount > 0) problems.Add($"{ErrorCount} errors logged (first: {string.Join(" | ", FirstErrors)})");
            foreach (Func<IEnumerable<string>> check in Checks)
                foreach (string p in check())
                    problems.Add(p);
            string summary = $"days={days} errors={ErrorCount}{(UpstreamErrorCount > 0 ? $" upstreamErrors={UpstreamErrorCount}" : "")} wall={wall.Elapsed.TotalSeconds:F0}s";
            Finish(problems.Count == 0, summary + (problems.Count > 0 ? " PROBLEMS: " + string.Join("; ", problems) : ""));
        }

        private static void Finish(bool pass, string summary)
        {
            phase = Phase.Done;
            Log.Message($"[LivingWorld:test] {(pass ? "PASS" : "FAIL")} plan={Plan} {summary}");
            Root.Shutdown();
        }
    }

    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    public static class Patch_Root_Update_AutoTest
    {
        public static void Postfix()
        {
            if (AutoTest.Active) AutoTest.Update();
        }
    }

    /// <summary>Autotest: every letter goes to the log, so a run reads like the story it told.</summary>
    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
    public static class Patch_LetterStack_ReceiveLetter_Log
    {
        public static void Postfix(Letter let)
        {
            if (!AutoTest.Active || let == null) return;
            string text = let is ChoiceLetter cl ? cl.Text.Resolve() : "";
            string line = $"[LivingWorld:letter] day={GenDate.DaysPassed} {let.Label.Resolve()}: {text.Replace("\n", " ").Replace("\r", "")}";
            Log.Message(line.Length > 500 ? line.Substring(0, 500) : line);
        }
    }

    [HarmonyPatch(typeof(Log), nameof(Log.Error), typeof(string))]
    public static class Patch_Log_Error_Count
    {
        public static void Postfix(string text)
        {
            if (!AutoTest.Active || text == null || text.StartsWith("Resolution too small")) return;
            // The test's in-process reload: vanilla can still update the old game's TickManager in a frame after the game
            // was torn down for the main menu (TimeSlower reads Find.TickManager). Harness noise, not gameplay.
            if (AutoTest.TearingDown && text.StartsWith("Root level exception in Update()"))
            {
                AutoTest.UpstreamErrorCount++;
                Log.Message("[LivingWorld:test] teardown noise during the reload: " + text);
                return;
            }
            // Known bugs of other mods, not ours: logged and counted apart, so they don't fail the run.
            if (text.StartsWith("Tried to destroy non-destroyable thing") && System.Environment.StackTrace.Contains("Hospitality.IncidentWorker_VisitorGroup.GiveItems"))
            {
                AutoTest.UpstreamErrorCount++;
                Log.Message("[LivingWorld:test] upstream (Hospitality GiveItems destroys an indestructible item): " + text);
                return;
            }
            // Vanilla backstory gaps: some pawn kinds have no shuffled backstory for their faction; vanilla picks a random one.
            if (text.StartsWith("No shuffled Adulthood found") || text.StartsWith("No shuffled Childhood found"))
            {
                AutoTest.UpstreamErrorCount++;
                Log.Message("[LivingWorld:test] upstream (vanilla backstory gap): " + text);
                return;
            }
            // Vanilla 1.7 (Biotech fire spew, CompAbilityEffect_Burner): the flame's line of sight runs past the map edge and
            // GenGrid.CanBeSeenOverFast indexes outside its grid. Small test maps put pawns near edges often.
            if (text.Contains("IndexOutOfRangeException") && text.Contains("CompAbilityEffect_Burner") && text.Contains("CanBeSeenOverFast"))
            {
                AutoTest.UpstreamErrorCount++;
                Log.Message("[LivingWorld:test] upstream (vanilla fire spew past the map edge): " + (text.Length > 120 ? text.Substring(0, 120) : text));
                return;
            }
            AutoTest.ErrorCount++;
            if (AutoTest.FirstErrors.Count < 5) AutoTest.FirstErrors.Add(text.Length > 300 ? text.Substring(0, 300) : text);
        }
    }
}

namespace LivingWorld.Testing
{
    /// <summary>
    /// Autotest diagnostics: when StartJob throws, say whose job it was (pawn, lord, current and new job, rope state),
    /// which vanilla's "Exception ticking" line leaves out. The exception still propagates. Patched only in autotests.
    /// </summary>
    public static class Patch_StartJob_Diagnostics
    {
        public static void Apply(HarmonyLib.Harmony harmony) =>
            harmony.Patch(HarmonyLib.AccessTools.Method(typeof(Verse.AI.Pawn_JobTracker), nameof(Verse.AI.Pawn_JobTracker.StartJob)),
                finalizer: new HarmonyLib.HarmonyMethod(typeof(Patch_StartJob_Diagnostics), nameof(Finalizer)));

        public static System.Exception Finalizer(System.Exception __exception, Verse.AI.Pawn_JobTracker __instance, Verse.AI.Job newJob, Pawn ___pawn)
        {
            if (__exception != null && AutoTest.Active)
            {
                // Each field on its own: a null here must not hide the original exception again.
                string Safe(System.Func<string> get)
                {
                    try { return get() ?? "null"; }
                    catch { return "?"; }
                }
                Pawn p = ___pawn;
                Log.Message($"[LivingWorld:test] StartJob threw for {Safe(() => p.LabelShort)} ({Safe(() => p.def.defName)}): " +
                            $"lord={Safe(() => Verse.AI.Group.LordUtility.GetLord(p).LordJob.GetType().Name)} toil={Safe(() => Verse.AI.Group.LordUtility.GetLord(p).CurLordToil.GetType().Name)} " +
                            $"curJob={Safe(() => __instance.curJob.def.defName)} newJob={Safe(() => newJob.def.defName)} newTarget={Safe(() => newJob.targetA.ToString())} " +
                            $"targetDestroyed={Safe(() => newJob.targetA.Thing.Destroyed.ToString())} duty={Safe(() => p.mindState.duty.def.defName)} dutyFocus={Safe(() => p.mindState.duty.focus.ToString())} " +
                            $"ropedBy={Safe(() => p.roping.RopedByPawn.LabelShort)}\n{__exception}");
            }
            return __exception;
        }
    }
}
