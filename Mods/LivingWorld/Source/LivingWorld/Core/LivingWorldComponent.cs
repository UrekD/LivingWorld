using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Root of the world simulation, saved with the world. Owns faction states, the chronicle and the id counter, and
    /// runs the systems on the sim clock (hourly tick, daily step). Settlement state lives in the settlements' comps;
    /// the lists here are derived indexes rebuilt each day.
    /// </summary>
    public class LivingWorldComponent : WorldComponent
    {
        public const int Schema = 1;

        public static LivingWorldComponent Instance { get; private set; }

        private int schemaVersion = Schema;
        private int nextId = 1;
        public List<FactionState> factionStates = new List<FactionState>();
        public ChronicleLog chronicle = new ChronicleLog();
        /// <summary>Groups on player maps and where they came from (see <see cref="Bridge"/>).</summary>
        public List<IncidentContext> contexts = new List<IncidentContext>();
        public List<War> wars = new List<War>();
        public List<PairValue> tradeLinks = new List<PairValue>();
        /// <summary>Roads: the edge ledger, NPC routes, trade traffic and backoff, joined towns (empty in a pre-roads save).</summary>
        public RoadState roadState = new RoadState();
        public bool warsSeeded;
        /// <summary>Settlements per faction when the simulation began: the yardstick for expansion.</summary>
        public float initialAvgSettlements;
        public List<Figure> figures = new List<Figure>();
        public List<PendingRevenge> revenge = new List<PendingRevenge>();
        public List<AidRequest> aidRequests = new List<AidRequest>();
        public List<PawnRoot> roots = new List<PawnRoot>();
        public int rebellions;
        public int lastRebellionTick = int.MinValue / 2;
        /// <summary>Renewal: the last arrival of a new people, and how many peoples the world started with.</summary>
        public int lastRenewalTick = int.MinValue / 2;
        public int initialFactionCount;
        public List<PendingRefugees> refugees = new List<PendingRefugees>();
        public List<WarRequest> warRequests = new List<WarRequest>();
        public List<PrisonerRecord> prisoners = new List<PrisonerRecord>();
        public List<Deed> deeds = new List<Deed>();
        /// <summary>Conquered towns the colony holds or held (Story/Tributaries.cs), the factions made for them (by loadID), and how many were ever made.</summary>
        public List<TributaryRecord> tributaries = new List<TributaryRecord>();
        public List<int> clientFactionIds = new List<int>();
        public int clientFactionsCreated;
        /// <summary>The colony's contracts with factions to send a war band (Story/HiredBands.cs).</summary>
        public List<HireContract> hires = new List<HireContract>();
        public int lastWarRequestTick = FactionState.Never, lastTributeTick = FactionState.Never, lastNeighbourVisitTick = FactionState.Never;
        /// <summary>"Prepare this save for removing the mod" was used: the simulation is off in this save.</summary>
        public bool preparedForRemoval;
        /// <summary>Faction loadID → tick until which its raids go elsewhere (tribute paid).</summary>
        private Dictionary<int, int> tributeTruces = new Dictionary<int, int>();
        /// <summary>Settlement id → tick a blood feud of that town with the colony last ended (any way): Feuds cooldown.</summary>
        public Dictionary<int, int> feudEnded = new Dictionary<int, int>();

        public bool TributeTruce(Faction f) => f != null && tributeTruces.TryGetValue(f.loadID, out int until) && Find.TickManager.TicksGame < until;

        public void SetTributeTruce(Faction f, int until)
        {
            if (f != null) tributeTruces[f.loadID] = until;
        }
        public int lastRefugeesTick = FactionState.Never;
        public int lastAidRequestTick = FactionState.Never;
        public int lastDailyTick = -1;
        public bool historyDone;

        // Derived indexes (not saved).
        private readonly Dictionary<int, FactionState> stateById = new Dictionary<int, FactionState>();
        public readonly List<CompSettlementLife> activeSettlements = new List<CompSettlementLife>();
        public readonly SpatialIndex spatial = new SpatialIndex();
        private static readonly Dictionary<int, WorldObject> objectById = new Dictionary<int, WorldObject>();
        private static int objectIndexTick = -1;
        private static World objectIndexWorld;
        private static int objectIndexCount = -1;
        private static bool objectIndexDirty;

        /// <summary>Wall-clock cost of the last daily step (ms), for the autotest's performance budget.</summary>
        public double lastDailyMs;

        public LivingWorldComponent(World world) : base(world)
        {
            Instance = this;
            // A new world (new game or load): nothing from the previous one may leak through statics.
            WorldParty.All.Clear();
            Bridge.PreferredSource.Clear();
            Bridge.PreferredTradeSource.Clear();
            Bridge.PreferredVisitSource.Clear();
            Bridge.PendingVisitOrigin.Clear();
            Diplomacy.ResetStatics();
            Figures.ResetCache();
            Neighbourhood.Reset();
            Territory.Reset();
            WorldOverlay.Reset();
            Roads.Reset();
            TradePlanner.Reset();
            Expansion.ResetStatics();
            Tributaries.ResetStatics();
            HistoryTotals.ResetStatics();
            KnowHow.ResetStatics();
            Feuds.Hunters.Clear();
            objectIndexTick = -1;
            objectIndexDirty = true;
        }

        public int NextId() => nextId++;

        public override void FinalizeInit(bool fromLoad)
        {
            Instance = this;
            WorldOverlay.ForgetWarSet();
            PerfLog.ResetClock();
            if (fromLoad) Testing.AutoTest.Notify_Loaded();
            // Borders need resolved references and the settlements' sim state: build them once the load or generation is over.
            Territory.ScheduleInitial(this, world);
        }

        [Unsaved] private bool indexesBuilt;

        private static readonly bool testNoSim = System.Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") == "1";

        public override void WorldComponentTick()
        {
            if (testNoSim || preparedForRemoval) return;
            int ticks = Find.TickManager.TicksGame;
            if (!indexesBuilt)
            {
                // Derived lists are empty after a load until rebuilt.
                indexesBuilt = true;
                RebuildIndexes(seedMissing: false);
                Feuds.RebuildHunters(this); // not saved: avengers on a map with their nemesis hunt again after a load
            }
            if (!historyDone) StartHistory(ticks);
            Roads.FlushRedraw(); // before the hourly early return: a burst of road changes costs one rebuild of the roads layer
            if (ticks % 250 == 0) Tributaries.TickSafe(this); // yielded towns whose maps are gone, and the held ones' identity
            if (ticks % GenDate.TicksPerHour != 7) return;
            if (Territory.PlayerHomesChanged()) Territory.Rebuild(this); // a colony landed, left or moved
            if (!SimClock.InHistory) VisitorStories.HourlyTick(this);
            if (lastDailyTick < 0 || ticks - lastDailyTick >= GenDate.TicksPerDay)
            {
                lastDailyTick = ticks;
                DailyStep();
            }
        }

        /// <summary>A new game: generate the world's past first (an old save gets none).</summary>
        private void StartHistory(int ticks)
        {
            historyDone = true;
            int years = LivingWorldMod.Settings.historyYears;
            if (years <= 0 || ticks > GenDate.TicksPerDay || lastDailyTick >= 0) return;
            if (Testing.AutoTest.Active) WorldHistory.Generate(this, years);
            else LongEventHandler.QueueLongEvent(() => WorldHistory.Generate(this, years), "LW_GeneratingHistory", doAsynchronously: false, null);
        }

        /// <summary>One simulated day for the whole world.</summary>
        [Unsaved] private readonly double[] sectionMs = new double[10];
        private static readonly string[] SectionNames = { "index", "territory", "economy", "trade", "expand", "shocks", "diplomacy", "military", "rest", "roads" };

        private void Mark(Stopwatch sw, int section, ref double last)
        {
            double now = sw.Elapsed.TotalMilliseconds;
            sectionMs[section] = now - last;
            last = now;
        }

        public void DailyStep()
        {
            var sw = Stopwatch.StartNew();
            double last = 0;
            LivingWorldSettings settings = LivingWorldMod.Settings;
            float pace = settings.pace;
            RebuildIndexes(seedMissing: true);
            Tributaries.ReconcileSafe(this); // identity first, before anything economic or military acts on a town
            if (initialAvgSettlements <= 0f) initialAvgSettlements = Mathf.Max(3f, AverageSettlementsPerFaction());
            Mark(sw, 0, ref last);
            Territory.Rebuild(this); // before anything that reads territory
            Mark(sw, 1, ref last);
            if (settings.simulateEconomy)
            {
                foreach (CompSettlementLife s in activeSettlements)
                {
                    FactionState fs = StateFor(s.Settlement.Faction);
                    int tierChange = SettlementEconomy.DailyStep(s, fs, pace, AtWar(fs));
                    Narrative.SettlementDay(s, tierChange);
                }
                foreach (FactionState fs in factionStates)
                    if (fs.faction != null) FactionEconomy.DailyStep(fs, pace);
                KnowHow.DailyStep(this, pace);
                Mark(sw, 2, ref last);
                Trade.DailyStep(this, pace);
                Mark(sw, 3, ref last);
                Expansion.DailyStep(this, pace);
                Mark(sw, 4, ref last);
                Shocks.DailyStep(this, pace);
                Mark(sw, 5, ref last);
            }
            Roads.DailyStep(this, pace);
            Mark(sw, 9, ref last);
            Diplomacy.DailyStep(this, pace);
            Mark(sw, 6, ref last);
            Military.DailyStep(this, pace);
            Mark(sw, 7, ref last);
            Rebellion.DailyStep(this, pace);
            if (!SimClock.InHistory) Tributaries.DailyStepSafe(this, pace); // tribute accrues and is paid, a held town may rise (game time only)
            if (!SimClock.InHistory) WorldOverlay.Notify_DayDone(this);
            PruneWars();
            // By index: a contested succession can create a rebel faction (and its state) during this loop.
            for (int i = 0; i < factionStates.Count; i++)
                if (factionStates[i].faction != null) Narrative.FactionDay(factionStates[i]);
            KnowHow.ProcessSplits(this);
            Renewal.DailyStep(this);
            if (!SimClock.InHistory)
            {
                Revenge.DailyCheck(this);
                Feuds.DailyCheck(this);
                Careers.DailyCheck(this);
                AidRequests.DailyCheck(this);
                Refugees.DailyCheck(this);
                Demands.DailyCheck(this);
                Prisoners.DailyCheck(this);
                Commissions.DailyCheck(this);
                Neighbours.DailyCheck(this);
                PruneContexts();
                HiredBands.Prune(this);
                chronicle.DigestTick();
            }
            Mark(sw, 8, ref last);
            lastDailyMs = sw.Elapsed.TotalMilliseconds;
            if (!SimClock.InHistory) PerfLog.Notify_Day(this);
            if (lastDailyMs > 15 && Testing.AutoTest.Active && !SimClock.InHistory)
            {
                var parts = new System.Text.StringBuilder();
                for (int i = 0; i < sectionMs.Length; i++) parts.Append($" {SectionNames[i]}={sectionMs[i]:F1}");
                LWLog.Message($"slow daily step {lastDailyMs:F1} ms:{parts}");
            }
        }

        /// <summary>
        /// At war: in an active war (raiders who are everyone's enemy always are). Standing hostility with raiders does
        /// not count, or every faction would always be at war.
        /// </summary>
        public bool AtWar(FactionState fs)
        {
            if (fs?.faction == null) return false;
            if (fs.faction.def.permanentEnemy) return true;
            foreach (War w in wars)
                if (w.Active && w.Involves(fs.factionId)) return true;
            return false;
        }

        public float AverageSettlementsPerFaction()
        {
            int n = 0, total = 0;
            foreach (FactionState fs in factionStates)
                if (fs.faction != null && !fs.faction.defeated && fs.settlements.Count > 0)
                {
                    n++;
                    total += fs.settlements.Count;
                }
            return n == 0 ? 0f : total / (float)n;
        }

        public Faction FactionById(int loadId)
        {
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
                if (f.loadID == loadId) return f;
            return null;
        }

        /// <summary>Keeps ended wars for history, but not forever.</summary>
        private void PruneWars()
        {
            int now = SimClock.Now;
            wars.RemoveAll(w => !w.Active && now - w.endTicks > GenDate.TicksPerYear * 3);
        }

        /// <summary>The faction's state if it has one; never creates (safe inside loops over factionStates).</summary>
        public FactionState ExistingStateFor(Faction f) => f != null && stateById.TryGetValue(f.loadID, out FactionState fs) ? fs : null;

        public FactionState StateFor(Faction f)
        {
            if (f == null) return null;
            if (stateById.TryGetValue(f.loadID, out FactionState fs)) return fs;
            if (!FactionState.IsSimulated(f)) return null;
            fs = new FactionState { factionId = f.loadID, faction = f };
            fs.RollTraits(f);
            // Seeded per faction like the traits: states are made at the first tick before history pushes its seed (RebuildIndexes in
            // WorldComponentTick), where the shared stream is seeded from the clock at startup.
            fs.treasury = 500f + Rand.RangeSeeded(0f, 1500f, Gen.HashCombineInt(f.loadID * 7919 + f.randomKey, 0x4C575452));
            factionStates.Add(fs);
            stateById[f.loadID] = fs;
            return fs;
        }

        /// <summary>Resolves faction states, seeds uninitialized settlements and rebuilds the per-faction lists.</summary>
        public void RebuildIndexes(bool seedMissing)
        {
            stateById.Clear();
            var factionsById = new Dictionary<int, Faction>();
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading) factionsById[f.loadID] = f;
            foreach (FactionState fs in factionStates)
            {
                fs.faction = factionsById.TryGetValue(fs.factionId, out Faction f) ? f : null;
                fs.settlements.Clear();
                fs.strength = 0f;
                fs.population = 0f;
                stateById[fs.factionId] = fs;
            }
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading) StateFor(f);
            activeSettlements.Clear();
            foreach (Settlement st in Find.WorldObjects.Settlements)
            {
                CompSettlementLife s = st.GetComponent<CompSettlementLife>();
                if (s == null || !CompSettlementLife.IsSimulatable(st)) continue;
                if (!s.initialized)
                {
                    if (!seedMissing) continue;
                    SettlementEconomy.Seed(s);
                }
                else if (s.productionPerCapita == null) SettlementEconomy.RecomputeProfile(s);
                activeSettlements.Add(s);
                FactionState fs = StateFor(st.Faction);
                if (fs == null) continue;
                fs.settlements.Add(s);
                fs.strength += s.garrison;
                fs.population += s.population;
            }
            spatial.Rebuild(activeSettlements);
        }

        /// <summary>Drops contexts whose lord no longer exists (map removed, lost track) after a grace period.</summary>
        private void PruneContexts()
        {
            int now = Find.TickManager.TicksGame;
            var live = new HashSet<int>();
            foreach (Map m in Find.Maps)
                foreach (Verse.AI.Group.Lord l in m.lordManager.lords) live.Add(l.loadID);
            contexts.RemoveAll(c => !live.Contains(c.lordId) && now - c.createdTicks > GenDate.TicksPerDay);
        }

        /// <summary>An object was added to or removed from the world: the id index no longer matches (see <see cref="Patch_WorldObjects_Membership"/>).</summary>
        internal static void Notify_WorldObjectsChanged() => objectIndexDirty = true;

        /// <summary>
        /// World object by id. The index is rebuilt when the world changes, when any object comes or goes (the count alone misses a
        /// ruin replaced by a new settlement in the same tick, and history never advances the tick), or when a lookup misses.
        /// </summary>
        public static WorldObject WorldObjectById(int id)
        {
            if (id < 0) return null;
            List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
            int now = Find.TickManager.TicksGame;
            if (objectIndexWorld != Find.World || objectIndexCount != all.Count || objectIndexDirty) RebuildObjectIndex(all, now);
            if (objectById.TryGetValue(id, out WorldObject o) && !o.Destroyed) return o;
            if (objectIndexTick == now) return null; // already rebuilt this tick: really gone
            RebuildObjectIndex(all, now);
            return objectById.TryGetValue(id, out o) && !o.Destroyed ? o : null;
        }

        private static void RebuildObjectIndex(List<WorldObject> all, int now)
        {
            objectById.Clear();
            foreach (WorldObject wo in all) objectById[wo.ID] = wo;
            objectIndexTick = now;
            objectIndexCount = all.Count;
            objectIndexWorld = Find.World;
            objectIndexDirty = false;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref schemaVersion, "schema");
            Scribe_Values.Look(ref nextId, "nextId", 1);
            Scribe_Values.Look(ref lastDailyTick, "lastDailyTick", -1);
            Scribe_Values.Look(ref historyDone, "historyDone");
            Scribe_Collections.Look(ref factionStates, "factionStates", LookMode.Deep);
            Scribe_Deep.Look(ref chronicle, "chronicle");
            Scribe_Collections.Look(ref contexts, "contexts", LookMode.Deep);
            Scribe_Collections.Look(ref wars, "wars", LookMode.Deep);
            Scribe_Collections.Look(ref tradeLinks, "tradeLinks", LookMode.Deep);
            Scribe_Deep.Look(ref roadState, "roadState");
            Scribe_Values.Look(ref warsSeeded, "warsSeeded");
            Scribe_Values.Look(ref initialAvgSettlements, "initialAvgSettlements");
            Scribe_Collections.Look(ref figures, "figures", LookMode.Deep);
            Scribe_Collections.Look(ref revenge, "revenge", LookMode.Deep);
            Scribe_Collections.Look(ref aidRequests, "aidRequests", LookMode.Deep);
            Scribe_Collections.Look(ref roots, "roots", LookMode.Deep);
            Scribe_Values.Look(ref rebellions, "rebellions");
            Scribe_Values.Look(ref lastRebellionTick, "lastRebellion", int.MinValue / 2);
            Scribe_Values.Look(ref lastRenewalTick, "lastRenewal", int.MinValue / 2);
            Scribe_Values.Look(ref initialFactionCount, "initialFactionCount");
            Scribe_Collections.Look(ref refugees, "refugees", LookMode.Deep);
            Scribe_Values.Look(ref lastRefugeesTick, "lastRefugeesTick", FactionState.Never);
            Scribe_Collections.Look(ref warRequests, "warRequests", LookMode.Deep);
            Scribe_Collections.Look(ref prisoners, "prisoners", LookMode.Deep);
            Scribe_Collections.Look(ref deeds, "deeds", LookMode.Deep);
            Scribe_Collections.Look(ref tributaries, "tributaries", LookMode.Deep);
            Scribe_Collections.Look(ref clientFactionIds, "lw_clientFactions", LookMode.Value);
            Scribe_Values.Look(ref clientFactionsCreated, "lw_clientFactionsCreated");
            Scribe_Collections.Look(ref hires, "hires", LookMode.Deep);
            Scribe_Values.Look(ref lastWarRequestTick, "lastWarRequestTick", FactionState.Never);
            Scribe_Values.Look(ref lastTributeTick, "lastTributeTick", FactionState.Never);
            Scribe_Values.Look(ref lastNeighbourVisitTick, "lastNeighbourVisitTick", FactionState.Never);
            Scribe_Values.Look(ref preparedForRemoval, "preparedForRemoval");
            Scribe_Collections.Look(ref tributeTruces, "tributeTruces", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref feudEnded, "feudEnded", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref lastAidRequestTick, "lastAidRequestTick", FactionState.Never);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                factionStates ??= new List<FactionState>();
                factionStates.RemoveAll(f => f == null);
                chronicle ??= new ChronicleLog();
                contexts ??= new List<IncidentContext>();
                wars ??= new List<War>();
                tradeLinks ??= new List<PairValue>();
                roadState ??= new RoadState();
                figures ??= new List<Figure>();
                revenge ??= new List<PendingRevenge>();
                aidRequests ??= new List<AidRequest>();
                roots ??= new List<PawnRoot>();
                refugees ??= new List<PendingRefugees>();
                warRequests ??= new List<WarRequest>();
                prisoners ??= new List<PrisonerRecord>();
                deeds ??= new List<Deed>();
                tributaries ??= new List<TributaryRecord>();
                tributaries.RemoveAll(r => r == null);
                clientFactionIds ??= new List<int>();
                hires ??= new List<HireContract>();
                hires.RemoveAll(h => h == null);
                prisoners.RemoveAll(r => r?.pawn == null);
                tributeTruces ??= new Dictionary<int, int>();
                feudEnded ??= new Dictionary<int, int>();
                roots.RemoveAll(r => r?.pawn == null);
                aidRequests.RemoveAll(r => r?.commodity == null);
                schemaVersion = Schema;
                objectIndexTick = -1;
            }
        }
    }

    /// <summary>Any world object coming or going invalidates the component's object-id index (see <see cref="LivingWorldComponent.WorldObjectById"/>).</summary>
    [HarmonyPatch]
    public static class Patch_WorldObjects_Membership
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Add));
            yield return AccessTools.Method(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.Remove));
        }

        public static void Postfix() => LivingWorldComponent.Notify_WorldObjectsChanged();
    }
}
