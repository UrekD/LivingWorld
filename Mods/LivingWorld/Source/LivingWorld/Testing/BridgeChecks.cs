using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Autotest for the storyteller bridge: fires a raid and a trade caravan the way the storyteller does (not forced),
    /// expects them to travel as world parties, arrive, and the survivors to head home.
    /// </summary>
    public static class BridgeChecks
    {
        private static bool fired, probed, roadTraded, neighboured;
        private static readonly HashSet<int> refired = new HashSet<int>();
        private static bool colonyWiped;
        private static string neighbourResult;
        private static bool rulerVisitAsked, merchantKilled, ropeTested, audienceTested;
        private static string audienceResult;
        private static string ropeResult;
        private static int merchantKillTick = -1;
        private static Caravan roadCaravan;
        private static string roadTradeResult;
        private static readonly List<string> probeProblems = new List<string>();
        private static int probeTradeables, probeHours;
        private static bool probeHadNegotiator, probeSawTrader;

        public static void Register()
        {
            if (System.Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") == "1") return;
            if (AutoTest.Plan == "life") return; // nothing forced: LifeChecks watches what the world does on its own
            AutoTest.OnBegin.Add(() =>
            {
                fired = false;
                // normal: the first war band's first attempt fails on purpose; the retry must still bring the raid.
                if (AutoTest.Plan == "normal")
                {
                    Bridge.TestFailNextRaid = true;
                    Commissions.TestAlways = true; // the first merchant orders something; the test fills it
                }
            });
            AutoTest.Hourly.Add(Hourly);
            AutoTest.DailyMetrics.Add(() =>
                $"bridge visitLords={Bridge.StatVisitLords} warBands={Bridge.StatWarBands} merchants={Bridge.StatMerchants} visitors={Bridge.StatVisitors} raidsArrived={Bridge.StatRaidsArrived} tradesArrived={Bridge.StatTradesArrived} visitsArrived={Bridge.StatVisitsArrived} returns={Bridge.StatReturns} parties={WorldParty.All.Count} contexts={LivingWorldComponent.Instance.contexts.Count}");
            AutoTest.Checks.Add(Checks);
        }

        private static void Hourly()
        {
            if (Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_GameEnded>().Any()) colonyWiped = true;
            if (AutoTest.Plan == "roadtrade" && roadCaravan != null && !roadTraded) TryRoadTrade();
            // Traders can only trade once they reached their spot: retry hourly for a day.
            if (!probed && Bridge.StatTradesArrived > 0)
            {
                probeHours++;
                List<string> found = MarketProbe().ToList();
                if (probeTradeables > 0 || probeHours > 24)
                {
                    probed = true;
                    probeProblems.AddRange(found);
                }
            }
            if (AutoTest.Plan == "normal" && !ropeTested && fired && Find.TickManager.TicksGame > GenDate.TicksPerHour * 3) RopeSwapTest();
            if (AutoTest.Plan == "normal") CommissionStep();
            if (AutoTest.Plan == "normal" && !audienceTested && Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_RulerAudience>().FirstOrDefault() is ChoiceLetter_RulerAudience audience)
                AudienceTest(audience);
            // normal: the returning merchant (who took over a caravan and its roped animals) dies on the map; the animals must
            // carry on without errors (a rope transfer bug left them pointing at a broken rope).
            if (AutoTest.Plan == "normal" && !merchantKilled)
            {
                // Not the merchant with the colony's order (the commission check needs them alive to deliver).
                Figure m = LivingWorldComponent.Instance.figures.FirstOrDefault(x => x.role == FigureRole.Merchant && x.visits >= 2 && x.pawn != null && x.pawn.Spawned && !x.pawn.Dead
                                                                                    && x.id != commissionFigure && x.commissionDef == null);
                if (m != null && merchantKillTick < 0) merchantKillTick = Find.TickManager.TicksGame + GenDate.TicksPerHour * 2;
                else if (m != null && Find.TickManager.TicksGame >= merchantKillTick)
                {
                    merchantKilled = true;
                    int led = m.pawn.roping?.Ropees.Count ?? 0;
                    m.pawn.Kill(null);
                    Log.Message($"[LivingWorld:test] merchant figure {m.name} killed on the map (was leading {led} animals)");
                }
            }
            // Each figure is called back once, as soon as it is free again (after day 2.5).
            if (fired && Find.TickManager.TicksGame > GenDate.TicksPerDay * 5 / 2) FireAgain();
            if (AutoTest.Plan == "normal" && fired && !neighboured && Find.TickManager.TicksGame > GenDate.TicksPerDay * 3) ForceNeighbourVisit();
            // Wait for the first daily step (settlement state seeded), then fire both once.
            if (fired || LivingWorldComponent.Instance.lastDailyTick < 0) return;
            fired = true;
            Map map = Find.AnyPlayerHomeMap;
            Faction enemy = Find.FactionManager.AllFactionsListForReading
                .Where(f => FactionState.IsSimulated(f) && f.HostileTo(Faction.OfPlayer) && LivingWorldComponent.Instance.StateFor(f)?.settlements.Count > 0)
                .OrderBy(f => Bridge.NearestSettlement(f, map.Tile) is Settlement s ? Find.WorldGrid.ApproxDistanceInTiles(s.Tile, map.Tile) : 9999f)
                .FirstOrDefault();
            if (enemy != null)
            {
                IncidentParms raid = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                raid.faction = enemy;
                raid.points = 150f;
                raid.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                raid.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                bool ok = IncidentDefOf.RaidEnemy.Worker.TryExecute(raid);
                Log.Message($"[LivingWorld:test] fired raid by {enemy.Name}: {ok}, war bands {Bridge.StatWarBands}");
                if (AutoTest.Plan == "intercept") PlaceCaravanOnRoute(map);
                if (AutoTest.Plan == "ambush") VanillaAmbush();
            }
            else Log.Message("[LivingWorld:test] no hostile simulated faction to raid with");
            Faction friend = Find.FactionManager.AllFactionsListForReading
                .FirstOrDefault(f => FactionState.IsSimulated(f) && !f.HostileTo(Faction.OfPlayer) && !f.def.caravanTraderKinds.NullOrEmpty()
                                     && LivingWorldComponent.Instance.StateFor(f)?.settlements.Count > 0);
            if (friend != null)
            {
                IncidentParms trade = StorytellerUtility.DefaultParmsNow(IncidentDefOf.TraderCaravanArrival.category, map);
                trade.faction = friend;
                bool ok = IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(trade);
                Log.Message($"[LivingWorld:test] fired trade caravan by {friend.Name}: {ok}, merchants {Bridge.StatMerchants}");
                if (AutoTest.Plan == "roadtrade") PlaceTraderCaravanOnRoute(map);
            }
            if (HospitalityCompat.Active)
            {
                // Nobody answers Hospitality's "is it safe to come?" prompt in a headless run.
                System.Type mc = HarmonyLib.AccessTools.TypeByName("Hospitality.Hospitality_MapComponent");
                MapComponent comp = mc == null ? null : map.GetComponent(mc);
                if (comp != null)
                {
                    HarmonyLib.Traverse.Create(comp).Field("askForSafety").SetValue(false);
                    // A fresh test colony has no painted guest area; Hospitality errors on an empty one.
                    HarmonyLib.Traverse.Create(comp).Field("defaultAreaRestriction").SetValue(null);
                }
            }
            IncidentParms visit = StorytellerUtility.DefaultParmsNow(IncidentDefOf.VisitorGroup.category, map);
            visit.points = 120f;
            bool vok = IncidentDefOf.VisitorGroup.Worker.TryExecute(visit);
            Log.Message($"[LivingWorld:test] fired visitor group ({IncidentDefOf.VisitorGroup.workerClass.FullName}, hospitality={HospitalityCompat.Active}): {vok}, visitors {Bridge.StatVisitors}");
        }

        /// <summary>Opens a trade session with a settlement: prices must carry its market factor, applied exactly once.</summary>
        private static IEnumerable<string> MarketProbe()
        {
            Pawn negotiator = Find.AnyPlayerHomeMap?.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !StatDefOf.TradePriceImprovement.Worker.IsDisabledFor(p));
            if (negotiator == null) yield break; // nobody able to trade (downed, killed): try again next hour
            probeHadNegotiator = true;
            var problems = new List<string>();
            int factored = 0, checkedCount = 0;
            // A trade caravan on the colony map (settlement trade needs a player caravan).
            foreach (Pawn trader in negotiator.Map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (trader.trader?.traderKind == null || trader.Faction == null || trader.Faction.HostileTo(Faction.OfPlayer) || !trader.CanTradeNow) continue;
                probeSawTrader = true;
                string s = MarketTrade.OriginOf(trader)?.Settlement.Label ?? "(no origin)";
                TradeSession.SetupWith(trader, negotiator, giftMode: false);
                try
                {
                    foreach (Tradeable t in TradeSession.deal.AllTradeables)
                    {
                        if (t.IsCurrency || !t.TraderWillTrade) continue;
                        float f = MarketTrade.PlayerPriceFactor(t.ThingDef, out _, out _);
                        float p1 = t.GetPriceFor(TradeAction.PlayerBuys), p2 = t.GetPriceFor(TradeAction.PlayerBuys);
                        checkedCount++;
                        if (p1 != p2) problems.Add($"price of {t.ThingDef.defName} changed between reads ({p1} -> {p2})");
                        if (System.Math.Abs(f - 1f) > 0.05f)
                        {
                            if (factored++ < 3) Log.Message($"[LivingWorld:test] market {s}: {t.ThingDef.defName} factor {f:F2} buy {p1:F1} tooltip: {t.GetPriceTooltip(TradeAction.PlayerBuys).Replace("\n", " | ")}");
                        }
                    }
                }
                finally
                {
                    TradeSession.Close();
                }
                if (factored > 0) break;
            }
            probeTradeables = checkedCount;
            Log.Message($"[LivingWorld:test] market probe: {checkedCount} tradeables checked, {factored} with a market factor");
            if (checkedCount > 0 && factored == 0) problems.Add("no tradeable carried a market factor");
            foreach (string p in problems) yield return p;
        }

        /// <summary>Second visit from the same towns: the merchant and (if alive) the warlord should come back in person.</summary>
        private static void FireAgain()
        {
            Map map = Find.AnyPlayerHomeMap;
            foreach (Figure f in LivingWorldComponent.Instance.figures)
            {
                if (refired.Count >= 3) break; // the first figures only: their visits make new ones
                if (!f.Available || !refired.Add(f.pawn.thingIDNumber)) continue;
                Log.Message($"[LivingWorld:test] figure {f.name} ({f.role}, {f.homeName}) status={f.status} visits={f.visits}: calling back");
                Faction fac = f.pawn.Faction;
                Bridge.PreferredSource[fac.loadID] = f.homeId;
                if (f.role == FigureRole.Merchant)
                {
                    Bridge.PreferredTradeSource[fac.loadID] = f.homeId;
                    IncidentParms trade = StorytellerUtility.DefaultParmsNow(IncidentDefOf.TraderCaravanArrival.category, map);
                    trade.faction = fac;
                    Log.Message($"[LivingWorld:test] second trade caravan from {f.homeName}: {IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(trade)}");
                }
                else if (f.role == FigureRole.Freed || f.role == FigureRole.Kin) continue; // come with visits of their own accord
                else if (f.role == FigureRole.Traveller)
                {
                    // normal: this visit brings the faction's ruler along (goodwill raised, chance forced).
                    if (AutoTest.Plan == "normal" && fac.leader != null)
                    {
                        if (fac.PlayerGoodwill < 70) fac.TryAffectGoodwillWith(Faction.OfPlayer, 70 - fac.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
                        Figures.TestForceRulerVisit = true;
                        rulerVisitAsked = true;
                    }
                    Bridge.PreferredVisitSource[fac.loadID] = f.homeId;
                    IncidentParms visit = StorytellerUtility.DefaultParmsNow(IncidentDefOf.VisitorGroup.category, map);
                    visit.faction = fac;
                    visit.points = 120f;
                    Log.Message($"[LivingWorld:test] second visit from {f.homeName}: {IncidentDefOf.VisitorGroup.Worker.TryExecute(visit)}");
                }
                else
                {
                    IncidentParms raid = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.ThreatBig, map);
                    raid.faction = fac;
                    raid.points = 150f;
                    raid.raidStrategy = RaidStrategyDefOf.ImmediateAttack;
                    raid.raidArrivalMode = PawnsArrivalModeDefOf.EdgeWalkIn;
                    Log.Message($"[LivingWorld:test] second raid from {f.homeName}: {IncidentDefOf.RaidEnemy.Worker.TryExecute(raid)}");
                }
            }
        }

        private static bool commissionTaken;
        private static int commissionFigure = -1, commissionPaid = -1;
        private static string inspectText;

        /// <summary>Goods as the colony would have made them, set aside (forbidden) as the order letter says.</summary>
        private static void Stock(Map map, ThingDef def, int count)
        {
            while (count > 0)
            {
                Thing t = ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
                t.stackCount = Mathf.Min(count, t.def.stackLimit);
                count -= t.stackCount;
                if (GenPlace.TryPlaceThing(t, map.Center, map, ThingPlaceMode.Near, out Thing placed)) placed.SetForbidden(true, warnOnFail: false);
            }
        }

        /// <summary>A merchant's order: accept it, stock the goods; when they return, hand them over and get paid.</summary>
        private static ThingDef commissionThing;
        private static int commissionCount;

        private static void CommissionStep()
        {
            Map map = Neighbourhood.Home;
            // Keep the order in stock until the merchant comes, as a colony would: visitors on the map can eat forbidden food.
            if (commissionThing != null && commissionPaid < 0 && map != null && Commissions.CountOn(map, commissionThing) < commissionCount)
                Stock(map, commissionThing, commissionCount - Commissions.CountOn(map, commissionThing));
            ChoiceLetter_Commission l = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter_Commission>().FirstOrDefault();
            if (l == null) return;
            if (!l.delivery && !commissionTaken)
            {
                commissionTaken = true;
                commissionFigure = l.figureId;
                l.Choices.First().action();
                commissionThing = l.thing;
                commissionCount = l.count;
                Stock(map, l.thing, l.count);
                Log.Message($"[LivingWorld:test] commission: took the order for {l.count} {l.thing.defName} ({l.price} silver), stocked them");
            }
            else if (l.delivery && commissionPaid < 0)
            {
                // The merchant is on the map: their inspect pane tells their history with the colony.
                if (Figures.ById(l.figureId)?.pawn is Pawn m && m.Spawned)
                {
                    inspectText = m.GetInspectString();
                    Log.Message($"[LivingWorld:test] merchant inspect: {inspectText.Replace("\n", " | ")}");
                }
                int silver = Demands.ColonySilver(map);
                l.Choices.First().action();
                commissionPaid = Demands.ColonySilver(map) - silver;
                Log.Message($"[LivingWorld:test] commission: delivered, paid {commissionPaid} of {l.price}");
            }
        }

        /// <summary>The ruler's audience: accepting the proposal has its effect (pact, war request, new town, gift).</summary>
        private static void AudienceTest(ChoiceLetter_RulerAudience l)
        {
            audienceTested = true;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            FactionState fs = comp.StateFor(l.faction);
            Map map = Neighbourhood.Home;
            int requests = comp.warRequests.Count, towns = fs.settlements.Count, silver = Demands.ColonySilver(map);
            l.Choices.First().action();
            comp.RebuildIndexes(seedMissing: false);
            bool ok = l.proposal switch
            {
                AudienceProposal.TradePact => fs.tradePactUntil > Find.TickManager.TicksGame,
                AudienceProposal.MarchWithUs => comp.warRequests.Count > requests,
                AudienceProposal.SettleNearby => fs.settlements.Count > towns,
                _ => Demands.ColonySilver(map) > silver,
            };
            audienceResult = ok ? "ok" : "FAILED";
            Log.Message($"[LivingWorld:test] ruler audience of {l.faction?.Name}: {l.proposal} accepted -> {audienceResult}");
        }

        /// <summary>
        /// A figure takes the place of a visitor leading an animal on a rope: the rope must pass to the figure intact
        /// (holder and animal agree), and the animal must be fine after the figure dies.
        /// </summary>
        private static void RopeSwapTest()
        {
            ropeTested = true;
            Map map = Find.AnyPlayerHomeMap;
            Faction f = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => FactionState.IsSimulated(x) && !x.HostileTo(Faction.OfPlayer) && Bridge.CanSendVisitors(x));
            PawnKindDef chickenKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Chicken");
            if (f == null || chickenKind == null || !CellFinder.TryFindRandomCellNear(map.Center, map, 8, c => c.Standable(map), out IntVec3 cell))
            {
                ropeResult = "skipped";
                return;
            }
            Pawn generated = PawnGenerator.GeneratePawn(f.RandomPawnKind(), f);
            GenSpawn.Spawn(generated, cell, map);
            Pawn chicken = PawnGenerator.GeneratePawn(chickenKind, f);
            GenSpawn.Spawn(chicken, cell, map);
            Lord lord = LordMaker.MakeNewLord(f, new LordJob_VisitColony(f, cell), map, new List<Pawn> { generated, chicken });
            generated.roping.RopePawn(chicken);
            Pawn figure = PawnGenerator.GeneratePawn(f.RandomPawnKind(), f);
            Find.WorldPawns.PassToWorld(figure);
            bool swapped = Figures.Swap(lord, generated, figure, asTrader: false);
            bool intact = chicken.roping.RopedByPawn == figure && figure.roping.Ropees.Contains(chicken);
            ropeResult = !swapped ? "skipped (swap refused)" : intact ? "ok" : $"FAILED (animal held by {chicken.roping.RopedByPawn?.LabelShort ?? "nobody"}, figure lists it: {figure.roping.Ropees.Contains(chicken)})";
            Log.Message($"[LivingWorld:test] rope swap: {ropeResult}");
            if (swapped) figure.Kill(null);
        }

        /// <summary>A friendly town nearby is made well-disposed and sends a neighbourly visit.</summary>
        private static void ForceNeighbourVisit()
        {
            neighboured = true;
            Map map = Find.AnyPlayerHomeMap;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            CompSettlementLife near = comp.activeSettlements
                .Where(s => !s.Settlement.Faction.HostileTo(Faction.OfPlayer) && !s.Settlement.Faction.def.permanentEnemy && Neighbours.CanSendAnyone(s.Settlement.Faction, map)
                            && Find.WorldGrid.ApproxDistanceInTiles(s.Settlement.Tile, map.Tile) <= Neighbourhood.Radius(map.Tile, Neighbours.Range))
                .OrderBy(s => Find.WorldGrid.ApproxDistanceInTiles(s.Settlement.Tile, map.Tile)).FirstOrDefault();
            if (near == null)
            {
                neighbourResult = "skipped (no friendly town in range)";
                Log.Message($"[LivingWorld:test] neighbourly visit {neighbourResult}");
                return;
            }
            Faction fac = near.Settlement.Faction;
            int letters = Find.LetterStack.LettersListForReading.Count;
            Shocks.StartFair(comp, near);
            if (!near.FairOn || Find.LetterStack.LettersListForReading.Count <= letters) neighbourResult = "FAILED (fair not started or no invitation)";
            if (fac.PlayerGoodwill < 40) fac.TryAffectGoodwillWith(Faction.OfPlayer, 40 - fac.PlayerGoodwill, canSendMessage: false, canSendHostilityLetter: false);
            int before = Bridge.StatMerchants + Bridge.StatVisitors, sent = Neighbours.StatSent;
            comp.lastNeighbourVisitTick = FactionState.Never;
            Neighbours.DailyCheck(comp, force: true);
            if (neighbourResult == null) neighbourResult = Neighbours.StatSent > sent && Bridge.StatMerchants + Bridge.StatVisitors > before ? "ok"
                : $"FAILED (sent {Neighbours.StatSent - sent}, parties {Bridge.StatMerchants + Bridge.StatVisitors - before})";
            Log.Message($"[LivingWorld:test] neighbourly visit from {near.Settlement.Label}: {neighbourResult}");
        }

        /// <summary>intercept: a colonist caravan waits a few tiles ahead on the war band's route.</summary>
        private static void PlaceCaravanOnRoute(Map map)
        {
            WorldParty band = WorldParty.All.FirstOrDefault(x => x.kind == LWPartyDefOf.LW_WarBand && x.deferredParms != null);
            Pawn colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => !x.Downed);
            if (band == null || colonist == null)
            {
                Log.Message("[LivingWorld:test] intercept: no war band or colonist");
                return;
            }
            band.LimitTravelTime(int.MaxValue); // computes the path
            PlanetTile tile = band.PeekPath(3);
            Caravan c = CaravanMaker.MakeCaravan(new[] { colonist }, Faction.OfPlayer, tile, addToWorldPawnsIfNotAlready: true);
            Log.Message($"[LivingWorld:test] intercept: caravan {c.Label} placed at {tile} on the route of {band.Label} (at {band.Tile})");
        }

        /// <summary>roadtrade: a caravan with silver waits on the merchants' route.</summary>
        private static void PlaceTraderCaravanOnRoute(Map map)
        {
            WorldParty merchants = WorldParty.All.FirstOrDefault(x => x.kind == LWPartyDefOf.LW_Merchants && x.deferredParms != null);
            Pawn colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => !x.Downed && !StatDefOf.TradePriceImprovement.Worker.IsDisabledFor(x));
            if (merchants == null || colonist == null) return;
            merchants.LimitTravelTime(int.MaxValue);
            PlanetTile tile = merchants.PeekPath(2);
            roadCaravan = CaravanMaker.MakeCaravan(new[] { colonist }, Faction.OfPlayer, tile, addToWorldPawnsIfNotAlready: true);
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = 6000;
            colonist.inventory.innerContainer.TryAdd(silver);
            Log.Message($"[LivingWorld:test] roadtrade: caravan at {tile} on the route of {merchants.Label}");
        }

        private static void TryRoadTrade()
        {
            WorldParty party = WorldParty.All.FirstOrDefault(x => x.kind == LWPartyDefOf.LW_Merchants && x.Tile == roadCaravan.Tile);
            if (party == null) return;
            roadTraded = true;
            Pawn negotiator = roadCaravan.PawnsListForReading.First();
            bool gizmo = party.GetCaravanGizmos(roadCaravan).Any();
            TradeSession.SetupWith(party, negotiator, giftMode: false);
            try
            {
                Tradeable buy = TradeSession.deal.AllTradeables.Where(t => !t.IsCurrency && t.TraderWillTrade && t.CanAdjustTo(1).Accepted).OrderBy(t => t.GetPriceFor(TradeAction.PlayerBuys)).FirstOrDefault();
                if (buy == null)
                {
                    var all = TradeSession.deal.AllTradeables;
                    roadTradeResult = $"merchants had nothing affordable: {all.Count} tradeables, trader-held {all.Count(t => t.CountHeldBy(Transactor.Trader) > 0)}, " +
                                      $"willTrade {all.Count(t => t.TraderWillTrade)}, goods {party.Goods.Count()}, kind {party.TraderKind?.defName}";
                    return;
                }
                int before = negotiator.inventory.innerContainer.TotalStackCountOfDef(buy.ThingDef);
                buy.AdjustTo(1); // positive = from the trader to the player
                TradeSession.deal.TryExecute(out bool traded);
                int after = negotiator.inventory.innerContainer.TotalStackCountOfDef(buy.ThingDef);
                roadTradeResult = $"gizmo={gizmo} bought {buy.ThingDef.defName}: traded={traded} inventory {before}->{after}";
                if (!traded || after <= before) roadTradeResult = "FAILED " + roadTradeResult;
            }
            finally
            {
                TradeSession.Close();
            }
            Log.Message($"[LivingWorld:test] roadtrade: {roadTradeResult}");
        }

        /// <summary>ambush: the vanilla caravan ambush incident, to tell engine limits from our interception code.</summary>
        public static void VanillaAmbush()
        {
            Map map = Find.AnyPlayerHomeMap;
            Pawn colonist = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(x => !x.Downed);
            if (colonist == null) return;
            PlanetTile tile = TileFinder.TryFindPassableTileWithTraversalDistance(map.Tile, 2, 4, out PlanetTile t) ? t : map.Tile;
            Caravan c = CaravanMaker.MakeCaravan(new[] { colonist }, Faction.OfPlayer, tile, addToWorldPawnsIfNotAlready: true);
            IncidentDef ambush = DefDatabase<IncidentDef>.GetNamed("Ambush");
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(ambush.category, c);
            parms.forced = true;
            Log.Message($"[LivingWorld:test] vanilla ambush on {c.Label}: {ambush.Worker.TryExecute(parms)}");
        }

        private static IEnumerable<string> Checks()
        {
            if (AutoTest.Plan == "roadtrade")
            {
                if (!roadTraded) yield return "caravan never met the merchants";
                else if (roadTradeResult == null || roadTradeResult.StartsWith("FAILED") || roadTradeResult.StartsWith("merchants had")) yield return "road trade: " + roadTradeResult;
                yield break;
            }
            if (AutoTest.Plan == "ambush")
            {
                if (Find.Maps.Count < 2) yield return "no ambush map";
                yield break;
            }
            if (AutoTest.Plan == "intercept")
            {
                if (Interception.StatEncounters == 0) yield return "war band never met the caravan";
                yield break;
            }
            int returning = 0, figures = LivingWorldComponent.Instance.figures.Count;
            foreach (Figure f in LivingWorldComponent.Instance.figures)
            {
                if (f.visits >= 2) returning++;
                Log.Message($"[LivingWorld:test] figure at end: {f.name} ({f.role}, {f.homeName}) status={f.status} visits={f.visits} deeds={f.deeds.Count}");
            }
            if ((AutoTest.Plan == "normal" || AutoTest.Plan == "hospitality" || AutoTest.Plan == "spaceports") && Bridge.StatTradesArrived > 0)
            {
                if (figures == 0) yield return "no figures registered";
                // Only testable if someone could be called back (a raid may kill every figure first).
                else if (returning == 0 && refired.Count > 0 && colonyWiped) Log.Message("[LivingWorld:test] recurring figures skipped: the colony was wiped out before the second visits arrived");
                else if (returning == 0 && refired.Count > 0) yield return "no figure came back for a second visit";
                else if (returning == 0) Log.Message("[LivingWorld:test] recurring figures skipped: no figure was free to be called back");
            }
            foreach (string p in probeProblems) yield return p;
            if (AutoTest.Plan == "normal" && neighbourResult != null && neighbourResult.StartsWith("FAILED")) yield return "neighbourly visit " + neighbourResult;
            if (ropeResult != null && ropeResult.StartsWith("FAILED")) yield return "rope swap " + ropeResult;
            Figure ordered = commissionFigure >= 0 ? Figures.ById(commissionFigure) : null;
            if (inspectText != null && (!inspectText.Contains("LW_Inspect_From".Translate("", "").Resolve().Trim().Split(' ')[0]) || !inspectText.Contains("Ordered")))
                yield return "merchant inspect pane lacks the world's lines";
            if (ordered != null && ordered.visits >= 2 && commissionPaid <= 0) yield return $"commission: {ordered.name} came back but the order was not delivered and paid (paid {commissionPaid})";
            if (audienceResult == "FAILED") yield return "ruler audience: accepting the proposal had no effect";
            // The audience letter is only checked in plan normal.
            if (AutoTest.Plan == "normal" && Figures.StatRulerVisits > 0 && !audienceTested) yield return "a ruler visited but no audience letter came";
            int stories = 0, told = 0;
            foreach (IncidentContext ctx in LivingWorldComponent.Instance.contexts)
                if (ctx.story != 0)
                {
                    stories++;
                    told += ctx.newsTold;
                }
            Log.Message($"[LivingWorld:test] visitor stories: {stories} groups with news from home, {told} told; ruler visits {Figures.StatRulerVisits} (asked {rulerVisitAsked})");
            // Only if a visit of that faction actually arrived and was considered (it may still be on the road at the end).
            if (rulerVisitAsked && Figures.TestRulerVisitEvaluated && Figures.StatRulerVisits == 0) yield return "a ruler visit was forced but never happened";
            if (Bridge.StatTradesArrived > 0 && probeTradeables == 0 && AutoTest.Plan != "spaceports")
            {
                // A caravan killed or gone before it could trade (the world decides) is no market bug.
                if (probeHadNegotiator && probeSawTrader) yield return "market probe found no tradeables";
                else if (probeHadNegotiator) Log.Message("[LivingWorld:test] market probe skipped: no trader could trade on the map within a day");
                else Log.Message("[LivingWorld:test] market probe skipped: no colonist able to trade");
            }
            if (!Bridge.RaidWorkerSwapped) yield return "raid worker not swapped";
            if (AutoTest.Plan != "bridge" && AutoTest.Plan != "normal" && AutoTest.Plan != "hospitality" && AutoTest.Plan != "spaceports") yield break;
            if (AutoTest.Plan == "normal")
            {
                Log.Message($"[LivingWorld:test] raid retries: {Bridge.StatRaidRetries}, raids arrived {Bridge.StatRaidsArrived}");
                if (Bridge.StatRaidsArrived > 0 && Bridge.StatRaidRetries == 0) yield return "the forced raid failure never went through the retry";
            }
            if (Bridge.StatWarBands == 0) yield return "no war band dispatched";
            else if (Bridge.StatRaidsArrived == 0 && (Bridge.StatRaidNoWayIn > 0 || Bridge.StatWarBandsTurnedBack > 0))
                Log.Message($"[LivingWorld:test] war band did not raid for a world reason: no way in {Bridge.StatRaidNoWayIn}, turned back {Bridge.StatWarBandsTurnedBack}");
            else if (Bridge.StatRaidsArrived == 0) yield return "war band never raided";
            if (AutoTest.Plan != "spaceports")
            {
                if (Bridge.StatMerchants == 0) yield return "no merchants dispatched";
                else if (Bridge.StatTradesArrived == 0) yield return "merchants never arrived";
            }
            if (AutoTest.Plan != "spaceports")
            {
                if (Bridge.StatVisitors == 0) yield return "no visitors dispatched";
                else if (Bridge.StatVisitsArrived == 0) yield return "visitors never arrived";
            }
            if (ModsConfig.IsActive("Orion.Hospitality") && !HospitalityCompat.Active) yield return "Hospitality active but integration not installed";
            if (Bridge.StatVisitsArrived > 0 && Bridge.StatVisitLords == 0) yield return "visitors arrived but no visit lord (guests never spawned)";
        }
    }
}
