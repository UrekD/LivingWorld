using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>NPC missions, then the colony's own (<see cref="PlayerDiplomacy"/>).</summary>
    public enum EnvoyMission : byte { Peace, Alliance, PlayerPeace, PlayerCallToArms, PlayerTribute, PlayerMercenary, PlayerWeregild, PlayerHireBand }

    /// <summary>Diplomacy travels: an envoy party carries a peace offer or alliance; it takes effect on arrival.</summary>
    public static class Envoys
    {
        public static void Send(LivingWorldComponent comp, FactionState from, FactionState to, EnvoyMission mission)
        {
            foreach (WorldParty p in WorldParty.All)
                if (p.kind == LWPartyDefOf.LW_Envoy && p.Faction == from.faction && p.targetFactionId == to.factionId) return;
            Settlement origin = null, dest = null;
            float best = float.MaxValue;
            foreach (CompSettlementLife s in from.settlements)
                foreach (CompSettlementLife t in to.settlements)
                {
                    float d = Find.WorldGrid.ApproxDistanceInTiles(s.Settlement.Tile, t.Settlement.Tile);
                    if (d < best && Find.WorldReachability.CanReach(s.Settlement.Tile, t.Settlement.Tile))
                    {
                        best = d;
                        origin = s.Settlement;
                        dest = t.Settlement;
                    }
                }
            if (origin == null || !PartyBudget.CanLaunchNpc())
            {
                Apply(comp, from.faction, to.faction, mission);
                return;
            }
            WorldParty party = WorldParty.Launch(LWPartyDefOf.LW_Envoy, from.faction, origin, dest);
            party.targetFactionId = to.factionId;
            party.mission = (byte)mission;
            party.strength = 30f;
        }

        public static void Apply(LivingWorldComponent comp, Faction from, Faction to, EnvoyMission mission)
        {
            if (from == null || to == null || from.defeated || to.defeated) return;
            if (mission == EnvoyMission.Peace)
            {
                if (from.HostileTo(to)) Diplomacy.MakePeace(comp, from, to);
            }
            else if (!from.HostileTo(to)) Diplomacy.MakeAlliance(comp, from, to);
        }
    }

    public class PartyWorker_Envoy : PartyWorker
    {
        public override bool ArrivedAtDestination(WorldParty p)
        {
            if (PlayerDiplomacy.IsPlayerMission((EnvoyMission)p.mission))
            {
                PlayerDiplomacy.Answer(p);
                return p.Destroyed;
            }
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Envoys.Apply(comp, p.Faction, comp.FactionById(p.targetFactionId), (EnvoyMission)p.mission);
            p.TurnBack();
            return false;
        }

        public override void ArrivedHome(WorldParty p)
        {
            if (PlayerDiplomacy.IsPlayerMission((EnvoyMission)p.mission)) PlayerDiplomacy.Notify_Home(p);
            else base.ArrivedHome(p);
        }

        public override string InspectExtra(WorldParty p)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction target = comp.FactionById(p.targetFactionId);
            if (target == null) return null;
            string s = ("LW_Envoy_Mission_" + (EnvoyMission)p.mission).Translate(target.Name, comp.FactionById(p.secondFactionId)?.Name ?? "?").Resolve();
            if (p.silver >= 1f) s += "\n" + "LW_Envoy_Silver".Translate(Mathf.RoundToInt(p.silver)).Resolve();
            return s;
        }
    }
}
