using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>War band: raids the colony on arrival (bridge); strength returns to the garrison when it comes home.</summary>
    public class PartyWorker_WarBand : PartyWorker
    {
        public override bool ArrivedAtDestination(WorldParty p)
        {
            if (p.deferredParms != null)
            {
                Faction f = p.Faction;
                // Tribute was paid while they marched: they keep the bargain and go home.
                bool truce = f != null && LivingWorldComponent.Instance.TributeTruce(f);
                if (f != null && !truce && f.HostileTo(Faction.OfPlayer) && Bridge.ExecuteDeferred(p, ContextKind.Raid)) return true;
                if (truce || f == null || !f.HostileTo(Faction.OfPlayer)) Bridge.StatWarBandsTurnedBack++; // a world reason (no way in is counted apart)
                if (Testing.AutoTest.Active) LWLog.Message($"[bridge] {p.Label} turned back at the colony: hostile={f?.HostileTo(Faction.OfPlayer)} truce={truce}");
                Messages.Message("LW_WarBandTurnedBack".Translate(p.Label), new LookTargets(p), MessageTypeDefOf.NeutralEvent);
                CallOff(p);
                return false;
            }
            // Against another faction's settlement.
            if (p.Destination is not Settlement ts || !ts.Spawned || p.Faction == null)
            {
                CallOff(p);
                return false;
            }
            CompSettlementLife life = ts.GetComponent<CompSettlementLife>();
            if (ts.Faction == p.Faction)
            {
                // Taken by friends meanwhile: reinforce it.
                if (life != null) life.garrison += p.strength;
                return true;
            }
            if (!p.Faction.HostileTo(ts.Faction) || life == null || !life.Active)
            {
                CallOff(p);
                return false;
            }
            BattleResult r = Battle.Resolve(p.Faction, p.Origin, life, p.strength);
            p.strength = r.attackerSurvivors;
            p.silver += r.loot;
            for (int i = 0; i < r.plunder.Length && i < p.cargo.Length; i++) p.cargo[i] += r.plunder[i];
            if (p.strength < 5f) return true;
            p.TurnBack();
            return false;
        }

        /// <summary>Goes home without fighting: the strength it took comes back with it.</summary>
        private static void CallOff(WorldParty p)
        {
            p.deferredParms = null;
            if (p.strengthPaid > 0f) p.strength = p.strengthPaid;
            p.TurnBack();
        }

        public override void DestinationLost(WorldParty p) => CallOff(p);

        public override void ArrivedHome(WorldParty p)
        {
            Settlement home = p.Origin;
            if (home == null) return;
            ArrivedHomeInstant(home, p.strength, p.silver);
            CompSettlementLife life = home.GetComponent<CompSettlementLife>();
            if (life != null && life.initialized)
                for (int i = 0; i < p.cargo.Length && i < life.stock.Length; i++) life.stock[i] += p.cargo[i];
        }

        public override void ArrivedHomeInstant(Settlement home, float strength, float silver)
        {
            CompSettlementLife life = home.GetComponent<CompSettlementLife>();
            if (life == null || !life.initialized) return;
            life.garrison += strength;
            FactionState fs = LivingWorldComponent.Instance.StateFor(home.Faction);
            if (fs != null) fs.treasury += silver;
        }

        public override string InspectExtra(WorldParty p) =>
            p.phase == PartyPhase.Outbound && p.deferredParms?.target is Map ? "LW_Party_RaidTarget".Translate().Resolve() : null;
    }

    /// <summary>Merchants: trade at the colony on arrival (bridge), or sell their cargo in another settlement.</summary>
    public class PartyWorker_Merchants : PartyWorker
    {
        public override bool ArrivedAtDestination(WorldParty p)
        {
            if (p.deferredParms != null)
            {
                Faction f = p.Faction;
                if (f != null && !f.HostileTo(Faction.OfPlayer) && Bridge.ExecuteDeferred(p, ContextKind.Trade)) return true;
                Messages.Message("LW_MerchantsTurnedBack".Translate(p.Label), new LookTargets(p), MessageTypeDefOf.NeutralEvent);
                p.deferredParms = null;
                p.TurnBack();
                return false;
            }
            if (p.Destination is Settlement ts && ts.Spawned && p.Faction != null && !p.Faction.HostileTo(ts.Faction))
            {
                CompSettlementLife buyer = ts.GetComponent<CompSettlementLife>();
                if (buyer != null && buyer.Active)
                {
                    LivingWorldComponent comp = LivingWorldComponent.Instance;
                    for (int i = 0; i < p.cargo.Length; i++)
                    {
                        if (p.cargo[i] <= 0f) continue;
                        Trade.Deliver(comp, p.Origin, buyer, i, p.cargo[i], out float profit);
                        p.silver += profit;
                        p.population += p.cargo[i]; // remembered as trade volume for the way home
                        p.cargo[i] = 0f;
                    }
                }
            }
            p.TurnBack();
            return false;
        }

        public override void ArrivedHome(WorldParty p)
        {
            Settlement home = p.Origin;
            if (home == null) return;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Trade.Earn(comp, home, p.silver, p.population + 50f);
            CompSettlementLife life = home.GetComponent<CompSettlementLife>();
            if (life != null && life.initialized)
                for (int i = 0; i < p.cargo.Length && i < life.stock.Length; i++) life.stock[i] += p.cargo[i];
        }

        public override void ArrivedHomeInstant(Settlement home, float strength, float silver) =>
            Trade.Earn(LivingWorldComponent.Instance, home, silver, silver * 0.5f + 50f);

        public override string InspectExtra(WorldParty p)
        {
            CommodityDef main = null;
            float most = 0f;
            foreach (CommodityDef c in Commodities.All)
                if (c.index < p.cargo.Length && p.cargo[c.index] > most)
                {
                    most = p.cargo[c.index];
                    main = c;
                }
            return main == null ? null : "LW_Party_Carrying".Translate(main.label).Resolve();
        }
    }

    /// <summary>Visitors: visit the colony on arrival (vanilla or Hospitality incident), then go home.</summary>
    public class PartyWorker_Visitors : PartyWorker
    {
        public override bool ArrivedAtDestination(WorldParty p)
        {
            if (p.deferredParms != null)
            {
                Faction f = p.Faction;
                IncidentParms parms = p.deferredParms;
                if (f != null && !f.HostileTo(Faction.OfPlayer) && Bridge.ExecuteDeferred(p, ContextKind.Visit)) return true;
                HospitalityCompat.Notify_VisitCalledOff(parms);
                p.deferredParms = null;
                p.TurnBack();
                return false;
            }
            return true;
        }

        public override void DestinationLost(WorldParty p)
        {
            HospitalityCompat.Notify_VisitCalledOff(p.deferredParms);
            p.deferredParms = null;
            p.TurnBack();
        }
    }
}
