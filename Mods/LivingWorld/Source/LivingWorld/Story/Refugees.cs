using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using Verse;

namespace LivingWorld
{
    /// <summary>Refugees from a nearby disaster (razed town, famine) who will ask the colony for shelter.</summary>
    public class PendingRefugees : IExposable
    {
        public string fromName;
        public int fireTicks;
        public bool razed;

        public void ExposeData()
        {
            Scribe_Values.Look(ref fromName, "from");
            Scribe_Values.Look(ref fireTicks, "fire");
            Scribe_Values.Look(ref razed, "razed");
        }
    }

    /// <summary>
    /// Disasters near the colony send refugees its way: vanilla's lodger quest (Royalty, "Hospitality_Refugee"),
    /// told as people fleeing a real town.
    /// </summary>
    public static class Refugees
    {
        public const float MaxRange = 18f;
        public static int StatQuests;
        private const int CooldownTicks = GenDate.TicksPerDay * 20;

        public static void Notify_Disaster(Settlement s, bool razed, bool force = false)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || SimClock.InHistory || !ModsConfig.RoyaltyActive || s == null) return;
            Map home = Neighbourhood.Home;
            if (home == null || Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Tile) > Neighbourhood.Radius(home.Tile, MaxRange)) return;
            int now = Find.TickManager.TicksGame;
            if (!force && (now - comp.lastRefugeesTick < CooldownTicks || comp.refugees.Count > 0 || !Rand.Chance(razed ? 0.6f : 0.3f))) return;
            comp.lastRefugeesTick = now;
            comp.refugees.Add(new PendingRefugees { fromName = s.Label, razed = razed, fireTicks = now + (force ? GenDate.TicksPerHour : (int)(GenDate.TicksPerDay * Rand.Range(1f, 3f))) });
        }

        public static void DailyCheck(LivingWorldComponent comp)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = comp.refugees.Count - 1; i >= 0; i--)
            {
                PendingRefugees r = comp.refugees[i];
                if (now < r.fireTicks) continue;
                comp.refugees.RemoveAt(i);
                QuestScriptDef script = DefDatabase<QuestScriptDef>.GetNamedSilentFail("Hospitality_Refugee");
                Map home = Neighbourhood.Home;
                if (script == null || home == null) continue;
                var slate = new Slate();
                slate.Set("points", StorytellerUtility.DefaultThreatPointsNow(home));
                if (!script.CanRun(slate, home)) continue;
                Quest quest = QuestUtility.GenerateQuestAndMakeAvailable(script, slate);
                quest.description += "\n\n" + (r.razed ? "LW_RefugeesFromRazed" : "LW_RefugeesFromFamine").Translate(r.fromName).Resolve();
                if (!quest.hidden && script.sendAvailableLetter) QuestUtility.SendLetterQuestAvailable(quest);
                StatQuests++;
                if (Testing.AutoTest.Active) LWLog.Message($"[refugees] quest '{quest.name}': {quest.description.Resolve().Replace("\n", " ")}");
            }
        }
    }
}
