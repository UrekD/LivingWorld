using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace LivingWorld
{
    /// <summary>A vendetta against the player: a raid from a specific settlement, some days after the offence.</summary>
    public class PendingRevenge : IExposable
    {
        public int factionId = -1;
        public int homeId = -1;
        public int fireTicks;
        public int causeId = -1;
        /// <summary>A blood feud's return raid: the avenger's figure id (cancelled when the feud ends); -1 otherwise.</summary>
        public int figureId = -1;
        /// <summary>Handed to the storyteller's queue; the war band dispatch picks up the home settlement.</summary>
        public bool queued;

        public void ExposeData()
        {
            Scribe_Values.Look(ref factionId, "faction", -1);
            Scribe_Values.Look(ref homeId, "home", -1);
            Scribe_Values.Look(ref fireTicks, "fire");
            Scribe_Values.Look(ref causeId, "cause", -1);
            Scribe_Values.Look(ref figureId, "figure", -1);
            Scribe_Values.Look(ref queued, "queued");
        }
    }

    /// <summary>
    /// Sim-native raids at the player come only from causes (a killed warlord, a razed town). They go through the
    /// storyteller's incident queue as RaidEnemy, so they count against its threat pacing like any raid.
    /// </summary>
    public static class Revenge
    {
        public const string QuestTag = "LW_Revenge";
        private const int MinGapTicks = GenDate.TicksPerDay * 8;
        public const int MaxPending = 2;
        public const float PointsFactor = 1.1f;

        public static bool Schedule(Faction f, int homeId, int causeId, int figureId = -1)
        {
            if (f == null || f.def.permanentEnemy && !f.HostileTo(Faction.OfPlayer)) return false;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp.revenge.Count >= MaxPending) return false; // a young colony should not face every grudge at once
            foreach (PendingRevenge r in comp.revenge)
                if (r.factionId == f.loadID) return false; // one vendetta per faction at a time
            comp.revenge.Add(new PendingRevenge
            {
                factionId = f.loadID,
                homeId = homeId,
                fireTicks = Find.TickManager.TicksGame + (int)(GenDate.TicksPerDay * Rand.Range(5f, 12f)),
                causeId = causeId,
                figureId = figureId,
            });
            return true;
        }

        /// <summary>A blood feud ended: its pending return raid is called off, also one already handed to the storyteller.</summary>
        public static void CancelFor(LivingWorldComponent comp, Figure avenger)
        {
            for (int i = comp.revenge.Count - 1; i >= 0; i--)
            {
                PendingRevenge r = comp.revenge[i];
                if (r.figureId != avenger.id) continue;
                comp.revenge.RemoveAt(i);
                if (!r.queued) continue;
                var queue = (List<QueuedIncident>)QueueField.GetValue(Find.Storyteller.incidentQueue);
                // One vendetta per faction at a time (Schedule), so the faction's queued LW_Revenge raid is this one.
                queue.RemoveAll(q => q.FiringIncident?.parms?.questTag == QuestTag && q.FiringIncident.parms.faction?.loadID == r.factionId);
            }
        }

        private static readonly System.Reflection.FieldInfo QueueField = HarmonyLib.AccessTools.Field(typeof(IncidentQueue), "queuedIncidents");

        /// <summary>The home settlement of the faction's queued vendetta (and forget it), or -1.</summary>
        public static int TakeQueuedHome(Faction f)
        {
            List<PendingRevenge> list = LivingWorldComponent.Instance.revenge;
            for (int i = 0; i < list.Count; i++)
                if (list[i].queued && list[i].factionId == f.loadID)
                {
                    int home = list[i].homeId;
                    list.RemoveAt(i);
                    return home;
                }
            return -1;
        }

        public static void DailyCheck(LivingWorldComponent comp)
        {
            int now = Find.TickManager.TicksGame;
            for (int i = comp.revenge.Count - 1; i >= 0; i--)
            {
                PendingRevenge r = comp.revenge[i];
                if (r.queued)
                {
                    // The queued raid never came (retry window passed): forget it.
                    if (now - r.fireTicks > GenDate.TicksPerDay * 2) comp.revenge.RemoveAt(i);
                    continue;
                }
                if (now < r.fireTicks) continue;
                comp.revenge.RemoveAt(i);
                // A feud's return whose feud has ended meanwhile does not come.
                if (r.figureId >= 0 && Figures.ById(r.figureId) is not { FeudOpen: true }) continue;
                Faction f = comp.FactionById(r.factionId);
                Map map = Neighbourhood.Home;
                if (f == null || f.defeated || map == null || !f.HostileTo(Faction.OfPlayer)) continue;
                // Not on the heels of another big threat.
                if (now - map.StoryState.LastThreatBigTick < MinGapTicks)
                {
                    r.fireTicks = now + GenDate.TicksPerDay * 3;
                    comp.revenge.Add(r);
                    continue;
                }
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                parms.faction = f;
                parms.points *= PointsFactor;
                parms.questTag = QuestTag;
                r.queued = true;
                r.fireTicks = now;
                comp.revenge.Add(r);
                Find.Storyteller.incidentQueue.Add(IncidentDefOf.RaidEnemy, now + 10, parms, retryDurationTicks: GenDate.TicksPerDay);
            }
        }
    }
}
