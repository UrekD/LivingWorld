using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    public enum PartyPhase : byte { Outbound, Returning }

    /// <summary>
    /// A group travelling the world map: merchants, a war band, settlers, visitors. Moves tile by tile along a path
    /// copied from the world pather (never holds a pooled WorldPath), draws as a faction-tinted icon, and hands its
    /// arrival to its kind's worker. Abstract: strength and cargo are numbers until something touches the player.
    /// </summary>
    public class WorldParty : WorldObject, ITrader, IThingHolder
    {
        public const int Schema = 1;

        public PartyKindDef kind;
        public PartyPhase phase;
        public int schemaVersion = Schema;
        /// <summary>World object id of the home settlement.</summary>
        public int originId = -1;
        /// <summary>World object id of the destination (settlement or player map parent); -1 for a bare tile.</summary>
        public int destinationId = -1;
        public PlanetTile destinationTile = PlanetTile.Invalid;
        /// <summary>Fighting strength in raid points.</summary>
        public float strength;
        /// <summary>Goods carried, by commodity index (value units).</summary>
        public float[] cargo = new float[0];
        public float silver;
        /// <summary>An incident that fires when the party reaches the player (raid, trade caravan).</summary>
        public IncidentDef deferredDef;
        public IncidentParms deferredParms;
        public int contextId = -1;
        /// <summary>Garrison strength the home settlement gave up for this party (comes back if it returns unspent).</summary>
        public float strengthPaid;
        /// <summary>&gt;1: forced march (a raid must not take longer than the storyteller allows).</summary>
        public float speedFactor = 1f;
        public string originName;
        public int departedTicks = -1;
        /// <summary>Mission details for kinds that need them (envoys: target faction and mission; settlers: people).</summary>
        public int targetFactionId = -1;
        /// <summary>Silver promised for the mission (mercenary contracts).</summary>
        public int pay;
        /// <summary>A second faction the mission is about (the colony's call to arms: the enemy).</summary>
        public int secondFactionId = -1;
        public byte mission;
        public float population;

        // Trade with player caravans (merchants only): stock generated on first contact.
        private ThingOwner<Thing> stock;
        private TraderKindDef traderKind;
        private int waitedTicks;
        private const int MaxWaitTicks = GenDate.TicksPerHour * 8;

        // Movement
        private PlanetTile prevTile = PlanetTile.Invalid;
        private PlanetTile nextTile = PlanetTile.Invalid;
        private float costLeft;
        private float costTotal = 1f;
        [Unsaved] private List<PlanetTile> path;
        [Unsaved] private int pathIndex;
        [Unsaved] private bool needsPath = true;
        [Unsaved] private Material cachedMat;

        private static int pathRequestsTick = -1;
        private static int pathRequestsThisTick;
        private const int MaxPathRequestsPerTick = 3;

        public static readonly List<WorldParty> All = new List<WorldParty>();

        public Settlement Origin => LivingWorldComponent.WorldObjectById(originId) as Settlement;
        public WorldObject Destination => destinationId < 0 ? null : LivingWorldComponent.WorldObjectById(destinationId);

        public PlanetTile GoalTile
        {
            get
            {
                if (phase == PartyPhase.Returning)
                {
                    Settlement home = Origin;
                    return home != null && home.Spawned ? home.Tile : PlanetTile.Invalid;
                }
                if (destinationId >= 0)
                {
                    WorldObject d = Destination;
                    return d != null && d.Spawned ? d.Tile : PlanetTile.Invalid;
                }
                return destinationTile;
            }
        }

        public float CargoValue
        {
            get
            {
                float v = silver;
                foreach (float c in cargo) v += c;
                return v;
            }
        }

        public override string Label
        {
            get
            {
                string pattern = phase == PartyPhase.Returning && !kind.labelReturning.NullOrEmpty() ? kind.labelReturning : kind.labelOutbound;
                return pattern.NullOrEmpty() ? kind.label : pattern.Formatted((Faction?.Name ?? "?").Named("FACTION"), (originName ?? "?").Named("ORIGIN")).Resolve();
            }
        }

        public override Material Material
        {
            get
            {
                if (cachedMat == null)
                {
                    Color c = Faction?.Color ?? Color.white;
                    cachedMat = MaterialPool.MatFrom(kind.iconPath, ShaderDatabase.WorldOverlayTransparentLit, c, WorldMaterials.DynamicObjectRenderQueue);
                }
                return cachedMat;
            }
        }

        public override Texture2D ExpandingIcon => kind.Icon;
        public override Color ExpandingIconColor => Faction?.Color ?? Color.white;
        public override float ExpandingIconPriority => kind.expandingIconPriority;

        public override Vector3 DrawPos
        {
            get
            {
                if (!nextTile.Valid || !Tile.Valid) return base.DrawPos;
                Vector3 a = Find.WorldGrid.GetTileCenter(Tile);
                Vector3 b = Find.WorldGrid.GetTileCenter(nextTile);
                float t = costTotal <= 0f ? 0f : 1f - Mathf.Clamp01(costLeft / costTotal);
                return Tile.Layer.Origin + Vector3.Lerp(a, b, t);
            }
        }

        /// <summary>Creates and spawns a party at its home settlement.</summary>
        public static WorldParty Launch(PartyKindDef kind, Faction faction, Settlement origin, WorldObject destination, PlanetTile? tile = null)
        {
            var p = (WorldParty)WorldObjectMaker.MakeWorldObject(LWPartyDefOf.LW_Party);
            p.kind = kind;
            p.SetFaction(faction);
            p.originId = origin?.ID ?? -1;
            p.originName = origin?.Label;
            p.destinationId = destination?.ID ?? -1;
            p.destinationTile = destination?.Tile ?? tile ?? PlanetTile.Invalid;
            p.cargo = new float[Commodities.Count];
            p.departedTicks = Find.TickManager.TicksGame;
            p.Tile = origin?.Tile ?? tile ?? PlanetTile.Invalid;
            Find.WorldObjects.Add(p);
            return p;
        }

        public override void SpawnSetup()
        {
            base.SpawnSetup();
            if (!All.Contains(this)) All.Add(this);
            needsPath = true;
        }

        public override void PostRemove()
        {
            base.PostRemove();
            All.Remove(this);
            if (Testing.AutoTest.Active && deferredParms != null && kind == LWPartyDefOf.LW_WarBand)
                LWLog.Message($"[bridge] {Label} removed before reaching the colony (phase {phase}, tile {Tile}, path {(nextTile.Valid ? "ok" : "none")})");
            // Colonists travelling with a player party (mercenaries) are never destroyed with it: they come home, or
            // (no home map to walk into) carry on as a caravan of the colony.
            // Only a party standing at a home of the colony walks into it; anywhere else they go on from where they are.
            if (stock != null && stock.Any(t => t is Pawn || t is Corpse))
            {
                Map here = Faction == Faction.OfPlayer ? Find.Maps.FirstOrDefault(m => m.IsPlayerHome && m.Tile == Tile) : null;
                if (here != null) Disembark(here);
                ReleaseToWorld();
            }
            stock?.ClearAndDestroyContents();
        }

        /// <summary>A colonist travels with this (player) party, held in it like a caravan member. False if they could not board
        /// (they stay where they were).</summary>
        public bool Board(Pawn p)
        {
            stock ??= new ThingOwner<Thing>(this);
            // What leaving the map would do: drop what they carry, leave their lord (a party, a ritual).
            if (p.Spawned && p.carryTracker?.CarriedThing != null) p.carryTracker.TryDropCarriedThing(p.Position, ThingPlaceMode.Near, out _);
            p.GetLord()?.Notify_PawnLost(p, PawnLostCondition.ExitedMap);
            Map map = p.MapHeld;
            IntVec3 at = p.PositionHeld;
            if (p.Spawned) p.DeSpawn();
            if (stock.TryAdd(p, canMergeWithExistingStacks: false)) return true;
            if (map != null && !p.Spawned) GenSpawn.Spawn(p, at, map);
            return false;
        }

        public IEnumerable<Pawn> Passengers => stock == null ? Enumerable.Empty<Pawn>() : stock.OfType<Pawn>();

        /// <summary>The colonists (or their bodies) walk in from the map's edge. Returns the living who did.</summary>
        public List<Pawn> Disembark(Map map)
        {
            var arrived = new List<Pawn>();
            if (stock == null || map == null) return arrived;
            foreach (Thing t in stock.ToList())
            {
                if (t is not Pawn && t is not Corpse) continue;
                // No longer the colony's (a lodger whose quest ended on the road): not ours to bring in.
                if (t is Pawn foreign && foreign.Faction != Faction.OfPlayer) continue;
                if (!RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 cell, map, CellFinder.EdgeRoadChance_Neutral)
                    && !CellFinder.TryFindRandomEdgeCellWith(c => c.Standable(map) && !c.Fogged(map), map, CellFinder.EdgeRoadChance_Neutral, out cell))
                    cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 10);
                stock.Remove(t);
                GenSpawn.Spawn(t, cell, map);
                if (t is Pawn p) arrived.Add(p);
            }
            return arrived;
        }

        /// <summary>
        /// Pawns still aboard with no map to walk into (the colony's home is gone): the living go on as a caravan of the
        /// colony where the party stood, the dead pass to the world. Never destroyed with the party.
        /// </summary>
        public void ReleaseToWorld()
        {
            if (stock == null) return;
            var living = new List<Pawn>();
            foreach (Thing t in stock.ToList())
            {
                if (t is Pawn p)
                {
                    stock.Remove(p);
                    living.Add(p);
                }
                else if (t is Corpse c)
                {
                    stock.Remove(c);
                    Pawn inner = c.InnerPawn;
                    if (inner != null)
                    {
                        c.GetDirectlyHeldThings().Remove(inner);
                        if (!Find.WorldPawns.Contains(inner)) Find.WorldPawns.PassToWorld(inner, PawnDiscardDecideMode.KeepForever);
                    }
                    c.Destroy();
                }
            }
            if (living.Count == 0) return;
            PlanetTile at = Tile.Valid ? Tile : Origin?.Tile ?? PlanetTile.Invalid;
            List<Pawn> colonists = living.Where(p => p.Faction == Faction.OfPlayer).ToList();
            foreach (Pawn p in living)
                if (p.Faction != Faction.OfPlayer || !at.Valid) Find.WorldPawns.PassToWorld(p);
            if (!at.Valid || colonists.Count == 0) return;
            Caravan caravan = CaravanMaker.MakeCaravan(colonists, Faction.OfPlayer, at, addToWorldPawnsIfNotAlready: true);
            if (silver >= 1f)
            {
                Thing coins = ThingMaker.MakeThing(ThingDefOf.Silver);
                coins.stackCount = Mathf.RoundToInt(silver);
                CaravanInventoryUtility.GiveThing(caravan, coins);
                silver = 0f;
            }
            Find.LetterStack.ReceiveLetter("LW_PartyStrandedLabel".Translate(), "LW_PartyStrandedText".Translate(colonists[0].Named("PAWN")), LetterDefOf.NeutralEvent, caravan);
        }

        public void TurnBack()
        {
            if (phase == PartyPhase.Returning || Origin == null)
            {
                Disband();
                return;
            }
            phase = PartyPhase.Returning;
            needsPath = true;
            nextTile = PlanetTile.Invalid;
        }

        /// <summary>Removes the party without arriving anywhere (home gone, nowhere to go).</summary>
        public void Disband()
        {
            if (!Destroyed) Destroy();
        }

        protected override void TickInterval(int delta)
        {
            base.TickInterval(delta);
            if (!Spawned || Destroyed) return;
            if (needsPath && !TryRecomputePath()) return;
            if (!nextTile.Valid)
            {
                Arrive();
                return;
            }
            // Merchants stop for a while when a player caravan shares their tile, so it can trade with them.
            if (kind == LWPartyDefOf.LW_Merchants)
            {
                if (PlayerCaravanHere() == null) waitedTicks = 0; // the next caravan they meet gets a stop too
                else if (waitedTicks < MaxWaitTicks)
                {
                    waitedTicks += delta;
                    return;
                }
            }
            costLeft -= delta * speedFactor;
            while (costLeft <= 0f && !Destroyed)
            {
                Tile = nextTile;
                pathIndex++;
                if (Interception.TryEncounter(this)) return;
                // The goal may have moved (gravship, captured) or vanished.
                PlanetTile goal = GoalTile;
                if (!goal.Valid)
                {
                    if (phase == PartyPhase.Outbound) kind.Worker.DestinationLost(this);
                    else Disband();
                    return;
                }
                if (path == null || pathIndex >= path.Count || path[path.Count - 1] != goal)
                {
                    nextTile = PlanetTile.Invalid;
                    if (Tile == goal) Arrive();
                    else needsPath = true;
                    return;
                }
                prevTile = Tile;
                nextTile = path[pathIndex];
                costTotal = CostTo(Tile, nextTile);
                costLeft += costTotal;
            }
        }

        private Caravan PlayerCaravanHere()
        {
            foreach (Caravan c in Find.WorldObjects.Caravans)
                if (c.IsPlayerControlled && c.Tile == Tile) return c;
            return null;
        }

        // ---- ITrader (merchant parties met by player caravans) ----

        public TraderKindDef TraderKind
        {
            get
            {
                if (traderKind == null && Faction?.def.caravanTraderKinds is { Count: > 0 } kinds)
                    traderKind = kinds[Mathf.Abs(ID) % kinds.Count];
                return traderKind;
            }
        }

        public IEnumerable<Thing> Goods
        {
            get
            {
                EnsureStock();
                return stock;
            }
        }

        public int RandomPriceFactorSeed => ID * 3571;
        public string TraderName => Label;
        public bool CanTradeNow => kind == LWPartyDefOf.LW_Merchants && Faction != null && !Faction.HostileTo(Faction.OfPlayer) && TraderKind != null;
        public float TradePriceImprovementOffsetForPlayer => 0f;
        public TradeCurrency TradeCurrency => TradeCurrency.Silver;

        private void EnsureStock()
        {
            if (stock != null) return;
            stock = new ThingOwner<Thing>(this);
            if (TraderKind == null) return;
            var parms = new ThingSetMakerParams { traderDef = TraderKind, tile = Tile, makingFaction = Faction };
            foreach (Thing t in ThingSetMakerDefOf.TraderStock.root.Generate(parms))
            {
                if (t is Pawn pawn) Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard); // no slaves or animals on the road
                else stock.TryAdd(t);
            }
            CompSettlementLife home = Origin?.GetComponent<CompSettlementLife>();
            if (home != null && home.Active) MarketTrade.BiasStock(stock, home);
        }

        public IEnumerable<Thing> ColonyThingsWillingToBuy(Pawn playerNegotiator)
        {
            Caravan caravan = playerNegotiator.GetCaravan();
            if (caravan == null) yield break;
            foreach (Thing t in CaravanInventoryUtility.AllInventoryItems(caravan)) yield return t;
            foreach (Pawn p in caravan.PawnsListForReading)
                if (!caravan.IsOwner(p)) yield return p;
        }

        public void GiveSoldThingToTrader(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            EnsureStock();
            Caravan caravan = playerNegotiator.GetCaravan();
            Thing thing = toGive.SplitOff(countToGive);
            thing.PreTraded(TradeAction.PlayerSells, playerNegotiator, this);
            if (thing is Pawn pawn)
            {
                if (caravan != null) CaravanInventoryUtility.MoveAllInventoryToSomeoneElse(pawn, caravan.PawnsListForReading);
                caravan?.RemovePawn(pawn);
                // Sold people live on as world pawns (relations intact), like vanilla settlement trade; animals go home with the herd.
                if (pawn.RaceProps.Humanlike)
                {
                    if (!Find.WorldPawns.Contains(pawn)) Find.WorldPawns.PassToWorld(pawn);
                }
                else if (!pawn.Destroyed) pawn.Destroy();
                return;
            }
            if (!stock.TryAdd(thing, canMergeWithExistingStacks: false)) thing.Destroy();
        }

        public void GiveSoldThingToPlayer(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            Caravan caravan = playerNegotiator.GetCaravan();
            Thing thing = toGive.SplitOff(countToGive);
            thing.PreTraded(TradeAction.PlayerBuys, playerNegotiator, this);
            Pawn carrier = caravan == null ? null : CaravanInventoryUtility.FindPawnToMoveInventoryTo(thing, caravan.PawnsListForReading, null);
            if (carrier == null || !carrier.inventory.innerContainer.TryAdd(thing))
            {
                Log.Error($"[LivingWorld] could not give bought {thing} to a caravan pawn");
                thing.Destroy();
            }
        }

        public ThingOwner GetDirectlyHeldThings() => stock;

        public void GetChildHolders(List<IThingHolder> outChildren) => ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());

        public override IEnumerable<Gizmo> GetCaravanGizmos(Caravan caravan)
        {
            foreach (Gizmo g in base.GetCaravanGizmos(caravan)) yield return g;
            if (!caravan.IsPlayerControlled || caravan.Tile != Tile || !CanTradeNow) yield break;
            Pawn negotiator = BestCaravanPawnUtility.FindBestNegotiator(caravan, Faction, TraderKind);
            var cmd = new Command_Action
            {
                defaultLabel = "LW_TradeWithParty".Translate(Label),
                defaultDesc = "LW_TradeWithPartyDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/Trade"),
                action = () =>
                {
                    if (negotiator != null && CanTradeNow) Find.WindowStack.Add(new Dialog_Trade(negotiator, this));
                },
            };
            if (negotiator == null) cmd.Disable("CommandTradeFailNoNegotiator".Translate());
            yield return cmd;
        }

        private float CostTo(PlanetTile from, PlanetTile to) => Caravan_PathFollower.CostToMove(kind.ticksPerTile, from, to);

        private bool TryRecomputePath()
        {
            int now = Find.TickManager.TicksGame;
            if (pathRequestsTick != now)
            {
                pathRequestsTick = now;
                pathRequestsThisTick = 0;
            }
            if (pathRequestsThisTick >= MaxPathRequestsPerTick) return false;
            pathRequestsThisTick++;
            needsPath = false;
            PlanetTile goal = GoalTile;
            if (!goal.Valid)
            {
                if (phase == PartyPhase.Outbound) kind.Worker.DestinationLost(this);
                else Disband();
                return false;
            }
            path ??= new List<PlanetTile>();
            path.Clear();
            pathIndex = 0;
            if (Tile == goal)
            {
                nextTile = PlanetTile.Invalid;
                return true;
            }
            PlanetTile keepNext = nextTile;
            WorldPath wp = Tile.Layer.Pather.FindPath(Tile, goal, null);
            try
            {
                if (wp == null || !wp.Found)
                {
                    if (phase == PartyPhase.Outbound) kind.Worker.DestinationLost(this);
                    else Disband();
                    return false;
                }
                // NodesReversed: [0] = destination … [last] = start.
                List<PlanetTile> nodes = wp.NodesReversed;
                for (int i = nodes.Count - 2; i >= 0; i--) path.Add(nodes[i]);
            }
            finally
            {
                wp?.ReleaseToPool();
            }
            if (path.Count == 0)
            {
                nextTile = PlanetTile.Invalid;
                return true;
            }
            nextTile = path[0];
            float cost = CostTo(Tile, nextTile);
            // Resuming after a load toward the same tile: keep the progress made.
            if (keepNext == nextTile && costLeft > 0f && costLeft <= cost) costTotal = cost;
            else costTotal = costLeft = cost;
            prevTile = Tile;
            return true;
        }

        private void Arrive()
        {
            if (phase == PartyPhase.Outbound)
            {
                if (kind.Worker.ArrivedAtDestination(this)) Disband();
            }
            else
            {
                kind.Worker.ArrivedHome(this);
                Disband();
            }
        }

        /// <summary>A tile some steps ahead on the current path (or the goal), for tests and previews.</summary>
        public PlanetTile PeekPath(int ahead)
        {
            if (path == null || path.Count == 0) return GoalTile;
            return path[System.Math.Min(path.Count - 1, pathIndex + ahead)];
        }

        /// <summary>Ticks until arrival along the current path (estimate).</summary>
        public int TicksToArrive()
        {
            if (path == null || needsPath) return EstimateTicksStraight();
            float total = Mathf.Max(0f, costLeft);
            for (int i = pathIndex; i + 1 < path.Count; i++) total += CostTo(path[i], path[i + 1]);
            return Mathf.RoundToInt(total / Mathf.Max(0.01f, speedFactor));
        }

        private int EstimateTicksStraight()
        {
            PlanetTile goal = GoalTile;
            if (!goal.Valid) return 0;
            return Mathf.RoundToInt(Find.WorldGrid.ApproxDistanceInTiles(Tile, goal) * kind.ticksPerTile * 1.3f / Mathf.Max(0.01f, speedFactor));
        }

        /// <summary>Speeds the party up so it arrives within maxTicks (forced march); computes the path if needed.</summary>
        public void LimitTravelTime(int maxTicks)
        {
            if (needsPath)
            {
                pathRequestsThisTick = 0;
                TryRecomputePath();
            }
            int t = TicksToArrive();
            if (t > maxTicks && maxTicks > 0) speedFactor *= t / (float)maxTicks;
        }

        public override void DrawExtraSelectionOverlays()
        {
            base.DrawExtraSelectionOverlays();
            if (path == null || needsPath) return;
            // Lifted off the surface like vanilla caravan paths, or terrain hides the line.
            const float lift = 0.08f;
            Vector3 from = DrawPos;
            from += from.normalized * lift;
            for (int i = pathIndex; i < path.Count; i++)
            {
                Vector3 to = Find.WorldGrid.GetTileCenter(path[i]) + Tile.Layer.Origin;
                to += to.normalized * lift;
                GenDraw.DrawWorldLineBetween(from, to);
                from = to;
            }
        }

        public override string GetInspectString()
        {
            var sb = new StringBuilder(base.GetInspectString());
            void Line(string s)
            {
                if (s.NullOrEmpty()) return;
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(s);
            }
            WorldObject dest = phase == PartyPhase.Returning ? Origin : Destination;
            string destLabel = dest?.Label ?? (phase == PartyPhase.Returning ? originName : null) ?? "LW_Party_Somewhere".Translate().Resolve();
            Line((phase == PartyPhase.Returning ? "LW_Party_ReturningTo" : "LW_Party_HeadingTo").Translate(destLabel));
            if (phase == PartyPhase.Outbound && originName != null) Line("LW_Party_From".Translate(originName));
            Line("LW_Party_Strength".Translate(Mathf.RoundToInt(strength)));
            if (CargoValue > 1f) Line("LW_Party_Cargo".Translate(Mathf.RoundToInt(CargoValue)));
            Line("LW_Party_ETA".Translate(TicksToArrive().ToStringTicksToPeriod()));
            Line(kind.Worker.InspectExtra(this));
            return sb.ToString();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos()) yield return g;
            if (Prefs.DevMode)
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Arrive now",
                    action = () =>
                    {
                        PlanetTile goal = GoalTile;
                        if (goal.Valid) Tile = goal;
                        nextTile = PlanetTile.Invalid;
                        needsPath = false;
                        Arrive();
                    },
                };
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref kind, "kind");
            Scribe_Values.Look(ref phase, "phase");
            Scribe_Values.Look(ref schemaVersion, "schema");
            Scribe_Values.Look(ref originId, "originId", -1);
            Scribe_Values.Look(ref destinationId, "destinationId", -1);
            Scribe_Values.Look(ref destinationTile, "destinationTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref strength, "strength");
            Scribe_Values.Look(ref silver, "silver");
            Scribe_Defs.Look(ref deferredDef, "deferredDef");
            Scribe_Deep.Look(ref deferredParms, "deferredParms");
            Scribe_Values.Look(ref contextId, "contextId", -1);
            Scribe_Values.Look(ref strengthPaid, "strengthPaid");
            Scribe_Values.Look(ref speedFactor, "speedFactor", 1f);
            Scribe_Values.Look(ref originName, "originName");
            Scribe_Values.Look(ref departedTicks, "departed", -1);
            Scribe_Values.Look(ref targetFactionId, "targetFaction", -1);
            Scribe_Values.Look(ref pay, "pay");
            Scribe_Values.Look(ref secondFactionId, "secondFaction", -1);
            Scribe_Values.Look(ref mission, "mission");
            Scribe_Values.Look(ref population, "population");
            Scribe_Values.Look(ref prevTile, "prevTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref nextTile, "nextTile", PlanetTile.Invalid);
            Scribe_Values.Look(ref costLeft, "costLeft");
            Scribe_Values.Look(ref costTotal, "costTotal", 1f);
            CommodityScribe.Look(ref cargo, "cargo");
            Scribe_Deep.Look(ref stock, "stock", this);
            Scribe_Defs.Look(ref traderKind, "traderKind");
            Scribe_Values.Look(ref waitedTicks, "waitedTicks");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                needsPath = true;
                schemaVersion = Schema;
                if (kind == null) kind = LWPartyDefOf.LW_Merchants;
            }
        }
    }

    /// <summary>Saves per-commodity arrays by defName, so commodity defs can change without breaking saves.</summary>
    public static class CommodityScribe
    {
        public static void Look(ref float[] values, string label)
        {
            Dictionary<string, float> byName = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                byName = new Dictionary<string, float>();
                if (values != null)
                    foreach (CommodityDef c in Commodities.All)
                        if (c.index < values.Length && values[c.index] != 0f) byName[c.defName] = values[c.index];
            }
            Scribe_Collections.Look(ref byName, label, LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                values = new float[Commodities.Count];
                if (byName != null)
                    foreach (CommodityDef c in Commodities.All)
                        if (byName.TryGetValue(c.defName, out float v)) values[c.index] = v;
            }
        }
    }

    /// <summary>
    /// A colonist away with a party of the colony (a mercenary contract) keeps the game going, like a colonist lent to a
    /// quest: vanilla only counts maps, caravans and transporters.
    /// </summary>
    [HarmonyPatch(typeof(GameEnder), nameof(GameEnder.CheckOrUpdateGameOver))]
    public static class Patch_GameEnder_CheckOrUpdateGameOver
    {
        public static void Postfix(GameEnder __instance)
        {
            if (!__instance.gameEnding) return;
            foreach (WorldParty p in WorldParty.All)
                if (p.Faction == Faction.OfPlayer && p.Passengers.Any(c => !c.Dead && c.IsFreeColonist))
                {
                    __instance.gameEnding = false;
                    return;
                }
        }
    }
}
