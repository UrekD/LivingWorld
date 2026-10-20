using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// The storyteller bridge: raids and trade caravans set out from real settlements and travel to the colony as
    /// world parties; groups on the colony map are tied to their home settlement and go home afterwards.
    /// </summary>
    public static class Bridge
    {
        /// <summary>Set while a deferred incident executes: the lords it makes get this context.</summary>
        public static IncidentContext Scope;
        /// <summary>Set while a deferred incident executes, so the swapped workers run the vanilla path.</summary>
        public static bool Bypass;

        public static bool RaidWorkerSwapped, TraderWorkerSwapped, VisitorWorkerSwapped;

        /// <summary>Test hooks: faction loadID → settlement the next war band / trade caravan comes from (not saved).
        /// Revenge raids use the saved PendingRevenge instead.</summary>
        public static readonly Dictionary<int, int> PreferredSource = new Dictionary<int, int>();
        public static readonly Dictionary<int, int> PreferredTradeSource = new Dictionary<int, int>();
        public static readonly Dictionary<int, int> PreferredVisitSource = new Dictionary<int, int>();
        public static bool TestFailNextRaid;
        public static int StatRaidRetries, StatRaidNoWayIn, StatWarBandsTurnedBack;

        /// <summary>Faction loadID → (origin settlement id, tick) of a visit just delivered whose lord may be made later
        /// (Hospitality creates it in a dialog callback, after our scope ended).</summary>
        public static readonly Dictionary<int, (int originId, int tick)> PendingVisitOrigin = new Dictionary<int, (int, int)>();

        /// <summary>Counters for the autotest (not saved).</summary>
        public static int StatWarBands, StatMerchants, StatVisitors, StatRaidsArrived, StatTradesArrived, StatVisitsArrived, StatReturns, StatVisitLords;

        /// <summary>Startup: swap the vanilla raid and trade caravan workers for ours (only if no other mod did).</summary>
        public static void SwapWorkers()
        {
            IncidentDef raid = IncidentDefOf.RaidEnemy;
            if (raid.workerClass == typeof(IncidentWorker_RaidEnemy))
            {
                raid.workerClass = typeof(IncidentWorker_RaidEnemy_LW);
                RaidWorkerSwapped = true;
            }
            else LWLog.Warning($"RaidEnemy uses {raid.workerClass?.FullName} (another mod); raids will not travel as war bands");
            IncidentDef trader = IncidentDefOf.TraderCaravanArrival;
            if (trader.workerClass == typeof(IncidentWorker_TraderCaravanArrival))
            {
                trader.workerClass = typeof(IncidentWorker_TraderCaravanArrival_LW);
                TraderWorkerSwapped = true;
            }
            else LWLog.Warning($"TraderCaravanArrival uses {trader.workerClass?.FullName} (another mod); trade caravans will not travel visibly");
            // Visitors: vanilla worker swapped here; Hospitality's worker gets a prefix (HospitalityCompat).
            if (RaidWorkerSwapped && raid.Worker.GetType() != raid.workerClass) LWLog.Warning("RaidEnemy worker was created before our swap; war bands disabled");
            IncidentDef visitors = IncidentDefOf.VisitorGroup;
            if (visitors.workerClass == typeof(IncidentWorker_VisitorGroup))
            {
                visitors.workerClass = typeof(IncidentWorker_VisitorGroup_LW);
                VisitorWorkerSwapped = true;
            }
        }

        /// <summary>The colony map a world party can walk to (surface home map), or null.</summary>
        public static Map TravelTarget(IncidentParms parms)
        {
            if (parms.target is not Map map || !map.IsPlayerHome || map.Parent == null || !map.Tile.Valid) return null;
            if (map.Tile.Layer == null || !map.Tile.Layer.IsRootSurface) return null;
            return map;
        }

        /// <summary>A settlement of the faction that can send a party to the tile: near ones weighted by strength.</summary>
        public static Settlement PickSource(Faction faction, PlanetTile target, bool needStrength)
        {
            FactionState fs = LivingWorldComponent.Instance.StateFor(faction);
            if (fs == null) return null;
            if (fs.settlements.Count == 0) LivingWorldComponent.Instance.RebuildIndexes(seedMissing: true);
            var near = new List<(CompSettlementLife s, float d)>();
            foreach (CompSettlementLife s in fs.settlements)
            {
                if (!s.Active || !Find.WorldReachability.CanReach(s.Settlement.Tile, target)) continue;
                near.Add((s, Find.WorldGrid.ApproxDistanceInTiles(s.Settlement.Tile, target)));
            }
            if (near.Count == 0) return null;
            near.SortBy(x => x.d);
            if (near.Count > 5) near.RemoveRange(5, near.Count - 5);
            (CompSettlementLife s, float d) pick = near.RandomElementByWeight(x => (needStrength ? x.s.garrison + 50f : 100f) / Mathf.Pow(x.d + 5f, 2f));
            return pick.s.Settlement;
        }

        public static bool PartiesAllowed(Faction faction)
        {
            LivingWorldSettings s = LivingWorldMod.Settings;
            return s.worldParties && WorldParty.All.Count < s.maxParties && faction != null && FactionState.IsSimulated(faction);
        }

        /// <summary>Raid: a war band sets out instead of the raid appearing at once. False = fire it the vanilla way.</summary>
        public static bool TryDispatchRaid(IncidentDef def, IncidentParms parms)
        {
            bool revenge = parms.questTag == Revenge.QuestTag;
            if (!LivingWorldMod.Settings.raidsAsWarBands || parms.forced || parms.quest != null || (!parms.questTag.NullOrEmpty() && !revenge)) return false;
            Map map = TravelTarget(parms);
            if (map == null || parms.raidArrivalMode == null || !parms.raidArrivalMode.walkIn || !PartiesAllowed(parms.faction)) return false;
            Settlement source = null;
            int prefId = revenge ? Revenge.TakeQueuedHome(parms.faction) : -1;
            if (prefId < 0 && PreferredSource.TryGetValue(parms.faction.loadID, out int testPref)) prefId = testPref;
            PreferredSource.Remove(parms.faction.loadID);
            if (LivingWorldComponent.WorldObjectById(prefId) is Settlement pref && pref.Faction == parms.faction && Find.WorldReachability.CanReach(pref.Tile, map.Tile))
                source = pref;
            source ??= PickSource(parms.faction, map.Tile, needStrength: true);
            if (source == null) return false;
            CompSettlementLife life = source.GetComponent<CompSettlementLife>();
            float paid = Mathf.Min(life.garrison, parms.points);
            WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_WarBand, parms.faction, source, map.Parent);
            p.strength = parms.points;
            p.strengthPaid = paid;
            p.deferredDef = def;
            p.deferredParms = parms;
            parms.spawnCenter = IntVec3.Invalid;
            LivingWorldSettings st = LivingWorldMod.Settings;
            p.LimitTravelTime(Mathf.RoundToInt(st.warBandMaxHours * GenDate.TicksPerHour));
            if (p.Destroyed || p.phase != PartyPhase.Outbound || p.deferredParms == null)
            {
                // No way there after all: the storyteller's raid happens the vanilla way.
                if (!p.Destroyed) p.Disband();
                parms.spawnCenter = IntVec3.Invalid;
                return false;
            }
            life.garrison -= paid;
            // A dread warlord at its head draws more to the band.
            Figure warlord = LivingWorldMod.Settings.figures && Figures.FindFor(source.ID, FigureRole.Warlord) is Figure w && w.Available ? w : null;
            if (warlord != null && warlord.Renowned)
            {
                // Not on top of a revenge raid's own bonus (the larger one counts), and never past vanilla's threat cap.
                parms.points = Mathf.Min(parms.points * (revenge ? Careers.DreadWarlordPoints / Revenge.PointsFactor : Careers.DreadWarlordPoints), 10000f);
                p.strength = parms.points;
            }
            int eta = p.TicksToArrive();
            TaggedString text = "LW_WarBandSpottedText".Translate(parms.faction.NameColored, source.Label.Named("ORIGIN"), map.Parent.Label.Named("COLONY"),
                eta.ToStringTicksToPeriod().Named("ETA"), Mathf.RoundToInt(parms.points).Named("POINTS"));
            if (revenge) text = "LW_RevengeIntro".Translate(parms.faction.NameColored, source.Label.Named("ORIGIN")) + "\n\n" + text;
            FactionState raiders = LivingWorldComponent.Instance.StateFor(parms.faction);
            if (raiders != null && raiders.goal == RulerGoalKind.Avenge && raiders.goalTargetId == Faction.OfPlayer.loadID && parms.faction.leader != null)
                text += "\n\n" + "LW_WarBandRulerVengeance".Translate(parms.faction.leader.Name.ToStringFull.Named("NAME"));
            // A known warlord at its head: say so now, not with a second letter when they arrive.
            if (warlord != null)
                text += "\n\n" + (warlord.FeudOpen
                    ? "LW_WarBandAvenger".Translate(warlord.pawn.Named("PAWN"), (warlord.avenges ?? "?").Named("FALLEN"), warlord.nemesisName.Named("SLAYER"), source.Label.Named("HOME"))
                    : "LW_WarBandLedBy".Translate(warlord.pawn.Named("PAWN"), source.Label.Named("HOME"), (warlord.visits + 1).Named("VISITS")));
            Find.LetterStack.ReceiveLetter("LW_WarBandSpottedLabel".Translate(parms.faction.Name), text, LetterDefOf.ThreatSmall, new LookTargets(p), parms.faction);
            ChronicleEventDef ce = LWChronicleDefOf.LW_WarBandMarches;
            LivingWorldComponent.Instance.chronicle.Record(ce, ce.text.Formatted(parms.faction.Name.Named("FACTION"), source.Label.Named("ORIGIN"),
                map.Parent.Label.Named("COLONY")).Resolve(), parms.faction, Faction.OfPlayer, source, silent: true);
            StatWarBands++;
            return true;
        }

        /// <summary>Trade caravan: merchants set out from a friendly settlement. False = arrive the vanilla way.</summary>
        public static bool TryDispatchTraders(IncidentDef def, IncidentParms parms)
        {
            if (parms.forced || parms.quest != null) return Refused("traders", "forced/quest");
            Map map = TravelTarget(parms);
            if (map == null || !PartiesAllowed(parms.faction) || parms.faction.HostileTo(Faction.OfPlayer)) return Refused("traders", $"map {map != null}, parties allowed {PartiesAllowed(parms.faction)}");
            Settlement source = TakePreferred(PreferredTradeSource, parms.faction, map) ?? PickSource(parms.faction, map.Tile, needStrength: false);
            if (source == null) return Refused("traders", "no source settlement");
            WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_Merchants, parms.faction, source, map.Parent);
            p.strength = parms.points > 0f ? parms.points : 300f;
            p.deferredDef = def;
            p.deferredParms = parms;
            parms.spawnCenter = IntVec3.Invalid;
            p.LimitTravelTime(Mathf.RoundToInt(LivingWorldMod.Settings.merchantMaxHours * GenDate.TicksPerHour));
            Messages.Message("LW_MerchantsOnTheWay".Translate(source.Label.Named("ORIGIN"), parms.faction.Name.Named("FACTION"), map.Parent.Label.Named("COLONY"),
                p.TicksToArrive().ToStringTicksToPeriod().Named("ETA")), new LookTargets(p), MessageTypeDefOf.NeutralEvent);
            StatMerchants++;
            return true;
        }

        /// <summary>The faction has peaceful groups (visitors) to send.</summary>
        public static bool CanSendVisitors(Faction f) =>
            f?.def.pawnGroupMakers != null && f.def.pawnGroupMakers.Any(m => m.kindDef == PawnGroupKindDefOf.Peaceful);

        private static bool Refused(string what, string why)
        {
            if (Testing.AutoTest.Active) LWLog.Message($"[bridge] {what} not dispatched: {why}");
            return false;
        }

        /// <summary>A settlement asked for by the caller (neighbourly visits, tests), if it can still send the group.</summary>
        private static Settlement TakePreferred(Dictionary<int, int> preferred, Faction faction, Map map)
        {
            if (!preferred.TryGetValue(faction.loadID, out int id)) return null;
            preferred.Remove(faction.loadID);
            return LivingWorldComponent.WorldObjectById(id) is Settlement s && s.Faction == faction && Find.WorldReachability.CanReach(s.Tile, map.Tile) ? s : null;
        }

        private static readonly Dictionary<System.Type, System.Reflection.MethodInfo> workerMethods = new Dictionary<System.Type, System.Reflection.MethodInfo>();

        private static bool ExecuteWorkerDirect(IncidentWorker worker, IncidentParms parms)
        {
            System.Type t = worker.GetType();
            if (!workerMethods.TryGetValue(t, out System.Reflection.MethodInfo m))
                workerMethods[t] = m = HarmonyLib.AccessTools.Method(t, "TryExecuteWorker", new[] { typeof(IncidentParms) });
            return m != null ? (bool)m.Invoke(worker, new object[] { parms }) : worker.TryExecute(parms);
        }

        /// <summary>Visitors: a group sets out from a friendly settlement. False = arrive the vanilla (or Hospitality) way.</summary>
        public static bool TryDispatchVisitors(IncidentDef def, IncidentParms parms)
        {
            if (parms.forced || parms.quest != null) return false;
            Map map = TravelTarget(parms);
            if (map == null) return false;
            // The group's faction may not be chosen yet: pick a friendly simulated faction with a settlement in reach.
            if (parms.faction == null)
            {
                var candidates = new List<Faction>();
                foreach (FactionState fs in LivingWorldComponent.Instance.factionStates)
                    if (fs.faction != null && fs.settlements.Count > 0 && !fs.faction.HostileTo(Faction.OfPlayer) && !fs.faction.def.permanentEnemy
                        && CanSendVisitors(fs.faction))
                        candidates.Add(fs.faction);
                if (!candidates.TryRandomElement(out parms.faction)) return false;
            }
            // A faction without peaceful groups (the Empire) cannot walk in as visitors at all: leave it to vanilla.
            if (!CanSendVisitors(parms.faction)) return Refused("visitors", "no peaceful group makers");
            // Spaceports flies Hospitality guests in by shuttle (only those): leave them to it.
            if (!PartiesAllowed(parms.faction) || parms.faction.HostileTo(Faction.OfPlayer) || (HospitalityCompat.Active && SpaceportsCompat.WouldFly(map, parms.faction))) return false;
            Settlement source = TakePreferred(PreferredVisitSource, parms.faction, map) ?? PickSource(parms.faction, map.Tile, needStrength: false);
            if (source == null) return false;
            WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_Visitors, parms.faction, source, map.Parent);
            p.strength = parms.points > 0f ? parms.points : 80f;
            p.deferredDef = def;
            p.deferredParms = parms;
            parms.spawnCenter = IntVec3.Invalid;
            p.LimitTravelTime(Mathf.RoundToInt(LivingWorldMod.Settings.merchantMaxHours * GenDate.TicksPerHour));
            Messages.Message("LW_VisitorsOnTheWay".Translate(source.Label.Named("ORIGIN"), parms.faction.Name.Named("FACTION"), map.Parent.Label.Named("COLONY"),
                p.TicksToArrive().ToStringTicksToPeriod().Named("ETA")), new LookTargets(p), MessageTypeDefOf.NeutralEvent);
            StatVisitors++;
            return true;
        }

        /// <summary>Runs a party's stored incident at the colony, with its context in scope. True if it happened.</summary>
        public static bool ExecuteDeferred(WorldParty p, ContextKind kind)
        {
            IncidentParms parms = p.deferredParms;
            if (parms == null || p.deferredDef == null) return false;
            if (parms.target is not Map map || map.Parent == null || !map.IsPlayerHome || !Find.Maps.Contains(map)) return false;
            // Consumed now: an incident that throws must not fire again on the next tick.
            p.deferredParms = null;
            Scope = new IncidentContext
            {
                kind = kind,
                originId = p.originId,
                originName = p.originName,
                factionId = p.Faction?.loadID ?? -1,
                partyId = p.ID,
                strengthPaid = p.strengthPaid,
                points = parms.points,
                createdTicks = Find.TickManager.TicksGame,
                mapLabel = map.Parent.Label,
            };
            Bypass = true;
            try
            {
                parms.spawnCenter = IntVec3.Invalid;
                bool ok;
                try
                {
                    // Tests: the first attempt of the next war band raid counts as "found no way in" (exercises the retry).
                    if (TestFailNextRaid && kind == ContextKind.Raid)
                    {
                        TestFailNextRaid = false;
                        ok = false;
                    }
                    else ok = p.deferredDef.Worker switch
                    {
                        IncidentWorker_RaidEnemy_LW raid => raid.ExecuteNow(parms),
                        IncidentWorker_TraderCaravanArrival_LW trade => trade.ExecuteNow(parms),
                        IncidentWorker_VisitorGroup_LW visit => visit.ExecuteNow(parms),
                        // Other workers (Hospitality's): run the worker itself, not TryExecute (the storyteller counted it at dispatch).
                        _ => ExecuteWorkerDirect(p.deferredDef.Worker, parms),
                    };
                }
                catch (System.Exception e)
                {
                    LWLog.Error($"deferred {p.deferredDef.defName} from {p.Label} failed: {e}");
                    ok = false;
                }
                // A war band that marched all the way does not just vanish: if vanilla found no way in (no spawn spot for
                // the chosen arrival), let it pick the arrival again once.
                if (!ok && kind == ContextKind.Raid && p.deferredDef.Worker is IncidentWorker_RaidEnemy_LW again)
                {
                    if (Testing.AutoTest.Active)
                        LWLog.Message($"[bridge] raid of {parms.faction?.Name} failed: hostile={parms.faction?.HostileTo(Faction.OfPlayer)} arrival={parms.raidArrivalMode?.defName} " +
                                      $"strategy={parms.raidStrategy?.defName} points={parms.points:F0} spawnCenter={parms.spawnCenter}; retrying with a fresh arrival");
                    StatRaidRetries++;
                    parms.raidArrivalMode = null;
                    parms.spawnCenter = IntVec3.Invalid;
                    try
                    {
                        ok = again.ExecuteNow(parms);
                    }
                    catch (System.Exception e)
                    {
                        LWLog.Error($"deferred raid retry from {p.Label} failed: {e}");
                    }
                    // Still no spawn spot: the colony has no way in from the map's edge (walled in by rock or water); the
                    // war band turns back (PartyWorker_WarBand), as a vanilla raid with nowhere to enter would not happen.
                    if (!ok && !parms.spawnCenter.IsValid) StatRaidNoWayIn++;
                }
                if (ok && kind == ContextKind.Raid) StatRaidsArrived++;
                if (ok && kind == ContextKind.Trade) StatTradesArrived++;
                if (ok && kind == ContextKind.Visit)
                {
                    StatVisitsArrived++;
                    if (p.Faction != null) PendingVisitOrigin[p.Faction.loadID] = (p.originId, Find.TickManager.TicksGame);
                }
                if (Testing.AutoTest.Active) LWLog.Message($"[bridge] {p.Label} arrived: {kind} executed={ok}");
                return ok;
            }
            finally
            {
                Bypass = false;
                Scope = null;
            }
        }

        // ---- Groups on the colony map ----

        /// <summary>A new lord on a player home map: tie it to its home settlement.</summary>
        public static void Notify_LordMade(Lord lord, Map map)
        {
            if (lord == null || map == null || lord.faction == null || !FactionState.IsSimulated(lord.faction)) return;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null) return;
            IncidentContext ctx = null;
            if (Scope != null) ctx = Scope.CloneForLord(lord.loadID);
            else if (map.IsPlayerHome && map.Tile.Valid)
            {
                ContextKind? kind = KindOf(lord);
                if (kind == null) return;
                Settlement origin = null;
                if (kind == ContextKind.Visit && PendingVisitOrigin.TryGetValue(lord.faction.loadID, out var pending) && Find.TickManager.TicksGame - pending.tick < GenDate.TicksPerDay)
                {
                    PendingVisitOrigin.Remove(lord.faction.loadID);
                    origin = LivingWorldComponent.WorldObjectById(pending.originId) as Settlement;
                }
                origin ??= NearestSettlement(lord.faction, map.Tile);
                if (origin == null) return;
                ctx = new IncidentContext
                {
                    id = comp.NextId(),
                    kind = kind.Value,
                    originId = origin.ID,
                    originName = origin.Label,
                    factionId = lord.faction.loadID,
                    lordId = lord.loadID,
                    createdTicks = Find.TickManager.TicksGame,
                    mapLabel = map.Parent?.Label,
                };
            }
            if (ctx == null) return;
            ctx.goodwillAtStart = lord.faction.HasGoodwill ? lord.faction.PlayerGoodwill : 0;
            foreach (Pawn p in lord.ownedPawns) ctx.silverAtStart += SilverOf(p);
            if (ctx.kind == ContextKind.Visit) StatVisitLords++;
            comp.contexts.Add(ctx);
            Figures.Notify_GroupArrived(lord, ctx);
            if (map.IsPlayerHome)
            {
                PlayerDeeds.TellNews(ctx);
                VisitorStories.Notify_GroupArrived(lord, ctx);
                if (ctx.kind == ContextKind.Raid)
                {
                    RaidMorale.Notify_RaidLordMade(lord, ctx);
                    RaidGear.Notify_RaidLordMade(lord);
                }
            }
            // Trade caravans carry what their home market can spare.
            if (ctx.kind == ContextKind.Trade && LivingWorldComponent.WorldObjectById(ctx.originId) is Settlement home)
            {
                CompSettlementLife life = home.GetComponent<CompSettlementLife>();
                if (life != null && life.Active)
                    foreach (Pawn p in lord.ownedPawns)
                        if (p.inventory != null) MarketTrade.BiasStock(p.inventory.innerContainer, life);
            }
        }

        private static ContextKind? KindOf(Lord lord)
        {
            if (lord.faction.HostileTo(Faction.OfPlayer)) return lord.LordJob is LordJob_AssaultColony or LordJob_StageThenAttack or LordJob_Siege ? ContextKind.Raid : null;
            if (lord.LordJob is LordJob_TradeWithColony) return ContextKind.Trade;
            // Vanilla's and Hospitality's visit lord jobs share a name; Spaceports adds shuttle variants of both.
            string job = lord.LordJob?.GetType().Name;
            if (job == "LordJob_VisitColony" || job == "LordJob_ShuttleVisitColony") return ContextKind.Visit;
            if (job == "LordJob_ShuttleTradeWithColony") return ContextKind.Trade;
            return null;
        }

        public static Settlement NearestSettlement(Faction f, PlanetTile tile)
        {
            Settlement best = null;
            float bestD = float.MaxValue;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                if (s.Faction != f || !s.Tile.Valid || s.Tile.Layer != tile.Layer) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(s.Tile, tile);
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
            return best;
        }

        public static IncidentContext ContextOf(Lord lord)
        {
            if (lord == null) return null;
            List<IncidentContext> list = LivingWorldComponent.Instance?.contexts;
            if (list == null) return null;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].lordId == lord.loadID) return list[i];
            return null;
        }

        public static void Notify_PawnExited(Pawn pawn, Lord lord)
        {
            IncidentContext ctx = ContextOf(lord);
            if (ctx == null) return;
            ctx.pawnsExited++;
            ctx.exitedPower += pawn.kindDef?.combatPower ?? 40f;
            ctx.exitedSilver += SilverOf(pawn);
        }

        private static int SilverOf(Pawn pawn)
        {
            int n = 0;
            if (pawn.inventory != null)
                foreach (Thing t in pawn.inventory.innerContainer)
                    if (t.def == ThingDefOf.Silver) n += t.stackCount;
            return n;
        }

        /// <summary>The group is gone from the map: send survivors home, charge the losses, write it down.</summary>
        public static void Notify_LordRemoved(Lord lord)
        {
            IncidentContext ctx = ContextOf(lord);
            if (ctx == null) return;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            comp.contexts.Remove(ctx);
            Faction f = lord.faction;
            int start = Mathf.Max(lord.numPawnsEverGained, ctx.pawnsExited);
            int fallen = start - ctx.pawnsExited;
            var origin = LivingWorldComponent.WorldObjectById(ctx.originId) as Settlement;
            CompSettlementLife life = origin?.GetComponent<CompSettlementLife>();
            if (ctx.kind == ContextKind.Raid && start > 0)
            {
                // A raid that did not travel was not paid for at departure: charge the losses now.
                if (ctx.partyId < 0 && life != null && life.initialized)
                {
                    float avg = ctx.points > 0f ? ctx.points / start : 60f;
                    life.garrison = Mathf.Max(0f, life.garrison - fallen * avg);
                }
                ChronicleEventDef def = LWChronicleDefOf.LW_RaidEnded;
                comp.chronicle.Record(def, def.text.Formatted(f.Name.Named("FACTION"), (ctx.originName ?? "?").Named("ORIGIN"), (ctx.mapLabel ?? "?").Named("COLONY"),
                    start.Named("COUNT"), fallen.Named("FALLEN")).Resolve(), f, Faction.OfPlayer, origin, silent: true);
            }
            if (ctx.kind == ContextKind.Visit && life != null && life.initialized && f.HasGoodwill) Fame.Notify_VisitEnded(ctx, life, f.PlayerGoodwill - ctx.goodwillAtStart);
            if (ctx.pawnsExited == 0 || origin == null || !origin.Spawned || origin.Faction != f) return;
            PartyKindDef kind = ctx.kind switch
            {
                ContextKind.Raid => LWPartyDefOf.LW_WarBand,
                ContextKind.Trade => LWPartyDefOf.LW_Merchants,
                _ => LWPartyDefOf.LW_Visitors,
            };
            Map map = lord.Map;
            if (map == null || !map.Tile.Valid) return;
            if (!PartiesAllowed(f))
            {
                // No room on the map for another party: they are home at once.
                kind.Worker.ArrivedHomeInstant(origin, ctx.exitedPower, Mathf.Max(0f, ctx.exitedSilver - ctx.silverAtStart));
                return;
            }
            WorldParty p = WorldParty.Launch(kind, f, origin, null, map.Tile);
            p.Tile = map.Tile;
            p.phase = PartyPhase.Returning;
            p.strength = ctx.exitedPower;
            // Only what they earned here counts as profit, not the purse they arrived with.
            p.silver = Mathf.Max(0f, ctx.exitedSilver - ctx.silverAtStart);
            StatReturns++;
            if (Testing.AutoTest.Active) LWLog.Message($"[bridge] {p.Label} heads home: {ctx.pawnsExited} pawns, power {ctx.exitedPower:F0}, silver {ctx.exitedSilver:F0}");
        }
    }

    [HarmonyPatch(typeof(LordMaker), nameof(LordMaker.MakeNewLord))]
    public static class Patch_LordMaker_MakeNewLord
    {
        public static void Postfix(Lord __result, Map map)
        {
            try
            {
                Bridge.Notify_LordMade(__result, map);
            }
            catch (System.Exception e)
            {
                LWLog.Error("lord context: " + e);
            }
        }
    }

    /// <summary>Counts pawns leaving the map while their lord still exists (it may remove itself right after).</summary>
    [HarmonyPatch(typeof(Lord), nameof(Lord.Notify_PawnLost))]
    public static class Patch_Lord_Notify_PawnLost
    {
        public static void Prefix(Lord __instance, Pawn pawn, PawnLostCondition cond)
        {
            if (cond != PawnLostCondition.ExitedMap || pawn == null) return;
            try
            {
                Bridge.Notify_PawnExited(pawn, __instance);
            }
            catch (System.Exception e)
            {
                LWLog.Error("pawn exit: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(LordManager), nameof(LordManager.RemoveLord))]
    public static class Patch_LordManager_RemoveLord
    {
        public static void Postfix(Lord oldLord)
        {
            try
            {
                Bridge.Notify_LordRemoved(oldLord);
            }
            catch (System.Exception e)
            {
                LWLog.Error("lord removed: " + e);
            }
        }
    }
}
