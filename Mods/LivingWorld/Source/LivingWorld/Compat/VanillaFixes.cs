using HarmonyLib;
using RimWorld;
using Verse.AI;

namespace LivingWorld
{
    /// <summary>
    /// Vanilla 1.7 (Odyssey flight): Pawn_JobTracker.StartJob calls flight.Notify_JobStarted(newJob) after the driver's
    /// first toil has run. If that toil ended the job at once, the job has already gone back to the pool (its def cleared),
    /// and a pawn that can fly (Odyssey chickens flutter) throws "Exception ticking Chicken". Seen with trade caravans'
    /// animals; the job is over, so there is nothing to notify.
    /// </summary>
    [HarmonyPatch(typeof(Pawn_FlightTracker), nameof(Pawn_FlightTracker.Notify_JobStarted))]
    public static class Patch_FlightTracker_Notify_JobStarted
    {
        public static bool Prefix(Job job) => job?.def != null;
    }
}
