using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Saved as a byte: append only. Out of range on load reads as <see cref="Lost"/>.</summary>
    public enum TributaryState : byte { Yielded, Held, Released, Revolted, Lost, Refused }

    /// <summary>
    /// The last stage of a transfer that finished (saved with the record). Ownership changes before any reward or history is
    /// written, so everything irreversible comes after <see cref="Captured"/> and is rolled forward when it keeps failing.
    /// </summary>
    public enum TransferStage : byte { None, ClientReady, Captured, DeedDone, War, Committed }

    /// <summary>Whether the assault's defenders were beaten when the town's map was removed (decided at teardown, never sampled earlier).</summary>
    public enum DefeatSnapshot : byte { Pending, Defeated, NotDefeated }

    /// <summary>The two options of the hold dialog, in the order of the node's options (tests pick one by this).</summary>
    public enum HoldChoice : byte { Hold, Raze }

    /// <summary>How much of a held town's income the colony takes. Saved as a byte (append only); the tuning lists are indexed by it.</summary>
    public enum TributeRate : byte { Suspended, Light, Normal, Heavy }

    /// <summary>
    /// A conquered town the colony chose to hold: yielded (the colony's people are still on its map), then held as the free town of a
    /// client faction made for it, or ended (released, revolted, lost) and kept for the factions page. Refers to everything by id.
    /// </summary>
    public class TributaryRecord : IExposable
    {
        public const int Schema = 1;

        public int schemaVersion = Schema;
        public int id;
        public int settlementId = -1;
        public string townName;
        public int oldFactionId = -1;
        public string oldFactionName;
        public int clientFactionId = -1;
        public string clientFactionName;
        public TributaryState state;
        public TransferStage stage;
        public bool resubmit;
        public DefeatSnapshot snapshot;
        public int snapshotTick = -1;
        /// <summary>The colonists on the town's map when it yielded (Yielded only): the vanilla deed is theirs.</summary>
        public List<Pawn> raiders = new List<Pawn>();
        public int failures;
        public int takenTicks, heldSinceTicks = -1, endedTicks = -1;
        public int reconquestWarId = -1, chronicleId = -1;
        /// <summary>Which parts of the deed already ran (bit per part): each runs at most once, whatever fails around it.</summary>
        public int deedParts;
        /// <summary>What the deed did to the colony's relations (for the Held letter): kept in memory only, a letter after a reload goes without it.</summary>
        internal string dealings;
        // Tribute and loyalty (held towns).
        public TributeRate rate = TributeRate.Normal;
        /// <summary>Taken from the client's treasury since the last payment (daily, from that day's income and rate), and what it could not pay of what was due.</summary>
        public float accruedSilver, arrears;
        /// <summary>Sum of the daily shares and the days of tribute since the last payment (a payment falls due after the tuned number of days).</summary>
        public float shareDays, accrualDays;
        /// <summary>The convoy sent last (a tribute party id); payments made, payments that fell short, and the worth (silver and goods) delivered.</summary>
        public int convoyPartyId = -1, payments, missed;
        public float paidTotal;
        public int lastWarningTick = FactionState.Never, lastGiftTick = FactionState.Never, lastRateTick = FactionState.Never;
        /// <summary>Simulated days (real days times the pace, as unrest moves) since the rate was last changed; a record that never changed it stands at <see cref="RateOpen"/>, so its first change is free.</summary>
        public float rateDays = RateOpen;
        public const float RateOpen = 100000f;
        /// <summary>Until when its defenders were paid for, or it was left to its fate (read by the unrest target; the wars of a held town set them).</summary>
        public int protectedUntilTick = FactionState.Never, abandonedUntilTick = FactionState.Never;

        public bool Live => state == TributaryState.Yielded || state == TributaryState.Held;

        public void ExposeData()
        {
            byte st = (byte)state, stg = (byte)stage, snap = (byte)snapshot, rt = (byte)rate;
            Scribe_Values.Look(ref schemaVersion, "tr_schema", Schema);
            Scribe_Values.Look(ref id, "tr_id");
            Scribe_Values.Look(ref settlementId, "tr_settlement", -1);
            Scribe_Values.Look(ref townName, "tr_town");
            Scribe_Values.Look(ref oldFactionId, "tr_old", -1);
            Scribe_Values.Look(ref oldFactionName, "tr_oldName");
            Scribe_Values.Look(ref clientFactionId, "tr_client", -1);
            Scribe_Values.Look(ref clientFactionName, "tr_clientName");
            Scribe_Values.Look(ref st, "tr_state");
            Scribe_Values.Look(ref stg, "tr_stage");
            Scribe_Values.Look(ref snap, "tr_snapshot");
            Scribe_Values.Look(ref resubmit, "tr_resubmit");
            Scribe_Values.Look(ref snapshotTick, "tr_snapshotTick", -1);
            Scribe_Collections.Look(ref raiders, "tr_raiders", LookMode.Reference);
            Scribe_Values.Look(ref failures, "tr_failures");
            Scribe_Values.Look(ref takenTicks, "tr_taken");
            Scribe_Values.Look(ref heldSinceTicks, "tr_held", -1);
            Scribe_Values.Look(ref endedTicks, "tr_ended", -1);
            Scribe_Values.Look(ref reconquestWarId, "tr_war", -1);
            Scribe_Values.Look(ref chronicleId, "tr_chronicle", -1);
            Scribe_Values.Look(ref deedParts, "tr_deed");
            Scribe_Values.Look(ref rt, "tr_rate", (byte)TributeRate.Normal);
            Scribe_Values.Look(ref accruedSilver, "tr_accruedSilver");
            Scribe_Values.Look(ref arrears, "tr_arrears");
            Scribe_Values.Look(ref shareDays, "tr_shareDays");
            Scribe_Values.Look(ref accrualDays, "tr_accrualDays");
            Scribe_Values.Look(ref convoyPartyId, "tr_convoy", -1);
            Scribe_Values.Look(ref payments, "tr_payments");
            Scribe_Values.Look(ref missed, "tr_missed");
            Scribe_Values.Look(ref paidTotal, "tr_paidTotal");
            Scribe_Values.Look(ref lastWarningTick, "tr_lastWarning", FactionState.Never);
            Scribe_Values.Look(ref lastGiftTick, "tr_lastGift", FactionState.Never);
            Scribe_Values.Look(ref lastRateTick, "tr_lastRate", FactionState.Never);
            Scribe_Values.Look(ref rateDays, "tr_rateDays", RateOpen);
            Scribe_Values.Look(ref protectedUntilTick, "tr_protectedUntil", FactionState.Never);
            Scribe_Values.Look(ref abandonedUntilTick, "tr_abandonedUntil", FactionState.Never);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                rate = rt > (byte)TributeRate.Heavy ? TributeRate.Normal : (TributeRate)rt;
                state = Tributaries.ReadState(st);
                stage = stg > (byte)TransferStage.Committed ? TransferStage.None : (TransferStage)stg;
                snapshot = snap > (byte)DefeatSnapshot.NotDefeated ? DefeatSnapshot.Pending : (DefeatSnapshot)snap;
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                raiders ??= new List<Pawn>();
                raiders.RemoveAll(p => p == null);
            }
        }
    }

    /// <summary>
    /// Holding a conquered town: when the colony's assault leaves a town undefended, vanilla would destroy it; the player may hold it
    /// instead. The town then becomes the free town of a faction made for it (the client), allied to the colony, at war with its old
    /// masters. The faction changes only after the assault's map is gone, in a staged, retryable transfer (see <see cref="Transfer"/>).
    /// </summary>
    public static class Tributaries
    {
        /// <summary>Failures of one stage after which a record is aborted (before ownership changed) or rolled forward (after).</summary>
        public const int MaxFailures = 3;
        private const int PartRaided = 1, PartEnemies = 2, PartTale = 4, PartDeed = 8;

        private static readonly HashSet<int> pendingChoice = new HashSet<int>();
        private static readonly HashSet<int> razeChosen = new HashSet<int>();
        private static int mapsTick = -1, mapsCount = -1;
        private static bool anyNpcMap;
        private static bool stateWarned;

        // Tests.
        internal static HoldChoice? TestChoice;
        /// <summary>The stage the transfer throws at, <see cref="TestFailCount"/> times (<see cref="int.MaxValue"/>: always).</summary>
        internal static TransferStage? TestFailAt;
        internal static int TestFailCount = 1;
        /// <summary>Throws right after a new client faction was made and saved on the record, once.</summary>
        internal static bool TestFailAfterCreate;
        /// <summary>The faction's goodwill cap with the colony, as the transfer sees it (a modded situation, simulated).</summary>
        internal static int? TestCapOverride;
        /// <summary>Why the hold node could not be used by a test (the option was disabled, or missing).</summary>
        internal static readonly List<string> TestProblems = new List<string>();
        /// <summary>Requested +20 goodwill changes with enemies of a town's old masters, over the game's life (the deed must run once).</summary>
        internal static int StatEnemyBonus;
        /// <summary>Memes the client's kind must have, as a modded kind could demand (the old faith is checked against them).</summary>
        internal static List<MemeDef> TestForcedMemes;
        /// <summary>Forces the roll of a revolt (true: the town rises, false: it never does), and the choice between going back to its old masters and breaking free.</summary>
        internal static bool? TestRevolt, TestLiberate;
        /// <summary>The colony is not there for the tribute (no colony to pay), and no convoy can be made (a full map of parties), and no thing can be placed near the drop spot.</summary>
        internal static bool TestNoColony, TestNoParty, TestPlaceFails;

        public static bool Enabled => LivingWorldMod.Settings.tributaries;

        public static void ResetStatics()
        {
            pendingChoice.Clear();
            razeChosen.Clear();
            mapsTick = mapsCount = -1;
            TestChoice = null;
            TestFailAt = null;
            TestFailCount = 1;
            TestFailAfterCreate = false;
            TestCapOverride = null;
            TestForcedMemes = null;
            TestRevolt = TestLiberate = null;
            TestNoColony = TestNoParty = TestPlaceFails = false;
            StatEnemyBonus = 0;
            TestProblems.Clear();
        }

        internal static TributaryState ReadState(byte raw)
        {
            if (raw <= (byte)TributaryState.Refused) return (TributaryState)raw;
            if (!stateWarned)
            {
                stateWarned = true;
                LWLog.Warning($"tributary record with state {raw}: read as Lost");
            }
            return TributaryState.Lost;
        }

        // ---- Lookups -------------------------------------------------------------------------------------------

        /// <summary>The held record of a town, if it is held now.</summary>
        public static TributaryRecord HeldFor(WorldObject s)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (s == null || comp == null) return null;
            List<TributaryRecord> list = comp.tributaries;
            for (int i = 0; i < list.Count; i++)
                if (list[i].state == TributaryState.Held && list[i].settlementId == s.ID) return list[i];
            return null;
        }

        /// <summary>The record of a town that is live (yielded or held).</summary>
        public static TributaryRecord RecordFor(int settlementId)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null) return null;
            List<TributaryRecord> list = comp.tributaries;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Live && list[i].settlementId == settlementId) return list[i];
            return null;
        }

        /// <summary>The faction is the client of a held town now: the client rules apply to it.</summary>
        public static bool IsClient(Faction f)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (f == null || comp == null) return false;
            List<TributaryRecord> list = comp.tributaries;
            for (int i = 0; i < list.Count; i++)
                if (list[i].state == TributaryState.Held && list[i].clientFactionId == f.loadID) return true;
            return false;
        }

        /// <summary>The faction was made by the colony as a client (held now or not).</summary>
        public static bool WasClient(Faction f)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            return f != null && comp != null && comp.clientFactionIds.Contains(f.loadID);
        }

        internal static int LiveCount(LivingWorldComponent comp)
        {
            int n = 0;
            foreach (TributaryRecord r in comp.tributaries)
                if (r.Live) n++;
            return n;
        }

        /// <summary>Yielded records that will still need a new faction (none made yet, not a town that submits again).</summary>
        internal static int PendingCreations(LivingWorldComponent comp, TributaryRecord except = null)
        {
            int n = 0;
            foreach (TributaryRecord r in comp.tributaries)
                if (r != except && r.state == TributaryState.Yielded && !r.resubmit && r.clientFactionId < 0) n++;
            return n;
        }

        /// <summary>The town is the only one of a living ex-client: it submits again, with no new faction.</summary>
        public static bool IsResubmission(Settlement s)
        {
            Faction f = s?.Faction;
            return f != null && !f.defeated && WasClient(f) && !IsClient(f) && SettlementOps.SettlementCount(f) == 1;
        }

        // ---- Client kind ---------------------------------------------------------------------------------------

        /// <summary>
        /// The kind of faction a conquered town becomes: its own if that kind can be a client, else the closest one that can (nearest in
        /// technology, same category, most common). Null if there is none.
        /// </summary>
        public static FactionDef ClientDefFor(FactionDef conquered)
        {
            if (conquered == null) return null;
            if (EligibleClientDef(conquered, conquered)) return conquered;
            FactionDef best = null;
            foreach (FactionDef d in DefDatabase<FactionDef>.AllDefsListForReading)
                if (EligibleClientDef(d, conquered) && (best == null || CloserTo(d, best, conquered))) best = d;
            return best;
        }

        private static bool EligibleClientDef(FactionDef d, FactionDef conquered)
        {
            if (!Rebellion.CanSplit(d) || d.naturalEnemy || d.baseTraderKinds.NullOrEmpty() || d.pawnGroupMakers.NullOrEmpty()) return false;
            // Never hostile to the colony or its neighbours by its kind (the conditions of the permanent enemy situation).
            if (d.permanentEnemy || d.permanentEnemyToEveryoneExceptPlayer) return false;
            FactionDef player = Faction.OfPlayerSilentFail?.def ?? FactionDefOf.PlayerColony;
            return d.permanentEnemyToEveryoneExcept == null || (d.permanentEnemyToEveryoneExcept.Contains(player) && d.permanentEnemyToEveryoneExcept.Contains(conquered));
        }

        private static bool CloserTo(FactionDef a, FactionDef b, FactionDef conquered)
        {
            int da = Mathf.Abs((int)a.techLevel - (int)conquered.techLevel), db = Mathf.Abs((int)b.techLevel - (int)conquered.techLevel);
            if (da != db) return da < db;
            bool sa = a.categoryTag == conquered.categoryTag, sb = b.categoryTag == conquered.categoryTag;
            if (sa != sb) return sa;
            if (!Mathf.Approximately(a.settlementGenerationWeight, b.settlementGenerationWeight)) return a.settlementGenerationWeight > b.settlementGenerationWeight;
            return string.CompareOrdinal(a.defName, b.defName) < 0;
        }

        // ---- Colony --------------------------------------------------------------------------------------------

        /// <summary>
        /// The colony a town deals with: among the player's homes on the planet's surface, the ones it can reach first, then the nearest
        /// (ties by map id). Map null: there is none.
        /// </summary>
        public static (Map map, bool reachable) ColonyFor(Settlement town)
        {
            Map best = null;
            bool bestReach = false;
            float bestDist = float.MaxValue;
            if (town == null || !town.Tile.Valid || TestNoColony) return (null, false);
            foreach (Map m in Find.Maps)
            {
                if (!m.IsPlayerHome || m.Parent is not Settlement || !m.Tile.Valid || m.Tile.Layer == null || !m.Tile.Layer.IsRootSurface || m.Tile.Layer != town.Tile.Layer) continue;
                bool reach = Find.WorldReachability.CanReach(town.Tile, m.Tile);
                float d = Find.WorldGrid.ApproxDistanceInTiles(town.Tile, m.Tile);
                bool better = best == null || (reach && !bestReach) || (reach == bestReach && (d < bestDist - 0.0001f || (Mathf.Abs(d - bestDist) <= 0.0001f && m.uniqueID < best.uniqueID)));
                if (!better) continue;
                best = m;
                bestReach = reach;
                bestDist = d;
            }
            return (best, best != null && bestReach);
        }

        /// <summary>The colony's name for lines about a town (its settlement's label, else the colony's faction).</summary>
        public static string ColonyLabel(Settlement town) => ColonyFor(town).map?.Parent?.Label ?? Faction.OfPlayer.Name;

        // ---- The offer -----------------------------------------------------------------------------------------

        /// <summary>Holding is offered for this undefended town: on, a simulated faction's town the colony is fighting, no quest around it.</summary>
        public static bool Offerable(Settlement s)
        {
            if (!Enabled || s == null || s.Faction == null || s.Faction.IsPlayer || !FactionState.IsSimulated(s.Faction)) return false;
            if (!s.Tile.Valid || s.Tile.Layer == null || !s.Tile.Layer.IsRootSurface || HeldFor(s) != null) return false;
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            return life != null && life.initialized && s.Faction.HostileTo(Faction.OfPlayer) && !SettlementOps.QuestRelated(s);
        }

        /// <summary>Why the colony cannot hold this town (null: it can).</summary>
        public static string CannotHold(Settlement s)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            int max = Tuning.T.tributaryMax;
            if (LiveCount(comp) >= max) return "LW_Hold_Cap".Translate(max);
            bool again = IsResubmission(s);
            if (!again && comp.clientFactionsCreated + PendingCreations(comp) >= Tuning.T.tributaryFactionBudget) return "LW_Hold_Budget".Translate();
            if (!again && ClientDefFor(s.Faction.def) == null) return "LW_Hold_NoKind".Translate();
            if (ColonyFor(s).map == null) return "LW_Hold_NoColony".Translate();
            return null;
        }

        internal static DiaNode BuildHoldNode(Settlement s)
        {
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            var node = new DiaNode("LW_Hold_Text".Translate(s.Label.Named("TOWN"), Mathf.RoundToInt(life?.population ?? 0f).Named("POP"), Tuning.T.tributeIntervalDays.Named("DAYS")));
            var hold = new DiaOption("LW_Hold_Option".Translate(s.Label))
            {
                resolveTree = true,
                action = () =>
                {
                    // The dialog pauses the game, but check again: what was true when it opened is not an order.
                    string why = Offerable(s) && s.HasMap ? CannotHold(s) : "LW_Hold_NoLongerApplies".Translate().Resolve();
                    if (why != null) Messages.Message(why, new GlobalTargetInfo(s), MessageTypeDefOf.RejectInput, historical: false);
                    else Yield(s, s.Map);
                },
            };
            string cannot = CannotHold(s);
            if (cannot != null) hold.Disable(cannot);
            node.options.Add(hold);
            node.options.Add(new DiaOption("LW_Hold_Raze".Translate(s.Label)) { resolveTree = true, action = () => razeChosen.Add(s.ID) });
            return node;
        }

        /// <summary>
        /// Called before vanilla decides whether the town of an assault is defeated. False: skip vanilla's destruction (the offer is open, or the
        /// town yielded and its map still stands).
        /// </summary>
        internal static bool BeforeDefeatCheck(Settlement s)
        {
            if (!AnyNpcSettlementMap() || !s.HasMap) return true;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null) return true;
            TributaryRecord y = YieldedFor(comp, s.ID);
            if (y != null)
            {
                // Yielded: never destroyed while it is the old masters' and its map lives. The defeat is judged at the map's removal.
                if (s.Faction != null && s.Faction.loadID == y.oldFactionId) return false;
                EndYielded(comp, y, s, "the town changed hands");
                if (y.state != TributaryState.Held) return true;
            }
            if (HeldFor(s) != null) Reconcile(comp); // a held town the colony attacked: hostile by now, so ended
            Faction f = s.Faction;
            if (f == null || f.IsPlayer || !SettlementDefeatUtility.IsDefeated(s.Map, f)) return true;
            if (pendingChoice.Contains(s.ID)) return false;
            if (razeChosen.Remove(s.ID)) return true;
            if (!Offerable(s)) return true;
            DiaNode node = BuildHoldNode(s);
            if (TestChoice != null) return RunTestChoice(s, node, TestChoice.Value);
            pendingChoice.Add(s.ID);
            int id = s.ID;
            Find.WindowStack.Add(new Dialog_NodeTree(node, delayInteractivity: false, radioMode: false, title: "LW_Hold_Title".Translate(s.Label))
            {
                // Closing runs before the option's action, in the same frame: nothing ticks between them.
                closeAction = () => pendingChoice.Remove(id),
            });
            return false;
        }

        /// <summary>The same node a player gets, answered by the test: a disabled option is a failure of the case, not a shortcut.</summary>
        private static bool RunTestChoice(Settlement s, DiaNode node, HoldChoice choice)
        {
            DiaOption option = (int)choice < node.options.Count ? node.options[(int)choice] : null;
            if (option == null || option.disabled)
            {
                TestProblems.Add($"{s.Label}: the {choice} option is {(option == null ? "missing" : "disabled (" + option.disabledReason + ")")}");
                return false;
            }
            option.action();
            if (choice == HoldChoice.Hold) return false;
            razeChosen.Remove(s.ID);
            return true;
        }

        private static bool AnyNpcSettlementMap()
        {
            int now = Find.TickManager.TicksGame, count = Find.Maps.Count;
            if (now == mapsTick && count == mapsCount) return anyNpcMap;
            mapsTick = now;
            mapsCount = count;
            anyNpcMap = false;
            foreach (Map m in Find.Maps)
                if (m.Parent is Settlement p && p.Faction != null && !p.Faction.IsPlayer)
                {
                    anyNpcMap = true;
                    break;
                }
            return anyNpcMap;
        }

        private static TributaryRecord YieldedFor(LivingWorldComponent comp, int settlementId)
        {
            foreach (TributaryRecord r in comp.tributaries)
                if (r.state == TributaryState.Yielded && r.settlementId == settlementId) return r;
            return null;
        }

        // ---- Yield and the snapshot ----------------------------------------------------------------------------

        /// <summary>
        /// The colony holds the town: a record is made and the town stays its old masters' until the colony's people leave its map. Vanilla's
        /// deed (goodwill with the old masters' enemies, the tale, the ideology memory) is not replayed here: it runs once, at the transfer.
        /// </summary>
        internal static TributaryRecord Yield(Settlement s, Map map)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction old = s.Faction;
            if (comp == null || old == null) return null;
            var r = new TributaryRecord
            {
                id = comp.NextId(),
                settlementId = s.ID,
                townName = s.Label,
                oldFactionId = old.loadID,
                oldFactionName = old.Name,
                state = TributaryState.Yielded,
                takenTicks = SimClock.Now,
                resubmit = IsResubmission(s),
            };
            if (map != null) r.raiders.AddRange(map.mapPawns.FreeColonistsSpawned);
            comp.tributaries.Add(r);
            Notify("LW_Hold_YieldedLabel".Translate(), "LW_Hold_YieldedText".Translate(s.Label.Named("TOWN"), old.NameColored.Named("FACTION")), LetterDefOf.PositiveEvent, s);
            LWLog.Message($"tributary: {s.Label} of {old.Name} yielded (record {r.id}{(r.resubmit ? ", submits again" : "")})");
            return r;
        }

        /// <summary>The town's map is about to be removed: judge now whether its defenders were beaten (the pawns still stand on it).</summary>
        internal static void Notify_MapAboutToBeRemoved(Settlement s)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || !s.HasMap || s.Faction == null) return;
            // A choice made for this map ends with it (a later defeat of the town is asked afresh).
            razeChosen.Remove(s.ID);
            pendingChoice.Remove(s.ID);
            TributaryRecord r = YieldedFor(comp, s.ID);
            if (r == null || s.Faction.loadID != r.oldFactionId) return;
            r.snapshot = SettlementDefeatUtility.IsDefeated(s.Map, s.Faction) ? DefeatSnapshot.Defeated : DefeatSnapshot.NotDefeated;
            r.snapshotTick = SimClock.Now;
        }

        // ---- Tick, reconcile -----------------------------------------------------------------------------------

        /// <summary>The identity and transfer steps never take the world's tick down with them: an exception is logged once and the day goes on.</summary>
        public static void TickSafe(LivingWorldComponent comp)
        {
            try
            {
                Tick(comp);
            }
            catch (Exception e)
            {
                LWLog.ErrorOnce("tributary tick: " + e, 0x4c570a32);
            }
        }

        public static void ReconcileSafe(LivingWorldComponent comp)
        {
            try
            {
                Reconcile(comp);
            }
            catch (Exception e)
            {
                LWLog.ErrorOnce("tributary reconcile: " + e, 0x4c570a33);
            }
        }

        /// <summary>Every 250 ticks: identity first, then the yielded towns whose maps are gone. A test passes the one record it is driving, so a town another case left waiting is not transferred under it.</summary>
        public static void Tick(LivingWorldComponent comp, TributaryRecord only = null)
        {
            if (comp.tributaries.Count == 0) return;
            Reconcile(comp);
            for (int i = comp.tributaries.Count - 1; i >= 0; i--)
            {
                if (i >= comp.tributaries.Count) continue;
                TributaryRecord r = comp.tributaries[i];
                if (r.state == TributaryState.Yielded && (only == null || r == only)) StepYielded(comp, r);
            }
        }

        /// <summary>The town still exists and is its old masters' (or, in a transfer under way, its client's).</summary>
        private static bool TownOk(TributaryRecord r, Settlement s) =>
            s != null && !s.Destroyed && s.Faction != null && (s.Faction.loadID == r.oldFactionId || (r.clientFactionId >= 0 && s.Faction.loadID == r.clientFactionId));

        /// <summary>This record made a faction for the town (a town that submits again has its own, which no record made).</summary>
        private static bool HasClient(TributaryRecord r) => !r.resubmit && r.clientFactionId >= 0;

        /// <summary>The faction made for the town owns it already: the capture is behind us, whatever the record says.</summary>
        private static bool ClientOwns(TributaryRecord r, Settlement s) =>
            HasClient(r) && s != null && !s.Destroyed && s.Faction != null && s.Faction.loadID == r.clientFactionId;

        private static void StepYielded(LivingWorldComponent comp, TributaryRecord r)
        {
            var s = LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement;
            if (!TownOk(r, s))
            {
                EndYielded(comp, r, s, "the town is gone or changed hands");
                return;
            }
            if (s.HasMap) return; // the snapshot comes at the map's removal
            if (r.snapshot != DefeatSnapshot.Defeated)
            {
                if (!HasClient(r)) Messages.Message("LW_Hold_DrivenOut".Translate(r.townName, r.oldFactionName), new GlobalTargetInfo(s), MessageTypeDefOf.NeutralEvent, historical: false);
                EndYielded(comp, r, s, "driven out");
                return;
            }
            // Until the town changes hands a quest it picks up holds every stage back, a retry included.
            if (r.stage < TransferStage.Captured && !ClientOwns(r, s) && SettlementOps.QuestRelated(s))
            {
                if (SimClock.Now - r.snapshotTick < Tuning.T.tributaryTransferWaitDays * GenDate.TicksPerDay) return;
                if (!HasClient(r)) Messages.Message("LW_Hold_QuestKept".Translate(r.townName, r.oldFactionName), new GlobalTargetInfo(s), MessageTypeDefOf.NeutralEvent, historical: false);
                EndYielded(comp, r, s, "a quest kept it");
                return;
            }
            Transfer(comp, r, s);
        }

        /// <summary>
        /// Identity, whatever the setting says: records follow the world. A held town whose client is gone or defeated, or that another
        /// faction owns now, is lost; one whose client turned hostile to the colony has revolted. Yielded towns that changed hands are dropped.
        /// </summary>
        public static void Reconcile(LivingWorldComponent comp)
        {
            if (comp.tributaries.Count == 0) return;
            for (int i = comp.tributaries.Count - 1; i >= 0; i--)
            {
                if (i >= comp.tributaries.Count) continue;
                TributaryRecord r = comp.tributaries[i];
                var s = LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement;
                if (r.state == TributaryState.Yielded)
                {
                    if (!TownOk(r, s)) EndYielded(comp, r, s, "the town is gone or changed hands");
                    continue;
                }
                if (r.state != TributaryState.Held) continue;
                Faction client = comp.FactionById(r.clientFactionId);
                if (client == null || client.defeated || s == null || s.Destroyed || s.Faction != client) End(comp, r, TributaryState.Lost, s);
                else if (client.HostileTo(Faction.OfPlayer)) End(comp, r, TributaryState.Revolted, s);
            }
            comp.clientFactionIds.RemoveAll(id => comp.FactionById(id) == null);
            PruneHistory(comp);
        }

        private static void End(LivingWorldComponent comp, TributaryRecord r, TributaryState to, Settlement s, bool announce = true)
        {
            r.state = to;
            r.endedTicks = SimClock.Now;
            string town = r.townName;
            ReturnAccrued(comp, r, s);
            // Tribute on the road is only ever delivered to a colony its client is still a friend of.
            if (to == TributaryState.Revolted || to == TributaryState.Lost) RecallConvoys(r);
            if (to == TributaryState.Revolted)
            {
                if (announce) Messages.Message("LW_Tributary_Revolted".Translate(town), s != null ? new GlobalTargetInfo(s) : GlobalTargetInfo.Invalid, MessageTypeDefOf.NegativeEvent, historical: false);
                LWLog.Message($"tributary: {town} turned hostile to the colony");
                return;
            }
            Faction owner = s != null && !s.Destroyed ? s.Faction : null;
            ChronicleEventDef def = LWChronicleDefOf.LW_TributaryLost;
            comp.chronicle.Record(def, def.text.Formatted(town.Named("SETTLEMENT"), Faction.OfPlayer.Name.Named("COLONY"), (owner?.Name ?? "LW_Tributary_NoOwner".Translate().Resolve()).Named("OWNER")).Resolve(),
                Faction.OfPlayer, owner, tile: s != null && !s.Destroyed ? s.Tile : (PlanetTile?)null, involvesPlayer: true, silent: true);
            TaggedString text = owner != null ? "LW_Tributary_LostText".Translate(town.Named("TOWN"), owner.NameColored.Named("OWNER")) : "LW_Tributary_GoneText".Translate(town.Named("TOWN"));
            Notify("LW_Tributary_LostLabel".Translate(town), text, LetterDefOf.NegativeEvent, owner != null ? s : null);
            LWLog.Message($"tributary: {town} is lost (now {owner?.Name ?? "nobody's"})");
        }

        private static void Drop(LivingWorldComponent comp, TributaryRecord r, string why)
        {
            comp.tributaries.Remove(r);
            LWLog.Message($"tributary: record {r.id} for {r.townName} dropped: {why}");
        }

        /// <summary>
        /// A yielded record that cannot go on as it is. A record that made no faction is simply dropped. One whose client owns the town already
        /// (or is past the capture) is completed, or ended as lost when the town has left it since. One whose client was made but never got
        /// the town is aborted: the client is defeated if it owns nothing, and the record is kept as refused.
        /// </summary>
        private static void EndYielded(LivingWorldComponent comp, TributaryRecord r, Settlement s, string why)
        {
            Faction client = r.clientFactionId >= 0 ? comp.FactionById(r.clientFactionId) : null;
            bool owned = s != null && !s.Destroyed && client != null && s.Faction == client;
            bool past = r.stage >= TransferStage.Captured;
            if (owned && (past || !r.resubmit))
            {
                LWLog.Warning($"tributary: transfer of {r.townName} completed instead of ending ({why}): its client owns it");
                Settle(comp, r, s);
                return;
            }
            if (past)
            {
                if (HasClient(r) && client != null && SettlementOps.SettlementCount(client) == 0) client.defeated = true;
                LWLog.Warning($"tributary: {r.townName} left its client before the transfer ended ({why})");
                End(comp, r, TributaryState.Lost, s);
                return;
            }
            if (HasClient(r)) Abort(comp, r, why);
            else Drop(comp, r, why);
        }

        /// <summary>Ended records are kept for the factions page: at most the tuned number, none older than three years.</summary>
        private static void PruneHistory(LivingWorldComponent comp)
        {
            int now = SimClock.Now, ended = 0;
            comp.tributaries.RemoveAll(r => !r.Live && r.endedTicks >= 0 && now - r.endedTicks > GenDate.TicksPerYear * 3);
            foreach (TributaryRecord r in comp.tributaries)
                if (!r.Live) ended++;
            int max = Tuning.T.tributaryHistoryMax;
            if (ended <= max) return;
            var old = new List<TributaryRecord>();
            foreach (TributaryRecord r in comp.tributaries)
                if (!r.Live) old.Add(r);
            old.Sort((a, b) => a.endedTicks.CompareTo(b.endedTicks));
            for (int i = 0; i < ended - max; i++) comp.tributaries.Remove(old[i]);
        }

        /// <summary>
        /// The save is prepared for removing the mod: towns that had yielded stay their old masters' (vanilla destroys them at its next check),
        /// unless a client already owns one (completed), or a client was made for one (its faction is defeated, the record is refused).
        /// </summary>
        internal static void Notify_PreparedForRemoval(LivingWorldComponent comp)
        {
            foreach (TributaryRecord r in comp.tributaries.ToArray())
                if (r.state == TributaryState.Yielded) PrepareYielded(comp, r);
            pendingChoice.Clear();
            razeChosen.Clear();
        }

        internal static void PrepareYielded(LivingWorldComponent comp, TributaryRecord r) =>
            EndYielded(comp, r, LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement, "the save was prepared for removing the mod");

        // ---- Transfer ------------------------------------------------------------------------------------------

        private enum Gate : byte { Go, Wait, Drop }

        /// <summary>
        /// Yielded to held, as a staged transaction: the client is made and saved first, then ownership changes, then everything the colony is
        /// credited with (the deed, the reconquest war), then the record commits. Each stage checks its own result first, so a retry skips
        /// what is done. A stage that keeps failing aborts the record while the old masters still own the town, and rolls forward once the
        /// client does.
        /// </summary>
        internal static void Transfer(LivingWorldComponent comp, TributaryRecord r, Settlement s)
        {
            try
            {
                Gate gate = r.stage < TransferStage.Captured ? Prevalidate(comp, r, s) : Gate.Go;
                if (gate == Gate.Wait) return;
                if (gate == Gate.Drop)
                {
                    EndYielded(comp, r, s, "the transfer is no longer possible");
                    return;
                }
                Run(comp, r, s, forward: false);
            }
            catch (Exception e)
            {
                r.failures++;
                if (r.failures == 1) LogFailure($"tributary transfer of {r.townName} failed at {r.stage}", e);
                if (r.failures >= MaxFailures) Settle(comp, r, s);
            }
        }

        /// <summary>
        /// No mutation: is the town still takeable, and does the budget still hold (a resubmission that no longer is one falls back to a new
        /// faction)? Asked again before every attempt until the client owns the town: a map or a quest that came meanwhile makes it wait.
        /// </summary>
        private static Gate Prevalidate(LivingWorldComponent comp, TributaryRecord r, Settlement s)
        {
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            Faction owner = s.Faction;
            bool clientOwns = ClientOwns(r, s);
            if (!s.Spawned || owner == null || (owner.loadID != r.oldFactionId && !clientOwns) || life == null || !life.initialized) return Gate.Drop;
            if (s.HasMap || (!clientOwns && SettlementOps.QuestRelated(s))) return Gate.Wait;
            Faction old = owner;
            if (r.clientFactionId >= 0) return Gate.Go; // the client exists (an earlier attempt): its budget is spent
            if (r.resubmit && !IsResubmission(s)) r.resubmit = false;
            if (r.resubmit) return Gate.Go;
            if (comp.clientFactionsCreated + PendingCreations(comp, r) + 1 > Tuning.T.tributaryFactionBudget || ClientDefFor(old.def) == null) return Gate.Drop;
            return Gate.Go;
        }

        private static void Run(LivingWorldComponent comp, TributaryRecord r, Settlement s, bool forward)
        {
            Faction old = comp.FactionById(r.oldFactionId);
            if (r.stage < TransferStage.ClientReady && !StageClient(comp, r, s, old)) return;
            Faction client = comp.FactionById(r.clientFactionId);
            if (client == null) throw new InvalidOperationException("the client faction is missing");
            // The last look before ownership changes: a map or a quest that came since the gate holds the capture back (the next tick asks again).
            if (!forward && r.stage < TransferStage.Captured && !ClientOwns(r, s) && (s.HasMap || SettlementOps.QuestRelated(s))) return;
            Step(forward, TransferStage.Captured, r, () => StageCapture(comp, r, s, client));
            Step(forward, TransferStage.DeedDone, r, () => StageDeed(comp, r, s, old, client));
            Step(forward, TransferStage.War, r, () => StageWar(comp, r, old, client));
            Step(forward, TransferStage.Committed, r, () => StageCommit(comp, r, s, old, client));
        }

        private static void Step(bool forward, TransferStage stage, TributaryRecord r, Action body)
        {
            if (r.stage >= stage) return;
            if (forward)
            {
                try
                {
                    body();
                }
                catch (Exception e)
                {
                    LogFailure($"tributary transfer of {r.townName}, rolling forward past {stage}", e);
                }
            }
            else
            {
                Inject(stage);
                body();
            }
            r.stage = stage;
        }

        private static void Inject(TransferStage stage)
        {
            if (TestFailAt != stage || TestFailCount <= 0) return;
            TestFailCount--;
            throw new InjectedFailure("injected failure at " + stage);
        }

        /// <summary>A failure a test asked for: logged as a warning (the run's error count is for real ones).</summary>
        private sealed class InjectedFailure : InvalidOperationException
        {
            public InjectedFailure(string message) : base(message)
            {
            }
        }

        private static void LogFailure(string what, Exception e)
        {
            if (e is InjectedFailure) LWLog.Warning($"{what}: {e.Message}");
            else LWLog.Error($"{what}: {e}");
        }

        /// <summary>Too many failures: before the client owns the town the record ends, after it the transaction is completed.</summary>
        private static void Settle(LivingWorldComponent comp, TributaryRecord r, Settlement s)
        {
            Faction client = comp.FactionById(r.clientFactionId);
            bool owned = r.stage >= TransferStage.Captured || (!r.resubmit && s != null && !s.Destroyed && client != null && s.Faction == client);
            if (!owned)
            {
                Abort(comp, r, $"it failed {r.failures} times at {r.stage}");
                return;
            }
            try
            {
                Run(comp, r, s, forward: true);
            }
            catch (Exception e)
            {
                LogFailure($"tributary transfer of {r.townName} could not be rolled forward", e);
            }
            if (r.state != TributaryState.Held)
            {
                r.state = TributaryState.Held;
                r.heldSinceTicks = SimClock.Now;
            }
        }

        /// <summary>The transfer ends before ownership changed: a client this record made and that owns no town is defeated (it stays in the registry), and the record is refused.</summary>
        private static void Abort(LivingWorldComponent comp, TributaryRecord r, string why)
        {
            Faction client = r.clientFactionId >= 0 ? comp.FactionById(r.clientFactionId) : null;
            bool made = !r.resubmit && client != null;
            if (made && SettlementOps.SettlementCount(client) == 0) client.defeated = true;
            LWLog.Warning($"tributary: transfer of {r.townName} aborted: {why}");
            if (!made)
            {
                Drop(comp, r, why);
                return;
            }
            r.state = TributaryState.Refused;
            r.endedTicks = SimClock.Now;
            var s = LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement;
            if (s != null && !s.Destroyed && s.Faction != null && s.Faction.loadID == r.oldFactionId)
                Notify("LW_Hold_RefusedLabel".Translate(r.townName), "LW_Hold_RefusedText".Translate(r.townName.Named("TOWN"), r.oldFactionName.Named("FACTION")), LetterDefOf.NeutralEvent, s);
            else
                Messages.Message("LW_Hold_ChangedHands".Translate(r.townName), s != null && !s.Destroyed ? new GlobalTargetInfo(s) : GlobalTargetInfo.Invalid, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        // ---- Stage 1: the client -------------------------------------------------------------------------------

        /// <summary>
        /// Makes (or finds) the client, saves its id before anything else, then sets it up in sub-steps that check their own result.
        /// False: the transfer ended here (the client's goodwill with the colony is capped below zero, or vanilla refuses to make it friendly).
        /// </summary>
        private static bool StageClient(LivingWorldComponent comp, TributaryRecord r, Settlement s, Faction old)
        {
            if (old == null) throw new InvalidOperationException("the old faction is gone");
            Inject(TransferStage.ClientReady);
            Faction client;
            if (r.clientFactionId < 0)
            {
                if (r.resubmit)
                {
                    client = old;
                    r.clientFactionId = old.loadID;
                    r.clientFactionName = old.Name;
                }
                else
                {
                    FactionDef def = ClientDefFor(old.def) ?? throw new InvalidOperationException("no kind of faction can be its client");
                    client = MakeClient(comp, r, def);
                    if (TestFailAfterCreate)
                    {
                        TestFailAfterCreate = false;
                        throw new InjectedFailure("injected failure after the client was made");
                    }
                }
            }
            else client = comp.FactionById(r.clientFactionId) ?? throw new InvalidOperationException("the client faction is missing");
            SetUpClient(comp, r, old, client);
            Find.GoodwillSituationManager.RecalculateAll(canSendHostilityChangedLetter: false);
            int cap = TestCapOverride ?? Find.GoodwillSituationManager.GetMaxGoodwill(client);
            if (cap < 0) return Refuse(comp, r, s, client, "its goodwill with the colony is capped below zero");
            SetRelations(r, old, client, cap);
            if (client.HostileTo(Faction.OfPlayer)) return Refuse(comp, r, s, client, "vanilla keeps it hostile to the colony");
            r.stage = TransferStage.ClientReady;
            return true;
        }

        /// <summary>
        /// A new faction of the kind, on the registry and the budget the moment it exists (it can never be removed again, so an untracked one
        /// would stay in the world). Should making it throw after it was added, the stray is found and taken in.
        /// </summary>
        private static Faction MakeClient(LivingWorldComponent comp, TributaryRecord r, FactionDef def)
        {
            int highest = 0;
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading) highest = Mathf.Max(highest, f.loadID);
            Faction made = null;
            try
            {
                made = Rebellion.CreateFaction(def);
            }
            finally
            {
                if (made == null)
                    foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
                        if (f.loadID > highest && f.def == def && !comp.clientFactionIds.Contains(f.loadID)) made = f;
                if (made != null)
                {
                    r.clientFactionId = made.loadID;
                    r.clientFactionName = made.Name;
                    comp.clientFactionIds.Add(made.loadID);
                    comp.clientFactionsCreated++;
                }
            }
            return made;
        }

        private static void SetUpClient(LivingWorldComponent comp, TributaryRecord r, Faction old, Faction client)
        {
            if (!r.resubmit)
            {
                string name = "LW_Client_Name".Translate(r.townName).Resolve();
                if (client.Name != name) client.Name = name;
                AdoptOldIdeo(old, client);
            }
            // A faction that submits again keeps its state and its ruler; a new one starts with 300, also when a daily step made its state meanwhile.
            FactionState fs = comp.StateFor(client);
            if (fs != null && !r.resubmit)
            {
                fs.treasury = 300f;
                if (client.leader != null && fs.rulerThingId != client.leader.thingIDNumber) Rulers.InitNewFaction(comp, client);
            }
            r.clientFactionName = client.Name;
        }

        /// <summary>
        /// With Ideology and not in classic mode, the free town keeps the faith of the people it was made of: the old faction's primary ideo, when
        /// the client's kind can use it. Only the pointer moves; no ideo is ever removed, and the one the client was made with stays registered.
        /// </summary>
        internal static void AdoptOldIdeo(Faction old, Faction client)
        {
            if (!ModsConfig.IdeologyActive || Find.IdeoManager.classicMode) return;
            Ideo ideo = old.ideos?.PrimaryIdeo;
            if (ideo == null || client.ideos == null || client.ideos.PrimaryIdeo == ideo) return;
            IdeoGenerationParms parms = Rebellion.IdeoParms(client.def);
            if (TestForcedMemes != null) parms.forcedMemes = TestForcedMemes;
            if (!IdeoUtility.CanUseIdeo(client.def, ideo, parms)) return;
            client.ideos.SetPrimary(ideo);
            client.leader?.ideo?.SetIdeo(ideo);
        }

        /// <summary>
        /// Goodwill, all of it requested (vanilla refuses what it must): neutral with everyone else, friendly to the colony as far as the cap allows,
        /// hostile to the old masters last (that is what tells a retry the relations are done).
        /// </summary>
        private static void SetRelations(TributaryRecord r, Faction old, Faction client, int cap)
        {
            Faction player = Faction.OfPlayer;
            if (!r.resubmit)
                foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
                {
                    if (other == client || other == old || other.IsPlayer || other.Hidden || client.RelationWith(other, allowNull: true) == null) continue;
                    int delta = -client.BaseGoodwillWith(other);
                    if (delta != 0) client.TryAffectGoodwillWith(other, delta, canSendMessage: false, canSendHostilityLetter: false);
                }
            int target = Mathf.Min(Tuning.T.tributaryStartGoodwill, cap);
            int change = target - client.BaseGoodwillWith(player);
            if (change != 0) player.TryAffectGoodwillWith(client, change, canSendMessage: false, canSendHostilityLetter: false);
            // Vanilla moves a change toward natural goodwill by a quarter more: bring an overshoot back.
            int now = client.GoodwillWith(player);
            if (now - target > 5) player.TryAffectGoodwillWith(client, target - now, canSendMessage: false, canSendHostilityLetter: false);
            if (!r.resubmit && !client.HostileTo(old)) client.TryAffectGoodwillWith(old, client.GoodwillToMakeHostile(old), canSendMessage: false, canSendHostilityLetter: false);
        }

        /// <summary>The colony cannot have this town as a friend: no ownership changes. A client made for it is defeated (as Renewal does with one that finds no land); a town that would submit again just stays as it is.</summary>
        private static bool Refuse(LivingWorldComponent comp, TributaryRecord r, Settlement s, Faction client, string why)
        {
            if (r.resubmit)
            {
                Messages.Message("LW_Hold_NoLongerApplies".Translate(), new GlobalTargetInfo(s), MessageTypeDefOf.NeutralEvent, historical: false);
                Drop(comp, r, why);
                return false;
            }
            if (SettlementOps.SettlementCount(client) == 0) client.defeated = true;
            r.state = TributaryState.Refused;
            r.endedTicks = SimClock.Now;
            LWLog.Warning($"tributary: {s.Label} refused: {why}");
            Notify("LW_Hold_RefusedLabel".Translate(r.townName), "LW_Hold_RefusedText".Translate(r.townName.Named("TOWN"), r.oldFactionName.Named("FACTION")), LetterDefOf.NeutralEvent, s);
            return false;
        }

        // ---- Stages 2 to 5 -------------------------------------------------------------------------------------

        /// <summary>Ownership changes (a town that submits again already belongs to its faction): the people are calm and half a garrison stands.</summary>
        private static void StageCapture(LivingWorldComponent comp, TributaryRecord r, Settlement s, Faction client)
        {
            if (!r.resubmit && s.Faction != client)
            {
                Faction loser = s.Faction;
                try
                {
                    // Ownership first: what vanilla's capture does before it (a ruler's fall) must not be repeated by a retry.
                    SettlementOps.Capture(s, client, ownershipFirst: true);
                }
                catch (Exception)
                {
                    // Ownership may already have moved: then the rest of the transaction goes on, else it is tried again.
                    if (s.Faction != client) throw;
                    LWLog.Error($"tributary: capturing {r.townName} threw after it changed hands");
                    try
                    {
                        SettlementOps.CheckDefeated(loser, client);
                    }
                    catch (Exception e)
                    {
                        LWLog.Error($"tributary: checking the old masters of {r.townName} for defeat: {e}");
                    }
                }
            }
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            if (life != null && life.initialized)
            {
                life.originalFactionId = client.loadID;
                life.occupiedSinceTicks = -1;
                life.unrest = 0.35f;
                life.garrison = 0.4f * SettlementEconomy.TargetGarrison(life, comp.StateFor(client));
            }
            comp.RebuildIndexes(seedMissing: false);
        }

        /// <summary>What vanilla's defeat does, once the town is ours to keep: the ideology memory, +20 with the old masters' enemies, the tale, then the chronicle and the old masters' grudge.</summary>
        private static void StageDeed(LivingWorldComponent comp, TributaryRecord r, Settlement s, Faction old, Faction client)
        {
            if (Part(r, PartRaided))
            {
                var raiders = new List<Pawn>();
                foreach (Pawn p in r.raiders)
                    if (p != null && !p.Dead && !p.Destroyed && p.Faction == Faction.OfPlayer) raiders.Add(p);
                IdeoUtility.Notify_PlayerRaidedSomeone(raiders);
            }
            if (Part(r, PartEnemies) && old != null)
            {
                var lines = new StringBuilder();
                foreach (Faction f in Find.FactionManager.AllFactions)
                {
                    if (f.Hidden || f.IsPlayer || f == old || f == client || !f.HostileTo(old)) continue;
                    StatEnemyBonus++;
                    FactionRelationKind kind = f.PlayerRelationKind;
                    int goodwill = f.PlayerGoodwill;
                    Faction.OfPlayer.TryAffectGoodwillWith(f, 20, canSendMessage: false, canSendHostilityLetter: false, reason: LWHistoryDefOf.LW_TookEnemyTown);
                    // The same lines vanilla's letter for a defeated town has.
                    lines.AppendLine().AppendLine().Append("RelationsWith".Translate(f.Name).Resolve() + ": " + (f.PlayerGoodwill - goodwill).ToStringWithSign());
                    f.TryAppendRelationKindChangedInfo(lines, kind, f.PlayerRelationKind);
                }
                r.dealings = lines.ToString();
            }
            if (Part(r, PartTale))
                foreach (Pawn p in r.raiders)
                    if (p != null && !p.Dead && !p.Destroyed && p.Faction == Faction.OfPlayer)
                    {
                        TaleRecorder.RecordTale(TaleDefOf.CaravanAssaultSuccessful, p);
                        break;
                    }
            if (Part(r, PartDeed))
            {
                ChronicleEntry e = PlayerDeeds.Notify_SettlementTakenByPlayer(s, old, r.townName, s.Tile, r.resubmit);
                if (e != null) r.chronicleId = e.id;
            }
            r.raiders.Clear();
        }

        /// <summary>True once per part: the bit is set before the work, so an effect that threw is never run a second time.</summary>
        private static bool Part(TributaryRecord r, int bit)
        {
            if ((r.deedParts & bit) != 0) return false;
            r.deedParts |= bit;
            return true;
        }

        /// <summary>The old masters want the town back: a war between them and the client, unless they are gone or at peace with it (or one is already on record).</summary>
        private static void StageWar(LivingWorldComponent comp, TributaryRecord r, Faction old, Faction client)
        {
            if (r.resubmit || old == null || old.defeated || !old.HostileTo(client)) return;
            War w = Diplomacy.ActiveWar(comp, client, old) ?? Diplomacy.StartWar(comp, client, old, "LW_WarCause_Reconquest".Translate(r.townName).Resolve(), ancient: false, announce: false);
            if (w.chronicleId < 0) w.chronicleId = r.chronicleId;
            r.reconquestWarId = w.id;
        }

        private static void StageCommit(LivingWorldComponent comp, TributaryRecord r, Settlement s, Faction old, Faction client)
        {
            r.state = TributaryState.Held;
            r.heldSinceTicks = SimClock.Now;
            bool ally = client.RelationKindWith(Faction.OfPlayer) == FactionRelationKind.Ally;
            string who = client.leader?.Name?.ToStringFull ?? "LW_Hold_NoLeader".Translate().Resolve();
            string text = "LW_Hold_HeldText".Translate(r.townName.Named("TOWN"), who.Named("LEADER"), (ally ? "LW_Hold_RelationAlly" : "LW_Hold_RelationNeutral").Translate().Named("RELATION"));
            if (r.reconquestWarId >= 0 && old != null) text += " " + "LW_Hold_HeldWar".Translate(old.NameColored.Named("OLD"));
            if (Enabled) text += " " + "LW_Hold_HeldTribute".Translate(Tuning.T.tributeIntervalDays);
            if (old != null && old.defeated) text += "\n\n" + "LetterFactionBaseDefeated_FactionDestroyed".Translate(old.Name).Resolve();
            if (!r.dealings.NullOrEmpty()) text += r.dealings;
            r.dealings = null;
            Notify("LW_Hold_HeldLabel".Translate(r.townName), text, ally ? LetterDefOf.PositiveEvent : LetterDefOf.NeutralEvent, s);
            LWLog.Message($"tributary: {r.townName} is now the free town of {client.Name} ({(ally ? "allied" : "neutral")})");
        }

        // ---- Release -------------------------------------------------------------------------------------------

        /// <summary>The colony lets the town go: it keeps its people, its ruler and its faction, and stays a former client.</summary>
        public static void Release(TributaryRecord r)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (r == null || comp == null || r.state != TributaryState.Held) return;
            r.state = TributaryState.Released;
            r.endedTicks = SimClock.Now;
            Faction client = comp.FactionById(r.clientFactionId);
            client?.TryAffectGoodwillWith(Faction.OfPlayer, 15, canSendMessage: false, canSendHostilityLetter: false);
            var s = LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement;
            PayAccruedOnRelease(comp, r, s, client);
            ChronicleEventDef def = LWChronicleDefOf.LW_TributaryReleased;
            comp.chronicle.Record(def, def.text.Formatted(r.townName.Named("SETTLEMENT"), Faction.OfPlayer.Name.Named("COLONY"), (client?.Name ?? r.clientFactionName ?? "?").Named("FACTION")).Resolve(),
                Faction.OfPlayer, client, s, involvesPlayer: true, silent: true);
            Messages.Message("LW_Tributary_Released".Translate(r.townName), s != null ? new GlobalTargetInfo(s) : GlobalTargetInfo.Invalid, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        // ---- Silver taken and not yet paid ---------------------------------------------------------------------

        /// <summary>
        /// The colony lets a town go with silver taken from its client and not yet sent: it goes out as a last payment (the debt arose while the town was
        /// held, as for a convoy already on the road), and what makes no ten, or cannot be sent, goes back to the client.
        /// </summary>
        private static void PayAccruedOnRelease(LivingWorldComponent comp, TributaryRecord r, Settlement s, Faction client)
        {
            CompSettlementLife life = s?.GetComponent<CompSettlementLife>();
            int silver = Mathf.FloorToInt(r.accruedSilver / 10f) * 10;
            if (silver > 0 && client != null && s != null && !s.Destroyed && life != null && life.initialized && ColonyFor(s).map != null)
            {
                r.accruedSilver -= silver;
                LWLog.Message($"tributary: {r.townName} released, {silver} silver accrued since the last payment is sent as its last");
                Dispatch(r, s, life, client, silver, null, 0f, KnowHow.Effective(client));
            }
            ReturnAccrued(comp, r, s);
        }

        /// <summary>
        /// A record leaves Held with silver in <see cref="TributaryRecord.accruedSilver"/>, taken from its client's treasury and not paid: it goes to the town's
        /// owner now (the client after a revolt or a release, the new master when the town was lost, as a convoy that turns back does), or is logged lost when
        /// the town has no owner with a treasury. Nothing stays on an ended record, and what was owed is forgotten.
        /// </summary>
        private static void ReturnAccrued(LivingWorldComponent comp, TributaryRecord r, Settlement s)
        {
            float amount = r.accruedSilver;
            r.accruedSilver = r.arrears = r.shareDays = r.accrualDays = 0f;
            if (amount <= 0f) return;
            Faction owner = s != null && !s.Destroyed ? s.Faction : null;
            FactionState fs = owner != null ? comp.StateFor(owner) : null;
            if (fs == null)
            {
                LWLog.Message($"tributary: {amount:F0} silver accrued for {r.townName} is lost: the town has no owner to give it back to");
                return;
            }
            fs.treasury += amount;
            LWLog.Message($"tributary: {amount:F0} silver accrued for {r.townName} went back to {owner.Name}");
        }

        // ---- Tribute and loyalty -------------------------------------------------------------------------------

        private const int WarnGapDays = 10, GiftCooldownDays = 10;

        private static float At(List<float> list, TributeRate rate, float fallback) =>
            list == null || list.Count == 0 ? fallback : list[Mathf.Min((int)rate, list.Count - 1)];

        /// <summary>The share of the town's income the rate takes.</summary>
        public static float Share(TributeRate rate) => At(Tuning.T.tributeShare, rate, 0f);

        public static string RateLabel(TributeRate rate) => ("LW_TributeRate_" + rate).Translate().Resolve();

        public static TributaryRecord RecordById(int id)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || id < 0) return null;
            foreach (TributaryRecord r in comp.tributaries)
                if (r.id == id) return r;
            return null;
        }

        /// <summary>
        /// Tribute is demanded of a held town: gameplay on, the economy simulated (with it off the client earns nothing and unrest never moves, so tribute
        /// would only drain it), the town takes part in the simulation, a rate is set and there is a colony to pay. While it is not, nothing accrues, the due dates stand still and the unrest term is the suspended one.
        /// </summary>
        public static bool Demanded(TributaryRecord r, Settlement town, out Map colony)
        {
            colony = null;
            if (r == null || !Enabled || !LivingWorldMod.Settings.simulateEconomy || r.state != TributaryState.Held || town == null || town.Destroyed) return false;
            CompSettlementLife life = town.GetComponent<CompSettlementLife>();
            if (life == null || !life.Active || r.rate == TributeRate.Suspended) return false;
            colony = ColonyFor(town).map;
            return colony != null;
        }

        public static bool Demanded(TributaryRecord r, Settlement town) => Demanded(r, town, out _);

        /// <summary>The held town's term of the unrest target: its rate (suspended when nothing is demanded), defenders paid for, neglect, a road to the colony. Zero for any other town, and with gameplay off.</summary>
        public static float UnrestTerm(CompSettlementLife s)
        {
            if (!Enabled) return 0f;
            TributaryRecord r = HeldFor(s.Settlement);
            if (r == null) return 0f;
            LivingWorldTuningDef t = Tuning.T;
            bool demanded = Demanded(r, s.Settlement, out Map colony);
            float term = At(t.tributeUnrest, demanded ? r.rate : TributeRate.Suspended, 0f);
            int now = SimClock.Now;
            if (r.protectedUntilTick > now) term += t.tributaryProtectedTerm;
            if (r.abandonedUntilTick > now) term += t.tributaryAbandonedTerm;
            if (colony == null) colony = ColonyFor(s.Settlement).map;
            if (colony != null && Roads.Connected(colony, s.Settlement)) term += t.tributaryRoadTerm;
            return term;
        }

        /// <summary>The share of its target garrison a held town's faction pays for (a heavy hand costs defenders); 1 for any other town, and with gameplay off.</summary>
        public static float GarrisonFunding(CompSettlementLife s)
        {
            if (!Enabled) return 1f;
            TributaryRecord r = HeldFor(s.Settlement);
            if (r == null) return 1f;
            return At(Tuning.T.tributeGarrisonFunding, Demanded(r, s.Settlement) ? r.rate : TributeRate.Suspended, 1f);
        }

        // -- Rate and gifts --

        /// <summary>
        /// Why the rate cannot be changed now (null: it can): at most one change in each stretch of simulated days between payments. The days are the
        /// ones unrest and payments move in (real days times the pace), so a slow pace does not shorten the lock.
        /// </summary>
        public static string CannotSetRate(TributaryRecord r)
        {
            float left = Tuning.T.tributeIntervalDays - r.rateDays;
            if (left <= 0.0001f) return null;
            int wait = Mathf.CeilToInt(left / Mathf.Max(0.05f, LivingWorldMod.Settings.pace) * GenDate.TicksPerDay);
            return "LW_Tributary_RateLocked".Translate(wait.ToStringTicksToPeriod()).Resolve();
        }

        public static void SetRate(TributaryRecord r, TributeRate rate)
        {
            if (r == null || r.state != TributaryState.Held || r.rate == rate || !Enabled) return;
            string why = CannotSetRate(r);
            if (why != null)
            {
                Messages.Message(why, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            r.rate = rate;
            r.lastRateTick = SimClock.Now;
            r.rateDays = 0f;
        }

        public static int GiftPrice(CompSettlementLife life) => Mathf.Max(50, Mathf.RoundToInt(life.population * Tuning.T.tributaryGiftPerCapita / 50f) * 50);

        /// <summary>Why the colony cannot send this town a gift now (null: it can).</summary>
        public static string CannotGift(TributaryRecord r, Settlement s)
        {
            CompSettlementLife life = s?.GetComponent<CompSettlementLife>();
            if (r == null || r.state != TributaryState.Held || life == null || !life.initialized || !Enabled) return "LW_Tributary_NoLongerApplies".Translate().Resolve();
            Map colony = ColonyFor(s).map;
            if (colony == null) return "LW_Tributary_NoColony".Translate().Resolve();
            int wait = r.lastGiftTick + GiftCooldownDays * GenDate.TicksPerDay - SimClock.Now;
            if (wait > 0) return "LW_Tributary_GiftWait".Translate(wait.ToStringTicksToPeriod()).Resolve();
            int price = GiftPrice(life);
            return Demands.ColonySilver(colony) < price ? "LW_Tributary_GiftNoSilver".Translate(price).Resolve() : null;
        }

        /// <summary>The colony gives the town silver at once: it goes into the client's treasury and calms the people.</summary>
        public static void SendGift(TributaryRecord r)
        {
            var s = r == null ? null : LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement;
            string why = CannotGift(r, s);
            if (why != null)
            {
                Messages.Message(why, MessageTypeDefOf.RejectInput, historical: false);
                return;
            }
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            FactionState fs = comp.StateFor(comp.FactionById(r.clientFactionId));
            int price = GiftPrice(life);
            Demands.TakeColonySilver(ColonyFor(s).map, price);
            if (fs != null) fs.treasury += price;
            life.unrest = Mathf.Clamp01(life.unrest - Mathf.Min(0.2f, price / Mathf.Max(1f, life.population * 10f)));
            r.lastGiftTick = SimClock.Now;
            Messages.Message("LW_Tributary_GiftSent".Translate(price, r.townName), new GlobalTargetInfo(s), MessageTypeDefOf.PositiveEvent, historical: false);
        }

        // -- The day --

        public static void DailyStepSafe(LivingWorldComponent comp, float pace)
        {
            try
            {
                DailyStep(comp, pace);
            }
            catch (Exception e)
            {
                LWLog.ErrorOnce("tributary day: " + e, 0x4c570a34);
            }
        }

        /// <summary>One day of tribute for every held town: accrual, a payment when it falls due, the warning, the roll for a revolt. Gameplay only: with the setting off nothing happens.</summary>
        public static void DailyStep(LivingWorldComponent comp) => DailyStep(comp, LivingWorldMod.Settings.pace);

        /// <summary>A test passes the one record it is driving, so a town another case left held does not pay, warn or rise under it.</summary>
        internal static void DailyStep(LivingWorldComponent comp, float pace, TributaryRecord only = null)
        {
            if (!Enabled || comp.tributaries.Count == 0) return;
            for (int i = comp.tributaries.Count - 1; i >= 0; i--)
            {
                if (i >= comp.tributaries.Count) continue;
                TributaryRecord r = comp.tributaries[i];
                if (r.state != TributaryState.Held || (only != null && r != only)) continue;
                try
                {
                    DayOf(comp, r, pace);
                }
                catch (Exception e)
                {
                    LWLog.ErrorOnce($"tributary day of {r.townName}: {e}", 0x4c570a35);
                }
            }
        }

        private static void DayOf(LivingWorldComponent comp, TributaryRecord r, float pace)
        {
            r.rateDays = Mathf.Min(TributaryRecord.RateOpen, r.rateDays + pace);
            var s = LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement;
            Faction client = comp.FactionById(r.clientFactionId);
            CompSettlementLife life = s?.GetComponent<CompSettlementLife>();
            // A town that is not its client's any more is Reconcile's to end, not ours to bill.
            if (s == null || s.Destroyed || client == null || s.Faction != client || life == null || !life.Active) return;
            FactionState fs = comp.StateFor(client);
            if (Demanded(r, s))
            {
                Accrue(r, life, fs, pace);
                if (r.accrualDays >= Tuning.T.tributeIntervalDays - 0.001f) Pay(comp, r, s, life, client);
            }
            Warn(r, s, life);
            Revolt(comp, r, s, life, client, pace);
        }

        /// <summary>The day's tribute: its share of the day's income (the income the faction's own books use), taken from the client's treasury above the reserve.</summary>
        private static void Accrue(TributaryRecord r, CompSettlementLife life, FactionState fs, float pace)
        {
            LivingWorldTuningDef t = Tuning.T;
            float share = Share(r.rate);
            float due = share * life.population * t.taxPerCapitaPerDay * (0.4f + life.prosperity) * pace;
            float take = Mathf.Min(due, Mathf.Max(0f, (fs?.treasury ?? 0f) - t.tributeTreasuryReserve));
            if (fs != null) fs.treasury -= take;
            r.accruedSilver += take;
            r.arrears += due - take;
            r.shareDays += share * pace;
            r.accrualDays += pace;
        }

        /// <summary>
        /// The goods of a payment: of the commodities the town has a surplus of (above 1.2 times its target stock) that can be made into things, its
        /// specialty first, else the most valuable surplus (ties by name); at the period's average share of the surplus and worth at most the cap. The first
        /// that makes at least one item. False: silver only.
        /// </summary>
        internal static bool PickGoods(CompSettlementLife life, float avgShare, TechLevel tech, out CommodityDef goods, out TributeGoods.GoodsPlan plan, out float units)
        {
            goods = null;
            plan = default;
            units = 0f;
            if (avgShare <= 0f) return false;
            var candidates = new List<(CommodityDef c, float surplus)>();
            foreach (CommodityDef c in Commodities.All)
            {
                if (c.commissionGoods.NullOrEmpty() || c.silverPerUnit <= 0f) continue;
                float surplus = life.Stock(c) - 1.2f * life.TargetStock(c);
                if (surplus > 0f) candidates.Add((c, surplus));
            }
            candidates.Sort((a, b) =>
            {
                bool sa = a.c == life.specialty, sb = b.c == life.specialty;
                if (sa != sb) return sa ? -1 : 1;
                int byValue = (b.surplus * b.c.silverPerUnit).CompareTo(a.surplus * a.c.silverPerUnit);
                return byValue != 0 ? byValue : string.CompareOrdinal(a.c.defName, b.c.defName);
            });
            foreach ((CommodityDef c, float surplus) in candidates)
            {
                float want = Mathf.Min(avgShare * surplus, Tuning.T.tributeGoodsMaxValue / c.silverPerUnit);
                TributeGoods.GoodsPlan p = TributeGoods.Plan(c, want * c.silverPerUnit, tech);
                if (!p.Any) continue;
                goods = c;
                plan = p;
                units = Mathf.Min(surplus, p.madeValue / c.silverPerUnit);
                return true;
            }
            return false;
        }

        /// <summary>A payment falls due: the silver in tens, the goods out of the town's stock, a message when it fell short, then on its way.</summary>
        private static void Pay(LivingWorldComponent comp, TributaryRecord r, Settlement town, CompSettlementLife life, Faction client)
        {
            float avgShare = r.accrualDays > 0f ? r.shareDays / r.accrualDays : 0f;
            int silver = Mathf.FloorToInt(r.accruedSilver / 10f) * 10;
            bool fellShort = silver < 0.5f * (silver + r.arrears);
            TechLevel tech = KnowHow.Effective(client);
            bool anyGoods = PickGoods(life, avgShare, tech, out CommodityDef goods, out TributeGoods.GoodsPlan plan, out float units);
            if (anyGoods) life.stock[goods.index] = Mathf.Max(0f, life.stock[goods.index] - units);
            r.accruedSilver -= silver; // what does not make a ten stays accrued
            if (fellShort)
            {
                r.missed++;
                Messages.Message("LW_Tribute_Short".Translate(r.townName), new GlobalTargetInfo(town), MessageTypeDefOf.NegativeEvent, historical: false);
            }
            r.accrualDays = r.shareDays = r.arrears = 0f;
            if (silver <= 0 && !anyGoods) return;
            Dispatch(r, town, life, client, silver, anyGoods ? goods : null, units, tech);
        }

        /// <summary>The payment goes to the nearest colony: by a convoy when it can be reached on the map and there is room for one, else at once by drop pods.</summary>
        private static void Dispatch(TributaryRecord r, Settlement town, CompSettlementLife life, Faction client, int silver, CommodityDef goods, float units, TechLevel tech)
        {
            (Map map, bool reachable) = ColonyFor(town);
            if (map == null)
            {
                // No colony to pay: what was taken is owed again.
                r.accruedSilver += silver;
                if (goods != null) life.stock[goods.index] += units;
                return;
            }
            var cargo = new float[Commodities.Count];
            if (goods != null) cargo[goods.index] = units;
            LivingWorldSettings settings = LivingWorldMod.Settings;
            if (reachable && settings.worldParties && !TestNoParty && WorldParty.All.Count < settings.maxParties)
            {
                WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_Tribute, client, town, map.Parent);
                p.silver = silver;
                p.cargo = cargo;
                p.strength = 20f;
                p.recordId = r.id;
                p.mission = (byte)tech; // the goods are made at delivery from the tech they were planned with
                r.convoyPartyId = p.ID;
                return;
            }
            SettlePayment(r, town, client, silver, cargo, map, viaPods: true, tech);
        }

        /// <summary>
        /// A payment reaches the colony, from a convoy or from the pods that stand in for it: the silver and the goods made from the commodity units, put
        /// down near the colony's drop spot (what cannot be placed comes by pod), and counted on the record. The goods are made with the tech level they were
        /// planned with at dispatch, so what the town was debited is what arrives.
        /// </summary>
        internal static void SettlePayment(TributaryRecord r, Settlement town, Faction client, int silver, float[] cargoUnits, Map map, bool viaPods, TechLevel? plannedTech = null)
        {
            if (map == null) return;
            TechLevel tech = plannedTech ?? (client != null ? KnowHow.Effective(client) : TechLevel.Neolithic);
            var things = new List<Thing>();
            float made = 0f;
            for (int left = silver; left > 0;)
            {
                Thing coins = ThingMaker.MakeThing(ThingDefOf.Silver);
                coins.stackCount = Mathf.Min(left, coins.def.stackLimit);
                left -= coins.stackCount;
                things.Add(coins);
            }
            string goodsText = null;
            if (cargoUnits != null)
                foreach (CommodityDef c in Commodities.All)
                {
                    if (c.index >= cargoUnits.Length || cargoUnits[c.index] <= 0.0001f) continue;
                    TributeGoods.GoodsPlan plan = TributeGoods.Plan(c, cargoUnits[c.index] * c.silverPerUnit, tech);
                    if (!plan.Any) continue;
                    things.AddRange(TributeGoods.Make(plan));
                    made += plan.madeValue;
                    goodsText = (goodsText == null ? "" : goodsText + ", ") + plan.count + " " + plan.def.label;
                }
            if (things.Count == 0) return;
            IntVec3 spot = DropCellFinder.TradeDropSpot(map);
            var byPod = viaPods ? things : new List<Thing>();
            var placed = new List<Thing>();
            float delivered = 0f; // what already stands on the map when a pod fails to land
            if (!viaPods)
                foreach (Thing t in things)
                {
                    float worth = t.def == ThingDefOf.Silver ? t.stackCount : t.MarketValue * t.stackCount;
                    if (!TestPlaceFails && GenPlace.TryPlaceThing(t, spot, map, ThingPlaceMode.Near))
                    {
                        delivered += worth;
                        if (t.Spawned) placed.Add(t); // silver that joined a stack is gone
                    }
                    else byPod.Add(t);
                }
            if (byPod.Count > 0)
                try
                {
                    DropPodUtility.DropThingsNear(spot, map, byPod, forbid: false, faction: client);
                }
                catch (Exception e)
                {
                    foreach (Thing t in byPod)
                        if (!t.Destroyed) t.Destroy();
                    LWLog.Error($"tribute of {town?.Label} {(delivered > 0f ? "partly lost" : "lost")}: its drop pods could not land ({silver} silver and {made:F0} in goods, {delivered:F0} of it already set down): {e}");
                    if (delivered <= 0f) return;
                    // What was set down stays on the map: it counts as the payment.
                    if (r != null)
                    {
                        r.paidTotal += delivered;
                        r.payments++;
                    }
                    return;
                }
            if (r != null)
            {
                r.paidTotal += made + silver;
                r.payments++;
            }
            string what = silver > 0 && goodsText != null ? "LW_Tribute_SilverAndGoods".Translate(silver, goodsText).Resolve() : silver > 0 ? "LW_Tribute_Silver".Translate(silver).Resolve() : goodsText;
            Messages.Message("LW_Tribute_Arrived".Translate(town?.Label ?? r?.townName ?? "?", what), placed.Count > 0 ? new LookTargets(placed) : new LookTargets(map.Parent), MessageTypeDefOf.PositiveEvent, historical: false);
        }

        /// <summary>A rough look at the next payment: what has accrued plus the days left at today's rate, and the goods the surplus would give. For the lines the colony reads.</summary>
        public static int EstimateNextPayment(TributaryRecord r, out CommodityDef goods, out float units)
        {
            goods = null;
            units = 0f;
            var s = r == null ? null : LivingWorldComponent.WorldObjectById(r.settlementId) as Settlement;
            CompSettlementLife life = s?.GetComponent<CompSettlementLife>();
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction client = comp?.FactionById(r.clientFactionId);
            if (life == null || client == null || !Demanded(r, s)) return 0;
            float pace = LivingWorldMod.Settings.pace;
            LivingWorldTuningDef t = Tuning.T;
            int daysLeft = DaysToPayment(r);
            float share = Share(r.rate);
            // The client pays only what its treasury holds above the reserve, as Accrue takes it.
            float comingDue = daysLeft * share * life.population * t.taxPerCapitaPerDay * (0.4f + life.prosperity) * pace;
            float silver = r.accruedSilver + Mathf.Min(comingDue, Mathf.Max(0f, (comp.StateFor(client)?.treasury ?? 0f) - t.tributeTreasuryReserve));
            float days = r.accrualDays + daysLeft * pace;
            float avg = days <= 0f ? share : (r.shareDays + daysLeft * share * pace) / days;
            if (PickGoods(life, avg, KnowHow.Effective(client), out goods, out TributeGoods.GoodsPlan plan, out units)) units = plan.madeValue / goods.silverPerUnit;
            return Mathf.FloorToInt(silver / 10f) * 10;
        }

        /// <summary>Days until the next payment at the current pace (counted in days of tribute, so a day it is not demanded does not count).</summary>
        public static int DaysToPayment(TributaryRecord r) =>
            Mathf.Max(0, Mathf.CeilToInt((Tuning.T.tributeIntervalDays - r.accrualDays) / Mathf.Max(0.05f, LivingWorldMod.Settings.pace)));

        // -- Unrest, revolt --

        private static void Warn(TributaryRecord r, Settlement s, CompSettlementLife life)
        {
            if (life.unrest < Tuning.T.tributaryWarnUnrest || SimClock.Now - r.lastWarningTick < WarnGapDays * GenDate.TicksPerDay) return;
            r.lastWarningTick = SimClock.Now;
            Messages.Message("LW_Tributary_Restless".Translate(r.townName, life.unrest.ToStringPercent()), new GlobalTargetInfo(s), MessageTypeDefOf.NegativeEvent, historical: false);
        }

        /// <summary>
        /// The people rise: a daily chance that grows with their unrest above the line. Only a town its client owns and nothing ties down can rise (no roll
        /// otherwise). Its old masters take it back, if they live and fight the client and conquest is allowed; else it breaks free of the colony.
        /// </summary>
        private static void Revolt(LivingWorldComponent comp, TributaryRecord r, Settlement s, CompSettlementLife life, Faction client, float pace)
        {
            LivingWorldTuningDef t = Tuning.T;
            if (s.Faction != client || SettlementOps.Protected(s)) return;
            float hazard = t.tributaryRevoltHazard * Mathf.Max(0f, life.unrest - t.tributaryRevoltUnrest) * pace;
            if (!(TestRevolt ?? (hazard > 0f && Rand.Chance(hazard)))) return;
            Faction old = comp.FactionById(r.oldFactionId);
            bool back = old != null && !old.defeated && !old.def.permanentEnemy && FactionState.IsSimulated(old) && old.HostileTo(client) && SettlementOps.CanFall(s);
            if (back && (TestLiberate ?? Rand.Chance(t.tributaryLiberateShare))) Liberated(comp, r, s, life, client, old);
            else BreakFree(comp, r, s, client);
        }

        /// <summary>The founders have the town back (and are not its occupiers): its client loses its only town and is defeated, and the record is lost.</summary>
        private static void Liberated(LivingWorldComponent comp, TributaryRecord r, Settlement s, CompSettlementLife life, Faction client, Faction old)
        {
            life.originalFactionId = old.loadID;
            Rebellion.Liberate(comp, life, client, old);
            End(comp, r, TributaryState.Lost, s);
        }

        /// <summary>The town turns on the colony: the client is hostile to it, which the vanilla letter says; a quest lock that forbids it means no revolt today.</summary>
        private static void BreakFree(LivingWorldComponent comp, TributaryRecord r, Settlement s, Faction client)
        {
            Faction player = Faction.OfPlayer;
            player.TryAffectGoodwillWith(client, client.GoodwillToMakeHostile(player), canSendMessage: false, canSendHostilityLetter: false);
            if (!client.HostileTo(player)) return;
            string town = r.townName;
            ChronicleEventDef def = LWChronicleDefOf.LW_TributaryRevolted;
            comp.chronicle.Record(def, def.text.Formatted(town.Named("SETTLEMENT"), player.Name.Named("COLONY"), client.Name.Named("FACTION")).Resolve(), client, player, s, involvesPlayer: true, silent: true);
            Diplomacy.AddGrudge(comp.StateFor(client), player, 30f, "LW_Grudge_TributaryRevolt".Translate(town).Resolve());
            End(comp, r, TributaryState.Revolted, s, announce: false);
            Notify("LW_Tributary_RevoltedLabel".Translate(town), "LW_Tributary_RevoltedText".Translate(town.Named("TOWN"), client.NameColored.Named("FACTION")), LetterDefOf.NegativeEvent, s);
        }

        // -- Convoys on the road --

        /// <summary>The convoys of a record that are still on their way out.</summary>
        private static List<WorldParty> ConvoysOf(TributaryRecord r)
        {
            var list = new List<WorldParty>();
            foreach (WorldParty p in WorldParty.All)
                if (p.kind == LWPartyDefOf.LW_Tribute && p.recordId == r.id && !p.Destroyed && p.phase == PartyPhase.Outbound) list.Add(p);
            return list;
        }

        /// <summary>The town is no friend of the colony any more: what it was sending turns back.</summary>
        private static void RecallConvoys(TributaryRecord r)
        {
            foreach (WorldParty p in ConvoysOf(r)) TurnBackConvoy(p);
        }

        internal static void TurnBackConvoy(WorldParty p)
        {
            if (p.Origin == null) LWLog.Message($"tribute of {p.originName} lost: its town is gone ({Mathf.RoundToInt(p.CargoValue)} in value)");
            p.TurnBack();
        }

        /// <summary>The save is being prepared for removal: tribute on the road is set down at the colony first (it was already paid), then the parties go.</summary>
        internal static void Notify_PartiesLeaving()
        {
            foreach (WorldParty p in WorldParty.All.ToArray())
            {
                if (p.kind != LWPartyDefOf.LW_Tribute || p.Destroyed) continue;
                try
                {
                    // On the way home what it carries goes back to whoever holds the town, as it would have on arrival.
                    if (p.phase == PartyPhase.Returning) p.kind.Worker.ArrivedHome(p);
                    else PartyWorker_Tribute.Deliver(p, viaPods: false);
                    p.silver = 0f;
                    System.Array.Clear(p.cargo, 0, p.cargo.Length);
                }
                catch (Exception e)
                {
                    LWLog.Error("tribute on the road could not be settled before the save was prepared: " + e);
                }
            }
        }

        // ---- Player-facing lines -------------------------------------------------------------------------------

        /// <summary>The inspect line of a town the colony yielded or holds (null for any other).</summary>
        public static string InspectLine(Settlement s)
        {
            TributaryRecord r = RecordFor(s.ID);
            if (r == null) return null;
            if (r.state == TributaryState.Yielded) return "LW_Inspect_Yielded".Translate(ColonyLabel(s)).Resolve();
            if (!Enabled) return "LW_Inspect_Held".Translate(ColonyLabel(s)).Resolve();
            if (!Demanded(r, s)) return "LW_Inspect_HeldNoTribute".Translate(ColonyLabel(s)).Resolve();
            return "LW_Inspect_HeldTribute".Translate(ColonyLabel(s), RateLabel(r.rate), CountLabel.Days(DaysToPayment(r))).Resolve();
        }

        /// <summary>The factions page's line for a client: a free town under the colony, or what it was.</summary>
        public static string FactionLine(Faction f)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || f == null || comp.tributaries.Count == 0) return null;
            TributaryRecord ended = null;
            foreach (TributaryRecord r in comp.tributaries)
            {
                if (r.clientFactionId != f.loadID) continue;
                if (r.state == TributaryState.Held) return "LW_Faction_FreeTown".Translate(r.townName).Resolve();
                if (r.state != TributaryState.Yielded && r.state != TributaryState.Refused) ended = r;
            }
            return ended == null ? null : "LW_Faction_FormerClient".Translate().Resolve();
        }

        /// <summary>A letter while the subsystem is on; a message when it is off (the colony's choice already made still plays out quietly).</summary>
        private static void Notify(TaggedString label, TaggedString text, LetterDef def, WorldObject target)
        {
            GlobalTargetInfo look = target != null && !target.Destroyed ? new GlobalTargetInfo(target) : GlobalTargetInfo.Invalid;
            if (Enabled) Find.LetterStack.ReceiveLetter(label, text, def, look);
            else Messages.Message(text, look, MessageTypeDefOf.NeutralEvent, historical: false);
        }

        /// <summary>Gizmos of a held town: the rate of tribute, a gift, release.</summary>
        internal static IEnumerable<Gizmo> Gizmos(Settlement s)
        {
            TributaryRecord r = Enabled ? HeldFor(s) : null;
            if (r == null) yield break;
            bool colony = ColonyFor(s).map != null;
            var rate = new Command_Action
            {
                defaultLabel = "LW_Tributary_RateButton".Translate(RateLabel(r.rate).CapitalizeFirst()),
                defaultDesc = "LW_Tributary_RateDesc".Translate(s.Label, Tuning.T.tributeIntervalDays),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Trade"),
                action = () => Find.WindowStack.Add(new FloatMenu(RateOptions(r))),
            };
            string locked = CannotSetRate(r);
            if (!colony) rate.Disable("LW_Tributary_NoColony".Translate());
            else if (locked != null) rate.Disable(locked);
            yield return rate;
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            if (life != null && life.initialized)
            {
                var gift = new Command_Action
                {
                    defaultLabel = "LW_Tributary_GiftButton".Translate(GiftPrice(life)),
                    defaultDesc = "LW_Tributary_GiftDesc".Translate(s.Label, GiftPrice(life), GiftCooldownDays),
                    icon = TexCommand.GatherSpotActive,
                    action = () => SendGift(HeldFor(s)),
                };
                string why = CannotGift(r, s);
                if (why != null) gift.Disable(why);
                yield return gift;
            }
            yield return new Command_Action
            {
                defaultLabel = "LW_Tributary_ReleaseButton".Translate(),
                defaultDesc = "LW_Tributary_ReleaseDesc".Translate(s.Label),
                icon = TexCommand.ClearPrioritizedWork,
                action = () => Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("LW_Tributary_ReleaseConfirm".Translate(s.Label), () => Release(HeldFor(s)), destructive: true)),
            };
        }

        /// <summary>The four rates, each with what it takes and what it costs in the people's patience and the town's defenders.</summary>
        private static List<FloatMenuOption> RateOptions(TributaryRecord r)
        {
            LivingWorldTuningDef t = Tuning.T;
            var options = new List<FloatMenuOption>();
            foreach (TributeRate rate in new[] { TributeRate.Suspended, TributeRate.Light, TributeRate.Normal, TributeRate.Heavy })
            {
                TributeRate chosen = rate;
                string label = "LW_Tributary_RateOption".Translate(RateLabel(rate).CapitalizeFirst(), Share(rate).ToStringPercent(), At(t.tributeUnrest, rate, 0f).ToStringPercent(), At(t.tributeGarrisonFunding, rate, 1f).ToStringPercent()).Resolve();
                if (rate == r.rate) label += " " + "LW_Tributary_RateCurrent".Translate();
                options.Add(new FloatMenuOption(label, () => SetRate(r, chosen)));
            }
            return options;
        }
    }

    /// <summary>Hook: a town's map is about to be removed: the colony's assault on a yielded town is judged now (the pawns are still on the map).</summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.Notify_MyMapAboutToBeRemoved))]
    public static class Patch_MapParent_Notify_MyMapAboutToBeRemoved_Tributaries
    {
        public static void Prefix(MapParent __instance)
        {
            try
            {
                if (__instance is Settlement s) Tributaries.Notify_MapAboutToBeRemoved(s);
            }
            catch (Exception e)
            {
                // The snapshot stays Pending: the transfer reads that as "driven out".
                LWLog.Error("tributary snapshot: " + e);
            }
        }
    }

    /// <summary>
    /// A tribute convoy: the client's payment walking from its town to the colony. At the colony it is set down; with the colony gone it looks for
    /// another, and when none can be reached it is paid by pod or turns back (see <see cref="Tributaries"/>).
    /// </summary>
    public class PartyWorker_Tribute : PartyWorker
    {
        public override bool ArrivedAtDestination(WorldParty p)
        {
            TributaryRecord r = Tributaries.RecordById(p.recordId);
            Faction client = LivingWorldComponent.Instance?.FactionById(r?.clientFactionId ?? -1);
            // A town that turned on the colony, or is another's now, does not pay it: the convoy goes home.
            if (r != null && (r.state == TributaryState.Revolted || r.state == TributaryState.Lost || client == null || (p.Origin != null && p.Origin.Faction != client)))
            {
                Tributaries.TurnBackConvoy(p);
                return false;
            }
            if ((p.Destination as MapParent)?.Map is Map map && map.IsPlayerHome)
            {
                Deliver(p, map, viaPods: false);
                return true;
            }
            return Retarget(p);
        }

        public override void DestinationLost(WorldParty p)
        {
            if (Retarget(p)) p.Disband();
        }

        /// <summary>Another colony to go to, else pods, else back home. True: the payment was made (nothing left to carry).</summary>
        private static bool Retarget(WorldParty p)
        {
            Settlement origin = p.Origin;
            if (origin == null)
            {
                Tributaries.TurnBackConvoy(p);
                return false;
            }
            (Map map, bool reachable) = Tributaries.ColonyFor(origin);
            if (map == null)
            {
                Tributaries.TurnBackConvoy(p);
                return false;
            }
            if (reachable && map.Parent != null && map.Parent.ID != p.destinationId)
            {
                p.Retarget(map.Parent);
                return false;
            }
            Deliver(p, map, viaPods: true);
            return true;
        }

        /// <summary>The payment goes to the colony the party was heading for (any colony when that one is gone): at once, as the save is prepared for removal.</summary>
        internal static void Deliver(WorldParty p, bool viaPods)
        {
            Settlement origin = p.Origin;
            Map map = (p.Destination as MapParent)?.Map;
            if (map == null || !map.IsPlayerHome) map = origin != null ? Tributaries.ColonyFor(origin).map : Neighbourhood.Home;
            if (map == null)
            {
                LWLog.Message($"tribute of {p.originName} lost: no colony to deliver to ({Mathf.RoundToInt(p.CargoValue)} in value)");
                return;
            }
            Deliver(p, map, viaPods);
        }

        private static void Deliver(WorldParty p, Map map, bool viaPods)
        {
            Tributaries.SettlePayment(Tributaries.RecordById(p.recordId), p.Origin, p.Faction, Mathf.RoundToInt(p.silver), p.cargo, map, viaPods, p.mission == 0 ? (TechLevel?)null : (TechLevel)p.mission);
            p.silver = 0f;
            System.Array.Clear(p.cargo, 0, p.cargo.Length);
        }

        /// <summary>Turned back (the colony is gone, or the town is no friend of it any more): what it carries is owed again if the town still is its client's, else it goes to whoever owns the town.</summary>
        public override void ArrivedHome(WorldParty p)
        {
            Settlement home = p.Origin;
            CompSettlementLife life = home?.GetComponent<CompSettlementLife>();
            if (home == null || life == null || !life.initialized)
            {
                LWLog.Message($"tribute of {p.originName} lost: its town is gone ({Mathf.RoundToInt(p.CargoValue)} in value)");
                return;
            }
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            TributaryRecord r = Tributaries.RecordById(p.recordId);
            Faction client = comp.FactionById(r?.clientFactionId ?? -1);
            if (r != null && r.state == TributaryState.Held && client != null && home.Faction == client) r.accruedSilver += p.silver;
            else if (comp.StateFor(home.Faction) is FactionState owner) owner.treasury += p.silver;
            for (int i = 0; i < p.cargo.Length && i < life.stock.Length; i++) life.stock[i] += p.cargo[i];
        }

        /// <summary>The way home is gone (the town vanished, or no path leads to it): what it carried is lost, and the log says so.</summary>
        public override void ReturnFailed(WorldParty p) =>
            LWLog.Message($"tribute of {p.originName} lost: it could not get home ({Mathf.RoundToInt(p.CargoValue)} in value)");

        public override string InspectExtra(WorldParty p)
        {
            var sb = new System.Text.StringBuilder();
            foreach (CommodityDef c in Commodities.All)
                if (c.index < p.cargo.Length && p.cargo[c.index] > 0.5f)
                {
                    if (sb.Length > 0) sb.Append(", ");
                    sb.Append(c.label);
                }
            string colony = (p.Destination as MapParent)?.Label ?? Faction.OfPlayer.Name;
            string line = (p.phase == PartyPhase.Returning ? "LW_Party_TributeHome" : "LW_Party_TributeFor").Translate(p.phase == PartyPhase.Returning ? p.Origin?.Label ?? p.originName ?? colony : colony, Mathf.RoundToInt(p.silver)).Resolve();
            return sb.Length == 0 ? line : line + ", " + sb;
        }
    }
}
