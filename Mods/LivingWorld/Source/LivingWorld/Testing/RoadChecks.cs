using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Roads. <see cref="Controlled"/> builds its own situations at the end of a run (ledger rules on a bare chain of tiles, the
    /// planner's rejections, an NPC route built and decayed in simulated days, effects and caches, who owns a caravan's walk, the
    /// progress gate, real merges, roads off, and last the real "prepare for removal"); each case restores what it touched. The normal
    /// plan also runs four caravans of temporary colonists through the real click path while the game ticks, each with milestones that
    /// must all be reached within a set allowance: a dirt road held at link 1 and then put into its travelling phase for the halfway
    /// save, which must really move on after the reload; a stone road that finishes and pays exactly its blocks (one link already
    /// stone: free); a stone road that waits for materials across the reload and goes on when they come; and a road job that an
    /// ordinary move order cancels. The world's links, ledger, routes and jobs must come back from the reload exactly. The test
    /// caravans are not intercepted by war bands (<c>Interception.TryEncounter</c>: an encounter map never finishes generating under
    /// -headless, which was the confirmed cause of the normal plan's hang) and, as defence in depth, are not storyteller targets either
    /// (a caravan incident opens dialogs and the world view). They are removed with their colonists as soon as their case is judged,
    /// and every case has a deadline, so nothing here can hold the run up.
    /// </summary>
    public static class RoadChecks
    {
        private const string Tag = "[LivingWorld:test] roads: ";
        private static readonly List<PlanetTile> neighbourBuf = new List<PlanetTile>();
        private static readonly List<string> liveProblems = new List<string>();
        private static string snapshot;
        private static LivingWorldTuningDef savedTuning;
        private static float savedWork;
        private static readonly List<Pawn> keptPawns = new List<Pawn>();
        private static readonly HashSet<string> testPawnIds = new HashSet<string>();

        /// <summary>A caravan of the road tests' temporary colonists (kept out of the storyteller's incident targets).</summary>
        internal static bool IsTestCaravan(Caravan c)
        {
            if (testPawnIds.Count == 0) return false;
            foreach (Pawn p in c.PawnsListForReading)
                if (testPawnIds.Contains(p.ThingID)) return true;
            return false;
        }

        /// <summary>
        /// Takes a test caravan out of the world with its colonists: they leave the caravan first (a destroyed caravan would stay their
        /// holder, un-fed, in the world pawns), then they are destroyed and discarded.
        /// </summary>
        internal static void DisposeCaravan(Caravan c)
        {
            if (c == null || c.Destroyed) return;
            var pawns = new List<Pawn>(c.PawnsListForReading);
            c.RemoveAllPawns();
            c.Destroy();
            foreach (Pawn p in pawns)
            {
                if (!p.Destroyed) p.Destroy();
                if (!p.Discarded) p.Discard(silentlyRemoveReferences: true);
            }
        }

        /// <summary>Where a job stands, for telling whether it moved: a change of phase alone is not progress.</summary>
        private readonly struct Progress
        {
            public readonly int tile, index;
            public readonly float work;
            public readonly RoadPhase phase;

            public Progress(int tile, int index, float work, RoadPhase phase)
            {
                this.tile = tile;
                this.index = index;
                this.work = work;
                this.phase = phase;
            }

            public static Progress Of(Caravan c, CompRoadBuilder comp) => new Progress(c.Tile.tileId, comp.HasJob ? comp.JobIndex : -1, comp.JobWork, comp.Phase);

            /// <summary>The caravan changed tile, the job went on to a later link, or work grew on the same link while working.</summary>
            public static bool Advanced(Progress then, Progress now) =>
                now.tile != then.tile || now.index > then.index
                || (now.index == then.index && then.phase == RoadPhase.Working && now.phase == RoadPhase.Working && now.work > then.work);

            public override string ToString() => $"{phase} at link {index}, tile {tile}, work {work:F0}";
        }

        /// <summary>An arrival action that only counts how often it ran.</summary>
        private sealed class CountingArrival : CaravanArrivalAction
        {
            public int arrived;
            public override string Label => "test";
            public override string ReportString => "test";
            public override void Arrived(Caravan caravan) => arrived++;
        }

        public static void Register()
        {
            if (Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") == "1") return;
            AutoTest.DailyMetrics.Add(() => Roads.Summary());
            AutoTest.Checks.Add(Controlled);
            AutoTest.Checks.Add(LiveProblems);
            AutoTest.BeforeSave.Add(() =>
            {
                if (AutoTest.Plan == "normal") Live.Notify_BeforeSave(); // puts the held job into its travelling phase: the snapshot is of that state
                snapshot = Snapshot();
                Log.Message($"{Tag}before save: {snapshot}");
            });
            AutoTest.AfterReload.Add(() =>
            {
                string after = Snapshot();
                if (after != snapshot) liveProblems.Add($"roads: save/load round trip changed state: before [{snapshot}] after [{after}]");
                else Log.Message($"{Tag}round trip ok: {after}");
                Live.Notify_Reloaded();
            });
            if (AutoTest.Plan == "normal")
            {
                AutoTest.Hourly.Add(Live.Hourly);
                AutoTest.Checks.Insert(0, Live.Finish); // before Controlled: it puts the test tuning back
            }
        }

        // ---- Round-trip snapshot ----

        private static long Mix(long h, long v) => unchecked((h ^ v) * 1099511628211L);

        private static long Hash(string s)
        {
            long h = unchecked((long)14695981039346656037UL);
            if (s != null)
                foreach (char c in s) h = Mix(h, c);
            return h;
        }

        /// <summary>
        /// The ledger, the routes, every traffic pair and value, every backoff deadline, the joined towns, every road link on the planet
        /// (order-independent sums), and each caravan job's saved fields. Compared before the halfway save and after the reload.
        /// </summary>
        private static string Snapshot()
        {
            RoadState st = Roads.State;
            if (st == null) return "no state";
            long edgeHash = 0, routeHash = 0, linkHash = 0, jobHash = 0, trafficHash = 0, backoffHash = 0;
            foreach (RoadEdge e in st.edges)
                edgeHash += Mix(Mix(Mix(Mix(e.key, Hash(e.baseline)), Hash(e.written)), e.player ? 1 : 0), e.claims.Aggregate(7L, (h, id) => Mix(h, id)));
            foreach (RoadRoute r in st.routes)
            {
                long h = Mix(Mix(Mix(Mix(r.id, r.factionId), r.builderId), r.otherId), Hash(r.def));
                h = Mix(Mix(Mix(Mix(h, r.next), (long)r.state), r.blockedSince), (long)(r.condition * 1000f));
                h = Mix(Mix(Mix(h, r.decaying ? 1 : 0), r.changed), r.tiles.Aggregate(3L, (a, t) => Mix(a, t)));
                routeHash += Mix(h, r.claimed.Aggregate(5L, (a, k) => Mix(a, k)));
            }
            int links = 0;
            PlanetLayer surface = Find.WorldGrid.Surface;
            for (int t = 0; t < surface.TilesCount; t++)
            {
                if (!(surface.Tiles[t] is SurfaceTile tile) || tile.potentialRoads == null) continue;
                foreach (SurfaceTile.RoadLink l in tile.potentialRoads)
                {
                    links++;
                    linkHash += Mix(Mix(Mix(1L, t), l.neighbor.tileId), Hash(l.road.defName));
                }
            }
            foreach (KeyValuePair<long, float> kv in st.traffic) trafficHash += Mix(Mix(3L, kv.Key), (long)Math.Round(kv.Value * 1000d));
            foreach (KeyValuePair<long, int> kv in st.backoff) backoffHash += Mix(Mix(5L, kv.Key), kv.Value);
            long joinedHash = st.joinedTowns.Aggregate(13L, (h, id) => Mix(h, id));
            var jobs = new List<string>();
            foreach (Caravan c in Find.WorldObjects.Caravans)
            {
                CompRoadBuilder comp = c.GetComponent<CompRoadBuilder>();
                if (comp == null || !comp.HasJob) continue;
                jobs.Add($"{comp.JobId}:{comp.Phase}:{comp.JobIndex}/{comp.JobEdges}:{comp.JobDef?.defName}:work={comp.JobWork:R}:changed={comp.JobChanged}:retries={comp.JobRetries}:owned={comp.JobOwnsMove}" +
                         $":new={(comp.JobNewEdges ?? new List<long>()).Aggregate(3L, (a, k) => Mix(a, k))}:tiles={comp.JobTiles.Aggregate(3L, (a, t) => Mix(a, t))}");
            }
            jobs.Sort(StringComparer.Ordinal);
            foreach (string j in jobs) jobHash = Mix(jobHash, Hash(j));
            return $"edges={st.edges.Count}/{edgeHash} routes={st.routes.Count}/{routeHash} traffic={st.traffic.Count}/{trafficHash} backoff={st.backoff.Count}/{backoffHash} joined={st.joinedTowns.Count}/{joinedHash} " +
                   $"timers={st.lastCheckTick}/{st.lastDegradeTick}/{st.lastFameTick} links={links}/{linkHash} jobs=[{string.Join("; ", jobs)}]";
        }

        // ---- Controlled cases (end of the run) ----

        public static IEnumerable<string> Controlled()
        {
            var bad = new List<string>();
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (c == null || Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") == "1") return bad;
            if (!LivingWorldMod.Settings.roads)
            {
                Guard(bad, "roads off world", () => RoadsOffWorld(bad, c));
                return bad;
            }
            // The plans that run the whole simulation; the scenario plans (intercept, ambush, …) keep their own worlds undisturbed.
            if (AutoTest.Plan != "normal" && AutoTest.Plan != "history" && AutoTest.Plan != "stress") return bad;
            int tick = Find.TickManager.TicksGame;
            try
            {
                Guard(bad, "link integrity", () => LinkIntegrity(bad));
                Guard(bad, "planner", () => Planner(bad, c));
                Guard(bad, "ledger", () => Ledger(bad, c));
                Guard(bad, "overlapping claims", () => Claims(bad, c));
                Guard(bad, "npc route", () => NpcRoute(bad, c));
                Guard(bad, "object index", () => ObjectIndex(bad, c));
                Guard(bad, "traffic", () => Traffic(bad, c));
                Guard(bad, "effects", () => Effects(bad, c));
                Guard(bad, "roads off", () => RoadsOff(bad, c));
                Guard(bad, "travel ownership", () => TravelOwnership(bad));
                Guard(bad, "progress gate", () => ProgressGate(bad));
                Guard(bad, "merge and split", () => MergeAndSplit(bad));
                Guard(bad, "player link drawn", () => PlayerLinkDrawn(bad));
                Guard(bad, "redraw", () => Redraw(bad));
                Guard(bad, "removal", () => Removal(bad, c)); // last: it switches this save off and back on
            }
            finally
            {
                Log.Message($"{Tag}{Roads.Summary()}");
            }
            if (Find.TickManager.TicksGame != tick) bad.Add("roads: the tick moved during the controlled cases");
            if (Roads.StatMaxAttempts > Tuning.T.roadPlanAttempts) bad.Add($"roads: one planning check made {Roads.StatMaxAttempts} path searches (budget {Tuning.T.roadPlanAttempts})");
            return bad;
        }

        /// <summary>One case failing (even by exception) must not stop the others.</summary>
        private static void Guard(List<string> bad, string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                bad.Add($"roads ({name}): {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>What the live caravans and the reload comparison found (collected while the game ran).</summary>
        private static IEnumerable<string> LiveProblems() => liveProblems.ToList();

        // ---- Links ----

        private static SurfaceTile TileOf(PlanetLayer surface, int id) => surface.Tiles[id] as SurfaceTile;

        /// <summary>Every raw link on the planet joins two distinct neighbours, is present on both tiles with the same def, and appears once.</summary>
        private static void LinkIntegrity(List<string> bad)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            int asym = 0, nonAdjacent = 0, self = 0, dup = 0, links = 0;
            for (int t = 0; t < surface.TilesCount; t++)
            {
                SurfaceTile tile = TileOf(surface, t);
                if (tile?.potentialRoads == null) continue;
                var seen = new HashSet<int>();
                foreach (SurfaceTile.RoadLink l in tile.potentialRoads)
                {
                    links++;
                    int n = l.neighbor.tileId;
                    if (n == t) self++;
                    else if ((uint)n >= (uint)surface.TilesCount || !surface.IsNeighbor(t, n)) nonAdjacent++;
                    else
                    {
                        if (!seen.Add(n)) dup++;
                        SurfaceTile other = TileOf(surface, n);
                        bool back = false;
                        if (other?.potentialRoads != null)
                            foreach (SurfaceTile.RoadLink o in other.potentialRoads)
                                if (o.neighbor.tileId == t && o.road == l.road) back = true;
                        if (!back) asym++;
                    }
                }
            }
            if (asym + nonAdjacent + self + dup > 0)
                bad.Add($"roads (links): {asym} without a matching link back, {nonAdjacent} to non-neighbours, {self} to themselves, {dup} duplicates of {links}");
            Log.Message($"{Tag}{links} road links checked");
        }

        // ---- Finding bare ground ----

        /// <summary>A chain of <paramref name="tiles"/> distinct neighbouring buildable tiles with no road links on or between them.</summary>
        private static bool FindChain(PlanetLayer surface, int tiles, out List<int> chain, int skip = 0)
        {
            chain = null;
            int found = 0;
            for (int start = 0; start < surface.TilesCount; start += 3)
            {
                var c = new List<int> { start };
                if (!BareTile(surface, start)) continue;
                while (c.Count < tiles)
                {
                    surface.GetTileNeighbors(c[c.Count - 1], neighbourBuf);
                    int next = -1;
                    foreach (PlanetTile n in neighbourBuf)
                    {
                        if (c.Contains(n.tileId) || !BareTile(surface, n.tileId) || !Roads.Buildable(surface, c[c.Count - 1], n.tileId) || Roads.RoadBetween(c[c.Count - 1], n.tileId) != null) continue;
                        next = n.tileId;
                        break;
                    }
                    if (next < 0) break;
                    c.Add(next);
                }
                if (c.Count < tiles || found++ < skip) continue;
                chain = c;
                return true;
            }
            return false;
        }

        /// <summary>Two bare, buildable tiles that are not neighbours but share a neighbour (so the pair is as close as a wrong link could be).</summary>
        private static bool FindNonNeighbours(PlanetLayer surface, out int a, out int b)
        {
            a = b = -1;
            var around = new List<PlanetTile>();
            var next = new List<PlanetTile>();
            for (int start = 0; start < surface.TilesCount; start += 3)
            {
                if (!BareTile(surface, start)) continue;
                surface.GetTileNeighbors(start, around);
                foreach (PlanetTile n in around)
                {
                    if (!BareTile(surface, n.tileId)) continue;
                    surface.GetTileNeighbors(n.tileId, next);
                    foreach (PlanetTile m in next)
                    {
                        if (m.tileId == start || !BareTile(surface, m.tileId) || surface.IsNeighbor(start, m.tileId)) continue;
                        a = start;
                        b = m.tileId;
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool BareTile(PlanetLayer surface, int t) => TileOf(surface, t) is SurfaceTile tile && (tile.potentialRoads == null || tile.potentialRoads.Count == 0)
                                                                  && !tile.WaterCovered && Territory.Passable(tile) && tile.PrimaryBiome.allowRoads;

        private static RoadDef Raw(PlanetLayer surface, int a, int b)
        {
            SurfaceTile t = TileOf(surface, a);
            if (t?.potentialRoads == null) return null;
            foreach (SurfaceTile.RoadLink l in t.potentialRoads)
                if (l.neighbor.tileId == b) return l.road;
            return null;
        }

        private static bool Symmetric(PlanetLayer surface, int a, int b) => Raw(surface, a, b) == Raw(surface, b, a);

        // ---- Planner ----

        private static void Planner(List<string> bad, LivingWorldComponent c)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            var tiles = new List<int>();
            // Rejections: the same tile, the invalid tile, out of bounds, another layer's tile with a surface id.
            var start = new PlanetTile(0, surface);
            if (Roads.PlanRoute(start, start, 30, tiles, out RouteReject r) || r != RouteReject.SameTile) bad.Add($"roads (planner): the same tile was {r}, not SameTile");
            if (Roads.PlanRoute(start, PlanetTile.Invalid, 30, tiles, out r) || r != RouteReject.Invalid) bad.Add($"roads (planner): the invalid tile was {r}, not Invalid");
            if (Roads.PlanRoute(start, new PlanetTile(surface.TilesCount, surface), 30, tiles, out r) || r != RouteReject.Invalid) bad.Add($"roads (planner): an out-of-bounds tile was {r}, not Invalid");
            foreach (KeyValuePair<int, PlanetLayer> kv in Find.WorldGrid.PlanetLayers)
            {
                if (kv.Value == surface) continue;
                var orbit = new PlanetTile(1, kv.Value);
                if (Roads.PlanRoute(start, orbit, 30, tiles, out r) || r != RouteReject.Invalid) bad.Add($"roads (planner): layer {kv.Key}'s tile with a surface id was {r}, not Invalid");
                break;
            }
            // A route between two neighbours of a town: contiguous, all links buildable, both ends right, never longer than allowed.
            if (!FindChain(surface, 8, out List<int> chain)) Log.Message(Tag + "no bare land for the planner cases");
            else
            {
                var from = new PlanetTile(chain[0], surface);
                var to = new PlanetTile(chain[chain.Count - 1], surface);
                if (!Roads.PlanRoute(from, to, 30, tiles, out r)) Log.Message($"{Tag}no route along the chain ({r}): contiguity case skipped");
                else
                {
                    if (tiles[0] != chain[0] || tiles[tiles.Count - 1] != chain[chain.Count - 1]) bad.Add("roads (planner): the route does not run from start to end");
                    for (int i = 0; i + 1 < tiles.Count; i++)
                        if (!Roads.Buildable(surface, tiles[i], tiles[i + 1])) bad.Add($"roads (planner): link {i} of the route is not buildable");
                    if (tiles.Distinct().Count() != tiles.Count) bad.Add("roads (planner): the route visits a tile twice");
                    if (Roads.PlanRoute(from, to, tiles.Count - 2, tiles, out r) || r != RouteReject.TooLong) bad.Add($"roads (planner): a route over the limit was {r}, not TooLong");
                    if (tiles.Count != 0) bad.Add("roads (planner): a rejected route left tiles behind");
                }
            }
            // Land that cannot take a road: water, and land whose biome forbids roads (ice sheets, sea ice).
            int water = -1, landBesideWater = -1, noRoad = -1, landBesideNoRoad = -1;
            for (int t = 0; t < surface.TilesCount && (water < 0 || noRoad < 0); t++)
            {
                SurfaceTile tile = TileOf(surface, t);
                if (tile == null) continue;
                bool isWater = tile.WaterCovered, isNoRoad = !isWater && Territory.Passable(tile) && !tile.PrimaryBiome.allowRoads;
                if (!isWater && !isNoRoad) continue;
                surface.GetTileNeighbors(t, neighbourBuf);
                foreach (PlanetTile n in neighbourBuf)
                    if (BareTile(surface, n.tileId))
                    {
                        if (isWater && water < 0) { water = t; landBesideWater = n.tileId; }
                        if (isNoRoad && noRoad < 0) { noRoad = t; landBesideNoRoad = n.tileId; }
                        break;
                    }
            }
            if (water >= 0)
            {
                if (Roads.Buildable(surface, landBesideWater, water)) bad.Add("roads (planner): a link into water is buildable");
                if (Roads.LaySegment(landBesideWater, water, LWRoadDefOf.DirtRoad) != LayResult.Invalid || Roads.RoadBetween(landBesideWater, water) != null) bad.Add("roads (planner): a road was laid into water");
            }
            if (noRoad >= 0)
            {
                if (Roads.Buildable(surface, landBesideNoRoad, noRoad)) bad.Add("roads (planner): a link onto land that forbids roads (ice) is buildable");
                if (Roads.LaySegment(landBesideNoRoad, noRoad, LWRoadDefOf.DirtRoad) != LayResult.Invalid || Roads.RoadBetween(landBesideNoRoad, noRoad) != null) bad.Add("roads (planner): a road was laid onto land that forbids roads");
            }
            else Log.Message(Tag + "no ice or other road-less land in this world: the ice case is skipped");
            // Not neighbours (two bare tiles with a tile between them that are no neighbours of each other: a chain of three on this grid
            // can close into a triangle, so its ends are not a safe pair), the same tile twice, and an unfit def.
            RoadState st = c.roadState;
            if (FindNonNeighbours(surface, out int far0, out int far1))
            {
                int version = Roads.Version, entries = st.edges.Count;
                if (surface.IsNeighbor(far0, far1)) bad.Add("roads (planner): the fixture for the non-neighbour case is two neighbours");
                if (Roads.Buildable(surface, far0, far1)) bad.Add("roads (planner): a link between tiles that are not neighbours is buildable");
                if (Roads.LaySegment(far0, far1, LWRoadDefOf.DirtRoad) != LayResult.Invalid || Roads.RoadBetween(far0, far1) != null || Raw(surface, far0, far1) != null || Raw(surface, far1, far0) != null
                    || Roads.Version != version || st.edges.Count != entries)
                    bad.Add("roads (planner): a link between tiles that are not neighbours was laid");
            }
            else Log.Message(Tag + "no pair of bare non-neighbours for the non-neighbour case");
            if (FindChain(surface, 2, out List<int> two))
            {
                if (Roads.LaySegment(two[0], two[0], LWRoadDefOf.DirtRoad) != LayResult.Invalid) bad.Add("roads (planner): a link from a tile to itself was accepted");
                if (Roads.LaySegment(two[0], two[1], null) != LayResult.Invalid || Roads.RoadBetween(two[0], two[1]) != null) bad.Add("roads (planner): a link with no def was accepted");
            }
            Log.Message($"{Tag}planner cases done (water {water}, no-road land {noRoad})");
        }

        // ---- Ledger ----

        /// <summary>
        /// On bare land the mod's link has no baseline and decays tier by tier to nothing; on a vanilla road it stops at that road; a link
        /// changed from outside is let go; a lay on a link that is already as good changes nothing and is not claimed.
        /// </summary>
        private static void Ledger(List<string> bad, LivingWorldComponent c)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            RoadState st = c.roadState;
            if (!FindChain(surface, 4, out List<int> t))
            {
                Log.Message(Tag + "no bare chain for the ledger cases");
                return;
            }
            int version = Roads.Version, entries = st.edges.Count;
            try
            {
                // Bare land.
                if (Roads.LaySegment(t[0], t[1], LWRoadDefOf.StoneRoad) != LayResult.Changed) bad.Add("roads (ledger): stone on bare land was not laid");
                RoadEdge e = st.Edge(Roads.EdgeKey(t[0], t[1]));
                if (e == null || e.baseline != null || e.written != LWRoadDefOf.StoneRoad.defName) bad.Add("roads (ledger): the laid link has no entry, or one with a baseline / the wrong def");
                if (!Symmetric(surface, t[0], t[1]) || Raw(surface, t[0], t[1]) != LWRoadDefOf.StoneRoad) bad.Add("roads (ledger): the laid link is not stone on both tiles");
                if (Roads.Version == version) bad.Add("roads (ledger): the version did not move when a link was laid");
                if (Roads.LaySegment(t[0], t[1], LWRoadDefOf.DirtRoad) != LayResult.AlreadySatisfied || Roads.LaySegment(t[0], t[1], LWRoadDefOf.StoneRoad) != LayResult.AlreadySatisfied)
                    bad.Add("roads (ledger): laying a road no better than the one there was not 'already satisfied'");
                RoadDef[] chain = { LWRoadDefOf.DirtRoad, LWRoadDefOf.DirtPath, null };
                foreach (RoadDef expect in chain)
                {
                    if (e == null || !Roads.Downgrade(e)) bad.Add($"roads (ledger): downgrading towards {expect?.defName ?? "nothing"} changed nothing");
                    if (Raw(surface, t[0], t[1]) != expect || Raw(surface, t[1], t[0]) != expect) bad.Add($"roads (ledger): the link is not {expect?.defName ?? "gone"} on both tiles after a downgrade");
                }
                if (st.Edge(Roads.EdgeKey(t[0], t[1])) != null) bad.Add("roads (ledger): the entry outlived the link it described");
                // A vanilla road: the baseline is kept and the link never goes below it.
                Find.WorldGrid.OverlayRoad(new PlanetTile(t[1], surface), new PlanetTile(t[2], surface), LWRoadDefOf.DirtRoad);
                if (Roads.LaySegment(t[1], t[2], LWRoadDefOf.StoneRoad) != LayResult.Changed) bad.Add("roads (ledger): stone over a dirt road was not laid");
                RoadEdge v = st.Edge(Roads.EdgeKey(t[1], t[2]));
                if (v == null || v.baseline != LWRoadDefOf.DirtRoad.defName) bad.Add("roads (ledger): the vanilla road was not recorded as the baseline");
                if (v != null && !Roads.Downgrade(v)) bad.Add("roads (ledger): the upgraded vanilla road did not come back down");
                if (Raw(surface, t[1], t[2]) != LWRoadDefOf.DirtRoad || st.Edge(Roads.EdgeKey(t[1], t[2])) != null) bad.Add("roads (ledger): the link did not stop at the vanilla road, or its entry stayed");
                // Changed from outside: not ours any more.
                if (Roads.LaySegment(t[2], t[3], LWRoadDefOf.DirtRoad) != LayResult.Changed) bad.Add("roads (ledger): dirt on bare land was not laid");
                Find.WorldGrid.OverlayRoad(new PlanetTile(t[2], surface), new PlanetTile(t[3], surface), LWRoadDefOf.StoneRoad);
                RoadEdge x = st.Edge(Roads.EdgeKey(t[2], t[3]));
                if (x != null) Roads.Downgrade(x);
                if (st.Edge(Roads.EdgeKey(t[2], t[3])) != null || Raw(surface, t[2], t[3]) != LWRoadDefOf.StoneRoad)
                    bad.Add("roads (ledger): a link upgraded from outside was lowered, or its entry stayed");
            }
            finally
            {
                for (int i = 0; i + 1 < t.Count; i++) Roads.RestoreLink(t[i], t[i + 1], null);
            }
            if (st.edges.Count != entries) bad.Add($"roads (ledger): {st.edges.Count - entries} entries left behind");
        }

        private static RoadRoute FakeRoute(LivingWorldComponent c, RouteState state, bool decaying)
        {
            int id = c.NextId();
            var r = new RoadRoute { id = id, builderId = 1_900_000_000 + id, otherId = 1_950_000_000 + id, def = LWRoadDefOf.StoneRoad.defName, state = state, decaying = decaying };
            c.roadState.Add(r);
            return r;
        }

        /// <summary>
        /// A link two routes laid stays while the other one is kept up; a player's link is never lowered; both let go of it. Two routes
        /// that wear out in the same check lower their shared link by exactly one tier, whatever the order they are listed in.
        /// </summary>
        private static void Claims(List<string> bad, LivingWorldComponent c)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            RoadState st = c.roadState;
            if (!FindChain(surface, 3, out List<int> t))
            {
                Log.Message(Tag + "no bare chain for the claim cases");
                return;
            }
            RoadRoute a = FakeRoute(c, RouteState.Finished, decaying: true), b = FakeRoute(c, RouteState.Finished, decaying: true);
            long shareKey = Roads.EdgeKey(t[0], t[1]);
            void LayShared()
            {
                Roads.RestoreLink(t[0], t[1], null);
                Roads.LaySegment(t[0], t[1], LWRoadDefOf.DirtRoad, a.id); // A lays dirt, B upgrades it to stone: both claim it
                Roads.LaySegment(t[0], t[1], LWRoadDefOf.StoneRoad, b.id);
            }
            try
            {
                // Both wearing out, due in the same check: one tier per check, in either listing order.
                foreach (bool reversed in new[] { false, true })
                {
                    LayShared();
                    RoadEdge shared = st.Edge(shareKey);
                    if (shared == null || shared.claims.Count != 2) bad.Add("roads (claims): the link is not claimed by both routes");
                    var due = reversed ? new List<RoadRoute> { b, a } : new List<RoadRoute> { a, b };
                    Roads.DecayDue(st, due);
                    if (Raw(surface, t[0], t[1]) != LWRoadDefOf.DirtRoad) bad.Add($"roads (claims): two routes wearing out together lowered their shared link {(reversed ? "(reversed)" : "")} to {Raw(surface, t[0], t[1])?.defName ?? "nothing"}, not by exactly one tier");
                    Roads.DecayDue(st, due);
                    if (Raw(surface, t[0], t[1]) != LWRoadDefOf.DirtPath) bad.Add("roads (claims): the second check did not lower the shared link by one more tier");
                    Roads.DecayDue(st, due);
                    if (Raw(surface, t[0], t[1]) != null || st.Edge(shareKey) != null || a.claimed.Count != 0 || b.claimed.Count != 0)
                        bad.Add("roads (claims): the third check did not take the shared link down to nothing and let both routes go");
                }
                // Only A wears out while B is kept up: A lets go, the link stays.
                LayShared();
                b.decaying = false;
                Roads.DecayDue(st, new List<RoadRoute> { a });
                RoadEdge kept = st.Edge(shareKey);
                if (Raw(surface, t[0], t[1]) != LWRoadDefOf.StoneRoad) bad.Add("roads (claims): a decaying route lowered a link another route keeps up");
                if (a.claimed.Count != 0 || kept == null || kept.claims.Contains(a.id) || !kept.claims.Contains(b.id)) bad.Add("roads (claims): the decaying route did not let go of the link (or the keeper lost it)");
                // The keeper wears out later: now it goes down, by one tier.
                b.decaying = true;
                Roads.DecayDue(st, new List<RoadRoute> { b });
                if (Raw(surface, t[0], t[1]) != LWRoadDefOf.DirtRoad) bad.Add("roads (claims): the last route's decay did not lower the link a tier");
                // A player's investment: the route lets go, the road stays.
                Roads.LaySegment(t[1], t[2], LWRoadDefOf.DirtRoad, a.id);
                Roads.LaySegment(t[1], t[2], LWRoadDefOf.StoneRoad, -1, player: true);
                Roads.DecayDue(st, new List<RoadRoute> { a });
                RoadEdge mine = st.Edge(Roads.EdgeKey(t[1], t[2]));
                if (Raw(surface, t[1], t[2]) != LWRoadDefOf.StoneRoad || mine == null || !mine.player) bad.Add("roads (claims): a player's link was lowered by a route's decay");
                if (a.claimed.Count != 0) bad.Add("roads (claims): the route still holds a link a player built");
            }
            finally
            {
                st.Drop(a);
                st.Drop(b);
                for (int i = 0; i + 1 < t.Count; i++) Roads.RestoreLink(t[i], t[i + 1], null);
            }
        }

        // ---- NPC route ----

        /// <summary>
        /// A route forced between two towns of one faction is built a link a day for exactly its price, finishes with one chronicle
        /// entry, and (abandoned) decays tier by tier until its links are gone and its record dropped.
        /// </summary>
        private static void NpcRoute(List<string> bad, LivingWorldComponent c)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            RoadState st = c.roadState;
            var tiles = new List<int>();
            CompSettlementLife builder = null, other = null;
            int tries = 0;
            foreach (CompSettlementLife a in c.activeSettlements)
            {
                if (builder != null || !a.Active) continue;
                foreach (CompSettlementLife b in c.activeSettlements)
                {
                    if (b == a || !b.Active || b.Settlement.Faction != a.Settlement.Faction || st.RouteFor(Roads.PairKey(a.Settlement.ID, b.Settlement.ID)) != null) continue;
                    float d = Find.WorldGrid.ApproxDistanceInTiles(a.Settlement.Tile, b.Settlement.Tile);
                    if (d < 3f || d > 9f || tries++ > 40) continue;
                    if (!Roads.PlanRoute(a.Settlement.Tile, b.Settlement.Tile, Tuning.T.roadNpcMaxEdges, tiles, out RouteReject _)) continue;
                    bool clear = true;
                    for (int i = 0; i + 1 < tiles.Count; i++)
                        if (st.Edge(Roads.EdgeKey(tiles[i], tiles[i + 1])) != null) clear = false;
                    if (!clear) continue;
                    builder = a;
                    other = b;
                    break;
                }
            }
            if (builder == null)
            {
                Log.Message(Tag + "no pair of towns with a free buildable route for the NPC route case");
                return;
            }
            FactionState fs = c.StateFor(builder.Settlement.Faction);
            float treasury = fs.treasury;
            RoadRoute route = null;
            var baselines = new Dictionary<long, RoadDef>();
            for (int i = 0; i + 1 < tiles.Count; i++) baselines[Roads.EdgeKey(tiles[i], tiles[i + 1])] = Roads.RoadBetween(tiles[i], tiles[i + 1]);
            int lastEntryId = c.chronicle.entries.Count == 0 ? -1 : c.chronicle.entries.Max(e => e.id);
            try
            {
                // The reserve: a faction with less than the reserve plus a link's price builds nothing and keeps its silver.
                float poorTreasury = Tuning.T.roadTreasuryReserve + 1f;
                fs.treasury = poorTreasury;
                RoadRoute poor = Roads.CreateRoute(c, st, builder, other, tiles);
                bool anyNeeded = false;
                for (int i = 0; i + 1 < tiles.Count; i++)
                    if (Roads.Need(tiles[i], tiles[i + 1], poor.Def) == EdgeNeed.Needs) anyNeeded = true;
                int versionBefore = Roads.Version;
                Roads.StepOneRoute(c, poor);
                if (anyNeeded && (poor.changed != 0 || poor.state != RouteState.Blocked || fs.treasury != poorTreasury || Roads.Version != versionBefore))
                    bad.Add($"roads (npc route): with {poorTreasury:F0} silver (reserve {Tuning.T.roadTreasuryReserve:F0}) the route laid {poor.changed} links, is {poor.state} and the treasury is {fs.treasury:F0}");
                if (st.Route(poor.id) != null) st.Drop(poor);
                fs.treasury = 10_000f; // small enough for exact float arithmetic
                route = Roads.CreateRoute(c, st, builder, other, tiles);
                RoadDef def = route.Def;
                float expectedCost = 0f;
                int needed = 0;
                for (int i = 0; i + 1 < tiles.Count; i++)
                    if (Roads.Need(tiles[i], tiles[i + 1], def) == EdgeNeed.Needs)
                    {
                        expectedCost += Roads.NpcCost(def, tiles[i], tiles[i + 1]);
                        needed++;
                    }
                int steps = 0;
                while (route.state == RouteState.Open && steps++ < tiles.Count + 3)
                {
                    int before = route.changed;
                    Roads.StepOneRoute(c, route); // only this route: on a big world the others spend the same treasury and chronicle their own ends
                    if (route.changed - before > 1) bad.Add("roads (npc route): a route laid more than one link in a day");
                }
                if (route.state != RouteState.Finished && st.Route(route.id) != null) bad.Add($"roads (npc route): after {steps} days the route is {route.state}, not finished");
                if (route.changed != needed) bad.Add($"roads (npc route): {route.changed} links changed, {needed} needed");
                if (Math.Abs(10_000f - fs.treasury - expectedCost) > 0.05f) bad.Add($"roads (npc route): the treasury paid {10_000f - fs.treasury:F2} silver, the links cost {expectedCost:F2}");
                if (Math.Abs(route.paid - expectedCost) > 0.05f) bad.Add($"roads (npc route): the route's paid counter says {route.paid:F2} silver, the links cost {expectedCost:F2}");
                for (int i = 0; i + 1 < tiles.Count; i++)
                    if ((Roads.RoadBetween(tiles[i], tiles[i + 1])?.priority ?? 0) < def.priority) bad.Add($"roads (npc route): link {i} is not a {def.defName}");
                int joined = c.chronicle.entries.Count(e => e.id > lastEntryId && e.defName == LWChronicleDefOf.LW_RoadJoined.defName
                    && e.worldObjectId == builder.Settlement.ID && e.factionA == builder.Settlement.Faction.loadID);
                if (joined != (needed > 0 ? 1 : 0)) bad.Add($"roads (npc route): {joined} chronicle entries for the finished road (expected {(needed > 0 ? 1 : 0)})");
                // Abandoned: the links wear down, tier by tier, to what was there before.
                if (st.Route(route.id) != null)
                {
                    route.state = RouteState.Abandoned;
                    int rounds = 0;
                    while (st.Route(route.id) != null && rounds++ < 16) Roads.Degrade(c, st, Tuning.T);
                    if (st.Route(route.id) != null) bad.Add("roads (npc route): the abandoned route's record is still there after the links should have worn away");
                    foreach (KeyValuePair<long, RoadDef> kv in baselines)
                    {
                        Roads.SplitKey(kv.Key, out int x, out int y);
                        if (Roads.RoadBetween(x, y) != kv.Value) bad.Add($"roads (npc route): link {x}-{y} is {Roads.RoadBetween(x, y)?.defName ?? "gone"} after the decay, it was {kv.Value?.defName ?? "bare"}");
                        if (st.Edge(kv.Key) != null) bad.Add($"roads (npc route): link {x}-{y} still has a ledger entry");
                    }
                    Log.Message($"{Tag}npc route {builder.Settlement.Label} → {other.Settlement.Label}: {tiles.Count - 1} links ({needed} laid for {expectedCost:F0} silver) built in {steps} days, decayed in {rounds} checks");
                }
            }
            finally
            {
                fs.treasury = treasury;
                if (route != null && st.Route(route.id) != null) st.Drop(route);
                foreach (KeyValuePair<long, RoadDef> kv in baselines)
                {
                    Roads.SplitKey(kv.Key, out int x, out int y);
                    if (Roads.RoadBetween(x, y) != kv.Value || st.Edge(kv.Key) != null) Roads.RestoreLink(x, y, kv.Value);
                }
            }
            if (!baselines.All(kv => { Roads.SplitKey(kv.Key, out int x, out int y); return Symmetric(surface, x, y); })) bad.Add("roads (npc route): links are not symmetric after the route");
        }

        // ---- Object index ----

        private static WorldObject SpawnRuin(PlanetTile tile)
        {
            WorldObject wo = WorldObjectMaker.MakeWorldObject(WorldObjectDefOf.DestroyedSettlement);
            wo.Tile = tile;
            Find.WorldObjects.Add(wo);
            return wo;
        }

        /// <summary>
        /// The id index the NPC roads rely on to tell a dead town from a live one: with the index populated, one world object replaced by
        /// another (the count unchanged, the tick unchanged: what refounding a ruin does, and what history does all day) must be seen at
        /// once, and traffic with the new object must not be pruned as traffic with a dead one.
        /// </summary>
        private static void ObjectIndex(List<string> bad, LivingWorldComponent c)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            RoadState st = c.roadState;
            var free = new List<PlanetTile>();
            for (int t = 0; t < surface.TilesCount && free.Count < 2; t += 7)
            {
                var tile = new PlanetTile(t, surface);
                if (TileOf(surface, t) is SurfaceTile st0 && !st0.WaterCovered && !Find.WorldObjects.AnyWorldObjectAt(tile)) free.Add(tile);
            }
            if (free.Count < 2)
            {
                Log.Message(Tag + "no two free tiles for the object index case");
                return;
            }
            CompSettlementLife town = c.activeSettlements.FirstOrDefault(x => x.Active);
            if (town == null) return;
            WorldObject first = null, second = null;
            var trafficBefore = new Dictionary<long, float>(st.traffic);
            try
            {
                first = SpawnRuin(free[0]);
                if (LivingWorldComponent.WorldObjectById(first.ID) != first) bad.Add("roads (index): an object just added to the world was not found by id");
                // The index is populated; nothing looks at it while the object is replaced by another.
                int tick = Find.TickManager.TicksGame, count = Find.WorldObjects.AllWorldObjects.Count;
                first.Destroy();
                second = SpawnRuin(free[1]);
                if (Find.WorldObjects.AllWorldObjects.Count != count || Find.TickManager.TicksGame != tick) bad.Add("roads (index): the fixture changed the object count or the tick");
                if (LivingWorldComponent.WorldObjectById(second.ID) != second) bad.Add("roads (index): an object that replaced another (same count, same tick) was not found by id");
                if (LivingWorldComponent.WorldObjectById(first.ID) != null) bad.Add("roads (index): a destroyed object is still found by id");
                // Days pass (the tick does not, as in history): traffic with the new object stays, traffic with the gone one goes.
                long key = Roads.PairKey(town.Settlement.ID, second.ID);
                long goneKey = Roads.PairKey(town.Settlement.ID, first.ID);
                st.traffic[key] = 5000f;
                st.traffic[goneKey] = 5000f;
                for (int day = 0; day < 3; day++) Roads.DecayTraffic(st, Tuning.T);
                float expected = 5000f * Mathf.Pow(Tuning.T.roadTrafficDecayPerDay, 3);
                if (!st.traffic.TryGetValue(key, out float left) || Math.Abs(left - expected) > 1f) bad.Add($"roads (index): traffic with an object that replaced another was {(st.traffic.ContainsKey(key) ? left.ToString("F0") : "pruned")}, expected {expected:F0}");
                if (st.traffic.ContainsKey(goneKey)) bad.Add("roads (index): traffic with a destroyed object was not pruned");
            }
            finally
            {
                if (first != null && !first.Destroyed) first.Destroy();
                if (second != null && !second.Destroyed) second.Destroy();
                st.traffic.Clear(); // the days of decay above touched every pair: back to what it was
                foreach (KeyValuePair<long, float> kv in trafficBefore) st.traffic[kv.Key] = kv.Value;
            }
        }

        // ---- Traffic ----

        private static void Traffic(List<string> bad, LivingWorldComponent c)
        {
            RoadState st = c.roadState;
            CompSettlementLife buyer = c.activeSettlements.FirstOrDefault(s => s.Active);
            CompSettlementLife seller = c.activeSettlements.FirstOrDefault(s => s.Active && s != buyer);
            if (buyer == null || seller == null) return;
            long key = Roads.PairKey(buyer.Settlement.ID, seller.Settlement.ID);
            st.traffic.TryGetValue(key, out float before);
            int pairs = st.traffic.Count;
            // Deliver adds the traffic exactly once (with the economy on: it is the economy that counts it, roads or not); goods from nowhere, from the same town, or between a town and a non-town count for nothing.
            float want = LivingWorldMod.Settings.simulateEconomy ? 1f : 0f;
            Trade.Deliver(c, seller.Settlement, buyer, 0, 1f, out float _);
            st.traffic.TryGetValue(key, out float after);
            if (Math.Abs(after - before - want) > 0.001f) bad.Add($"roads (traffic): a delivery of 1 changed the pair's traffic by {after - before}, expected {want}");
            Trade.Deliver(c, null, buyer, 0, 1f, out float _);
            Trade.Deliver(c, buyer.Settlement, buyer, 0, 1f, out float _);
            st.traffic.TryGetValue(key, out float again);
            if (again != after || st.traffic.Count != (before == 0f && want > 0f ? pairs + 1 : pairs)) bad.Add("roads (traffic): a delivery from nowhere or from the same town was counted");
            // Undo what the deliveries did (stock, trade volume); the pair's traffic goes back to what it was.
            buyer.stock[0] -= 3f;
            buyer.recentTrade -= 3f;
            if (before == 0f) st.traffic.Remove(key);
            else st.traffic[key] = before;
        }

        // ---- Effects ----

        private static bool FindFriendlyTownWithRoute(LivingWorldComponent c, Map home, List<int> tiles, out CompSettlementLife town)
        {
            town = null;
            int tries = 0;
            foreach (CompSettlementLife s in c.activeSettlements.OrderBy(x => Find.WorldGrid.ApproxDistanceInTiles(home.Tile, x.Settlement.Tile)))
            {
                Faction f = s.Settlement.Faction;
                if (!s.Active || f.HostileTo(Faction.OfPlayer) || f.def.permanentEnemy || Roads.Connected(home, s.Settlement) || tries++ > 12) continue; // not one vanilla roads already join
                if (!Roads.PlanRoute(home.Tile, s.Settlement.Tile, Tuning.T.roadPlayerMaxEdges, tiles, out RouteReject _)) continue;
                town = s;
                return true;
            }
            return false;
        }

        /// <summary>
        /// A road from the colony to a friendly town joins them (and only while the road is visible and short enough), changes the
        /// Neighbours score, lifts fame by the rule (never past the ceiling, never lowering a higher one), is credited once, and
        /// goes away with the road.
        /// </summary>
        private static void Effects(List<string> bad, LivingWorldComponent c)
        {
            Map home = Neighbourhood.Home;
            if (home == null)
            {
                Log.Message(Tag + "no colony on the surface for the effect cases");
                return;
            }
            RoadState st = c.roadState;
            var tiles = new List<int>();
            if (!FindFriendlyTownWithRoute(c, home, tiles, out CompSettlementLife town))
            {
                Log.Message(Tag + "no friendly town with a buildable road to the colony for the effect cases");
                return;
            }
            Settlement s = town.Settlement;
            var baselines = new List<RoadDef>();
            for (int i = 0; i + 1 < tiles.Count; i++) baselines.Add(Roads.RoadBetween(tiles[i], tiles[i + 1]));
            var fames = new Dictionary<CompSettlementLife, float>();
            foreach (CompSettlementLife x in c.activeSettlements) fames[x] = x.fame;
            int joined = st.joinedTowns.Count, lastFame = st.lastFameTick;
            int connectEdges = Tuning.T.roadConnectEdges;
            bool wasJoined = st.joinedTowns.Contains(s.ID);
            try
            {
                if (Roads.Connected(home, s) || Roads.InspectLine(s) != null || Roads.NeighbourFactor(home, s) != 1f) bad.Add("roads (effects): the town is joined to the colony before any road exists");
                for (int i = 0; i + 1 < tiles.Count; i++) Roads.LaySegment(tiles[i], tiles[i + 1], LWRoadDefOf.DirtRoad);
                if (!Roads.Connected(home, s)) bad.Add($"roads (effects): a road of {tiles.Count - 1} links from the colony to {s.Label} does not join them");
                if (Roads.NeighbourFactor(home, s) != Tuning.T.roadNeighbourFactor) bad.Add("roads (effects): the Neighbours factor of a joined town is not the tuned one");
                if (Roads.InspectLine(s) == null) bad.Add("roads (effects): a joined town has no inspect line");
                // The link-count boundary: a town as many links away as the limit is joined, one more is not.
                Tuning.T.roadConnectEdges = tiles.Count - 2;
                if (Roads.Connected(home, s)) bad.Add($"roads (effects): a town {tiles.Count - 1} links away is joined with a limit of {tiles.Count - 2}");
                Tuning.T.roadConnectEdges = tiles.Count - 1;
                if (!Roads.Connected(home, s)) bad.Add($"roads (effects): a town {tiles.Count - 1} links away is not joined with a limit of {tiles.Count - 1}");
                Tuning.T.roadConnectEdges = connectEdges;
                // Fame: 0.49 → the ceiling, the ceiling and above stay, a bad name mends a little.
                float ceiling = Tuning.T.roadFameCeiling, gain = Tuning.T.roadFameGain;
                foreach ((float from, float expect) in new[] { (ceiling - 0.01f, ceiling), (ceiling, ceiling), (0.8f, 0.8f), (-0.2f, -0.2f + gain) })
                {
                    town.fame = from;
                    st.lastFameTick = FactionState.Never;
                    Roads.FameDaily(c, st, SimClock.Now);
                    if (Math.Abs(town.fame - expect) > 0.0001f) bad.Add($"roads (effects): fame {from} became {town.fame}, expected {expect}");
                }
                // Credit goes only to a connection the job's own new links made: links of another builder or an unrelated job do not earn
                // the town (it was joined without them), a job that only upgraded roads earns nothing, the job's own links earn it once.
                var own = new List<long>();
                for (int i = 0; i + 1 < tiles.Count; i++) own.Add(Roads.EdgeKey(tiles[i], tiles[i + 1]));
                st.joinedTowns.Remove(s.ID);
                int before = c.chronicle.entries.Count;
                Roads.Notify_PlayerRouteFinished(null, new List<long> { Roads.EdgeKey(int.MaxValue - 1, int.MaxValue) });
                if (st.joinedTowns.Contains(s.ID) || c.chronicle.entries.Count != before) bad.Add("roads (effects): an unrelated new link was credited with a connection that exists without it");
                Roads.Notify_PlayerRouteFinished(null, new List<long>());
                if (st.joinedTowns.Contains(s.ID) || c.chronicle.entries.Count != before) bad.Add("roads (effects): a job that made no new link was credited");
                Roads.Notify_PlayerRouteFinished(null, own);
                int firstCredit = c.chronicle.entries.Count - before;
                Roads.Notify_PlayerRouteFinished(null, own);
                if (!st.joinedTowns.Contains(s.ID) || firstCredit < 1 || c.chronicle.entries.Count - before != firstCredit) bad.Add($"roads (effects): the connection was credited {c.chronicle.entries.Count - before} times (first {firstCredit}), not once");
                // The town is joined by the job's links together with a link of someone else: still the job's connection.
                if (own.Count >= 2)
                {
                    st.joinedTowns.Remove(s.ID);
                    before = c.chronicle.entries.Count;
                    Roads.Notify_PlayerRouteFinished(null, own.Skip(1).ToList());
                    if (!st.joinedTowns.Contains(s.ID) || c.chronicle.entries.Count == before) bad.Add("roads (effects): a connection completed by the job's links (and one of another builder's) was not credited");
                }
            }
            finally
            {
                Tuning.T.roadConnectEdges = connectEdges;
                for (int i = 0; i + 1 < tiles.Count; i++) Roads.RestoreLink(tiles[i], tiles[i + 1], baselines[i]);
                foreach (KeyValuePair<CompSettlementLife, float> kv in fames) kv.Key.fame = kv.Value;
                st.lastFameTick = lastFame;
                while (st.joinedTowns.Count > joined) st.joinedTowns.RemoveAt(st.joinedTowns.Count - 1);
                if (wasJoined && !st.joinedTowns.Contains(s.ID)) st.joinedTowns.Add(s.ID);
            }
            if (Roads.Connected(home, s)) bad.Add("roads (effects): the town is still joined after the road was taken away (cache not invalidated)");
            if (Roads.InspectLine(s) != null) bad.Add("roads (effects): the inspect line stays after the road was taken away");
        }

        // ---- Roads off ----

        /// <summary>
        /// Switched off: no building by NPCs, no effects, no gizmo; nothing is lost either (what is built stays). Traffic is still counted (it follows the economy,
        /// the trade routes overlay draws it; the overlay checks cover that gate): the cases here only put the pair's traffic back to what it was.
        /// </summary>
        private static void RoadsOff(List<string> bad, LivingWorldComponent c)
        {
            LivingWorldSettings stored = LivingWorldMod.Stored;
            RoadState st = c.roadState;
            CompSettlementLife a = c.activeSettlements.FirstOrDefault(s => s.Active);
            CompSettlementLife b = c.activeSettlements.FirstOrDefault(s => s.Active && s != a);
            if (a == null || b == null) return;
            var tiles = new List<int>();
            bool was = stored.roads;
            RoadRoute route = null;
            long key = Roads.PairKey(a.Settlement.ID, b.Settlement.ID);
            st.traffic.TryGetValue(key, out float traffic);
            var trafficBefore = new Dictionary<long, float>(st.traffic);
            float maxTraffic = Roads.StatMaxTraffic;
            try
            {
                stored.roads = false;
                Roads.Notify_RoadsSettingChanged();
                if (Roads.Active) bad.Add("roads (off): Active with the setting off");
                Roads.NoteDelivery(a.Settlement, b.Settlement, 5f);
                st.traffic.TryGetValue(key, out float after);
                bool economy = LivingWorldMod.Settings.simulateEconomy;
                if (Math.Abs(after - traffic - (economy ? 5f : 0f)) > 0.001f) bad.Add($"roads (off): a delivery of 5 changed the traffic by {after - traffic} with the economy {(economy ? "on" : "off")}");
                if (Roads.PlanRoute(a.Settlement.Tile, b.Settlement.Tile, 30, tiles, out RouteReject _) && tiles.Count > 1)
                {
                    route = Roads.CreateRoute(c, st, a, b, tiles);
                    int version = Roads.Version, next = route.next;
                    Roads.DailyStep(c, 1f);
                    if (Roads.Version != version || route.next != next) bad.Add("roads (off): the daily step built or changed a road");
                }
                Map home = Neighbourhood.Home;
                if (home != null && (Roads.Connected(home, a.Settlement) || Roads.NeighbourFactor(home, a.Settlement) != 1f || Roads.InspectLine(a.Settlement) != null)) bad.Add("roads (off): an effect is on");
            }
            finally
            {
                stored.roads = was;
                if (route != null && st.Route(route.id) != null) st.Drop(route);
                Roads.Notify_RoadsSettingChanged();
                st.traffic.Clear(); // the delivery above and the daily step's decay touched every pair
                foreach (KeyValuePair<long, float> kv in trafficBefore) st.traffic[kv.Key] = kv.Value;
                Roads.StatMaxTraffic = maxTraffic;
            }
            if (!Roads.Active) bad.Add("roads (off): not Active after the setting went back on");
        }

        /// <summary>A world prepared for removal / roads off from the start: nothing builds, no traffic is counted without the economy (it is with it, roads or not), and a caravan job is not kept.</summary>
        private static void RoadsOffWorld(List<string> bad, LivingWorldComponent c)
        {
            RoadState st = c.roadState;
            if (st.routes.Any(r => r.state == RouteState.Open && r.changed > 0)) bad.Add("roads (off world): a route was building with roads off");
            if (Roads.StatEdgesLaid > 0) bad.Add($"roads (off world): {Roads.StatEdgesLaid} links were laid with roads off");
            if (!LivingWorldMod.Settings.simulateEconomy && st.traffic.Count > 0) bad.Add($"roads (off world): {st.traffic.Count} pairs counted traffic with the economy off");
            // With the economy on, traffic is counted whether roads are on or not (V7): history's direct deliveries leave some by landing. Logged, not asserted: a short run may have none.
            if (LivingWorldMod.Settings.simulateEconomy) Log.Message($"{Tag}roads off world: {st.traffic.Count} pairs with traffic (counted with roads off, the economy on){(st.traffic.Count == 0 ? ", NONE: nothing was delivered yet, or counting is gated on roads again" : "")}");
            foreach (Caravan car in Find.WorldObjects.Caravans)
                if (car.GetComponent<CompRoadBuilder>()?.HasJob == true) bad.Add("roads (off world): a caravan still has a road job");
        }

        // ---- Merge and removal ----

        internal static Pawn TempColonist()
        {
            for (int i = 0; i < 12; i++)
            {
                Pawn p = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
                if (p.skills == null || p.WorkTypeIsDisabled(WorkTypeDefOf.Construction))
                {
                    p.Discard(silentlyRemoveReferences: true);
                    continue;
                }
                for (int k = 0; k < 2; k++)
                {
                    Thing meals = ThingMaker.MakeThing(ThingDefOf.MealSimple);
                    meals.stackCount = 10;
                    p.inventory.innerContainer.TryAdd(meals);
                }
                keptPawns.Add(p);
                testPawnIds.Add(p.ThingID);
                return p;
            }
            return null;
        }

        internal static Caravan MakeCaravan(PlanetTile at, params Pawn[] pawns) =>
            CaravanMaker.MakeCaravan(pawns, Faction.OfPlayer, at, addToWorldPawnsIfNotAlready: true);

        internal static void Give(Pawn p, ThingDef def, int count)
        {
            while (count > 0)
            {
                Thing t = ThingMaker.MakeThing(def);
                t.stackCount = Math.Min(count, def.stackLimit);
                count -= t.stackCount;
                p.inventory.innerContainer.TryAdd(t);
            }
        }

        /// <summary>
        /// A start tile next to the colony (one not in <paramref name="used"/>) and a destination <paramref name="minEdges"/>–<paramref name="maxEdges"/>
        /// links away over bare land that the planner accepts with a route of that length.
        /// </summary>
        private static bool FindJobSites(PlanetLayer surface, int minEdges, int maxEdges, HashSet<int> used, out PlanetTile start, out PlanetTile dest)
        {
            start = dest = PlanetTile.Invalid;
            Map home = Neighbourhood.Home;
            if (home == null) return false;
            surface.GetTileNeighbors(home.Tile, neighbourBuf);
            var starts = neighbourBuf.Where(n => Roads.Buildable(surface, n.tileId, home.Tile.tileId) && (used == null || !used.Contains(n.tileId))).ToList();
            var tiles = new List<int>();
            var around = new List<PlanetTile>();
            foreach (PlanetTile s in starts)
            {
                var seen = new Dictionary<int, int> { [s.tileId] = 0 };
                var queue = new List<int> { s.tileId };
                for (int head = 0; head < queue.Count; head++)
                {
                    int t = queue[head], d = seen[t];
                    if (d >= maxEdges) continue;
                    surface.GetTileNeighbors(t, around);
                    foreach (PlanetTile n in around)
                    {
                        if (seen.ContainsKey(n.tileId) || !BareTile(surface, n.tileId) || n.tileId == home.Tile.tileId) continue;
                        seen[n.tileId] = d + 1;
                        queue.Add(n.tileId);
                    }
                }
                int tried = 0;
                foreach (int t in queue)
                {
                    if (seen[t] < minEdges || seen[t] > maxEdges || tried++ > 25) continue;
                    var candidate = new PlanetTile(t, surface);
                    if (!Roads.PlanRoute(s, candidate, 30, tiles, out RouteReject _) || tiles.Count - 1 < minEdges || tiles.Count - 1 > maxEdges) continue;
                    start = s;
                    dest = candidate;
                    used?.Add(s.tileId);
                    return true;
                }
            }
            return false;
        }

        // ---- Caravan fixtures ----

        /// <summary>Caravans and links a test case makes, taken away again when it is done.</summary>
        private sealed class Rig : IDisposable
        {
            public readonly List<Caravan> caravans = new List<Caravan>();
            private readonly List<(int a, int b, RoadDef was)> links = new List<(int, int, RoadDef)>();

            /// <summary>A caravan of <paramref name="pawns"/> temporary colonists at the tile, null if none could be made.</summary>
            public Caravan Make(PlanetTile at, int pawns)
            {
                var list = new List<Pawn>();
                for (int i = 0; i < pawns; i++)
                {
                    Pawn p = TempColonist();
                    if (p == null) return null;
                    list.Add(p);
                }
                Caravan car = MakeCaravan(at, list.ToArray());
                caravans.Add(car);
                return car;
            }

            /// <summary>
            /// Starts a road job through the click path (working phase), or with <paramref name="travelling"/> also finishes the first link
            /// at once, so the caravan is walking to the next tile on the job's own account.
            /// </summary>
            public bool Job(Caravan car, PlanetTile start, PlanetTile dest, bool travelling, out CompRoadBuilder comp)
            {
                comp = car.GetComponent<CompRoadBuilder>();
                if (comp == null || !comp.OnTargetChosen(LWRoadDefOf.DirtRoad, start, new GlobalTargetInfo(dest)) || !comp.HasJob) return false;
                int a = comp.JobTiles[0], b = comp.JobTiles[1];
                links.Add((a, b, Roads.RoadBetween(a, b)));
                return !travelling || (comp.Test_CompleteLinkNow() && comp.Phase == RoadPhase.Travelling && car.pather.Moving && comp.JobOwnsMove);
            }

            public void Dispose()
            {
                foreach (Caravan car in caravans) DisposeCaravan(car);
                for (int i = links.Count - 1; i >= 0; i--) Roads.RestoreLink(links[i].a, links[i].b, links[i].was); // newest first: the oldest record holds the original
            }
        }

        /// <summary>The pather walks to the end of its walk (a walk costs a few thousand ticks; each call pays up to 30000).</summary>
        private static void Walk(Caravan car)
        {
            for (int i = 0; i < 8 && car.pather.Moving; i++) car.pather.PatherTickInterval(30000);
        }

        // ---- Who owns a caravan's walk ----

        /// <summary>
        /// The job's own walk, anyone else's order and vanilla's own restarts, told apart: the arrival of the job's own hop is swallowed, but
        /// an arrival action set in the gap before the comp's next step runs exactly once; an order to the very tile the job walks to (with
        /// or without an action) ends the job and keeps the order; stopping or disabling afterwards touches nothing of the player's; the job's
        /// own walk is stopped; a reload's restart and a rest's pause keep the job.
        /// </summary>
        private static void TravelOwnership(List<string> bad)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            if (!FindJobSites(surface, 3, 6, null, out PlanetTile start, out PlanetTile dest))
            {
                Log.Message(Tag + "no sites for the travel cases");
                return;
            }
            using (var rig = new Rig())
            {
                bool Setup(string name, out Caravan car, out CompRoadBuilder comp, out PlanetTile next)
                {
                    next = PlanetTile.Invalid;
                    comp = null;
                    car = rig.Make(start, 1);
                    if (car == null)
                    {
                        bad.Add($"roads (travel {name}): no construction-capable temporary colonist");
                        return false;
                    }
                    if (!rig.Job(car, start, dest, travelling: true, out comp))
                    {
                        bad.Add($"roads (travel {name}): could not put a job into its travelling phase ({(comp == null ? "no comp" : comp.HasJob ? comp.Phase.ToString() : "no job")})");
                        return false;
                    }
                    next = new PlanetTile(comp.JobTiles[1], surface);
                    return true;
                }

                // Between the pather's arrival and the comp's next step: a current-tile arrival action must run, once.
                if (Setup("arrival", out Caravan car, out CompRoadBuilder comp, out PlanetTile next))
                {
                    Walk(car);
                    if (car.pather.Moving || car.Tile != next) bad.Add("roads (travel arrival): the job's own walk did not end on the next tile");
                    else if (!comp.HasJob || comp.Phase != RoadPhase.Travelling) bad.Add($"roads (travel arrival): the job changed before its comp stepped ({comp.Phase})");
                    var action = new CountingArrival();
                    bool accepted = car.pather.StartPath(car.Tile, action, repathImmediately: true);
                    if (!accepted || action.arrived != 1) bad.Add($"roads (travel arrival): an arrival action ordered at the current tile in that gap ran {action.arrived} times (accepted {accepted}), not once");
                    if (comp.HasJob) bad.Add("roads (travel arrival): the job outlived an order given at its caravan's tile");
                }
                // The same destination with an arrival action: the job ends, the walk and the action stay.
                if (Setup("same destination with an action", out car, out comp, out next))
                {
                    int jobId = comp.JobId;
                    var action = new CountingArrival();
                    bool accepted = car.pather.StartPath(next, action, repathImmediately: true);
                    if (!accepted) bad.Add("roads (travel same destination): the order was refused");
                    if (comp.HasJob || !Roads.CancelledJobs.TryGetValue(jobId, out string why) || why != "LW_Roads_Cancelled_Ordered") bad.Add("roads (travel same destination): an order to the job's next tile did not end the job as an order");
                    if (!car.pather.Moving || Patch_PathFollower_RoadHopArrived.ArrivalActionOf(car.pather) != action) bad.Add("roads (travel same destination): the order's walk or arrival action was disturbed");
                    Walk(car);
                    if (action.arrived != 1) bad.Add($"roads (travel same destination): the order's arrival action ran {action.arrived} times, not once");
                }
                // The same destination without an action; stopping the job or switching roads off right after leaves that walk alone.
                if (Setup("same destination", out car, out comp, out next))
                {
                    bool accepted = car.pather.StartPath(next, null, repathImmediately: true);
                    if (!accepted || comp.HasJob) bad.Add("roads (travel same destination): a plain order to the job's next tile did not end the job");
                    comp.Cancel(stopOwnMovement: true, "LW_Roads_Cancelled_Stopped");
                    Roads.CancelAllJobs();
                    if (!car.pather.Moving || car.pather.Destination != next) bad.Add("roads (travel same destination): stopping the job right after the order stopped the player's walk");
                }
                // The job's own walk is stopped by a cancel that says so.
                if (Setup("own walk", out car, out comp, out next))
                {
                    comp.Cancel(stopOwnMovement: true, null);
                    if (comp.HasJob || car.pather.Moving) bad.Add("roads (travel own walk): cancelling did not stop the job's own walk");
                }
                // Vanilla restarting the saved path after a load, and a rest's pause: neither is an order.
                if (Setup("reload and rest", out car, out comp, out next))
                {
                    car.pather.StartPath(car.pather.Destination, null, repathImmediately: true, resetPauseStatus: false);
                    if (!comp.HasJob || !comp.JobOwnsMove) bad.Add("roads (travel reload): vanilla restarting the saved path cancelled the job");
                    car.pather.Paused = true;
                    comp.CompTickInterval(250);
                    if (!comp.HasJob) bad.Add("roads (travel rest): a paused walk cancelled the job");
                    car.pather.Paused = false;
                }
            }
        }

        // ---- The progress gate ----

        /// <summary>
        /// "Progress" means the caravan moved, the job went on to a later link or work grew; a phase value alone is not. A deliberately
        /// frozen pather keeps the job in its travelling phase, and the gate must refuse it; the same job with the pather running passes.
        /// </summary>
        private static void ProgressGate(List<string> bad)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            if (!FindJobSites(surface, 3, 6, null, out PlanetTile start, out PlanetTile dest))
            {
                Log.Message(Tag + "no sites for the progress gate case");
                return;
            }
            using (var rig = new Rig())
            {
                Caravan car = rig.Make(start, 1);
                if (car == null || !rig.Job(car, start, dest, travelling: true, out CompRoadBuilder comp))
                {
                    bad.Add("roads (gate): could not put a job into its travelling phase");
                    return;
                }
                Progress before = Progress.Of(car, comp);
                car.pather.Paused = true; // frozen
                for (int i = 0; i < 6; i++)
                {
                    car.pather.PatherTickInterval(30000);
                    comp.CompTickInterval(250);
                }
                Progress frozen = Progress.Of(car, comp);
                if (!comp.HasJob || comp.Phase != RoadPhase.Travelling) bad.Add($"roads (gate): the frozen job is not in its travelling phase ({frozen})");
                if (Progress.Advanced(before, frozen)) bad.Add($"roads (gate): the gate accepted a frozen pather as progress ({before} → {frozen})");
                if (Progress.Advanced(before, new Progress(before.tile, before.index, before.work, RoadPhase.Working))) bad.Add("roads (gate): a change of phase alone was accepted as progress");
                car.pather.Paused = false;
                for (int i = 0; i < 6; i++)
                {
                    car.pather.PatherTickInterval(30000);
                    comp.CompTickInterval(250);
                }
                Progress moved = Progress.Of(car, comp);
                if (!Progress.Advanced(before, moved)) bad.Add($"roads (gate): the gate refused a job whose pather ran ({before} → {moved})");
            }
        }

        // ---- Merge, split, map entry ----

        /// <summary>
        /// Through the game's own merge (the bigger caravan survives, in either listing order): the loser's job is gone, the survivor's
        /// stays and revalidates; a caravan split off with the dialog's own steps starts idle and leaves the old job alone; a caravan that
        /// enters a map keeps no job.
        /// </summary>
        private static void MergeAndSplit(List<string> bad)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            if (!FindJobSites(surface, 3, 6, null, out PlanetTile start, out PlanetTile dest))
            {
                Log.Message(Tag + "no sites for the merge case");
                return;
            }
            MethodInfo merge = AccessTools.Method(typeof(CaravanMergeUtility), "MergeCaravans");
            if (merge == null)
            {
                bad.Add("roads (merge): the game's merge entry point (CaravanMergeUtility.MergeCaravans) was not found");
                return;
            }
            using (var rig = new Rig())
            {
                foreach ((bool bigJob, bool smallJob) in new[] { (true, true), (false, true), (true, false) })
                {
                    foreach (bool bigFirst in new[] { true, false })
                    {
                        string name = $"survivor job {bigJob}, loser job {smallJob}, {(bigFirst ? "survivor" : "loser")} listed first";
                        Caravan big = rig.Make(start, 2), small = rig.Make(start, 1);
                        if (big == null || small == null)
                        {
                            bad.Add("roads (merge): no construction-capable temporary colonists");
                            return;
                        }
                        CompRoadBuilder cb = big.GetComponent<CompRoadBuilder>(), cs = small.GetComponent<CompRoadBuilder>();
                        if ((bigJob && !rig.Job(big, start, dest, false, out cb)) || (smallJob && !rig.Job(small, start, dest, false, out cs)))
                        {
                            bad.Add($"roads (merge {name}): could not start the jobs");
                            continue;
                        }
                        merge.Invoke(null, new object[] { bigFirst ? new List<Caravan> { big, small } : new List<Caravan> { small, big } });
                        if (!small.Destroyed || big.Destroyed) bad.Add($"roads (merge {name}): the bigger caravan did not survive the merge");
                        if (cs.HasJob) bad.Add($"roads (merge {name}): the merged-away caravan still has a job");
                        if (cb.HasJob != bigJob) bad.Add($"roads (merge {name}): the survivor {(bigJob ? "lost its job" : "has a job that was not its own")}");
                        if (cb.HasJob)
                        {
                            cb.CompTickInterval(250);
                            if (!cb.HasJob) bad.Add($"roads (merge {name}): the survivor's job was cancelled when it revalidated");
                        }
                    }
                }
                // Split, the way the split dialog does it (the dialog itself needs a window).
                Caravan whole = rig.Make(start, 2);
                if (whole != null && rig.Job(whole, start, dest, false, out CompRoadBuilder cw))
                {
                    Pawn leaving = whole.PawnsListForReading[1];
                    var leaves = new List<Pawn> { leaving };
                    CaravanInventoryUtility.MoveAllInventoryToSomeoneElse(leaving, whole.PawnsListForReading, leaves);
                    whole.RemovePawn(leaving);
                    Caravan fresh = CaravanMaker.MakeCaravan(leaves, whole.Faction, whole.Tile, addToWorldPawnsIfNotAlready: true);
                    rig.caravans.Add(fresh);
                    if (fresh.GetComponent<CompRoadBuilder>() is not CompRoadBuilder cf) bad.Add("roads (split): the new caravan has no road builder comp");
                    else if (cf.HasJob) bad.Add("roads (split): the new caravan started with a job");
                    cw.CompTickInterval(250);
                    if (!cw.HasJob) bad.Add("roads (split): the old caravan lost its job in the split");
                }
                else bad.Add("roads (split): could not set up the split case");
                // Entering a map takes the caravan out of the world: its job goes with it.
                Map map = Find.AnyPlayerHomeMap;
                Caravan entering = map == null ? null : rig.Make(start, 1);
                if (entering != null && rig.Job(entering, start, dest, false, out CompRoadBuilder ce))
                {
                    var pawns = new List<Pawn>(entering.PawnsListForReading);
                    CaravanEnterMapUtility.Enter(entering, map, CaravanEnterMode.Edge);
                    if (ce.HasJob) bad.Add("roads (map entry): a caravan that entered a map kept its job");
                    foreach (Pawn p in pawns)
                    {
                        if (!p.Destroyed) p.Destroy();
                        if (!p.Discarded) p.Discard(silentlyRemoveReferences: true);
                    }
                }
                else if (map != null) bad.Add("roads (map entry): could not set up the map entry case");
            }
        }

        // ---- Removal ----

        /// <summary>
        /// The real "prepare for removal" (through <see cref="LivingWorldMod.PrepareForRemoval"/>) with a working job, a job on its own
        /// walk and an ordinary walk: both jobs end at once, the job's own walk is stopped and the other is not, the finished link stays,
        /// nothing is built afterwards and no new job can start; then the save is switched back on.
        /// </summary>
        private static void Removal(List<string> bad, LivingWorldComponent c)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            if (!FindJobSites(surface, 3, 6, null, out PlanetTile start, out PlanetTile dest))
            {
                Log.Message(Tag + "no sites for the removal case");
                return;
            }
            using (var rig = new Rig())
            {
                Caravan w = rig.Make(start, 1), t = rig.Make(start, 1), e = rig.Make(start, 1);
                if (w == null || t == null || e == null)
                {
                    bad.Add("roads (removal): no construction-capable temporary colonists");
                    return;
                }
                if (!rig.Job(w, start, dest, false, out CompRoadBuilder cw) || !rig.Job(t, start, dest, true, out CompRoadBuilder ct))
                {
                    bad.Add("roads (removal): could not start the jobs");
                    return;
                }
                if (cw.Phase != RoadPhase.Working || w.pather.Moving || ct.Phase != RoadPhase.Travelling || !t.pather.Moving) bad.Add("roads (removal): the jobs are not in the phases the case needs");
                PlanetTile away = PlanetTile.Invalid;
                var around = new List<PlanetTile>();
                surface.GetTileNeighbors(start, around);
                foreach (PlanetTile n in around)
                    if (!cw.JobTiles.Contains(n.tileId) && Roads.Buildable(surface, start.tileId, n.tileId))
                    {
                        away = n;
                        break;
                    }
                bool walking = away.Valid && e.pather.StartPath(away, null, repathImmediately: true) && e.pather.Moving;
                if (!walking) Log.Message(Tag + "no ordinary walk for the removal case");
                int laid = ct.JobTiles[0], laidTo = ct.JobTiles[1];
                RoadDef laidDef = Roads.RoadBetween(laid, laidTo);
                if (laidDef == null) bad.Add("roads (removal): the travelling job has laid no link");
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                try
                {
                    LivingWorldMod.PrepareForRemoval();
                    if (cw.HasJob || ct.HasJob) bad.Add("roads (removal): a job survived 'prepare for removal'");
                    if (t.pather.Moving) bad.Add("roads (removal): the job's own walk was left going");
                    if (walking && (!e.pather.Moving || e.pather.Destination != away)) bad.Add("roads (removal): an ordinary walk was stopped along with the jobs");
                    if (Roads.RoadBetween(laid, laidTo) != laidDef) bad.Add("roads (removal): the link a cancelled job had finished was taken away");
                    if (Roads.Active) bad.Add("roads (removal): roads are still active on a save prepared for removal");
                    int version = Roads.Version;
                    cw.CompTickInterval(1000);
                    ct.CompTickInterval(1000);
                    cw.OnTargetChosen(LWRoadDefOf.DirtRoad, start, new GlobalTargetInfo(dest));
                    if (cw.HasJob) bad.Add("roads (removal): a job could be started on a save prepared for removal");
                    if (Roads.Version != version) bad.Add("roads (removal): a road was built or changed after preparing for removal");
                }
                finally
                {
                    c.preparedForRemoval = false;
                    Territory.Rebuild(c); // what the re-enable button does
                    Roads.Notify_RoadsSettingChanged();
                }
                if (!Roads.Active) bad.Add("roads (removal): roads are not active after the save was switched back on");
            }
        }

        // ---- Redraw ----

        /// <summary>
        /// The roads layer is dirtied once for a burst of changes; NPC changes while the world view is drawn dirty it at most once per
        /// roadRedrawMinDays (a week of continuous changes gives a few invalidations, not one per change), a player's link at once, and a
        /// hidden world view takes every change straight away (free: the rebuild is when it opens). Vanilla's full rebuild is timed twice
        /// (the first may include JIT) and held to a regression guard.
        /// </summary>
        private static void Redraw(List<string> bad)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            var layer = surface.GetLayer<WorldDrawLayer_Roads>();
            if (layer == null)
            {
                Log.Message(Tag + "no roads draw layer in the headless world");
                return;
            }
            try
            {
                Roads.ResetRedrawThrottle();
                Roads.WorldRenderedOverride = true;
                int redraws = Roads.StatRedraws;
                Roads.Notify_HistoryEnded();
                Roads.Notify_HistoryEnded();
                Roads.FlushRedraw(1_000_000);
                Roads.FlushRedraw(1_000_000);
                if (Roads.StatRedraws != redraws + 1) bad.Add($"roads (redraw): {Roads.StatRedraws - redraws} invalidations for one burst of changes");

                // A week of continuous NPC changes (one an hour) while the world is drawn.
                int minDays = Math.Max(1, Tuning.T.roadRedrawMinDays);
                Roads.ResetRedrawThrottle();
                redraws = Roads.StatRedraws;
                int start = 2_000_000, changes = 0;
                for (int h = 0; h < 7 * 24; h++)
                {
                    Roads.NoteChange(urgent: false);
                    changes++;
                    Roads.FlushRedraw(start + h * GenDate.TicksPerHour);
                }
                int shown = Roads.StatRedraws - redraws, allowed = (7 + minDays - 1) / minDays;
                if (shown < 1 || shown > allowed) bad.Add($"roads (redraw): {shown} invalidations in a week of {changes} NPC changes with the world drawn (expected 1 to {allowed}, one per {minDays} days)");

                // A player's link inside the window goes through at once.
                Roads.NoteChange(urgent: false);
                Roads.FlushRedraw(start + 7 * 24 * GenDate.TicksPerHour);
                int before = Roads.StatRedraws;
                Roads.NoteChange(urgent: true);
                Roads.FlushRedraw(start + 7 * 24 * GenDate.TicksPerHour + 1);
                if (Roads.StatRedraws != before + 1) bad.Add("roads (redraw): a player's link did not redraw the roads layer at once");

                // Hidden world view: no waiting, the layer is only marked.
                Roads.WorldRenderedOverride = false;
                Roads.ResetRedrawThrottle();
                redraws = Roads.StatRedraws;
                for (int h = 0; h < 24; h++)
                {
                    Roads.NoteChange(urgent: false);
                    Roads.FlushRedraw(start + h * GenDate.TicksPerHour);
                }
                if (Roads.StatRedraws - redraws != 24) bad.Add($"roads (redraw): with the world view hidden {Roads.StatRedraws - redraws} of 24 changes marked the layer");
                Log.Message($"{Tag}redraw batching: {shown} invalidations in a week of {changes} NPC changes with the world drawn (minimum {minDays} days apart)");
            }
            finally
            {
                Roads.WorldRenderedOverride = null;
                Roads.ResetRedrawThrottle();
                Roads.Notify_HistoryEnded(); // one real invalidation for whatever the checks changed
            }
            double limit = AutoTest.Plan == "stress" ? 1500d : 400d;
            var sw = Stopwatch.StartNew();
            layer.RegenerateNow();
            double first = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            layer.RegenerateNow();
            double second = sw.Elapsed.TotalMilliseconds;
            Log.Message($"[LivingWorld:metrics] roads regen ms first={first:F1} second={second:F1} tiles={surface.TilesCount}");
            if (Math.Max(first, second) > limit) bad.Add($"roads (redraw): a full regeneration of the roads layer took {Math.Max(first, second):F0} ms (regression guard {limit:F0})");
        }

        // ---- A player's link reaches the roads layer ----

        private static readonly FieldInfo subMeshesField = AccessTools.Field(typeof(WorldDrawLayerBase), "subMeshes");

        /// <summary>Vertices in the layer's meshes, and how many of them lie within one tile of the given tile centres.</summary>
        private static void CountVerts(WorldDrawLayer layer, PlanetLayer surface, List<Vector3> centres, out int all, out int near)
        {
            all = near = 0;
            float reach = surface.AverageTileSize;
            foreach (LayerSubMesh m in (List<LayerSubMesh>)subMeshesField.GetValue(layer))
            {
                all += m.verts.Count;
                foreach (Vector3 v in m.verts)
                    foreach (Vector3 c in centres)
                        if ((v - c).sqrMagnitude < reach * reach)
                        {
                            near++;
                            break;
                        }
            }
        }

        /// <summary>
        /// A player's new link (the way the caravan job lays it) marks the roads layer dirty at once with the throttle open, and the
        /// layer's next rebuild holds geometry for it: more vertices than before, and some of them at the laid tiles. This is what the
        /// UI tour's "road built" step shows; the layer is rebuilt in the test with <c>RegenerateNow</c> because nothing is drawn here.
        /// </summary>
        private static void PlayerLinkDrawn(List<string> bad)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            var layer = surface.GetLayer<WorldDrawLayer_Roads>();
            if (layer == null)
            {
                Log.Message(Tag + "no roads draw layer in the headless world: the player link case is skipped");
                return;
            }
            if (!FindChain(surface, 4, out List<int> chain))
            {
                Log.Message(Tag + "no bare chain for the player link case");
                return;
            }
            var laid = new List<(int a, int b)>();
            try
            {
                Roads.WorldRenderedOverride = true; // the world view is open: only an urgent change may pass the throttle
                Roads.ResetRedrawThrottle();
                layer.RegenerateNow();
                var centres = chain.Select(t => surface.GetTileCenter(t)).ToList();
                CountVerts(layer, surface, centres, out int before, out int nearBefore);
                if (layer.Dirty) bad.Add("roads (drawn): the roads layer is dirty right after it was rebuilt");
                for (int i = 0; i + 1 < chain.Count; i++)
                {
                    if (Roads.LaySegment(chain[i], chain[i + 1], LWRoadDefOf.StoneRoad, player: true) != LayResult.Changed) bad.Add($"roads (drawn): the player's link {i} was not laid");
                    else laid.Add((chain[i], chain[i + 1]));
                }
                int redraws = Roads.StatRedraws;
                Roads.FlushRedraw();
                if (Roads.StatRedraws != redraws + 1 || !layer.Dirty) bad.Add($"roads (drawn): after a player's links and a flush the roads layer is {(layer.Dirty ? "dirty" : "clean")} ({Roads.StatRedraws - redraws} invalidations)");
                layer.RegenerateNow();
                CountVerts(layer, surface, centres, out int after, out int nearAfter);
                if (after <= before) bad.Add($"roads (drawn): the roads layer has {after} vertices after {laid.Count} player links, it had {before}");
                if (nearAfter <= nearBefore) bad.Add($"roads (drawn): no geometry was added at the laid tiles ({nearBefore} vertices near them before, {nearAfter} after)");
                Log.Message($"{Tag}player link drawn: {laid.Count} links, layer vertices {before} → {after}, near the tiles {nearBefore} → {nearAfter}");
            }
            finally
            {
                for (int i = laid.Count - 1; i >= 0; i--) Roads.RestoreLink(laid[i].a, laid[i].b, null);
                Roads.WorldRenderedOverride = false; // the view is "hidden": the layer is marked at once, so the next rebuild drops the fixture
                Roads.FlushRedraw();
                Roads.WorldRenderedOverride = null;
                Roads.ResetRedrawThrottle();
            }
        }

        // ---- Live caravans (normal plan) ----

        /// <summary>
        /// Four caravans of temporary colonists, each through the real click path (OnTargetChosen), watched hourly, each with milestones
        /// that must be reached before its deadline: a case that cannot be set up, is lost or runs out of time fails the run (it is not a
        /// skip). Judged cases are removed from the world at once. The cases need a run of at least four days (the halfway save is in the middle).
        /// </summary>
        private static class Live
        {
            private sealed class Case
            {
                public string name;
                public readonly List<string> pawnIds = new List<string>();
                public PlanetTile start, dest;
                public RoadDef def;
                public List<int> route = new List<int>();
                public bool started, retired;
                public int startTick, jobId;
                public string verdict, problem;
                // dirt: held at link 1, put into its travelling phase for the save, compared after the reload
                public bool held, snapped;
                public Progress saved, loaded;
                // stone
                public int blocksGiven, expectedPaid;
                // waiting
                public bool waited, blocksAdded;
                public int waitIndex, blocksAddedTick;
            }

            private const int Day = GenDate.TicksPerDay;
            /// <summary>Time a case may take to reach each of its milestones (a night's rest and a few days' walks included).</summary>
            private const int Allowance = Day * 2;

            private static readonly Case dirt = new Case { name = "dirt" }, stone = new Case { name = "stone" },
                waiting = new Case { name = "waiting" }, ordered = new Case { name = "ordered" };
            private static readonly Case[] all = { dirt, stone, waiting, ordered };
            private static bool inited, reloaded;
            private static int reloadTick;
            /// <summary>The stone fixture's pre-laid link and what it was before (restored in <see cref="Finish"/>).</summary>
            private static (int a, int b, RoadDef was)? prelaid;

            private static bool Applies => AutoTest.Days >= 4;

            private static Caravan CaravanOf(Case k)
            {
                if (k.pawnIds.Count == 0) return null;
                foreach (Caravan c in Find.WorldObjects.Caravans)
                    foreach (Pawn p in c.PawnsListForReading)
                        if (p.ThingID == k.pawnIds[0]) return c;
                return null;
            }

            private static string Why(Case k) => Roads.CancelledJobs.TryGetValue(k.jobId, out string why) ? why : "no record";

            private static string State(Case k)
            {
                Caravan c = CaravanOf(k);
                CompRoadBuilder comp = c?.GetComponent<CompRoadBuilder>();
                return c == null ? "no caravan" : comp == null ? "no comp" : comp.HasJob ? $"{comp.Phase} at link {comp.JobIndex}/{comp.JobEdges}, moving {c.pather.Moving}" : "no job";
            }

            private static string Mass(Caravan c) => $"mass {c.MassUsage:F1} of {c.MassCapacity:F1} kg";

            /// <summary>For a case that ran out of time: what the walk was doing (cost of the hop, where the pather is going, how far it has to go, what stops it).</summary>
            private static string Diagnose(Case k, Caravan c, CompRoadBuilder comp)
            {
                Caravan_PathFollower pather = c.pather;
                IReadOnlyList<int> tiles = comp.JobTiles;
                int next = tiles != null && comp.JobIndex + 1 < tiles.Count ? tiles[comp.JobIndex + 1] : -1;
                string hop = "?";
                if (next >= 0 && Roads.Surface() is PlanetLayer surface)
                    hop = $"{Caravan_PathFollower.CostToMove(c, c.Tile, new PlanetTile(next, surface))} ticks";
                return $"caravan at {c.Tile.tileId}, link target {next}, hop costs {hop}, pather destination {pather.Destination.tileId} next tile {pather.nextTile.tileId} "
                    + $"cost left {pather.nextTileCostLeft:F0}/{pather.nextTileCostTotal:F0}, path nodes left {pather.curPath?.NodesLeftCount.ToString() ?? "none"}, "
                    + $"paused {pather.Paused}, cantMove {c.CantMove}, nightResting {c.NightResting}, owned move {comp.JobOwnsMove}, {Mass(c)}, ticks per move {c.TicksPerMove}, "
                    + $"route {string.Join(">", k.route)}";
            }

            /// <summary>The case is over (judged or failed): its job is cancelled and the caravan leaves the world (and the storyteller's reach).</summary>
            private static void Retire(Case k)
            {
                if (k.retired) return;
                k.retired = true;
                Caravan c = CaravanOf(k);
                if (c == null || c.Destroyed) return;
                c.GetComponent<CompRoadBuilder>()?.Cancel(stopOwnMovement: true, null);
                DisposeCaravan(c);
            }

            private static void Fail(Case k, string why)
            {
                if (k.problem != null || k.verdict != null) return;
                k.problem = why;
                liveProblems.Add($"roads (live {k.name}): {why}");
                Retire(k);
            }

            private static void Pass(Case k, string how)
            {
                k.verdict = how;
                Log.Message($"{Tag}live {k.name}: {how}");
                Retire(k);
            }

            private static bool Open(Case k) => k.verdict == null && k.problem == null;

            /// <summary>Right before the halfway save: the dirt job (held at link 1) goes into its travelling phase; the waiting job must be waiting.</summary>
            public static void Notify_BeforeSave()
            {
                if (!Applies || !LivingWorldMod.Settings.roads) return;
                if (!inited)
                {
                    liveProblems.Add("roads (live): the live caravan cases had not started when the halfway save came");
                    return;
                }
                if (Open(dirt))
                {
                    Caravan c = CaravanOf(dirt);
                    CompRoadBuilder comp = c?.GetComponent<CompRoadBuilder>();
                    if (comp == null || !comp.HasJob || !dirt.held || comp.Phase != RoadPhase.Working) Fail(dirt, $"no held working job at the halfway save ({State(dirt)})");
                    else
                    {
                        comp.TestHold = false;
                        if (!comp.Test_CompleteLinkNow() || comp.Phase != RoadPhase.Travelling || !c.pather.Moving || !comp.JobOwnsMove)
                            Fail(dirt, $"could not put the held job into its travelling phase for the save ({State(dirt)})");
                        else
                        {
                            dirt.saved = Progress.Of(c, comp);
                            dirt.snapped = true;
                            Log.Message($"{Tag}live dirt: job {comp.JobId} saved while travelling: {dirt.saved}");
                        }
                    }
                }
                if (Open(waiting))
                {
                    CompRoadBuilder comp = CaravanOf(waiting)?.GetComponent<CompRoadBuilder>();
                    if (comp == null || !waiting.waited || !comp.HasJob || comp.Phase != RoadPhase.WaitingForMaterials) Fail(waiting, $"not waiting for materials at the halfway save ({State(waiting)})");
                }
            }

            /// <summary>The game is back from the reload (nothing has ticked): the saved dirt job must be where it was saved.</summary>
            public static void Notify_Reloaded()
            {
                reloaded = true;
                reloadTick = Find.TickManager.TicksGame;
                if (!Applies || !inited || !Open(dirt) || !dirt.snapped) return;
                Caravan c = CaravanOf(dirt);
                CompRoadBuilder comp = c?.GetComponent<CompRoadBuilder>();
                if (comp == null || !comp.HasJob) Fail(dirt, "the job did not survive the reload");
                else
                {
                    dirt.loaded = Progress.Of(c, comp);
                    if (dirt.loaded.tile != dirt.saved.tile || dirt.loaded.index != dirt.saved.index || dirt.loaded.phase != RoadPhase.Travelling || !c.pather.Moving)
                        Fail(dirt, $"after the reload the job is {dirt.loaded} (moving {c.pather.Moving}), it was saved as {dirt.saved}");
                }
            }

            public static void Hourly()
            {
                if (!Applies || !Roads.Active || Find.CurrentMap == null || Neighbourhood.Home == null) return;
                try
                {
                    if (!inited) Init();
                    foreach (Case k in all) Update(k);
                }
                catch (Exception e)
                {
                    liveProblems.Add($"roads (live): {e.GetType().Name}: {e.Message}");
                    foreach (Case k in all) Fail(k, "the live cases stopped on an exception");
                }
            }

            private static void Init()
            {
                inited = true;
                LivingWorldTuningDef t = Tuning.T;
                savedTuning = t;
                savedWork = t.roadWorkPerEdge;
                t.roadWorkPerEdge = 9000f; // the test's own pace: a link in a few hours, so the short roads end inside the run
                PlanetLayer surface = Find.WorldGrid.Surface;
                var used = new HashSet<int>();
                Setup(dirt, surface, used, LWRoadDefOf.DirtRoad, 6, 10, 1);
                Setup(stone, surface, used, LWRoadDefOf.StoneRoad, 2, 2, 2);
                Setup(waiting, surface, used, LWRoadDefOf.StoneRoad, 2, 2, 1);
                Setup(ordered, surface, used, LWRoadDefOf.DirtRoad, 3, 6, 1);
            }

            private static void Setup(Case k, PlanetLayer surface, HashSet<int> used, RoadDef def, int minEdges, int maxEdges, int pawnCount)
            {
                k.def = def;
                if (!FindJobSites(surface, minEdges, maxEdges, used, out k.start, out k.dest))
                {
                    Fail(k, $"no sites for the fixture ({minEdges}-{maxEdges} links of bare land from the colony)");
                    return;
                }
                var pawns = new List<Pawn>();
                for (int i = 0; i < pawnCount; i++)
                {
                    Pawn p = TempColonist();
                    if (p != null) pawns.Add(p);
                }
                if (pawns.Count < pawnCount)
                {
                    Fail(k, "not enough construction-capable temporary colonists for the fixture");
                    return;
                }
                if (k == stone)
                {
                    // Two carriers with different kinds of block, neither enough for the whole road but together over it, and light enough
                    // that the caravan is not immobilised by mass (a granite block weighs 1.25 kg, a pawn carries 35 kg with its meals and gear).
                    int each = Tuning.T.roadStoneBlocksPerEdge * 3 / 5;
                    Give(pawns[0], ThingDefOf.BlocksGranite, each);
                    Give(pawns[1], DefDatabase<ThingDef>.GetNamed("BlocksSandstone"), each);
                    k.blocksGiven = each * 2;
                }
                foreach (Pawn p in pawns)
                {
                    // A random pawn's weapon and clothes are part of the caravan's mass; the fixtures must not depend on them.
                    p.equipment?.DestroyAllEquipment();
                    p.apparel?.DestroyAll();
                    k.pawnIds.Add(p.ThingID);
                }
                MakeCaravan(k.start, pawns.ToArray());
            }

            private static void Update(Case k)
            {
                if (!Open(k) || k.retired) return;
                Caravan c = CaravanOf(k);
                if (c == null || c.Destroyed)
                {
                    Fail(k, "the caravan is gone before the case was judged");
                    return;
                }
                CompRoadBuilder comp = c.GetComponent<CompRoadBuilder>();
                if (comp == null)
                {
                    Fail(k, "the caravan has no road builder comp");
                    return;
                }
                if (!k.started) StartCase(k, c, comp);
                else if (k == dirt) UpdateDirt(k, c, comp);
                else if (k == stone) UpdateStone(k, c, comp);
                else if (k == waiting) UpdateWaiting(k, c, comp);
                else UpdateOrdered(k, c, comp);
            }

            private static void StartCase(Case k, Caravan caravan, CompRoadBuilder comp)
            {
                if (caravan.pather.Moving || caravan.Tile != k.start)
                {
                    Fail(k, "the caravan is not standing at its start tile");
                    return;
                }
                if (caravan.ImmobilizedByMass)
                {
                    Fail(k, $"the fixture caravan is immobilised by mass ({Mass(caravan)}), it could never walk");
                    return;
                }
                if (k == stone)
                {
                    // The first link of the road is stone already: it is walked over, not paid for.
                    var tiles = new List<int>();
                    if (Roads.PlanRoute(k.start, k.dest, 30, tiles, out RouteReject _) && tiles.Count == 3)
                    {
                        // Laid for the fixture only: its ledger entry has no claim and is not the player's, so nothing would ever age it
                        // out; Finish puts the link back as it was.
                        RoadDef was = Roads.RoadBetween(tiles[0], tiles[1]);
                        if (Roads.LaySegment(tiles[0], tiles[1], LWRoadDefOf.StoneRoad) == LayResult.Changed) prelaid = (tiles[0], tiles[1], was);
                    }
                }
                if (!comp.OnTargetChosen(k.def, k.start, new GlobalTargetInfo(k.dest)) || !comp.HasJob)
                {
                    Fail(k, "the click path did not start a job");
                    return;
                }
                k.started = true;
                k.startTick = Find.TickManager.TicksGame;
                k.jobId = comp.JobId;
                k.route = new List<int>(comp.JobTiles);
                if (k == stone)
                    for (int i = 0; i + 1 < k.route.Count; i++)
                        if (Roads.Need(k.route[i], k.route[i + 1], LWRoadDefOf.StoneRoad) == EdgeNeed.Needs) k.expectedPaid += Tuning.T.roadStoneBlocksPerEdge;
                if (k == stone && k.expectedPaid != Tuning.T.roadStoneBlocksPerEdge) Fail(k, $"the fixture expects to pay {k.expectedPaid} blocks, not one link's worth");
                Log.Message($"{Tag}live {k.name}: job {comp.JobId} over {comp.JobEdges} links of {k.def.defName}, {k.pawnIds.Count} colonists, {CompRoadBuilder.StoneBlocksOwned(caravan)} blocks, expected to pay {k.expectedPaid}");
            }

            private static bool TimedOut(Case k) => Find.TickManager.TicksGame - k.startTick > Allowance;

            /// <summary>Held at link 1 until the halfway save (see <see cref="Notify_BeforeSave"/>); after the reload it must really move on.</summary>
            private static void UpdateDirt(Case k, Caravan c, CompRoadBuilder comp)
            {
                if (!comp.HasJob)
                {
                    Fail(k, $"the job ended ({(Roads.FinishedJobs.Contains(k.jobId) ? "finished before the halfway save" : "cancelled: " + Why(k))})");
                    return;
                }
                if (comp.JobId != k.jobId)
                {
                    Fail(k, "the job's id changed");
                    return;
                }
                if (!reloaded)
                {
                    if (k.held) return;
                    if (comp.JobIndex >= 1 && comp.Phase == RoadPhase.Working)
                    {
                        comp.TestHold = true;
                        k.held = true;
                        Log.Message($"{Tag}live dirt: held working at link {comp.JobIndex}/{comp.JobEdges} until the halfway save");
                    }
                    else if (TimedOut(k)) Fail(k, $"never reached link 1 in the working phase ({State(k)})");
                    return;
                }
                if (!k.snapped)
                {
                    Fail(k, "no job was saved while travelling, so none crossed the reload");
                    return;
                }
                Progress now = Progress.Of(c, comp);
                if (comp.JobIndex < k.saved.index) Fail(k, $"the job went back from link {k.saved.index} to {comp.JobIndex} across the reload");
                else if (Progress.Advanced(k.loaded, now)) Pass(k, $"saved travelling, after the reload {k.loaded} → {now}");
                else if (Find.TickManager.TicksGame - reloadTick > Allowance) Fail(k, $"no progress in {Allowance / (double)Day:F1} days after the reload ({k.loaded} → {now}, moving {c.pather.Moving})");
            }

            /// <summary>Must finish, pay exactly one link's blocks and leave stone links all along the road.</summary>
            private static void UpdateStone(Case k, Caravan c, CompRoadBuilder comp)
            {
                if (comp.HasJob)
                {
                    if (TimedOut(k)) Fail(k, $"not finished in time ({State(k)}, {k.blocksGiven - CompRoadBuilder.StoneBlocksOwned(c)} blocks paid; {Diagnose(k, c, comp)})");
                    return;
                }
                if (!Roads.FinishedJobs.Contains(k.jobId))
                {
                    Fail(k, $"the job ended without finishing ({Why(k)})");
                    return;
                }
                int paid = k.blocksGiven - CompRoadBuilder.StoneBlocksOwned(c);
                if (paid != k.expectedPaid)
                {
                    Fail(k, $"paid {paid} blocks, expected {k.expectedPaid}");
                    return;
                }
                RoadState st = Roads.State;
                int playerEdges = 0;
                for (int i = 0; i + 1 < k.route.Count; i++)
                {
                    if (st.Edge(Roads.EdgeKey(k.route[i], k.route[i + 1])) is RoadEdge e && e.player) playerEdges++;
                    if ((Roads.RoadBetween(k.route[i], k.route[i + 1])?.priority ?? 0) < LWRoadDefOf.StoneRoad.priority)
                    {
                        Fail(k, $"link {i} is not stone");
                        return;
                    }
                }
                if (playerEdges != k.expectedPaid / Tuning.T.roadStoneBlocksPerEdge) Fail(k, $"{playerEdges} links belong to the player, {k.expectedPaid / Tuning.T.roadStoneBlocksPerEdge} were paid for");
                else Pass(k, $"finished, paid {paid} blocks for {playerEdges} link(s)");
            }

            /// <summary>Waits for materials before the save, still waits after the reload, then the blocks come, exactly one link is paid and the job goes on.</summary>
            private static void UpdateWaiting(Case k, Caravan c, CompRoadBuilder comp)
            {
                if (!comp.HasJob)
                {
                    Fail(k, $"the job ended ({(Roads.FinishedJobs.Contains(k.jobId) ? "finished without any blocks" : "cancelled: " + Why(k))})");
                    return;
                }
                if (!k.waited)
                {
                    if (comp.Phase == RoadPhase.WaitingForMaterials)
                    {
                        k.waited = true;
                        k.waitIndex = comp.JobIndex;
                        if (Roads.Need(k.route[k.waitIndex], k.route[k.waitIndex + 1], LWRoadDefOf.StoneRoad) != EdgeNeed.Needs) Fail(k, "the link was laid without the blocks");
                        else Log.Message($"{Tag}live waiting: waiting for materials at link {k.waitIndex}");
                    }
                    else if (TimedOut(k)) Fail(k, $"never reached the material wait ({State(k)})");
                    return;
                }
                if (!reloaded)
                {
                    if (comp.Phase != RoadPhase.WaitingForMaterials || comp.JobIndex != k.waitIndex) Fail(k, $"left the material wait without any materials ({State(k)})");
                    return;
                }
                if (!k.blocksAdded)
                {
                    if (comp.Phase != RoadPhase.WaitingForMaterials || comp.JobIndex != k.waitIndex)
                    {
                        Fail(k, $"after the reload the job is {comp.Phase} at link {comp.JobIndex}, it was waiting at link {k.waitIndex}");
                        return;
                    }
                    k.blocksAdded = true;
                    k.blocksAddedTick = Find.TickManager.TicksGame;
                    k.blocksGiven = Tuning.T.roadStoneBlocksPerEdge;
                    Give(c.PawnsListForReading[0], ThingDefOf.BlocksGranite, k.blocksGiven);
                    return;
                }
                if (Roads.Need(k.route[k.waitIndex], k.route[k.waitIndex + 1], LWRoadDefOf.StoneRoad) == EdgeNeed.Satisfied)
                {
                    int left = CompRoadBuilder.StoneBlocksOwned(c);
                    bool resumed = comp.Phase != RoadPhase.WaitingForMaterials || comp.JobIndex != k.waitIndex;
                    if (left != 0) Fail(k, $"{left} of {k.blocksGiven} blocks left after one link was laid (should be none)");
                    else if (!resumed) Fail(k, "the link was laid but the job stayed in the material wait");
                    else Pass(k, $"waited across the reload, paid one link, went on as {comp.Phase}");
                }
                else if (Find.TickManager.TicksGame - k.blocksAddedTick > Day) Fail(k, "the blocks were there for a day and no link was paid for");
            }

            /// <summary>An ordinary move order ends the job at once (not at the comp's next step) and leaves the caravan its own walk.</summary>
            private static void UpdateOrdered(Case k, Caravan c, CompRoadBuilder comp)
            {
                if (!comp.HasJob)
                {
                    Fail(k, $"the job ended before the order ({Why(k)})");
                    return;
                }
                if (comp.Phase != RoadPhase.Working)
                {
                    if (Find.TickManager.TicksGame - k.startTick > Day / 2) Fail(k, $"never in the working phase ({State(k)})");
                    return;
                }
                // The player sends it somewhere else: a neighbouring tile that is not on the road.
                PlanetLayer surface = Find.WorldGrid.Surface;
                var around = new List<PlanetTile>();
                surface.GetTileNeighbors(c.Tile, around);
                PlanetTile orderTile = PlanetTile.Invalid;
                foreach (PlanetTile n in around)
                    if (!k.route.Contains(n.tileId) && Roads.Buildable(surface, c.Tile.tileId, n.tileId))
                    {
                        orderTile = n;
                        break;
                    }
                if (!orderTile.Valid || !c.pather.StartPath(orderTile, null, repathImmediately: true))
                {
                    Fail(k, "no tile beside the road to send it to, or the order was refused");
                    return;
                }
                if (comp.HasJob) Fail(k, "the job survived an ordinary move order");
                else if (Why(k) != "LW_Roads_Cancelled_Ordered") Fail(k, $"the job ended as '{Why(k)}', not as an ordinary move order");
                else if (!c.pather.Moving && c.Tile != orderTile) Fail(k, "the caravan's own order was stopped along with the job");
                else Pass(k, "an ordinary move order ended the job and kept the caravan's own walk");
            }

            /// <summary>End of the run: every case must have been judged; the test pace is put back and nothing is left in the world.</summary>
            public static IEnumerable<string> Finish()
            {
                var bad = new List<string>();
                if (savedTuning != null)
                {
                    savedTuning.roadWorkPerEdge = savedWork;
                    savedTuning = null;
                }
                if (prelaid is { } link)
                {
                    Roads.RestoreLink(link.a, link.b, link.was);
                    prelaid = null;
                }
                if (!LivingWorldMod.Settings.roads) return bad;
                if (!Applies)
                {
                    Log.Message($"{Tag}live caravan cases need a run of at least 4 days (this one is {AutoTest.Days}): not run");
                    return bad;
                }
                if (!inited)
                {
                    bad.Add("roads (live): the live caravan cases never started");
                    return bad;
                }
                foreach (Case k in all)
                {
                    if (Open(k)) bad.Add($"roads (live {k.name}): not judged by the end of the run ({State(k)})");
                    Log.Message($"{Tag}live {k.name} at the end: {k.verdict ?? k.problem ?? "open"}");
                    Retire(k);
                }
                return bad;
            }
        }
    }

    /// <summary>
    /// The road tests' caravans are no targets for the storyteller. Defence in depth, not the cure of the normal plan's hang (that was a
    /// war band's interception, see <c>Interception.TryEncounter</c>): a caravan incident (meeting, demand, ambush) opens a dialog,
    /// jumps the camera to the world view and may generate a map in a long event, none of which works in a headless run. Only
    /// autotests, only the temporary colonists' caravans. Applied by <c>LivingWorldStartup</c> only when a test is armed (not a
    /// <c>[HarmonyPatch]</c> class, which would be patched in every game).
    /// </summary>
    public static class Patch_Storyteller_NoRoadTestCaravans
    {
        public static void Apply(Harmony harmony) =>
            harmony.Patch(AccessTools.PropertyGetter(typeof(Storyteller), nameof(Storyteller.AllIncidentTargets)),
                postfix: new HarmonyMethod(typeof(Patch_Storyteller_NoRoadTestCaravans), nameof(Postfix)));

        public static void Postfix(List<IIncidentTarget> __result)
        {
            if (AutoTest.Active) __result.RemoveAll(t => t is Caravan c && RoadChecks.IsTestCaravan(c));
        }
    }
}
