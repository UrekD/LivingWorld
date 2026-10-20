using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    public enum FigureRole : byte { Merchant, Warlord, Traveller, Freed, Kin }

    public enum FigureStatus : byte { Active, Dead, Captured, Joined, Retired }

    /// <summary>
    /// A notable person of the world: a real world pawn the simulation keeps alive and brings back (the merchant who
    /// trades with you every season, the warlord who keeps raiding you). Name and deeds are snapshotted.
    /// </summary>
    public class Figure : IExposable
    {
        public int id;
        public Pawn pawn;
        public FigureRole role;
        public FigureStatus status;
        public int factionId = -1;
        public int homeId = -1;
        public string homeName;
        public string name;
        public int sinceTicks;
        public int visits;
        public float tradedSilver;
        public int kills;
        public List<int> deeds = new List<int>();
        /// <summary>Colonists (thingIDNumber) this figure has dealt with: traded with, told news to, greeted.</summary>
        public List<int> friends = new List<int>();
        /// <summary>An open order the colony agreed to fill (Commissions): what, how many, for how much, due when.</summary>
        public ThingDef commissionDef;
        /// <summary>Kin figures: the colonist (thingIDNumber) whose parent this is.</summary>
        public int kinOfId = -1;
        /// <summary>Avengers (Feuds): the colonist they are sworn to kill (null when none or done), whom they avenge.</summary>
        public Pawn nemesis;
        public string nemesisName, avenges;
        public bool heirOfFallen;
        public int swornTick, feudReturns;
        /// <summary>When the avenger's people last downed the nemesis (-1: not now): dying of it counts as their revenge.</summary>
        public int nemesisDownedTick = -1;
        /// <summary>The colonist who last downed this figure (credited if they die of it later).</summary>
        public Pawn downedBy;

        public bool FeudOpen => nemesis != null && avenges != null;
        /// <summary>Careers: the rise to merchant prince / dread warlord was told; the figure this one took over from.</summary>
        public bool renownTold;
        public int successorOf = -1;
        public int commissionCount, commissionPrice, commissionDue, commissionSummoned = FactionState.Never;

        public void ClearCommission()
        {
            commissionDef = null;
            commissionCount = commissionPrice = 0;
        }

        public bool Alive => status == FigureStatus.Active && pawn != null && !pawn.Dead && !pawn.Discarded;

        /// <summary>Free to appear somewhere: alive, off-map, still with its faction.</summary>
        public bool Available => Alive && !pawn.Spawned && pawn.Faction != null && pawn.Faction.loadID == factionId && Find.WorldPawns.Contains(pawn)
                                 && Find.WorldPawns.GetSituation(pawn) == WorldPawnSituation.Free;

        public string RoleLabel => ("LW_FigureRole_" + role + (Renowned ? "_Renowned" : "")).Translate(homeName ?? "?");

        /// <summary>A career made: a merchant prince (5+ visits), a dread warlord (3+ raids or 5+ kills). See Careers.</summary>
        public bool Renowned => role == FigureRole.Merchant ? visits >= Careers.PrinceVisits : role == FigureRole.Warlord && (visits >= Careers.DreadRaids || kills >= Careers.DreadKills);

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref role, "role");
            Scribe_Values.Look(ref status, "status");
            Scribe_Values.Look(ref factionId, "faction", -1);
            Scribe_Values.Look(ref homeId, "home", -1);
            Scribe_Values.Look(ref homeName, "homeName");
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref sinceTicks, "since");
            Scribe_Values.Look(ref visits, "visits");
            Scribe_Values.Look(ref tradedSilver, "traded");
            Scribe_Values.Look(ref kills, "kills");
            Scribe_Collections.Look(ref deeds, "deeds", LookMode.Value);
            Scribe_Collections.Look(ref friends, "friends", LookMode.Value);
            Scribe_Defs.Look(ref commissionDef, "commissionDef");
            Scribe_Values.Look(ref kinOfId, "kinOf", -1);
            Scribe_References.Look(ref nemesis, "nemesis");
            Scribe_References.Look(ref downedBy, "downedBy");
            Scribe_Values.Look(ref swornTick, "swornTick");
            Scribe_Values.Look(ref feudReturns, "feudReturns");
            Scribe_Values.Look(ref nemesisDownedTick, "nemesisDowned", -1);
            Scribe_Values.Look(ref renownTold, "renownTold");
            Scribe_Values.Look(ref successorOf, "successorOf", -1);
            Scribe_Values.Look(ref nemesisName, "nemesisName");
            Scribe_Values.Look(ref avenges, "avenges");
            Scribe_Values.Look(ref heirOfFallen, "heirOfFallen");
            Scribe_Values.Look(ref commissionCount, "commissionCount");
            Scribe_Values.Look(ref commissionPrice, "commissionPrice");
            Scribe_Values.Look(ref commissionDue, "commissionDue");
            Scribe_Values.Look(ref commissionSummoned, "commissionSummoned", FactionState.Never);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                deeds ??= new List<int>();
                friends ??= new List<int>();
            }
        }
    }

    /// <summary>Creates, finds and brings back figures; reacts to their fate.</summary>
    public static class Figures
    {
        public const int MaxFigures = 60;
        /// <summary>A figure pruning must leave alone for now (a retiree while their heir is registered).</summary>
        internal static int PruneProtectId = -1;
        private static readonly HashSet<Pawn> keep = new HashSet<Pawn>();
        private static int keepBuiltTick = -1;

        private static List<Figure> List => LivingWorldComponent.Instance.figures;

        public static void ResetCache()
        {
            keep.Clear();
            keepBuiltTick = -1;
        }

        public static Figure ById(int id)
        {
            foreach (Figure f in LivingWorldComponent.Instance.figures)
                if (f.id == id) return f;
            return null;
        }

        public static Figure Of(Pawn p)
        {
            if (p == null || LivingWorldComponent.Instance == null) return null;
            foreach (Figure f in List)
                if (f.pawn == p) return f;
            return null;
        }

        public static Figure FindFor(int homeId, FigureRole role)
        {
            foreach (Figure f in List)
                if (f.homeId == homeId && f.role == role && f.Alive) return f;
            return null;
        }

        /// <summary>For the world pawn GC: pawns the simulation still needs.</summary>
        public static bool ShouldKeep(Pawn p)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null) return false;
            int now = Verse.Find.TickManager.TicksGame;
            if (keepBuiltTick != now)
            {
                keep.Clear();
                foreach (Figure f in comp.figures)
                    if (f.Alive || f.status == FigureStatus.Retired && f.pawn != null && !f.pawn.Dead && !f.pawn.Destroyed && !f.pawn.Discarded) keep.Add(f.pawn); // retired: their heir's parent
                keepBuiltTick = now;
            }
            return keep.Contains(p);
        }

        public static Figure Register(Pawn p, FigureRole role, Settlement home)
        {
            if (!LivingWorldMod.Settings.figures || p == null || Of(p) != null || home == null) return null;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp.figures.Count >= MaxFigures) PruneOld(comp);
            var f = new Figure
            {
                id = comp.NextId(),
                pawn = p,
                role = role,
                factionId = p.Faction?.loadID ?? -1,
                homeId = home.ID,
                homeName = home.Label,
                name = p.Name?.ToStringFull ?? p.LabelShort,
                sinceTicks = SimClock.Now,
            };
            comp.figures.Add(f);
            keepBuiltTick = -1;
            return f;
        }

        private static void PruneOld(LivingWorldComponent comp)
        {
            // Forget the dead first, then the least remarkable.
            // (A retiree whose heir carries on stays: they are the heir's parent.)
            comp.figures.RemoveAll(f => f.status != FigureStatus.Active && f.deeds.Count < 2 && f.id != PruneProtectId && !HasLivingSuccessor(comp, f));
            while (comp.figures.Count >= MaxFigures)
            {
                Figure least = null;
                foreach (Figure f in comp.figures)
                {
                    if (f.Alive && (f.FeudOpen || f.role == FigureRole.Kin)) continue; // a sworn avenger or a colonist's parent stays
                    if (f.id == PruneProtectId || f.status == FigureStatus.Retired && HasLivingSuccessor(comp, f)) continue;
                    if (least == null || f.visits + f.deeds.Count < least.visits + least.deeds.Count) least = f;
                }
                if (least == null) break;
                comp.figures.Remove(least);
            }
        }

        private static bool HasLivingSuccessor(LivingWorldComponent comp, Figure f)
        {
            foreach (Figure o in comp.figures)
                if (o.successorOf == f.id && o.Alive) return true;
            return false;
        }

        public static void AddDeed(Figure f, ChronicleEntry e)
        {
            if (f == null || e == null) return;
            f.deeds.Add(e.id);
            e.figures ??= new List<int>();
            e.figures.Add(f.id);
        }

        /// <summary>A group from a settlement just arrived on the player's map: bring back its known people, or remember new ones.</summary>
        public static void Notify_GroupArrived(Lord lord, IncidentContext ctx)
        {
            if (!LivingWorldMod.Settings.figures || ctx.originId < 0) return;
            if (LivingWorldComponent.WorldObjectById(ctx.originId) is not Settlement home) return;
            if (ctx.kind == ContextKind.Trade) MerchantArrived(lord, home);
            else if (ctx.kind == ContextKind.Raid) WarbandArrived(lord, home);
            // Hospitality sets up its guests itself: only vanilla visitor groups get recurring travellers.
            else if (ctx.kind == ContextKind.Visit && lord.LordJob is LordJob_VisitColony && !RulerVisits(lord, home) && !FreedReturns(lord, home) && !Kin.Visits(lord, home)) TravellerArrived(lord, home);
        }

        private static void MerchantArrived(Lord lord, Settlement home)
        {
            Pawn trader = TraderCaravanUtility.FindTrader(lord);
            if (trader == null) return;
            Figure f = FindFor(home.ID, FigureRole.Merchant);
            if (f == null)
            {
                f = Register(trader, FigureRole.Merchant, home);
                if (f != null)
                {
                    f.visits = 1;
                    Commissions.Notify_MerchantOnMap(f, home, lord.Map);
                }
                return;
            }
            if (!f.Available || !Swap(lord, trader, f.pawn, asTrader: true)) return;
            f.visits++;
            Careers.Notify_Led(f);
            Messages.Message("LW_MerchantReturns".Translate(f.pawn.Named("PAWN"), home.Label.Named("HOME"), f.visits.Named("VISITS")), f.pawn, MessageTypeDefOf.PositiveEvent);
            Commissions.Notify_MerchantOnMap(f, home, lord.Map);
        }

        private static void WarbandArrived(Lord lord, Settlement home)
        {
            Figure f = FindFor(home.ID, FigureRole.Warlord);
            Pawn strongest = null;
            foreach (Pawn p in lord.ownedPawns)
                if (p.RaceProps.Humanlike && (strongest == null || p.kindDef.combatPower > strongest.kindDef.combatPower)) strongest = p;
            if (strongest == null) return;
            if (f == null)
            {
                f = Register(strongest, FigureRole.Warlord, home);
                if (f != null) f.visits = 1;
                return;
            }
            if (!f.Available || !Swap(lord, strongest, f.pawn, asTrader: false)) return;
            f.visits++;
            Careers.Notify_Led(f);
            Feuds.Notify_AvengerArrived(f, lord);
            // The war band letter already named them: a message is enough next to the raid letter.
            Messages.Message("LW_WarlordReturns".Translate(f.pawn.Named("PAWN"), home.Label.Named("HOME"), f.visits.Named("VISITS")), f.pawn, MessageTypeDefOf.ThreatSmall);
        }

        /// <summary>
        /// A friendly faction's ruler may come along with a visit (high goodwill, now and then): the vanilla leader pawn
        /// takes a generated visitor's place. Nothing else changes; if the colony harms them, vanilla handles a dead or
        /// captured leader.
        /// </summary>
        public static int StatRulerVisits;
        /// <summary>Tests: the next possible ruler visit happens (no chance roll).</summary>
        public static bool TestForceRulerVisit;
        public static bool TestRulerVisitEvaluated;

        private static bool RulerVisits(Lord lord, Settlement home)
        {
            Faction f = lord.faction;
            Pawn ruler = f?.leader;
            string refused = ruler == null ? "no leader" : ruler.Dead ? "leader dead" : ruler.Spawned ? "leader on a map" : !f.HasGoodwill ? "no goodwill"
                : f.PlayerGoodwill < 60 ? $"goodwill {f.PlayerGoodwill}" : !Find.WorldPawns.Contains(ruler) ? "leader not a world pawn"
                : Find.QuestManager.IsReservedByAnyQuest(ruler) ? "leader reserved by a quest" : null;
            FactionState fs = LivingWorldComponent.Instance.StateFor(f);
            if (refused == null && fs != null && Find.TickManager.TicksGame - fs.lastRulerVisitTick < GenDate.TicksPerYear) refused = "visited this year";
            if (refused == null && home != SettlementOps.CapitalOf(f) && Neighbourhood.Home is Map m
                && Find.WorldGrid.ApproxDistanceInTiles(m.Tile, home.Tile) > Neighbourhood.Radius(m.Tile, Neighbours.Range)) refused = "too far from the capital and the colony";
            if (TestForceRulerVisit && Testing.AutoTest.Active)
            {
                // The test only expects a visit when the rules allow one.
                if (refused == null) TestRulerVisitEvaluated = true;
                LWLog.Message($"[figures] ruler visit for {f?.Name}: {refused ?? "possible"}");
            }
            if (refused != null || !(TestForceRulerVisit || Rand.Chance(0.3f))) return false;
            Pawn stand = null;
            foreach (Pawn p in lord.ownedPawns)
                if (p.RaceProps.Humanlike && p.Spawned && !p.mindState.wantsToTradeWithColony // not the group's small trader
                    && (stand == null || p.ageTracker.AgeBiologicalYears > stand.ageTracker.AgeBiologicalYears)) stand = p;
            if (stand == null || !Swap(lord, stand, ruler, asTrader: false)) return false;
            StatRulerVisits++;
            if (fs != null) fs.lastRulerVisitTick = Find.TickManager.TicksGame;
            RulerAudience.Open(ruler, f, home); // the visit comes with a proposal shaped by the ruler's goal
            ChronicleEventDef def = LWChronicleDefOf.LW_RulerVisited;
            LivingWorldComponent.Instance.chronicle.Record(def, def.text.Formatted(ruler.Name.ToStringFull.Named("NAME"), f.LeaderTitle.Named("TITLE"), f.Name.Named("FACTION"),
                Faction.OfPlayer.Name.Named("COLONY")).Resolve(), f, Faction.OfPlayer, silent: true);
            return true;
        }

        public static int StatFreedReturns;
        /// <summary>Tests: a freed captive always comes along when possible.</summary>
        public static bool TestForceFreed;

        /// <summary>A captive the colony set free becomes a figure of their town, remembering the colonists they liked.</summary>
        public static Figure RegisterFreed(Pawn p, Settlement home, Map map)
        {
            Figure f = Register(p, FigureRole.Freed, home);
            if (f == null) return null;
            f.visits = 1;
            if (map != null && p.relations != null)
                foreach (Pawn c in map.mapPawns.FreeColonists)
                    if (p.relations.OpinionOf(c) >= 10) Befriend(f, c);
            return f;
        }

        /// <summary>The freed captive comes along with a visit from their town, with a small gift for the colony.</summary>
        private static bool FreedReturns(Lord lord, Settlement home)
        {
            Figure f = FindFor(home.ID, FigureRole.Freed);
            if (f == null || !f.Available || !(TestForceFreed || Rand.Chance(0.5f))) return false;
            Pawn stand = null;
            foreach (Pawn p in lord.ownedPawns)
                if (p.RaceProps.Humanlike && p.Spawned && !p.mindState.wantsToTradeWithColony && (stand == null || p.ageTracker.AgeBiologicalYears < stand.ageTracker.AgeBiologicalYears)) stand = p;
            if (stand == null || !Swap(lord, stand, f.pawn, asTrader: false)) return false;
            f.visits++;
            StatFreedReturns++;
            int gift = Mathf.RoundToInt(Rand.Range(60f, 160f) / 10f) * 10;
            Thing silver = ThingMaker.MakeThing(ThingDefOf.Silver);
            silver.stackCount = gift;
            GenPlace.TryPlaceThing(silver, f.pawn.Position, lord.Map, ThingPlaceMode.Near);
            Find.LetterStack.ReceiveLetter("LW_FreedReturnsLabel".Translate(f.pawn.LabelShort), "LW_FreedReturnsText".Translate(f.pawn.Named("PAWN"), home.Label.Named("HOME"), gift.Named("SILVER")),
                LetterDefOf.PositiveEvent, f.pawn, f.pawn.Faction);
            return true;
        }

        private static void TravellerArrived(Lord lord, Settlement home)
        {
            Pawn guest = null;
            foreach (Pawn p in lord.ownedPawns)
                if (p.RaceProps.Humanlike && p.Spawned && !p.mindState.wantsToTradeWithColony // not the group's small trader
                    && (guest == null || p.ageTracker.AgeBiologicalYears > guest.ageTracker.AgeBiologicalYears)) guest = p;
            if (guest == null) return;
            Figure f = FindFor(home.ID, FigureRole.Traveller);
            if (f == null)
            {
                f = Register(guest, FigureRole.Traveller, home);
                if (f != null) f.visits = 1;
                return;
            }
            if (!f.Available || !Swap(lord, guest, f.pawn, asTrader: false)) return;
            f.visits++;
            Messages.Message("LW_TravellerReturns".Translate(f.pawn.Named("PAWN"), home.Label.Named("HOME"), f.visits.Named("VISITS")), f.pawn, MessageTypeDefOf.PositiveEvent);
        }

        /// <summary>Puts a world pawn in the place of a freshly generated one (same spot, same lord, same goods).</summary>
        internal static bool Swap(Lord lord, Pawn generated, Pawn figure, bool asTrader)
        {
            Map map = lord.Map;
            if (map == null || !generated.Spawned || figure.Faction != generated.Faction || !lord.CanAddPawn(figure)) return false;
            IntVec3 cell = generated.Position;
            // Free whatever the generated pawn had claimed (it may have started a job already).
            generated.jobs?.StopAll();
            map.reservationManager.ReleaseAllClaimedBy(generated);
            if (Find.WorldPawns.Contains(figure)) Find.WorldPawns.RemovePawn(figure);
            GenSpawn.Spawn(figure, cell, map);
            figure.jobs?.StopAll();
            figure.jobs?.ClearQueuedJobs();
            lord.AddPawn(figure);
            if (figure.GetLord() != lord)
            {
                // The lord refused (toil can't take pawns now): put the figure back, keep the generated pawn.
                figure.DeSpawn();
                Find.WorldPawns.PassToWorld(figure);
                return false;
            }
            if (asTrader)
            {
                // The merchant takes over the generated trader's goods and trade; a warlord keeps their own kit.
                if (figure.inventory != null && generated.inventory != null)
                {
                    figure.inventory.innerContainer.ClearAndDestroyContents();
                    generated.inventory.innerContainer.TryTransferAllToContainer(figure.inventory.innerContainer);
                }
                figure.mindState.wantsToTradeWithColony = true;
                PawnComponentsUtility.AddAndRemoveDynamicComponents(figure, actAsIfSpawned: true);
                if (figure.trader != null) figure.trader.traderKind = generated.trader?.traderKind;
            }
            // Animals the generated pawn was leading on a rope follow the figure instead (their jobs pointed at the old pawn).
            if (generated.roping != null && figure.roping != null)
                foreach (Pawn ropee in new List<Pawn>(generated.roping.Ropees))
                {
                    // Drop first: RopePawn does not take the animal off the old holder's list, and destroying the
                    // generated pawn would then cut the new rope while the figure still counts the animal as led.
                    generated.roping.DropRope(ropee);
                    figure.roping.RopePawn(ropee);
                    ropee.jobs?.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
                }
            lord.RemovePawn(generated);
            lord.numPawnsEverGained--; // AddPawn counted the figure; the generated pawn it replaced must not count too (flee threshold)
            // A placeholder the generator made is destroyed; a pawn someone knows (a figure, a former colonist) goes back to the world.
            if (ShouldKeep(generated) || Of(generated) != null || PawnUtility.EverBeenColonistOrTameAnimal(generated))
            {
                generated.DeSpawn();
                if (!Find.WorldPawns.Contains(generated)) Find.WorldPawns.PassToWorld(generated);
            }
            else generated.Destroy();
            // Duties (animals following a caravan member, guards around the trader) may point at the pawn just replaced.
            lord.CurLordToil?.UpdateAllDuties();
            return true;
        }

        /// <summary>A figure died (anywhere); a colonist's death may settle a feud.</summary>
        public static void Notify_Died(Pawn p, DamageInfo? dinfo, Hediff exactCulprit = null)
        {
            if (LivingWorldComponent.Instance == null) return;
            if (p.Faction == Faction.OfPlayer && p.RaceProps.Humanlike)
            {
                Feuds.Notify_ColonistDied(LivingWorldComponent.Instance, p, dinfo, exactCulprit);
                // A figure's kill: a warlord who takes enough colonists' lives becomes dread.
                if (dinfo?.Instigator is Pawn killer && Of(killer) is Figure kf && kf.Alive)
                {
                    kf.kills++;
                    Careers.TellRenown(kf);
                }
            }
            Figure f = Of(p);
            if (f == null || f.status != FigureStatus.Active) return;
            f.status = FigureStatus.Dead;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Faction faction = comp.FactionById(f.factionId);
            // The colony's doing: its blow, its home, or a colonist downed them and they died of it (anywhere: a caravan's fight too).
            Pawn slayer = Feuds.SlayerOf(f, dinfo);
            bool byPlayer = dinfo?.Instigator?.Faction == Faction.OfPlayer || (p.MapHeld != null && p.MapHeld.IsPlayerHome) || slayer != null;
            ChronicleEventDef def = f.role == FigureRole.Warlord ? LWChronicleDefOf.LW_WarlordFell : LWChronicleDefOf.LW_FigureDied;
            string where = p.MapHeld?.Parent?.Label ?? "LW_Figure_FarAway".Translate().Resolve();
            ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(f.name.Named("NAME"), f.RoleLabel.Named("ROLE"), where.Named("PLACE"),
                (faction?.Name ?? "?").Named("FACTION")).Resolve(), faction, byPlayer ? Faction.OfPlayer : null, LivingWorldComponent.WorldObjectById(f.homeId));
            AddDeed(f, e);
            if (slayer != null)
                Legends.Add(slayer, "LW_Deed_SlewFigure".Translate(f.name, f.RoleLabel).Resolve());
            if (f.role == FigureRole.Warlord && byPlayer)
            {
                // The home town is weakened and its people swear revenge.
                if (LivingWorldComponent.WorldObjectById(f.homeId) is Settlement home && home.GetComponent<CompSettlementLife>() is { initialized: true } life)
                    life.garrison *= 0.85f;
                FactionState fs = comp.StateFor(faction);
                Diplomacy.AddGrudge(fs, Faction.OfPlayer, 25f, "LW_Grudge_KilledWarlord".Translate(f.name).Resolve());
                // The feud first: slaying a sworn avenger closes it and calls off its return raid, which would block this one.
                Feuds.Notify_WarlordSlain(comp, f, slayer, LivingWorldComponent.WorldObjectById(f.homeId) as Settlement);
                Revenge.Schedule(faction, f.homeId, e.id);
            }
            else if (f.FeudOpen) Feuds.Close(comp, f, null); // an avenger dead by other hands: the oath dies with them
        }

        /// <summary>A figure became the player's prisoner or joined the player.</summary>
        public static void Notify_FactionOrGuestChanged(Pawn p)
        {
            Figure f = Of(p);
            if (f == null) return;
            if (f.status == FigureStatus.Captured && p.Faction != null && p.Faction.loadID == f.factionId && !p.IsPrisoner)
            {
                f.status = FigureStatus.Active; // released or escaped: back to their people
                return;
            }
            if (f.status != FigureStatus.Active) return;
            if (p.Faction == Faction.OfPlayer) f.status = FigureStatus.Joined;
            else if (p.IsPrisonerOfColony) f.status = FigureStatus.Captured;
            else return;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            ChronicleEventDef def = f.status == FigureStatus.Joined ? LWChronicleDefOf.LW_FigureJoined : LWChronicleDefOf.LW_FigureCaptured;
            ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(f.name.Named("NAME"), f.RoleLabel.Named("ROLE")).Resolve(),
                comp.FactionById(f.factionId), Faction.OfPlayer);
            AddDeed(f, e);
            // The sworn avenger in the colony's hands: the feud is broken.
            if (f.FeudOpen) Feuds.Close(comp, f, LWChronicleDefOf.LW_FeudBroken);
        }

        public static void Notify_Traded(float silver)
        {
            if (TradeSession.trader is not Pawn p || Of(p) is not Figure f) return;
            f.tradedSilver += silver;
            if (TradeSession.playerNegotiator is Pawn n) Befriend(f, n);
        }

        /// <summary>The figure now knows this colonist (they will greet them on a later visit).</summary>
        public static void Befriend(Figure f, Pawn colonist)
        {
            if (f == null || colonist == null || f.friends.Contains(colonist.thingIDNumber)) return;
            f.friends.Add(colonist.thingIDNumber);
            if (f.friends.Count > 8) f.friends.RemoveAt(0);
        }
    }

    [HarmonyPatch(typeof(WorldPawnGC), "GetCriticalPawnReason")]
    public static class Patch_WorldPawnGC_GetCriticalPawnReason
    {
        public static void Postfix(Pawn pawn, ref string __result)
        {
            if (__result == null && Figures.ShouldKeep(pawn)) __result = "LivingWorldFigure";
        }
    }

    /// <summary>Vanilla must not reuse a figure (active or retired) as a random pawn of some group: the mod brings them back itself.</summary>
    [HarmonyPatch(typeof(PawnGenerator), "IsValidCandidateToRedress")]
    public static class Patch_PawnGenerator_IsValidCandidateToRedress_Figures
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (__result && LivingWorldComponent.Instance != null && Figures.ShouldKeep(pawn)) __result = false;
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Pawn_Kill_Figures
    {
        public static void Prefix(Pawn __instance)
        {
            try
            {
                Prisoners.Notify_Dying(__instance);
            }
            catch (System.Exception e)
            {
                LWLog.Error("prisoner death: " + e);
            }
        }

        public static void Postfix(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit)
        {
            if (!__instance.Dead) return;
            try
            {
                Figures.Notify_Died(__instance, dinfo, exactCulprit);
            }
            catch (System.Exception e)
            {
                LWLog.Error("figure death: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_GuestTracker), nameof(Pawn_GuestTracker.SetGuestStatus))]
    public static class Patch_GuestTracker_SetGuestStatus_Figures
    {
        public static void Postfix(Pawn ___pawn)
        {
            if (___pawn != null && LivingWorldComponent.Instance != null) Figures.Notify_FactionOrGuestChanged(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
    public static class Patch_Pawn_SetFaction_Figures
    {
        public static void Postfix(Pawn __instance)
        {
            if (LivingWorldComponent.Instance != null && Current.ProgramState == ProgramState.Playing) Figures.Notify_FactionOrGuestChanged(__instance);
        }
    }
}
