using System;
using HarmonyLib;
using Verse;

namespace LivingWorld
{
    // Applies every [HarmonyPatch] class in this assembly once the game has loaded its defs.
    [StaticConstructorOnStartup]
    public static class LivingWorldStartup
    {
        public const string HarmonyId = "UrekD.LivingWorld";
        public static int PatchFailures;

        static LivingWorldStartup()
        {
            Commodities.Init();
            SettlementCompInjector.Inject();
            RoadCompInjector.Inject();
            Bridge.SwapWorkers();
            var harmony = new Harmony(HarmonyId);
            PatchAllSafely(harmony);
            HospitalityCompat.Init(harmony);
            // The player's performance log (setting): times our systems and known heavy mods, one CSV line per day.
            if (LivingWorldMod.Stored.perfLog) PerfLog.Enable(PerfLog.DefaultTargets, toFile: true, warnMissing: false);
            Testing.AutoTest.CheckCommandLine();
            if (Testing.AutoTest.Active)
            {
                Testing.WorldChecks.Register();
                Testing.BridgeChecks.Register();
                Testing.CompatChecks.Register();
                Testing.DiplomacyChecks.Register();
                Testing.PrisonerChecks.Register();
                Testing.LifeChecks.Register();
                Testing.AgesChecks.Register();
                Testing.RoadChecks.Register();
                Testing.TributaryChecks.RegisterTributaries();
                Testing.AgencyChecks.Register();
                Testing.Patch_StartJob_Diagnostics.Apply(harmony);
                Testing.Patch_Storyteller_NoRoadTestCaravans.Apply(harmony);
                // Tests of the player's performance log file (normal plan in tools/regression.sh).
                if (System.Environment.GetEnvironmentVariable("LIVINGWORLD_PERFLOG") == "file")
                {
                    PerfLog.Enable(PerfLog.DefaultTargets, toFile: true, warnMissing: false);
                    Testing.AutoTest.Checks.Add(Testing.WorldChecks.PerfLogFile);
                }
                if (System.Environment.GetEnvironmentVariable("LIVINGWORLD_PROFILE") is { Length: > 0 } spec)
                {
                    PerfLog.Enable(spec.Split(','), toFile: false, warnMissing: true);
                    Testing.AutoTest.DailyMetrics.Add(() => PerfLog.LastLine ?? "profile (first day pending)");
                }
            }
            if (Testing.AutoTest.Active && PatchFailures > 0)
            {
                // An unattended test would only hang until the harness timeout; fail fast instead.
                Log.Message($"[LivingWorld:test] FAIL harmony: {PatchFailures} patch classes failed to apply");
                Root.Shutdown();
            }
            Log.Message("[LivingWorld] loaded");
        }

        /// <summary>Patches class by class so one broken target (game update) does not disable every other patch.</summary>
        private static void PatchAllSafely(Harmony harmony)
        {
            int ok = 0, failed = 0;
            foreach (Type type in typeof(LivingWorldStartup).Assembly.GetTypes())
            {
                if (!type.IsDefined(typeof(HarmonyPatch), false)) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception e)
                {
                    failed++;
                    LWLog.Error($"patch {type.Name} failed: {e.GetBaseException().Message}");
                }
            }
            LWLog.Message($"harmony: {ok} patch classes applied, {failed} failed");
            PatchFailures = failed;
        }
    }
}
