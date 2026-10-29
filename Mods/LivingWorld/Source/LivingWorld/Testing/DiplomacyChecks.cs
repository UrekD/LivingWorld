using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plan "diplomacy": the colony's envoys. Answers are forced (yes) so the effects can be checked: peace ends the
    /// hostility and keeps the gift, tribute comes home as silver, an ally answers a call to arms. One envoy walks the
    /// whole way there and back. It also holds the hired war bands (PLAN §4, step A2): offers and their reasons, the contract in every
    /// phase, both branches of a result, every call-off, the refund, and the contracts across the halfway save.
    /// </summary>
    public static class DiplomacyChecks
    {
        private static readonly List<string> problems = new List<string>();
        // By id, not reference: after the halfway save/reload the envoy is a new object.
        private static int walkerId = -1, walkerFaction = -1, answersBefore;
        private static bool walkerHome;

        public static void Register()
        {
            AutoTest.DailyMetrics.Add(HireMetrics);
            AutoTest.Checks.Add(NoHireLeaks);
            if (AutoTest.Plan != "diplomacy") return;
            AutoTest.BeforeSave.Add(() => hireSnapshot = HireSnapshot());
            AutoTest.AfterReload.Add(AfterReloadHires);
            AutoTest.Checks.Add(HireFinal);
            AutoTest.OnBegin.Add(Begin);
            AutoTest.Hourly.Add(() =>
            {
                if (walkerId >= 0 && !walkerHome && !WorldParty.All.Any(p => p.ID == walkerId))
                {
                    walkerHome = true;
                    Log.Message($"[LivingWorld:test] diplomacy: walking envoy done, answers {PlayerDiplomacy.StatPeaceAnswers - answersBefore}");
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
            // The colony hires war bands (the walking envoy below is a faction of its own: the contracts left for the save use others).
            HireChecks(comp, map, talkers, foe);
            // One envoy walks there and back for real: peace with the nearest faction again.
            foe.TryAffectGoodwillWith(Faction.OfPlayer, -100 - foe.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            comp.StateFor(foe).lastPlayerEnvoyTick = FactionState.Never;
            answersBefore = PlayerDiplomacy.StatPeaceAnswers;
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
            if (PlayerDiplomacy.StatPeaceAnswers <= answersBefore) yield return "walking envoy never got an answer";
            else if (!walkerHome) yield return "walking envoy never came home";
            Faction foe = LivingWorldComponent.Instance.FactionById(walkerFaction);
            if (foe != null && foe.HostileTo(Faction.OfPlayer) && PlayerDiplomacy.StatPeaceAnswers > answersBefore) yield return "walking envoy: peace accepted but still hostile";
        }

        // ---- Hired war bands (agency, step A2) ------------------------------------------------------------------

        // The scenario: one enemy of the colony that fights every hirer, the hirers (not the colony's enemies), a target town of the enemy.
        // Every case starts from HireReset (relations, garrisons, treasuries, the envoy cooldown). Contracts are checked through the real
        // path: the offer, HiredBands.Send, the envoy's answer (PlayerDiplomacy.Answer), the band's arrival (the party worker), the call-off.
        // The answers are forced (TestOutcome); where a fight is in question the target's garrison decides it (0: the band wins; a million: it
        // loses) and Battle.TestNoFall / TestFall decide whether a won fight takes or razes the town. Nothing is restored in the world: the
        // game is disposable (only the in-memory switches are put back).
        private static LivingWorldComponent hComp;
        private static Map hMap;
        private static Faction hEnemy, hFoe;
        private static readonly List<Faction> hHirers = new List<Faction>();
        private static Settlement hTarget;
        private static string hireSnapshot;
        private static int hireCases;
        private static (bool hire, bool diplomacy, bool parties, bool warfare, int maxParties) hSet;

        private static void HExpect(string c, bool ok, string what)
        {
            if (!ok) problems.Add($"hire {c}: {what}");
        }

        private static void RestoreSettings()
        {
            LivingWorldSettings st = LivingWorldMod.Stored;
            st.hireWarBands = hSet.hire;
            st.simulateDiplomacy = hSet.diplomacy;
            st.worldParties = hSet.parties;
            st.simulateWarfare = hSet.warfare;
            st.maxParties = hSet.maxParties;
        }

        private static void HCase(string name, Action body)
        {
            int before = problems.Count;
            try
            {
                body();
            }
            catch (Exception e)
            {
                problems.Add($"hire {name}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                RestoreSettings();
                Battle.TestNoFall = true;
                Battle.TestFall = null;
                HiredBands.TestNoColony = false;
                PlayerDiplomacy.TestOutcome = true;
            }
            hireCases++;
            Log.Message($"[LivingWorld:test] hire: case '{name}' done, {problems.Count - before} problems");
        }

        /// <summary>Makes a pair hostile or at peace through vanilla's own goodwill (a few tries: vanilla pulls a change toward natural goodwill).</summary>
        private static void Relation(Faction a, Faction b, bool hostile)
        {
            for (int i = 0; i < 5 && a.HostileTo(b) != hostile; i++)
                a.TryAffectGoodwillWith(b, hostile ? a.GoodwillToMakeHostile(b) : 20 - a.GoodwillWith(b), canSendMessage: false, canSendHostilityLetter: false);
        }

        private static void EnsureSilver(int amount)
        {
            int have = Demands.ColonySilver(hMap);
            if (have >= amount) return;
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = amount - have;
            GenPlace.TryPlaceThing(silver, hMap.Center, hMap, ThingPlaceMode.Near);
        }

        private static CompSettlementLife LifeOf(Settlement s) => s.GetComponent<CompSettlementLife>();

        /// <summary>The towns of the enemy the colony could march on, nearest the colony first (the target first).</summary>
        private static List<Settlement> EnemyTowns() =>
            hComp.StateFor(hEnemy).settlements.Where(t => t.Active && t.Settlement.Faction == hEnemy && !SettlementOps.Protected(t.Settlement))
                .Select(t => t.Settlement).OrderBy(t => t == hTarget ? 0 : 1).ThenBy(t => Find.WorldGrid.ApproxDistanceInTiles(hMap.Tile, t.Tile)).ToList();

        private static void ResetProblem(string message)
        {
            if (!problems.Contains(message)) problems.Add(message);
        }

        private static void HireReset()
        {
            foreach (Faction h in hHirers)
            {
                Relation(h, Faction.OfPlayer, hostile: false);
                if (h.PlayerGoodwill < 10) h.TryAffectGoodwillWith(Faction.OfPlayer, 20 - h.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
                Relation(h, hEnemy, hostile: true);
                FactionState fs = hComp.StateFor(h);
                fs.lastPlayerEnvoyTick = FactionState.Never;
                fs.treasury = 5000f;
                foreach (CompSettlementLife s in fs.settlements)
                    if (s.Active && s.Settlement.Faction == h) s.garrison = 400f;
            }
            Relation(hEnemy, Faction.OfPlayer, hostile: true);
            foreach (Faction h in hHirers)
                if (h.HostileTo(Faction.OfPlayer) || !h.HostileTo(hEnemy)) ResetProblem($"hire: {h.Name} could not be put back at peace with the colony and at war with {hEnemy.Name}; the cases after this one are not reliable");
            if (!hEnemy.HostileTo(Faction.OfPlayer)) ResetProblem($"hire: {hEnemy.Name} could not be put back at war with the colony; the cases after this one are not reliable");
            LifeOf(hTarget).garrison = 0f;
            EnsureSilver(20000);
            if (Demands.ColonySilver(hMap) < 20000) ResetProblem($"hire: the colony holds {Demands.ColonySilver(hMap)} silver, not the 20000 the cases count on");
            PlayerDiplomacy.TestOutcome = true;
            Battle.TestNoFall = true;
            Battle.TestFall = null;
        }

        private static HireOffer OfferAgainst(Faction h, Settlement town) => HiredBands.Offers(h, town).FirstOrDefault();

        private static int LetterCount(string label) => Find.LetterStack.LettersListForReading.Count(l => l.Label.Resolve() == label.CapitalizeFirst());

        private static int HireChronicle() => hComp.chronicle.entries.Count(e => e.def == LWChronicleDefOf.LW_PlayerHiredBand);

        private static float EnemyGrudge() => Diplomacy.GrudgeAgainst(hComp.StateFor(hEnemy), Faction.OfPlayer);

        private static WorldParty PartyById(int id) => id < 0 ? null : WorldParty.All.FirstOrDefault(p => p.ID == id && !p.Destroyed);

        /// <summary>The offer sent for real: its contract and the envoy that carries it (null for either: a problem was added).</summary>
        private static HireContract SendHire(string c, Faction h, Settlement town, out WorldParty envoy)
        {
            envoy = null;
            HireOffer o = OfferAgainst(h, town);
            if (o.hirer == null || o.cannot != null)
            {
                problems.Add($"hire {c}: no offer from {h.Name} against {town?.Label}: {o.cannot}");
                return null;
            }
            HireContract k = HiredBands.Send(o);
            envoy = k == null ? null : PartyById(k.envoyId);
            if (k == null || envoy == null) problems.Add($"hire {c}: the offer from {h.Name} was not sent (contract {k?.id}, envoy {envoy != null})");
            return k;
        }

        /// <summary>A hire that was answered yes: the contract marches, its band is on the map, the envoy has gone home.</summary>
        private static HireContract MarchingHire(string c, Faction h, Settlement town, out WorldParty band)
        {
            band = null;
            HireContract k = SendHire(c, h, town, out WorldParty envoy);
            if (k == null) return null;
            PlayerDiplomacy.Answer(envoy);
            if (!envoy.Destroyed) envoy.Disband();
            band = PartyById(k.bandId);
            if (k.phase != HirePhase.Marching || band == null)
            {
                problems.Add($"hire {c}: the hire was not answered yes (phase {k.phase}, band {band != null})");
                return null;
            }
            return k;
        }

        private static void HireChecks(LivingWorldComponent comp, Map map, List<Faction> talkers, Faction foe)
        {
            hComp = comp;
            hMap = map;
            hFoe = foe;
            LivingWorldSettings st = LivingWorldMod.Stored;
            hSet = (st.hireWarBands, st.simulateDiplomacy, st.worldParties, st.simulateWarfare, st.maxParties);
            HiredBands.TestRange = 500f;
            try
            {
                HCase("win chance", WinChanceCase);
                HCase("outcome of an unknown byte", () => HExpect("phase byte", HiredBands.ReadPhase(200) == HirePhase.Void && HiredBands.ReadPhase((byte)HirePhase.Done) == HirePhase.Done, "an unknown phase must read as void, a known one as itself"));
                HCase("prune", PruneCase);
                if (!HireFixture(talkers)) return;
                HCase("offers", OffersCase);
                HCase("offer reasons", ReasonsCase);
                HCase("send, caps, refusal, a yes, the result", FlowCase);
                HCase("partial acceptance", PartialCase);
                HCase("not enough silver", NoSilverCase);
                HCase("moot at the answer", MootCases);
                HCase("envoy that never answered", EnvoyEndCases);
                HCase("call-offs", CallOffCases);
                HCase("hirer turns hostile", HostileCase);
                HCase("results, both branches", OutcomeCases);
                HCase("gizmo and dialog", UiCases);
                HCase("legacy mercenary", MercenaryCase);
                HCase("prepare for removal", PrepareCase);
                HCase("contracts for the save", KeepCases);
            }
            finally
            {
                HiredBands.TestRange = null;
                HiredBands.TestNoColony = false;
                Battle.TestNoFall = false;
                Battle.TestFall = null;
                PlayerDiplomacy.TestOutcome = true;
            }
        }

        // ---- Pure --------------------------------------------------------------------------------------------

        private static ulong mcState;

        private static double McNext()
        {
            mcState ^= mcState >> 12;
            mcState ^= mcState << 25;
            mcState ^= mcState >> 27;
            return ((mcState * 2685821657736338717UL) >> 11) * (1.0 / 9007199254740992.0);
        }

        private static void WinChanceCase()
        {
            foreach (float r in new[] { 0.6f, 1f, 1.4f, 2f })
            {
                mcState = 20261028UL;
                int wins = 0;
                const int n = 20000;
                for (int i = 0; i < n; i++)
                {
                    double u1 = 0.75 + 0.5 * McNext(), u2 = 0.75 + 0.5 * McNext();
                    if (u1 * r > u2) wins++;
                }
                float mc = wins / (float)n, exact = Battle.WinChance(r);
                HExpect("win chance", Mathf.Abs(mc - exact) <= 0.01f, $"ratio {r}: closed form {exact:F4}, Monte Carlo {mc:F4}");
                HExpect("win chance", Mathf.Abs(Battle.WinChance(r) + Battle.WinChance(1f / r) - 1f) < 0.0001f, $"ratio {r}: the chances of both sides do not add up to 1");
            }
            HExpect("win chance", Battle.WinChance(0.5f) == 0f && Battle.WinChance(0f) == 0f && Battle.WinChance(1.7f) == 1f && Battle.WinChance(1000f) == 1f, "outside 0.6..1.667 the chance must be 0 or 1");
            float last = -1f;
            for (float r = 0.5f; r <= 1.8f; r += 0.01f)
            {
                float w = Battle.WinChance(r);
                HExpect("win chance", w >= last - 0.00001f && w >= 0f && w <= 1f, $"not monotonic or out of range at {r}");
                last = w;
            }
        }

        private static void PruneCase()
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            List<HireContract> saved = comp.hires;
            int now = SimClock.Now;
            comp.hires = new List<HireContract>();
            try
            {
                // 24 ended a day apart, one over a year old, one still under way.
                for (int i = 0; i < 24; i++) comp.hires.Add(new HireContract { id = 1000 + i, phase = HirePhase.Done, endedTick = now - GenDate.TicksPerDay * (i + 1) - 7 });
                comp.hires.Add(new HireContract { id = 2000, phase = HirePhase.Void, endedTick = now - GenDate.TicksPerYear - 7 });
                comp.hires.Add(new HireContract { id = 2001, phase = HirePhase.Marching, createdTick = now - GenDate.TicksPerYear * 2 });
                HiredBands.Prune(comp);
                int ended = comp.hires.Count(h => !h.Active);
                HExpect("prune", ended == 20, $"{ended} ended contracts kept, expected 20");
                HExpect("prune", comp.hires.All(h => h.id != 2000), "a contract that ended over a year ago was kept");
                HExpect("prune", comp.hires.Any(h => h.id == 2001), "a contract under way was pruned");
                HExpect("prune", comp.hires.Where(h => !h.Active).All(h => h.id <= 1019), "the oldest ended contracts were not the ones dropped");
            }
            finally
            {
                comp.hires = saved;
            }
        }

        // ---- The scenario ----------------------------------------------------------------------------------------

        private static bool HireFixture(List<Faction> talkers)
        {
            var pool = talkers.Where(f => !f.defeated && hComp.StateFor(f)?.settlements.Any(s => s.Active) == true).ToList();
            hEnemy = pool.OrderByDescending(f => hComp.StateFor(f).settlements.Count(s => s.Active)).FirstOrDefault();
            hHirers.Clear();
            if (hEnemy != null) hHirers.AddRange(pool.Where(f => f != hEnemy).OrderBy(f => f == hFoe ? 1 : 0).ThenBy(f => f.loadID).Take(3));
            hComp.RebuildIndexes(seedMissing: false);
            if (hEnemy == null || hHirers.Count < 2)
            {
                problems.Add($"hire: not enough factions for the scenario (enemy {hEnemy?.Name}, hirers {hHirers.Count})");
                return false;
            }
            hTarget = hComp.StateFor(hEnemy).settlements.Where(t => t.Active && !SettlementOps.Protected(t.Settlement))
                .OrderBy(t => Find.WorldGrid.ApproxDistanceInTiles(hMap.Tile, t.Settlement.Tile)).FirstOrDefault()?.Settlement;
            if (hTarget == null)
            {
                problems.Add("hire: the enemy has no town to march on");
                return false;
            }
            HireReset();
            foreach (Faction h in hHirers.ToArray())
            {
                HireOffer o = OfferAgainst(h, hTarget);
                if (!h.HostileTo(Faction.OfPlayer) && h.HostileTo(hEnemy) && o.cannot == null) continue;
                Log.Message($"[LivingWorld:test] hire: {h.Name} cannot be a hirer in this world: {o.cannot} (hostile to colony {h.HostileTo(Faction.OfPlayer)}, to the enemy {h.HostileTo(hEnemy)})");
                hHirers.Remove(h);
            }
            if (!hEnemy.HostileTo(Faction.OfPlayer) || hHirers.Count < 2)
            {
                problems.Add($"hire: the scenario could not be set up (enemy hostile to the colony: {hEnemy.HostileTo(Faction.OfPlayer)}, hirers: {hHirers.Count})");
                return false;
            }
            Log.Message($"[LivingWorld:test] hire: enemy {hEnemy.Name}, target {hTarget.Label} ({EnemyTowns().Count} towns to march on), hirers {string.Join(", ", hHirers.Select(h => h.Name))}");
            return true;
        }

        // ---- Offers --------------------------------------------------------------------------------------------

        private static void OffersCase()
        {
            const string c = "offers";
            HireReset();
            Faction h = hHirers[0];
            HireOffer o = OfferAgainst(h, hTarget);
            HireOffer plain = HiredBands.Offers(h).FirstOrDefault(x => x.enemy == hEnemy);
            HExpect(c, o.hirer == h && o.cannot == null, $"no offer from {h.Name}: {o.cannot}");
            HExpect(c, plain.hirer == h && plain.cannot == null && plain.target != null, $"the plain offers list has no offer against {hEnemy.Name}: {plain.cannot}");
            if (o.cannot != null) return;
            CompSettlementLife source = LifeOf(o.source), target = LifeOf(o.target);
            HExpect(c, o.source.Faction == h && o.target.Faction == hEnemy && o.target == hTarget, "the offer's towns do not belong to the hirer and the enemy");
            HExpect(c, source.garrison >= 120f && Mathf.Abs(o.strength - Tuning.T.hireShare * source.garrison) < 0.01f, $"strength {o.strength:F1} is not {Tuning.T.hireShare} of the source's garrison {source.garrison:F1}");
            HExpect(c, o.price % 50 == 0 && o.price >= Tuning.T.hireMinPrice, $"price {o.price}");
            HExpect(c, o.acceptChance >= 0.2f && o.acceptChance <= 0.95f && o.etaTicks > 0, $"acceptance {o.acceptChance:F2}, eta {o.etaTicks}");
            FactionState hs = hComp.StateFor(h);
            float att = o.strength * hs.TraitFactor(t => t.militarization) * KnowHow.BattleFactor(h);
            float hills = o.target.Tile.Tile?.hilliness >= Hilliness.LargeHills ? 1.2f : 1f;
            float def = (target.garrison + 10f) * target.Tier.fortification * hills * KnowHow.BattleFactor(hEnemy);
            HExpect(c, Mathf.Abs(o.odds - Battle.WinChance(att / def)) < 0.0001f, $"odds {o.odds:F3} against {Battle.WinChance(att / def):F3} worked out here");
            // A full garrison against a quoted half: the odds are not a certainty.
            target.garrison = 4000f;
            HExpect(c, HiredBands.Offers(h, hTarget).First().odds < 0.05f, "odds against a strong garrison were not low");
            Log.Message($"[LivingWorld:test] hire: offer {h.Name} {o.strength:F0} from {o.source.Label} against {o.target.Label} for {o.price} silver, odds {o.odds:P0}, accept {o.acceptChance:P0}, eta {o.etaTicks.ToStringTicksToPeriod()}");
            HireReset();
        }

        private static void ExpectCannot(string c, Faction h, Action mutate, string expected)
        {
            try
            {
                mutate();
                HireOffer o = HiredBands.Offers(h, hTarget).FirstOrDefault();
                HExpect(c, o.hirer == h && o.cannot == expected, $"cannot was '{o.cannot}', expected '{expected}'");
            }
            finally
            {
                RestoreSettings();
                if (hTarget.questTags != null) hTarget.questTags.Remove("lwtest");
                HireReset();
            }
        }

        private static void ReasonsCase()
        {
            Faction h = hHirers[0];
            ExpectCannot("reason: hostile hirer", h, () => Relation(h, Faction.OfPlayer, hostile: true), "LW_Hire_Hostile".Translate(h.Name));
            Relation(h, Faction.OfPlayer, hostile: true);
            HExpect("reason: hostile hirer", HiredBands.Offers(h).Count == 0, "a hostile faction still has offers in the plain list");
            HireReset();
            h.TryAffectGoodwillWith(Faction.OfPlayer, -5 - h.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            if (h.PlayerGoodwill < Tuning.T.hireMinGoodwill && !h.HostileTo(Faction.OfPlayer))
                HExpect("reason: little goodwill", OfferAgainst(h, hTarget).cannot == (string)"LW_Hire_Goodwill".Translate(h.Name, Tuning.T.hireMinGoodwill), "wrong reason for a faction that thinks too little of the colony");
            else Log.Message($"[LivingWorld:test] hire: goodwill {h.PlayerGoodwill} is not a case of little goodwill (skipped)");
            HireReset();
            ExpectCannot("reason: enemy at peace with the colony", h, () => Relation(hEnemy, Faction.OfPlayer, hostile: false), "LW_Hire_EnemyNotHostile".Translate(hEnemy.Name));
            Relation(hEnemy, Faction.OfPlayer, hostile: false);
            HExpect("reason: enemy at peace with the colony", HiredBands.Offers(h).All(x => x.enemy != hEnemy), "the plain list offers a war against a faction at peace with the colony");
            HireReset();
            ExpectCannot("reason: no war", h, () => Relation(h, hEnemy, hostile: false), "LW_Hire_NotAtWar".Translate(h.Name, hEnemy.Name));
            Relation(h, hEnemy, hostile: false);
            HExpect("reason: no war", HiredBands.Offers(h).All(x => x.enemy != hEnemy), "the plain list offers a war the hirer is not fighting");
            HireReset();
            ExpectCannot("reason: no men to spare", h, () =>
            {
                foreach (CompSettlementLife s in hComp.StateFor(h).settlements) s.garrison = 100f;
            }, "LW_Hire_NoSource".Translate(h.Name, hTarget.Label, Mathf.RoundToInt(HiredBands.Range)));
            ExpectCannot("reason: protected by a quest", h, () =>
            {
                hTarget.questTags ??= new List<string>();
                hTarget.questTags.Add("lwtest");
            }, "LW_Hire_TargetProtected".Translate(hTarget.Label));
            ExpectCannot("reason: setting off", h, () => LivingWorldMod.Stored.hireWarBands = false, "LW_Hire_Off_Setting".Translate());
            ExpectCannot("reason: diplomacy off", h, () => LivingWorldMod.Stored.simulateDiplomacy = false, "LW_Hire_Off_Diplomacy".Translate());
            ExpectCannot("reason: parties off", h, () => LivingWorldMod.Stored.worldParties = false, "LW_Hire_Off_Parties".Translate());
            ExpectCannot("reason: warfare off", h, () => LivingWorldMod.Stored.simulateWarfare = false, "LW_Hire_Off_Warfare".Translate());
            // Off: nothing is sent, and no slot is taken.
            HireOffer fresh = OfferAgainst(h, hTarget);
            int contracts = hComp.hires.Count;
            LivingWorldMod.Stored.hireWarBands = false;
            HExpect("reason: setting off", !HiredBands.Enabled && HiredBands.Send(fresh) == null && hComp.hires.Count == contracts, "an offer was sent with the setting off");
            HExpect("reason: setting off", HiredBands.Offers(h).Count == 0, "the plain list (the diplomacy dialog) offers hires with the setting off");
            LivingWorldMod.Stored.hireWarBands = hSet.hire;
            // A quest-tied target: the plain list picks another town of the enemy (or none), never the tied one.
            hTarget.questTags ??= new List<string>();
            hTarget.questTags.Add("lwtest");
            HireOffer other = HiredBands.Offers(h).FirstOrDefault(x => x.enemy == hEnemy);
            HExpect("reason: protected by a quest", other.target != hTarget, "the plain list chose a town a quest holds");
            hTarget.questTags.Remove("lwtest");
            HireReset();
        }

        // ---- The flow ----------------------------------------------------------------------------------------------

        private static void FlowCase()
        {
            const string c = "flow";
            HireReset();
            Faction h1 = hHirers[0], h2 = hHirers[1];
            int silver0 = Demands.ColonySilver(hMap);
            HireOffer o1 = OfferAgainst(h1, hTarget);
            HireContract c1 = SendHire(c, h1, hTarget, out WorldParty e1);
            if (c1 == null) return;
            HExpect(c, Demands.ColonySilver(hMap) == silver0 - o1.price, $"silver {silver0} -> {Demands.ColonySilver(hMap)}, price {o1.price}");
            HExpect(c, c1.phase == HirePhase.EnvoyOut && c1.quotedPrice == o1.price && Mathf.Abs(c1.quotedStrength - o1.strength) < 0.01f && c1.envoyId == e1.ID, "the contract does not hold what was quoted");
            HExpect(c, e1.recordId == c1.id && e1.mission == (byte)EnvoyMission.PlayerHireBand && Mathf.RoundToInt(e1.silver) == o1.price && e1.Destination == o1.source && e1.Faction == Faction.OfPlayer, "the envoy does not carry the contract");
            HExpect(c, HiredBands.ActiveCount(hComp) == 1 && HiredBands.ActiveCount(hComp, h1.loadID) == 1, "the contract does not hold a slot");
            string line = e1.kind.Worker.InspectExtra(e1);
            HExpect(c, !line.NullOrEmpty() && line.Contains(hEnemy.Name), $"the envoy's inspect line does not name the enemy: '{line}'");
            // A second hire from the same faction is refused, and the refusal costs nothing.
            int contracts = hComp.hires.Count, silver1 = Demands.ColonySilver(hMap);
            HExpect(c, HiredBands.Send(o1) == null && hComp.hires.Count == contracts && Demands.ColonySilver(hMap) == silver1, "a second hire from the same faction was allowed");
            HExpect(c, OfferAgainst(h1, hTarget).cannot == (string)"LW_Hire_CapHirer".Translate(h1.Name), "the second offer does not say why it cannot be made");
            // Another faction: allowed. A third: the cap of contracts under way.
            HireContract c2 = SendHire(c, h2, hTarget, out WorldParty e2);
            if (c2 == null) return;
            HExpect(c, HiredBands.ActiveCount(hComp) == 2, "two hirers should hold two slots");
            if (hHirers.Count > 2)
            {
                Faction h3 = hHirers[2];
                HireOffer o3 = OfferAgainst(h3, hTarget);
                HExpect(c, o3.cannot == (string)"LW_Hire_CapTotal".Translate(Tuning.T.hireMaxActive), $"the third hire's reason was '{o3.cannot}'");
                int silver3 = Demands.ColonySilver(hMap);
                HExpect(c, HiredBands.Send(o3) == null && hComp.hires.Count == contracts + 1 && Demands.ColonySilver(hMap) == silver3, "a third hire was allowed past the cap");
            }
            else Log.Message("[LivingWorld:test] hire: only two hirers: the cap of contracts under way is not tried with a third");
            // The second hirer says no: the silver walks home with the envoy, the slot is free.
            PlayerDiplomacy.TestOutcome = false;
            PlayerDiplomacy.Answer(e2);
            PlayerDiplomacy.TestOutcome = true;
            HExpect(c, c2.phase == HirePhase.Refused && c2.endedTick >= 0, $"a refusal left the contract {c2.phase}");
            HExpect(c, Mathf.RoundToInt(e2.silver) == c2.quotedPrice && PartyById(c2.bandId) == null, "a refusal kept the silver or sent a band");
            HExpect(c, HiredBands.ActiveCount(hComp) == 1, "a refused contract still holds a slot");
            PlayerDiplomacy.Notify_Home(e2);
            HExpect(c, Demands.ColonySilver(hMap) == silver0 - o1.price, $"the refused silver did not come home: {Demands.ColonySilver(hMap)} against {silver0 - o1.price}");
            e2.Disband();
            HExpect(c, c2.phase == HirePhase.Refused, "the envoy coming home changed an ended contract");
            // The first says yes with its full strength.
            FactionState fs1 = hComp.StateFor(h1);
            CompSettlementLife source = LifeOf(o1.source);
            float treasury = fs1.treasury, garrison = source.garrison;
            PlayerDiplomacy.Answer(e1);
            HExpect(c, c1.phase == HirePhase.Marching && c1.netFee == c1.quotedPrice && Mathf.Abs(c1.sentStrength - c1.quotedStrength) < 0.01f, $"a yes: phase {c1.phase}, net fee {c1.netFee} of {c1.quotedPrice}, sent {c1.sentStrength:F1} of {c1.quotedStrength:F1}");
            HExpect(c, e1.silver < 0.5f, "the envoy still carries silver the hirer took");
            HExpect(c, Mathf.Abs(fs1.treasury - (treasury + c1.netFee)) < 0.01f && Mathf.Abs(source.garrison - (garrison - c1.sentStrength)) < 0.01f, "the fee did not reach the hirer's treasury, or the band's men did not leave the garrison");
            WorldParty band = PartyById(c1.bandId);
            if (band == null)
            {
                problems.Add("hire flow: a yes sent no band");
                return;
            }
            HExpect(c, band.recordId == c1.id && band.Faction == h1 && band.kind == LWPartyDefOf.LW_WarBand && band.Destination == hTarget && band.Origin == o1.source && Mathf.Abs(band.strength - c1.sentStrength) < 0.01f, "the band does not carry the contract");
            HExpect(c, HiredBands.ContractOf(band) == c1, "the band's contract is not found from the band");
            string bandLine = band.kind.Worker.InspectExtra(band);
            HExpect(c, !bandLine.NullOrEmpty() && bandLine.Contains(hTarget.Label), $"the band's inspect line does not say who paid for what: '{bandLine}'");
            // The envoy coming home after a yes leaves the marching contract alone.
            e1.Disband();
            HExpect(c, c1.phase == HirePhase.Marching && c1.bandId == band.ID && HiredBands.ActiveCount(hComp) == 1, "the envoy coming home changed the marching contract");
            // The band arrives and wins: the result is told and remembered exactly once.
            int results = HiredBands.StatResults, chronicle = HireChronicle();
            float grudge = EnemyGrudge();
            int goodwill = h1.PlayerGoodwill;
            string label = "LW_Hire_ResultLabel_Plundered".Translate(c1.targetName).Resolve();
            int letters = LetterCount(label);
            band.kind.Worker.ArrivedAtDestination(band);
            HExpect(c, c1.phase == HirePhase.Done && c1.completed && c1.outcome == "Plundered" && c1.endedTick >= 0 && !c1.refunded, $"a won fight: phase {c1.phase}, outcome {c1.outcome}");
            HExpect(c, HiredBands.StatResults == results + 1 && HireChronicle() == chronicle + 1 && LetterCount(label) == letters + 1, "the result was not told once");
            HExpect(c, Mathf.Abs(EnemyGrudge() - (grudge + 15f)) < 0.01f, $"the enemy's grudge against the colony went {grudge:F1} -> {EnemyGrudge():F1}, expected +15");
            HExpect(c, h1.PlayerGoodwill >= goodwill, "the hirer thinks less of the colony after a fight it was paid for");
            HExpect(c, band.recordId == -1 && HiredBands.ActiveCount(hComp) == 0, "the contract still marks the band or holds a slot");
            HiredBands.Complete(c1, hTarget, new BattleResult { attackerWon = true, plunder = new float[Commodities.Count] });
            HExpect(c, HiredBands.StatResults == results + 1 && HireChronicle() == chronicle + 1 && LetterCount(label) == letters + 1 && Mathf.Abs(EnemyGrudge() - (grudge + 15f)) < 0.01f, "a second Complete did something");
            if (!band.Destroyed) band.Disband();
            HireReset();
        }

        private static void PartialCase()
        {
            const string c = "partial";
            HireReset();
            Faction h = hHirers[0];
            int silver0 = Demands.ColonySilver(hMap);
            HireContract k = SendHire(c, h, hTarget, out WorldParty envoy);
            if (k == null) return;
            // Between the offer and the answer the source lost men: the hirer sends less (0.6 of what it has) and keeps a share of the fee.
            CompSettlementLife source = LifeOf(hComp.StateFor(h).settlements.First(s => s.Settlement.ID == k.sourceId).Settlement);
            source.garrison = 300f;
            PlayerDiplomacy.Answer(envoy);
            float sent = 180f;
            int net = Mathf.RoundToInt(k.quotedPrice * sent / k.quotedStrength);
            HExpect(c, k.phase == HirePhase.Marching && Mathf.Abs(k.sentStrength - sent) < 0.01f && k.netFee == net && net < k.quotedPrice, $"partial: phase {k.phase}, sent {k.sentStrength:F1}, net fee {k.netFee} (expected {net} of {k.quotedPrice})");
            HExpect(c, Mathf.RoundToInt(envoy.silver) == k.quotedPrice - k.netFee, $"the envoy carries {envoy.silver} instead of the difference {k.quotedPrice - k.netFee}");
            WorldParty band = PartyById(k.bandId);
            HExpect(c, band != null && band.recordId == k.id && Mathf.Abs(band.strength - sent) < 0.01f, "the band is not the smaller one");
            PlayerDiplomacy.Notify_Home(envoy);
            HExpect(c, Demands.ColonySilver(hMap) == silver0 - k.netFee, $"the difference did not come home: silver {Demands.ColonySilver(hMap)} against {silver0 - k.netFee}");
            envoy.Disband();
            // The band is destroyed marching (nothing of it left): the contract is void and holds no slot.
            band?.Disband();
            HExpect(c, k.phase == HirePhase.Void && HiredBands.ActiveCount(hComp) == 0, $"a band destroyed on the road left the contract {k.phase}");
            // The same with no room for a band: the result letter says why some silver comes home.
            HireReset();
            k = SendHire(c, h, hTarget, out envoy);
            if (k == null) return;
            LifeOf(hComp.StateFor(h).settlements.First(s => s.Settlement.ID == k.sourceId).Settlement).garrison = 300f;
            LivingWorldMod.Stored.maxParties = WorldParty.All.Count;
            PlayerDiplomacy.Answer(envoy);
            LivingWorldMod.Stored.maxParties = hSet.maxParties;
            string back = "LW_Hire_YesPartial".Translate((k.quotedPrice - k.netFee).Named("BACK")).Resolve();
            HExpect(c, k.phase == HirePhase.Done && k.netFee < k.quotedPrice, $"instant partial: phase {k.phase}, net fee {k.netFee} of {k.quotedPrice}");
            HExpect(c, Find.LetterStack.LettersListForReading.Any(l => l is ChoiceLetter cl && cl.Text.Resolve().Contains(back)), "the result letter of a smaller band does not tell of the silver that comes home");
            envoy.Disband();
            HireReset();
        }

        private static void NoSilverCase()
        {
            const string c = "no silver";
            HireReset();
            Faction h = hHirers[0];
            HireOffer o = OfferAgainst(h, hTarget);
            int have = Demands.ColonySilver(hMap), contracts = hComp.hires.Count;
            Demands.TakeColonySilver(hMap, have);
            try
            {
                HExpect(c, HiredBands.Send(o) == null && hComp.hires.Count == contracts && HiredBands.ActiveCount(hComp) == 0, "a hire was sent with no silver, or left a contract behind");
            }
            finally
            {
                EnsureSilver(20000);
            }
        }

        private static void MootCases()
        {
            Moot("quest", () =>
            {
                hTarget.questTags ??= new List<string>();
                hTarget.questTags.Add("lwtest");
            }, () => hTarget.questTags.Remove("lwtest"), "LW_Hire_Why_TargetProtected");
            Moot("enemy at peace with the colony", () => Relation(hEnemy, Faction.OfPlayer, hostile: false), () => { }, "LW_Hire_Why_EnemyPeace");
            Moot("no war between hirer and enemy", () => Relation(hHirers[0], hEnemy, hostile: false), () => { }, "LW_Hire_Why_Peace");
            Moot("hirer hostile to the colony", () => Relation(hHirers[0], Faction.OfPlayer, hostile: true), () => { }, "LW_Hire_Why_HirerHostile");
            Moot("source short of men", () => hComp.StateFor(hHirers[0]).settlements.ForEach(s => s.garrison = 50f), () => { }, "LW_Hire_Why_SourceShort");
            Moot("town changed hands", () => hTarget.SetFaction(hHirers[1]), () => hTarget.SetFaction(hEnemy), "LW_Hire_Why_TargetHands");
            Moot("setting off", () => LivingWorldMod.Stored.hireWarBands = false, () => { }, "LW_Hire_Why_Disabled");
            // A map on the target: a town with a map is not the world's to march on.
            HireReset();
            HireContract k = SendHire("moot: map", hHirers[0], hTarget, out WorldParty envoy);
            if (k == null) return;
            Map generated = GetOrGenerateMapUtility.GetOrGenerateMap(hTarget.Tile, new IntVec3(150, 1, 150), null);
            try
            {
                PlayerDiplomacy.Answer(envoy);
            }
            finally
            {
                if (Current.Game.Maps.Contains(generated)) Current.Game.DeinitAndRemoveMap(generated, false);
            }
            HExpect("moot: map", k.phase == HirePhase.Moot && k.outcome == "LW_Hire_Why_TargetProtected" && PartyById(k.bandId) == null, $"a town with a map: phase {k.phase}, outcome {k.outcome}");
            envoy.Disband();
            HireReset();
        }

        private static void Moot(string name, Action mutate, Action undo, string whyKey)
        {
            string c = "moot: " + name;
            HireReset();
            int silver0 = Demands.ColonySilver(hMap);
            HireContract k = SendHire(c, hHirers[0], hTarget, out WorldParty envoy);
            if (k == null) return;
            int letters = LetterCount("LW_Hire_MootLabel".Translate(k.hirerName).Resolve());
            try
            {
                mutate();
                PlayerDiplomacy.Answer(envoy);
            }
            finally
            {
                undo();
                RestoreSettings();
            }
            HExpect(c, k.phase == HirePhase.Moot && k.outcome == whyKey, $"phase {k.phase}, outcome '{k.outcome}', expected moot because '{whyKey}'");
            HExpect(c, PartyById(k.bandId) == null && k.bandId < 0 && HiredBands.ActiveCount(hComp) == 0, "a moot hire sent a band or holds a slot");
            HExpect(c, Mathf.RoundToInt(envoy.silver) == k.quotedPrice && LetterCount("LW_Hire_MootLabel".Translate(k.hirerName).Resolve()) == letters + 1, "the silver did not stay on the envoy, or the colony was not told once");
            PlayerDiplomacy.Notify_Home(envoy);
            HExpect(c, Demands.ColonySilver(hMap) == silver0, "the silver did not come home");
            envoy.Disband();
            HireReset();
        }

        private static void EnvoyEndCases()
        {
            const string c = "envoy end";
            HireReset();
            int silver0 = Demands.ColonySilver(hMap);
            // Destroyed before it answered: the silver is lost with it and the contract is void.
            HireContract k = SendHire(c, hHirers[0], hTarget, out WorldParty envoy);
            if (k == null) return;
            envoy.Disband();
            HExpect(c, k.phase == HirePhase.Void && HiredBands.ActiveCount(hComp) == 0 && Demands.ColonySilver(hMap) == silver0 - k.quotedPrice, $"an envoy destroyed on the road: phase {k.phase}, slots {HiredBands.ActiveCount(hComp)}");
            HireReset();
            // Turned back with nobody to answer (its faction gone): moot at once, so the slot is free while the envoy walks home.
            silver0 = Demands.ColonySilver(hMap);
            k = SendHire(c, hHirers[0], hTarget, out envoy);
            if (k == null) return;
            string mootLabel = "LW_Hire_MootLabel".Translate(k.hirerName).Resolve();
            int mootLetters = LetterCount(mootLabel);
            HiredBands.Notify_EnvoyTurnedBack(envoy);
            HExpect(c, LetterCount(mootLabel) == mootLetters + 1 && k.outcome == "faction gone", "the colony was not told once that the hire was moot");
            envoy.TurnBack();
            HExpect(c, k.phase == HirePhase.Moot && HiredBands.ActiveCount(hComp) == 0, $"an envoy that turned back left the contract {k.phase}");
            PlayerDiplomacy.Notify_Home(envoy);
            envoy.Disband();
            HExpect(c, k.phase == HirePhase.Moot && Demands.ColonySilver(hMap) == silver0, "the silver of an envoy that turned back was lost");
            HireReset();
            // Home with no answer (the backstop when the envoy leaves the world coming back): moot.
            k = SendHire(c, hHirers[0], hTarget, out envoy);
            if (k == null) return;
            envoy.TurnBack();
            HExpect(c, k.phase == HirePhase.EnvoyOut, "turning back alone changed the contract");
            mootLetters = LetterCount(mootLabel);
            envoy.Disband();
            HExpect(c, k.phase == HirePhase.Moot && HiredBands.ActiveCount(hComp) == 0, $"an envoy home without an answer left the contract {k.phase}");
            HExpect(c, LetterCount(mootLabel) == mootLetters + 1, "the colony was not told once that the hire was moot (envoy home without an answer)");
            HireReset();
        }

        // ---- Call-offs -----------------------------------------------------------------------------------------------

        private static void CallOffRow(string name, Action mutate, Action undo, string whyKey, bool destinationLost = false, Action extra = null)
        {
            string c = "call-off: " + name;
            HireReset();
            Faction h = hHirers[0];
            HireContract k = MarchingHire(c, h, hTarget, out WorldParty band);
            if (k == null) return;
            FactionState hs = hComp.StateFor(h);
            hs.treasury = 5000f;
            int refunds = HiredBands.StatRefunds, results = HiredBands.StatResults, chronicle = HireChronicle();
            mutate();
            try
            {
                if (destinationLost) band.kind.Worker.DestinationLost(band);
                else band.kind.Worker.ArrivedAtDestination(band);
            }
            finally
            {
                undo();
            }
            int expect = Mathf.RoundToInt(Tuning.T.hireRefundShare * k.netFee);
            HExpect(c, k.phase == HirePhase.CalledOff && k.refunded && k.outcome == whyKey, $"phase {k.phase}, refunded {k.refunded}, outcome '{k.outcome}'");
            HExpect(c, HiredBands.StatRefunds == refunds + 1 && Mathf.Abs(hs.treasury - (5000f - expect)) < 0.01f, $"refund: {HiredBands.StatRefunds - refunds} paid, hirer's treasury {hs.treasury:F0} (expected {5000 - expect})");
            HExpect(c, HiredBands.StatResults == results && HireChronicle() == chronicle && HiredBands.ActiveCount(hComp) == 0, "a called-off band was counted as a result, or still holds a slot");
            HExpect(c, band.Destroyed || band.phase == PartyPhase.Returning, "the band did not turn back");
            extra?.Invoke();
            // Calling it off a second time changes nothing (the refund runs once).
            k.phase = HirePhase.Marching;
            HiredBands.CallOff(k, "LW_Hire_Why_NoRoute", refund: true);
            HExpect(c, HiredBands.StatRefunds == refunds + 1 && Mathf.Abs(hs.treasury - (5000f - expect)) < 0.01f && k.refunded, "a second call-off paid the refund again");
            if (!band.Destroyed) band.Disband();
            HireReset();
        }

        private static void CallOffCases()
        {
            Faction third = hHirers[1];
            CallOffRow("town changed hands", () => hTarget.SetFaction(third), () => hTarget.SetFaction(hEnemy), "LW_Hire_Why_TargetHands");
            // A band never reinforces: the town now belongs to the hirer itself.
            float garrison = 0f;
            CallOffRow("town taken by the hirer: no reinforcement", () =>
            {
                garrison = LifeOf(hTarget).garrison;
                hTarget.SetFaction(hHirers[0]);
            }, () => hTarget.SetFaction(hEnemy), "LW_Hire_Why_TargetHands", extra: () => HExpect("call-off: reinforcement", Mathf.Abs(LifeOf(hTarget).garrison - garrison) < 0.01f, "a hired band reinforced a town of its hirer"));
            CallOffRow("quest", () =>
            {
                hTarget.questTags ??= new List<string>();
                hTarget.questTags.Add("lwtest");
            }, () => hTarget.questTags.Remove("lwtest"), "LW_Hire_Why_TargetProtected");
            Map generated = null;
            CallOffRow("map generated", () => generated = GetOrGenerateMapUtility.GetOrGenerateMap(hTarget.Tile, new IntVec3(150, 1, 150), null), () =>
            {
                if (generated != null && Current.Game.Maps.Contains(generated)) Current.Game.DeinitAndRemoveMap(generated, false);
            }, "LW_Hire_Why_TargetProtected");
            CallOffRow("hirer and enemy at peace", () => Relation(hHirers[0], hEnemy, hostile: false), () => { }, "LW_Hire_Why_Peace");
            CallOffRow("enemy at peace with the colony", () => Relation(hEnemy, Faction.OfPlayer, hostile: false), () => { }, "LW_Hire_Why_EnemyPeace");
            CallOffRow("destination lost", () => { }, () => { }, "LW_Hire_Why_NoRoute", destinationLost: true);
            // No colony on the surface to refund to: forfeited with a letter, the hirer keeps the silver, and it is still only once.
            string c = "call-off: no colony";
            HireReset();
            HireContract k = MarchingHire(c, hHirers[0], hTarget, out WorldParty band);
            if (k == null) return;
            FactionState hs = hComp.StateFor(hHirers[0]);
            hs.treasury = 5000f;
            int refunds = HiredBands.StatRefunds;
            string label = "LW_Hire_RefundLostLabel".Translate(k.hirerName).Resolve();
            int letters = LetterCount(label);
            HiredBands.TestNoColony = true;
            hTarget.SetFaction(third);
            try
            {
                band.kind.Worker.ArrivedAtDestination(band);
            }
            finally
            {
                hTarget.SetFaction(hEnemy);
                HiredBands.TestNoColony = false;
            }
            HExpect(c, k.phase == HirePhase.CalledOff && k.refunded && HiredBands.StatRefunds == refunds && Mathf.Abs(hs.treasury - 5000f) < 0.01f, $"no colony: phase {k.phase}, refunds {HiredBands.StatRefunds - refunds}, treasury {hs.treasury:F0}");
            HExpect(c, LetterCount(label) == letters + 1, "the forfeited refund was not told once");
            if (!band.Destroyed) band.Disband();
            HireReset();
        }

        private static void HostileCase()
        {
            // On arrival, and when the band is called off on the way (the way is lost; the target is gone): void in every case, never a refund.
            HostileRow("hirer turns hostile", band => band.kind.Worker.ArrivedAtDestination(band));
            HostileRow("hirer turns hostile, destination lost", band => band.kind.Worker.DestinationLost(band));
            HostileRow("hirer turns hostile, target gone", band =>
            {
                band.destinationId = -1;
                band.kind.Worker.ArrivedAtDestination(band);
            });
        }

        private static void HostileRow(string c, Action<WorldParty> trigger)
        {
            HireReset();
            Faction h = hHirers[0];
            HireContract k = MarchingHire(c, h, hTarget, out WorldParty band);
            if (k == null) return;
            FactionState hs = hComp.StateFor(h);
            hs.treasury = 5000f;
            int silver0 = Demands.ColonySilver(hMap);
            int refunds = HiredBands.StatRefunds, results = HiredBands.StatResults, chronicle = HireChronicle();
            string voidLabel = "LW_Hire_VoidLabel".Translate(k.hirerName).Resolve();
            int letters = LetterCount(voidLabel);
            Relation(h, Faction.OfPlayer, hostile: true);
            if (!h.HostileTo(Faction.OfPlayer))
            {
                problems.Add($"hire {c}: {h.Name} could not be made hostile to the colony");
                if (!band.Destroyed) band.Disband();
                HireReset();
                return;
            }
            trigger(band);
            HExpect(c, k.phase == HirePhase.Void && !k.completed && !k.refunded && k.outcome == "LW_Hire_Void_Hostile", $"phase {k.phase}, completed {k.completed}, refunded {k.refunded}, outcome '{k.outcome}'");
            HExpect(c, band.recordId == -1 && HiredBands.ContractOf(band) == null, "the band still carries the contract");
            HExpect(c, HiredBands.StatRefunds == refunds && HiredBands.StatResults == results && HireChronicle() == chronicle && Mathf.Abs(hs.treasury - 5000f) < 0.01f, "a voided contract paid a refund or counted a result");
            HExpect(c, Demands.ColonySilver(hMap) == silver0, "a voided contract sent silver to the colony");
            HExpect(c, HiredBands.ActiveCount(hComp) == 0, "a voided contract holds a slot");
            HExpect(c, LetterCount(voidLabel) == letters + 1, "the colony was not told once that the hire was void");
            if (!band.Destroyed) band.Disband();
            HireReset();
        }

        // ---- Results, both branches -------------------------------------------------------------------------------------

        private sealed class Obs
        {
            public HirePhase phase;
            public string outcome;
            public bool completed, refunded;
            public int results, chronicle, letters;
            public float grudge;
            public bool band;
        }

        /// <summary>One hire carried through to its result, by the band's arrival (a party) or at once (the party budget full), and what it left behind.</summary>
        private static Obs RunOutcome(string c, bool instant, string outcome, Settlement town)
        {
            HireReset();
            Battle.TestNoFall = outcome == "Plundered" || outcome == "Repelled";
            Battle.TestFall = outcome == "Razed" ? 1 : outcome == "Captured" ? 2 : (int?)null;
            LifeOf(town).garrison = outcome == "Repelled" ? 1000000f : 0f;
            Faction h = hHirers[0];
            HireContract k = SendHire(c, h, town, out WorldParty envoy);
            if (k == null) return null;
            CompSettlementLife source = LifeOf(hComp.StateFor(h).settlements.First(s => s.Settlement.ID == k.sourceId).Settlement);
            float garrison = source.garrison;
            string label = ("LW_Hire_ResultLabel_" + outcome).Translate(k.targetName).Resolve();
            int results = HiredBands.StatResults, chronicle = HireChronicle(), letters = LetterCount(label);
            float grudge = EnemyGrudge();
            WorldParty band = null;
            if (instant)
            {
                // No room on the map for a band: the fight is at once, inside the answer.
                LivingWorldMod.Stored.maxParties = WorldParty.All.Count;
                PlayerDiplomacy.Answer(envoy);
                LivingWorldMod.Stored.maxParties = hSet.maxParties;
                HExpect(c, k.bandId < 0 && WorldParty.All.All(p => p.recordId != k.id || p == envoy), "an instant fight left a band on the map");
                HExpect(c, source.garrison > garrison - k.sentStrength - 0.01f, "the survivors did not go back to the garrison");
            }
            else
            {
                PlayerDiplomacy.Answer(envoy);
                band = PartyById(k.bandId);
                if (band == null)
                {
                    problems.Add($"hire {c}: no band marched");
                    return null;
                }
                band.kind.Worker.ArrivedAtDestination(band);
            }
            var obs = new Obs
            {
                phase = k.phase, outcome = k.outcome, completed = k.completed, refunded = k.refunded, results = HiredBands.StatResults - results,
                chronicle = HireChronicle() - chronicle, letters = LetterCount(label) - letters, grudge = EnemyGrudge() - grudge, band = band != null,
            };
            HExpect(c, k.phase == HirePhase.Done && k.outcome == outcome && k.completed && !k.refunded, $"phase {k.phase}, outcome '{k.outcome}', expected '{outcome}'");
            HExpect(c, obs.results == 1 && obs.chronicle == 1 && obs.letters == 1, $"the result was told {obs.results} times (chronicle {obs.chronicle}, letters {obs.letters})");
            HExpect(c, Mathf.Abs(obs.grudge - 15f) < 0.01f, $"the enemy's grudge grew by {obs.grudge:F1}, expected 15");
            HExpect(c, HiredBands.ActiveCount(hComp) == 0, "a finished contract still holds a slot");
            if (outcome == "Captured") HExpect(c, town.Faction == h, $"the town belongs to {town.Faction?.Name} after it was taken for {h.Name}");
            if (outcome == "Razed") HExpect(c, town.Destroyed, "a razed town still stands");
            if (!envoy.Destroyed) envoy.Disband();
            if (band != null && !band.Destroyed) band.Disband();
            if (outcome == "Captured") town.SetFaction(hEnemy);
            return obs;
        }

        private static void OutcomeCases()
        {
            List<Settlement> spare = EnemyTowns().Where(t => t != hTarget).ToList();
            foreach (string outcome in new[] { "Plundered", "Repelled", "Captured", "Razed" })
            {
                bool falls = outcome == "Captured" || outcome == "Razed";
                // A town that falls is one the enemy can lose (it keeps another), and a razed one is gone for the next case.
                Settlement a = falls ? spare.FirstOrDefault() : hTarget, b = falls ? spare.Skip(outcome == "Razed" ? 1 : 0).FirstOrDefault() : hTarget;
                int towns = SettlementOps.SettlementCount(hEnemy);
                if (a == null || (outcome == "Razed" && (b == null || towns < 3)) || (outcome == "Captured" && towns < 2))
                {
                    Log.Message($"[LivingWorld:test] hire: no spare towns of {hEnemy.Name} for the forced '{outcome}' (skipped)");
                    continue;
                }
                Obs party = RunOutcome("outcome " + outcome + " by party", instant: false, outcome, outcome == "Razed" ? b : a);
                Obs instant = RunOutcome("outcome " + outcome + " at once", instant: true, outcome, a);
                if (party != null && instant != null)
                    HExpect("outcome " + outcome, party.phase == instant.phase && party.outcome == instant.outcome && party.completed == instant.completed && party.results == instant.results
                        && party.chronicle == instant.chronicle && party.letters == instant.letters && Mathf.Abs(party.grudge - instant.grudge) < 0.01f && party.refunded == instant.refunded,
                        "the party branch and the instant branch told and remembered the result differently");
                if (outcome == "Razed") spare.Remove(b);
                if (outcome == "Razed") spare.Remove(a);
            }
        }

        // ---- The player's side -------------------------------------------------------------------------------------------

        private static void UiCases()
        {
            const string c = "ui";
            HireReset();
            CompSettlementLife tl = LifeOf(hTarget);
            List<Gizmo> gizmos = HiredBands.Gizmos(tl).ToList();
            HExpect(c, gizmos.Count == 1 && gizmos[0] is Command_Action ca && !ca.Disabled, "an enemy town does not offer the hire gizmo");
            LivingWorldMod.Stored.simulateWarfare = false;
            gizmos = HiredBands.Gizmos(tl).ToList();
            HExpect(c, gizmos.Count == 1 && gizmos[0].Disabled && gizmos[0].disabledReason == HiredBands.WhyOff(), "the gizmo is not disabled with the reason when warfare is off");
            LivingWorldMod.Stored.simulateWarfare = hSet.warfare;
            LivingWorldMod.Stored.hireWarBands = false;
            HExpect(c, HiredBands.Gizmos(tl).Count() == 0, "the gizmo shows with the setting off");
            LivingWorldMod.Stored.hireWarBands = hSet.hire;
            CompSettlementLife friendly = hComp.StateFor(hHirers[0]).settlements.FirstOrDefault(s => s.Active && s.Settlement.Faction == hHirers[0]);
            HExpect(c, friendly == null || !HiredBands.Gizmos(friendly).Any(), "a town of a friend offers the hire gizmo");
            LivingWorldMod.Stored.hireWarBands = false;
            HExpect(c, !(LifeOf(hTarget).GetGizmos().OfType<Command_Action>().Any(g => (string)g.defaultLabel == (string)"LW_Hire_Gizmo".Translate())), "the settlement comp offers the hire gizmo with the setting off");
            LivingWorldMod.Stored.hireWarBands = hSet.hire;
            HExpect(c, LifeOf(hTarget).GetGizmos().OfType<Command_Action>().Any(g => (string)g.defaultLabel == (string)"LW_Hire_Gizmo".Translate()), "the settlement comp does not offer the hire gizmo");
            // The menu's offers: every hirer fighting the town's people, each with its reason when it cannot.
            List<HireOffer> against = HiredBands.OffersAgainst(hTarget);
            foreach (Faction h in hHirers)
                HExpect(c, against.Any(o => o.hirer == h && o.cannot == null), $"{h.Name} is missing from the menu of offers against {hTarget.Label}");
            // The diplomacy dialog lists the hire for a faction that fights the enemy.
            var dialog = new Dialog_Diplomacy(hHirers[0]);
            var options = (System.Collections.IList)AccessTools.Method(typeof(Dialog_Diplomacy), "Options").Invoke(dialog, null);
            bool listed = false, sendable = false;
            foreach (object option in options)
            {
                Traverse t = Traverse.Create(option);
                string label = t.Field("label").GetValue<string>();
                if (label == null || !label.Contains(hEnemy.Name) || !label.Contains(hTarget.Label)) continue;
                listed = true;
                sendable = t.Field("disabled").GetValue<string>() == null && t.Field("send").GetValue<Action>() != null;
            }
            HExpect(c, listed && sendable, $"the diplomacy dialog for {hHirers[0].Name} does not list a hire against {hTarget.Label} that can be sent (listed {listed})");
        }

        private static void MercenaryCase()
        {
            const string c = "legacy mercenary";
            HireReset();
            Pawn fighter = Mercenaries.BestFighter(hMap);
            if (fighter == null)
            {
                Log.Message("[LivingWorld:test] hire: no colonist can be spared for a mercenary contract (skipped)");
                return;
            }
            Faction employer = hHirers[0];
            Settlement town = hComp.StateFor(employer).settlements.First(s => s.Active && s.Settlement.Faction == employer).Settlement;
            // A hire is under way for the same faction: a mercenary on contract is not its envoy, and its pay is its own.
            HireContract k = SendHire(c, employer, hTarget, out WorldParty hireEnvoy);
            if (k == null) return;
            WorldParty m = Mercenaries.Hire(fighter, employer, town, 100);
            HExpect(c, m != null && m.recordId == -1 && m.pay == 100 && m.mission == (byte)EnvoyMission.PlayerMercenary && m.Passengers.Contains(fighter), "the mercenary party was changed by the contract field");
            HExpect(c, PlayerDiplomacy.EnvoyTo(employer) == hireEnvoy, "a mercenary on contract is taken for the hire's envoy");
            if (m == null) return;
            Mercenaries.TestOutcome = 0;
            try
            {
                PlayerDiplomacy.Answer(m);
            }
            finally
            {
                Mercenaries.TestOutcome = -1;
            }
            HExpect(c, m.pay == 100 && k.phase == HirePhase.EnvoyOut && k.envoyId == hireEnvoy.ID, "the mercenary's answer changed its pay or the hire contract");
            PlayerDiplomacy.Notify_Home(m);
            HExpect(c, fighter.Spawned && !fighter.Dead, "the mercenary did not walk back in");
            m.Disband();
            HExpect(c, k.phase == HirePhase.EnvoyOut, "a mercenary party coming home changed the hire contract");
            hireEnvoy.Disband();
            HExpect(c, k.phase == HirePhase.Void, "the hire's envoy was destroyed and its contract stayed open");
            HireReset();
        }

        private static void PrepareCase()
        {
            const string c = "prepare for removal";
            HireReset();
            HireContract out1 = SendHire(c, hHirers[0], hTarget, out WorldParty envoy);
            if (out1 == null) return;
            HireContract march = MarchingHire(c, hHirers[1], hTarget, out WorldParty marchBand);
            if (march == null) return;
            HExpect(c, HiredBands.ActiveCount(hComp) == 2, "the contracts under way are not both held");
            HiredBands.Notify_PreparedForRemoval(hComp);
            HExpect(c, out1.phase == HirePhase.Void && march.phase == HirePhase.Void && HiredBands.ActiveCount(hComp) == 0, "contracts under way were not voided");
            // The parties are destroyed after the contracts end (as PrepareForRemoval does): the reason stays the removal, not a band or envoy lost on the road.
            envoy.Disband();
            marchBand.Disband();
            HExpect(c, out1.phase == HirePhase.Void && march.phase == HirePhase.Void && out1.outcome == "prepared for removal" && march.outcome == "prepared for removal", $"removing the parties changed a voided contract ('{out1.outcome}', '{march.outcome}')");
            HireReset();
        }

        /// <summary>The contracts left for the halfway save: an envoy out for real and a band marching for real (the rest are in their ended states).</summary>
        private static void KeepCases()
        {
            const string c = "kept";
            HireReset();
            // The walking envoy below is the nearest faction's: keep the envoy of a hirer that is not it, and the band of another.
            Faction other = hHirers.FirstOrDefault(h => h != hFoe);
            Faction marching = hHirers.FirstOrDefault(h => h != other);
            if (other != null && SendHire(c, other, hTarget, out WorldParty envoy) != null)
                HExpect(c, envoy != null && HiredBands.ActiveCount(hComp) == 1, "the kept envoy is not out");
            else Log.Message("[LivingWorld:test] hire: no envoy kept for the save");
            if (marching != null)
            {
                HireContract k = MarchingHire(c, marching, hTarget, out WorldParty band);
                if (k != null) HExpect(c, band != null && HiredBands.ActiveCount(hComp) >= 1, "the kept band is not marching");
            }
            else Log.Message("[LivingWorld:test] hire: no band kept for the save");
        }

        // ---- The save, the metric, the invariants ---------------------------------------------------------------------------

        private static string HireSnapshot() =>
            string.Join("|", LivingWorldComponent.Instance.hires.Select(h => $"{h.id}:{(int)h.phase}:{h.refunded}:{h.completed}:{h.netFee}:{h.quotedPrice}:{h.envoyId}:{h.bandId}:{h.endedTick}:{h.hirerId}:{h.sourceId}:{h.targetId}"));

        private static void AfterReloadHires()
        {
            string after = HireSnapshot();
            if (hireSnapshot == null) return;
            Log.Message($"[LivingWorld:test] hire: {LivingWorldComponent.Instance.hires.Count} contracts across the save");
            if (after != hireSnapshot) problems.Add($"hire: the contracts changed across the save:\nbefore {hireSnapshot}\nafter  {after}");
            foreach (HireContract h in LivingWorldComponent.Instance.hires)
                if (h.Active && !HireHasParty(h)) problems.Add($"hire: contract {h.id} ({h.phase}) lost its party across the save");
        }

        private static bool HireHasParty(HireContract h) =>
            h.phase == HirePhase.EnvoyOut ? PartyById(h.envoyId) is { } e && e.recordId == h.id : PartyById(h.bandId) is { } b && b.recordId == h.id;

        private static string HireMetrics()
        {
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (c == null || c.hires.Count == 0) return null;
            return $"hires active={c.hires.Count(h => h.Active)} done={c.hires.Count(h => h.phase == HirePhase.Done)} calledOff={c.hires.Count(h => h.phase == HirePhase.CalledOff)} "
                + $"other={c.hires.Count(h => !h.Active && h.phase != HirePhase.Done && h.phase != HirePhase.CalledOff)} sent={HiredBands.StatHires} results={HiredBands.StatResults} refunds={HiredBands.StatRefunds}";
        }

        /// <summary>Without the colony ever hiring, nothing of this step exists in the world.</summary>
        private static IEnumerable<string> NoHireLeaks()
        {
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (c == null || AutoTest.Plan == "diplomacy") yield break;
            if (c.hires.Count > 0 || HiredBands.StatHires > 0 || HiredBands.StatResults > 0 || HiredBands.StatRefunds > 0) yield return $"hire contracts without a hire: {c.hires.Count}";
        }

        private static IEnumerable<string> HireFinal()
        {
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (hireCases == 0) yield return "hire: no case ran";
            if (c == null) yield break;
            foreach (HireContract h in c.hires)
            {
                if (h.Active && !HireHasParty(h)) yield return $"hire: contract {h.id} is {h.phase} but has no party";
                if (h.completed && h.phase != HirePhase.Done) yield return $"hire: contract {h.id} is {h.phase} but was completed";
                if (h.refunded && h.phase != HirePhase.CalledOff) yield return $"hire: contract {h.id} is {h.phase} but was refunded";
            }
            if (HiredBands.ActiveCount(c) > Tuning.T.hireMaxActive) yield return $"hire: {HiredBands.ActiveCount(c)} contracts under way, more than the {Tuning.T.hireMaxActive} allowed";
            if (HiredBands.StatResults < c.hires.Count(h => h.completed)) yield return "hire: fewer results counted than contracts completed";
            if (hireSnapshot == null) yield return "hire: the contracts were never compared across the save";
        }
    }
}
