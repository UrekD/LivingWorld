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
    /// Player agency (PLAN §4) checks. The trade-route planner (plans normal and stress): the player's price factor against what the live trade
    /// patch makes of a real trade session, the candidate towns, the representative goods, the route estimate, the per-frame path budget and the
    /// staleness of shown rows. The planner changes nothing in the world; the checks put back what they touch (fame, fair, a town's owner, the
    /// price setting).
    /// </summary>
    public static class AgencyChecks
    {
        private static readonly List<string> problems = new List<string>();
        private static int testFrame = int.MaxValue / 2;
        /// <summary>Frames the planner may take to fill its rows: a search that is far over the frame time is followed by as many frames without path work.</summary>
        private const int FrameLimit = 3000;

        public static void Register()
        {
            if (AutoTest.Plan != "normal" && AutoTest.Plan != "stress") return;
            AutoTest.DailyMetrics.Add(PlannerMetrics);
            AutoTest.Checks.Add(PlannerChecks);
        }

        private static void Expect(bool ok, string message)
        {
            if (!ok) problems.Add("planner: " + message);
        }

        /// <summary>One planner pass a day: the cost of the rows, and of the path searches (these run on a test frame counter, a few a frame).</summary>
        private static string PlannerMetrics()
        {
            Map home = Neighbourhood.Home;
            if (home == null) return null;
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
            return TradePlanner.MetricsLine();
        }

        /// <summary>Frames of the planner's path step until it has nothing left to ask for (or the limit); the test frame counter stands in for the game's.</summary>
        private static int Drive(int limit)
        {
            int frames = 0;
            try
            {
                while (frames < limit)
                {
                    TradePlanner.TestFrame = ++testFrame;
                    TradePlanner.StepPaths();
                    frames++;
                    if (!TradePlanner.Searching && TradePlanner.Pending == 0) break;
                }
            }
            finally
            {
                TradePlanner.TestFrame = null;
            }
            return frames;
        }

        private static IEnumerable<string> PlannerChecks()
        {
            problems.Clear();
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Map home = Neighbourhood.Home;
            if (comp == null || home == null)
            {
                Log.Message("[LivingWorld:test] planner: no colony on the surface at the end (skipped)");
                return problems;
            }
            comp.RebuildIndexes(seedMissing: false);
            // A planner pass must not make any town generate its stock: remember who had been visited.
            var visited = new Dictionary<int, bool>();
            foreach (CompSettlementLife life in comp.activeSettlements)
                if (life.Settlement.trader != null) visited[life.Settlement.ID] = life.Settlement.trader.EverVisited;
            Run("off contract and unavailable states", () => States(home));
            Run("candidates", () => Candidates(comp, home));
            Run("representatives", Representatives);
            Run("estimate", Estimate);
            Run("rows", () => Rows(home));
            Run("filters", () => Filters(home));
            Run("travel", () => Travel(home));
            Run("staleness", () => Staleness(home));
            Run("price factor", () => Factors(comp, home));
            Run("performance", () => Performance(home));
            int changed = 0;
            foreach (CompSettlementLife life in comp.activeSettlements)
                if (visited.TryGetValue(life.Settlement.ID, out bool was) && life.Settlement.trader.EverVisited != was) changed++;
            Expect(changed == 0, $"{changed} towns generated their stock during the planner checks");
            TradePlanner.Rebuild(home, null, false);
            return problems;
        }

        private static void Run(string name, Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                problems.Add($"planner {name}: {e.GetType().Name}: {e.Message}");
                Log.Error($"[LivingWorld:test] planner {name} threw: {e}");
            }
        }

        // ---- Price factor against the live patch ----

        /// <summary>Stands in for a town's trader: its kind, a few unspawned things, and the market it belongs to (registered with <see cref="MarketTrade.TestOrigins"/>).</summary>
        private sealed class FakeTrader : ITrader
        {
            public TraderKindDef kind;
            public Faction faction;
            public readonly List<Thing> goods = new List<Thing>();
            public TraderKindDef TraderKind => kind;
            public IEnumerable<Thing> Goods => goods;
            public int RandomPriceFactorSeed => 1;
            public string TraderName => "planner test";
            public bool CanTradeNow => true;
            public float TradePriceImprovementOffsetForPlayer => 0.02f;
            public Faction Faction => faction;
            public TradeCurrency TradeCurrency => TradeCurrency.Silver;
            public IEnumerable<Thing> ColonyThingsWillingToBuy(Pawn playerNegotiator) => Enumerable.Empty<Thing>();
            public void GiveSoldThingToTrader(Thing toGive, int countToGive, Pawn playerNegotiator) { }
            public void GiveSoldThingToPlayer(Thing toGive, int countToGive, Pawn playerNegotiator) { }
        }

        private struct Price
        {
            public float buy, sell, buyAgain, afterTooltip;
        }

        /// <summary>Opens a trade session with the fake trader and reads each tradeable's prices (twice, and after the tooltip: the factor is applied once).</summary>
        private static Dictionary<ThingDef, Price> Prices(CompSettlementLife origin, List<Thing> things, Pawn negotiator, bool pricesOn, out float currencyBuy)
        {
            bool was = LivingWorldMod.Stored.marketPrices;
            LivingWorldMod.Stored.marketPrices = pricesOn;
            var trader = new FakeTrader { kind = origin.Settlement.TraderKind, faction = origin.Settlement.Faction };
            trader.goods.AddRange(things);
            MarketTrade.TestOrigins[trader] = origin;
            var result = new Dictionary<ThingDef, Price>();
            currencyBuy = -1f;
            try
            {
                TradeSession.SetupWith(trader, negotiator, giftMode: false);
                foreach (Tradeable t in TradeSession.deal.AllTradeables)
                {
                    if (t.IsCurrency)
                    {
                        currencyBuy = t.GetPriceFor(TradeAction.PlayerBuys);
                        continue;
                    }
                    var p = new Price { buy = t.GetPriceFor(TradeAction.PlayerBuys), sell = t.GetPriceFor(TradeAction.PlayerSells) };
                    p.buyAgain = t.GetPriceFor(TradeAction.PlayerBuys);
                    t.GetPriceTooltip(TradeAction.PlayerBuys);
                    p.afterTooltip = t.GetPriceFor(TradeAction.PlayerBuys);
                    result[t.ThingDef] = p;
                }
            }
            finally
            {
                TradeSession.Close();
                MarketTrade.TestOrigins.Remove(trader);
                LivingWorldMod.Stored.marketPrices = was;
            }
            return result;
        }

        private static bool Near(float a, float b) => Mathf.Abs(a - b) <= 1e-4f * Mathf.Max(1f, Mathf.Abs(b));

        private static void Factors(LivingWorldComponent comp, Map home)
        {
            Pawn negotiator = home.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !StatDefOf.TradePriceImprovement.Worker.IsDisabledFor(p));
            if (negotiator == null)
            {
                Log.Message("[LivingWorld:test] planner: no colonist able to trade (price factor check skipped)");
                return;
            }
            var defs = new List<ThingDef>();
            foreach (CommodityDef c in Commodities.All)
            {
                List<ThingDef> reps = TradePlanner.RepsOf(c);
                if (reps.Count > 0) defs.Add(reps[0]);
            }
            // A thing that is in no commodity (the patch applies nothing), found among the items a trader could hold (what class it is does not matter: the thing is made with its comps).
            ThingDef unmapped = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.category == ThingCategory.Item && d.tradeability != Tradeability.None && d.BaseMarketValue > 0f
                                                                                       && !d.MadeFromStuff && !d.IsCorpse && Commodities.Of(d) == null).OrderBy(d => d.tradeability == Tradeability.All ? 0 : 1).FirstOrDefault();
            if (unmapped != null) defs.Add(unmapped);
            // The same things in every session (a thing's quality is random): only the setting and the market differ between the sessions.
            var things = defs.Select(d => ThingMaker.MakeThing(d, GenStuff.DefaultStuffFor(d))).ToList();
            var towns = new List<CompSettlementLife>();
            List<CompSettlementLife> all = comp.activeSettlements.Where(s => s.Settlement.TraderKind != null && s.Settlement.Faction != null).ToList();
            for (int i = 0; i < 5 && all.Count > 0; i++) towns.Add(all[i * all.Count / 5]);
            // Fame and a fair, as they are and as they can be.
            (float fame, bool? fair)[] variants = { (float.NaN, null), (0.5f, false), (-0.7f, false), (0f, true), (1f, true) };
            int compared = 0, clamped = 0, unmappedSeen = 0, currencies = 0;
            bool setting = LivingWorldMod.Stored.marketPrices;
            LivingWorldMod.Stored.marketPrices = true;
            try
            {
                foreach (CompSettlementLife town in towns.Distinct())
                {
                    float fame = town.fame;
                    int fairUntil = town.fairUntilTicks;
                    try
                    {
                        foreach ((float fameValue, bool? fair) in variants)
                        {
                            if (!float.IsNaN(fameValue)) town.fame = fameValue;
                            if (fair.HasValue) town.fairUntilTicks = fair.Value ? Find.TickManager.TicksGame + GenDate.TicksPerDay : int.MinValue / 2;
                            string tag = $"{town.Settlement.Label} fame {town.fame:F2} fair {town.FairOn}";
                            Dictionary<ThingDef, Price> off = Prices(town, things, negotiator, pricesOn: false, out float offCurrency);
                            Dictionary<ThingDef, Price> on = Prices(town, things, negotiator, pricesOn: true, out float onCurrency);
                            Expect(Near(onCurrency, offCurrency) && Near(onCurrency, 1f), $"{tag}: silver is priced {onCurrency} with prices on, {offCurrency} off (must stay 1)");
                            currencies++;
                            TraderKindDef kind = town.Settlement.TraderKind;
                            foreach (ThingDef d in defs)
                            {
                                if (!on.TryGetValue(d, out Price pOn) || !off.TryGetValue(d, out Price pOff)) continue;
                                CommodityDef c = Commodities.Of(d);
                                float fBuy = MarketTrade.PlayerFactorAt(town, c, playerBuys: true), fSell = MarketTrade.PlayerFactorAt(town, c, playerBuys: false);
                                float expectBuy = pOff.buy * fBuy, expectSell = Mathf.Min(pOff.sell * fSell, expectBuy);
                                compared++;
                                Expect(Near(pOn.buy, expectBuy), $"{tag}: {d.defName} buys at {pOn.buy:F3}, expected {expectBuy:F3} (vanilla {pOff.buy:F3} x {fBuy:F3})");
                                Expect(Near(pOn.sell, expectSell), $"{tag}: {d.defName} sells at {pOn.sell:F3}, expected {expectSell:F3} (vanilla {pOff.sell:F3} x {fSell:F3})");
                                Expect(pOn.sell <= pOn.buy + 1e-4f, $"{tag}: {d.defName} sells ({pOn.sell}) above its buy price ({pOn.buy})");
                                Expect(pOn.buyAgain == pOn.buy && pOn.afterTooltip == pOn.buy, $"{tag}: {d.defName} price changed on a repeated read ({pOn.buy} -> {pOn.buyAgain} -> {pOn.afterTooltip}): scaled more than once");
                                if (c == null)
                                {
                                    unmappedSeen++;
                                    Expect(pOn.buy == pOff.buy && pOn.sell == pOff.sell, $"{tag}: unmapped {d.defName} was scaled");
                                }
                                if (pOn.sell < pOff.sell * fSell - 1e-3f) clamped++;
                                // The unpatched price, worked out independently of the tradeable, for a thing without sell bonuses.
                                Thing thing = things.First(x => x.def == d);
                                float gain = negotiator.GetStatValue(StatDefOf.TradePriceImprovement);
                                float vanillaBuy = TradeUtility.GetPricePlayerBuy(thing, kind.PriceTypeFor(d, TradeAction.PlayerBuys).PriceMultiplier(), 1f, gain, 0.02f);
                                Expect(Near(pOff.buy, vanillaBuy), $"{tag}: with prices off {d.defName} buys at {pOff.buy:F3}, vanilla says {vanillaBuy:F3}");
                            }
                        }
                    }
                    finally
                    {
                        town.fame = fame;
                        town.fairUntilTicks = fairUntil;
                    }
                }
                ClampCase(towns.FirstOrDefault(), things, negotiator);
            }
            finally
            {
                LivingWorldMod.Stored.marketPrices = setting;
            }
            Log.Message($"[LivingWorld:test] planner: price factor compared {compared} prices over {towns.Count} towns x {variants.Length} market states ({unmappedSeen} unmapped ({unmapped?.defName ?? "none found"}), {currencies} currency reads, {clamped} sells held to the buy price)");
            Expect(compared > 0, "no price was compared");
            Expect(unmapped != null && unmappedSeen > 0, $"no unmapped thing was compared (found {unmapped?.defName ?? "none"}, seen {unmappedSeen})");
        }

        /// <summary>The patch itself, given a buy and a sell price that are equal: the sell price may not end above the buy price, with fame and a fair that favour the seller.</summary>
        private static void ClampCase(CompSettlementLife town, List<Thing> things, Pawn negotiator)
        {
            if (town == null) return;
            Thing sample = things.FirstOrDefault(x => Commodities.Of(x.def) != null);
            if (sample == null) return;
            ThingDef d = sample.def;
            float fame = town.fame;
            int fairUntil = town.fairUntilTicks;
            bool was = LivingWorldMod.Stored.marketPrices;
            var trader = new FakeTrader { kind = town.Settlement.TraderKind, faction = town.Settlement.Faction };
            trader.goods.Add(sample);
            MarketTrade.TestOrigins[trader] = town;
            try
            {
                LivingWorldMod.Stored.marketPrices = true;
                town.fame = 1f;
                town.fairUntilTicks = Find.TickManager.TicksGame + GenDate.TicksPerDay;
                TradeSession.SetupWith(trader, negotiator, giftMode: false);
                Tradeable t = TradeSession.deal.AllTradeables.First(x => !x.IsCurrency);
                float buy = 10f, sell = 10f;
                Patch_Tradeable_InitPriceData.Postfix(t, true, ref buy, ref sell);
                float expected = 10f * MarketTrade.PlayerFactorAt(town, Commodities.Of(d), playerBuys: true);
                Expect(Near(buy, expected), $"clamp case: buy {buy} expected {expected}");
                Expect(sell == buy, $"clamp case: equal prices with fame and a fair ended as buy {buy}, sell {sell} (the sell price must be held to the buy price)");
                // Already-computed prices are left alone (the prefix's state says nothing was computed on this call).
                float b2 = 10f, s2 = 5f;
                Patch_Tradeable_InitPriceData.Postfix(t, false, ref b2, ref s2);
                Expect(b2 == 10f && s2 == 5f, "clamp case: prices that were not computed on this call were scaled");
            }
            finally
            {
                TradeSession.Close();
                MarketTrade.TestOrigins.Remove(trader);
                LivingWorldMod.Stored.marketPrices = was;
                town.fame = fame;
                town.fairUntilTicks = fairUntil;
            }
        }

        // ---- States ----

        private static void States(Map home)
        {
            bool was = LivingWorldMod.Stored.marketPrices;
            try
            {
                LivingWorldMod.Stored.marketPrices = false;
                TradePlanner.Rebuild(home, null, false);
                Drive(5);
                Expect(TradePlanner.Unavailable != null, "prices off: the view does not say so");
                Expect(TradePlanner.Sell.Count == 0 && TradePlanner.Routes.Count == 0, "prices off: rows listed");
                // The factor is 1 whatever the market says, fame and fairs included.
                CompSettlementLife any = LivingWorldComponent.Instance.activeSettlements.FirstOrDefault();
                if (any != null) Expect(MarketTrade.PlayerFactorAt(any, Commodities.All[0], true) == 1f && MarketTrade.PlayerFactorAt(any, Commodities.All[0], false) == 1f, "prices off: PlayerFactorAt is not 1");
            }
            finally
            {
                LivingWorldMod.Stored.marketPrices = was;
            }
            TradePlanner.Rebuild(null, null, false);
            Expect(TradePlanner.Unavailable != null && TradePlanner.Sell.Count == 0, "no colony: the view does not say so");
            Expect(MarketTrade.PlayerFactorAt(null, Commodities.All[0], true) == 1f && MarketTrade.PlayerFactorAt(LivingWorldComponent.Instance.activeSettlements.FirstOrDefault(), null, true) == 1f, "no market or no commodity: PlayerFactorAt is not 1");
            TradePlanner.Rebuild(home, null, false);
            Expect(TradePlanner.Unavailable == null, "prices on: the view claims there is nothing to show");
            Expect(!TradePlanner.RebuildIfStale(home, null, false), "an unchanged page rebuilt");
            Expect(TradePlanner.RebuildIfStale(home, Commodities.All[0], false), "a new good filter did not rebuild");
            Expect(TradePlanner.RebuildIfStale(home, Commodities.All[0], true), "the have toggle did not rebuild");
        }

        // ---- Candidates ----

        private static bool Eligible(CompSettlementLife l, Map home)
        {
            Settlement s = l.Settlement;
            return l.Active && s.Spawned && !s.HasMap && s.Faction != null && !s.Faction.IsPlayer && !s.Faction.def.permanentEnemy && !s.Faction.HostileTo(Faction.OfPlayer)
                   && s.TraderKind != null && s.CanTradeNow && s.Tile.Layer == home.Tile.Layer;
        }

        private static void Candidates(LivingWorldComponent comp, Map home)
        {
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
            var expected = new List<CompSettlementLife>();
            var reasons = new SortedDictionary<string, int>();
            foreach (CompSettlementLife l in comp.activeSettlements)
            {
                string fault = TradePlanner.CandidateFault(l, home);
                reasons[fault ?? "ok"] = reasons.TryGetValue(fault ?? "ok", out int n) ? n + 1 : 1;
                Expect((fault == null) == Eligible(l, home), $"{l.Settlement.Label}: the planner says '{fault ?? "candidate"}' but the rules say eligible={Eligible(l, home)}");
                if (fault == null && Find.World.reachability.CanReach(home.Tile, l.Settlement.Tile)) expected.Add(l);
            }
            expected.Sort((a, b) =>
            {
                int c = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, a.Settlement.Tile).CompareTo(Find.WorldGrid.ApproxDistanceInTiles(home.Tile, b.Settlement.Tile));
                return c != 0 ? c : a.Settlement.ID.CompareTo(b.Settlement.ID);
            });
            int cap = Tuning.T.plannerTowns;
            if (expected.Count > cap) expected.RemoveRange(cap, expected.Count - cap);
            IReadOnlyList<CompSettlementLife> actual = TradePlanner.Towns(reachableOnly: true);
            Expect(actual.Count == expected.Count && actual.All(expected.Contains), $"the planner's {actual.Count} towns are not the {expected.Count} nearest reachable ones that trade");
            Expect(!TradePlanner.Searching, $"the planner is still working out reachability after {FrameLimit} frames");
            Log.Message($"[LivingWorld:test] planner: candidates {actual.Count} of {comp.activeSettlements.Count} towns; by fault: {string.Join(", ", reasons.Select(kv => $"{kv.Key} {kv.Value}"))}");
            Expect(actual.Count > 0, "no candidate town at all");
            foreach (SellRow r in TradePlanner.Sell) Expect(actual.Contains(r.town), $"sell row for {r.town.Settlement.Label}, which is not a candidate");
            foreach (RouteRow r in TradePlanner.Routes) Expect(actual.Contains(r.buyAt) && actual.Contains(r.sellAt) && r.buyAt != r.sellAt, "route row with a town that is not a candidate");
        }

        // ---- Representatives ----

        private static TraderKindDef Kind(params StockGenerator[] generators) => new TraderKindDef { defName = "LW_TestKind", stockGenerators = generators.ToList() };

        private static StockGenerator Sells(ThingDef d)
        {
            var g = new StockGenerator_SingleDef();
            Traverse.Create(g).Field("thingDef").SetValue(d);
            return g;
        }

        private static StockGenerator Buys(ThingDef d) => new StockGenerator_BuySingleDef { thingDef = d };

        private static void Representatives()
        {
            ThingDef gold = ThingDefOf.Gold, herbal = ThingDefOf.MedicineHerbal, industrial = ThingDefOf.MedicineIndustrial;
            CommodityDef luxuries = Commodities.Of(gold), medicine = Commodities.Of(herbal);
            Expect(luxuries != null && medicine != null && Commodities.Of(industrial) == medicine, "gold or the medicines are in no commodity");
            if (luxuries == null || medicine == null) return;
            // A trader that only buys gold never sells it.
            TraderKindDef buyOnly = Kind(Buys(gold));
            Expect(TradePlanner.WillBuy(buyOnly, gold) && !TradePlanner.WillSell(buyOnly, gold), "a buy-only generator counts as selling");
            Expect(TradePlanner.BuysAny(buyOnly, luxuries) && !TradePlanner.SellsAny(buyOnly, luxuries), "buy-only kind: sell or buy masks wrong");
            Expect(TradePlanner.RouteRep(buyOnly, Kind(Buys(gold)), luxuries) == null, "a route from a trader that only buys");
            Expect(TradePlanner.RouteRep(Kind(Sells(gold)), buyOnly, luxuries) == gold, "no route from a seller of gold to a buyer of gold");
            // Herbal against industrial medicine: nothing both sell and take.
            TraderKindDef herbalSeller = Kind(Sells(herbal)), industrialBuyer = Kind(Buys(industrial)), herbalBuyer = Kind(Buys(herbal));
            Expect(TradePlanner.RouteRep(herbalSeller, industrialBuyer, medicine) == null, "a route between herbal and industrial medicine");
            Expect(TradePlanner.RouteRep(herbalSeller, herbalBuyer, medicine) == herbal, "no route for herbal medicine from a seller to a buyer");
            // The first listed thing that fits is the representative (commission goods first).
            TraderKindDef both = Kind(Sells(herbal), Sells(industrial)), takesBoth = Kind(Buys(herbal), Buys(industrial));
            Expect(TradePlanner.RouteRep(both, takesBoth, medicine) == medicine.commissionGoods[0].thing, "the representative is not the first commission good");
            // Every kind in the game: a thing a kind can only buy is never on its sell side.
            int kindsSeen = 0, buyOnlySeen = 0;
            foreach (TraderKindDef k in DefDatabase<TraderKindDef>.AllDefsListForReading)
            {
                kindsSeen++;
                foreach (CommodityDef c in Commodities.All)
                    foreach (ThingDef d in TradePlanner.RepsOf(c))
                    {
                        bool anyMaker = k.stockGenerators.Any(g => !(g is StockGenerator_BuyExpensiveSimple || g is StockGenerator_BuySingleDef || g is StockGenerator_BuySlaves || g is StockGenerator_BuyTradeTag) && g.HandlesThingDef(d));
                        Expect(TradePlanner.WillSell(k, d) == (anyMaker && d.tradeability.TraderCanSell()), $"{k.defName} / {d.defName}: sell side wrong");
                        Expect(TradePlanner.WillBuy(k, d) == (k.WillTrade(d) && d.tradeability.PlayerCanSell()), $"{k.defName} / {d.defName}: buy side wrong");
                        if (k.WillTrade(d) && !anyMaker) buyOnlySeen++;
                    }
            }
            // Goods with no thing to stand for them (animals) are left out.
            foreach (CommodityDef c in Commodities.All)
                foreach (ThingDef d in TradePlanner.RepsOf(c))
                    Expect(d.category == ThingCategory.Item && Commodities.Of(d) == c && d.tradeability != Tradeability.None, $"{c.defName}: {d.defName} is not a fit representative");
            Log.Message($"[LivingWorld:test] planner: representatives: {Commodities.All.Count(c => TradePlanner.RepsOf(c).Count > 0)} of {Commodities.Count} goods have one; {kindsSeen} trader kinds checked, {buyOnlySeen} buy-only pairs");
        }

        // ---- Estimate ----

        /// <summary>
        /// The route estimate written out again from vanilla's prices (no planner helper): what the selling town A pays is vanilla's sell price held to its own
        /// buy price, scaled by the market's factors, held again to the scaled buy price; against the buying town B's buy price.
        /// </summary>
        private static float Independent(float spf, float ptbB, float ptbA, float fb, float fbA, float fa, float n, float loss)
        {
            float buyAVanilla = 1.4f * ptbA * (1f + loss) * Mathf.Max(0.05f, 1f - n - 0.02f);
            float sellAVanilla = Mathf.Min(0.6f * spf * (1f - loss) * (1f + n + 0.02f), buyAVanilla);
            float got = Mathf.Min(sellAVanilla * fa, buyAVanilla * fbA);
            return got / (1.4f * ptbB * (1f + loss) * Mathf.Max(0.05f, 1f - n - 0.02f) * fb);
        }

        private static void Estimate()
        {
            float min = Tuning.T.plannerMinProfit;
            // A factor ratio of 1.5 at zero negotiator bonus and no difficulty loss returns 0.669 of the purchase cost: not a route.
            float ratio = TradePlanner.Estimate(1f, 1f, 1f, 1.5f, 0f, 0f, 1f, 1f);
            Expect(Mathf.Abs(ratio - 0.669f) < 0.001f && ratio < min, $"a factor ratio of 1.5 estimates {ratio:F3} (expected 0.669, below {min})");
            float breakEven = TradePlanner.Estimate(1f, 1f, 1f, 2.24f, 0f, 0f, 1f, 1f);
            Expect(breakEven > 0.99f && breakEven < 1.01f, $"break-even factor ratio estimates {breakEven:F3} (expected 1)");
            // The same formula written out again, with every term (here the clamp does not bite).
            float sellPf = 0.5f, ptb = 0.7f, fb = 0.8f, fa = 1.9f, n = 0.15f, loss = 0.1f;
            float independent = 0.6f * fa * sellPf * (1f - loss) * (1f + n + 0.02f) / (1.4f * fb * ptb * (1f + loss) * (1f - n - 0.02f));
            Expect(Near(TradePlanner.Estimate(sellPf, ptb, fb, fa, n, loss, ptb, 1f), independent), "the estimate differs from the formula written out");
            // Things with a low sell factor (weapons): the factor is applied.
            int seen = 0;
            foreach (CommodityDef c in Commodities.All)
                foreach (ThingDef d in TradePlanner.RepsOf(c))
                {
                    float spf = d.GetStatValueAbstract(StatDefOf.SellPriceFactor);
                    if (Mathf.Approximately(spf, 1f)) continue;
                    TraderKindDef kind = Kind(Sells(d));
                    float ptbKind = kind.PriceTypeFor(d, TradeAction.PlayerBuys).PriceMultiplier();
                    float with = TradePlanner.EstimateFor(d, kind, kind, 1f, 1f, 1f, 0f, 0f), expected = Independent(spf, ptbKind, ptbKind, 1f, 1f, 1f, 0f, 0f);
                    seen++;
                    Expect(Near(with, expected), $"{d.defName}: sell price factor {spf:F2}: estimate {with:F3}, expected {expected:F3}");
                }
            // The selling town holds what it pays to its own buy price (finding P2-2): a fair there with a very good negotiator, and fame with a good one.
            (float spf, float ptbB, float ptbA, float fb, float fbA, float fa, float n, float loss)[] cases =
            {
                (1f, 1f, 1f, 1f, 0.9f, 1.1f, 0.35f, 0f),
                (1f, 1f, 1f, 1f, 1f, 1f, 0.395f, 0f),
                (1f, 1f, 1f, 1.2f, 0.92f, 1.08f, 0.24f, 0f),
                (0.5f, 0.7f, 0.9f, 0.8f, 1f, 1.9f, 0.15f, 0.1f),
                (1f, 1f, 1f, 1f, 1.05f, 1.2f, 0f, 0.05f),
            };
            foreach ((float cSpf, float cPtbB, float cPtbA, float cFb, float cFbA, float cFa, float cN, float cLoss) in cases)
            {
                float got = TradePlanner.Estimate(cSpf, cPtbB, cFb, cFa, cN, cLoss, cPtbA, cFbA), want = Independent(cSpf, cPtbB, cPtbA, cFb, cFbA, cFa, cN, cLoss);
                Expect(Near(got, want), $"clamp case spf {cSpf} n {cN}: estimate {got:F3}, expected {want:F3}");
            }
            // Fair at A, n 0.35, no loss: A would pay 0.6 x 1.37 x 1.1 = 0.904, but its own buy price is 1.4 x 0.63 x 0.9 = 0.794, so the trip returns 0.794 / 0.882 = 0.9, not 1.025.
            float fair = TradePlanner.Estimate(1f, 1f, 1f, 1.1f, 0.35f, 0f, 1f, 0.9f), unclamped = 0.6f * 1.1f * 1.37f / (1.4f * 0.63f);
            Expect(unclamped > 1.02f && fair < 0.91f && Mathf.Abs(fair - 0.9f) < 0.005f, $"a fair at the selling town with a negotiator of +35% estimates {fair:F3} (expected 0.900; unclamped {unclamped:F3})");
            Log.Message($"[LivingWorld:test] planner: estimate checked; {seen} representatives have a sell price factor other than 1, {cases.Length + 1} clamp cases");
        }

        // ---- Rows ----

        private static void Rows(Map home)
        {
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
            float minSell = Tuning.T.plannerMinSellFactor, minProfit = Tuning.T.plannerMinProfit, maxDays = Tuning.T.plannerMaxDays;
            float loss = Find.Storyteller.difficulty.tradePriceFactorLoss, gain = 0f;
            Pawn best = null;
            foreach (Pawn p in home.mapPawns.FreeColonistsSpawned)
            {
                if (StatDefOf.TradePriceImprovement.Worker.IsDisabledFor(p)) continue;
                float g = p.GetStatValue(StatDefOf.TradePriceImprovement);
                if (best == null || g > gain)
                {
                    best = p;
                    gain = g;
                }
            }
            float prev = float.MaxValue;
            foreach (SellRow r in TradePlanner.Sell)
            {
                float f = MarketTrade.PlayerFactorAt(r.town, r.good, playerBuys: false);
                Expect(Near(r.factor, f) && f >= minSell - 1e-4f, $"sell row {r.town.Settlement.Label} {r.good.defName}: factor {r.factor:F3} (recomputed {f:F3}, minimum {minSell})");
                Expect(r.ticks / (float)GenDate.TicksPerDay <= maxDays + 1e-3f, $"sell row {r.town.Settlement.Label}: {r.ticks / (float)GenDate.TicksPerDay:F1} days is over the limit");
                TraderKindDef kind = r.town.Settlement.TraderKind;
                Expect(TradePlanner.BuysAny(kind, r.good), $"sell row {r.town.Settlement.Label} {r.good.defName}: the town does not take it");
                Expect((kind.TitleRequiredToTrade != null) == !r.note.NullOrEmpty(), $"sell row {r.town.Settlement.Label}: the royal title note is wrong");
                float score = (r.factor - 1f) / (1f + r.ticks / (float)GenDate.TicksPerDay / 5f);
                Expect(score <= prev + 1e-4f, $"sell rows are not in order at {r.town.Settlement.Label} {r.good.defName}");
                prev = score;
            }
            prev = float.MaxValue;
            foreach (RouteRow r in TradePlanner.Routes)
            {
                string tag = $"route {r.rep.defName}: {r.buyAt.Settlement.Label} -> {r.sellAt.Settlement.Label}";
                TraderKindDef kindB = r.buyAt.Settlement.TraderKind, kindA = r.sellAt.Settlement.TraderKind;
                // Recomputed from the markets and vanilla's trade formula, not from the planner's helper.
                float fb = MarketTrade.PlayerFactorAt(r.buyAt, r.good, playerBuys: true), fa = MarketTrade.PlayerFactorAt(r.sellAt, r.good, playerBuys: false);
                float spf = r.rep.GetStatValueAbstract(StatDefOf.SellPriceFactor), ptbB = kindB.PriceTypeFor(r.rep, TradeAction.PlayerBuys).PriceMultiplier(), ptbA = kindA.PriceTypeFor(r.rep, TradeAction.PlayerBuys).PriceMultiplier();
                float fbA = MarketTrade.PlayerFactorAt(r.sellAt, r.good, playerBuys: true);
                float est = Independent(spf, ptbB, ptbA, fb, fbA, fa, gain, loss);
                Expect(Mathf.Abs(est - r.estProfit) < 1e-3f * Mathf.Max(1f, est), $"{tag}: estimate {r.estProfit:F3}, recomputed {est:F3}");
                Expect(r.estProfit >= minProfit - 1e-4f && est >= minProfit - 1e-3f, $"{tag}: listed at {r.estProfit:F3} (recomputed {est:F3}) under the minimum {minProfit}");
                Expect(TradePlanner.WillSell(kindB, r.rep) && TradePlanner.WillBuy(kindA, r.rep) && Commodities.Of(r.rep) == r.good, $"{tag}: the thing does not fit the trip");
                Expect(kindB.TitleRequiredToTrade == null && kindA.TitleRequiredToTrade == null, $"{tag}: a town that needs a royal title");
                float score = (r.estProfit - 1f) / (1f + (r.ticksToBuy + r.ticksOnward) / (float)GenDate.TicksPerDay / 5f);
                Expect(score <= prev + 1e-4f, $"routes are not in order at {tag}");
                prev = score;
            }
            // Twice over the same state gives the same rows, in the same order.
            string first = Signature();
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
            Expect(first == Signature(), "a second pass over the same markets listed other rows");
            Log.Message($"[LivingWorld:test] planner: {TradePlanner.Sell.Count} sell rows, {TradePlanner.Routes.Count} routes; best negotiator {best?.LabelShort ?? "none"} +{gain:P0}");
            foreach (SellRow r in TradePlanner.Sell.Take(2)) Log.Message($"[LivingWorld:test] planner: sell {r.good.defName} at {r.town.Settlement.Label} x{r.factor:F2}, {r.ticks / (float)GenDate.TicksPerDay:F1} days{(r.exact ? "" : " (approximate)")}, have {r.have}");
            foreach (RouteRow r in TradePlanner.Routes.Take(2)) Log.Message($"[LivingWorld:test] planner: route {r.rep.defName} buy {r.buyAt.Settlement.Label} sell {r.sellAt.Settlement.Label} +{(r.estProfit - 1f):P0}");
        }

        private static string Signature() =>
            string.Join(",", TradePlanner.Sell.Select(r => r.town.Settlement.ID + ":" + r.good.index + ":" + r.ticks)) + "|" +
            string.Join(",", TradePlanner.Routes.Select(r => r.buyAt.Settlement.ID + ">" + r.sellAt.Settlement.ID + ":" + r.good.index + ":" + r.ticksToBuy + "+" + r.ticksOnward));

        private static void Filters(Map home)
        {
            CommodityDef food = LWDefOf.LW_Food;
            TradePlanner.Rebuild(home, food, false);
            Drive(FrameLimit);
            Expect(TradePlanner.Sell.All(r => r.good == food) && TradePlanner.Routes.All(r => r.good == food), "the good filter let another good through");
            TradePlanner.Rebuild(home, null, true);
            Drive(FrameLimit);
            Expect(TradePlanner.Sell.All(r => r.have > 0), "\"What I have\" listed a good the colony has none of");
            // Counted resources of a good: the sum over the things of the good that count (the tooltip's "You have").
            foreach (SellRow r in TradePlanner.Sell.Take(3))
            {
                int sum = 0;
                bool any = false;
                foreach (KeyValuePair<ThingDef, int> kv in home.resourceCounter.AllCountedAmounts)
                    if (Commodities.Of(kv.Key) == r.good)
                    {
                        any = true;
                        sum += kv.Value;
                    }
                Expect(r.have == (any ? sum : -1), $"{r.good.defName}: have {r.have}, counted {sum}");
            }
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
        }

        // ---- Travel ----

        private static void Travel(Map home)
        {
            int perFrame = Tuning.T.plannerPathsPerFrame;
            double budget = Tuning.T.plannerPathMsPerFrame;
            TradePlanner.TestClearCache();
            TradePlanner.Rebuild(home, null, false);
            // Several calls in one frame do one frame's work.
            int frame = ++testFrame;
            int maxPaths = 0, frames = 1;
            double maxMs = 0d, totalMs = 0d;
            try
            {
                TradePlanner.TestFrame = frame;
                TradePlanner.StepPaths();
                totalMs += TradePlanner.PathMsLastFrame;
                int afterFirst = TradePlanner.StatSearches;
                for (int i = 0; i < 4; i++) TradePlanner.StepPaths();
                Expect(TradePlanner.StatSearches == afterFirst, "StepPaths did more work for repeated calls in one frame");
                // Frame after frame: at most the count, and, apart from the one operation that crosses it, the time budget.
                while (frames < FrameLimit)
                {
                    TradePlanner.TestFrame = ++testFrame;
                    TradePlanner.StepPaths();
                    frames++;
                    maxPaths = Mathf.Max(maxPaths, TradePlanner.PathsLastFrame);
                    maxMs = Math.Max(maxMs, TradePlanner.PathMsLastFrame);
                    totalMs += TradePlanner.PathMsLastFrame;
                    Expect(TradePlanner.PathsLastFrame <= perFrame, $"{TradePlanner.PathsLastFrame} searches in one frame (limit {perFrame})");
                    Expect(TradePlanner.PathMsLastFrame - TradePlanner.FrameLongestMs <= budget + 1d, $"a frame of path work took {TradePlanner.PathMsLastFrame:F2} ms (budget {budget}) without its longest operation of {TradePlanner.FrameLongestMs:F2} ms");
                    if (!TradePlanner.Searching && TradePlanner.Pending == 0) break;
                }
                Expect(!TradePlanner.Searching && TradePlanner.Pending == 0, $"after {frames} frames the planner still waits for {TradePlanner.Pending} travel times");
                // A search cannot be divided, so a long one is paid for by the frames after it: over the whole fill, the path work averages within the budget.
                int spread = frames + TradePlanner.HoldFramesLeft;
                double average = totalMs / spread;
                Expect(average <= budget + 0.1d, $"the path work averaged {average:F2} ms a frame over {spread} frames (budget {budget})");
                Log.Message($"[LivingWorld:test] planner: travel times in {frames} frames (+{TradePlanner.HoldFramesLeft} held), at most {maxPaths} searches and {maxMs:F2} ms a frame, {average:F2} ms on average, {TradePlanner.StatSearches} searches ({TradePlanner.StatColdReach} cold reachability), longest {TradePlanner.LongestSearchMs:F2} ms");
            }
            finally
            {
                TradePlanner.TestFrame = null;
            }
            // Only the colony's trips are searched: the leg between two towns is the distance estimate, and a path is kept for each trip searched.
            Expect(TradePlanner.KeptPaths <= TradePlanner.TownCount && TradePlanner.StatSearches <= TradePlanner.KeptPaths + TradePlanner.StatColdReach, $"{TradePlanner.StatSearches} searches kept {TradePlanner.KeptPaths} paths for {TradePlanner.TownCount} towns");
            // Every shown town has the estimator's value for this departure.
            int checkedRows = 0;
            foreach (SellRow r in TradePlanner.Sell)
            {
                int ticks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(home.Tile, r.town.Settlement.Tile, null);
                if (ticks <= 0) continue;
                checkedRows++;
                Expect(r.exact && r.ticks == ticks, $"sell row {r.town.Settlement.Label}: {r.ticks} ticks (exact {r.exact}), the estimator says {ticks}");
            }
            foreach (RouteRow r in TradePlanner.Routes)
            {
                int t1 = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(home.Tile, r.buyAt.Settlement.Tile, null);
                int t2 = Mathf.RoundToInt(Find.WorldGrid.ApproxDistanceInTiles(r.buyAt.Settlement.Tile, r.sellAt.Settlement.Tile) * 3300f * 1.3f);
                Expect(Math.Abs(r.ticksOnward - t2) <= 1, $"route {r.buyAt.Settlement.Label} -> {r.sellAt.Settlement.Label}: the leg between the towns is {r.ticksOnward} ticks, the distance says {t2}");
                if (t1 <= 0) continue;
                checkedRows++;
                Expect(r.exact && r.ticksToBuy == t1, $"route {r.buyAt.Settlement.Label} -> {r.sellAt.Settlement.Label}: {r.ticksToBuy} ticks to the buying town, the estimator says {t1}");
            }
            Log.Message($"[LivingWorld:test] planner: {checkedRows} shown legs match the estimator");
            // A new departure bucket drops the cached times but keeps the paths: the times are worked out again along them, with no new search of a trip already searched.
            int cached = TradePlanner.CachedLegs, keptBefore = TradePlanner.KeptPaths, searchesBefore = TradePlanner.StatSearches;
            if (TradePlanner.Sell.Count + TradePlanner.Routes.Count > 0) Expect(cached > 0, "no travel time was cached");
            Expect(!TradePlanner.RebuildIfStale(home, null, false), "an unchanged planner wanted a rebuild");
            try
            {
                int later = Find.TickManager.TicksAbs + 15000;
                TradePlanner.TestTicksAbs = later;
                Drive(FrameLimit);
                Expect(TradePlanner.StatSearches - searchesBefore == TradePlanner.KeptPaths - keptBefore, $"a new departure bucket searched {TradePlanner.StatSearches - searchesBefore} times for {TradePlanner.KeptPaths - keptBefore} new paths: a known trip was searched again");
                Expect(TradePlanner.Pending == 0, $"a new departure bucket left {TradePlanner.Pending} times to work out");
                int again = 0;
                foreach (SellRow r in TradePlanner.Sell)
                {
                    if (!r.exact) continue;
                    using WorldPath path = home.Tile.Layer.Pather.FindPath(home.Tile, r.town.Settlement.Tile, null);
                    if (!path.Found) continue;
                    int ticks = CaravanArrivalTimeEstimator.EstimatedTicksToArrive(home.Tile, r.town.Settlement.Tile, path, 0f, 3300, later);
                    again++;
                    Expect(r.ticks == ticks, $"sell row {r.town.Settlement.Label}: {r.ticks} ticks in the next bucket, the estimator says {ticks}");
                }
                Log.Message($"[LivingWorld:test] planner: a new departure bucket: {TradePlanner.StatSearches - searchesBefore} searches, {again} sell legs match the estimator at the later time");
                // A road change drops the paths too, and rebuilds.
                TradePlanner.TestTicksAbs = null;
                TradePlanner.TestRoadsVersion = Roads.Version + 1;
                Expect(TradePlanner.RebuildIfStale(home, null, false), "a new road version did not rebuild");
                Expect(TradePlanner.KeptPaths == 0, $"a new road version kept {TradePlanner.KeptPaths} paths");
                TradePlanner.TestFrame = ++testFrame;
                TradePlanner.StepPaths();
                Expect(TradePlanner.CachedLegs <= perFrame, $"a new road version kept {TradePlanner.CachedLegs} cached times");
            }
            finally
            {
                TradePlanner.TestTicksAbs = null;
                TradePlanner.TestRoadsVersion = null;
                TradePlanner.TestFrame = null;
            }
            TradePlanner.TestClearCache();
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
        }

        // ---- Staleness ----

        private static void Staleness(Map home)
        {
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
            Settlement target = TradePlanner.Sell.Count > 0 ? TradePlanner.Sell[0].town.Settlement : TradePlanner.Routes.Count > 0 ? TradePlanner.Routes[0].buyAt.Settlement : null;
            if (target == null)
            {
                Log.Message("[LivingWorld:test] planner: no rows shown, staleness check skipped");
                return;
            }
            Faction owner = target.Faction;
            Faction foe = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && f != owner && f.HostileTo(Faction.OfPlayer) && !f.def.hidden);
            Faction friend = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => !f.IsPlayer && f != owner && !f.HostileTo(Faction.OfPlayer) && !f.def.hidden && !f.def.permanentEnemy && FactionState.IsSimulated(f));
            if (foe == null) Log.Message("[LivingWorld:test] planner: no hostile faction to take the town, that part of the staleness check skipped");
            else
            {
                try
                {
                    // A shown town captured by an enemy while the page is open: its rows are gone at the next draw and a rebuild is due.
                    target.SetFaction(foe);
                    TradePlanner.ValidateShown();
                    Expect(!ShowsTown(target), "rows of a town turned hostile are still shown");
                    Expect(TradePlanner.RebuildIfStale(home, null, false), "no rebuild was flagged after a row went stale");
                }
                finally
                {
                    target.SetFaction(owner);
                }
                TradePlanner.Rebuild(home, null, false);
                Drive(FrameLimit);
            }
            if (friend == null || !ShowsTown(target))
            {
                Log.Message("[LivingWorld:test] planner: no friendly faction (or the town is no longer shown) to take the town, that part of the staleness check skipped");
                return;
            }
            try
            {
                // A shown town that changes hands between two factions that are not hostile: nothing makes it a fault, but its trader kind and market came with the old owner.
                target.SetFaction(friend);
                TradePlanner.ValidateShown();
                Expect(!ShowsTown(target), "rows of a town that changed hands are still shown");
                Expect(TradePlanner.RebuildIfStale(home, null, false), "no rebuild was flagged after a town changed hands");
            }
            finally
            {
                target.SetFaction(owner);
            }
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
        }

        private static bool ShowsTown(Settlement town) =>
            TradePlanner.Sell.Any(r => r.town.Settlement == town) || TradePlanner.Routes.Any(r => r.buyAt.Settlement == town || r.sellAt.Settlement == town);

        // ---- Performance ----

        private static void Performance(Map home)
        {
            // One pass to compile the code, then the one that is timed (the page's rows from the markets, no path searches in it).
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
            TradePlanner.Rebuild(home, null, false);
            Drive(FrameLimit);
            double ms = TradePlanner.LastRebuildMs + TradePlanner.LastRowsMs;
            Log.Message($"[LivingWorld:test] {TradePlanner.MetricsLine()}");
            if (AutoTest.Plan == "stress") Expect(ms < 5d, $"the planner took {ms:F2} ms to take its candidates and work out the rows (limit 5)");
            else if (ms >= 5d) Log.Message($"[LivingWorld:test] planner: {ms:F2} ms for the rows (over the stress limit of 5)");
        }
    }
}
