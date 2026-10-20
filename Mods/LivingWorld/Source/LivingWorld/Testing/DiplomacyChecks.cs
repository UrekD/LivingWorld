using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plan "diplomacy": the colony's envoys. Answers are forced (yes) so the effects can be checked: peace ends the
    /// hostility and keeps the gift, tribute comes home as silver, an ally answers a call to arms. One envoy walks the
    /// whole way there and back.
    /// </summary>
    public static class DiplomacyChecks
    {
        private static readonly List<string> problems = new List<string>();
        // By id, not reference: after the halfway save/reload the envoy is a new object.
        private static int walkerId = -1, walkerFaction = -1, answersBefore;
        private static bool walkerHome;

        public static void Register()
        {
            if (AutoTest.Plan != "diplomacy") return;
            AutoTest.OnBegin.Add(Begin);
            AutoTest.Hourly.Add(() =>
            {
                if (walkerId >= 0 && !walkerHome && !WorldParty.All.Any(p => p.ID == walkerId))
                {
                    walkerHome = true;
                    Log.Message($"[LivingWorld:test] diplomacy: walking envoy done, answers {PlayerDiplomacy.StatAnswers - answersBefore}");
                }
            });
            AutoTest.Checks.Add(Checks);
        }

        /// <summary>Successive new leaders of one faction: each is of the ruling house (renamed heir) or founds a new one.</summary>
        private static void Houses(LivingWorldComponent comp)
        {
            FactionState fs = comp.factionStates.FirstOrDefault(x => x.faction != null && !x.faction.defeated && x.faction.leader?.Name is NameTriple && x.faction.def.humanlikeFaction);
            if (fs == null)
            {
                problems.Add("houses: no faction with a named leader");
                return;
            }
            Faction f = fs.faction;
            if (fs.house == null) Rulers.Succeed(fs, f.leader, succession: false);
            int renamed = 0, founded = 0;
            for (int i = 0; i < 8; i++)
            {
                string house = fs.house;
                fs.pendingNewHouse = false; // the daily chronicle would have taken the last one
                if (!f.TryGenerateNewLeader()) continue;
                var nt = (NameTriple)f.leader.Name;
                if (fs.houseLeaderId != f.leader.thingIDNumber) problems.Add("houses: the new leader's house was not decided");
                if (nt.Last != fs.house) problems.Add($"houses: leader {nt} is not of house {fs.house}");
                if (fs.house == house) renamed++;
                else founded++;
                if (fs.pendingNewHouse != (fs.house != house)) problems.Add("houses: a new house is not pending for the chronicle");
            }
            Log.Message($"[LivingWorld:test] houses: {f.Name} now {f.leader.Name.ToStringFull}{Rulers.HouseLabel(fs)}; same house {renamed}, new houses {founded}");
            if (renamed + founded == 0) problems.Add("houses: no new leader could be made");
        }

        private static void Begin()
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            comp.RebuildIndexes(seedMissing: true);
            Houses(comp);
            Map map = Neighbourhood.Home;
            PlayerDiplomacy.TestOutcome = true;
            var talkers = Find.FactionManager.AllFactionsListForReading
                .Where(f => FactionState.IsSimulated(f) && f.HasGoodwill && !f.def.permanentEnemy && PlayerDiplomacy.Destination(f, map) != null)
                .OrderBy(f => Find.WorldGrid.ApproxDistanceInTiles(map.Tile, PlayerDiplomacy.Destination(f, map).Tile)).ToList();
            if (talkers.Count < 3)
            {
                problems.Add("fewer than 3 factions to talk to");
                return;
            }
            Faction foe = talkers[0], weak = talkers[1], ally = talkers[2];
            // Peace: make them enemies first, then send a gift and answer at once.
            foe.TryAffectGoodwillWith(Faction.OfPlayer, -100 - foe.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = 3000;
            GenPlace.TryPlaceThing(silver, map.Center, map, ThingPlaceMode.Near);
            int had = Demands.ColonySilver(map), gift = PlayerDiplomacy.SuggestedGift();
            string cannot = PlayerDiplomacy.CannotSend(foe);
            WorldParty peace = PlayerDiplomacy.Send(foe, EnvoyMission.PlayerPeace, gift);
            if (peace == null)
            {
                problems.Add("peace envoy not sent: " + cannot);
                return;
            }
            if (Demands.ColonySilver(map) != had - gift) problems.Add("peace gift not taken from the colony");
            if (peace.Faction != Faction.OfPlayer || peace.silver != gift) problems.Add("peace envoy: wrong faction or gift");
            if (PlayerDiplomacy.Send(foe, EnvoyMission.PlayerPeace) != null) problems.Add("a second envoy to the same faction was allowed");
            PlayerDiplomacy.Answer(peace);
            Log.Message($"[LivingWorld:test] diplomacy: peace with {foe.Name}: hostile={foe.HostileTo(Faction.OfPlayer)} goodwill={foe.PlayerGoodwill} envoy silver={peace.silver}");
            if (foe.HostileTo(Faction.OfPlayer)) problems.Add("peace accepted but still hostile");
            if (peace.silver > 0f) problems.Add("peace accepted but the envoy still carries the gift");
            peace.Disband();
            // Tribute from a weak neighbour: the silver comes home with the envoy.
            FactionState ws = comp.StateFor(weak);
            ws.treasury = 2000f;
            foreach (CompSettlementLife s in ws.settlements) s.garrison = 1f;
            comp.RebuildIndexes(seedMissing: false);
            if (!PlayerDiplomacy.CanDemandTribute(weak)) problems.Add($"cannot demand tribute from a weak faction (strength {ws.strength:F0})");
            else
            {
                WorldParty tribute = PlayerDiplomacy.Send(weak, EnvoyMission.PlayerTribute);
                int amount = PlayerDiplomacy.TributeAmount(weak);
                PlayerDiplomacy.Answer(tribute);
                int before = Demands.ColonySilver(map);
                PlayerDiplomacy.Notify_Home(tribute);
                Log.Message($"[LivingWorld:test] diplomacy: tribute {amount} from {weak.Name}, colony silver {before}->{Demands.ColonySilver(map)}");
                if (Demands.ColonySilver(map) != before + amount) problems.Add("tribute did not arrive at the colony");
                tribute.Disband();
            }
            // Call to arms: an ally against an enemy of the colony.
            ally.TryAffectGoodwillWith(Faction.OfPlayer, 100 - ally.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            Faction enemy = PlayerDiplomacy.CallToArmsTargets(ally).FirstOrDefault();
            if (ally.PlayerRelationKind != FactionRelationKind.Ally) problems.Add("could not make an ally");
            else if (enemy == null) Log.Message("[LivingWorld:test] diplomacy: no enemy to call the ally against (skipped)");
            else
            {
                int bands = WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_WarBand && p.Faction == ally);
                WorldParty call = PlayerDiplomacy.Send(ally, EnvoyMission.PlayerCallToArms, enemy: enemy);
                PlayerDiplomacy.Answer(call);
                int after = WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_WarBand && p.Faction == ally);
                Log.Message($"[LivingWorld:test] diplomacy: {ally.Name} called against {enemy.Name}: hostile={ally.HostileTo(enemy)} war bands {bands}->{after}");
                if (!ally.HostileTo(enemy)) problems.Add("call to arms accepted but the ally is not at war");
                call.Disband();
                // Made peace with that enemy meanwhile: the call is no longer valid on arrival.
                enemy.TryAffectGoodwillWith(Faction.OfPlayer, 10 - enemy.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
                if (!enemy.HostileTo(Faction.OfPlayer) && PlayerDiplomacy.CallToArmsValid(ally, enemy)) problems.Add("call to arms still valid against a faction at peace with the colony");
            }
            // A ruler's audience, each proposal accepted: its effect must follow.
            AudienceChecks(comp, map, talkers);
            // One envoy walks there and back for real: peace with the nearest faction again.
            foe.TryAffectGoodwillWith(Faction.OfPlayer, -100 - foe.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            comp.StateFor(foe).lastPlayerEnvoyTick = FactionState.Never;
            answersBefore = PlayerDiplomacy.StatAnswers;
            WorldParty walker = PlayerDiplomacy.Send(foe, EnvoyMission.PlayerPeace);
            walkerId = walker?.ID ?? -1;
            walkerFaction = foe.loadID;
            Log.Message($"[LivingWorld:test] diplomacy: walking envoy to {walker?.Destination?.Label}, ETA {walker?.TicksToArrive().ToStringTicksToPeriod()}");
            string status = PlayerDiplomacy.CannotSend(foe);
            Log.Message($"[LivingWorld:test] diplomacy: while walking: {status}");
            if (walker != null && (status == null || PlayerDiplomacy.EnvoyTo(foe) != walker)) problems.Add("an envoy on the way is not shown for its faction");
        }

        private static void AudienceChecks(LivingWorldComponent comp, Map map, List<Faction> talkers)
        {
            Faction host = talkers.FirstOrDefault(f => !f.HostileTo(Faction.OfPlayer) && f.leader != null);
            FactionState fs = host == null ? null : comp.StateFor(host);
            if (fs == null)
            {
                Log.Message("[LivingWorld:test] diplomacy: no friendly faction with a ruler for the audience (skipped)");
                return;
            }
            // An enemy with a town the colony could reach (within the neighbourhood), so "march with us" is possible.
            float range = Neighbourhood.Radius(map.Tile, Demands.Range);
            Faction enemy = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => f != host && FactionState.IsSimulated(f) && !f.IsPlayer
                && comp.StateFor(f)?.settlements.Any(t => t.Active && SettlementOps.CanFall(t.Settlement) && Find.WorldGrid.ApproxDistanceInTiles(map.Tile, t.Settlement.Tile) <= range) == true);
            foreach (RulerGoalKind goal in new[] { RulerGoalKind.Prosper, RulerGoalKind.Conquer, RulerGoalKind.Expand, RulerGoalKind.Hold })
            {
                fs.goal = goal;
                fs.goalTargetId = goal == RulerGoalKind.Conquer ? enemy?.loadID ?? -1 : -1;
                // A colonist from the enemy's people: they should object to marching on their home.
                Pawn objector = null;
                if (goal == RulerGoalKind.Conquer && enemy != null && map.mapPawns.FreeColonists.FirstOrDefault() is Pawn c)
                {
                    objector = c;
                    comp.roots.RemoveAll(r => r.pawn == c);
                    var home = comp.StateFor(enemy).settlements.First(t => t.Active).Settlement;
                    comp.roots.Add(new PawnRoot { pawn = c, settlementId = home.ID, settlementName = home.Label, factionId = enemy.loadID });
                }
                int requests = comp.warRequests.Count, towns = fs.settlements.Count, silver = Demands.ColonySilver(map);
                RulerAudience.Open(host.leader, host, fs.settlements[0].Settlement);
                ChoiceLetter_RulerAudience l = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_RulerAudience>().LastOrDefault();
                if (l == null)
                {
                    problems.Add($"audience ({goal}): no letter");
                    continue;
                }
                l.Choices.First().action();
                comp.RebuildIndexes(seedMissing: false);
                bool ok = l.proposal switch
                {
                    AudienceProposal.TradePact => fs.tradePactUntil > Find.TickManager.TicksGame,
                    AudienceProposal.MarchWithUs => comp.warRequests.Count > requests,
                    AudienceProposal.SettleNearby => fs.settlements.Count > towns,
                    _ => Demands.ColonySilver(map) == silver + l.giftSilver, // exactly the promised gift
                };
                Log.Message($"[LivingWorld:test] diplomacy: audience of {host.Name} with goal {goal}: {l.proposal} -> {(ok ? "ok" : "FAILED")}");
                if (objector != null && l.proposal == AudienceProposal.MarchWithUs)
                {
                    bool spoke = l.objectors.Contains(objector.thingIDNumber), hurt = objector.needs?.mood?.thoughts.memories.GetFirstMemoryOfDef(LWThoughtDefOf.LW_BetrayedHome) != null;
                    Log.Message($"[LivingWorld:test] diplomacy: {objector.LabelShort} objected={spoke}, betrayed thought={hurt}");
                    if (!spoke || !hurt) problems.Add("audience: a colonist from the target's people did not object or did not mind");
                }
                if (goal == RulerGoalKind.Conquer && enemy != null && l.proposal != AudienceProposal.MarchWithUs) problems.Add("audience: a conquering ruler with a reachable enemy town did not ask to march");
                if (!ok) problems.Add($"audience {l.proposal}: accepting had no effect");
            }
        }

        private static IEnumerable<string> Checks()
        {
            foreach (string p in problems) yield return p;
            if (walkerId < 0) yield break;
            if (PlayerDiplomacy.StatAnswers <= answersBefore) yield return "walking envoy never got an answer";
            else if (!walkerHome) yield return "walking envoy never came home";
            Faction foe = LivingWorldComponent.Instance.FactionById(walkerFaction);
            if (foe != null && foe.HostileTo(Faction.OfPlayer) && PlayerDiplomacy.StatAnswers > answersBefore) yield return "walking envoy: peace accepted but still hostile";
        }
    }
}
