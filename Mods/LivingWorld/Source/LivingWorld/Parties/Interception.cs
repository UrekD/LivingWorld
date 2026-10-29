using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// A hostile war band that meets a player caravan on the world map fights it there (vanilla caravan ambush map)
    /// instead of marching on. A raid stopped this way never reaches the colony.
    /// </summary>
    public static class Interception
    {
        public static int StatEncounters;

        /// <summary>Called when a party enters a tile (and before it moves on).</summary>
        public static bool TryEncounter(WorldParty p)
        {
            if (p.kind != LWPartyDefOf.LW_WarBand || p.Faction == null || !p.Faction.HostileTo(Faction.OfPlayer) || p.strength < 30f) return false;
            Caravan caravan = null;
            foreach (Caravan c in Find.WorldObjects.Caravans)
            {
                if (!c.IsPlayerControlled || c.Tile != p.Tile) continue;
                if (Testing.AutoTest.Active && Testing.RoadChecks.IsTestCaravan(c)) continue; // the road job tests' caravans are not ambushed
                foreach (Pawn x in c.PawnsListForReading)
                    if (x.RaceProps.Humanlike && !x.Downed) caravan = c;
            }
            if (caravan == null || !CaravanIncidentUtility.CanFireIncidentWhichWantsToGenerateMapAt(caravan.Tile)) return false;
            return Encounter(p, caravan);
        }

        private static bool Encounter(WorldParty p, Caravan caravan)
        {
            Faction f = p.Faction;
            var parms = new PawnGroupMakerParms
            {
                groupKind = PawnGroupKindDefOf.Combat,
                faction = f,
                points = System.Math.Max(p.strength, f.def.MinPointsToGeneratePawnGroup(PawnGroupKindDefOf.Combat)),
                tile = p.Tile,
                generateFightersOnly = true,
            };
            List<Pawn> enemies = PawnGroupMakerUtility.GeneratePawns(parms).ToList();
            if (enemies.Count == 0) return false;
            bool wasRaid = p.deferredParms != null;
            var ctx = new IncidentContext
            {
                kind = ContextKind.Raid,
                originId = p.originId,
                originName = p.originName,
                factionId = f.loadID,
                partyId = p.ID,
                strengthPaid = p.strengthPaid,
                points = p.strength,
                createdTicks = Find.TickManager.TicksGame,
                mapLabel = caravan.Label,
            };
            string partyLabel = p.Label;
            p.deferredParms = null;
            p.Disband();
            StatEncounters++;
            LongEventHandler.QueueLongEvent(() =>
            {
                Map map = CaravanIncidentUtility.SetupCaravanAttackMap(caravan, enemies, sendLetterIfRelatedPawns: false);
                Bridge.Scope = ctx;
                try
                {
                    LordMaker.MakeNewLord(f, new LordJob_AssaultColony(f, canKidnap: true, canTimeoutOrFlee: true), map, enemies);
                }
                finally
                {
                    Bridge.Scope = null;
                }
                Find.TickManager.Notify_GeneratedPotentiallyHostileMap();
                TaggedString text = "LW_InterceptText".Translate(caravan.Label.Named("CARAVAN"), partyLabel.Named("PARTY"), (ctx.originName ?? "?").Named("ORIGIN"));
                if (wasRaid) text += "\n\n" + "LW_InterceptStoppedRaid".Translate();
                Find.LetterStack.ReceiveLetter("LW_InterceptLabel".Translate(), text, LetterDefOf.ThreatBig, enemies, f);
                ChronicleEventDef def = LWChronicleDefOf.LW_WarBandIntercepted;
                LivingWorldComponent.Instance.chronicle.Record(def, def.text.Formatted(caravan.Label.Named("CARAVAN"), f.Name.Named("FACTION"),
                    (ctx.originName ?? "?").Named("ORIGIN")).Resolve(), Faction.OfPlayer, f, silent: true);
            }, "GeneratingMapForNewEncounter", doAsynchronously: false, null);
            return true;
        }
    }
}
