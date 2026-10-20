using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Soft integration with Hospitality (Continued), packageId Orion.Hospitality: its visitor groups set out from a
    /// real friendly settlement and walk to the colony like other world parties. Everything goes through reflection
    /// and a guarded Harmony patch; a missing type or method disables the integration (logged), never half of it.
    /// </summary>
    public static class HospitalityCompat
    {
        public static bool Active { get; private set; }
        private static System.Reflection.MethodInfo planNewVisit;
        public static Type VisitorWorkerType { get; private set; }

        public static void Init(Harmony harmony)
        {
            if (!ModsConfig.IsActive("Orion.Hospitality")) return;
            try
            {
                VisitorWorkerType = AccessTools.TypeByName("Hospitality.IncidentWorker_VisitorGroup");
                var target = VisitorWorkerType == null ? null : AccessTools.Method(VisitorWorkerType, "TryExecuteWorker", new[] { typeof(IncidentParms) });
                if (target == null)
                {
                    LWLog.Warning("Hospitality is active but its visitor worker was not found; visitors will not travel on the world map");
                    return;
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(HospitalityCompat), nameof(Prefix_TryExecuteWorker)) { priority = Priority.First });
                planNewVisit = AccessTools.Method(AccessTools.TypeByName("Hospitality.Utilities.GenericUtility"), "PlanNewVisit");
                Active = true;
                LWLog.Message("Hospitality detected: visitor groups travel from their home settlements");
            }
            catch (Exception e)
            {
                LWLog.Error("Hospitality integration failed, disabled: " + e.Message);
                Active = false;
            }
        }

        /// <summary>A visit that never arrived: let Hospitality plan the faction's next one (it only does so after a visit).</summary>
        public static void Notify_VisitCalledOff(IncidentParms parms)
        {
            if (!Active || planNewVisit == null || parms?.target is not Map map || parms.faction == null) return;
            try
            {
                planNewVisit.Invoke(null, new object[] { map, Rand.Range(5f, 15f), parms.faction });
            }
            catch (Exception e)
            {
                LWLog.Warning("Hospitality PlanNewVisit failed: " + e.Message);
            }
        }

        /// <summary>Hospitality decided a group visits: send it from a settlement instead (unless this is the arrival).</summary>
        public static bool Prefix_TryExecuteWorker(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            try
            {
                if (Bridge.Bypass || !Bridge.TryDispatchVisitors(__instance.def, parms)) return true;
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                LWLog.Error("visitor dispatch: " + e);
                return true;
            }
        }
    }
}
