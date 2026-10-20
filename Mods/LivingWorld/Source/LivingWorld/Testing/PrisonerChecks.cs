using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plan "prisoners": three captives of a hostile faction. One is ransomed (offer letter, accept: silver in, captive
    /// gone home), one dies in captivity (grudge, chronicle, the town's visitors will hold it against the colony), one is
    /// set free (chronicle, kind words at home).
    /// </summary>
    public static class PrisonerChecks
    {
        private static readonly List<string> problems = new List<string>();

        public static void Register()
        {
            if (AutoTest.Plan != "prisoners") return;
            AutoTest.OnBegin.Add(Run);
            AutoTest.Checks.Add(() => problems);
        }

        private static Pawn Capture(Faction f, Map map)
        {
            Pawn p = PawnGenerator.GeneratePawn(f.RandomPawnKind(), f);
            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 10, c => c.Standable(map), out IntVec3 cell)) cell = map.Center;
            GenSpawn.Spawn(p, cell, map);
            p.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
            return p;
        }

        private static void Run()
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            comp.RebuildIndexes(seedMissing: true);
            Map map = Neighbourhood.Home;
            Faction f = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => FactionState.IsSimulated(x) && x.HostileTo(Faction.OfPlayer)
                                                                                         && comp.StateFor(x)?.settlements.Count > 0 && !x.def.permanentEnemy)
                        ?? Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => FactionState.IsSimulated(x) && x.HostileTo(Faction.OfPlayer) && comp.StateFor(x)?.settlements.Count > 0);
            if (f == null || map == null)
            {
                problems.Add("no hostile faction with towns");
                return;
            }
            FactionState fs = comp.StateFor(f);
            fs.treasury = System.Math.Max(fs.treasury, 5000f);
            Pawn ransomed = Capture(f, map), dying = Capture(f, map), freed = Capture(f, map);
            if (comp.prisoners.Count != 3) problems.Add($"captives recorded: {comp.prisoners.Count} of 3");
            // Ransom: offered now, accepted.
            Prisoners.TestOfferNow = true;
            int letters = Find.LetterStack.LettersListForReading.Count;
            Prisoners.DailyCheck(comp);
            Prisoners.TestOfferNow = false;
            ChoiceLetter_Ransom offer = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_Ransom>().FirstOrDefault(l => l.prisoner == ransomed);
            int silverBefore = Demands.ColonySilver(map);
            if (offer == null) problems.Add($"no ransom offer (letters {letters} -> {Find.LetterStack.LettersListForReading.Count}, offers {Prisoners.StatOffers})");
            else
            {
                offer.Choices.First().action();
                Log.Message($"[LivingWorld:test] prisoners: ransom {offer.amount} for {ransomed.LabelShort}: silver {silverBefore}->{Demands.ColonySilver(map)}, prisoner={ransomed.IsPrisonerOfColony} spawned={ransomed.Spawned}");
                if (Demands.ColonySilver(map) != silverBefore + offer.amount || ransomed.IsPrisonerOfColony || ransomed.Spawned) problems.Add("ransom: silver or release wrong");
            }
            // Dies in captivity.
            float grudge = Diplomacy.GrudgeAgainst(fs, Faction.OfPlayer);
            int chronicle = comp.chronicle.Count;
            dying.Kill(null);
            // Set free.
            freed.guest.SetGuestStatus(null);
            Prisoners.DailyCheck(comp);
            float grudgeAfter = Diplomacy.GrudgeAgainst(fs, Faction.OfPlayer);
            var home = LivingWorldComponent.WorldObjectById(comp.prisoners.Count == 0 ? -1 : comp.prisoners[0].homeId);
            Log.Message($"[LivingWorld:test] prisoners: grudge {grudge:F0}->{grudgeAfter:F0}, chronicle +{comp.chronicle.Count - chronicle}, records left {comp.prisoners.Count}");
            if (comp.chronicle.Count - chronicle < 2) problems.Add("death in captivity and release not both chronicled");
            if (comp.prisoners.Count != 0) problems.Add($"{comp.prisoners.Count} prisoner records left");
            // The town now has news of the colony's captives for its visitors.
            int towns = comp.activeSettlements.Count(s => s.captiveNews != CaptiveNews.None);
            if (towns == 0) problems.Add("no town heard of its captives");
            FreedReturns(comp, map, freed, f);
            Pleas(comp, map);
            FeudChecks(comp, map, f);
            CareerChecks(comp, f);
        }

        /// <summary>A merchant who keeps coming becomes a merchant prince; an old one retires and their child takes over.</summary>
        private static void CareerChecks(LivingWorldComponent comp, Faction f)
        {
            Settlement town = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == f && Figures.FindFor(x.ID, FigureRole.Merchant) == null);
            if (town == null)
            {
                Log.Message("[LivingWorld:test] careers skipped (no town without a merchant)");
                return;
            }
            var request = new PawnGenerationRequest(f.RandomPawnKind(), f, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, fixedBiologicalAge: 70f, fixedChronologicalAge: 70f);
            Pawn old = PawnGenerator.GeneratePawn(request);
            Find.WorldPawns.PassToWorld(old);
            Figure m = Figures.Register(old, FigureRole.Merchant, town);
            m.visits = Careers.PrinceVisits;
            int renowned = Careers.StatRenowned;
            Careers.TellRenown(m);
            Log.Message($"[LivingWorld:test] careers: {m.name} is now '{m.RoleLabel}'");
            if (!m.Renowned || Careers.StatRenowned != renowned + 1 || !m.RoleLabel.Contains("prince")) problems.Add("careers: a merchant of five visits did not become a merchant prince");
            Careers.TestRetireNow = true;
            Careers.DailyCheck(comp);
            Figure heir = Figures.FindFor(town.ID, FigureRole.Merchant);
            Log.Message($"[LivingWorld:test] careers: {m.name} status {m.status}, heir {heir?.name} (successor of {heir?.successorOf}), parent link {heir?.pawn?.GetFather() == old || heir?.pawn?.GetMother() == old}, kept {Figures.ShouldKeep(old)}");
            if (m.status != FigureStatus.Retired || heir == null || heir.successorOf != m.id) problems.Add("careers: the old merchant did not retire to a successor");
            else if (heir.pawn.GetFather() != old && heir.pawn.GetMother() != old) problems.Add("careers: the successor is not the old merchant's child");
            if (!Figures.ShouldKeep(old)) problems.Add("careers: the retired merchant is not kept for their heir");
            // A merchant prince brings a heavier purse when leading their caravan in.
            Map map = Neighbourhood.Home;
            Figure prince = Figures.FindFor(town.ID, FigureRole.Merchant);
            if (prince != null && map != null)
            {
                prince.visits = Careers.PrinceVisits;
                if (Find.WorldPawns.Contains(prince.pawn)) Find.WorldPawns.RemovePawn(prince.pawn);
                if (!CellFinder.TryFindRandomCellNear(map.Center, map, 10, c => c.Standable(map), out IntVec3 at)) at = map.Center;
                GenSpawn.Spawn(prince.pawn, at, map);
                int before = prince.pawn.inventory.innerContainer.TotalStackCountOfDef(ThingDefOf.Silver);
                Careers.Notify_Led(prince);
                int after = prince.pawn.inventory.innerContainer.TotalStackCountOfDef(ThingDefOf.Silver);
                Log.Message($"[LivingWorld:test] careers: merchant prince purse {before} -> {after}");
                if (after < before + 300) problems.Add("careers: a merchant prince brought no extra silver");
                prince.pawn.DeSpawn();
                Find.WorldPawns.PassToWorld(prince.pawn);
            }
            // A dread warlord's war band is stronger (×1.2 at dispatch).
            if (!f.HostileTo(Faction.OfPlayer)) f.TryAffectGoodwillWith(Faction.OfPlayer, -200, canSendMessage: false, canSendHostilityLetter: false);
            Settlement camp = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == f && x != town && Figures.FindFor(x.ID, FigureRole.Warlord) is Figure wl && wl.Available)
                              ?? Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == f && x != town && Figures.FindFor(x.ID, FigureRole.Warlord) == null);
            if (camp != null && map != null)
            {
                Figure dread = Figures.FindFor(camp.ID, FigureRole.Warlord);
                if (dread == null)
                {
                    Pawn wp = PawnGenerator.GeneratePawn(new PawnGenerationRequest(f.RandomPawnKind(), f, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true));
                    Find.WorldPawns.PassToWorld(wp);
                    dread = Figures.Register(wp, FigureRole.Warlord, camp);
                }
                dread.visits = Careers.DreadRaids;
                camp.GetComponent<CompSettlementLife>().garrison += 2000f;
                IncidentParms raid = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                raid.faction = f;
                raid.points = 500f;
                raid.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                raid.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                Bridge.PreferredSource[f.loadID] = camp.ID;
                bool sent = Bridge.TryDispatchRaid(IncidentDefOf.RaidEnemy, raid);
                Log.Message($"[LivingWorld:test] careers: dread warlord's war band sent={sent}, points 500 -> {raid.points:F0}");
                if (sent && System.Math.Abs(raid.points - 500f * Careers.DreadWarlordPoints) > 1f) problems.Add("careers: a dread warlord's war band is not stronger");
                if (!sent) Log.Message("[LivingWorld:test] careers: dread warlord check skipped (no war band could set out)");
                // Old now, but leading that band in: no retirement until it has arrived.
                if (sent)
                {
                    dread.pawn.ageTracker.AgeBiologicalTicks = (long)(70f * GenDate.TicksPerYear);
                    Careers.TestRetireNow = true;
                    Careers.DailyCheck(comp);
                    if (dread.status != FigureStatus.Active) problems.Add("careers: a warlord retired while leading a war band to the colony");
                }
                foreach (WorldParty band in WorldParty.All.Where(x => x.deferredParms == raid).ToList()) band.Disband();
            }

            // Vanilla must not reuse a retired parent as some group's random pawn.
            var isValid = HarmonyLib.AccessTools.Method(typeof(PawnGenerator), "IsValidCandidateToRedress");
            bool reusable = (bool)isValid.Invoke(null, new object[] { old, new PawnGenerationRequest(old.kindDef, f) });
            Log.Message($"[LivingWorld:test] careers: retired parent reusable by vanilla = {reusable}");
            if (reusable) problems.Add("careers: vanilla may reuse a retired parent as a random pawn");
            // With notable people off, nobody retires (no heir could be registered): the figure carries on unchanged.
            Settlement town3 = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == f && Figures.FindFor(x.ID, FigureRole.Merchant) == null && x != town);
            if (town3 != null)
            {
                Pawn old3 = PawnGenerator.GeneratePawn(new PawnGenerationRequest(f.RandomPawnKind(), f, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, fixedBiologicalAge: 70f, fixedChronologicalAge: 70f));
                Find.WorldPawns.PassToWorld(old3);
                Figure m3 = Figures.Register(old3, FigureRole.Merchant, town3);
                bool was = LivingWorldMod.Stored.figures;
                LivingWorldMod.Stored.figures = false;
                Figure none = Careers.Retire(comp, m3);
                LivingWorldMod.Stored.figures = was;
                if (none != null || m3.status != FigureStatus.Active) problems.Add("careers: a figure retired while notable people were off");
            }
            // Retiring with the list full: the retiree (the heir's parent) must survive the pruning that registering the heir does.
            var request2 = new PawnGenerationRequest(f.RandomPawnKind(), f, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, fixedBiologicalAge: 70f, fixedChronologicalAge: 70f);
            Settlement town2 = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == f && x != town && Figures.FindFor(x.ID, FigureRole.Warlord) == null);
            if (town2 != null)
            {
                Pawn old2 = PawnGenerator.GeneratePawn(request2);
                Find.WorldPawns.PassToWorld(old2);
                Figure w = Figures.Register(old2, FigureRole.Warlord, town2);
                var fillers = new List<Figure>();
                while (comp.figures.Count < Figures.MaxFigures)
                {
                    var filler = new Figure { id = comp.NextId(), role = FigureRole.Traveller, status = FigureStatus.Active, name = "filler" };
                    fillers.Add(filler);
                    comp.figures.Add(filler);
                }
                Figure heir2 = Careers.Retire(comp, w);
                bool kept = comp.figures.Contains(w);
                // Several at once at the cap: every successor's parent must still be a registered figure.
                Careers.TestRetireNow = true;
                Careers.DailyCheck(comp);
                foreach (Figure x in comp.figures)
                    if (x.successorOf >= 0 && Figures.ById(x.successorOf) == null) problems.Add($"careers: {x.name} succeeds a figure no longer registered");
                comp.figures.RemoveAll(fillers.Contains);
                Log.Message($"[LivingWorld:test] careers: retired at the cap, retiree kept={kept}, heir={heir2?.name}");
                if (!kept || heir2 == null) problems.Add("careers: retiring at the figure cap lost the retiree or the heir");
            }
        }

        /// <summary>
        /// Blood feuds: a colonist slays a warlord, the warlord's child swears to kill them; the avenger downed by that
        /// colonist bleeds out on the map, which ends it (and the town swears no new feud for a while); a second feud
        /// ends with the avenger's revenge on another colonist.
        /// </summary>
        private static void FeudChecks(LivingWorldComponent comp, Map map, Faction f)
        {
            Settlement home = Find.WorldObjects.Settlements.FirstOrDefault(s => s.Faction == f);
            Settlement home2 = Find.WorldObjects.Settlements.FirstOrDefault(s => s != home && s.Faction != null && !s.Faction.IsPlayer && s.Faction.def.humanlikeFaction && !s.Faction.Hidden);
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            if (home == null || home2 == null || colonists.Count < 2)
            {
                problems.Add("feud: no towns or colonists");
                return;
            }
            Pawn slayer = colonists[0], second = colonists[1];
            Figure avenger = Swear(home, slayer, 52f, out Pawn fallen);
            Log.Message($"[LivingWorld:test] feud: {fallen.LabelShort} slain -> avenger {avenger?.name} sworn on {avenger?.nemesisName}, child={avenger?.heirOfFallen}");
            if (avenger == null || avenger.nemesis != slayer) problems.Add("feud: no avenger swore to kill the slayer");
            else
            {
                if (!avenger.heirOfFallen) problems.Add("feud: an old warlord's avenger is not their child");
                if (!LWThoughtDefOf.LW_MarkedForVengeance.Worker.CurrentState(slayer).Active && !slayer.story.traits.HasTrait(TraitDefOf.Psychopath))
                    problems.Add("feud: the slayer does not feel marked for vengeance");
                // The colony talks about it, the hunted one most.
                InteractionDef talk = DefDatabase<InteractionDef>.GetNamed("LW_TalkFeud");
                float hunted = talk.Worker.RandomSelectionWeight(slayer, second), other = talk.Worker.RandomSelectionWeight(second, slayer);
                Log.Message($"[LivingWorld:test] feud: talk weight hunted {hunted:F2}, other {other:F2}");
                if (other <= 0f || hunted <= other) problems.Add("feud: the colony does not talk about the feud (the hunted one most)");
                // Downed by the slayer on the colony's map, then bleeds out (no instigator): the feud ends.
                int ended = Feuds.StatEnded;
                if (!CellFinder.TryFindRandomEdgeCellWith(c => c.Standable(map), map, 0f, out IntVec3 cell)) cell = map.Center;
                if (Find.WorldPawns.Contains(avenger.pawn)) Find.WorldPawns.RemovePawn(avenger.pawn);
                GenSpawn.Spawn(avenger.pawn, cell, map);
                avenger.downedBy = slayer;
                avenger.pawn.Kill(null);
                if (Feuds.StatEnded != ended + 1) problems.Add("feud: the avenger bleeding out after the slayer downed them did not end the feud");
                if (!Legends.Of(comp, slayer).Any(d => d.text.Contains(avenger.name))) problems.Add("feud: no deed for ending it");
                if (slayer.needs?.mood != null && !slayer.needs.mood.thoughts.memories.Memories.Any(m => m.def == LWThoughtDefOf.LW_FeudOver)) problems.Add("feud: no relief when it ended");
                // The town has had enough: its next warlord slain by the colony swears no new feud yet.
                int sworn = Feuds.StatSworn;
                Swear(home, slayer, 52f, out _);
                if (Feuds.StatSworn != sworn) problems.Add("feud: a new feud right after one ended");
            }
            // A second feud ends with the avenger's revenge.
            Figure avenger2 = Swear(home2, second, 30f, out _);
            if (avenger2 == null || avenger2.nemesis != second) problems.Add("feud: no second avenger");
            else
            {
                if (avenger2.heirOfFallen) problems.Add("feud: a young warlord's avenger should be a comrade");
                FeudWarningCheck(comp, map, avenger2, second);
                int avenged = Feuds.StatAvenged;
                second.Kill(new DamageInfo(DamageDefOf.Cut, 500f, instigator: avenger2.pawn));
                Log.Message($"[LivingWorld:test] feud: {avenger2.name} avenged on {avenger2.nemesisName}: {Feuds.StatAvenged - avenged}");
                if (Feuds.StatAvenged != avenged + 1 || avenger2.FeudOpen) problems.Add("feud: the slayer's death at the avenger's hands was not chronicled");
            }
            FeudReturnAndCapture(comp, map, f, home, slayer);
            FeudConqueredHome(comp, f, slayer);
            BleedOutCheck(comp, f);
            // A third feud is settled with a blood price carried by an envoy.
            Settlement home3 = Find.WorldObjects.Settlements.FirstOrDefault(x => x != home && x != home2 && x.Faction != null && !x.Faction.IsPlayer && x.Faction.def.humanlikeFaction
                                                                               && !x.Faction.Hidden && PlayerDiplomacy.CannotSend(x.Faction) == null);
            Figure avenger3 = home3 == null ? null : Swear(home3, slayer, 30f, out _);
            if (avenger3 == null) problems.Add("feud: no third avenger (blood price)");
            else
            {
                int price = Feuds.BloodPrice();
                Thing coins = ThingMaker.MakeThing(ThingDefOf.Silver);
                coins.stackCount = price;
                GenPlace.TryPlaceThing(coins, DropCellFinder.TradeDropSpot(map), map, ThingPlaceMode.Near);
                WorldParty envoy = PlayerDiplomacy.Send(home3.Faction, EnvoyMission.PlayerWeregild, price);
                if (envoy == null) problems.Add($"feud: could not send a blood price ({PlayerDiplomacy.CannotSend(home3.Faction)})");
                else
                {
                    PlayerDiplomacy.TestOutcome = true;
                    PlayerDiplomacy.Answer(envoy);
                    PlayerDiplomacy.TestOutcome = null;
                    Log.Message($"[LivingWorld:test] feud: blood price {price} to {home3.Faction.Name}: settled={!avenger3.FeudOpen}");
                    if (avenger3.FeudOpen) problems.Add("feud: the blood price was taken but the oath stands");
                    PlayerDiplomacy.Notify_Home(envoy);
                    envoy.Disband();
                }
            }
        }

        /// <summary>A flying-capable animal told about a job that already went back to the pool (def cleared) must not throw.</summary>
        private static void FlightCheck(Map map, List<string> problems)
        {
            PawnKindDef chickenKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Chicken");
            if (chickenKind == null) return;
            Pawn hen = PawnGenerator.GeneratePawn(chickenKind);
            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 8, c => c.Standable(map), out IntVec3 cell)) cell = map.Center;
            GenSpawn.Spawn(hen, cell, map);
            Log.Message($"[LivingWorld:test] flight: {hen.LabelShort} flight={hen.flight != null}, can ever fly={hen.flight?.CanEverFly}");
            if (hen.flight != null && hen.flight.CanEverFly)
            {
                Verse.AI.Job pooled = JobMaker.MakeJob(JobDefOf.Wait);
                pooled.def = null; // what a job returned to the pool looks like
                try
                {
                    hen.flight.Notify_JobStarted(pooled);
                }
                catch (System.Exception e)
                {
                    problems.Add("flight: a pooled job threw in Notify_JobStarted (" + e.GetType().Name + ")");
                }
            }
            hen.Destroy();
        }

        /// <summary>Field wounds, many times over on one pawn: never an untended wound (a stump) to carry home.</summary>
        private static void WoundCheck(Map map, List<string> problems)
        {
            Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            for (int i = 0; i < 12 && !p.Dead && !p.Downed; i++) Mercenaries.Wound(p);
            int untended = p.health.hediffSet.hediffs.Count(h => h.TendableNow());
            int missing = p.health.hediffSet.hediffs.Count(h => h is Hediff_MissingPart);
            Log.Message($"[LivingWorld:test] merc: repeated field wounds -> {p.health.hediffSet.hediffs.Count} hediffs, missing parts {missing}, untended {untended}, dead={p.Dead}");
            if (untended > 0) problems.Add("mercenary: repeated field wounds left an untended wound");
            if (missing > 0) problems.Add("mercenary: a field wound took a part off");
            p.Destroy();
        }

        /// <summary>The warlord's home town fell to another faction before the colony slew them: their own people swear it.</summary>
        private static void FeudConqueredHome(LivingWorldComponent comp, Faction f, Pawn slayer)
        {
            List<Settlement> towns = Find.WorldObjects.Settlements.Where(x => x.Faction == f && !Feuds.RecentlyEnded(comp, x.ID)).ToList();
            Faction other = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => x != f && !x.IsPlayer && !x.Hidden && x.def.humanlikeFaction && !x.defeated && FactionState.IsSimulated(x));
            if (towns.Count < 2 || other == null)
            {
                Log.Message("[LivingWorld:test] feud: conquered-home check skipped (needs two towns and another faction)");
                return;
            }
            Settlement lost = towns[0];
            var request = new PawnGenerationRequest(f.RandomPawnKind(), f, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, fixedBiologicalAge: 50f, fixedChronologicalAge: 50f);
            Pawn old = PawnGenerator.GeneratePawn(request);
            Find.WorldPawns.PassToWorld(old);
            Figure fallen = Figures.Register(old, FigureRole.Warlord, lost);
            SettlementOps.Capture(lost, other);
            old.Kill(new DamageInfo(DamageDefOf.Cut, 500f, instigator: slayer));
            Figure avenger = comp.figures.LastOrDefault(x => x.FeudOpen && x.avenges == fallen?.name);
            Settlement at = avenger == null ? null : LivingWorldComponent.WorldObjectById(avenger.homeId) as Settlement;
            Log.Message($"[LivingWorld:test] feud: home {lost.Label} fell to {other.Name}; avenger {avenger?.name} of {at?.Label} ({at?.Faction?.Name})");
            if (avenger == null || avenger.factionId != f.loadID || at?.Faction != f) problems.Add("feud: a conquered home gave the feud to the wrong people");
        }

        /// <summary>An open feud brings the avenger back after a while; capturing the avenger breaks the feud.</summary>
        private static void FeudReturnAndCapture(LivingWorldComponent comp, Map map, Faction f, Settlement used, Pawn slayer)
        {
            Settlement home4 = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == f && x != used);
            if (home4 == null)
            {
                Log.Message("[LivingWorld:test] feud: return/capture skipped (the faction has one town)");
                return;
            }
            Figure a = Swear(home4, slayer, 30f, out _);
            if (a == null)
            {
                problems.Add("feud: no avenger for the return/capture check");
                return;
            }
            comp.revenge.Clear(); // the slaying scheduled a revenge raid already; only the feud's return is tested here
            // Earlier steps (ransom, release) may have mended relations; vengeance raids come only from enemies.
            if (!f.HostileTo(Faction.OfPlayer)) f.TryAffectGoodwillWith(Faction.OfPlayer, -200, canSendMessage: false, canSendHostilityLetter: false);
            a.swornTick -= GenDate.TicksPerDay * (Feuds.ReturnDays + 1);
            Feuds.DailyCheck(comp);
            Log.Message($"[LivingWorld:test] feud: {a.name} returns={a.feudReturns}, pending revenge={comp.revenge.Count} (open={a.FeudOpen} alive={a.Alive} hostile={f.HostileTo(Faction.OfPlayer)} defeated={f.defeated} sworn={a.swornTick} now={Find.TickManager.TicksGame} nemesisFaction={a.nemesis?.Faction?.Name})");
            if (a.feudReturns != 1 || comp.revenge.Count != 1) problems.Add("feud: an open feud did not bring the avenger back");
            // Hunting: on the map with their nemesis, the avenger's combat AI keeps the nemesis as its target.
            if (Find.WorldPawns.Contains(a.pawn)) Find.WorldPawns.RemovePawn(a.pawn);
            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 12, c => c.Standable(map), out IntVec3 at)) at = map.Center;
            GenSpawn.Spawn(a.pawn, at, map);
            Feuds.Hunters[a.pawn] = slayer;
            a.pawn.mindState.duty = new Verse.AI.PawnDuty(DutyDefOf.AssaultColony);
            Patch_AIFightEnemy_UpdateEnemyTarget_Feuds.Postfix(a.pawn);
            Log.Message($"[LivingWorld:test] feud: hunting target {a.pawn.mindState.enemyTarget?.LabelShort} (nemesis {slayer.LabelShort})");
            if (a.pawn.mindState.enemyTarget != slayer) problems.Add("feud: the avenger does not target the slayer");
            // Breaking off (exit duty): the hunt must not keep them on the map.
            a.pawn.mindState.enemyTarget = null;
            a.pawn.mindState.duty = new Verse.AI.PawnDuty(DutyDefOf.ExitMapBestAndDefendSelf);
            Patch_AIFightEnemy_UpdateEnemyTarget_Feuds.Postfix(a.pawn);
            if (a.pawn.mindState.enemyTarget == slayer) problems.Add("feud: a fleeing avenger is still forced onto the slayer");
            a.pawn.mindState.duty = null;
            // Recovering from a downing forgets who did it.
            a.downedBy = slayer;
            Feuds.Notify_Undowned(a.pawn);
            if (a.downedBy != null) problems.Add("feud: an old downing is still credited after recovery");
            comp.revenge.Clear();
            if (Find.WorldPawns.Contains(a.pawn)) Find.WorldPawns.RemovePawn(a.pawn);
            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 10, c => c.Standable(map), out IntVec3 cell)) cell = map.Center;
            if (!a.pawn.Spawned) GenSpawn.Spawn(a.pawn, cell, map);
            a.pawn.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Prisoner);
            Log.Message($"[LivingWorld:test] feud: {a.name} captured, open={a.FeudOpen}, hunting={Feuds.Hunters.ContainsKey(a.pawn)}");
            if (a.FeudOpen) problems.Add("feud: capturing the avenger did not break the feud");
            if (!Feuds.RecentlyEnded(comp, home4.ID)) problems.Add("feud: no cooldown after the feud was broken");
            // A blood price taken while a return raid is pending calls the raid off.
            comp.feudEnded.Remove(home4.ID);
            Figure b = Swear(home4, slayer, 30f, out _);
            if (b == null) problems.Add("feud: no avenger for the blood-price cancel check");
            else
            {
                comp.revenge.Clear();
                b.swornTick -= GenDate.TicksPerDay * (Feuds.ReturnDays + 1);
                Feuds.DailyCheck(comp);
                int pending = comp.revenge.Count;
                Feuds.Settle(comp, f, 0);
                Log.Message($"[LivingWorld:test] feud: return pending {pending} -> {comp.revenge.Count} after the blood price");
                if (pending != 1 || comp.revenge.Count != 0) problems.Add("feud: a settled feud's return raid is still pending");
            }
        }

        /// <summary>Friendly visitors standing by the hunted colonist warn them of the feud.</summary>
        private static void FeudWarningCheck(LivingWorldComponent comp, Map map, Figure avenger, Pawn hunted)
        {
            Faction friend = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => FactionState.IsSimulated(x) && !x.HostileTo(Faction.OfPlayer)
                                                                                             && x.loadID != avenger.factionId && comp.StateFor(x)?.settlements.Count > 0);
            if (friend == null || !hunted.Spawned)
            {
                Log.Message("[LivingWorld:test] feud warning skipped (no friendly faction)");
                return;
            }
            var pawns = new List<Pawn>();
            for (int i = 0; i < 2; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(friend.RandomPawnKind(), friend);
                if (!CellFinder.TryFindRandomCellNear(hunted.Position, map, 3, c => c.Standable(map), out IntVec3 cell)) cell = hunted.Position;
                GenSpawn.Spawn(p, cell, map);
                pawns.Add(p);
            }
            var lord = LordMaker.MakeNewLord(friend, new LordJob_VisitColony(friend, map.Center), map, pawns);
            var home = comp.StateFor(friend).settlements[0].Settlement;
            comp.contexts.Add(new IncidentContext { id = comp.NextId(), kind = ContextKind.Visit, originId = home.ID, originName = home.Label, factionId = friend.loadID,
                                                    lordId = lord.loadID, createdTicks = Find.TickManager.TicksGame - GenDate.TicksPerHour * 4 });
            int before = VisitorStories.StatFeudWarnings;
            if (!hunted.Awake()) RestUtility.WakeUp(hunted); // a sleeping colonist cannot be told (vanilla refuses the interaction)
            VisitorStories.TestWarnAlways = true;
            VisitorStories.HourlyTick(comp);
            VisitorStories.TestWarnAlways = false;
            Log.Message($"[LivingWorld:test] feud: visitors' warnings {before} -> {VisitorStories.StatFeudWarnings}");
            if (VisitorStories.StatFeudWarnings <= before)
                foreach (Pawn t in pawns)
                    Log.Message($"[LivingWorld:test] feud warning diag: teller {t.LabelShort} spawned={t.Spawned} lord={t.GetLord()?.LordJob?.GetType().Name} recent={t.interactions?.InteractedTooRecentlyToInteract()} " +
                                $"canTalkHunted={t.interactions?.CanInteractNowWith(hunted, LWInteractionDefOf.LW_VisitorFeudWarning)} dist={(t.Position - hunted.Position).LengthHorizontal:F1} " +
                                $"hunted awake={hunted.Awake()} downed={hunted.Downed} mental={hunted.InMentalState} faction hostile={friend.HostileTo(Faction.OfPlayer)} ctxWarned={comp.contexts.LastOrDefault()?.warnedOfFeud}");
            if (VisitorStories.StatFeudWarnings <= before) problems.Add("feud: visitors standing by the hunted colonist gave no warning");
        }

        /// <summary>Downed by the avenger's band, the hunted colonist dies: of blood loss, the band's revenge; of anything else, a moot oath.</summary>
        private static void BleedOutCheck(LivingWorldComponent comp, Faction f)
        {
            Settlement town = Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == f);
            if (town == null) return;
            Figure Sworn(Pawn hunted)
            {
                Pawn a = PawnGenerator.GeneratePawn(new PawnGenerationRequest(f.RandomPawnKind(), f, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true));
                Find.WorldPawns.PassToWorld(a);
                Figure fig = Figures.Register(a, FigureRole.Traveller, town);
                if (fig == null) return null;
                fig.nemesis = hunted;
                fig.nemesisName = hunted.LabelShort;
                fig.avenges = "test";
                fig.nemesisDownedTick = Find.TickManager.TicksGame;
                return fig;
            }
            Pawn h1 = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer), h2 = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            Figure f1 = Sworn(h1), f2 = Sworn(h2);
            if (f1 == null || f2 == null)
            {
                problems.Add("feud: bleed-out check could not register its avengers");
                return;
            }
            int avenged = Feuds.StatAvenged;
            Hediff disease = HediffMaker.MakeHediff(HediffDefOf.Malnutrition, h1);
            Feuds.Notify_ColonistDied(comp, h1, null, disease);
            bool mootRight = Feuds.StatAvenged == avenged && !f1.FeudOpen;
            Feuds.Notify_ColonistDied(comp, h2, null, HediffMaker.MakeHediff(HediffDefOf.BloodLoss, h2));
            bool bleedRight = Feuds.StatAvenged == avenged + 1 && !f2.FeudOpen;
            Log.Message($"[LivingWorld:test] feud: died of something else -> moot={mootRight}; bled out -> avenged={bleedRight}");
            if (!mootRight) problems.Add("feud: a death of something else after the downing counted as vengeance");
            if (!bleedRight) problems.Add("feud: bleeding out after the band's downing did not count as vengeance");
            h1.Destroy();
            h2.Destroy();
            // Leave no stray figures behind for the rest of the run.
            foreach (Figure x in new[] { f1, f2 })
            {
                comp.figures.Remove(x);
                if (Find.WorldPawns.Contains(x.pawn)) Find.WorldPawns.RemovePawn(x.pawn);
                x.pawn.Destroy();
            }
        }

        /// <summary>A fresh warlord of the town, slain by the colonist; returns the avenger (or null).</summary>
        private static Figure Swear(Settlement home, Pawn slayer, float age, out Pawn fallen)
        {
            Figures.FindFor(home.ID, FigureRole.Warlord)?.pawn?.Kill(null);
            var request = new PawnGenerationRequest(home.Faction.RandomPawnKind(), home.Faction, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true, fixedBiologicalAge: age, fixedChronologicalAge: age);
            fallen = PawnGenerator.GeneratePawn(request);
            Find.WorldPawns.PassToWorld(fallen);
            Figures.Register(fallen, FigureRole.Warlord, home);
            fallen.Kill(new DamageInfo(DamageDefOf.Cut, 500f, instigator: slayer));
            Figure next = Figures.FindFor(home.ID, FigureRole.Warlord);
            return next != null && next.FeudOpen ? next : null;
        }

        /// <summary>The new colonist left a parent in their home town: a visit, then the town falls and the parent with it.</summary>
        private static void KinChecks(LivingWorldComponent comp, Map map, Pawn colonist)
        {
            Figure kin = comp.figures.FirstOrDefault(x => x.role == FigureRole.Kin && x.kinOfId == colonist.thingIDNumber);
            bool related = kin != null && colonist.relations.DirectRelationExists(PawnRelationDefOf.Parent, kin.pawn);
            Log.Message($"[LivingWorld:test] kin: {colonist.LabelShort}'s parent {kin?.name} in {kin?.homeName}, related={related}");
            if (kin == null || !related)
            {
                problems.Add("kin: the new colonist has no parent in their home town");
                return;
            }
            var home = LivingWorldComponent.WorldObjectById(kin.homeId) as RimWorld.Planet.Settlement;
            Faction f = kin.pawn.Faction;
            if (f.PlayerGoodwill < 30) f.TryAffectGoodwillWith(Faction.OfPlayer, 30 - f.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            var pawns = new List<Pawn>();
            for (int i = 0; i < 3; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(f.RandomPawnKind(), f);
                CellFinder.TryFindRandomCellNear(map.Center, map, 10, c => c.Standable(map), out IntVec3 cell);
                GenSpawn.Spawn(p, cell, map);
                pawns.Add(p);
            }
            var lord = Verse.AI.Group.LordMaker.MakeNewLord(f, new LordJob_VisitColony(f, map.Center), map, pawns);
            Figures.Notify_GroupArrived(lord, new IncidentContext { id = comp.NextId(), kind = ContextKind.Visit, originId = kin.homeId, originName = kin.homeName, factionId = f.loadID, lordId = lord.loadID });
            Log.Message($"[LivingWorld:test] kin: parent came to visit={kin.pawn.Spawned}");
            if (!kin.pawn.Spawned) problems.Add("kin: the parent did not come with their town's visitors");
            // Back home, then the town falls.
            if (kin.pawn.Spawned)
            {
                kin.pawn.GetLord()?.RemovePawn(kin.pawn);
                kin.pawn.DeSpawn();
                Find.WorldPawns.PassToWorld(kin.pawn);
            }
            Kin.Notify_HomeRazed(home);
            bool grief = colonist.needs?.mood?.thoughts.memories.Memories.Any(m => m.def.defName.Contains("Died") && m.otherPawn == kin.pawn) == true;
            Log.Message($"[LivingWorld:test] kin: town razed, parent dead={kin.pawn.Dead}, colonist grieves={grief}");
            if (!kin.pawn.Dead) problems.Add("kin: the parent survived a forced death");
        }

        /// <summary>The freed captive is a figure of their town and comes back with its visitors, bringing a gift.</summary>
        private static void FreedReturns(LivingWorldComponent comp, Map map, Pawn freed, Faction f)
        {
            Figure fig = Figures.Of(freed);
            if (fig == null || fig.role != FigureRole.Freed)
            {
                problems.Add("freed captive not remembered as a figure");
                return;
            }
            // Home again, and their people now friendly enough to visit.
            if (freed.Spawned) freed.DeSpawn();
            if (!Find.WorldPawns.Contains(freed)) Find.WorldPawns.PassToWorld(freed);
            if (f.PlayerGoodwill < 30) f.TryAffectGoodwillWith(Faction.OfPlayer, 30 - f.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            // Counted before the group exists: making its lord already runs the arrival hook (the captive may come then).
            int silver = Demands.ColonySilver(map), before = Figures.StatFreedReturns;
            var pawns = new List<Pawn>();
            for (int i = 0; i < 3; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(f.RandomPawnKind(), f);
                CellFinder.TryFindRandomCellNear(map.Center, map, 10, c => c.Standable(map), out IntVec3 cell);
                GenSpawn.Spawn(p, cell, map);
                pawns.Add(p);
            }
            var lord = Verse.AI.Group.LordMaker.MakeNewLord(f, new LordJob_VisitColony(f, map.Center), map, pawns);
            var ctx = new IncidentContext { id = comp.NextId(), kind = ContextKind.Visit, originId = fig.homeId, originName = fig.homeName, factionId = f.loadID, lordId = lord.loadID };
            Log.Message($"[LivingWorld:test] prisoners: freed {freed.LabelShort} before the visit: spawned={freed.Spawned} worldPawn={Find.WorldPawns.Contains(freed)} faction={freed.Faction?.Name} available={fig.Available} home={fig.homeName} figuresForHome={comp.figures.Count(x => x.homeId == fig.homeId && x.role == FigureRole.Freed)}");
            Figures.TestForceFreed = true;
            Figures.Notify_GroupArrived(lord, ctx);
            Figures.TestForceFreed = false;
            Log.Message($"[LivingWorld:test] prisoners: freed {freed.LabelShort} came back={freed.Spawned} with a gift: silver {silver}->{Demands.ColonySilver(map)}");
            if (Figures.StatFreedReturns == before || !freed.Spawned) problems.Add("the freed captive did not come back with their town's visitors");
        }

        /// <summary>Visitors from a starving town buy meals; one from a sacked town asks to stay and joins.</summary>
        private static void Pleas(LivingWorldComponent comp, Map map)
        {
            Faction friend = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => FactionState.IsSimulated(x) && !x.HostileTo(Faction.OfPlayer)
                                                                                             && Bridge.CanSendVisitors(x) && comp.StateFor(x)?.settlements.Count > 0);
            if (friend == null)
            {
                Log.Message("[LivingWorld:test] pleas: no friendly faction (skipped)");
                return;
            }
            Verse.AI.Group.Lord Group(HomeStory story, out IncidentContext ctx)
            {
                var pawns = new List<Pawn>();
                for (int i = 0; i < 3; i++)
                {
                    Pawn p = PawnGenerator.GeneratePawn(friend.RandomPawnKind(), friend);
                    CellFinder.TryFindRandomCellNear(map.Center, map, 10, c => c.Standable(map), out IntVec3 cell);
                    GenSpawn.Spawn(p, cell, map);
                    pawns.Add(p);
                }
                var lord = Verse.AI.Group.LordMaker.MakeNewLord(friend, new LordJob_VisitColony(friend, map.Center), map, pawns);
                var home = comp.StateFor(friend).settlements[0].Settlement;
                ctx = new IncidentContext { id = comp.NextId(), kind = ContextKind.Visit, originId = home.ID, originName = home.Label, factionId = friend.loadID,
                                            lordId = lord.loadID, createdTicks = Find.TickManager.TicksGame - GenDate.TicksPerHour * 4, story = (byte)story };
                return lord;
            }
            VisitorPleas.TestAlways = true;
            Kin.TestAlways = true;
            // Food: give the colony meals first.
            Thing meals = ThingMaker.MakeThing(ThingDefOf.MealSimple);
            meals.stackCount = 12;
            GenPlace.TryPlaceThing(meals, map.Center, map, ThingPlaceMode.Near);
            var hungry = Group(HomeStory.Starving, out IncidentContext c1);
            int silver = Demands.ColonySilver(map);
            VisitorPleas.TryPlea(comp, c1, hungry);
            ChoiceLetter_VisitorPlea food = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_VisitorPlea>().LastOrDefault(l => l.kind == PleaKind.BuyFood);
            if (food == null) problems.Add("plea: starving visitors did not ask for food");
            else
            {
                food.Choices.First().action();
                Log.Message($"[LivingWorld:test] pleas: food sold for {food.silver}, silver {silver}->{Demands.ColonySilver(map)}");
                if (Demands.ColonySilver(map) != silver + food.silver) problems.Add("plea: food not paid");
                if (food.silver < VisitorPleas.Meals * 10) problems.Add($"plea: {VisitorPleas.Meals} meals priced at only {food.silver} silver");
            }
            // Refuge.
            var shaken = Group(HomeStory.Sacked, out IncidentContext c2);
            VisitorPleas.TryPlea(comp, c2, shaken);
            ChoiceLetter_VisitorPlea refuge = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_VisitorPlea>().LastOrDefault(l => l.kind == PleaKind.Refuge);
            if (refuge == null) problems.Add("plea: sacked-town visitors did not ask for refuge");
            else
            {
                refuge.Choices.First().action();
                Log.Message($"[LivingWorld:test] pleas: {refuge.asker?.LabelShort} asked to stay, now colonist={refuge.asker?.Faction == Faction.OfPlayer}");
                if (refuge.asker?.Faction != Faction.OfPlayer) problems.Add("plea: the refugee did not join");
                else KinChecks(comp, map, refuge.asker);
            }
            // Mercenary contract: visitors from a town at war hire the colony's best fighter.
            var warring = Group(HomeStory.War, out IncidentContext c3);
            VisitorPleas.TryPlea(comp, c3, warring);
            ChoiceLetter_VisitorPlea merc = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_VisitorPlea>().LastOrDefault(l => l.kind == PleaKind.Mercenary);
            if (merc == null) problems.Add("plea: visitors from a town at war did not ask for a fighter");
            else
            {
                Pawn fighter = merc.asker;
                int silverBefore = Demands.ColonySilver(map);
                merc.Choices.First().action();
                WorldParty party = WorldParty.All.FirstOrDefault(p => p.Passengers.Contains(fighter));
                Log.Message($"[LivingWorld:test] merc: {fighter?.LabelShort} left with a party={party != null}, spawned={fighter?.Spawned}");
                if (party == null) problems.Add("mercenary: the fighter did not leave with a party");
                else
                {
                    // A colonist away with the party keeps the game from ending (as if the rest of the colony fell).
                    Find.GameEnder.gameEnding = true;
                    Patch_GameEnder_CheckOrUpdateGameOver.Postfix(Find.GameEnder);
                    if (Find.GameEnder.gameEnding) problems.Add("mercenary: the game would end with a colonist away");
                    Find.GameEnder.gameEnding = false;
                    int hurtBefore = fighter.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
                    Mercenaries.TestOutcome = 1; // wounded
                    PlayerDiplomacy.Answer(party);
                    Mercenaries.TestOutcome = -1;
                    PlayerDiplomacy.Notify_Home(party);
                    party.Disband();
                    int hurt = fighter.health.hediffSet.hediffs.Count(h => h is Hediff_Injury);
                    Log.Message($"[LivingWorld:test] merc: back home={fighter.Spawned}, wounds {hurtBefore}->{hurt}, silver {silverBefore}->{Demands.ColonySilver(map)}");
                    if (!fighter.Spawned || fighter.Dead) problems.Add("mercenary: the fighter did not come home");
                    if (hurt <= hurtBefore) problems.Add("mercenary: a wounded outcome left no wounds");
                    if (fighter.health.hediffSet.hediffs.Any(h => h is Hediff_Injury && h.TendableNow())) problems.Add("mercenary: field wounds came home untended");
                    if (Demands.ColonySilver(map) <= silverBefore) problems.Add("mercenary: no pay brought home");
                }
                StrandedCheck(map, merc, problems);
                WoundCheck(map, problems);
                FlightCheck(map, problems);
            }
            VisitorPleas.TestAlways = false;
            Kin.TestAlways = false;
        }

        /// <summary>A party removed with no map to walk into: the colonist goes on as a caravan, never destroyed.</summary>
        private static void StrandedCheck(Map map, ChoiceLetter_VisitorPlea merc, List<string> problems)
        {
            Pawn second = Mercenaries.BestFighter(map);
            Settlement town = LivingWorldComponent.WorldObjectById(merc.homeId) as Settlement;
            if (second == null || town == null)
            {
                Log.Message($"[LivingWorld:test] merc: stranded check skipped (fighter={second != null}, town={town != null})");
                return;
            }
            WorldParty party = Mercenaries.Hire(second, merc.faction, town, 100);
            if (party == null)
            {
                problems.Add("mercenary: second hire failed");
                return;
            }
            // The employer gone (or turned hostile) by the time they arrive: no fight, no roll.
            int fights = Mercenaries.StatFights;
            Mercenaries.Fight(LivingWorldComponent.Instance, party, null);
            if (Mercenaries.StatFights != fights || second.Dead) problems.Add("mercenary: fought for an employer that was gone");
            party.ReleaseToWorld();
            Caravan caravan = second.GetCaravan();
            Log.Message($"[LivingWorld:test] merc: stranded -> caravan={caravan != null}, dead={second.Dead}, destroyed={second.Destroyed}");
            if (caravan == null || second.Dead || second.Destroyed) problems.Add("mercenary: a stranded colonist did not go on as a caravan");
            party.Disband();
            if (caravan != null) CaravanEnterMapUtility.Enter(caravan, map, CaravanEnterMode.Edge);
        }
    }
}
