using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// Raiders' resolve follows the world: vanilla lets an attacking group flee once a share of it is lost
    /// (Trigger_FractionPawnsLost on the panic-flee transitions Lord adds to every NPC lord). We scale that share when
    /// the raid's lord is made: war-weary factions and men whose home town has fallen break sooner, a ruler's sworn
    /// vengeance on the colony makes them hold on longer. No AI patches; the graph is the vanilla one.
    /// </summary>
    public static class RaidMorale
    {
        private static readonly AccessTools.FieldRef<Trigger_FractionPawnsLost, float> FractionRef =
            AccessTools.FieldRefAccess<Trigger_FractionPawnsLost, float>("fraction");

        public static void Notify_RaidLordMade(Lord lord, IncidentContext ctx)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            FactionState fs = comp.StateFor(lord.faction);
            if (fs == null || lord.Graph == null) return;
            float factor = 1f - 0.5f * fs.warWeariness;
            bool homeLost = LivingWorldComponent.WorldObjectById(ctx.originId) is not Settlement home || home.Destroyed || home.Faction != lord.faction;
            if (homeLost) factor *= 0.7f;
            bool sworn = fs.goal == RulerGoalKind.Avenge && fs.goalTargetId == Faction.OfPlayer.loadID;
            if (sworn) factor *= 1.3f;
            if (Mathf.Abs(factor - 1f) < 0.05f) return;
            int changed = 0;
            foreach (Transition t in lord.Graph.transitions)
            {
                if (t.target is not LordToil_PanicFlee) continue;
                foreach (Trigger trigger in t.triggers)
                    if (trigger is Trigger_FractionPawnsLost lost)
                    {
                        FractionRef(lost) = Mathf.Clamp(FractionRef(lost) * factor, 0.15f, 0.95f);
                        changed++;
                    }
            }
            if (changed == 0) return;
            if (Testing.AutoTest.Active) LWLog.Message($"[raid morale] {lord.faction.Name}: flee threshold ×{factor:F2} (weariness {fs.warWeariness:F2}, home lost {homeLost}, sworn {sworn}) on {changed} transitions");
            string key = sworn ? "LW_RaidersSworn" : homeLost ? "LW_RaidersHomeLost" : factor < 1f ? "LW_RaidersWeary" : null;
            if (key != null) Messages.Message(key.Translate(lord.faction.NameColored), new LookTargets(lord.ownedPawns), MessageTypeDefOf.NeutralEvent, historical: false);
        }
    }
}
