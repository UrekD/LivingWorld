using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plans "agency" and "agencycore" (3 days, a fresh game with a fixed seed; "agencycore" runs with Core only): holding conquered towns
    /// (PLAN §4, step A3). The cases are destructive and run on distinct towns of the generated world, one batch of them every in-game
    /// hour after the world's history is done. Nothing is restored for factions, ideologies or the world's history: the game is disposable
    /// (only in-memory switches and counters are put back). Cases that decide how the hold is offered go through the real path (an
    /// assault map made with <c>GetOrGenerateMap</c>, vanilla's own <c>AffectRelationsOnAttacked</c>, the real defeat check and the real map
    /// removal); cases about the stages of the transfer yield the town without a map (<c>Yield(town, null)</c>) and set the snapshot by hand.
    /// A record is saved and reloaded in each state, and the colony's removal-safe state is checked in memory. Tribute and loyalty (step A4) follow in
    /// <c>TributeChecks.cs</c> (the same class), on one held town the cases share and a few that end in a revolt or a loss.
    /// Every plan also gets the daily line and the check that nothing leaks when no town was ever held.
    /// </summary>
    public static partial class TributaryChecks
    {
        private const string Tag = "[LivingWorld:test] agency: ";

        private static readonly List<string> problems = new List<string>();
        private static readonly Queue<(string name, Action run)> queue = new Queue<(string, Action)>();
        private static readonly List<(string name, Action check)> later = new List<(string, Action)>();
        private static readonly HashSet<int> usedTowns = new HashSet<int>();
        private static bool queued, saved;
        private static int ran, skipped;
        private static bool skippedNow, roundTripChecked;
        private static readonly HashSet<string> completed = new HashSet<string>();
        // Ids carried over the halfway save (the objects are new after the reload).
        private static int waitingTownId = -1, heldClientId = -1, releasedClientId = -1, refusedRecordId = -1;
        private static string signature;
        private static int keptClientId = -1, keptTownId = -1;

        /// <summary>The checks of every plan, and the scenario of the agency plans.</summary>
        public static void RegisterTributaries()
        {
            AutoTest.DailyMetrics.Add(TributaryMetrics);
            AutoTest.Checks.Add(NothingLeaks);
            if (AutoTest.Plan == "agencyremoval") AutoTest.Checks.Add(RemovalSaves);
            if (AutoTest.Plan != "agency" && AutoTest.Plan != "agencycore") return;
            AutoTest.Hourly.Add(Step);
            // First, whatever else is registered: it sets the held town up for the payment due after the load (the accrued days, the treasury and the stores),
            // and WorldChecks' snapshot of the round trip must be taken after that, not before.
            AutoTest.BeforeSave.Insert(0, BeforeSave);
            AutoTest.AfterReload.Add(AfterReload);
            AutoTest.Checks.Add(Final);
        }

        private static LivingWorldComponent Comp => LivingWorldComponent.Instance;

        private static bool Agency => AutoTest.Plan.StartsWith("agency", StringComparison.Ordinal);

        private static string TributaryMetrics()
        {
            LivingWorldComponent c = Comp;
            if (c == null || (c.tributaries.Count == 0 && c.clientFactionIds.Count == 0)) return null;
            int yielded = c.tributaries.Count(r => r.state == TributaryState.Yielded), held = c.tributaries.Count(r => r.state == TributaryState.Held);
            int convoys = WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_Tribute);
            return $"tributaries live={yielded + held} yielded={yielded} held={held} ended={c.tributaries.Count - yielded - held} clients={c.clientFactionIds.Count} created={c.clientFactionsCreated} payments={c.tributaries.Sum(r => r.payments)} missed={c.tributaries.Sum(r => r.missed)} paid={c.tributaries.Sum(r => r.paidTotal):F0} convoys={convoys}";
        }

        /// <summary>Without the colony ever holding a town, nothing of this step exists in the world (the rules about clients are no-ops).</summary>
        private static IEnumerable<string> NothingLeaks()
        {
            LivingWorldComponent c = Comp;
            if (c == null || Agency) yield break;
            if (c.tributaries.Count > 0 || c.clientFactionIds.Count > 0 || c.clientFactionsCreated > 0) yield return $"tributaries without a hold: {c.tributaries.Count} records, {c.clientFactionIds.Count} clients";
            if (Tributaries.StatEnemyBonus != 0) yield return "the deed of holding a town ran without a hold";
        }

        // ---- The driver ------------------------------------------------------------------------------------

        /// <summary>Hourly: the cases, a batch per hour (a long frame is a frame the watchdog sees; the game ticks between batches).</summary>
        private static void Step()
        {
            LivingWorldComponent comp = Comp;
            if (comp == null || !comp.historyDone || saved) return;
            if (later.Count > 0)
            {
                foreach ((string name, Action check) in later.ToArray()) Run("later: " + name, check);
                later.Clear();
            }
            if (!queued)
            {
                queued = true;
                Enqueue();
                comp.RebuildIndexes(seedMissing: true);
            }
            var sw = Stopwatch.StartNew();
            while (queue.Count > 0 && sw.ElapsedMilliseconds < 20000)
            {
                (string name, Action run) = queue.Dequeue();
                Run(name, run);
            }
        }

        private static void Run(string name, Action run)
        {
            int before = problems.Count;
            var sw = Stopwatch.StartNew();
            skippedNow = false;
            try
            {
                run();
            }
            catch (Exception e)
            {
                problems.Add($"{name}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                foreach (Assault a in open.ToArray())
                {
                    try
                    {
                        Leave(a);
                    }
                    catch (Exception e)
                    {
                        Log.Message($"{Tag}an assault map could not be removed after '{name}': {e.Message}");
                    }
                }
                Tributaries.TestChoice = null;
                Tributaries.TestFailAt = null;
                Tributaries.TestFailCount = 1;
                Tributaries.TestFailAfterCreate = false;
                Tributaries.TestCapOverride = null;
                Tributaries.TestForcedMemes = null;
                Tributaries.TestRevolt = Tributaries.TestLiberate = null;
                Tributaries.TestNoColony = Tributaries.TestNoParty = Tributaries.TestPlaceFails = false;
            }
            ran++;
            if (!skippedNow) completed.Add(name);
            Log.Message($"{Tag}case '{name}' done in {sw.ElapsedMilliseconds} ms, {problems.Count - before} problems");
        }

        private static void Skip(string name, string why)
        {
            skipped++;
            skippedNow = true;
            Log.Message($"{Tag}case '{name}' skipped: {why}");
        }

        private static void Enqueue()
        {
            // The save in the middle must find these in their states: first.
            Add("quest waits across the save", WaitingAcrossSave);
            Add("quest wait ends", QuestWaitEnds);
            Add("real path: outlanders", () => RealPath("OutlanderCivil"));
            Add("real path: rough tribe", () => RealPath("TribeRough"));
            Add("real path: pirates", () => RealPath("Pirate"));
            Add("real path: savage tribe", () => RealPath("TribeSavage"));
            Add("real path: any faction", () => RealPath(null));
            Add("two maps", TwoMaps);
            Add("raze", Raze);
            Add("setting off", SettingOff);
            Add("offer limits", OfferLimits);
            Add("failure: client stage once", () => FailOnce("client stage", TransferStage.ClientReady));
            Add("failure: after the client was made", FailAfterCreate);
            Add("failure: capture stage once", () => FailOnce("capture stage", TransferStage.Captured));
            Add("failure: deed stage once", () => FailOnce("deed stage", TransferStage.DeedDone));
            Add("failure: war stage once", () => FailOnce("war stage", TransferStage.War));
            Add("failure: commit stage once", () => FailOnce("commit stage", TransferStage.Committed));
            Add("failure: aborts before ownership", FailAbort);
            Add("failure: aborts before the client", FailAbortEarly);
            Add("failure: rolls forward from the deed", () => FailRollForward("deed", TransferStage.DeedDone));
            Add("failure: rolls forward from the war", () => FailRollForward("war", TransferStage.War));
            Add("failure: rolls forward from the commit", () => FailRollForward("commit", TransferStage.Committed));
            Add("no empty client: made, then the town changed hands", () => NoEmptyClient("made", null, TributaryState.Refused));
            Add("no empty client: capture failed, then the town changed hands", () => NoEmptyClient("capture failed", TransferStage.Captured, TributaryState.Refused));
            Add("no empty client: deed failed, then the town changed hands", () => NoEmptyClient("deed failed", TransferStage.DeedDone, TributaryState.Lost));
            Add("prepare for removal with transfers under way", PrepareUnderWay);
            Add("retry waits for a quest", RetryQuest);
            Add("cap below zero", CapBelowZero);
            Add("resubmission", Resubmission);
            Add("last town", LastTown);
            Add("cap and budget", CapAndBudget);
            Add("allowConquest off", ConquestOff);
            Add("client rules", ClientRules);
            Add("tribute from a neutral client", TributeNeutral);
            Add("release", ReleaseCase);
            Add("reconcile with the setting off", ReconcileOff);
            Add("ideology: normal", IdeologyNormal);
            Add("ideology: classic mode", IdeologyClassic);
            Add("ideology: reuse branch", IdeologyReuse);
            Add("migration", Migration);
            // Tribute and loyalty: the shared held town first, the cases that end it after.
            Add("tribute: accrual", Accrual);
            Add("tribute: short", Short);
            Add("tribute: rate", RateCase);
            Add("tribute: goods", Goods);
            Add("tribute: delivery", Delivery);
            Add("tribute: pods", PodsCase);
            Add("tribute: colony gone", ColonyGone);
            Add("tribute: setting off", SwitchedOff);
            Add("tribute: accounting", Accounting);
            Add("loyalty: terms", Loyalty);
            Add("loyalty: engine against the balance table", EngineAgainstTable);
            Add("revolt: gates", RevoltGates);
            Add("tribute: gift", Gift);
            Add("tribute: convoys when the town changes", ConvoyPolicy);
            Add("revolt: breaks free", BreakFree);
            Add("revolt: liberation", Liberation);
            Add("revolt: hazard and warning", RevoltHazard);
            Add("tribute: due across the save", BoundaryTown); // last: the shared town stays held for the save
        }

        private static void Add(string name, Action run) => queue.Enqueue((name, run));

        private static void Expect(string c, bool ok, string what)
        {
            if (!ok) problems.Add($"{c}: {what}");
        }

        // ---- Helpers ---------------------------------------------------------------------------------------

        private static int Chronicle(ChronicleEventDef def) => Comp.chronicle.entries.Count(e => e.def == def);

        /// <summary>A town the case may spoil: active, nothing tying it down, and not one an earlier case used.</summary>
        private static Settlement PickTown(Func<Settlement, bool> where = null)
        {
            Map home = Neighbourhood.Home;
            foreach (CompSettlementLife c in Comp.activeSettlements.OrderBy(t => home == null ? 0f : Find.WorldGrid.ApproxDistanceInTiles(home.Tile, t.Settlement.Tile)).ThenBy(t => t.Settlement.ID))
            {
                Settlement s = c.Settlement;
                if (usedTowns.Contains(s.ID) || !c.Active || SettlementOps.Protected(s) || Tributaries.RecordFor(s.ID) != null || Tributaries.WasClient(s.Faction)) continue;
                if (where != null && !where(s)) continue;
                usedTowns.Add(s.ID);
                return s;
            }
            return null;
        }

        /// <summary>The town yielded without a map, its defeat settled by hand (the stages of the transfer are what is under test).</summary>
        private static TributaryRecord Yielded(Settlement s, DefeatSnapshot snapshot = DefeatSnapshot.Defeated)
        {
            TributaryRecord r = Tributaries.Yield(s, null);
            r.snapshot = snapshot;
            r.snapshotTick = SimClock.Now;
            return r;
        }

        private static void Pump(TributaryRecord r, int ticks = 6)
        {
            for (int i = 0; i < ticks && Comp.tributaries.Contains(r) && r.state == TributaryState.Yielded; i++) Tributaries.Tick(Comp, r);
        }

        /// <summary>The factions a won assault pleases the colony with: visible, not the old masters, hostile to them.</summary>
        private static int EnemiesOf(Faction old) => Find.FactionManager.AllFactions.Count(f => !f.Hidden && !f.IsPlayer && f != old && f.HostileTo(old));

        /// <summary>Assault maps still standing: a case that ends early (a failed check, an exception) must not leave an NPC map in the world.</summary>
        private static readonly List<Assault> open = new List<Assault>();

        private sealed class Assault
        {
            public Settlement town;
            public Map map;
            public Pawn colonist;
        }

        /// <summary>
        /// An assault as vanilla starts it: the town's map, the relations it spoils (and so the -80 cap of an attacked faction) and vanilla's
        /// recalculation of goodwill situations; then the defenders are gone and one colonist stands on the map.
        /// </summary>
        private static Assault Attack(Settlement s)
        {
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(s.Tile, new IntVec3(150, 1, 150), null);
            TaggedString letter = "";
            SettlementUtility.AffectRelationsOnAttacked(s, ref letter);
            Find.GoodwillSituationManager.RecalculateAll(canSendHostilityChangedLetter: false);
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.Where(p => p.Faction == s.Faction).ToList())
            {
                bool leader = p == s.Faction.leader;
                p.DeSpawn();
                // A faction's leader is kept for good; discarding it would leave a saved leader reference null (vanilla logs an error every load).
                Find.WorldPawns.PassToWorld(p, leader ? PawnDiscardDecideMode.KeepForever : PawnDiscardDecideMode.Discard);
            }
            Pawn colonist = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(colonist, CellFinder.RandomClosewalkCellNear(map.Center, map, 20), map);
            var a = new Assault { town = s, map = map, colonist = colonist };
            open.Add(a);
            return a;
        }

        /// <summary>The colony's people walk out (they are not on the map when it goes: a map's removal makes its hostile faction's prisoners of them), then the map is removed as the game does it.</summary>
        private static void Leave(Assault a)
        {
            open.Remove(a);
            if (a.colonist.Spawned) a.colonist.DeSpawn();
            if (!Find.WorldPawns.Contains(a.colonist)) Find.WorldPawns.PassToWorld(a.colonist);
            if (Current.Game.Maps.Contains(a.map)) Current.Game.DeinitAndRemoveMap(a.map, false);
        }

        /// <summary>The real defeat check, with the test answering the dialog; its problems (a disabled option) are the case's.</summary>
        private static void Defeat(string c, Settlement s, HoldChoice choice)
        {
            Tributaries.TestProblems.Clear();
            Tributaries.TestChoice = choice;
            try
            {
                SettlementDefeatUtility.CheckDefeated(s);
            }
            finally
            {
                Tributaries.TestChoice = null;
            }
            foreach (string p in Tributaries.TestProblems) problems.Add($"{c}: {p}");
        }

        private static Pawn SpawnThreat(Assault a, Faction f)
        {
            PawnKindDef kind = f.def.basicMemberKind ?? f.RandomPawnKind();
            Pawn p = PawnGenerator.GeneratePawn(kind, f);
            // Vanilla counts a pawn as a threat only if it can reach unfogged ground (a map's generator says so): a cell in the open, never inside a closed room.
            IntVec3 cell;
            if (!CellFinder.TryFindRandomCellNear(a.map.Center, a.map, 25, c => OpenGround(c, a.map), out cell) && !CellFinder.TryFindRandomCell(a.map, c => OpenGround(c, a.map), out cell))
                cell = CellFinder.RandomClosewalkCellNear(a.map.Center, a.map, 25);
            GenSpawn.Spawn(p, cell, a.map);
            return p;
        }

        private static bool OpenGround(IntVec3 c, Map map) => c.Standable(map) && !c.Roofed(map) && !c.Fogged(map);

        /// <summary>Why a spawned threat is (not) one for vanilla's defeat check, for a problem line.</summary>
        private static string ThreatState(Pawn p) =>
            $"hostile to the colony {p.HostileTo(Faction.OfPlayer)} ({p.Faction?.PlayerRelationKind}), spawned {p.Spawned}, fogged {p.Spawned && p.Position.Fogged(p.Map)}, active {p.mindState?.Active}, downed {p.Downed}, humanlike {p.RaceProps.Humanlike}";

        private static Faction ClientOf(TributaryRecord r) => Comp.FactionById(r.clientFactionId);

        private static string ExpectedClientDef(FactionDef old)
        {
            switch (old.defName)
            {
                case "OutlanderCivil":
                case "OutlanderRough":
                    return "OutlanderCivil";
                case "TribeCivil":
                case "TribeRough":
                case "TribeSavage":
                    return "TribeCivil";
                default:
                    return null;
            }
        }

        // ---- Cases: the real path -------------------------------------------------------------------------

        /// <summary>
        /// The whole path as a player takes it: attack, defenders gone, the hold dialog's own node (the option must not be disabled), yield (no ruin, no
        /// deed yet, a second defeat check does nothing), the real map removal (the snapshot), then the transfer and everything it must leave behind.
        /// </summary>
        private static void RealPath(string defName)
        {
            string c = "real path " + (defName ?? "any");
            Settlement s = PickTown(t => (defName == null || t.Faction.def.defName == defName) && (defName != null || !t.Faction.def.permanentEnemy));
            if (s == null)
            {
                Skip(c, "no town of that kind");
                return;
            }
            Faction old = s.Faction;
            FactionDef oldDef = old.def;
            int destroyed = Chronicle(LWChronicleDefOf.LW_PlayerDestroyedSettlement), took = Chronicle(LWChronicleDefOf.LW_PlayerTookTown), created = Comp.clientFactionsCreated;
            int factions = Find.FactionManager.AllFactionsListForReading.Count;
            Assault a = Attack(s);
            int cap = Find.GoodwillSituationManager.GetMaxGoodwill(old);
            Log.Message($"{Tag}{c}: {s.Label} of {old.Name} ({oldDef.defName}), goodwill cap while attacked {cap}");
            Expect(c, cap <= -80 || old.HostileTo(Faction.OfPlayer), "the assault did not make the town's faction a hostile one");
            var goodwill = Find.FactionManager.AllFactionsListForReading.Where(FactionState.IsSimulated).ToDictionary(f => f, f => f.PlayerGoodwill);
            Defeat(c, s, HoldChoice.Hold);
            TributaryRecord r = Tributaries.RecordFor(s.ID);
            Expect(c, r != null && r.state == TributaryState.Yielded && r.snapshot == DefeatSnapshot.Pending && r.stage == TransferStage.None, "no yielded record with a pending snapshot after holding");
            if (r == null) return;
            Expect(c, !s.Destroyed && Find.WorldObjects.DestroyedSettlementAt(s.Tile) == null, "yielding destroyed the town or made a ruin");
            Expect(c, Chronicle(LWChronicleDefOf.LW_PlayerDestroyedSettlement) == destroyed && Chronicle(LWChronicleDefOf.LW_PlayerTookTown) == took, "yielding chronicled something");
            Expect(c, goodwill.All(kv => kv.Key.PlayerGoodwill == kv.Value), "yielding changed goodwill (the +20 comes with the transfer)");
            int records = Comp.tributaries.Count;
            SettlementDefeatUtility.CheckDefeated(s);
            Expect(c, Comp.tributaries.Count == records && !s.Destroyed && !Find.WorldObjects.AnyDestroyedSettlementAt(s.Tile), "a second defeat check did something");
            Leave(a);
            Expect(c, r.snapshot == DefeatSnapshot.Defeated, $"the snapshot at the map's removal is {r.snapshot}");
            int enemies = EnemiesOf(old), bonus = Tributaries.StatEnemyBonus;
            bool onlyTown = SettlementOps.SettlementCount(old) == 1;
            Pump(r);
            Expect(c, r.state == TributaryState.Held && r.stage == TransferStage.Committed, $"the town is {r.state} at stage {r.stage} after the transfer");
            Faction client = ClientOf(r);
            if (client == null)
            {
                problems.Add($"{c}: no client faction");
                return;
            }
            string expected = ExpectedClientDef(oldDef);
            if (expected != null) Expect(c, client.def.defName == expected, $"the client's kind is {client.def.defName} for {oldDef.defName}, expected {expected}");
            else if (oldDef.defName == "Pirate") Expect(c, client.def.defName.StartsWith("Outlander", StringComparison.Ordinal), $"the client's kind is {client.def.defName} for pirates, expected an outlander one");
            Expect(c, client.def == Tributaries.ClientDefFor(oldDef), "the client's kind is not ClientDefFor of the old one");
            Expect(c, client.Name == "LW_Client_Name".Translate(r.townName).Resolve(), $"the client is called '{client.Name}'");
            Find.GoodwillSituationManager.RecalculateAll(canSendHostilityChangedLetter: false);
            int clientCap = Find.GoodwillSituationManager.GetMaxGoodwill(client);
            Expect(c, client.PlayerRelationKind == (clientCap >= 75 ? FactionRelationKind.Ally : FactionRelationKind.Neutral), $"the client is {client.PlayerRelationKind} to the colony with a cap of {clientCap}");
            War war = Diplomacy.ActiveWar(Comp, client, old);
            if (onlyTown) Expect(c, old.defeated && war == null && r.reconquestWarId < 0, "the old masters' last town fell and a reconquest war was started anyway");
            else Expect(c, war != null && r.reconquestWarId == war.id, "no active reconquest war with the old masters");
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            Expect(c, s.Faction == client && life != null && life.originalFactionId == client.loadID && !life.Occupied && life.occupiedSinceTicks < 0, "the town is not the client's own");
            Expect(c, Chronicle(LWChronicleDefOf.LW_PlayerTookTown) == took + 1, "the colony taking the town was not chronicled exactly once");
            Expect(c, Tributaries.StatEnemyBonus - bonus == enemies, $"{Tributaries.StatEnemyBonus - bonus} goodwill requests with the old masters' enemies, expected {enemies}");
            Expect(c, !SettlementOps.MustSurvive(client) && SettlementOps.CanFall(s), "a free town must be able to fall and its faction not be a required one");
            Expect(c, Comp.clientFactionsCreated == created + 1 && Find.FactionManager.AllFactionsListForReading.Count == factions + 1, "exactly one client faction was made");
            Expect(c, Tributaries.IsClient(client) && Tributaries.WasClient(client) && Tributaries.HeldFor(s) == r, "the lookups do not know the held town");
            if (keptClientId < 0 && !onlyTown)
            {
                keptClientId = client.loadID;
                keptTownId = s.ID;
            }
            else Tributaries.Release(r);
            Comp.clientFactionsCreated = created;
        }

        /// <summary>
        /// Two assaulted maps at once: the first is removed with its defenders beaten (held), the second is removed with a hostile pawn standing on
        /// it (driven out, whatever the first one decided), a third has a threat that is gone just before it is removed (held). The third is left to the
        /// world's own ticks: it must be held at the next hour.
        /// </summary>
        private static void TwoMaps()
        {
            const string c = "two maps";
            Settlement sa = PickTown(), sb = PickTown(), sc = PickTown();
            if (sa == null || sb == null || sc == null)
            {
                Skip(c, "fewer than three towns left");
                return;
            }
            Assault aa = Attack(sa), ab = Attack(sb), ac;
            Defeat(c, sa, HoldChoice.Hold);
            Defeat(c, sb, HoldChoice.Hold);
            TributaryRecord ra = Tributaries.RecordFor(sa.ID), rb = Tributaries.RecordFor(sb.ID);
            if (ra == null || rb == null)
            {
                problems.Add($"{c}: a town did not yield");
                return;
            }
            Faction oldB = sb.Faction;
            Leave(aa);
            Expect(c, ra.snapshot == DefeatSnapshot.Defeated && rb.snapshot == DefeatSnapshot.Pending, $"snapshots {ra.snapshot} and {rb.snapshot} after the first removal");
            for (int i = 0; i < 3; i++) Tributaries.Tick(Comp);
            Expect(c, ra.state == TributaryState.Held && rb.state == TributaryState.Yielded && rb.snapshot == DefeatSnapshot.Pending, "the first town did not become held on its own, or the second was touched");
            Pawn threat = SpawnThreat(ab, oldB);
            Expect(c, !SettlementDefeatUtility.IsDefeated(ab.map, oldB), $"a hostile pawn on the map left the town defeated (the case cannot tell): {ThreatState(threat)}");
            Leave(ab);
            Expect(c, rb.snapshot == DefeatSnapshot.NotDefeated, $"the second snapshot is {rb.snapshot} with a threat on the map");
            Tributaries.Tick(Comp);
            Expect(c, !Comp.tributaries.Contains(rb) && sb.Faction == oldB && !sb.Destroyed, "a town with its defenders standing was not left with its old masters");
            if (!threat.Destroyed) Find.WorldPawns.RemoveAndDiscardPawnViaGC(threat);
            Tributaries.Release(ra);
            // The reverse: a threat that is gone before the removal.
            ac = Attack(sc);
            Defeat(c, sc, HoldChoice.Hold);
            TributaryRecord rc = Tributaries.RecordFor(sc.ID);
            if (rc == null)
            {
                problems.Add($"{c}: the third town did not yield");
                return;
            }
            Pawn gone = SpawnThreat(ac, sc.Faction);
            Expect(c, !SettlementDefeatUtility.IsDefeated(ac.map, sc.Faction), $"the third threat did not count: {ThreatState(gone)}");
            gone.Destroy();
            Leave(ac);
            Expect(c, rc.snapshot == DefeatSnapshot.Defeated, $"the third snapshot is {rc.snapshot} after its threat was gone");
            int id = rc.id;
            later.Add(("the third town is held by the world's own ticks", () =>
            {
                TributaryRecord now = Comp.tributaries.FirstOrDefault(x => x.id == id);
                Expect(c, now != null && now.state == TributaryState.Held, $"the third town is {now?.state.ToString() ?? "gone"} an hour later, not held");
            }));
        }

        /// <summary>Raze keeps vanilla exactly: a ruin and the existing chronicle entry, no record.</summary>
        private static void Raze()
        {
            const string c = "raze";
            Settlement s = PickTown();
            if (s == null)
            {
                Skip(c, "no town left");
                return;
            }
            int destroyed = Chronicle(LWChronicleDefOf.LW_PlayerDestroyedSettlement), records = Comp.tributaries.Count;
            Assault a = Attack(s);
            Defeat(c, s, HoldChoice.Raze);
            Expect(c, s.Destroyed && Find.WorldObjects.AnyDestroyedSettlementAt(s.Tile), "razing left the town standing, or made no ruin");
            Expect(c, Chronicle(LWChronicleDefOf.LW_PlayerDestroyedSettlement) == destroyed + 1, "the destroyed town was not chronicled once");
            Expect(c, Comp.tributaries.Count == records, "razing made a record");
            Leave(a);
        }

        /// <summary>The setting off: no offer, vanilla's destruction (and no record).</summary>
        private static void SettingOff()
        {
            const string c = "setting off";
            Settlement s = PickTown();
            if (s == null)
            {
                Skip(c, "no town left");
                return;
            }
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool was = stored.tributaries;
            int records = Comp.tributaries.Count;
            Assault a = Attack(s);
            try
            {
                stored.tributaries = false;
                Defeat(c, s, HoldChoice.Hold);
                Expect(c, s.Destroyed && Find.WorldObjects.AnyDestroyedSettlementAt(s.Tile) && Comp.tributaries.Count == records, "with the setting off the town was not destroyed as in vanilla");
                Leave(a);
            }
            finally
            {
                stored.tributaries = was;
            }
        }

        /// <summary>Who is offered the hold: a faction at peace with the colony, a town a quest refers to and a switched off setting get no offer.</summary>
        private static void OfferLimits()
        {
            const string c = "offer limits";
            Settlement s = PickTown(t => !t.Faction.def.permanentEnemy && Diplomacy.Diplomatic(t.Faction));
            if (s == null)
            {
                Skip(c, "no town left");
                return;
            }
            Faction f = s.Faction;
            f.TryAffectGoodwillWith(Faction.OfPlayer, 10 - f.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            if (f.HostileTo(Faction.OfPlayer))
            {
                Skip(c, "the faction cannot be made friendly");
                return;
            }
            Expect(c, !Tributaries.Offerable(s), "a town of a faction at peace with the colony is offered");
            f.TryAffectGoodwillWith(Faction.OfPlayer, f.GoodwillToMakeHostile(Faction.OfPlayer), canSendMessage: false, canSendHostilityLetter: false);
            if (!f.HostileTo(Faction.OfPlayer))
            {
                Skip(c, "the faction cannot be made hostile");
                return;
            }
            Expect(c, Tributaries.Offerable(s), "a hostile town is not offered (the case's control)");
            s.questTags ??= new List<string>();
            s.questTags.Add("lwtest");
            Expect(c, !Tributaries.Offerable(s), "a town a quest refers to is offered");
            s.questTags.Remove("lwtest");
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool was = stored.tributaries;
            try
            {
                stored.tributaries = false;
                Expect(c, !Tributaries.Offerable(s), "with the setting off a town is offered");
            }
            finally
            {
                stored.tributaries = was;
            }
        }

        // ---- Cases: quests and the save ---------------------------------------------------------------------

        /// <summary>A yielded town with a snapshot and a quest tag waits, and is saved like that (the halfway save carries it).</summary>
        private static void WaitingAcrossSave()
        {
            const string c = "quest waits";
            Settlement s = PickTown();
            if (s == null)
            {
                Skip(c, "no town");
                return;
            }
            s.questTags ??= new List<string>();
            s.questTags.Add("lwtest");
            TributaryRecord r = Yielded(s);
            for (int i = 0; i < 3; i++) Tributaries.Tick(Comp, r);
            Expect(c, r.state == TributaryState.Yielded && r.stage == TransferStage.None && s.Faction.loadID == r.oldFactionId, "a town a quest refers to was taken anyway");
            waitingTownId = s.ID;
        }

        /// <summary>A quest attached after the yield waits for the transfer; when it outlasts the wait, the town stays with its old masters.</summary>
        private static void QuestWaitEnds()
        {
            const string c = "quest wait ends";
            Settlement s = PickTown();
            if (s == null)
            {
                Skip(c, "no town");
                return;
            }
            Faction old = s.Faction;
            s.questTags ??= new List<string>();
            s.questTags.Add("lwtest");
            TributaryRecord r = Yielded(s);
            Tributaries.Tick(Comp, r);
            Expect(c, Comp.tributaries.Contains(r), "the record was dropped at once");
            r.snapshotTick = SimClock.Now - (Tuning.T.tributaryTransferWaitDays + 1) * GenDate.TicksPerDay;
            Tributaries.Tick(Comp, r);
            Expect(c, !Comp.tributaries.Contains(r) && s.Faction == old, "the wait is over and the town was not left with its old masters");
        }

        // ---- Cases: the transfer's stages -------------------------------------------------------------------

        private sealed class Case
        {
            public string name;
            public Settlement town;
            public Faction old;
            public TributaryRecord record;
            public int factions, created, took, enemies, bonus, wars;
        }

        private static Case Start(string c, Func<Settlement, bool> where = null, bool pump = false)
        {
            Settlement s = PickTown(where);
            if (s == null)
            {
                Skip(c, "no town left");
                return null;
            }
            var x = new Case
            {
                name = c,
                town = s,
                old = s.Faction,
                factions = Find.FactionManager.AllFactionsListForReading.Count,
                created = Comp.clientFactionsCreated,
                took = Chronicle(LWChronicleDefOf.LW_PlayerTookTown),
                enemies = EnemiesOf(s.Faction),
                bonus = Tributaries.StatEnemyBonus,
                wars = Comp.wars.Count,
            };
            x.record = Yielded(s);
            if (pump) Pump(x.record);
            return x;
        }

        /// <summary>A transfer that finished: held, committed, the deed run once, one client faction and at most one war.</summary>
        private static void ExpectDone(Case x)
        {
            TributaryRecord r = x.record;
            Expect(x.name, r.state == TributaryState.Held && r.stage == TransferStage.Committed, $"the town is {r.state} at stage {r.stage}");
            Faction client = ClientOf(r);
            Expect(x.name, client != null && x.town.Faction == client, "the town does not belong to its client");
            Expect(x.name, r.deedParts == 15, $"the deed ran in parts {r.deedParts}");
            Expect(x.name, Chronicle(LWChronicleDefOf.LW_PlayerTookTown) == x.took + 1, "the colony taking the town was not chronicled exactly once");
            Expect(x.name, Tributaries.StatEnemyBonus - x.bonus == x.enemies, $"{Tributaries.StatEnemyBonus - x.bonus} goodwill requests with the old masters' enemies, expected {x.enemies}");
            Expect(x.name, Comp.clientFactionsCreated == x.created + 1 && Find.FactionManager.AllFactionsListForReading.Count == x.factions + 1, "not exactly one client faction was made");
            if (client != null)
            {
                int wars = Comp.wars.Count(w => w.Active && w.Between(client.loadID, x.old.loadID));
                Expect(x.name, wars <= 1 && (wars == 1) == (r.reconquestWarId >= 0), $"{wars} wars between the client and the old masters, the record names {r.reconquestWarId}");
            }
        }

        /// <summary>One failure at a stage, then the retry: whatever was done is not done twice.</summary>
        private static void FailOnce(string what, TransferStage stage)
        {
            string c = "failure at " + what;
            Case x = Start(c);
            if (x == null) return;
            Tributaries.TestFailAt = stage;
            Tributaries.TestFailCount = 1;
            Tributaries.Tick(Comp, x.record);
            Expect(c, x.record.failures == 1 && x.record.state == TributaryState.Yielded, $"after the failure the record has {x.record.failures} failures and is {x.record.state}");
            Expect(c, x.record.stage < stage, $"the record claims stage {x.record.stage} though {stage} failed");
            Tributaries.Tick(Comp, x.record);
            ExpectDone(x);
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>The client was made and saved, then the stage failed: the retry takes it up and makes no second faction.</summary>
        private static void FailAfterCreate()
        {
            const string c = "failure after the client was made";
            Case x = Start(c);
            if (x == null) return;
            Tributaries.TestFailAfterCreate = true;
            Tributaries.Tick(Comp, x.record);
            Expect(c, x.record.failures == 1 && x.record.clientFactionId >= 0, "the client id was not saved before the failure");
            Expect(c, Find.FactionManager.AllFactionsListForReading.Count == x.factions + 1 && Comp.clientFactionsCreated == x.created + 1, "the failed attempt did not leave exactly one faction on the books");
            Tributaries.Tick(Comp, x.record);
            ExpectDone(x);
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>The capture keeps failing while the old masters own the town: the record is refused, the client (made already) is defeated, nothing was credited.</summary>
        private static void FailAbort()
        {
            const string c = "failure aborts before ownership";
            Case x = Start(c);
            if (x == null) return;
            Tributaries.TestFailAt = TransferStage.Captured;
            Tributaries.TestFailCount = int.MaxValue;
            for (int i = 0; i < Tributaries.MaxFailures; i++) Tributaries.Tick(Comp, x.record);
            TributaryRecord r = x.record;
            Faction client = ClientOf(r);
            Expect(c, r.state == TributaryState.Refused && client != null && client.defeated, $"the record is {r.state} (client defeated: {client?.defeated})");
            Expect(c, x.town.Faction == x.old && r.deedParts == 0 && Chronicle(LWChronicleDefOf.LW_PlayerTookTown) == x.took && Tributaries.StatEnemyBonus == x.bonus, "the colony was credited with a town it did not get");
            Expect(c, Find.FactionManager.AllFactionsListForReading.Count == x.factions + 1 && Tributaries.WasClient(client), "the client made is not on the books exactly once");
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>The first stage keeps failing: no client was made, the record goes.</summary>
        private static void FailAbortEarly()
        {
            const string c = "failure aborts before the client";
            Case x = Start(c);
            if (x == null) return;
            Tributaries.TestFailAt = TransferStage.ClientReady;
            Tributaries.TestFailCount = int.MaxValue;
            for (int i = 0; i < Tributaries.MaxFailures; i++) Tributaries.Tick(Comp, x.record);
            Expect(c, !Comp.tributaries.Contains(x.record) && x.town.Faction == x.old, "the record was not dropped with the town left to its old masters");
            Expect(c, Find.FactionManager.AllFactionsListForReading.Count == x.factions && Comp.clientFactionsCreated == x.created, "a faction was made though the first stage never ran");
        }

        /// <summary>A stage after the capture keeps failing once the client owns the town: the transaction is completed, the deed once.</summary>
        private static void FailRollForward(string what, TransferStage stage)
        {
            string c = "failure rolls forward from the " + what;
            Case x = Start(c);
            if (x == null) return;
            Tributaries.TestFailAt = stage;
            Tributaries.TestFailCount = int.MaxValue;
            for (int i = 0; i < Tributaries.MaxFailures; i++) Tributaries.Tick(Comp, x.record);
            ExpectDone(x);
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
        }

        private static Faction ThirdOf(Case x) => Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => FactionState.IsSimulated(f) && f != x.old && !Tributaries.WasClient(f));

        /// <summary>
        /// A client was made (the transfer failed after that, once), then the town went to a third faction before the retry: the record must
        /// end, and the faction made for it must not stay in the world, alive, owning nothing. Refused while the colony never had the town,
        /// lost once it had.
        /// </summary>
        private static void NoEmptyClient(string what, TransferStage? failAt, TributaryState expected)
        {
            string c = "no empty client, " + what;
            Case x = Start(c);
            if (x == null) return;
            Faction third = ThirdOf(x);
            if (third == null)
            {
                Skip(c, "no third faction");
                return;
            }
            if (failAt == null) Tributaries.TestFailAfterCreate = true;
            else
            {
                Tributaries.TestFailAt = failAt;
                Tributaries.TestFailCount = 1;
            }
            Tributaries.Tick(Comp, x.record);
            Tributaries.TestFailAt = null;
            Faction client = ClientOf(x.record);
            Expect(c, x.record.failures == 1 && client != null && x.record.state == TributaryState.Yielded, $"the setup: {x.record.failures} failures, client {client?.Name ?? "none"}, {x.record.state}");
            if (client == null) return;
            x.town.SetFaction(third);
            Tributaries.Tick(Comp, x.record);
            Expect(c, Comp.tributaries.Contains(x.record) && x.record.state == expected, $"the record is {(Comp.tributaries.Contains(x.record) ? x.record.state.ToString() : "gone")}, expected {expected}");
            Expect(c, client.defeated && SettlementOps.SettlementCount(client) == 0, $"the client made for the town is alive (defeated {client.defeated}) and owns nothing");
            Expect(c, Tributaries.WasClient(client) && !Tributaries.IsClient(client) && Tributaries.HeldFor(x.town) == null, "the client is not a former client, or is held");
            Expect(c, Comp.clientFactionsCreated == x.created + 1 && Find.FactionManager.AllFactionsListForReading.Count == x.factions + 1, "not exactly one client faction on the books");
            Expect(c, x.record.endedTicks >= 0, "the ended record has no end");
            Expect(c, x.record.deedParts == 0 && Chronicle(LWChronicleDefOf.LW_PlayerTookTown) == x.took, "the colony was credited with a town that left its client before the deed");
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>
        /// Preparing the save for removing the mod while transfers are under way: one past the capture is completed (the client owns the town),
        /// one whose client owns nothing is refused with its faction defeated, one with no client is dropped.
        /// </summary>
        private static void PrepareUnderWay()
        {
            const string c = "prepare with transfers under way";
            Case a = Start(c);
            if (a == null) return;
            Tributaries.TestFailAt = TransferStage.DeedDone;
            Tributaries.TestFailCount = 1;
            Tributaries.Tick(Comp, a.record);
            Tributaries.TestFailAt = null;
            Expect(c, a.record.stage == TransferStage.Captured && a.town.Faction == ClientOf(a.record), "the first record is not past the capture (the setup)");
            Tributaries.PrepareYielded(Comp, a.record);
            ExpectDone(a);
            Tributaries.Release(a.record);
            Comp.clientFactionsCreated = a.created;
            Case b = Start(c);
            if (b == null) return;
            Tributaries.TestFailAfterCreate = true;
            Tributaries.Tick(Comp, b.record);
            Faction made = ClientOf(b.record);
            Expect(c, made != null && b.record.stage == TransferStage.None, "the second record has no client (the setup)");
            Tributaries.PrepareYielded(Comp, b.record);
            Expect(c, Comp.tributaries.Contains(b.record) && b.record.state == TributaryState.Refused && made != null && made.defeated && b.town.Faction == b.old, $"the record with a client but no town is {b.record.state}, its client defeated: {made?.defeated}");
            Comp.clientFactionsCreated = b.created;
            Case d = Start(c);
            if (d == null) return;
            Tributaries.PrepareYielded(Comp, d.record);
            Expect(c, !Comp.tributaries.Contains(d.record) && d.town.Faction == d.old && Comp.clientFactionsCreated == d.created && Find.FactionManager.AllFactionsListForReading.Count == d.factions, "the record with no client was not dropped, or a faction was made for it");
        }

        /// <summary>
        /// The capture failed once; a quest then refers to the town: the retry waits (nothing changes hands), and goes on once the quest is over. A
        /// quest that outlasts the wait ends the record as refused, with its client defeated.
        /// </summary>
        private static void RetryQuest()
        {
            const string c = "retry waits for a quest";
            Case x = Start(c);
            if (x == null) return;
            Tributaries.TestFailAt = TransferStage.Captured;
            Tributaries.TestFailCount = 1;
            Tributaries.Tick(Comp, x.record);
            Tributaries.TestFailAt = null;
            Expect(c, x.record.stage == TransferStage.ClientReady && x.record.failures == 1, "the capture did not fail once (the setup)");
            x.town.questTags ??= new List<string>();
            x.town.questTags.Add("lwtest");
            for (int i = 0; i < 3; i++) Tributaries.Tick(Comp, x.record);
            Expect(c, x.record.state == TributaryState.Yielded && x.town.Faction == x.old && x.record.stage == TransferStage.ClientReady, $"a town a quest refers to was taken on the retry ({x.record.state}, owner {x.town.Faction?.Name})");
            x.town.questTags.Remove("lwtest");
            Tributaries.Tick(Comp, x.record);
            ExpectDone(x);
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
            Case y = Start(c);
            if (y == null) return;
            Tributaries.TestFailAt = TransferStage.Captured;
            Tributaries.TestFailCount = 1;
            Tributaries.Tick(Comp, y.record);
            Tributaries.TestFailAt = null;
            y.town.questTags ??= new List<string>();
            y.town.questTags.Add("lwtest");
            y.record.snapshotTick = SimClock.Now - (Tuning.T.tributaryTransferWaitDays + 1) * GenDate.TicksPerDay;
            Tributaries.Tick(Comp, y.record);
            Faction client = ClientOf(y.record);
            Expect(c, y.record.state == TributaryState.Refused && y.town.Faction == y.old && client != null && client.defeated, $"a quest that outlasted the wait left the record {y.record.state} (owner {y.town.Faction?.Name}), its client defeated: {client?.defeated}");
            y.town.questTags.Remove("lwtest");
            Comp.clientFactionsCreated = y.created;
        }

        /// <summary>A goodwill cap below zero (a modded situation, simulated): refused before ownership changes, the client made for it is defeated.</summary>
        private static void CapBelowZero()
        {
            const string c = "cap below zero";
            Case x = Start(c);
            if (x == null) return;
            Tributaries.TestCapOverride = -5;
            Tributaries.Tick(Comp, x.record);
            TributaryRecord r = x.record;
            Faction client = ClientOf(r);
            Expect(c, r.state == TributaryState.Refused && client != null && client.defeated, $"the record is {r.state}");
            Expect(c, x.town.Faction == x.old && Tributaries.StatEnemyBonus == x.bonus && Chronicle(LWChronicleDefOf.LW_PlayerTookTown) == x.took, "a refused town changed hands or was credited");
            refusedRecordId = r.id;
            Comp.clientFactionsCreated = x.created;
        }

        // ---- Cases: the rest ----------------------------------------------------------------------------------

        /// <summary>
        /// A revolted one-town client held again submits again: the same faction, no capture, nothing new on the budget, no grudge or revenge, and the
        /// budget being spent does not stop the offer.
        /// </summary>
        private static void Resubmission()
        {
            const string c = "resubmission";
            Faction client = keptClientId < 0 ? null : Comp.FactionById(keptClientId);
            var s = LivingWorldComponent.WorldObjectById(keptTownId) as Settlement;
            TributaryRecord held = Tributaries.HeldFor(s);
            if (client == null || s == null || held == null)
            {
                Skip(c, "no client was kept by the real path cases");
                return;
            }
            client.TryAffectGoodwillWith(Faction.OfPlayer, client.GoodwillToMakeHostile(Faction.OfPlayer), canSendMessage: false, canSendHostilityLetter: false);
            Tributaries.Reconcile(Comp);
            Expect(c, held.state == TributaryState.Revolted, $"a client hostile to the colony is {held.state}");
            Expect(c, Tributaries.IsResubmission(s) && Tributaries.WasClient(client) && !Tributaries.IsClient(client), "the revolted town is not a candidate to submit again");
            int created = Comp.clientFactionsCreated, factions = Find.FactionManager.AllFactionsListForReading.Count, revenge = Comp.revenge.Count, took = Chronicle(LWChronicleDefOf.LW_PlayerTookTown);
            float grudge = Diplomacy.GrudgeAgainst(Comp.StateFor(client), Faction.OfPlayer);
            Comp.clientFactionsCreated = Tuning.T.tributaryFactionBudget;
            Expect(c, Tributaries.CannotHold(s) == null, "the spent budget stopped a town that submits again");
            TributaryRecord r = Yielded(s);
            Expect(c, r.resubmit, "the record does not know it submits again");
            Pump(r);
            Expect(c, r.state == TributaryState.Held && ClientOf(r) == client && s.Faction == client, $"the town is {r.state}, its client {ClientOf(r)?.Name}");
            Expect(c, Comp.clientFactionsCreated == Tuning.T.tributaryFactionBudget && Find.FactionManager.AllFactionsListForReading.Count == factions, "submitting again used the budget or made a faction");
            Expect(c, Comp.revenge.Count == revenge && Mathf.Approximately(Diplomacy.GrudgeAgainst(Comp.StateFor(client), Faction.OfPlayer), grudge), "submitting again made a grudge or a revenge");
            Expect(c, r.reconquestWarId < 0 && !client.HostileTo(Faction.OfPlayer), "submitting again started a war or left the town hostile");
            Expect(c, Chronicle(LWChronicleDefOf.LW_PlayerTookTown) == took + 1, "the town taken again was not chronicled once");
            Comp.clientFactionsCreated = created;
            heldClientId = client.loadID;
        }

        /// <summary>The old masters' last town: they are defeated, and there is no war with them.</summary>
        private static void LastTown()
        {
            const string c = "last town";
            int defeated = Chronicle(LWChronicleDefOf.LW_FactionDefeated);
            Case x = Start(c, t => SettlementOps.SettlementCount(t.Faction) == 1 && !Tributaries.WasClient(t.Faction), pump: true);
            if (x == null) return;
            ExpectDone(x);
            Expect(c, x.old.defeated && Chronicle(LWChronicleDefOf.LW_FactionDefeated) == defeated + 1, "the old masters were not defeated, or not chronicled once");
            Expect(c, x.record.reconquestWarId < 0 && Diplomacy.ActiveWar(Comp, ClientOf(x.record), x.old) == null, "a war with a defeated faction was started");
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>The cap of live records, the budget of client factions, and the town that submits again being exempt from the budget.</summary>
        private static void CapAndBudget()
        {
            const string c = "cap and budget";
            Settlement s = PickTown(t => !Tributaries.IsResubmission(t) && Tributaries.ClientDefFor(t.Faction.def) != null);
            if (s == null)
            {
                Skip(c, "no town");
                return;
            }
            var fakes = new List<TributaryRecord>();
            try
            {
                int max = Tuning.T.tributaryMax;
                Expect(c, Tributaries.CannotHold(s) == null, $"a town cannot be held with {Tributaries.LiveCount(Comp)} records live (the case's control): {Tributaries.CannotHold(s)}");
                for (int i = 0; i < max - Tributaries.LiveCount(Comp); i++)
                    fakes.Add(new TributaryRecord { id = Comp.NextId(), settlementId = -100 - i, state = TributaryState.Yielded, resubmit = true });
                Comp.tributaries.AddRange(fakes);
                string cannot = Tributaries.CannotHold(s);
                Expect(c, cannot == "LW_Hold_Cap".Translate(max).Resolve(), $"with {max} records live the reason is '{cannot}'");
                DiaOption hold = Tributaries.BuildHoldNode(s).options[(int)HoldChoice.Hold];
                Expect(c, hold.disabled && hold.disabledReason == cannot, "the hold option of the dialog is not disabled with the cap's reason");
            }
            finally
            {
                foreach (TributaryRecord f in fakes) Comp.tributaries.Remove(f);
            }
            int was = Comp.clientFactionsCreated;
            try
            {
                Comp.clientFactionsCreated = Tuning.T.tributaryFactionBudget;
                string cannot = Tributaries.CannotHold(s);
                Expect(c, cannot == "LW_Hold_Budget".Translate().Resolve(), $"with the budget spent the reason is '{cannot}'");
            }
            finally
            {
                Comp.clientFactionsCreated = was;
            }
        }

        /// <summary>The transfer is the player's choice: with conquest switched off it still happens.</summary>
        private static void ConquestOff()
        {
            const string c = "allowConquest off";
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool was = stored.allowConquest;
            Case x;
            try
            {
                stored.allowConquest = false;
                x = Start(c, pump: true);
                if (x == null) return;
                Expect(c, x.record.state == TributaryState.Held && x.town.Faction == ClientOf(x.record), "the transfer did not go ahead with conquest switched off");
            }
            finally
            {
                stored.allowConquest = was;
            }
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>A held town the colony then lets go: released, not hostile, still a former client after the save.</summary>
        private static void ReleaseCase()
        {
            const string c = "release";
            Case x = Start(c, pump: true);
            if (x == null) return;
            Faction client = ClientOf(x.record);
            int released = Chronicle(LWChronicleDefOf.LW_TributaryReleased);
            Tributaries.Release(x.record);
            Expect(c, x.record.state == TributaryState.Released && !Tributaries.IsClient(client) && Tributaries.WasClient(client), "the released town is not a former client");
            Expect(c, !client.HostileTo(Faction.OfPlayer) && !SettlementOps.MustSurvive(client), "the released town is hostile or required");
            Expect(c, Chronicle(LWChronicleDefOf.LW_TributaryReleased) == released + 1 && x.town.Faction == client, "the release was not chronicled once or the town changed hands");
            releasedClientId = client.loadID;
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>Identity follows the world with the setting off (a town handed to a third faction is lost, quietly), and turning it on again leaves nothing stale.</summary>
        private static void ReconcileOff()
        {
            const string c = "reconcile with the setting off";
            Case x = Start(c, pump: true);
            if (x == null) return;
            Faction client = ClientOf(x.record), third = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => FactionState.IsSimulated(f) && f != client && f != x.old);
            if (third == null)
            {
                Skip(c, "no third faction");
                return;
            }
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool was = stored.tributaries;
            int letters = Find.LetterStack.LettersListForReading.Count;
            try
            {
                x.town.SetFaction(third);
                stored.tributaries = false;
                Tributaries.Reconcile(Comp);
                Expect(c, x.record.state == TributaryState.Lost, $"a town another faction owns is {x.record.state} with the setting off");
                Expect(c, Find.LetterStack.LettersListForReading.Count == letters, "a letter was sent with the setting off");
            }
            finally
            {
                stored.tributaries = was;
            }
            Tributaries.Reconcile(Comp);
            Expect(c, x.record.state == TributaryState.Lost && Find.LetterStack.LettersListForReading.Count == letters, "turning the setting on again changed the record or sent a letter");
            Comp.clientFactionsCreated = x.created;
        }

        // ---- Cases: the client rules -------------------------------------------------------------------------

        private static void ClientRules()
        {
            const string c = "client rules";
            // Towns whose faction has others, so that the old masters live on (a rebellion hands a town back to them).
            Func<Settlement, bool> several = t => SettlementOps.SettlementCount(t.Faction) > 1 && !t.Faction.def.permanentEnemy;
            Case x = Start(c, several, pump: true), y = x == null ? null : Start(c, several, pump: true);
            if (x == null || y == null) return;
            if (x.record.state != TributaryState.Held || y.record.state != TributaryState.Held)
            {
                problems.Add($"{c}: two held towns could not be made ({x.record.state}, {y.record.state})");
                return;
            }
            Faction cx = ClientOf(x.record), cy = ClientOf(y.record);
            LivingWorldComponent comp = Comp;
            List<Faction> others = Find.FactionManager.AllFactionsListForReading.Where(f => FactionState.IsSimulated(f) && Diplomacy.Diplomatic(f) && f != cx && f != cy && !Tributaries.WasClient(f)
                && f != x.old && f != y.old && !cx.HostileTo(f) && !cy.HostileTo(f) && comp.StateFor(f)?.settlements.Count > 0).ToList();
            if (others.Count < 3)
            {
                Skip(c, "fewer than three other factions");
                return;
            }
            Faction n = others[0], n2 = others[1], n3 = others[2];
            // Drift: a client stops short of hostility, an ordinary pair does not (the control).
            cx.TryAffectGoodwillWith(n, -70 - cx.GoodwillWith(n), canSendMessage: false, canSendHostilityLetter: false);
            for (int i = 0; i < 30; i++) Diplomacy.ApplyDrift(cx, n, -3f);
            Expect(c, !cx.HostileTo(n) && cx.GoodwillWith(n) >= -74, $"a client drifted to {cx.GoodwillWith(n)} ({cx.RelationKindWith(n)})");
            n2.TryAffectGoodwillWith(n3, -70 - n2.GoodwillWith(n3), canSendMessage: false, canSendHostilityLetter: false);
            for (int i = 0; i < 30; i++) Diplomacy.ApplyDrift(n2, n3, -3f);
            Expect(c, n2.HostileTo(n3), "the control pair did not drift into hostility (the case cannot tell)");
            Diplomacy.MakePeace(comp, n2, n3);
            // War: the client is never the aggressor, the other side still can be; two clients never fight.
            if (comp.wars.Count(w => w.Active && w.Involves(n.loadID)) < 2)
            {
                Diplomacy.ConsiderWar(comp, comp.StateFor(cx), comp.StateFor(n), 1e6f);
                War war = Diplomacy.ActiveWar(comp, cx, n);
                Expect(c, war != null && war.factionA == n.loadID, $"the war between a client and a neighbour is {(war == null ? "missing" : war.factionA == cx.loadID ? "the client's declaration" : "fine")}");
                if (war != null) Diplomacy.MakePeace(comp, n, cx);
                Diplomacy.ConsiderWar(comp, comp.StateFor(n), comp.StateFor(cx), 1e6f);
                war = Diplomacy.ActiveWar(comp, cx, n);
                Expect(c, war != null && war.factionA == n.loadID, "the neighbour could not declare on the client");
                if (war != null) Diplomacy.MakePeace(comp, n, cx);
            }
            Diplomacy.ConsiderWar(comp, comp.StateFor(cx), comp.StateFor(cy), 1e6f);
            Expect(c, Diplomacy.ActiveWar(comp, cx, cy) == null && !cx.HostileTo(cy), "two free towns went to war");
            // The colony's call to arms is the exception: an allied client may declare.
            Faction enemy = cx.PlayerRelationKind == FactionRelationKind.Ally ? PlayerDiplomacy.CallToArmsTargets(cx).FirstOrDefault(e => !cx.HostileTo(e)) : null;
            if (enemy == null) Log.Message(Tag + "client rules: no call to arms for a client (skipped)");
            else
            {
                bool? outcome = PlayerDiplomacy.TestOutcome;
                PlayerDiplomacy.TestOutcome = true;
                try
                {
                    WorldParty call = PlayerDiplomacy.Send(cx, EnvoyMission.PlayerCallToArms, enemy: enemy);
                    if (call == null) Log.Message(Tag + "client rules: no envoy could be sent to the client (skipped)");
                    else
                    {
                        PlayerDiplomacy.Answer(call);
                        Expect(c, cx.HostileTo(enemy), "an allied client did not answer the colony's call to arms");
                        call.Disband();
                    }
                }
                finally
                {
                    PlayerDiplomacy.TestOutcome = outcome;
                }
            }
            // A client's army never takes or razes a town (forty battles at a strength nothing can resist); a non-client army at the same strength does (the control).
            Settlement home = x.town;
            CompSettlementLife target = comp.activeSettlements.FirstOrDefault(t => t.Settlement.Faction != cx && t.Settlement.Faction != cy && t.Settlement.Faction != n && SettlementOps.SettlementCount(t.Settlement.Faction) > 1 && SettlementOps.CanFall(t.Settlement));
            if (target == null) Log.Message(Tag + "client rules: no town for the battles (skipped)");
            else
            {
                Faction owner = target.Settlement.Faction;
                bool fell = false;
                for (int i = 0; i < 40 && !fell; i++)
                {
                    BattleResult br = Battle.Resolve(cx, home, target, 1e6f);
                    fell = br.captured || br.razed || target.Settlement.Faction != owner || target.Settlement.Destroyed;
                }
                Expect(c, !fell, "a free town's army took or razed a town");
                bool control = false;
                for (int i = 0; i < 60 && !control; i++)
                {
                    BattleResult br = Battle.Resolve(n, home, target, 1e6f);
                    control = br.captured || br.razed;
                }
                Expect(c, control, "a non-client army never took or razed the town in sixty battles (the case cannot tell)");
            }
            // Expansion skips clients (everyone else is made poor, so that nobody founds anything meanwhile).
            var treasuries = comp.factionStates.ToDictionary(fs => fs, fs => fs.treasury);
            try
            {
                foreach (FactionState fs in comp.factionStates) fs.treasury = 0f;
                comp.StateFor(cx).treasury = 5000f;
                Expansion.DailyStep(comp, 1000f);
                Expect(c, PartyBudget.CountOf(LWPartyDefOf.LW_Settlers, cx) == 0 && SettlementOps.SettlementCount(cx) == 1, "a free town sent settlers");
            }
            finally
            {
                foreach (KeyValuePair<FactionState, float> kv in treasuries) kv.Key.treasury = kv.Value;
            }
            // Renewal does not count them; a rebellion never starts in a held town; no tribute is demanded from one.
            comp.RebuildIndexes(seedMissing: false);
            Renewal.Census(comp, out int alive, out _, out _);
            int all = comp.factionStates.Count(fs => fs.faction != null && !fs.faction.defeated && fs.settlements.Count > 0);
            int clients = comp.factionStates.Count(fs => fs.faction != null && !fs.faction.defeated && fs.settlements.Count > 0 && Tributaries.WasClient(fs.faction));
            Expect(c, alive == all - clients && clients >= 2, $"renewal counts {alive} peoples of {all} with {clients} made by the colony");
            // A rebellion hands an occupied town back to its founders (here the old masters): not a held one. The control is a released town.
            Tributaries.Release(y.record);
            bool liberated = Rebels(y, comp), kept = !Rebels(x, comp);
            Expect(c, liberated, "a rebellion did not hand back an occupied town that is not held (the case cannot tell)");
            Expect(c, kept && x.town.Faction == cx && Tributaries.HeldFor(x.town) == x.record, "a held town rose or was handed back by the rebellion step");
            CompSettlementLife life = x.town.GetComponent<CompSettlementLife>();
            life.originalFactionId = cx.loadID;
            life.occupiedSinceTicks = -1;
            life.unrest = 0.35f;
            Expect(c, !PlayerDiplomacy.CanDemandTribute(cx), "tribute can be demanded from a free town of the colony");
            heldClientId = heldClientId >= 0 ? heldClientId : cx.loadID;
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>The town made occupied and restless, with only it in the daily rebellion step (the step stops at the first town it acts on): did it change hands?</summary>
        private static bool Rebels(Case k, LivingWorldComponent comp)
        {
            CompSettlementLife life = k.town.GetComponent<CompSettlementLife>();
            Faction owner = k.town.Faction;
            life.originalFactionId = k.old.loadID;
            life.occupiedSinceTicks = SimClock.Now;
            life.unrest = 0.95f;
            List<CompSettlementLife> all = comp.activeSettlements.ToList();
            comp.activeSettlements.Clear();
            comp.activeSettlements.Add(life);
            try
            {
                Rebellion.DailyStep(comp, 1000f);
            }
            finally
            {
                comp.activeSettlements.Clear();
                comp.activeSettlements.AddRange(all);
            }
            return k.town.Faction != owner;
        }

        /// <summary>Only the colony's allies are exempt from a demand for tribute by the old rule: a neutral free town is exempt by being a client. The released town is the control.</summary>
        private static void TributeNeutral()
        {
            const string c = "tribute from a neutral client";
            Case x = Start(c);
            if (x == null) return;
            Tributaries.TestCapOverride = 50;
            Pump(x.record);
            Tributaries.TestCapOverride = null;
            Faction client = ClientOf(x.record);
            if (x.record.state != TributaryState.Held || client == null)
            {
                problems.Add($"{c}: the town is {x.record.state}");
                return;
            }
            Expect(c, client.PlayerRelationKind == FactionRelationKind.Neutral, $"the client is {client.PlayerRelationKind} with a cap of 50 (the case cannot tell)");
            FactionState fs = Comp.StateFor(client);
            fs.strength = 0f;
            fs.lastPlayerTributeTick = FactionState.Never;
            Expect(c, !PlayerDiplomacy.CanDemandTribute(client), "tribute can be demanded from a neutral free town of the colony");
            Tributaries.Release(x.record);
            Expect(c, PlayerDiplomacy.CanDemandTribute(client), "tribute cannot be demanded from the released town (the case cannot tell)");
            Comp.clientFactionsCreated = x.created;
        }

        // ---- Cases: ideology ------------------------------------------------------------------------------------

        private static bool Faith(string c)
        {
            if (ModsConfig.IdeologyActive) return true;
            Skip(c, "Ideology is not active");
            return false;
        }

        /// <summary>The client takes up the old faith (the leader too); every ideo that was registered still is.</summary>
        private static void IdeologyNormal()
        {
            const string c = "ideology normal";
            if (!Faith(c)) return;
            Case x = Start(c, t => t.Faction.ideos?.PrimaryIdeo != null);
            if (x == null) return;
            Ideo faith = x.old.ideos.PrimaryIdeo;
            var before = new HashSet<Ideo>(Find.IdeoManager.IdeosListForReading);
            Pump(x.record);
            Faction client = ClientOf(x.record);
            Expect(c, x.record.state == TributaryState.Held && client != null, "the transfer did not finish");
            if (client == null) return;
            if (IdeoUtility.CanUseIdeo(client.def, faith, Rebellion.IdeoParms(client.def)))
                Expect(c, client.ideos.PrimaryIdeo == faith && (client.leader?.ideo?.Ideo == faith || client.leader?.DevelopmentalStage.Baby() == true || client.leader?.ideo == null), "the client's faith is not the conquered one");
            Expect(c, before.All(i => Find.IdeoManager.IdeosListForReading.Contains(i)), "an ideo was removed");
            Expect(c, Find.IdeoManager.IdeosListForReading.Contains(client.ideos.PrimaryIdeo), "the client's faith is not registered");
            // Already the client's faith: nothing happens (and nothing is removed).
            int count = Find.IdeoManager.IdeosListForReading.Count;
            Tributaries.AdoptOldIdeo(x.old, client);
            Expect(c, client.ideos.PrimaryIdeo == (IdeoUtility.CanUseIdeo(client.def, faith, Rebellion.IdeoParms(client.def)) ? faith : client.ideos.PrimaryIdeo) && Find.IdeoManager.IdeosListForReading.Count == count, "adopting a faith the client already has changed something");
            // A faith the kind cannot use is refused: the forced meme the ideo lacks.
            MemeDef missing = DefDatabase<MemeDef>.AllDefsListForReading.FirstOrDefault(m => !faith.memes.Contains(m));
            if (missing != null)
            {
                IdeoGenerationParms parms = Rebellion.IdeoParms(client.def);
                parms.forcedMemes = new List<MemeDef> { missing };
                Expect(c, !IdeoUtility.CanUseIdeo(client.def, faith, parms), "an ideo without the kind's forced meme was accepted");
                // AdoptOldIdeo asks with the kind's own parms: with a forced meme the old faith lacks, the faith the client has stays; without it, the old one comes back.
                Ideo other = Find.IdeoManager.IdeosListForReading.FirstOrDefault(i => i != faith);
                if (other != null && IdeoUtility.CanUseIdeo(client.def, faith, Rebellion.IdeoParms(client.def)))
                {
                    client.ideos.SetPrimary(other);
                    Tributaries.TestForcedMemes = new List<MemeDef> { missing };
                    try
                    {
                        Tributaries.AdoptOldIdeo(x.old, client);
                        Expect(c, client.ideos.PrimaryIdeo == other, "the old faith was taken up though it lacks a meme the client's kind forces");
                    }
                    finally
                    {
                        Tributaries.TestForcedMemes = null;
                    }
                    Tributaries.AdoptOldIdeo(x.old, client);
                    Expect(c, client.ideos.PrimaryIdeo == faith, "the old faith was not taken up again without the forced meme (the case cannot tell)");
                }
            }
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>Classic mode: the client shares the colony's ideo, which is still registered, and every colonist keeps theirs.</summary>
        private static void IdeologyClassic()
        {
            const string c = "ideology classic mode";
            if (!Faith(c)) return;
            IdeoManager manager = Find.IdeoManager;
            bool was = manager.classicMode;
            Ideo mine = Faction.OfPlayer.ideos?.PrimaryIdeo;
            if (mine == null)
            {
                Skip(c, "the colony has no primary ideo");
                return;
            }
            var colonists = PawnsFinder.AllMaps_FreeColonists.ToDictionary(p => p, p => p.ideo?.Ideo);
            Case x = null;
            try
            {
                manager.classicMode = true;
                x = Start(c);
                if (x == null) return;
                Pump(x.record);
            }
            finally
            {
                manager.classicMode = was;
            }
            Faction client = ClientOf(x.record);
            Expect(c, x.record.state == TributaryState.Held && client != null && client.ideos.PrimaryIdeo == mine, "the client does not share the colony's ideo in classic mode");
            Expect(c, manager.IdeosListForReading.Contains(mine) && colonists.All(kv => kv.Key.ideo?.Ideo == kv.Value), "a colonist lost an ideo, or the colony's ideo was removed");
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
        }

        /// <summary>With ten ideos about, a new faction reuses one: nothing is removed, the reused one stays registered.</summary>
        private static void IdeologyReuse()
        {
            const string c = "ideology reuse branch";
            if (!Faith(c)) return;
            Case x = Start(c, t => t.Faction.ideos?.PrimaryIdeo != null);
            if (x == null) return;
            FactionDef def = Tributaries.ClientDefFor(x.old.def);
            for (int i = 0; i < 10; i++) Find.IdeoManager.Add(IdeoGenerator.GenerateIdeo(Rebellion.IdeoParms(def)));
            var before = new HashSet<Ideo>(Find.IdeoManager.IdeosListForReading);
            Pump(x.record);
            Faction client = ClientOf(x.record);
            Expect(c, x.record.state == TributaryState.Held && client != null, "the transfer did not finish");
            Expect(c, before.All(i => Find.IdeoManager.IdeosListForReading.Contains(i)), "an ideo was removed");
            Expect(c, client != null && Find.IdeoManager.IdeosListForReading.Contains(client.ideos.PrimaryIdeo), "the client's faith is not registered");
            Tributaries.Release(x.record);
            Comp.clientFactionsCreated = x.created;
        }

        // ---- Cases: saves ------------------------------------------------------------------------------------

        /// <summary>A record written by hand: fields it lacks load as defaults (the fields of tribute and loyalty are read by the tribute migration check), and a state byte out of range reads as lost.</summary>
        private static void Migration()
        {
            const string c = "migration";
            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                Skip(c, "a Scribe session is open");
                return;
            }
            string dir = Path.Combine(Path.GetTempPath(), "lw-tributary-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                TributaryRecord a = Load(dir, "<tr_id>7</tr_id><tr_settlement>12</tr_settlement><tr_town>Oakhollow</tr_town><tr_state>1</tr_state><tr_rate>2</tr_rate><tr_accruedSilver>5.5</tr_accruedSilver>");
                Expect(c, a != null && a.id == 7 && a.settlementId == 12 && a.townName == "Oakhollow" && a.state == TributaryState.Held, "a record with fields of later steps did not load");
                Expect(c, a != null && a.stage == TransferStage.None && a.snapshot == DefeatSnapshot.Pending && a.snapshotTick == -1 && a.deedParts == 0 && a.failures == 0 && !a.resubmit && a.clientFactionId == -1 && a.heldSinceTicks == -1,
                    "the fields the record lacks did not load as defaults");
                TributaryRecord b = Load(dir, "<tr_id>8</tr_id><tr_state>99</tr_state><tr_stage>99</tr_stage><tr_snapshot>99</tr_snapshot>");
                Expect(c, b != null && b.state == TributaryState.Lost && b.stage == TransferStage.None && b.snapshot == DefeatSnapshot.Pending, "bytes out of range did not load as lost, none and pending");
                TributeMigration(dir);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        private static TributaryRecord Load(string dir, string body)
        {
            string file = Path.Combine(dir, "record.xml");
            File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Block><rec Class=\"LivingWorld.TributaryRecord\">" + body + "</rec></Block>");
            TributaryRecord loaded = null;
            Scribe.loader.InitLoading(file);
            try
            {
                Scribe_Deep.Look(ref loaded, "rec");
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }
            return loaded;
        }

        /// <summary>Every record's state, every id and counter: what the save must give back exactly.</summary>
        private static string Sign()
        {
            LivingWorldComponent c = Comp;
            var parts = c.tributaries.Select(r => $"{r.id}:{(int)r.state}:{(int)r.stage}:{(int)r.snapshot}:{r.snapshotTick}:{r.settlementId}:{r.oldFactionId}:{r.clientFactionId}:{r.resubmit}:{r.failures}:{r.deedParts}:{r.takenTicks}:{r.heldSinceTicks}:{r.endedTicks}:{r.reconquestWarId}:{r.chronicleId}:{r.townName}:{TributeSign(r)}");
            return string.Join("|", parts) + $"#{string.Join(",", c.clientFactionIds)}#{c.clientFactionsCreated}";
        }

        private static void BeforeSave()
        {
            saved = true;
            PrepareBoundary();
            signature = Sign();
            Comp.RebuildIndexes(seedMissing: false);
            Log.Message($"{Tag}before the save: {Comp.tributaries.Count} records ({string.Join(", ", Comp.tributaries.GroupBy(r => r.state).Select(g => g.Count() + " " + g.Key))}), {queue.Count} cases not run");
            if (queue.Count > 0) problems.Add($"{queue.Count} cases had not run when the game was saved");
            if (!Comp.tributaries.Any(r => r.state == TributaryState.Yielded && r.snapshot == DefeatSnapshot.Defeated && r.settlementId == waitingTownId)) problems.Add("the yielded town with a snapshot is not there to be saved");
        }

        private static void AfterReload()
        {
            LivingWorldComponent c = Comp;
            string after = Sign();
            Expect("round trip", after == signature, $"the tributary records changed over the save:\n before {signature}\n after  {after}");
            TributaryRecord waiting = c.tributaries.FirstOrDefault(r => r.settlementId == waitingTownId && r.state == TributaryState.Yielded);
            var s = LivingWorldComponent.WorldObjectById(waitingTownId) as Settlement;
            if (waiting == null || s == null)
            {
                problems.Add("round trip: the waiting town's record or the town is gone after the load");
                return;
            }
            Tributaries.Tick(c, waiting);
            Expect("round trip", c.tributaries.Contains(waiting) && waiting.state == TributaryState.Yielded && waiting.snapshot == DefeatSnapshot.Defeated, "the waiting town did not keep waiting for its quest after the load");
            s.questTags.Remove("lwtest");
            Pump(waiting);
            Expect("round trip", waiting.state == TributaryState.Held && s.Faction == c.FactionById(waiting.clientFactionId), $"the town is {waiting.state} after the load and its quest ended");
            if (heldClientId >= 0 && c.FactionById(heldClientId) is Faction held) Expect("round trip", Tributaries.IsClient(held) && c.tributaries.Any(r => r.clientFactionId == heldClientId && r.state == TributaryState.Held), "the held client is not held after the load");
            if (releasedClientId >= 0 && c.FactionById(releasedClientId) is Faction rel)
                Expect("round trip", Tributaries.WasClient(rel) && !Tributaries.IsClient(rel) && !SettlementOps.MustSurvive(rel), "a released client is no longer a former client after the load");
            if (refusedRecordId >= 0) Expect("round trip", c.tributaries.Any(r => r.id == refusedRecordId && r.state == TributaryState.Refused), "the refused record is not refused after the load");
            CheckBoundary(c);
            roundTripChecked = true;
        }

        // ---- Plan agencyremoval: the saves for tools/removal-check.sh ---------------------------------------------

        /// <summary>
        /// At the end of the run, the saves the removal check loads in a game without the mod: with nothing held (the baseline of load errors) and with a
        /// town held through the real path, each prepared for removal and, as the "isolated fixture", not prepared (only the world parties gone, which
        /// are expected not to load without the mod). A held town's save has a sidecar file naming the town and its client.
        /// </summary>
        private static IEnumerable<string> RemovalSaves()
        {
            var bad = new List<string>();
            try
            {
                LivingWorldComponent comp = Comp;
                comp.RebuildIndexes(seedMissing: true);
                foreach (WorldParty p in WorldParty.All.ToArray()) p.Destroy();
                GameDataSaveLoader.SaveGame("LW-baseline-raw");
                LivingWorldMod.PrepareForRemoval();
                GameDataSaveLoader.SaveGame("LW-baseline");
                comp.preparedForRemoval = false;
                Settlement s = PickTown(t => Tributaries.ClientDefFor(t.Faction.def) != null && !t.Faction.def.permanentEnemy);
                if (s == null)
                {
                    bad.Add("removal: no town to hold");
                    return bad;
                }
                Assault a = Attack(s);
                Defeat("removal", s, HoldChoice.Hold);
                TributaryRecord r = Tributaries.RecordFor(s.ID);
                if (r == null)
                {
                    bad.Add("removal: the town did not yield");
                    return bad;
                }
                Leave(a);
                Pump(r);
                if (r.state != TributaryState.Held)
                {
                    bad.Add($"removal: the town is {r.state}, not held");
                    return bad;
                }
                string info = $"settlement={s.ID}\nfaction={r.clientFactionId}\n";
                foreach (WorldParty p in WorldParty.All.ToArray()) p.Destroy(); // none of the world's parties are in the unprepared fixture
                GameDataSaveLoader.SaveGame("LW-removal-raw");
                File.WriteAllText(Path.Combine(GenFilePaths.SavedGamesFolderPath, "LW-removal-raw.info"), info);
                // A tribute convoy on the road when the save is prepared: it is set down at the colony first, and nothing of it is left in the save.
                Tributaries.TestRevolt = false;
                for (int day = 0; day < Tuning.T.tributeIntervalDays; day++) Tributaries.DailyStep(comp, 1f, r);
                Tributaries.TestRevolt = null;
                bool convoy = WorldParty.All.Any(p => p.kind == LWPartyDefOf.LW_Tribute && p.recordId == r.id);
                if (!convoy && r.payments == 0) bad.Add($"removal: no tribute on the road after {Tuning.T.tributeIntervalDays} days (accrued {r.accruedSilver:F1}, parties {WorldParty.All.Count})");
                LivingWorldMod.PrepareForRemoval();
                if (r.payments != 1 || WorldParty.All.Any(p => p.kind == LWPartyDefOf.LW_Tribute)) bad.Add($"removal: the tribute on the road was not delivered by the preparation ({r.payments} payments, {WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_Tribute)} convoys left)");
                GameDataSaveLoader.SaveGame("LW-removal");
                File.WriteAllText(Path.Combine(GenFilePaths.SavedGamesFolderPath, "LW-removal.info"), info);
                Log.Message($"{Tag}removal saves made: {s.Label} is the free town of {ClientOf(r)?.Name} (tribute on the road: {convoy}, payments {r.payments})");
            }
            catch (Exception e)
            {
                bad.Add($"removal: {e.GetType().Name}: {e.Message}");
            }
            return bad;
        }

        // ---- The end ---------------------------------------------------------------------------------------------

        /// <summary>Cases that must run to the end (not skip): a sparse world cannot pass with the destructive ones skipped. Ideology cases depend on the DLC and are left out.</summary>
        private static readonly string[] CoreCases =
        {
            "quest waits across the save", "quest wait ends", "two maps", "failure: client stage once", "failure: after the client was made", "failure: capture stage once",
            "failure: deed stage once", "failure: war stage once", "failure: commit stage once", "failure: aborts before ownership", "failure: aborts before the client",
            "failure: rolls forward from the deed", "failure: rolls forward from the war", "failure: rolls forward from the commit",
            "no empty client: made, then the town changed hands", "no empty client: capture failed, then the town changed hands", "no empty client: deed failed, then the town changed hands",
            "prepare for removal with transfers under way", "retry waits for a quest", "cap below zero", "resubmission", "release", "client rules", "tribute from a neutral client",
            "tribute: accrual", "tribute: goods", "tribute: delivery", "loyalty: terms",
        };

        private static IEnumerable<string> Final()
        {
            if (!queued) problems.Add("no case ever started");
            if (queue.Count > 0) problems.Add($"{queue.Count} cases did not run");
            if (later.Count > 0) problems.Add($"{later.Count} later checks did not run");
            foreach (string name in CoreCases)
                if (!completed.Contains(name)) problems.Add($"the core case '{name}' did not run to the end (skipped, or not run): the world gave too few towns");
            if (!completed.Any(n => n.StartsWith("real path: ", StringComparison.Ordinal))) problems.Add("no real path case ran to the end");
            if (!roundTripChecked) problems.Add("the round trip through the save was not checked");
            Log.Message($"{Tag}{ran} cases, {skipped} skipped, {problems.Count} problems; {TributaryMetrics()}");
            if (problems.Count == 0)
                try
                {
                    PrepareAndReEnable();
                }
                catch (Exception e)
                {
                    problems.Add($"prepare for removal: {e.GetType().Name}: {e.Message}");
                }
            return problems;
        }

        /// <summary>
        /// Preparing the save for removal drops what had yielded and switches the subsystem off; re-enabling reconciles first. Last, because it
        /// takes every world party and the sim's setting with it.
        /// </summary>
        private static void PrepareAndReEnable()
        {
            const string c = "prepare for removal";
            LivingWorldComponent comp = Comp;
            Settlement s = PickTown(), t = PickTown();
            if (s == null || t == null)
            {
                Skip(c, "no town");
                return;
            }
            TributaryRecord yielded = Yielded(s), held = Yielded(t);
            Pump(held);
            Expect(c, held.state == TributaryState.Held, "no held town to prepare");
            Faction client = ClientOf(held);
            // Tribute on the road is delivered by the preparation, not lost with the parties.
            Tributaries.TestRevolt = false;
            for (int day = 0; day < Tuning.T.tributeIntervalDays; day++) Tributaries.DailyStep(comp, 1f, held);
            Tributaries.TestRevolt = null;
            LivingWorldMod.PrepareForRemoval();
            Expect(c, held.payments == 1 && !WorldParty.All.Any(p => p.kind == LWPartyDefOf.LW_Tribute), $"tribute on the road was not delivered by the preparation ({held.payments} payments)");
            Expect(c, !comp.tributaries.Contains(yielded) && comp.tributaries.Contains(held), "a yielded record survived the preparation, or a held one did not");
            Expect(c, !LivingWorldMod.Settings.tributaries && !Tributaries.Enabled && comp.preparedForRemoval, "the subsystem is still on in a prepared save");
            Faction third = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => FactionState.IsSimulated(f) && f != client && f.loadID != held.oldFactionId);
            if (third != null) t.SetFaction(third);
            comp.preparedForRemoval = false; // "Re-enable"
            Tributaries.Reconcile(comp);
            if (third != null) Expect(c, held.state == TributaryState.Lost, $"re-enabling left a town another faction owns {held.state}");
            Expect(c, LivingWorldMod.Settings.tributaries == LivingWorldMod.Stored.tributaries, "the settings in effect are not the player's after re-enabling");
        }
    }
}
