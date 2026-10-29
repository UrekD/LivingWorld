using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    public enum LayResult : byte { Changed, AlreadySatisfied, Invalid }

    public enum RouteReject : byte { None, Invalid, SameTile, Unreachable, TooLong, Unbuildable }

    /// <summary>What a road of this def would do on the link: lay something, nothing (as good or better already), or it cannot be built.</summary>
    public enum EdgeNeed : byte { Needs, Satisfied, Invalid }

    /// <summary>The vanilla road defs this mod builds with (no asphalt: it spawns ancient debris on new maps).</summary>
    [DefOf]
    public static class LWRoadDefOf
    {
        public static RoadDef DirtPath;
        public static RoadDef DirtRoad;
        public static RoadDef StoneRoad;

        static LWRoadDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWRoadDefOf));
    }

    /// <summary>
    /// Roads on the root surface: what can be built where, the single place road links are written (with the edge ledger that
    /// remembers what the mod changed), the NPC building and decay (RoadsNpc.cs), and what a road to the colony does. Main thread
    /// only. Vanilla roads are all equally fast (movementCostMultiplier 0.5): the tiers are looks only.
    /// </summary>
    public static partial class Roads
    {
        /// <summary>Roads are switched on for this world (the setting is the all-off one on a save prepared for removal).</summary>
        public static bool Active => LivingWorldComponent.Instance != null && LivingWorldMod.Settings.roads;

        /// <summary>Bumped on every change to a road link: caches and the redraw follow it.</summary>
        public static int Version { get; private set; }

        private static bool redrawPending, redrawUrgent;
        private static int lastRedrawTick = FactionState.Never;

        public static int StatEdgesLaid, StatEdgesDegraded, StatPlayerEdges, StatRoutesOpened, StatRoutesFinished, StatRoutesAbandoned;
        public static int StatPlanAttempts, StatPlanFailures, StatRedraws;
        /// <summary>The most path searches one planning check has made (the tuning's budget per check).</summary>
        public static int StatMaxAttempts;
        /// <summary>Failed plans by reason (indexed by <see cref="RouteReject"/>): what to look at when calibrating the traffic threshold.</summary>
        public static readonly int[] StatRejects = new int[6];
        /// <summary>Player jobs: ids of those that reached their end, and why the others were cancelled (diagnostics, tests).</summary>
        internal static readonly HashSet<int> FinishedJobs = new HashSet<int>();
        internal static readonly Dictionary<int, string> CancelledJobs = new Dictionary<int, string>();
        /// <summary>Of the last planning check: pairs over the traffic threshold, and the highest traffic.</summary>
        public static int StatOverThreshold;
        public static float StatMaxTraffic;

        internal static RoadState State => LivingWorldComponent.Instance?.roadState;

        /// <summary>A new world: nothing from the previous one may leak through statics.</summary>
        public static void Reset()
        {
            Version = 0;
            redrawPending = redrawUrgent = false;
            lastRedrawTick = FactionState.Never;
            WorldRenderedOverride = null;
            RoadTargeting.StopIfOurs();
            StatEdgesLaid = StatEdgesDegraded = StatPlayerEdges = StatRoutesOpened = StatRoutesFinished = StatRoutesAbandoned = 0;
            StatPlanAttempts = StatPlanFailures = StatRedraws = StatOverThreshold = StatMaxAttempts = 0;
            System.Array.Clear(StatRejects, 0, StatRejects.Length);
            FinishedJobs.Clear();
            CancelledJobs.Clear();
            StatMaxTraffic = 0f;
            ClearCaches();
        }

        /// <summary>The root surface when there is one.</summary>
        internal static PlanetLayer Surface()
        {
            PlanetLayer s = Find.WorldGrid?.Surface;
            return s != null && s.IsRootSurface && s.TilesCount > 0 ? s : null;
        }

        /// <summary>Undirected edge or pair key: (lower id, higher id).</summary>
        public static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        public static long PairKey(int a, int b) => EdgeKey(a, b);

        public static void SplitKey(long key, out int a, out int b)
        {
            a = (int)(key >> 32);
            b = (int)(key & 0xffffffffL);
        }

        // ---- Buildable edges ----

        private static SurfaceTile TileAt(PlanetLayer surface, int id) => (uint)id < (uint)surface.TilesCount ? surface.Tiles[id] as SurfaceTile : null;

        /// <summary>The surface tile id of a tile on the root surface, -1 for the invalid tile, another layer (an orbit tile with the same id) or out of bounds.</summary>
        internal static int SurfaceId(PlanetLayer surface, PlanetTile tile) =>
            surface != null && tile.Valid && Territory.LayerOf(tile) == surface && tile.tileId < surface.TilesCount ? tile.tileId : -1;

        /// <summary>Passable dry land whose biome allows roads.</summary>
        private static bool BuildableTile(PlanetLayer surface, int id)
        {
            SurfaceTile t = TileAt(surface, id);
            return t != null && Territory.Passable(t) && !t.WaterCovered && t.PrimaryBiome.allowRoads;
        }

        /// <summary>One predicate for planning and laying: distinct neighbouring tiles of the root surface, both buildable.</summary>
        public static bool Buildable(PlanetLayer surface, int a, int b) =>
            surface != null && a != b && BuildableTile(surface, a) && BuildableTile(surface, b) && surface.IsNeighbor(a, b);

        private static int Level(RoadDef d) => d?.priority ?? 0;

        /// <summary>The raw link from the tile to the neighbour, hidden ones included (what is saved).</summary>
        private static RoadDef RawLink(SurfaceTile t, int neighbour)
        {
            List<SurfaceTile.RoadLink> list = t.potentialRoads;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
                if (list[i].neighbor.tileId == neighbour) return list[i].road;
            return null;
        }

        /// <summary>The link's road as both tiles hold it, null if either side has none: the weaker of the two.</summary>
        public static RoadDef RoadBetween(int a, int b)
        {
            PlanetLayer surface = Surface();
            SurfaceTile ta = surface == null ? null : TileAt(surface, a), tb = surface == null ? null : TileAt(surface, b);
            if (ta == null || tb == null) return null;
            RoadDef fwd = RawLink(ta, b), rev = RawLink(tb, a);
            return Level(fwd) <= Level(rev) ? fwd : rev;
        }

        /// <summary>
        /// The only writer of road links: both tiles, the old link replaced (null removes it). A link is only ever written between two
        /// distinct neighbouring tiles (anything else would not survive saving); returns false and writes nothing otherwise.
        /// </summary>
        private static bool WriteLink(PlanetLayer surface, int a, int b, RoadDef def, bool urgent = false)
        {
            SurfaceTile ta = TileAt(surface, a), tb = TileAt(surface, b);
            if (ta == null || tb == null || a == b || !surface.IsNeighbor(a, b)) return false;
            ta.potentialRoads?.RemoveAll(rl => rl.neighbor.tileId == b);
            tb.potentialRoads?.RemoveAll(rl => rl.neighbor.tileId == a);
            if (def != null)
            {
                (ta.potentialRoads ??= new List<SurfaceTile.RoadLink>()).Add(new SurfaceTile.RoadLink { neighbor = new PlanetTile(b, surface), road = def });
                (tb.potentialRoads ??= new List<SurfaceTile.RoadLink>()).Add(new SurfaceTile.RoadLink { neighbor = new PlanetTile(a, surface), road = def });
            }
            NoteChange(urgent);
            return true;
        }

        public static EdgeNeed Need(int a, int b, RoadDef def)
        {
            PlanetLayer surface = Surface();
            if (def == null || !Buildable(surface, a, b)) return EdgeNeed.Invalid;
            RoadDef have = RoadBetween(a, b);
            return Level(have) >= def.priority ? EdgeNeed.Satisfied : EdgeNeed.Needs;
        }

        // ---- Planning ----

        /// <summary>
        /// A route of buildable links from one surface tile to another, for <paramref name="tiles"/> (start first). Uses the game's path
        /// search on the main thread (the nodes are copied and the path is released at once), so the way follows existing roads and is
        /// not searched around ice: a path with an unbuildable link is rejected. At most <paramref name="maxEdges"/> links.
        /// </summary>
        public static bool PlanRoute(PlanetTile from, PlanetTile to, int maxEdges, List<int> tiles, out RouteReject reject)
        {
            tiles.Clear();
            reject = RouteReject.None;
            PlanetLayer surface = Surface();
            int a = SurfaceId(surface, from), b = SurfaceId(surface, to);
            if (a < 0 || b < 0) reject = RouteReject.Invalid;
            else if (a == b) reject = RouteReject.SameTile;
            // No search at all for a pair that is too far apart however the way runs (the margin covers uneven tile sizes).
            else if (Find.WorldGrid.ApproxDistanceInTiles(from, to) > maxEdges * 1.25f) reject = RouteReject.TooLong;
            if (reject != RouteReject.None) return false;
            WorldPath path = surface.Pather.FindPath(from, to, null);
            try
            {
                if (path == null || !path.Found) reject = RouteReject.Unreachable;
                else if (path.NodesReversed.Count - 1 > maxEdges) reject = RouteReject.TooLong;
                else
                {
                    // NodesReversed: [0] = destination … [last] = start.
                    List<PlanetTile> nodes = path.NodesReversed;
                    for (int i = nodes.Count - 1; i >= 0; i--) tiles.Add(nodes[i].tileId);
                }
            }
            finally
            {
                path?.ReleaseToPool();
            }
            if (reject != RouteReject.None)
            {
                tiles.Clear();
                return false;
            }
            if (tiles.Count < 2 || tiles[0] != a || tiles[tiles.Count - 1] != b) reject = RouteReject.Unreachable;
            for (int i = 0; reject == RouteReject.None && i + 1 < tiles.Count; i++)
                if (!Buildable(surface, tiles[i], tiles[i + 1])) reject = RouteReject.Unbuildable;
            if (reject == RouteReject.None) return true;
            tiles.Clear();
            return false;
        }

        // ---- Laying, degrading, removing ----

        /// <summary>
        /// Lays the road on the link unless it already has one as good or better. A link the mod changes enters the ledger (with
        /// what was there before) and is held by the route, or by the player when <paramref name="player"/>. Rechecks buildability.
        /// </summary>
        public static LayResult LaySegment(int a, int b, RoadDef def, int routeId = -1, bool player = false)
        {
            RoadState st = State;
            PlanetLayer surface = Surface();
            if (st == null || surface == null) return LayResult.Invalid;
            EdgeNeed need = Need(a, b, def);
            if (need == EdgeNeed.Invalid) return LayResult.Invalid;
            SurfaceTile ta = TileAt(surface, a), tb = TileAt(surface, b);
            RoadDef fwd = RawLink(ta, b), rev = RawLink(tb, a);
            long key = EdgeKey(a, b);
            RoadEdge e = st.Edge(key);
            // Someone else changed the link since: it is no longer ours to lower.
            if (e != null && (e.WrittenDef != fwd || e.WrittenDef != rev))
            {
                st.Drop(e);
                e = null;
            }
            if (need == EdgeNeed.Satisfied) return LayResult.AlreadySatisfied;
            if (!WriteLink(surface, a, b, def, urgent: player)) return LayResult.Invalid;
            if (e == null)
            {
                e = new RoadEdge { key = key, baseline = (fwd ?? rev)?.defName, written = def.defName };
                st.Add(e);
            }
            else e.written = def.defName;
            if (routeId >= 0 && st.Route(routeId) is RoadRoute r && !e.claims.Contains(routeId))
            {
                e.claims.Add(routeId);
                r.claimed.Add(key);
            }
            if (player) e.player = true;
            StatEdgesLaid++;
            if (player) StatPlayerEdges++;
            return LayResult.Changed;
        }

        /// <summary>The road one step below, down to nothing: stone → dirt road → dirt path → none (anything else: none).</summary>
        private static RoadDef Lower(RoadDef d) =>
            d == LWRoadDefOf.StoneRoad ? LWRoadDefOf.DirtRoad : d == LWRoadDefOf.DirtRoad ? LWRoadDefOf.DirtPath : null;

        /// <summary>
        /// Lowers one of the mod's links a tier, never below what was there before the mod. Only while both tiles still hold what the
        /// mod wrote; otherwise the entry is dropped and the link left alone. Returns whether the road changed.
        /// </summary>
        public static bool Downgrade(RoadEdge e)
        {
            RoadState st = State;
            PlanetLayer surface = Surface();
            if (st == null || surface == null) return false;
            SplitKey(e.key, out int a, out int b);
            SurfaceTile ta = TileAt(surface, a), tb = TileAt(surface, b);
            RoadDef written = e.WrittenDef, baseline = e.BaselineDef;
            if (ta == null || tb == null || written == null || RawLink(ta, b) != written || RawLink(tb, a) != written)
            {
                st.Drop(e);
                return false;
            }
            if (Level(written) <= Level(baseline))
            {
                st.Drop(e); // nothing of ours above the original is left
                return false;
            }
            RoadDef next = Lower(written);
            if (Level(next) <= Level(baseline)) next = baseline;
            if (next == null) return Remove(e);
            if (!WriteLink(surface, a, b, next))
            {
                st.Drop(e);
                return false;
            }
            StatEdgesDegraded++;
            if (next == baseline) st.Drop(e);
            else e.written = next.defName;
            return true;
        }

        /// <summary>Takes the mod's own link away (only where nothing was before, and only while both tiles still hold what it wrote).</summary>
        public static bool Remove(RoadEdge e)
        {
            RoadState st = State;
            PlanetLayer surface = Surface();
            if (st == null || surface == null) return false;
            SplitKey(e.key, out int a, out int b);
            SurfaceTile ta = TileAt(surface, a), tb = TileAt(surface, b);
            RoadDef written = e.WrittenDef;
            bool ours = ta != null && tb != null && written != null && e.baseline == null && RawLink(ta, b) == written && RawLink(tb, a) == written;
            st.Drop(e);
            if (!ours || !WriteLink(surface, a, b, null)) return false;
            StatEdgesDegraded++;
            return true;
        }

        /// <summary>Puts the link back to the def (null: none) and forgets its ledger entry: for tests that clean up after themselves.</summary>
        internal static void RestoreLink(int a, int b, RoadDef def)
        {
            PlanetLayer surface = Surface();
            if (surface == null || !surface.IsNeighbor(a, b)) return;
            if (State?.Edge(EdgeKey(a, b)) is RoadEdge e) State.Drop(e);
            WriteLink(surface, a, b, def);
        }

        // ---- Costs ----

        /// <summary>Silver per laid link and terrain: the harder of the two tiles' hilliness counts.</summary>
        public static float TerrainFactor(int a, int b)
        {
            PlanetLayer surface = Surface();
            SurfaceTile ta = surface == null ? null : TileAt(surface, a), tb = surface == null ? null : TileAt(surface, b);
            int hill = Mathf.Max(ta == null ? 0 : (int)ta.hilliness, tb == null ? 0 : (int)tb.hilliness);
            return 1f + Tuning.T.roadTerrainStep * Mathf.Max(0, hill - (int)Hilliness.Flat);
        }

        public static float NpcCost(RoadDef def, int a, int b) =>
            (def == LWRoadDefOf.StoneRoad ? Tuning.T.roadCostStone : Tuning.T.roadCostDirt) * TerrainFactor(a, b);

        // ---- Redraw ----

        /// <summary>A link changed: the layer is due a redraw, at once when it is a player's, else when the throttle allows (see <see cref="FlushRedraw()"/>).</summary>
        internal static void NoteChange(bool urgent)
        {
            Version++;
            redrawPending = true;
            if (urgent) redrawUrgent = true;
        }

        /// <summary>Tests: pretend the world view is (not) being rendered; null = ask the game.</summary>
        internal static bool? WorldRenderedOverride;

        /// <summary>The planet view of the root surface is what is drawn (the roads layer is not drawn in the background or with an orbit layer selected).</summary>
        private static bool WorldViewShown => WorldRenderedOverride ?? (WorldRendererUtility.WorldSelected && PlanetLayer.Selected is PlanetLayer l && l.IsRootSurface);

        /// <summary>Tests: forget when the layer was last invalidated, so the next NPC change flushes at once.</summary>
        internal static void ResetRedrawThrottle()
        {
            redrawPending = redrawUrgent = false;
            lastRedrawTick = FactionState.Never;
        }

        /// <summary>
        /// The roads layer is rebuilt in full when it is next visible, and vanilla's layer cannot do less than the whole planet (hundreds
        /// of ms on a big one). Called once per tick by the component before its hourly early return: a burst of changes costs one
        /// rebuild, history one after it ends. A player's new link flushes at once. NPC changes only mark the layer dirty at most once per
        /// <see cref="LivingWorldTuningDef.roadRedrawMinDays"/> while the world view is drawn (the rebuild happens right then, in the
        /// frame); while it is not drawn they mark it dirty at once, which is free: the one rebuild comes when the view is opened.
        /// </summary>
        public static void FlushRedraw() => FlushRedraw(Find.TickManager.TicksGame);

        internal static void FlushRedraw(int now)
        {
            if (!redrawPending || SimClock.InHistory) return;
            if (!redrawUrgent && WorldViewShown && now >= lastRedrawTick && now - lastRedrawTick < Tuning.T.roadRedrawMinDays * GenDate.TicksPerDay) return;
            redrawPending = redrawUrgent = false;
            lastRedrawTick = now;
            InvalidateLayer();
        }

        private static void InvalidateLayer()
        {
            if (Current.ProgramState == ProgramState.Playing && Find.World != null) Find.WorldGrid?.Surface?.SetDirty<WorldDrawLayer_Roads>();
            StatRedraws++;
        }

        /// <summary>History made many changes without drawing: one invalidation now.</summary>
        public static void Notify_HistoryEnded()
        {
            NoteChange(urgent: false);
        }

        // ---- What a road to the colony does ----

        private static readonly Dictionary<int, HashSet<int>> reach = new Dictionary<int, HashSet<int>>();
        private static readonly List<int> reachKey = new List<int>(), reachKeyNow = new List<int>();
        private static readonly List<int> bfsTiles = new List<int>(), bfsDepth = new List<int>();
        private static World reachWorld;
        private static int reachVersion = -1, reachDay = -1;

        private static void ClearCaches()
        {
            reach.Clear();
            reachKey.Clear();
            reachWorld = null;
            reachVersion = -1;
            reachDay = -1;
        }

        /// <summary>Player homes on the root surface (maps with a valid tile there), in the game's map order.</summary>
        private static void PlayerHomes(PlanetLayer surface, List<Map> into)
        {
            into.Clear();
            List<Map> maps = Find.Maps;
            if (maps == null || surface == null) return;
            foreach (Map m in maps)
                if (m.IsPlayerHome && SurfaceId(surface, m.Tile) >= 0) into.Add(m);
        }

        private static readonly List<Map> homesTmp = new List<Map>();

        /// <summary>
        /// Per player home, the tiles its visible, passable road links lead to within the tuning's number of links. Rebuilt when the
        /// road version, the set of homes (maps and tiles), the day or the world changes; nothing else reads or writes the cache.
        /// </summary>
        private static void EnsureReach()
        {
            PlanetLayer surface = Surface();
            World world = Find.World;
            int day = Find.TickManager.TicksGame / GenDate.TicksPerDay;
            PlayerHomes(surface, homesTmp);
            reachKeyNow.Clear();
            foreach (Map m in homesTmp)
            {
                reachKeyNow.Add(m.uniqueID);
                reachKeyNow.Add(m.Tile.tileId);
            }
            reachKeyNow.Add(Tuning.T.roadConnectEdges);
            if (reachWorld == world && reachVersion == Version && reachDay == day && SameKey(reachKey, reachKeyNow)) return;
            reachWorld = world;
            reachVersion = Version;
            reachDay = day;
            reachKey.Clear();
            reachKey.AddRange(reachKeyNow);
            reach.Clear();
            foreach (Map m in homesTmp)
            {
                var set = new HashSet<int>();
                Follow(surface, m.Tile.tileId, Tuning.T.roadConnectEdges, set, null);
                reach[m.uniqueID] = set;
            }
        }

        private static bool SameKey(List<int> a, List<int> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// Tiles reached from the start along visible road links onto passable tiles, at most <paramref name="maxEdges"/> links away;
        /// links whose key is in <paramref name="excluded"/> (when given) count as absent.
        /// </summary>
        private static void Follow(PlanetLayer surface, int start, int maxEdges, HashSet<int> into, HashSet<long> excluded)
        {
            into.Add(start);
            bfsTiles.Clear();
            bfsDepth.Clear();
            bfsTiles.Add(start);
            bfsDepth.Add(0);
            for (int head = 0; head < bfsTiles.Count; head++)
            {
                if (bfsDepth[head] >= maxEdges) continue;
                List<SurfaceTile.RoadLink> links = TileAt(surface, bfsTiles[head])?.Roads;
                if (links == null) continue;
                for (int i = 0; i < links.Count; i++)
                {
                    int n = links[i].neighbor.tileId;
                    if (links[i].road == null || into.Contains(n) || (uint)n >= (uint)surface.TilesCount || !Territory.Passable(surface.Tiles[n])) continue;
                    if (excluded != null && excluded.Contains(EdgeKey(bfsTiles[head], n))) continue;
                    into.Add(n);
                    bfsTiles.Add(n);
                    bfsDepth.Add(bfsDepth[head] + 1);
                }
            }
        }

        /// <summary>The settlement is joined to this colony by road: a chain of visible road links from the colony's tile to its own. No side effects.</summary>
        public static bool Connected(Map home, Settlement s)
        {
            if (!Active || home == null || s == null || s.Destroyed) return false;
            EnsureReach();
            return InReach(home, s);
        }

        private static bool InReach(Map home, Settlement s)
        {
            int id = SurfaceId(Surface(), s.Tile);
            return id >= 0 && id != home.Tile.tileId && reach.TryGetValue(home.uniqueID, out HashSet<int> set) && set.Contains(id);
        }

        /// <summary>The first colony (by map order) the settlement is joined to, null if none.</summary>
        public static Map JoinedHome(Settlement s)
        {
            if (!Active || s == null) return null;
            EnsureReach();
            foreach (Map m in homesTmp)
                if (!s.Destroyed && InReach(m, s)) return m;
            return null;
        }

        /// <summary>Neighbours' arrival score factor for a town joined to the colony that the arrival is for.</summary>
        public static float NeighbourFactor(Map home, Settlement s) => Connected(home, s) ? Tuning.T.roadNeighbourFactor : 1f;

        /// <summary>The settlement inspect line, null when it has none to show.</summary>
        public static string InspectLine(Settlement s)
        {
            Map home = JoinedHome(s);
            return home == null ? null : "LW_Inspect_RoadJoined".Translate(home.Parent?.LabelCap ?? "?");
        }

        /// <summary>
        /// Every ten days a friendly town joined to a colony thinks a little better of it (once per town, whatever the number of
        /// colonies), up to a ceiling: fame above the ceiling is never lowered. Roads may rehabilitate a bad name this way.
        /// </summary>
        internal static void FameDaily(LivingWorldComponent comp, RoadState st, int now)
        {
            LivingWorldTuningDef t = Tuning.T;
            if (now - st.lastFameTick < t.roadFameDays * GenDate.TicksPerDay) return;
            st.lastFameTick = now;
            foreach (CompSettlementLife s in comp.activeSettlements)
            {
                if (!s.Active || s.fame >= t.roadFameCeiling) continue;
                Faction f = s.Settlement.Faction;
                if (f.HostileTo(Faction.OfPlayer) || f.def.permanentEnemy || JoinedHome(s.Settlement) == null) continue;
                s.fame = Mathf.Min(t.roadFameCeiling, s.fame + t.roadFameGain);
            }
        }

        /// <summary>Most town names one road's chronicle entry lists; the rest are counted ("and N more").</summary>
        private const int ChronicleTownNames = 3;

        /// <summary>
        /// A player route finished. <paramref name="newEdges"/> are the links the job made out of nothing (keys): only they can join a town
        /// to the colony that was not joined without them, so a town that was already joined (vanilla roads, another builder's work) or a
        /// job that only upgraded roads earns nothing. Each town is counted once, ever; the job gets one chronicle entry naming up to
        /// <see cref="ChronicleTownNames"/> of its towns, and the colonist who led it one deed.
        /// </summary>
        internal static void Notify_PlayerRouteFinished(Pawn leader, IReadOnlyCollection<long> newEdges)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            RoadState st = State;
            if (!Active || newEdges == null || newEdges.Count == 0 || comp == null || st == null) return;
            EnsureReach();
            PlanetLayer surface = Surface();
            var mine = new HashSet<long>(newEdges);
            var without = new Dictionary<int, HashSet<int>>();
            var names = new List<string>();
            Settlement firstTown = null;
            Map firstHome = null;
            foreach (CompSettlementLife s in comp.activeSettlements)
            {
                Settlement town = s.Settlement;
                if (!s.Active || st.joinedTowns.Contains(town.ID) || Hostile(comp, town.Faction, Faction.OfPlayer)) continue;
                int townTile = SurfaceId(surface, town.Tile);
                Map home = null;
                foreach (Map m in homesTmp)
                {
                    if (!InReach(m, town)) continue;
                    if (!without.TryGetValue(m.uniqueID, out HashSet<int> set))
                    {
                        set = new HashSet<int>();
                        Follow(surface, m.Tile.tileId, Tuning.T.roadConnectEdges, set, mine);
                        without[m.uniqueID] = set;
                    }
                    if (set.Contains(townTile)) continue; // joined without this job's links
                    home = m;
                    break;
                }
                if (home == null) continue;
                st.joinedTowns.Add(town.ID);
                names.Add(town.Label);
                firstTown ??= town;
                firstHome ??= home;
            }
            if (firstTown == null) return;
            string list = TownList(names);
            ChronicleEventDef def = LWChronicleDefOf.LW_ColonyRoadJoined;
            comp.chronicle.Record(def, def.text.Formatted((firstHome.Parent?.Label ?? "?").Named("COLONY"), list.Named("SETTLEMENT")).Resolve(),
                Faction.OfPlayer, firstTown.Faction, firstTown);
            if (leader != null) Legends.Add(leader, "LW_Roads_Deed".Translate(list));
        }

        /// <summary>"A", "A and B", "A, B and C", "A, B, C and 2 more".</summary>
        internal static string TownList(List<string> names)
        {
            if (names.Count <= ChronicleTownNames) return names.ToCommaList(useAnd: true);
            return string.Join(", ", names.GetRange(0, ChronicleTownNames)) + " " + "LW_Roads_AndMore".Translate(names.Count - ChronicleTownNames);
        }

        // ---- Settings, removal ----

        /// <summary>
        /// "Prepare for removal" or switching roads off: every caravan job is cancelled at once (its own movement stopped), a pending
        /// road targeter stopped, caches forgotten. Built roads stay.
        /// </summary>
        public static void Notify_PreparedForRemoval()
        {
            CancelAllJobs();
            ClearCaches();
            if (!redrawPending) return;
            // A change still waiting for the throttle (or a player's last link) must reach the layer: this session may never flush again.
            redrawPending = redrawUrgent = false;
            InvalidateLayer();
        }

        public static void Notify_RoadsSettingChanged()
        {
            if (LivingWorldMod.Settings.roads) ClearCaches();
            else Notify_PreparedForRemoval();
        }

        internal static void CancelAllJobs()
        {
            RoadTargeting.StopIfOurs();
            if (Current.ProgramState != ProgramState.Playing || Find.World == null) return;
            foreach (Caravan c in Find.WorldObjects.Caravans)
                c.GetComponent<CompRoadBuilder>()?.Cancel(stopOwnMovement: true, messageKey: null);
        }

        /// <summary>One line for logs and metrics.</summary>
        public static string Summary()
        {
            RoadState st = State;
            if (!Active || st == null) return "roads off";
            int open = 0, blocked = 0, finished = 0, abandoned = 0;
            foreach (RoadRoute r in st.routes)
            {
                switch (r.state)
                {
                    case RouteState.Open: open++; break;
                    case RouteState.Blocked: blocked++; break;
                    case RouteState.Finished: finished++; break;
                    default: abandoned++; break;
                }
            }
            return $"roads routes open/blocked/finished/abandoned={open}/{blocked}/{finished}/{abandoned} opened={StatRoutesOpened} finished={StatRoutesFinished} abandoned={StatRoutesAbandoned} " +
                   $"edgesLaid={StatEdgesLaid} (player {StatPlayerEdges}) edgesDegraded={StatEdgesDegraded} ledger={st.edges.Count} traffic={st.traffic.Count} overThreshold={StatOverThreshold} " +
                   $"maxTraffic={StatMaxTraffic:F0} planAttempts={StatPlanAttempts} planFailures={StatPlanFailures} (unreachable/tooLong/unbuildable={StatRejects[(int)RouteReject.Unreachable]}/" +
                   $"{StatRejects[(int)RouteReject.TooLong]}/{StatRejects[(int)RouteReject.Unbuildable]}) maxAttempts={StatMaxAttempts} redraws={StatRedraws} " +
                   $"playerJobs finished/cancelled={FinishedJobs.Count}/{CancelledJobs.Count}";
        }
    }
}
