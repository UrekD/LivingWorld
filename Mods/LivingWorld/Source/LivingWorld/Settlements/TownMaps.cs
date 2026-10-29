using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.BaseGen;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>What one town map was generated with (tests and the autotest log).</summary>
    public sealed class TownMapStats
    {
        public CellRect vanillaRect, rect, mainRect;
        /// <summary>The defenders' raid points we set; 0 when the points were left to vanilla.</summary>
        public float points;
        public float loot, strength;
        public int defenders;
        public double ms;
        public string genStepDef;

        // The state post pass (scars, emptier stockpiles, hungry defenders).
        /// <summary>The town's scar (0..1) the map was generated with.</summary>
        public float scar;
        /// <summary>Scars: buildings destroyed, buildings left damaged, roof holders weakened (never destroyed), floor cells burned, ash and rubble filth made.</summary>
        public int destroyed, damaged, weakened, burned, ash, rubble;
        /// <summary>Where the scars show, with a weight each (a fallen building counts most): only for a camera to find the worst of the damage (the tour's shots; nothing in the game reads it).</summary>
        public readonly List<(IntVec3 cell, int weight)> damageSpots = new List<(IntVec3, int)>();
        /// <summary>Roof holders (walls, doors, columns) and doors of the town's faction in its grounds, before the scars and after them (equal: none is ever destroyed).</summary>
        public int roofHoldersBefore, roofHoldersAfter, doorsBefore, doorsAfter;
        /// <summary>The thing ids of those roof holders and doors as the post pass found them, before the scars: all must still stand on the finished map.</summary>
        public readonly HashSet<int> holderIds = new HashSet<int>();
        /// <summary>Stockpile things the thinning looked at (not the loot scatter's), and how many it destroyed whole.</summary>
        public int itemsBefore, itemsRemoved;
        /// <summary>Market value of those things before the thinning and after it.</summary>
        public float stockpileValueBefore, stockpileValueAfter;
        /// <summary>Why the stockpiles were not thinned at all (null if they were looked at).</summary>
        public string thinSkip;
        /// <summary>Per commodity, in the same pass: units and market value before and after.</summary>
        public readonly Dictionary<CommodityDef, (int countBefore, float valueBefore, int countAfter, float valueAfter)> byCommodity =
            new Dictionary<CommodityDef, (int, float, int, float)>();
        /// <summary>
        /// Per commodity, the least and the most market value the thinning can leave (each stack is rounded down or up, a single item kept or not):
        /// the stockpile's value after it must lie between them.
        /// </summary>
        public readonly Dictionary<CommodityDef, (float low, float high)> afterBounds = new Dictionary<CommodityDef, (float, float)>();
        /// <summary>Famine defenders given malnutrition (every candidate gets it, or already has more), and the highest severity seen.</summary>
        public int hungry;
        public float maxMalnutrition;
        /// <summary>The things the loot scatter spawned (thing id → stack count then): never thinned.</summary>
        public readonly Dictionary<int, int> lootStacks = new Dictionary<int, int>();
    }

    /// <summary>
    /// An NPC town's map is generated from the town's state in the simulation: the grounds follow its size, the defenders follow how full its
    /// garrison is against its own target, the loot follows its wealth. Only the town's own <c>GenStep_Settlement</c> run opens a context; the
    /// <c>SymbolResolver_Settlement.Resolve</c> prefix then fills the parameters vanilla leaves open. The result is baked at generation:
    /// a live map is never changed. Every hook falls back to vanilla on any error.
    /// </summary>
    public static class TownMaps
    {
        internal sealed class Context
        {
            public Map map;
            public Settlement settlement;
            public CompSettlementLife life;
            public Faction faction;
            public SettlementTierDef tier;
            public bool applied;
            public float strength;
            public string genStepDef;
            public readonly Stopwatch clock = Stopwatch.StartNew();
            public readonly TownMapStats stats = new TownMapStats();
            /// <summary>Set when recording the loot scatter threw: what it spawned is unknown, so nothing is thinned.</summary>
            public bool lootFailed;
            /// <summary>Set when our loot hook recorded this map's loot scatter (its prefix ran and its finalizer followed): without it the loot is unknown.</summary>
            public bool lootRecorded;
        }

        private const string DoneVar = "LW_TownMapDone";
        private const float MinRect = 20f;
        /// <summary>Points are kept this much above a maker's smallest group (plus one), so a rounding or a costlier leader never leaves the group empty.</summary>
        private const float FloorMargin = 1.05f;

        private static Context open;
        private static int lastMapId = -1;
        private static readonly HashSet<string> failuresLogged = new HashSet<string>();

        /// <summary>The stats of the last map whose parameters we set (null after a vanilla one); with <see cref="LastSkip"/>, for tests and metrics.</summary>
        public static TownMapStats Last;
        /// <summary>Why the last ScatterAt of the last generation was left alone (null if none was).</summary>
        public static string LastSkip;
        /// <summary>Test switch: the Resolve prefix applies nothing (what another mod skipping the resolver looks like).</summary>
        internal static bool TestSkipResolve;
        /// <summary>Test counters: ScatterAt entries of town gen steps since the test reset it, and the rect the last settlement symbol was resolved with (autotest only).</summary>
        internal static int Entries;
        internal static CellRect SeenRect;

        public static bool Eligible(Settlement s) => IneligibleReason(s) == null;

        private static string IneligibleReason(Settlement s)
        {
            if (!LivingWorldMod.Settings.townMaps) return "setting";
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            if (life == null || !life.Active) return "inactive";
            if (SettlementOps.QuestRelated(s)) return "quest";
            return null;
        }

        // ---- The numbers ----

        private static FactionState StateOf(CompSettlementLife life) => LivingWorldComponent.Instance?.ExistingStateFor(life.Settlement.Faction);

        private static bool AtWar(FactionState fs) => LivingWorldComponent.Instance?.AtWar(fs) == true;

        /// <summary>
        /// What the town's garrison is regrowing toward: the same figure <see cref="SettlementEconomy.DailyStep"/> uses (war footing included), without
        /// the cut for an empty treasury. A garrison at this size is full, whether the faction is at war or at peace, rich or broke.
        /// </summary>
        internal static float GarrisonGoal(CompSettlementLife life)
        {
            FactionState fs = StateOf(life);
            return Mathf.Max(1f, SettlementEconomy.RegrowthTarget(life, fs, AtWar(fs), includePay: false));
        }

        /// <summary>The garrison against <see cref="GarrisonGoal"/>, before the bounds: 1 is full, below 1 is depleted.</summary>
        internal static float RawStrength(CompSettlementLife life) => life.garrison / GarrisonGoal(life);

        /// <summary>How full the garrison is, kept between the tuned bounds (0.7 depleted … 1.1 reinforced).</summary>
        public static float Strength(CompSettlementLife life) => Mathf.Clamp(RawStrength(life), Tuning.T.townMapMinStrength, Tuning.T.townMapMaxStrength);

        /// <summary>The faction's militarization (traits, permanent enemy) before the bounds.</summary>
        internal static float RawMilitarization(CompSettlementLife life)
        {
            Faction f = life.Settlement.Faction;
            return (StateOf(life)?.TraitFactor(t => t.militarization) ?? 1f) * (f != null && f.def.permanentEnemy ? 1.3f : 1f);
        }

        private static float Militarization(CompSettlementLife life) => Mathf.Clamp(RawMilitarization(life), Tuning.T.townMapMilMin, Tuning.T.townMapMilMax);

        /// <summary>A faction at war keeps a few more defenders at home; the garrison's own war footing is already part of <see cref="GarrisonGoal"/>.</summary>
        internal static float WarFactor(CompSettlementLife life) => AtWar(StateOf(life)) ? Tuning.T.townMapWarFactor : 1f;

        /// <summary>The tier's normal force scaled by the three factors, before the pawn group makers' ranges.</summary>
        internal static float RawPoints(SettlementTierDef tier, float strength, float militarization, float war) =>
            Tuning.T.townMapBasePoints * tier.townMapPointsFactor * strength * militarization * war;

        /// <summary>
        /// The defenders' raid points for this town's map, or null to leave vanilla's. The tier's normal force scaled by the town's strength, its
        /// faction's militarization and war footing, then held inside what one of the faction's pawn group makers of this kind can make.
        /// </summary>
        public static float? PlannedPoints(CompSettlementLife life, PawnGroupKindDef kind = null)
        {
            Faction f = life.Settlement.Faction;
            if (f == null) return null;
            kind ??= PawnGroupKindDefOf.Settlement;
            return ClampToIntervals(RawPoints(life.Tier, Strength(life), Militarization(life), WarFactor(life)), IntervalsOf(f.def, kind));
        }

        // The makers' ranges never change in a session, and the inspect line asks every frame the town is selected.
        private static readonly Dictionary<(FactionDef, PawnGroupKindDef), List<(float lo, float hi)>> intervalCache = new Dictionary<(FactionDef, PawnGroupKindDef), List<(float lo, float hi)>>();

        private static List<(float lo, float hi)> IntervalsOf(FactionDef def, PawnGroupKindDef kind)
        {
            if (!intervalCache.TryGetValue((def, kind), out List<(float lo, float hi)> list))
                intervalCache[(def, kind)] = list = MakerIntervals(def, kind, Tuning.T.townMapMaxPoints);
            return list;
        }

        /// <summary>The points one maker can generate from: from just above its smallest group to its own maximum, capped; null if nothing fits.</summary>
        internal static (float lo, float hi)? MakerInterval(float minPoints, float maxTotalPoints, float cap)
        {
            float lo = minPoints * FloorMargin + 1f, hi = Mathf.Min(maxTotalPoints, cap);
            return lo <= hi ? (lo, hi) : ((float, float)?)null;
        }

        /// <summary>One interval per maker of the kind that can be used at all. A value between two makers' ranges satisfies neither, so it must be held inside one of them.</summary>
        internal static List<(float lo, float hi)> MakerIntervals(FactionDef def, PawnGroupKindDef kind, float cap)
        {
            var list = new List<(float lo, float hi)>();
            if (def?.pawnGroupMakers == null) return list;
            foreach (PawnGroupMaker m in def.pawnGroupMakers)
            {
                if (m.kindDef != kind) continue;
                (float lo, float hi)? iv = MakerInterval(m.MinPointsToGenerateAnything(def), m.maxTotalPoints, cap);
                if (iv.HasValue) list.Add(iv.Value);
            }
            return list;
        }

        /// <summary>The value itself if an interval holds it, else the nearest bound of the nearest interval; null with no interval (leave vanilla).</summary>
        internal static float? ClampToIntervals(float raw, List<(float lo, float hi)> intervals)
        {
            if (intervals.Count == 0) return null;
            float best = raw, bestDistance = float.MaxValue;
            foreach ((float lo, float hi) in intervals)
            {
                float c = Mathf.Clamp(raw, lo, hi);
                float d = Mathf.Abs(c - raw);
                if (d >= bestDistance) continue;
                best = c;
                bestDistance = d;
            }
            return best;
        }

        /// <summary>The loot's market value: vanilla's 1800 by tier and by the town's prosperity (0.6 to 1.4 times), capped.</summary>
        public static float PlannedLoot(CompSettlementLife life) =>
            Mathf.Min(Tuning.T.townMapMaxLoot, SymbolResolver_Settlement.DefaultLootMarketValue * life.Tier.townMapLootFactor * (0.6f + 0.8f * Mathf.Clamp01(life.prosperity)));

        private static readonly string[] DefenderWordKeys =
            { "LW_Inspect_Defenders_Handful", "LW_Inspect_Defenders_Company", "LW_Inspect_Defenders_Strong", "LW_Inspect_Defenders_Fortress" };

        /// <summary>0 "a handful" … 3 "a fortress" for planned points (thresholds in the tuning).</summary>
        internal static int DefenderWordIndex(float points)
        {
            Vector3 w = Tuning.T.townMapDefenderWords;
            return points < w.x ? 0 : points < w.y ? 1 : points < w.z ? 2 : 3;
        }

        internal static string DefenderWordKey(float points) => DefenderWordKeys[DefenderWordIndex(points)];

        /// <summary>Below this strength the inspect line adds "(weakened)".</summary>
        internal const float WeakenedBelow = 0.85f;

        /// <summary>The inspect line about what would meet an attacker, or null (vanilla maps, other maps, errors).</summary>
        public static string DefendersLine(CompSettlementLife life)
        {
            try
            {
                if (!Eligible(life.Settlement) || PlannedPoints(life) is not float points) return null;
                string weakened = Strength(life) < WeakenedBelow ? "LW_Inspect_DefendersWeakened".Translate().Resolve() : "";
                return "LW_Inspect_Defenders".Translate(DefenderWordKey(points).Translate().Resolve(), weakened).Resolve();
            }
            catch (Exception e)
            {
                Fail("defenders line", e);
                return null;
            }
        }

        // ---- Hook 1: the town's own gen step ----

        /// <summary>
        /// Opens a context for the map of an eligible town if this is the town's own step: a def-backed step with vanilla fields (not the abandoned
        /// colony mutator's bare copy, not another mod's), once per generation.
        /// </summary>
        internal static void Begin(GenStep_Settlement step, Map map)
        {
            open = null;
            if (map.uniqueID != lastMapId)
            {
                lastMapId = map.uniqueID;
                Last = null;
                LastSkip = null;
            }
            if (map.Parent is not Settlement s) return;
            Entries++;
            string skip = IneligibleReason(s);
            if (skip == null && step.def == null) skip = "def null";
            if (skip == null && (!step.generatePawns || step.overrideFaction != null || step.postProcessSettlementParams != null || step.lootMarketValue.HasValue
                                 || step.lootThingSetMaker != null || step.requiredGravcoreRooms != 0)) skip = "step parameters";
            if (skip == null && map.ParentFaction != s.Faction) skip = "faction";
            if (skip == null && MapGenerator.TryGetVar(DoneVar, out bool done) && done) skip = "done";
            if (skip != null)
            {
                LastSkip = skip;
                return;
            }
            MapGenerator.SetVar(DoneVar, true);
            CompSettlementLife life = s.GetComponent<CompSettlementLife>();
            open = new Context
            {
                map = map,
                settlement = s,
                life = life,
                faction = s.Faction,
                tier = life.Tier,
                strength = Strength(life),
                genStepDef = step.def.defName,
            };
        }

        /// <summary>Always closes the context, even if generation threw.</summary>
        internal static void End() => open = null;

        /// <summary>A hook failed: the context is closed so neither the post pass nor a later pass acts on a map whose parameters were not applied, and the map is vanilla's.</summary>
        internal static void Abandon(string where, Exception e)
        {
            open = null;
            LastSkip = "error";
            Fail(where, e);
        }

        // ---- Hook 2: the settlement symbol ----

        /// <summary>
        /// Sets what the symbol leaves open (and never what another caller already set): the rect, the defenders' points, the loot value and the edge
        /// defences. The first call with the open context's map does it; <see cref="TestSkipResolve"/> turns it off. All-or-nothing: everything that
        /// can throw is computed first, and only plain assignments follow, so an exception leaves vanilla's generation and the context as they were.
        /// </summary>
        internal static void Apply(ref ResolveParams rp)
        {
            if (Testing.AutoTest.Active) SeenRect = rp.rect;
            Context c = open;
            if (c == null || c.applied || TestSkipResolve || BaseGen.globalSettings.map != c.map || (rp.faction != null && rp.faction != c.faction)) return;

            CellRect vanillaRect = rp.rect;
            CellRect rect = ScaledRect(c, vanillaRect);
            float? points = !rp.settlementPawnGroupPoints.HasValue && rp.pawnGroupMakerParams == null && rp.settlementDontGeneratePawns != true
                ? PlannedPoints(c.life, rp.pawnGroupKindDef)
                : null;
            float? loot = rp.lootMarketValue.HasValue ? null : PlannedLoot(c.life);
            int? edge = !rp.edgeDefenseWidth.HasValue && c.tier.townMapEdgeDefense >= 0 ? c.tier.townMapEdgeDefense : null;
            List<CellRect> usedRects = MapGenerator.UsedRects;

            // Commit. Nothing below can throw; `applied` goes last.
            TownMapStats st = c.stats;
            st.vanillaRect = vanillaRect;
            st.rect = rect;
            st.strength = c.strength;
            st.genStepDef = c.genStepDef;
            if (points.HasValue)
            {
                rp.settlementPawnGroupPoints = points;
                st.points = points.Value;
            }
            if (loot.HasValue)
            {
                rp.lootMarketValue = loot;
                st.loot = loot.Value;
            }
            if (edge.HasValue) rp.edgeDefenseWidth = edge;
            rp.rect = rect;
            MapGenerator.SetVar("SettlementRect", rect);
            usedRects.Add(rect);
            // BaseGen took the vanilla rect as the main one before this prefix ran; its coverage caps (farms, breweries, power) divide by it.
            if (rect != vanillaRect) BaseGen.globalSettings.mainRect = rect;
            // Read here: BaseGen clears its global settings (mainRect included) when generation ends.
            st.mainRect = BaseGen.globalSettings.mainRect;
            c.applied = true;
        }

        /// <summary>
        /// The town's grounds, scaled about their centre by the tier. Smaller is always taken; larger only if it fits (inside the map with a margin,
        /// clear of layout structures and of rects other steps already claimed). The Empire's grounds are left alone: its fallback landing pad is
        /// placed next to the rect vanilla's own gen step still holds, so a changed rect would leave the pad off the town.
        /// </summary>
        private static CellRect ScaledRect(Context c, CellRect rect)
        {
            float scale = c.tier.townMapRectScale;
            if (c.faction == Faction.OfEmpire || rect.IsEmpty || Mathf.Approximately(scale, 1f)) return rect;
            int w = rect.Width, h = rect.Height, nw, nh;
            if (scale < 1f)
            {
                nw = Mathf.Min(w, Mathf.Max((int)MinRect, Mathf.RoundToInt(w * scale)));
                nh = Mathf.Min(h, Mathf.Max((int)MinRect, Mathf.RoundToInt(h * scale)));
            }
            else
            {
                nw = Mathf.RoundToInt(w * scale);
                nh = Mathf.RoundToInt(h * scale);
            }
            if (nw == w && nh == h) return rect;
            CellRect scaled = CellRect.CenteredOn(rect.CenterCell, nw, nh);
            if (scale > 1f)
            {
                scaled = scaled.ClipInsideRect(c.map.BoundsRect(10));
                if (scaled.Width < w || scaled.Height < h || Overlaps(c.map, scaled)) return rect;
            }
            return scaled;
        }

        private static bool Overlaps(Map map, CellRect rect)
        {
            foreach (LayoutStructureSketch sketch in map.layoutStructureSketches)
                if (sketch?.layoutSketch != null && sketch.layoutSketch.OccupiedRect.Overlaps(rect)) return true;
            if (MapGenerator.TryGetVar("UsedRects", out List<CellRect> used))
                foreach (CellRect r in used)
                    if (r.Overlaps(rect)) return true;
            return false;
        }

        // ---- Hook 3: what the loot scatter spawned ----

        /// <summary>Test switch: the loot hook counts as missing, so nothing is thinned (the case a game update breaks the hook).</summary>
        internal static bool TestLootHookOff;
        /// <summary>Test switch: the loot prefix records nothing for the map (what a mod's resolver or a higher-priority prefix that skips ours looks like).</summary>
        internal static bool TestLootBeforeNull;
        private static bool? lootHookPatched;

        /// <summary>
        /// True when our prefix and finalizer really sit on the loot scatter resolver. Stockpiles are thinned only then: without knowing which things
        /// are the loot, the loot could be thinned, and that decision is never undone silently.
        /// </summary>
        internal static bool LootHookOk => !TestLootHookOff && (lootHookPatched ??= LootHookPatched());

        private static bool LootHookPatched()
        {
            System.Reflection.MethodBase resolve = AccessTools.Method(typeof(SymbolResolver_LootScatter), nameof(SymbolResolver_LootScatter.Resolve));
            Patches patches = resolve == null ? null : Harmony.GetPatchInfo(resolve);
            bool Ours(IEnumerable<Patch> list) => list != null && list.Any(p => p.owner == LivingWorldStartup.HarmonyId);
            return patches != null && Ours(patches.Prefixes) && Ours(patches.Finalizers);
        }

        private static bool LootContext(Map map) => open != null && open.applied && open.map == map && BaseGen.globalSettings.map == map;

        /// <summary>The ids of the things lying on the map just before the loot scatter resolves (null when no context wants them).</summary>
        internal static void LootBefore(Map map, out HashSet<int> ids)
        {
            ids = null;
            if (map == null || TestLootBeforeNull || !LootContext(map)) return;
            List<Thing> things = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            ids = new HashSet<int>();
            for (int i = 0; i < things.Count; i++) ids.Add(things[i].thingIDNumber);
        }

        /// <summary>
        /// Every haulable the loot scatter added; marks the map's loot as recorded. It resolves last (the symbol stack is LIFO), so what is new since <see cref="LootBefore"/> is exactly
        /// the loot. Runs from a finalizer: what was spawned before an exception inside the resolver is recorded too.
        /// </summary>
        internal static void LootAfter(Map map, HashSet<int> before)
        {
            if (before == null || map == null || !LootContext(map)) return;
            open.lootRecorded = true;
            List<Thing> things = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            Dictionary<int, int> loot = open.stats.lootStacks;
            for (int i = 0; i < things.Count; i++)
                if (!before.Contains(things[i].thingIDNumber)) loot[things[i].thingIDNumber] = things[i].stackCount;
        }

        /// <summary>The loot hook failed: what the scatter spawned is unknown for this map, so its stockpiles are left as vanilla made them.</summary>
        internal static void LootFailed(string where, Exception e)
        {
            if (open != null) open.lootFailed = true;
            Fail(where, e);
        }

        // ---- After the step ----

        // Scars: shares of the buildings and of the floor the attack touched, and the damage it did.
        private const float BreakableShare = 0.7f;
        private const float RoofHolderShare = 0.5f;
        private const float AshPerCell = 0.03f;
        private const float BurnedShare = 0.2f;
        private const int RubblePerBuilding = 2;
        private const int AshTriesPerPiece = 4;
        /// <summary>Famine defenders are hungry from this many days of famine.</summary>
        private const int HungryFromFamineDays = 3;

        /// <summary>
        /// Runs after the town's step, before the power step (conduits follow the surviving buildings): the town's state on its map. Scars from its last
        /// lost attack, stockpiles that hold what its stores hold, hungry defenders when it starves. It runs on its own random seed, so it is the same for
        /// a given tile and sack, whatever BaseGen and the pawn generation used. Each part is guarded on its own: one failing leaves the others.
        /// </summary>
        internal static void PostPass(Map map)
        {
            Context c = open;
            if (c == null || !c.applied || c.map != map) return;
            TownMapStats st = c.stats;
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                if (p.Faction == c.faction && p.RaceProps.Humanlike && !p.Dead && st.rect.Contains(p.Position)) st.defenders++;
            st.scar = c.life.Scar();
            Rand.PushState(Gen.HashCombineInt(map.Tile.GetHashCode(), 0x4C57544D ^ c.life.sackedTicks));
            try
            {
                Part(c, "scars", Scars);
                Part(c, "stockpiles", Stockpiles);
                Part(c, "hungry defenders", HungryDefenders);
            }
            finally
            {
                Rand.PopState();
            }
            st.ms = c.clock.Elapsed.TotalMilliseconds;
            Last = st;
            if (Testing.AutoTest.Active)
                Log.Message($"[LivingWorld:townmap] {c.settlement.LabelCap} tier={c.tier.defName} rect={st.rect.Width}x{st.rect.Height} (vanilla {st.vanillaRect.Width}x{st.vanillaRect.Height}) " +
                            $"points={(st.points > 0f ? st.points.ToString("F0") : "vanilla")} strength={st.strength:F2} defenders={st.defenders} loot={st.loot:F0} " +
                            $"stockpile={st.stockpileValueBefore:F0}->{st.stockpileValueAfter:F0}{(st.thinSkip != null ? $" (not thinned: {st.thinSkip})" : "")} scar={st.scar:F2} destroyed={st.destroyed} " +
                            $"roofHolders={st.roofHoldersBefore}/{st.roofHoldersAfter} doors={st.doorsBefore}/{st.doorsAfter} ash={st.ash} itemsRemoved={st.itemsRemoved}/{st.itemsBefore} " +
                            $"hungry={st.hungry} ms={st.ms:F0} def={st.genStepDef}");
        }

        private static void Part(Context c, string what, Action<Context> part)
        {
            try
            {
                part(c);
            }
            catch (Exception e)
            {
                Fail(what, e);
            }
        }

        // ---- Scars ----

        /// <summary>The town's buildings in its grounds: what its faction built inside the rect (not the landscape's, not another step's).</summary>
        private static List<Building> BuildingsInGrounds(Context c)
        {
            var list = new List<Building>();
            foreach (Thing t in c.map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                if (t is Building b && b.Faction == c.faction && c.stats.rect.Contains(b.Position)) list.Add(b);
            return list;
        }

        /// <summary>
        /// A building that holds a roof, a door, or a holder of things is never damaged by the scars: a destroyed roof holder would bring the roof down on
        /// the defenders, and vanilla's damage routine destroys the contents of a holder and any door that touches a destroyed building and has no wall
        /// beside it. Keeping every building that touches a door out of the damaged set (cardinally, as that routine looks) means no door is ever removed.
        /// </summary>
        internal static bool Breakable(Building b, Map map)
        {
            if (b.def.holdsRoof || b is IThingHolder || b is Building_Door) return false;
            foreach (IntVec3 cell in b.OccupiedRect().AdjacentCellsCardinal)
                if (cell.InBounds(map) && cell.GetDoor(map) != null) return false;
            return true;
        }

        private static void Scars(Context c)
        {
            TownMapStats st = c.stats;
            List<Building> all = BuildingsInGrounds(c);
            foreach (Building b in all)
            {
                if (b.def.holdsRoof) st.roofHoldersBefore++;
                if (b is Building_Door) st.doorsBefore++;
                if (b.def.holdsRoof || b is Building_Door) st.holderIds.Add(b.thingIDNumber);
            }
            float scar = st.scar;
            if (scar >= CompSettlementLife.ScarVisible) ApplyScars(c, all, scar);
            foreach (Building b in all)
            {
                if (b.Destroyed) continue;
                if (b.def.holdsRoof) st.roofHoldersAfter++;
                if (b is Building_Door) st.doorsAfter++;
            }
        }

        private static void ApplyScars(Context c, List<Building> all, float scar)
        {
            TownMapStats st = c.stats;
            Map map = c.map;
            CellRect rect = st.rect;

            // 1. Buildings: a random share of those that can break, hit from one side by vanilla's own routine (it burns the floor under what it destroys).
            var breakable = new List<Building>();
            foreach (Building b in all)
                if (Breakable(b, map)) breakable.Add(b);
            int hitCount = Mathf.RoundToInt(breakable.Count * scar * BreakableShare);
            var hit = new List<Building>(hitCount);
            foreach (Building b in breakable.InRandomOrder())
            {
                if (hit.Count >= hitCount) break;
                hit.Add(b);
            }
            var spots = new List<IntVec3>(hit.Count);
            foreach (Building b in hit) spots.Add(b.Position);
            MapGenUtility.DamageBuildings(hit, map, 0.5f * scar, 1.2f, 1.32f);
            var ruined = new List<IntVec3>();
            for (int i = 0; i < hit.Count; i++)
            {
                if (hit[i].Destroyed)
                {
                    ruined.Add(spots[i]);
                    st.damageSpots.Add((spots[i], 4));
                }
                else if (hit[i].HitPoints < hit[i].MaxHitPoints)
                {
                    st.damaged++;
                    st.damageSpots.Add((spots[i], 2));
                }
            }
            st.destroyed = ruined.Count;

            // 2. Roof holders: weakened, never destroyed.
            var holders = new List<Building>();
            foreach (Building b in all)
                if (b.def.holdsRoof && !b.Destroyed) holders.Add(b);
            int weaken = Mathf.RoundToInt(holders.Count * scar * RoofHolderShare);
            foreach (Building b in holders.InRandomOrder())
            {
                if (st.weakened >= weaken) break;
                b.HitPoints = Mathf.Max(1, Mathf.RoundToInt(b.HitPoints * Rand.Range(0.35f, 0.85f)));
                st.weakened++;
            }

            // 3. Filth: ash over the grounds, rubble where a building fell.
            int ash = Mathf.RoundToInt(rect.Area * AshPerCell * scar);
            for (int tries = 0; st.ash < ash && tries < ash * AshTriesPerPiece; tries++)
            {
                IntVec3 cell = rect.RandomCell;
                if (cell.InBounds(map) && cell.Standable(map) && FilthMaker.TryMakeFilth(cell, map, ThingDefOf.Filth_Ash, 1, FilthSourceFlags.None, shouldPropagate: false))
                {
                    st.ash++;
                    st.damageSpots.Add((cell, 1));
                }
            }
            foreach (IntVec3 spot in ruined)
                for (int i = 0; i < RubblePerBuilding; i++)
                    if (CellFinder.TryFindRandomCellNear(spot, map, 2, x => x.InBounds(map) && x.Standable(map), out IntVec3 near)
                        && FilthMaker.TryMakeFilth(near, map, ThingDefOf.Filth_RubbleBuilding, 1, FilthSourceFlags.None, shouldPropagate: false))
                    {
                        st.rubble++;
                        st.damageSpots.Add((near, 2));
                    }

            // 4. Floors: a share of the cells that have a burned version (floors only; bridges and soil have none).
            var burnable = new List<IntVec3>();
            foreach (IntVec3 cell in rect)
                if (cell.InBounds(map) && cell.GetTerrain(map).burnedDef != null) burnable.Add(cell);
            int burn = Mathf.RoundToInt(burnable.Count * BurnedShare * scar);
            foreach (IntVec3 cell in burnable.InRandomOrder())
            {
                if (st.burned >= burn) break;
                TerrainDef burned = cell.GetTerrain(map).burnedDef;
                if (burned == null) continue;
                map.terrainGrid.SetTerrain(cell, burned);
                st.burned++;
            }
        }

        // ---- Emptier stockpiles ----

        /// <summary>
        /// The share of a commodity's vanilla stockpile that stays, from how full the town's stores are against its target (<paramref name="ratio"/>): 1 at
        /// <c>townStockNormal</c> or more, in proportion below that, never under <c>townMinStockScale</c>. With <paramref name="famineFood"/> it is cut by
        /// <c>townFamineFoodFactor</c> on top (food in a town in famine).
        /// </summary>
        internal static float ScaleOf(float ratio, bool famineFood)
        {
            LivingWorldTuningDef t = Tuning.T;
            float scale = Mathf.Clamp(ratio / t.townStockNormal, t.townMinStockScale, 1f);
            return famineFood ? scale * t.townFamineFoodFactor : scale;
        }

        internal static float StockScale(CompSettlementLife life, CommodityDef commodity, bool famine) =>
            ScaleOf(life.Stock(commodity) / life.TargetStock(commodity), famine && commodity == LWDefOf.LW_Food);

        /// <summary>
        /// The days of famine the town's maps show. The count only moves while the economy is simulated, so with it switched off (a famine that was
        /// running when it was) the last count would show for good: none then.
        /// </summary>
        internal static int FamineDays(CompSettlementLife life) => LivingWorldMod.Settings.simulateEconomy ? life.famineDays : 0;

        private static void Stockpiles(Context c)
        {
            TownMapStats st = c.stats;
            if (c.lootFailed || !LootHookOk)
            {
                st.thinSkip = c.lootFailed ? "loot hook failed" : "no loot hook";
                return;
            }
            // We asked for loot, but our hook never saw this map's loot scatter (another resolver for the symbol, a subclass, a prefix of a mod that skips ours):
            // which things are the loot is unknown, so nothing is thinned.
            if (st.loot > 0f && !c.lootRecorded)
            {
                st.thinSkip = "loot hook did not run";
                return;
            }
            Map map = c.map;
            bool famine = FamineDays(c.life) > 0;
            var scales = new float[Commodities.Count];
            foreach (CommodityDef commodity in Commodities.All) scales[commodity.index] = StockScale(c.life, commodity, famine);
            var items = new List<Thing>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t is Pawn || t is Corpse || t is MinifiedThing || !t.Spawned || !st.rect.Contains(t.Position) || st.lootStacks.ContainsKey(t.thingIDNumber)) continue;
                if (Commodities.Of(t.def) != null) items.Add(t);
            }
            st.itemsBefore = items.Count;
            foreach (Thing t in items)
            {
                CommodityDef commodity = Commodities.Of(t.def);
                float scale = scales[commodity.index];
                int count = t.stackCount, left = count;
                float unit = t.MarketValue;
                int lowest = count, highest = count;
                if (scale < 1f)
                {
                    if (t.def.stackLimit > 1)
                    {
                        // Randomly rounded, so a stack of one stays with the chance the share says (plain rounding would drop every one of them under half).
                        left = GenMath.RoundRandom(count * scale);
                        lowest = Mathf.FloorToInt(count * scale);
                        highest = Mathf.CeilToInt(count * scale);
                    }
                    else
                    {
                        left = Rand.Chance(1f - scale) ? 0 : count;
                        lowest = 0;
                    }
                }
                if (left <= 0)
                {
                    t.Destroy();
                    st.itemsRemoved++;
                }
                else if (left < count) t.stackCount = left;
                st.byCommodity.TryGetValue(commodity, out var e);
                st.byCommodity[commodity] = (e.countBefore + count, e.valueBefore + unit * count, e.countAfter + left, e.valueAfter + unit * left);
                st.afterBounds.TryGetValue(commodity, out var bounds);
                st.afterBounds[commodity] = (bounds.low + unit * lowest, bounds.high + unit * highest);
                st.stockpileValueBefore += unit * count;
                st.stockpileValueAfter += unit * left;
            }
        }

        // ---- Hungry defenders ----

        /// <summary>Whether a pawn counts as one of the town's defenders for hunger: its faction's humanlike, in the grounds, not a prisoner or a guest, and with a food need to recover through.</summary>
        internal static bool HungryCandidate(Pawn p, Faction faction, CellRect rect) =>
            p.Faction == faction && p.RaceProps.Humanlike && !p.Dead && !p.IsPrisoner && p.GuestStatus == null && rect.Contains(p.Position) && p.needs?.food != null;

        /// <summary>The malnutrition severity famine gives: 0.05 and 0.02 more a day of famine, never above <c>townMalnutritionMax</c> (trivial and minor stages).</summary>
        internal static float HungerSeverity(int famineDays) => Mathf.Clamp(0.05f + 0.02f * famineDays, 0.05f, Tuning.T.townMalnutritionMax);

        private static void HungryDefenders(Context c)
        {
            int days = FamineDays(c.life);
            if (days < HungryFromFamineDays) return;
            TownMapStats st = c.stats;
            float severity = HungerSeverity(days);
            foreach (Pawn p in c.map.mapPawns.AllPawnsSpawned)
            {
                if (!HungryCandidate(p, c.faction, st.rect)) continue;
                Hediff h = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                if (h == null)
                {
                    h = HediffMaker.MakeHediff(HediffDefOf.Malnutrition, p);
                    h.Severity = severity;
                    p.health.AddHediff(h);
                }
                else if (h.Severity < severity) h.Severity = severity;
                st.hungry++;
                st.maxMalnutrition = Mathf.Max(st.maxMalnutrition, h.Severity);
            }
        }

        /// <summary>
        /// Before a town's map goes away: the settlement faction's humanlikes on it lose the mild malnutrition famine gave them (and any other that mild)
        /// unless they are starving for real. A pawn that is not on a map never recovers (its food need is frozen), and vanilla keeps defenders as
        /// world pawns (leaders, pawns with relations, figures) and redresses them into later maps, where the hunger would show without any famine.
        /// </summary>
        internal static void ClearFamineHunger(Map map)
        {
            if (map?.Parent is not Settlement s || s.Faction == null || s.Faction.IsPlayer) return;
            float mild = Tuning.T.townMalnutritionMax + 0.001f;
            foreach (Pawn p in map.mapPawns.AllPawns)
            {
                if (p.Faction != s.Faction || !p.RaceProps.Humanlike || p.Dead || p.health?.hediffSet == null || p.needs?.food == null || p.needs.food.Starving) continue;
                Hediff h = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                if (h != null && h.Severity <= mild) p.health.RemoveHediff(h);
            }
        }

        /// <summary>
        /// How full the towns' stores are against what they want (the calibration of <c>townStockNormal</c>): per commodity the 10th, 50th and 90th percentile
        /// of stock / target over the active towns, and how many are under the normal share. The value (1.3) is set near the 25th percentile of the essential goods,
        /// so about three quarters of the towns lose nothing from their stockpiles.
        /// </summary>
        internal static string StockDistribution(LivingWorldComponent comp)
        {
            float normal = Tuning.T.townStockNormal;
            var ratios = new List<float>[Commodities.Count];
            foreach (CommodityDef commodity in Commodities.All) ratios[commodity.index] = new List<float>();
            var pooled = new List<float>();
            int towns = 0, thinned = 0, thinnedEssential = 0;
            foreach (CompSettlementLife life in comp.activeSettlements)
            {
                if (!life.Active) continue;
                towns++;
                bool any = false, essential = false;
                foreach (CommodityDef commodity in Commodities.All)
                {
                    float ratio = life.Stock(commodity) / life.TargetStock(commodity);
                    ratios[commodity.index].Add(ratio);
                    if (commodity.essential) pooled.Add(ratio);
                    if (ratio >= normal) continue;
                    any = true;
                    essential |= commodity.essential;
                }
                if (any) thinned++;
                if (essential) thinnedEssential++;
            }
            string Percentile(List<float> sorted, float q) => sorted.Count == 0 ? "-" : sorted[Mathf.Min(sorted.Count - 1, Mathf.FloorToInt(q * sorted.Count))].ToString("F2");
            pooled.Sort();
            var parts = new List<string>();
            foreach (CommodityDef commodity in Commodities.All)
            {
                List<float> list = ratios[commodity.index];
                list.Sort();
                string name = commodity == LWDefOf.LW_Food ? "food" : commodity.label;
                int below = list.FindAll(r => r < normal).Count;
                parts.Add($"{name}{(commodity.essential ? "*" : "")}={Percentile(list, 0.1f)}/{Percentile(list, 0.5f)}/{Percentile(list, 0.9f)} (p25 {Percentile(list, 0.25f)}, under {normal:F2}: {below})");
            }
            return $"stock/target p10/p50/p90 {string.Join(" ", parts)}; towns {towns}, losing stockpile goods: {thinned}, essential goods: {thinnedEssential}; p25 of all essential goods pooled: {Percentile(pooled, 0.25f)} (townStockNormal {normal:F2}; * = essential)";
        }

        /// <summary>What the defenders' points of the towns a player can attack look like: per tier, how many sit on a clamp, and which words show.</summary>
        internal sealed class PointsReport
        {
            public int towns, weak, atMaxStrength, atMinStrength, atMaxMil, atMinMil;
            public readonly HashSet<int> words = new HashSet<int>();
            public string perTier = "";

            private static string Share(int n, int all) => all == 0 ? "0/0" : $"{n}/{all} ({100f * n / all:F0}%)";

            public float Fraction(int n) => towns == 0 ? 0f : (float)n / towns;

            public override string ToString() =>
                $"townmap points per tier: {perTier}; strength < {WeakenedBelow:F2}: {Share(weak, towns)}; on the strength clamps max/min: {Share(atMaxStrength, towns)} / {Share(atMinStrength, towns)}; " +
                $"on the militarization clamps max/min: {Share(atMaxMil, towns)} / {Share(atMinMil, towns)}; words in use: {words.Count}";
        }

        /// <summary>
        /// The planned defenders of every town that gets them (eligible, so quest towns are out, as in the inspect line), by tier, with how many towns
        /// sit on each clamp (the history plan's calibration log and the check that the model has not collapsed into a number per tier).
        /// </summary>
        internal static PointsReport Distribution(LivingWorldComponent comp)
        {
            LivingWorldTuningDef t = Tuning.T;
            var report = new PointsReport();
            var byTier = new Dictionary<SettlementTierDef, List<float>>();
            foreach (CompSettlementLife life in comp.activeSettlements)
            {
                if (!Eligible(life.Settlement)) continue;
                report.towns++;
                float strength = RawStrength(life), mil = RawMilitarization(life);
                if (strength < WeakenedBelow) report.weak++;
                if (strength >= t.townMapMaxStrength) report.atMaxStrength++;
                if (strength <= t.townMapMinStrength) report.atMinStrength++;
                if (mil >= t.townMapMilMax) report.atMaxMil++;
                if (mil <= t.townMapMilMin) report.atMinMil++;
                if (PlannedPoints(life) is not float p) continue;
                report.words.Add(DefenderWordIndex(p));
                if (!byTier.TryGetValue(life.Tier, out List<float> list)) byTier[life.Tier] = list = new List<float>();
                list.Add(p);
            }
            var parts = new List<string>();
            foreach (SettlementTierDef tier in DefDatabase<SettlementTierDef>.AllDefsListForReading.OrderBy(d => d.order))
            {
                if (!byTier.TryGetValue(tier, out List<float> list) || list.Count == 0)
                {
                    parts.Add($"{tier.label} none");
                    continue;
                }
                list.Sort();
                float median = list[list.Count / 2];
                parts.Add($"{tier.label} n={list.Count} min/median/p95/max {list[0]:F0}/{median:F0}/{list[Mathf.Min(list.Count - 1, Mathf.CeilToInt(list.Count * 0.95f) - 1)]:F0}/{list[list.Count - 1]:F0} " +
                          $"median word '{DefenderWordKey(median).Translate()}'");
            }
            report.perTier = string.Join("; ", parts);
            return report;
        }

        internal static string PointsDistribution(LivingWorldComponent comp) => Distribution(comp).ToString();

        /// <summary>A failure in a hook is logged once per session and place, so one failing part never hides another; the map is generated the vanilla way from there.</summary>
        internal static void Fail(string where, Exception e)
        {
            if (!failuresLogged.Add(where)) return;
            LWLog.Error($"town maps ({where}): {e}");
        }
    }

    /// <summary>Hook 1: opens the context before the town's own gen step runs, finishes it after, and always closes it.</summary>
    [HarmonyPatch(typeof(GenStep_Settlement), "ScatterAt")]
    public static class Patch_GenStep_Settlement_ScatterAt
    {
        public static void Prefix(GenStep_Settlement __instance, Map map)
        {
            try
            {
                TownMaps.Begin(__instance, map);
            }
            catch (Exception e)
            {
                TownMaps.Abandon("begin", e);
            }
        }

        public static void Postfix(Map map)
        {
            try
            {
                TownMaps.PostPass(map);
            }
            catch (Exception e)
            {
                TownMaps.Fail("post pass", e);
            }
        }

        public static Exception Finalizer(Exception __exception)
        {
            TownMaps.End();
            return __exception;
        }
    }

    /// <summary>Hook 2: fills the open parameters of the town's settlement symbol.</summary>
    [HarmonyPatch(typeof(SymbolResolver_Settlement), nameof(SymbolResolver_Settlement.Resolve))]
    public static class Patch_SymbolResolver_Settlement_Resolve
    {
        public static void Prefix(ref ResolveParams rp)
        {
            try
            {
                // A copy, so a failure part way leaves vanilla's parameters whole.
                ResolveParams changed = rp;
                TownMaps.Apply(ref changed);
                rp = changed;
            }
            catch (Exception e)
            {
                TownMaps.Abandon("resolve", e);
            }
        }
    }

    /// <summary>Map removal: a town's map is about to be removed; its defenders go to the world without the famine's malnutrition (the pawns are passed to the world after this call).</summary>
    [HarmonyPatch(typeof(MapParent), nameof(MapParent.Notify_MyMapAboutToBeRemoved))]
    public static class Patch_MapParent_Notify_MyMapAboutToBeRemoved_TownMaps
    {
        public static void Prefix(MapParent __instance)
        {
            try
            {
                if (__instance is Settlement && __instance.HasMap) TownMaps.ClearFamineHunger(__instance.Map);
            }
            catch (Exception e)
            {
                TownMaps.Fail("map removal", e);
            }
        }
    }

    /// <summary>Hook 3: records the things the loot scatter spawns, so the stockpile thinning never touches them.</summary>
    [HarmonyPatch(typeof(SymbolResolver_LootScatter), nameof(SymbolResolver_LootScatter.Resolve))]
    public static class Patch_SymbolResolver_LootScatter_Resolve
    {
        public static void Prefix(out HashSet<int> __state)
        {
            __state = null;
            try
            {
                TownMaps.LootBefore(BaseGen.globalSettings.map, out __state);
            }
            catch (Exception e)
            {
                __state = null;
                TownMaps.LootFailed("loot before", e);
            }
        }

        public static Exception Finalizer(Exception __exception, HashSet<int> __state)
        {
            try
            {
                TownMaps.LootAfter(BaseGen.globalSettings.map, __state);
            }
            catch (Exception e)
            {
                TownMaps.LootFailed("loot after", e);
            }
            return __exception;
        }
    }
}
