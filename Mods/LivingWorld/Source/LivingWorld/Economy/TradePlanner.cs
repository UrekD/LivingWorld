using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>A market comparison: a town that pays more than a normal price for a good the colony could sell.</summary>
    public struct SellRow
    {
        public CompSettlementLife town;
        public CommodityDef good;
        /// <summary>The player's sell factor there (<see cref="MarketTrade.PlayerFactorAt"/>).</summary>
        public float factor;
        /// <summary>What the colony has of the good as counted resources; -1 when none of the good's things are counted.</summary>
        public int have;
        public int ticks;
        /// <summary><see cref="ticks"/> is the estimator's value (else an estimate from the distance).</summary>
        public bool exact, road;
        public string note;
    }

    /// <summary>A trip worth looking at: buy a thing at one town, sell it at another. Stock is never checked.</summary>
    public struct RouteRow
    {
        public CommodityDef good;
        public ThingDef rep;
        public CompSettlementLife buyAt, sellAt;
        public float buyFactor, sellFactor, estProfit;
        public int ticksToBuy, ticksOnward;
        /// <summary>The first leg (the colony to the buying town) holds the estimator's value. The second is always worked out from the distance.</summary>
        public bool exact;
        public string negotiator;
    }

    /// <summary>
    /// The trade-route planner behind the Routes view of the Markets page. No save data, no world changes: it reads the
    /// markets, never generates a town's stock, and works out the colony's travel times to the towns a path search at a time.
    /// </summary>
    public static class TradePlanner
    {
        public const int SellShown = 15, RoutesShown = 10;
        private const int MaxReps = 31;
        private const float ColdReachMs = 0.25f;
        private const int BucketTicks = 15000;
        private const int VanillaTicksPerMove = 3300;
        private const float CaravanTicksPerTile = VanillaTicksPerMove * 1.3f;
        /// <summary>Towns taken into the rows in one frame, so the work of a rebuild is spread over a few frames.</summary>
        private const int MaxTownsPerFrame = 10;
        /// <summary>Frames the path work waits after a frame that went over its time budget (one search can be far over it), at most.</summary>
        private const int MaxHoldFrames = 30;

        private enum Reach : byte { Unknown, Yes, No }

        private enum Status : byte { Idle, NoColony, PricesOff, Ready }

        private sealed class Candidate
        {
            public CompSettlementLife town;
            public TraderKindDef kind;
            public KindInfo info;
            public PlanetTile tile;
            public int id;
            public float tiles;
            public bool titled, road, roadKnown;
            public Faction faction;
            public Reach reach;
            /// <summary>Place among the reachable towns (the order they were admitted in).</summary>
            public int slot;
            public float[] buy, sell;
            public string note;
            /// <summary>The estimate from the distance, in ticks, from the colony and to each reachable town (by slot).</summary>
            public int homeApprox;
            public int[] approx;
            /// <summary>The colony's trip to the town as the rows use it (set at each <see cref="Resort"/>).</summary>
            public int homeTicks;
            public bool homeExact;
        }

        private sealed class KindInfo
        {
            public int[] sellMask, buyMask;
            /// <summary>The trader's price type multiplier for the player buying, by (good, representative) = good * MaxReps + k; where it sells or takes the representative.</summary>
            public float[] buyPrice;
        }

        private struct SellItem
        {
            public Candidate town;
            public int good, ticks;
            public bool exact;
            public float score;
        }

        private struct RouteItem
        {
            public Candidate buy, sell;
            public int good;
            public ThingDef rep;
            public float est, score;
            public int ticksOnward;
            public bool exact;
        }

        private static readonly List<Candidate> eligible = new List<Candidate>(), near = new List<Candidate>();
        private static readonly List<SellItem> sellPool = new List<SellItem>(), sellTop = new List<SellItem>();
        private static readonly List<RouteItem> routePool = new List<RouteItem>(), routeTop = new List<RouteItem>();
        private static readonly Dictionary<Faction, string> factionFaults = new Dictionary<Faction, string>();
        private static readonly List<SellRow> sell = new List<SellRow>();
        private static readonly List<RouteRow> routes = new List<RouteRow>();
        private static readonly Dictionary<long, int> pathCache = new Dictionary<long, int>();
        /// <summary>The paths found, by trip (null: no path), kept for one road network: a new departure time only needs the estimate worked out along them again.</summary>
        private static readonly Dictionary<long, WorldPath> pathStore = new Dictionary<long, WorldPath>();
        private static readonly Dictionary<CompSettlementLife, Candidate> candidateOf = new Dictionary<CompSettlementLife, Candidate>();
        private static readonly Dictionary<TraderKindDef, KindInfo> kinds = new Dictionary<TraderKindDef, KindInfo>();
        private static List<ThingDef>[] reps;
        private static float[][] repSellFactor;
        private static int[] have = new int[0];
        private static bool[] counted = new bool[0];

        private static Status status;
        private static Map homeMap;
        private static PlanetTile homeTile = PlanetTile.Invalid;
        private static CommodityDef filter;
        private static bool onlyHave, dirty = true;
        private static int builtDay = -1, builtRoads = -1, stampRoads = -1, stampBucket = -1, probeIndex, reachable, lastStepFrame = -1;
        private static bool builtPrices;
        private static float negotiatorGain;
        private static string negotiatorName;
        private static float tradeLoss;
        private static string negotiatorLabel;
        private static int holdFrames;

        public static int PathsLastFrame, ColdReachLastFrame, StatRebuilds, StatSearches, StatColdReach, MaxPathsInFrame;
        /// <summary>
        /// The cost of the last rebuild (taking the candidates), of working out the rows since (towns taken in, rows picked), the slowest single path
        /// operation (a search, an estimate along a kept path or a reachability check) of the last frame and since the last rebuild, and the most rows work in one frame.
        /// The counters above and these maxima start again at each rebuild, so the daily metric line is about one pass.
        /// </summary>
        public static double LastRebuildMs, LastRowsMs, PathMsMax, PathMsLastFrame, FrameLongestMs, LongestSearchMs, RowsMsMax;

        /// <summary>Tests: stand in for <see cref="Time.frameCount"/>, the road version and the time of day.</summary>
        internal static int? TestFrame, TestRoadsVersion, TestTicksAbs;

        public static IReadOnlyList<SellRow> Sell => sell;
        public static IReadOnlyList<RouteRow> Routes => routes;

        /// <summary>What the page says instead of rows: no colony to plan from, prices off. Null when there is something to show.</summary>
        public static string Unavailable => status == Status.NoColony ? (LivingWorldComponent.Instance != null && Find.AnyPlayerHomeMap != null ? "LW_Routes_NoColony" : "LW_Routes_NoHome").Translate().Resolve()
            : status == Status.PricesOff ? "LW_Routes_PricesOff".Translate().Resolve() : null;

        /// <summary>The planner is still working out who is within reach (no row can be shown before that).</summary>
        public static bool Searching => status == Status.Ready && probeIndex < eligible.Count && reachable < Tuning.T.plannerTowns;

        /// <summary>The towns the planner is looking at (reachable ones first, in order of distance).</summary>
        public static int TownCount => reachable;

        /// <summary>Shown legs still waiting for the estimator (the colony's trips to the towns; the leg between two towns is only ever approximate).</summary>
        public static int Pending
        {
            get
            {
                int n = 0;
                foreach (SellRow r in sell)
                    if (!r.exact && !Failed(homeTile, r.town.Settlement.Tile)) n++;
                foreach (RouteRow r in routes)
                    if (!Cached(homeTile, r.buyAt.Settlement.Tile) && !Failed(homeTile, r.buyAt.Settlement.Tile)) n++;
                return n;
            }
        }

        /// <summary>Tests: how many trips hold an estimator value (or a "no path") for the cached departure time, and a way to forget them.</summary>
        internal static int CachedLegs => pathCache.Count;

        internal static int KeptPaths => pathStore.Count;

        internal static int HoldFramesLeft => holdFrames;

        internal static void TestClearCache()
        {
            pathCache.Clear();
            pathStore.Clear();
        }

        internal static IReadOnlyList<CompSettlementLife> Towns(bool reachableOnly)
        {
            var list = new List<CompSettlementLife>();
            foreach (Candidate c in eligible)
                if (!reachableOnly || c.reach == Reach.Yes) list.Add(c.town);
            return list;
        }

        /// <summary>The next draw rebuilds (the page opened).</summary>
        public static void Invalidate() => dirty = true;

        /// <summary>The colony's trip to the town has the estimator's value.</summary>
        public static bool HomeLegExact(CompSettlementLife town) => homeMap != null && Cached(homeTile, town.Settlement.Tile);

        /// <summary>The colony's trip to the town has no estimator value to wait for (the estimator found no path): the row keeps its approximate time.</summary>
        public static bool HomeLegFailed(CompSettlementLife town) => homeMap != null && Failed(homeTile, town.Settlement.Tile);

        /// <summary>A new world: nothing from the previous one may leak through statics.</summary>
        public static void Reset()
        {
            eligible.Clear();
            near.Clear();
            sellPool.Clear();
            routePool.Clear();
            sell.Clear();
            routes.Clear();
            pathCache.Clear();
            pathStore.Clear();
            candidateOf.Clear();
            kinds.Clear();
            factionFaults.Clear();
            reps = null;
            repSellFactor = null;
            status = Status.Idle;
            homeMap = null;
            homeTile = PlanetTile.Invalid;
            filter = null;
            onlyHave = false;
            dirty = true;
            builtDay = builtRoads = stampRoads = stampBucket = lastStepFrame = -1;
            probeIndex = reachable = holdFrames = 0;
            PathsLastFrame = ColdReachLastFrame = StatRebuilds = StatSearches = StatColdReach = MaxPathsInFrame = 0;
            LastRebuildMs = LastRowsMs = PathMsMax = PathMsLastFrame = FrameLongestMs = LongestSearchMs = RowsMsMax = 0d;
            negotiatorLabel = null;
            TestFrame = TestRoadsVersion = TestTicksAbs = null;
        }

        private static int FrameNow => TestFrame ?? Time.frameCount;
        private static int RoadsNow => TestRoadsVersion ?? Roads.Version;
        private static int TicksAbsNow => TestTicksAbs ?? Find.TickManager.TicksAbs;
        private static int DayNow => Find.TickManager.TicksGame / GenDate.TicksPerDay;

        // ---- Rebuild ----

        /// <summary>Rebuilds when the page's inputs, the day, the colony or the roads changed, or a shown row went stale. True when it did.</summary>
        public static bool RebuildIfStale(Map home, CommodityDef goodFilter, bool haveOnly)
        {
            bool sameWorld = !dirty && home == homeMap && home != null && home.Tile == homeTile && DayNow == builtDay
                             && RoadsNow == builtRoads && LivingWorldMod.Settings.marketPrices == builtPrices;
            if (sameWorld && goodFilter == filter && haveOnly == onlyHave) return false;
            if (sameWorld) Refilter(goodFilter, haveOnly);
            else Rebuild(home, goodFilter, haveOnly);
            return true;
        }

        /// <summary>A new good filter or "what I have" setting over the towns already taken: the candidates and who is within reach stay, only the rows are picked again.</summary>
        private static void Refilter(CommodityDef goodFilter, bool haveOnly)
        {
            filter = goodFilter;
            onlyHave = haveOnly;
            sell.Clear();
            routes.Clear();
            if (status != Status.Ready) return;
            var sw = Stopwatch.StartNew();
            sellPool.Clear();
            routePool.Clear();
            CountHave(homeMap);
            foreach (Candidate c in near) AddRows(c);
            Resort();
            LastRowsMs = sw.Elapsed.TotalMilliseconds;
            if (LastRowsMs > RowsMsMax) RowsMsMax = LastRowsMs;
        }

        /// <summary>Takes the candidate towns and works out every row from the markets as they are now. No path search runs here (<see cref="StepPaths"/> does those).</summary>
        public static void Rebuild(Map home, CommodityDef goodFilter, bool haveOnly)
        {
            var sw = Stopwatch.StartNew();
            eligible.Clear();
            near.Clear();
            candidateOf.Clear();
            sellPool.Clear();
            routePool.Clear();
            sell.Clear();
            routes.Clear();
            probeIndex = reachable = 0;
            LastRowsMs = 0d;
            StatSearches = StatColdReach = MaxPathsInFrame = 0;
            PathMsMax = LongestSearchMs = RowsMsMax = 0d;
            PlanetTile newTile = home?.Tile ?? PlanetTile.Invalid;
            if (newTile != homeTile) pathStore.Clear();
            homeMap = home;
            homeTile = newTile;
            filter = goodFilter;
            onlyHave = haveOnly;
            ApplyStamp();
            dirty = false;
            builtDay = DayNow;
            builtRoads = RoadsNow;
            builtPrices = LivingWorldMod.Settings.marketPrices;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (home == null || comp == null) status = Status.NoColony;
            else if (!builtPrices) status = Status.PricesOff;
            else
            {
                status = Status.Ready;
                tradeLoss = Find.Storyteller.difficulty.tradePriceFactorLoss;
                FindNegotiator(home);
                CountHave(home);
                factionFaults.Clear();
                List<CompSettlementLife> all = comp.activeSettlements;
                for (int i = 0; i < all.Count; i++)
                {
                    CompSettlementLife life = all[i];
                    if (Fault(life, home, factionFaults) != null) continue;
                    Settlement s = life.Settlement;
                    TraderKindDef kind = s.TraderKind;
                    PlanetTile tile = s.Tile;
                    var candidate = new Candidate
                    {
                        town = life, kind = kind, faction = s.Faction, tile = tile, id = s.ID, tiles = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, tile),
                        titled = kind.TitleRequiredToTrade != null,
                    };
                    eligible.Add(candidate);
                    candidateOf[life] = candidate;
                }
                factionFaults.Clear();
                eligible.Sort(ByDistance);
            }
            StatRebuilds++;
            LastRebuildMs = sw.Elapsed.TotalMilliseconds;
        }

        private static readonly System.Comparison<Candidate> ByDistance = (a, b) =>
        {
            int c = a.tiles.CompareTo(b.tiles);
            return c != 0 ? c : a.id.CompareTo(b.id);
        };

        /// <summary>
        /// Why a town is not a candidate (null: it is): not on the map, not simulated (its market is then not the town's), someone's base is on its map,
        /// the colony's own, a permanent enemy or hostile, no trader kind, nothing it will trade now (the check does not generate its stock), or on another layer than the colony.
        /// </summary>
        internal static string CandidateFault(CompSettlementLife life, Map home) => Fault(life, home, null);

        /// <summary>What a faction's towns are excluded for, whatever the town: the player's own, permanent enemies, hostile ones (null: none).</summary>
        private static string FactionFault(Faction f) =>
            f.IsPlayer ? "player" : f.def.permanentEnemy ? "permanentEnemy" : f.HostileTo(Faction.OfPlayer) ? "hostile" : null;

        /// <summary>The fault of a town; the answers per faction are kept in <paramref name="memo"/> (a scan over every town asks the same few factions again and again).</summary>
        private static string Fault(CompSettlementLife life, Map home, Dictionary<Faction, string> memo)
        {
            Settlement s = life?.Settlement;
            if (s == null || !s.Spawned || s.Destroyed) return "gone";
            if (!life.Active) return "inactive";
            if (s.HasMap) return "map";
            Faction f = s.Faction;
            if (f == null) return "player";
            string byFaction;
            if (memo == null) byFaction = FactionFault(f);
            else if (!memo.TryGetValue(f, out byFaction)) memo[f] = byFaction = FactionFault(f);
            if (byFaction != null) return byFaction;
            if (s.TraderKind == null) return "noTrader";
            if (!s.CanTradeNow) return "cannotTradeNow";
            if (home == null || s.Tile.Layer != home.Tile.Layer) return "layer";
            return null;
        }

        private static void FindNegotiator(Map home)
        {
            negotiatorGain = 0f;
            negotiatorName = null;
            foreach (Pawn p in home.mapPawns.FreeColonistsSpawned)
            {
                if (StatDefOf.TradePriceImprovement.Worker.IsDisabledFor(p)) continue;
                float gain = p.GetStatValue(StatDefOf.TradePriceImprovement);
                if (negotiatorName != null && gain <= negotiatorGain) continue;
                negotiatorGain = gain;
                negotiatorName = p.LabelShort;
            }
            negotiatorLabel = negotiatorName ?? "LW_Routes_NoNegotiator".Translate().Resolve();
        }

        private static void CountHave(Map home)
        {
            int n = Commodities.Count;
            if (have.Length != n)
            {
                have = new int[n];
                counted = new bool[n];
            }
            System.Array.Clear(have, 0, n);
            System.Array.Clear(counted, 0, n);
            foreach (KeyValuePair<ThingDef, int> kv in home.resourceCounter.AllCountedAmounts)
            {
                CommodityDef c = Commodities.Of(kv.Key);
                if (c == null) continue;
                counted[c.index] = true;
                have[c.index] += kv.Value;
            }
        }

        // ---- Representatives ----

        /// <summary>Things that stand for a commodity in a trade: its commission goods, then its listed things (items with a value that can change hands).</summary>
        internal static List<ThingDef> RepsOf(CommodityDef c)
        {
            if (reps == null || reps.Length != Commodities.Count)
            {
                reps = new List<ThingDef>[Commodities.Count];
                repSellFactor = new float[Commodities.Count][];
                kinds.Clear();
            }
            List<ThingDef> list = reps[c.index];
            if (list != null) return list;
            list = reps[c.index] = new List<ThingDef>();
            foreach (CommissionGood g in c.commissionGoods) AddRep(list, c, g.thing);
            foreach (ThingDef d in c.thingDefs) AddRep(list, c, d);
            // The stat is read once here, not for every pair of towns.
            var factors = new float[list.Count];
            for (int k = 0; k < factors.Length; k++) factors[k] = list[k].GetStatValueAbstract(StatDefOf.SellPriceFactor);
            repSellFactor[c.index] = factors;
            return list;
        }

        private static void AddRep(List<ThingDef> list, CommodityDef c, ThingDef d)
        {
            if (d == null || list.Count >= MaxReps || list.Contains(d) || d.category != ThingCategory.Item || d.tradeability == Tradeability.None) return;
            if (Commodities.Of(d) != c || d.BaseMarketValue <= 0f) return;
            list.Add(d);
        }

        /// <summary>The town will take the thing from the player (a stock generator handles it, and the player may sell it).</summary>
        internal static bool WillBuy(TraderKindDef kind, ThingDef d) => d.tradeability.PlayerCanSell() && kind.WillTrade(d);

        /// <summary>The town can have the thing for sale: a generator that makes goods (not one that only buys) handles it, and the player may buy it.</summary>
        internal static bool WillSell(TraderKindDef kind, ThingDef d)
        {
            if (!d.tradeability.TraderCanSell()) return false;
            List<StockGenerator> gens = kind.stockGenerators;
            for (int i = 0; i < gens.Count; i++)
            {
                StockGenerator g = gens[i];
                if (g is StockGenerator_BuyExpensiveSimple || g is StockGenerator_BuySingleDef || g is StockGenerator_BuySlaves || g is StockGenerator_BuyTradeTag) continue;
                if (g.HandlesThingDef(d)) return true;
            }
            return false;
        }

        private static KindInfo InfoOf(TraderKindDef kind)
        {
            if (kinds.TryGetValue(kind, out KindInfo info)) return info;
            int n = Commodities.Count;
            info = new KindInfo { sellMask = new int[n], buyMask = new int[n], buyPrice = new float[n * MaxReps] };
            for (int i = 0; i < n; i++)
            {
                List<ThingDef> list = RepsOf(Commodities.All[i]);
                for (int k = 0; k < list.Count; k++)
                {
                    bool sells = WillSell(kind, list[k]), buys = WillBuy(kind, list[k]);
                    if (sells) info.sellMask[i] |= 1 << k;
                    if (buys) info.buyMask[i] |= 1 << k;
                    // A town that takes a thing has a buy price for it too: vanilla holds what it pays to that price.
                    if (sells || buys) info.buyPrice[i * MaxReps + k] = kind.PriceTypeFor(list[k], TradeAction.PlayerBuys).PriceMultiplier();
                }
            }
            kinds[kind] = info;
            return info;
        }

        /// <summary>The first representative of the good that the buying town can sell and the selling town will take (null: none, no route).</summary>
        internal static ThingDef RouteRep(TraderKindDef buyAtKind, TraderKindDef sellAtKind, CommodityDef c)
        {
            KindInfo b = InfoOf(buyAtKind), a = InfoOf(sellAtKind);
            int mask = b.sellMask[c.index] & a.buyMask[c.index];
            if (mask == 0) return null;
            int bit = 0;
            while ((mask & (1 << bit)) == 0) bit++;
            return RepsOf(c)[bit];
        }

        /// <summary>A kind's mask over the good's representatives (tests).</summary>
        internal static bool SellsAny(TraderKindDef kind, CommodityDef c) => InfoOf(kind).sellMask[c.index] != 0;

        internal static bool BuysAny(TraderKindDef kind, CommodityDef c) => InfoOf(kind).buyMask[c.index] != 0;

        /// <summary>
        /// What selling a thing at one market (A) brings against what buying it at another (B) costs, by vanilla's rules (buy ×1.4, sell ×0.6, the trader's
        /// price type for the buy, the thing's sell price factor, the difficulty's loss, the negotiator and the settlement's 2 %). Above 1 is a profit.
        /// The price A pays is held to what A itself would sell the thing for, as the game holds it: first before the market's factors (vanilla), then after them
        /// (the trade patch), so a fair, fame or a very good negotiator cannot lift the estimate past what the trade screen would pay.
        /// </summary>
        internal static float Estimate(float sellPriceFactor, float priceTypeBuyB, float buyFactorB, float sellFactorA, float negotiator, float loss, float priceTypeBuyA, float buyFactorA)
        {
            float gain = 1f + negotiator + 0.02f, pay = Mathf.Max(0.05f, 1f - negotiator - 0.02f);
            float sellA = 0.6f * sellPriceFactor * (1f - loss) * gain;
            float buyA = 1.4f * priceTypeBuyA * (1f + loss) * pay;
            float got = Mathf.Min(Mathf.Min(sellA, buyA) * sellFactorA, buyA * buyFactorA);
            float cost = 1.4f * priceTypeBuyB * (1f + loss) * pay * buyFactorB;
            return got / Mathf.Max(0.0001f, cost);
        }

        internal static float EstimateFor(ThingDef rep, TraderKindDef buyAtKind, TraderKindDef sellAtKind, float buyFactorB, float sellFactorA, float buyFactorA, float negotiator, float loss) =>
            Estimate(rep.GetStatValueAbstract(StatDefOf.SellPriceFactor), buyAtKind.PriceTypeFor(rep, TradeAction.PlayerBuys).PriceMultiplier(), buyFactorB, sellFactorA, negotiator, loss,
                sellAtKind.PriceTypeFor(rep, TradeAction.PlayerBuys).PriceMultiplier(), buyFactorA);

        // ---- Rows ----

        /// <summary>Takes a reachable town in: its price factors, its place among the reachable ones, and its share of the rows (pairs with the towns taken before it).</summary>
        private static void Admit(Candidate c)
        {
            int n = Commodities.Count;
            c.info ??= InfoOf(c.kind);
            c.buy = new float[n];
            c.sell = new float[n];
            MarketTrade.PlayerFactorsAt(c.town, c.buy, c.sell);
            c.note = c.titled ? "LW_Routes_NeedsTitle".Translate().Resolve() : null;
            c.slot = near.Count;
            c.homeApprox = ApproxTicks(homeTile, c.tile);
            c.approx = new int[System.Math.Max(Tuning.T.plannerTowns, c.slot + 1)];
            for (int k = 0; k < near.Count; k++)
            {
                Candidate u = near[k];
                c.approx[k] = ApproxTicks(c.tile, u.tile);
                if (u.approx.Length <= c.slot) System.Array.Resize(ref u.approx, c.slot + 1);
                u.approx[c.slot] = ApproxTicks(u.tile, c.tile);
            }
            near.Add(c);
            reachable++;
            AddRows(c);
        }

        /// <summary>The colony is joined to the town by road (worked out when a row first needs it).</summary>
        private static bool RoadOf(Candidate c)
        {
            if (!c.roadKnown)
            {
                c.road = Roads.Connected(homeMap, c.town.Settlement);
                c.roadKnown = true;
            }
            return c.road;
        }

        /// <summary>
        /// The candidate rows of a town: where to sell each good there, and the trips with it as the selling town or the buying one, against the towns
        /// taken before it (every pair is seen once, whatever order the towns come in: the pools are sorted whole).
        /// </summary>
        private static void AddRows(Candidate t)
        {
            int n = Commodities.Count;
            float minSell = Tuning.T.plannerMinSellFactor, minProfit = Tuning.T.plannerMinProfit;
            for (int i = 0; i < n; i++)
            {
                CommodityDef good = Commodities.All[i];
                if (filter != null && filter != good) continue;
                if (t.sell[i] >= minSell && t.info.buyMask[i] != 0 && (!onlyHave || have[i] > 0)) sellPool.Add(new SellItem { town = t, good = i });
                if (t.titled) continue;
                for (int k = 0; k < t.slot; k++)
                {
                    Candidate u = near[k];
                    if (u.titled) continue;
                    AddRoute(u, t, i, minProfit);
                    AddRoute(t, u, i, minProfit);
                }
            }
        }

        /// <summary>A trip for a good, buying at one town and selling at the other, when some representative fits and the estimate is worth listing.</summary>
        private static void AddRoute(Candidate buyAt, Candidate sellAt, int i, float minProfit)
        {
            int mask = buyAt.info.sellMask[i] & sellAt.info.buyMask[i];
            if (mask == 0) return;
            int bit = 0;
            while ((mask & (1 << bit)) == 0) bit++;
            int at = i * MaxReps + bit;
            float est = Estimate(repSellFactor[i][bit], buyAt.info.buyPrice[at], buyAt.buy[i], sellAt.sell[i], negotiatorGain, tradeLoss, sellAt.info.buyPrice[at], sellAt.buy[i]);
            if (est < minProfit) return;
            routePool.Add(new RouteItem { buy = buyAt, sell = sellAt, good = i, rep = reps[i][bit], est = est });
        }

        private static int ApproxTicks(PlanetTile a, PlanetTile b) => Mathf.RoundToInt(Find.WorldGrid.ApproxDistanceInTiles(a, b) * CaravanTicksPerTile);

        private static long Key(PlanetTile a, PlanetTile b) => ((long)a.tileId << 32) | (uint)b.tileId;

        private static bool Cached(PlanetTile a, PlanetTile b) => pathCache.TryGetValue(Key(a, b), out int t) && t > 0;

        private static bool Failed(PlanetTile a, PlanetTile b) => pathCache.TryGetValue(Key(a, b), out int t) && t < 0;

        private static int CompareSell(in SellItem x, in SellItem y)
        {
            int c = y.score.CompareTo(x.score);
            if (c != 0) return c;
            c = x.town.id.CompareTo(y.town.id);
            return c != 0 ? c : x.good.CompareTo(y.good);
        }

        private static int CompareRoute(in RouteItem x, in RouteItem y)
        {
            int c = y.score.CompareTo(x.score);
            if (c != 0) return c;
            c = x.buy.id.CompareTo(y.buy.id);
            if (c != 0) return c;
            c = x.sell.id.CompareTo(y.sell.id);
            return c != 0 ? c : x.good.CompareTo(y.good);
        }

        /// <summary>Keeps the best few in order. The order is a total one (ties end on ids and the good), so this is what sorting everything and taking the top gives.</summary>
        private static void TakeSell(in SellItem it)
        {
            int pos = sellTop.Count;
            while (pos > 0 && CompareSell(it, sellTop[pos - 1]) < 0) pos--;
            if (pos >= SellShown) return;
            sellTop.Insert(pos, it);
            if (sellTop.Count > SellShown) sellTop.RemoveAt(SellShown);
        }

        private static void TakeRoute(in RouteItem it)
        {
            int pos = routeTop.Count;
            while (pos > 0 && CompareRoute(it, routeTop[pos - 1]) < 0) pos--;
            if (pos >= RoutesShown) return;
            routeTop.Insert(pos, it);
            if (routeTop.Count > RoutesShown) routeTop.RemoveAt(RoutesShown);
        }

        /// <summary>Takes the best rows of the pools by their current travel times, so an exact time that arrives can change which rows show and in what order.</summary>
        private static void Resort()
        {
            sell.Clear();
            routes.Clear();
            sellTop.Clear();
            routeTop.Clear();
            float maxDays = Tuning.T.plannerMaxDays;
            // The colony's trip to each reachable town, once for all the rows that use it.
            foreach (Candidate c in near)
            {
                if (pathCache.TryGetValue(Key(homeTile, c.tile), out int t) && t > 0)
                {
                    c.homeTicks = t;
                    c.homeExact = true;
                }
                else
                {
                    c.homeTicks = c.homeApprox;
                    c.homeExact = false;
                }
            }
            for (int k = 0; k < sellPool.Count; k++)
            {
                SellItem it = sellPool[k];
                it.ticks = it.town.homeTicks;
                it.exact = it.town.homeExact;
                float days = it.ticks / (float)GenDate.TicksPerDay;
                if (days > maxDays) continue;
                it.score = (it.town.sell[it.good] - 1f) / (1f + days / 5f);
                TakeSell(it);
            }
            for (int k = 0; k < routePool.Count; k++)
            {
                RouteItem it = routePool[k];
                it.ticksOnward = it.buy.approx[it.sell.slot];
                it.exact = it.buy.homeExact;
                float days = (it.buy.homeTicks + it.ticksOnward) / (float)GenDate.TicksPerDay;
                it.score = (it.est - 1f) / (1f + days / 5f);
                TakeRoute(it);
            }
            foreach (SellItem it in sellTop)
            {
                Candidate c = it.town;
                sell.Add(new SellRow
                {
                    town = c.town, good = Commodities.All[it.good], factor = c.sell[it.good], have = counted[it.good] ? have[it.good] : -1, ticks = it.ticks, exact = it.exact,
                    road = RoadOf(c), note = c.note,
                });
            }
            foreach (RouteItem it in routeTop)
            {
                routes.Add(new RouteRow
                {
                    good = Commodities.All[it.good], rep = it.rep, buyAt = it.buy.town, sellAt = it.sell.town, buyFactor = it.buy.buy[it.good], sellFactor = it.sell.sell[it.good],
                    estProfit = it.est, ticksToBuy = it.buy.homeTicks, ticksOnward = it.ticksOnward, exact = it.exact, negotiator = negotiatorLabel,
                });
            }
        }

        // ---- Paths ----

        /// <summary>
        /// The times are kept for one departure bucket (six hours) and one road network. A new bucket drops the times but keeps the paths (the next steps only
        /// work the estimate out along them again); a road change drops both, and rebuilds.
        /// </summary>
        private static void ApplyStamp()
        {
            int roads = RoadsNow, bucket = TicksAbsNow / BucketTicks;
            if (roads == stampRoads && bucket == stampBucket) return;
            if (roads != stampRoads)
            {
                if (stampRoads >= 0) dirty = true;
                pathStore.Clear();
            }
            stampRoads = roads;
            stampBucket = bucket;
            pathCache.Clear();
        }

        /// <summary>
        /// Once per frame (repeated IMGUI events in a frame do nothing): decides who is within reach, nearest first (a few towns a frame), then asks for the
        /// colony's travel time to the towns of the shown rows, in order (the leg between two towns is only ever approximate). A time is a path search, or, for a
        /// path found before in this bucket's predecessor, a cheap estimate along it. Stops at <c>plannerPathsPerFrame</c> searches (a cold reachability check
        /// counts as one) or <c>plannerPathMsPerFrame</c> milliseconds, whichever comes first. A single search cannot be divided and can run far past the time,
        /// so a frame that did leaves the next ones without path work, in proportion, and the average stays within the time. New results re-sort the rows.
        /// </summary>
        public static void StepPaths()
        {
            int frame = FrameNow;
            if (frame == lastStepFrame) return;
            lastStepFrame = frame;
            PathsLastFrame = ColdReachLastFrame = 0;
            PathMsLastFrame = FrameLongestMs = 0d;
            if (status != Status.Ready || homeMap == null) return;
            var sw = Stopwatch.StartNew();
            double rowsMs = 0d;
            int bucketBefore = stampBucket;
            ApplyStamp();
            bool rowsChanged = stampBucket != bucketBefore, reachChanged = false;
            bool hold = holdFrames > 0;
            if (hold) holdFrames--;
            WorldReachability reachability = Find.World.reachability;
            int towns = Tuning.T.plannerTowns, maxSearches = Tuning.T.plannerPathsPerFrame, taken = 0;
            double maxMs = Tuning.T.plannerPathMsPerFrame;
            // The time budget is for the path work; working out the rows from the markets is counted apart.
            bool Within() => PathsLastFrame < maxSearches && sw.Elapsed.TotalMilliseconds - rowsMs < maxMs;
            while (!hold && reachable < towns && probeIndex < eligible.Count && taken < MaxTownsPerFrame && Within())
            {
                Candidate c = eligible[probeIndex++];
                long t0 = Stopwatch.GetTimestamp();
                bool ok = reachability.CanReach(homeTile, c.tile);
                double reachMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                if (reachMs > ColdReachMs)
                {
                    Longest(reachMs);
                    PathsLastFrame++;
                    ColdReachLastFrame++;
                }
                c.reach = ok ? Reach.Yes : Reach.No;
                if (!ok) continue;
                long f0 = Stopwatch.GetTimestamp();
                Admit(c);
                double admitMs = (Stopwatch.GetTimestamp() - f0) * 1000.0 / Stopwatch.Frequency;
                rowsMs += admitMs;
                LastRowsMs += admitMs;
                taken++;
                reachChanged = true;
            }
            if (reachChanged || rowsChanged)
            {
                long r0 = Stopwatch.GetTimestamp();
                Resort();
                double ms = (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;
                rowsMs += ms;
                LastRowsMs += ms;
            }
            bool got = false;
            if (!hold && !(reachChanged && Searching))
            {
                foreach (SellRow r in sell)
                {
                    if (r.exact || !Within()) continue;
                    got |= Search(homeTile, r.town.Settlement.Tile);
                }
                foreach (RouteRow r in routes)
                {
                    PlanetTile b = r.buyAt.Settlement.Tile;
                    if (!Cached(homeTile, b) && Within()) got |= Search(homeTile, b);
                }
            }
            if (got)
            {
                long r1 = Stopwatch.GetTimestamp();
                Resort();
                rowsMs += (Stopwatch.GetTimestamp() - r1) * 1000.0 / Stopwatch.Frequency;
            }
            PathMsLastFrame = sw.Elapsed.TotalMilliseconds - rowsMs;
            if (PathMsLastFrame > PathMsMax) PathMsMax = PathMsLastFrame;
            if (rowsMs > RowsMsMax) RowsMsMax = rowsMs;
            if (PathsLastFrame > MaxPathsInFrame) MaxPathsInFrame = PathsLastFrame;
            if (maxMs > 0d && PathMsLastFrame > maxMs) holdFrames = Mathf.Clamp(Mathf.CeilToInt((float)(PathMsLastFrame / maxMs)) - 1, 1, MaxHoldFrames);
            StatSearches += PathsLastFrame;
            StatColdReach += ColdReachLastFrame;
        }

        private static void Longest(double ms)
        {
            if (ms > FrameLongestMs) FrameLongestMs = ms;
            if (ms > LongestSearchMs) LongestSearchMs = ms;
        }

        /// <summary>
        /// The estimator's time for one trip, unless this pair has been tried for this departure bucket (an answer of 0 is kept as "no path"). A path found
        /// before is estimated along again (no search, not counted as one); otherwise this is a path search.
        /// </summary>
        private static bool Search(PlanetTile a, PlanetTile b)
        {
            long key = Key(a, b);
            if (pathCache.ContainsKey(key)) return false;
            long t0 = Stopwatch.GetTimestamp();
            int ticks;
            bool searched = false;
            if (pathStore.TryGetValue(key, out WorldPath kept))
            {
                ticks = kept == null ? 0 : CaravanArrivalTimeEstimator.EstimatedTicksToArrive(a, b, kept, 0f, VanillaTicksPerMove, TicksAbsNow);
            }
            else
            {
                searched = true;
                using WorldPath found = a.Layer.Pather.FindPath(a, b, null);
                ticks = found.Found ? CaravanArrivalTimeEstimator.EstimatedTicksToArrive(a, b, found, 0f, VanillaTicksPerMove, TicksAbsNow) : 0;
                pathStore[key] = found.Found ? Copy(found) : null;
            }
            Longest((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            pathCache[key] = ticks > 0 ? ticks : -1;
            if (searched) PathsLastFrame++;
            return ticks > 0;
        }

        /// <summary>A path of its own (the found one goes back to the game's pool, which counts them against the caravans).</summary>
        private static WorldPath Copy(WorldPath found)
        {
            var own = new WorldPath();
            List<PlanetTile> nodes = found.NodesReversed;
            for (int i = 0; i < nodes.Count; i++) own.AddNodeAtStart(nodes[i]);
            own.SetupFound(found.TotalCost, found.Layer);
            return own;
        }

        // ---- Staleness ----

        /// <summary>Hides the shown rows whose towns are no longer fit (captured, hostile, no longer simulated, mapped, nothing to trade, gone) or changed hands or trader kind since the rebuild, and flags a rebuild. O(rows).</summary>
        public static void ValidateShown()
        {
            if (status != Status.Ready || homeMap == null) return;
            bool removed = false;
            for (int i = sell.Count - 1; i >= 0; i--)
            {
                if (!Stale(sell[i].town)) continue;
                sell.RemoveAt(i);
                removed = true;
            }
            for (int i = routes.Count - 1; i >= 0; i--)
            {
                if (!Stale(routes[i].buyAt) && !Stale(routes[i].sellAt)) continue;
                routes.RemoveAt(i);
                removed = true;
            }
            if (!removed) return;
            // The pools may hold the same town: they stay empty until the rebuild.
            sellPool.Clear();
            routePool.Clear();
            dirty = true;
        }

        /// <summary>The town is not fit now, or its owner or trader kind (what the row's masks and factors came from) is not the one the rows were built with. A new owner can follow the kind and the market.</summary>
        private static bool Stale(CompSettlementLife town)
        {
            if (CandidateFault(town, homeMap) != null) return true;
            Settlement s = town.Settlement;
            return candidateOf.TryGetValue(town, out Candidate c) && (s.TraderKind != c.kind || s.Faction != c.faction);
        }

        /// <summary>The line of the daily metrics.</summary>
        public static string MetricsLine() =>
            $"planner ms={LastRebuildMs + LastRowsMs:F2} pathMsMax={PathMsMax:F2} longest={LongestSearchMs:F2} paths={StatSearches} maxPerFrame={MaxPathsInFrame} coldReach={StatColdReach} rebuilds={StatRebuilds} towns={reachable}/{eligible.Count} sell={sell.Count} routes={routes.Count} rowsMsMax={RowsMsMax:F2}";
    }
}
