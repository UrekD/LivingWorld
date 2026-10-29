using System.Collections.Generic;
using RimWorld;
using Verse;

namespace LivingWorld
{
    public enum RouteState : byte { Open, Blocked, Finished, Abandoned }

    /// <summary>
    /// One undirected road link between neighbouring surface tiles that this mod changed: what was there before, what it
    /// wrote last, and who holds it up. Links the mod never changed have no entry. Defs are saved by name.
    /// </summary>
    public class RoadEdge : IExposable
    {
        /// <summary>(lower tile id, higher tile id), see <see cref="Roads.EdgeKey"/>.</summary>
        public long key;
        /// <summary>The def found before the mod first wrote this link (null: none).</summary>
        public string baseline;
        /// <summary>The def the mod wrote last: the link counts as ours only while it still reads this on both tiles.</summary>
        public string written;
        /// <summary>Ids of the NPC routes that laid it.</summary>
        public List<int> claims = new List<int>();
        /// <summary>A player's caravan laid it: never degraded.</summary>
        public bool player;

        public RoadDef BaselineDef => baseline == null ? null : DefDatabase<RoadDef>.GetNamedSilentFail(baseline);

        public RoadDef WrittenDef => written == null ? null : DefDatabase<RoadDef>.GetNamedSilentFail(written);

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref baseline, "baseline");
            Scribe_Values.Look(ref written, "written");
            Scribe_Collections.Look(ref claims, "claims", LookMode.Value);
            Scribe_Values.Look(ref player, "player");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) claims ??= new List<int>();
        }
    }

    /// <summary>A road an NPC town builds between itself and a trade partner, one link a day.</summary>
    public class RoadRoute : IExposable
    {
        public int id;
        /// <summary>The builder's faction (loadID) when the route began: war and ownership are judged against it.</summary>
        public int factionId;
        /// <summary>World object ids of the builder town and the town at the other end (never rebound to a refounded town on the same tile).</summary>
        public int builderId, otherId;
        public List<int> tiles = new List<int>();
        public string def;
        /// <summary>Index into <see cref="tiles"/> of the next link to lay (link i joins tiles[i] and tiles[i + 1]).</summary>
        public int next;
        public RouteState state;
        /// <summary>Sim time the route has been blocked since (<see cref="FactionState.Never"/> when not blocked).</summary>
        public int blockedSince = FactionState.Never;
        public float condition = 1f;
        /// <summary>Last degrade check found it neglected (abandoned, an end gone or at war): its links no longer hold others up.</summary>
        public bool decaying;
        /// <summary>Links this route actually changed.</summary>
        public int changed;
        /// <summary>Silver the builder's treasury has paid for this route so far.</summary>
        public float paid;
        /// <summary>The links it holds (keys of the ledger entries that list this route as a claim).</summary>
        public List<long> claimed = new List<long>();

        public RoadDef Def => def == null ? null : DefDatabase<RoadDef>.GetNamedSilentFail(def);

        public bool Building => state == RouteState.Open || state == RouteState.Blocked;

        public long PairKey => Roads.PairKey(builderId, otherId);

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref factionId, "faction");
            Scribe_Values.Look(ref builderId, "builder");
            Scribe_Values.Look(ref otherId, "other");
            Scribe_Collections.Look(ref tiles, "tiles", LookMode.Value);
            Scribe_Values.Look(ref def, "def");
            Scribe_Values.Look(ref next, "next");
            Scribe_Values.Look(ref state, "state");
            Scribe_Values.Look(ref blockedSince, "blockedSince", FactionState.Never);
            Scribe_Values.Look(ref condition, "condition", 1f);
            Scribe_Values.Look(ref decaying, "decaying");
            Scribe_Values.Look(ref changed, "changed");
            Scribe_Values.Look(ref paid, "paid");
            Scribe_Collections.Look(ref claimed, "claimed", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                tiles ??= new List<int>();
                claimed ??= new List<long>();
            }
        }
    }

    /// <summary>Everything the roads feature saves in the world component. Pre-roads saves have none of it: all defaults are empty.</summary>
    public class RoadState : IExposable
    {
        public List<RoadEdge> edges = new List<RoadEdge>();
        public List<RoadRoute> routes = new List<RoadRoute>();
        /// <summary>Goods delivered between an unordered pair of settlements (<see cref="Roads.PairKey"/>), decaying daily.</summary>
        public Dictionary<long, float> traffic = new Dictionary<long, float>();
        /// <summary>Pair → sim time before which it is not planned again.</summary>
        public Dictionary<long, int> backoff = new Dictionary<long, int>();
        /// <summary>Towns the colony has already been credited with joining by road (once each).</summary>
        public List<int> joinedTowns = new List<int>();
        public int lastCheckTick = FactionState.Never, lastDegradeTick = FactionState.Never, lastFameTick = FactionState.Never;

        // Derived indexes (not saved).
        private readonly Dictionary<long, RoadEdge> edgeByKey = new Dictionary<long, RoadEdge>();
        private readonly Dictionary<int, RoadRoute> routeById = new Dictionary<int, RoadRoute>();
        private readonly Dictionary<long, RoadRoute> routeByPair = new Dictionary<long, RoadRoute>();

        public RoadEdge Edge(long key) => edgeByKey.TryGetValue(key, out RoadEdge e) ? e : null;

        public RoadRoute Route(int id) => routeById.TryGetValue(id, out RoadRoute r) ? r : null;

        /// <summary>The route record for the pair, whatever its state.</summary>
        public RoadRoute RouteFor(long pair) => routeByPair.TryGetValue(pair, out RoadRoute r) ? r : null;

        public void Add(RoadEdge e)
        {
            edges.Add(e);
            edgeByKey[e.key] = e;
        }

        public void Add(RoadRoute r)
        {
            routes.Add(r);
            routeById[r.id] = r;
            routeByPair[r.PairKey] = r;
        }

        /// <summary>Forgets the link's entry and lets every route that listed it let go. The road itself is not touched.</summary>
        public void Drop(RoadEdge e)
        {
            edges.Remove(e);
            edgeByKey.Remove(e.key);
            foreach (int id in e.claims)
                if (routeById.TryGetValue(id, out RoadRoute r)) r.claimed.Remove(e.key);
            e.claims.Clear();
        }

        public void Drop(RoadRoute r)
        {
            routes.Remove(r);
            routeById.Remove(r.id);
            if (routeByPair.TryGetValue(r.PairKey, out RoadRoute at) && at == r) routeByPair.Remove(r.PairKey);
            foreach (long key in r.claimed)
                if (edgeByKey.TryGetValue(key, out RoadEdge e)) e.claims.Remove(r.id);
            r.claimed.Clear();
        }

        public int OpenCount(int factionId = -1)
        {
            int n = 0;
            foreach (RoadRoute r in routes)
                if (r.Building && (factionId < 0 || r.factionId == factionId)) n++;
            return n;
        }

        public void ExposeData()
        {
            Scribe_Collections.Look(ref edges, "edges", LookMode.Deep);
            Scribe_Collections.Look(ref routes, "routes", LookMode.Deep);
            Scribe_Collections.Look(ref traffic, "traffic", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref backoff, "backoff", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref joinedTowns, "joinedTowns", LookMode.Value);
            Scribe_Values.Look(ref lastCheckTick, "lastCheck", FactionState.Never);
            Scribe_Values.Look(ref lastDegradeTick, "lastDegrade", FactionState.Never);
            Scribe_Values.Look(ref lastFameTick, "lastFame", FactionState.Never);
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;
            edges ??= new List<RoadEdge>();
            routes ??= new List<RoadRoute>();
            traffic ??= new Dictionary<long, float>();
            backoff ??= new Dictionary<long, int>();
            joinedTowns ??= new List<int>();
            // A def removed since the save: its entries cannot be judged any more (their links stay as they are).
            edges.RemoveAll(e => e == null || e.WrittenDef == null || (e.baseline != null && e.BaselineDef == null));
            routes.RemoveAll(r => r == null || r.Def == null);
            edgeByKey.Clear();
            foreach (RoadEdge e in edges) edgeByKey[e.key] = e;
            routeById.Clear();
            routeByPair.Clear();
            foreach (RoadRoute r in routes)
            {
                routeById[r.id] = r;
                routeByPair[r.PairKey] = r;
            }
        }
    }
}
