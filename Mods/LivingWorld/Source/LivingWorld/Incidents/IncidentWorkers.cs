using RimWorld;

namespace LivingWorld
{
    /// <summary>
    /// RaidEnemy worker (swapped in by <see cref="Bridge.SwapWorkers"/>): resolves faction, strategy and arrival as
    /// vanilla does, then lets a war band carry the raid from a real settlement. Returns true at dispatch, so the
    /// storyteller counts the threat once; the raid itself runs later through <see cref="ExecuteNow"/>.
    /// </summary>
    public class IncidentWorker_RaidEnemy_LW : IncidentWorker_RaidEnemy
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            // Tribute was paid: this faction's raids go to someone else for a while (storyteller raids only).
            if (!Bridge.Bypass && !parms.forced && parms.quest == null && parms.faction != null && LivingWorldComponent.Instance?.TributeTruce(parms.faction) == true)
                parms.faction = null;
            if (Bridge.Bypass || !LivingWorldMod.Settings.raidsAsWarBands || Bridge.TravelTarget(parms) == null) return base.TryExecuteWorker(parms);
            ResolveRaidPoints(parms);
            if (!TryResolveRaidFaction(parms)) return false;
            ResolveRaidStrategy(parms, parms.pawnGroupKind ?? PawnGroupKindDefOf.Combat);
            if (parms.raidArrivalMode == null && !TryResolveRaidArriveMode(parms)) return false;
            // A defeated faction sends no war bands, but quest/dev raids of it still run the vanilla way.
            if (parms.faction.defeated) return base.TryExecuteWorker(parms);
            return Bridge.TryDispatchRaid(def, parms) || base.TryExecuteWorker(parms);
        }

        /// <summary>The war band reached the colony: the vanilla raid, without storyteller bookkeeping.</summary>
        public bool ExecuteNow(IncidentParms parms) => base.TryExecuteWorker(parms);

        /// <summary>A faction under a tribute truce is never picked for storyteller raids (if nobody else can come, no raid).</summary>
        public override bool FactionCanBeGroupSource(Faction f, IncidentParms parms, bool desperate = false)
        {
            if (!parms.forced && parms.quest == null && LivingWorldComponent.Instance?.TributeTruce(f) == true) return false;
            return base.FactionCanBeGroupSource(f, parms, desperate);
        }
    }

    /// <summary>VisitorGroup worker (without Hospitality): visitors walk from a friendly settlement to the colony.</summary>
    public class IncidentWorker_VisitorGroup_LW : IncidentWorker_VisitorGroup
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            if (Bridge.Bypass || Bridge.TravelTarget(parms) == null) return base.TryExecuteWorker(parms);
            return Bridge.TryDispatchVisitors(def, parms) || base.TryExecuteWorker(parms);
        }

        public bool ExecuteNow(IncidentParms parms) => base.TryExecuteWorker(parms);
    }

    /// <summary>TraderCaravanArrival worker: the caravan sets out from a friendly settlement and walks to the colony.</summary>
    public class IncidentWorker_TraderCaravanArrival_LW : IncidentWorker_TraderCaravanArrival
    {
        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            if (Bridge.Bypass || Bridge.TravelTarget(parms) == null) return base.TryExecuteWorker(parms);
            if (!TryResolveParms(parms)) return false;
            return Bridge.TryDispatchTraders(def, parms) || base.TryExecuteWorker(parms);
        }

        public bool ExecuteNow(IncidentParms parms) => base.TryExecuteWorker(parms);
    }
}
