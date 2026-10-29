using System.Diagnostics;
using RimWorld;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Before the landing, the world lives through some years in fast mode (no parties on the map, no letters, every
    /// expedition resolved at once), so a new game starts with wars, ruins, new towns and a chronicle reaching back.
    /// </summary>
    public static class WorldHistory
    {
        public static void Generate(LivingWorldComponent comp, int years)
        {
            int days = years * GenDate.DaysPerYear;
            var sw = Stopwatch.StartNew();
            Rand.PushState(GenText.StableStringHash(Find.World.info.seedString) ^ 0x4C57);
            try
            {
                Testing.DeterminismLog.Day(comp, -1);
                for (int d = days; d > 0; d--)
                {
                    SimClock.Offset = -d * GenDate.TicksPerDay;
                    comp.DailyStep();
                    Testing.DeterminismLog.Day(comp, days - d);
                    if (Testing.AutoTest.Active && d % GenDate.DaysPerYear == 0) Testing.WorldChecks.LogYear(years - d / GenDate.DaysPerYear);
                }
            }
            finally
            {
                SimClock.Offset = 0;
                Rand.PopState();
            }
            comp.historyDone = true;
            Territory.Rebuild(comp); // the picture the player lands in
            WorldOverlay.InvalidateAll();
            Roads.Notify_HistoryEnded(); // the roads layer is redrawn once, not for every road history laid
            ChronicleEventDef def = LWChronicleDefOf.LW_StoryBegins;
            comp.chronicle.Record(def, def.text.Formatted(years.Named("YEARS")).Resolve(), silent: true);
            LWLog.Message($"history: {years} years in {sw.ElapsedMilliseconds} ms, {comp.chronicle.Count} chronicle entries");
            if (Testing.AutoTest.Active) Testing.TownMapChecks.LogLanding(comp);
        }
    }
}
