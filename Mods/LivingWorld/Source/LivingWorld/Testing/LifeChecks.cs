using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plan "life" (Peaceful difficulty, nothing forced): a month of colony life, to see the colony-side systems happen on
    /// their own: neighbours' visits, recurring figures, merchants' orders, visitors' pleas, ruler audiences, kin.
    /// </summary>
    public static class LifeChecks
    {
        public static void Register()
        {
            if (AutoTest.Plan != "life") return;
            AutoTest.DailyMetrics.Add(() =>
                $"life visits={Bridge.StatVisitsArrived} trades={Bridge.StatTradesArrived} raids={Bridge.StatRaidsArrived} neighbours={Neighbours.StatSent} figures={LivingWorldComponent.Instance.figures.Count} " +
                $"returning={LivingWorldComponent.Instance.figures.Count(f => f.visits >= 2)} orders={Commissions.StatOffers}/{Commissions.StatDelivered} pleas={VisitorPleas.StatPleas} " +
                $"contracts={Mercenaries.StatContracts} rulerVisits={Figures.StatRulerVisits} kin={Kin.StatCreated} colonists={Find.AnyPlayerHomeMap?.mapPawns.FreeColonistsSpawnedCount ?? 0}");
            // Nobody manages the colony in a headless test: keep the colonists fed and steady so it lives the month (it starved
            // by day 14, then broke down and fought itself to death by day 12).
            AutoTest.Hourly.Add(() =>
            {
                if (Find.AnyPlayerHomeMap is Map map)
                    foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                    {
                        if (p.needs?.food != null) p.needs.food.CurLevelPercentage = 1f;
                        if (p.needs?.mood != null) p.needs.mood.CurLevel = Mathf.Max(p.needs.mood.CurLevel, 0.7f);
                    }
            });
            AutoTest.Checks.Add(Checks);
        }

        private static IEnumerable<string> Checks()
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Log.Message($"[LivingWorld:test] life: {Bridge.StatVisitsArrived} visits, {Bridge.StatTradesArrived} caravans, {Neighbours.StatSent} neighbourly arrivals, " +
                        $"{comp.figures.Count} figures ({comp.figures.Count(f => f.visits >= 2)} returning), orders {Commissions.StatOffers} offered / {Commissions.StatDelivered} delivered, " +
                        $"{VisitorPleas.StatPleas} pleas, {Mercenaries.StatContracts} contracts, {Figures.StatRulerVisits} ruler visits, {Kin.StatCreated} kin");
            // How often a merchant's town has something to order (a shortage of goods the colony could make).
            int towns = comp.activeSettlements.Count, needing = comp.activeSettlements.Count(s => Commissions.PickGood(s) != null);
            Log.Message($"[LivingWorld:test] life: {needing} of {towns} towns short of something the colony could make");
            if (Bridge.StatVisitsArrived + Bridge.StatTradesArrived == 0) yield return "life: a month passed without a single visit or caravan arriving from a town";
            if (comp.figures.Count == 0) yield return "life: no notable people met in a month";
            // An unmanaged colony may still die of vanilla hazards (disease, heat): logged, not a failure of the world.
            if ((Find.AnyPlayerHomeMap?.mapPawns.FreeColonistsSpawnedCount ?? 0) == 0) Log.Message("[LivingWorld:test] life: the colony did not live the whole month");
        }
    }
}
