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
                for (int d = days; d > 0; d--)
                {
                    SimClock.Offset = -d * GenDate.TicksPerDay;
                    comp.DailyStep();
                    if (Testing.AutoTest.Active && d % GenDate.DaysPerYear == 0) Testing.WorldChecks.LogYear(years - d / GenDate.DaysPerYear);
                }
            }
            finally
            {
                SimClock.Offset = 0;
                Rand.PopState();
            }
            comp.historyDone = true;
            ChronicleEventDef def = LWChronicleDefOf.LW_StoryBegins;
            comp.chronicle.Record(def, def.text.Formatted(years.Named("YEARS")).Resolve(), silent: true);
            LWLog.Message($"history: {years} years in {sw.ElapsedMilliseconds} ms, {comp.chronicle.Count} chronicle entries");
        }
    }
}
