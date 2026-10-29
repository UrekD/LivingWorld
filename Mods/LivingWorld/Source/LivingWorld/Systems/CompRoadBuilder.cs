using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    public enum RoadPhase : byte { None, Working, Travelling, WaitingForMaterials }

    /// <summary>
    /// A player caravan building a road: the colonists work on one world tile at a time while the caravan stands still, pay the
    /// materials, lay the link and walk on. The job is saved in this comp's own namespaced fields (<c>lw_road*</c>), so nothing about
    /// it is in the game's movement state: the caravan moves with ordinary vanilla pathing, and a move this comp did not start cancels the job.
    /// Injected in code into the Caravan world object def (see <see cref="RoadCompInjector"/>), so caravans of old saves have it too.
    /// </summary>
    public class CompRoadBuilder : WorldObjectComp
    {
        /// <summary>The comp works in steps of this many ticks (the world object ticks in intervals of its own).</summary>
        private const int Interval = 250;

        private List<int> tiles;
        private string defName;
        private int index;
        private float work;
        private RoadPhase phase;
        private int jobId;
        /// <summary>Links this job changed (only those earn XP, a chronicle entry and a deed).</summary>
        private int changed;
        private int retries;
        /// <summary>The caravan is walking to the next tile because this job sent it (not because anyone else ordered it).</summary>
        private bool ownedMove;
        /// <summary>Links this job made where there was no road at all: only these can newly join a town to the colony.</summary>
        private List<long> newEdges;
        [Unsaved] private int accumulated;
        /// <summary>Tests: a job held in its working phase (no progress), so a live job can be kept for the halfway save.</summary>
        internal bool TestHold { get; set; }
        /// <summary>True while this comp is itself calling <c>StartPath</c> (see <see cref="Patch_PathFollower_RoadStartPath"/>).</summary>
        internal static bool InternalStart;

        private static readonly List<Pawn> workersTmp = new List<Pawn>();
        private static readonly List<Thing> itemsTmp = new List<Thing>();
        private static Texture2D icon;

        public Caravan Caravan => parent as Caravan;

        public bool HasJob => phase != RoadPhase.None;

        public RoadPhase Phase => phase;

        public int JobIndex => index;

        public int JobEdges => tiles == null ? 0 : Mathf.Max(0, tiles.Count - 1);

        public int JobChanged => changed;

        public int JobId => jobId;

        public IReadOnlyList<int> JobTiles => tiles;

        public RoadDef JobDef => defName == null ? null : DefDatabase<RoadDef>.GetNamedSilentFail(defName);

        public float JobWork => work;

        public int JobRetries => retries;

        public bool JobOwnsMove => ownedMove;

        public IReadOnlyList<long> JobNewEdges => newEdges;

        public override void PostExposeData()
        {
            Scribe_Collections.Look(ref tiles, "lw_roadTiles", LookMode.Value);
            Scribe_Values.Look(ref defName, "lw_roadDef");
            Scribe_Values.Look(ref index, "lw_roadIndex");
            Scribe_Values.Look(ref work, "lw_roadWork");
            Scribe_Values.Look(ref phase, "lw_roadPhase", RoadPhase.None);
            Scribe_Values.Look(ref jobId, "lw_roadJob");
            Scribe_Values.Look(ref changed, "lw_roadChanged");
            Scribe_Values.Look(ref retries, "lw_roadRetries");
            Scribe_Values.Look(ref ownedMove, "lw_roadOwned");
            Scribe_Collections.Look(ref newEdges, "lw_roadNew", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && phase != RoadPhase.None && (tiles == null || defName == null)) ClearJob();
        }

        // ---- Workers and materials ----

        /// <summary>Colonists who can work now: alive, up, in their right mind, able to do construction. Fills <paramref name="into"/>; returns their summed speed.</summary>
        public static float Workers(Caravan c, List<Pawn> into)
        {
            into.Clear();
            float speed = 0f;
            foreach (Pawn p in c.PawnsListForReading)
            {
                if (p.Dead || p.Downed || p.InMentalState || !p.IsColonist || p.skills == null || p.WorkTypeIsDisabled(WorkTypeDefOf.Construction)) continue;
                into.Add(p);
                speed += Mathf.Max(0f, p.GetStatValue(StatDefOf.ConstructionSpeed));
            }
            return speed;
        }

        /// <summary>Stone blocks across everything the caravan's pawns carry.</summary>
        public static int StoneBlocksOwned(Caravan c)
        {
            itemsTmp.Clear();
            itemsTmp.AddRange(CaravanInventoryUtility.AllInventoryItems(c));
            int n = 0;
            foreach (Thing t in itemsTmp)
                if (t.def.IsWithinCategory(ThingCategoryDefOf.StoneBlocks)) n += t.stackCount;
            return n;
        }

        private static void ConsumeStoneBlocks(Caravan c, int count)
        {
            itemsTmp.Clear();
            itemsTmp.AddRange(CaravanInventoryUtility.AllInventoryItems(c));
            foreach (Thing t in itemsTmp)
            {
                if (count <= 0) break;
                if (!t.def.IsWithinCategory(ThingCategoryDefOf.StoneBlocks)) continue;
                int n = Mathf.Min(count, t.stackCount);
                t.holdingOwner.Take(t, n).Destroy();
                count -= n;
            }
            c.RecacheInventory();
        }

        // ---- Gizmos ----

        public override IEnumerable<Gizmo> GetGizmos()
        {
            Caravan c = Caravan;
            // Only the plain caravan: a subclass (a vehicle caravan, say) may move with a pather of its own that the job does not drive.
            if (!Roads.Active || c == null || parent.GetType() != typeof(Caravan) || !c.Spawned || !c.IsPlayerControlled || Find.WorldSelector.SingleSelectedObject != c
                || Roads.SurfaceId(Roads.Surface(), c.Tile) < 0) yield break;
            if (HasJob)
            {
                yield return new Command_Action
                {
                    defaultLabel = "LW_Roads_Stop".Translate(),
                    defaultDesc = "LW_Roads_StopDesc".Translate(),
                    icon = TexCommand.ClearPrioritizedWork,
                    action = () => Cancel(stopOwnMovement: true, "LW_Roads_Cancelled_Stopped"),
                };
                yield break;
            }
            var build = new Command_Action
            {
                defaultLabel = "LW_Roads_Build".Translate(),
                defaultDesc = "LW_Roads_BuildDesc".Translate(),
                icon = Icon,
                action = OpenMenu,
            };
            // The night's rest is no reason: the job can be queued and its work waits for the morning.
            string reason = CantMoveBesidesNight(c) ? "LW_Roads_CantMove".Translate() : c.pather.Moving ? "LW_Roads_Moving".Translate() : Workers(c, workersTmp) <= 0f ? "LW_Roads_NoWorkers".Translate() : null;
            if (reason != null) build.Disable(reason);
            yield return build;
        }

        /// <summary><see cref="Caravan.CantMove"/> for every cause but the night's rest.</summary>
        private static bool CantMoveBesidesNight(Caravan c) =>
            c.pather.Paused || c.AllOwnersHaveMentalBreak || c.AllOwnersDowned || c.ImmobilizedByMass || c.Shuttle != null;

        /// <summary>A keyed string with a "_One" twin for a count of exactly one; the arguments are the string's own ({0}, {1}, …).</summary>
        private static TaggedString Counted(string key, int count, params NamedArgument[] args) => (count == 1 ? key + "_One" : key).Translate(args);

        internal static Texture2D Icon => icon ??= ContentFinder<Texture2D>.Get("LivingWorld/UI/Roads", reportFailure: true) ?? BaseContent.BadTex;

        private void OpenMenu()
        {
            int perEdge = Tuning.T.roadStoneBlocksPerEdge, owned = Caravan == null ? 0 : StoneBlocksOwned(Caravan);
            var options = new List<FloatMenuOption>
            {
                new FloatMenuOption("LW_Roads_Dirt".Translate(), () => RoadTargeting.Begin(this, LWRoadDefOf.DirtRoad)),
                new FloatMenuOption(Counted("LW_Roads_Stone", perEdge, perEdge, owned), () => RoadTargeting.Begin(this, LWRoadDefOf.StoneRoad)),
            };
            Find.WindowStack.Add(new FloatMenu(options));
        }

        // ---- Choosing the target ----

        /// <summary>Cheap, side-effect-free: could this be a target at all (surface tile, not the caravan's own, not hopelessly far).</summary>
        internal bool CanTarget(PlanetTile start, GlobalTargetInfo target)
        {
            if (!Roads.Active || !StillAt(start)) return false;
            PlanetLayer surface = Roads.Surface();
            PlanetTile t = target.Tile;
            return Roads.SurfaceId(surface, t) >= 0 && t != start && Find.WorldGrid.ApproxDistanceInTiles(start, t) <= Tuning.T.roadPlayerMaxEdges + 2;
        }

        /// <summary>The caravan is still where the targeting began and free to start a job.</summary>
        internal bool StillAt(PlanetTile start)
        {
            Caravan c = Caravan;
            return c != null && !c.Destroyed && c.Spawned && c.IsPlayerControlled && c.Tile == start && !HasJob && !c.pather.Moving;
        }

        internal string HoverLabel(PlanetTile start, GlobalTargetInfo target)
        {
            if (!Roads.Active || !StillAt(start)) return null;
            PlanetTile t = target.Tile;
            if (Roads.SurfaceId(Roads.Surface(), t) < 0 || t == start) return null;
            int dist = Mathf.RoundToInt(Find.WorldGrid.ApproxDistanceInTiles(start, t));
            // A tile that cannot be chosen says why, rather than doing nothing silently.
            return CanTarget(start, target) ? Counted("LW_Roads_TargetLabel", dist, dist, Tuning.T.roadPlayerMaxEdges).Resolve() : RejectText(RouteReject.TooLong).Resolve();
        }

        /// <summary>
        /// The click: everything is checked again (the caravan may have moved, merged, lost its builders, or roads were switched off while
        /// targeting). Returns false to keep targeting after a miss the player can correct.
        /// </summary>
        internal bool OnTargetChosen(RoadDef def, PlanetTile start, GlobalTargetInfo target)
        {
            Caravan c = Caravan;
            if (!Roads.Active || !StillAt(start) || c.Shuttle != null || Workers(c, workersTmp) <= 0f)
            {
                Messages.Message("LW_Roads_Reject_Caravan".Translate(), MessageTypeDefOf.RejectInput, historical: false);
                return true;
            }
            var route = new List<int>();
            if (!Roads.PlanRoute(start, target.Tile, Tuning.T.roadPlayerMaxEdges, route, out RouteReject reject))
            {
                Messages.Message(RejectText(reject), MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            tiles = route;
            defName = def.defName;
            index = 0;
            work = 0f;
            phase = RoadPhase.Working;
            jobId = LivingWorldComponent.Instance.NextId();
            changed = 0;
            retries = 0;
            ownedMove = false;
            newEdges = new List<long>();
            accumulated = 0;
            Messages.Message(Counted("LW_Roads_Started", route.Count - 1, c.LabelCap, def.label, route.Count - 1), c, MessageTypeDefOf.TaskCompletion, historical: false);
            return true;
        }

        public static TaggedString RejectText(RouteReject reject) => reject == RouteReject.TooLong
            ? Counted("LW_Roads_Reject_TooLong", Tuning.T.roadPlayerMaxEdges, Tuning.T.roadPlayerMaxEdges)
            : ("LW_Roads_Reject_" + reject).Translate();

        // ---- The job ----

        public override void CompTickInterval(int delta)
        {
            if (!HasJob) return;
            accumulated += delta;
            if (accumulated < Interval) return;
            int d = accumulated;
            accumulated = 0;
            Step(d);
        }

        private void Step(int delta)
        {
            Caravan c = Caravan;
            if (!Roads.Active)
            {
                Cancel(stopOwnMovement: true, null);
                return;
            }
            PlanetLayer surface = Roads.Surface();
            RoadDef def = JobDef;
            if (c == null || !c.Spawned || !c.IsPlayerControlled || surface == null || def == null || tiles == null || index < 0 || index + 1 >= tiles.Count)
            {
                Cancel(stopOwnMovement: true, "LW_Roads_Cancelled_Invalid");
                return;
            }
            int here = Roads.SurfaceId(surface, c.Tile);
            if (phase == RoadPhase.Travelling) StepTravelling(c, surface, here);
            else StepWorking(c, def, here, delta);
        }

        private void StepWorking(Caravan c, RoadDef def, int here, int delta)
        {
            if (here != tiles[index])
            {
                Cancel(stopOwnMovement: false, "LW_Roads_Cancelled_Moved");
                return;
            }
            if (c.pather.Moving)
            {
                Cancel(stopOwnMovement: false, "LW_Roads_Cancelled_Ordered");
                return;
            }
            if (TestHold) return;
            // Another builder (or the world) may have made the link already: walk on without work, materials or XP.
            switch (Roads.Need(tiles[index], tiles[index + 1], def))
            {
                case EdgeNeed.Invalid:
                    Cancel(stopOwnMovement: false, "LW_Roads_Cancelled_Invalid");
                    return;
                case EdgeNeed.Satisfied:
                    BeginTravel(c);
                    return;
            }
            float speed = Workers(c, workersTmp);
            if (speed <= 0f || c.NightResting) return;
            float need = Tuning.T.roadWorkPerEdge;
            if (phase == RoadPhase.Working) work = Mathf.Min(need, work + speed * delta);
            if (work >= need) Commit(c, def);
        }

        /// <summary>The work on this link is done: with the materials at hand they are spent and the link laid, otherwise the work waits for them.</summary>
        private void Commit(Caravan c, RoadDef def)
        {
            int blocks = def == LWRoadDefOf.StoneRoad ? Tuning.T.roadStoneBlocksPerEdge : 0;
            if (blocks > 0 && StoneBlocksOwned(c) < blocks)
            {
                if (phase != RoadPhase.WaitingForMaterials)
                {
                    phase = RoadPhase.WaitingForMaterials;
                    Messages.Message(Counted("LW_Roads_Waiting", blocks, c.LabelCap, blocks), c, MessageTypeDefOf.NegativeEvent, historical: false);
                }
                return;
            }
            if (blocks > 0) ConsumeStoneBlocks(c, blocks);
            bool hadRoad = Roads.RoadBetween(tiles[index], tiles[index + 1]) != null;
            LayResult result = Roads.LaySegment(tiles[index], tiles[index + 1], def, -1, player: true);
            if (result == LayResult.Invalid)
            {
                Cancel(stopOwnMovement: false, "LW_Roads_Cancelled_Invalid");
                return;
            }
            if (result == LayResult.Changed)
            {
                changed++;
                if (!hadRoad) (newEdges ??= new List<long>()).Add(Roads.EdgeKey(tiles[index], tiles[index + 1]));
                float xp = Tuning.T.roadXpPerEdge / Mathf.Max(1, workersTmp.Count);
                foreach (Pawn p in workersTmp) p.skills.Learn(SkillDefOf.Construction, xp);
            }
            BeginTravel(c);
        }

        private void BeginTravel(Caravan c)
        {
            work = 0f;
            retries = 0;
            phase = RoadPhase.Travelling;
            TryStartPath(c);
        }

        /// <summary>The job's own walk to the next tile. Marked, so the order hook can tell it from anyone else's.</summary>
        private bool TryStartPath(Caravan c)
        {
            bool ok;
            InternalStart = true;
            try
            {
                ok = c.pather.StartPath(new PlanetTile(tiles[index + 1], Roads.Surface()), null, repathImmediately: true);
            }
            finally
            {
                InternalStart = false;
            }
            ownedMove = ok && c.pather.Moving;
            return ok;
        }

        /// <summary>The player (or any other code) ordered this caravan somewhere: the job ends at once, the new order is left alone.</summary>
        internal void Notify_ExternalOrder()
        {
            if (HasJob) Cancel(stopOwnMovement: false, "LW_Roads_Cancelled_Ordered");
        }

        /// <summary>Tests: finish the work on the current link now (materials and laying as usual), skipping the pace and the night's rest.</summary>
        internal bool Test_CompleteLinkNow()
        {
            Caravan c = Caravan;
            RoadDef def = JobDef;
            if (phase != RoadPhase.Working || c == null || def == null || tiles == null || index + 1 >= tiles.Count) return false;
            Workers(c, workersTmp);
            work = Tuning.T.roadWorkPerEdge;
            Commit(c, def);
            return true;
        }

        private void StepTravelling(Caravan c, PlanetLayer surface, int here)
        {
            int next = tiles[index + 1];
            if (here == next && !c.pather.Moving)
            {
                index++;
                retries = 0;
                ownedMove = false;
                if (index + 1 >= tiles.Count) Finish(c);
                else
                {
                    phase = RoadPhase.Working;
                    work = 0f;
                }
                return;
            }
            if (c.pather.Moving)
            {
                // Resting or paused still counts as moving. The job's own walk goes to the next tile with no arrival action; any other
                // order is the player's (the order hook normally cancelled the job already; this is the backstop).
                if (!ownedMove || c.pather.Destination != new PlanetTile(next, surface) || Patch_PathFollower_RoadHopArrived.ArrivalActionOf(c.pather) != null)
                    Cancel(stopOwnMovement: false, "LW_Roads_Cancelled_Ordered");
                return;
            }
            ownedMove = false;
            if (here != tiles[index])
            {
                Cancel(stopOwnMovement: false, "LW_Roads_Cancelled_Moved");
                return;
            }
            if (retries >= 1)
            {
                Cancel(stopOwnMovement: false, "LW_Roads_Cancelled_Failed");
                return;
            }
            retries++;
            TryStartPath(c);
        }

        private void Finish(Caravan c)
        {
            Messages.Message("LW_Roads_Finished".Translate(c.LabelCap), c, MessageTypeDefOf.TaskCompletion, historical: false);
            if (Testing.AutoTest.Active) Roads.FinishedJobs.Add(jobId);
            var made = newEdges == null ? new List<long>() : new List<long>(newEdges);
            Workers(c, workersTmp);
            Pawn leader = null;
            foreach (Pawn p in workersTmp)
                if (leader == null || p.skills.GetSkill(SkillDefOf.Construction).Level > leader.skills.GetSkill(SkillDefOf.Construction).Level) leader = p;
            ClearJob();
            Roads.Notify_PlayerRouteFinished(leader, made);
        }

        /// <summary>
        /// Ends the job, keeping what is built. <paramref name="stopOwnMovement"/> halts the caravan only while it is on the walk this job
        /// started (owned, to the next tile, with no arrival action): anyone else's order, also one to the same tile, is never touched.
        /// Ownership is checked here and now, not when the comp next ticks.
        /// </summary>
        public void Cancel(bool stopOwnMovement, string messageKey)
        {
            if (!HasJob) return;
            Caravan c = Caravan;
            if (stopOwnMovement && c != null && phase == RoadPhase.Travelling && ownedMove && tiles != null && index + 1 < tiles.Count && c.pather.Moving
                && Roads.Surface() is PlanetLayer surface && c.pather.Destination == new PlanetTile(tiles[index + 1], surface)
                && Patch_PathFollower_RoadHopArrived.ArrivalActionOf(c.pather) == null)
                c.pather.StopDead();
            if (Testing.AutoTest.Active) Roads.CancelledJobs[jobId] = messageKey ?? "silent";
            ClearJob();
            if (messageKey != null && c != null)
                Messages.Message(messageKey.Translate(c.LabelCap), c, messageKey is "LW_Roads_Cancelled_Stopped" or "LW_Roads_Cancelled_Ordered" ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.NegativeEvent, historical: false);
        }

        private void ClearJob()
        {
            tiles = null;
            defName = null;
            index = 0;
            work = 0f;
            phase = RoadPhase.None;
            jobId = 0;
            changed = 0;
            retries = 0;
            ownedMove = false;
            newEdges = null;
            accumulated = 0;
        }

        /// <summary>The caravan has just reached the tile this job was walking it to (vanilla's own arrival is about to run).</summary>
        internal bool OwnsArrivalAt(PlanetTile arrived) =>
            phase == RoadPhase.Travelling && ownedMove && tiles != null && index + 1 < tiles.Count && Roads.Surface() is PlanetLayer surface && arrived == new PlanetTile(tiles[index + 1], surface);

        /// <summary>The caravan left the world (entered a map, was merged into another, destroyed): the job goes with it.</summary>
        public override void PostPostRemove() => ClearJob();

        public override void PostDestroy() => ClearJob();

        public override string CompInspectStringExtra()
        {
            if (!HasJob) return null;
            string line = "LW_Roads_Inspect".Translate(index + 1, JobEdges, JobDef?.label ?? "?");
            Caravan c = Caravan;
            if (c == null) return line;
            // What the job is really doing: the wait for materials can last for ever, the work stops without workers and at night.
            string state = null;
            if (phase == RoadPhase.WaitingForMaterials)
            {
                int blocks = Tuning.T.roadStoneBlocksPerEdge;
                state = Counted("LW_Roads_InspectWaiting", blocks, blocks);
            }
            else if (phase == RoadPhase.Working)
                state = Workers(c, workersTmp) <= 0f ? "LW_Roads_InspectNoWorkers".Translate().Resolve() : c.NightResting ? "LW_Roads_InspectResting".Translate().Resolve() : null;
            return state == null ? line : line + " (" + state + ")";
        }
    }

    /// <summary>
    /// A road job walks the caravan one tile at a time with ordinary pathing; vanilla tells the player "arrived at destination" at the end
    /// of every such walk when the camera is elsewhere. Without an arrival action that message is all <c>PatherArrived</c> does besides
    /// stopping, so for the job's own hops it only stops. The suppression needs all of: the underlying arrival action field is empty (the
    /// public property reads null whenever the caravan is not moving, which hides an action set by a fresh <c>StartPath</c>), the job's own
    /// walk is what arrives, and no other order is inside <c>StartPath</c> right now. Everything else about arriving is untouched.
    /// </summary>
    [HarmonyPatch(typeof(Caravan_PathFollower), "PatherArrived")]
    public static class Patch_PathFollower_RoadHopArrived
    {
        internal static readonly AccessTools.FieldRef<Caravan_PathFollower, Caravan> CaravanField = AccessTools.FieldRefAccess<Caravan_PathFollower, Caravan>("caravan");
        private static readonly AccessTools.FieldRef<Caravan_PathFollower, CaravanArrivalAction> arrivalActionField =
            AccessTools.FieldRefAccess<Caravan_PathFollower, CaravanArrivalAction>("arrivalAction");

        /// <summary>The arrival action the pather holds, moving or not.</summary>
        internal static CaravanArrivalAction ArrivalActionOf(Caravan_PathFollower pather) => arrivalActionField(pather);

        public static bool Prefix(Caravan_PathFollower __instance)
        {
            if (Patch_PathFollower_RoadStartPath.OutsideOrder || arrivalActionField(__instance) != null) return true;
            Caravan c = CaravanField(__instance);
            if (c?.GetComponent<CompRoadBuilder>() is not CompRoadBuilder comp || !comp.OwnsArrivalAt(c.Tile)) return true;
            __instance.StopDead();
            return false;
        }
    }

    /// <summary>
    /// Tells the road job's own walk from every other order. A <c>StartPath</c> call that does not come from the comp itself and is not
    /// vanilla restarting the saved path after a load (that one keeps the pause status) is somebody's order: when it takes, the job ends
    /// at once and the new order, its destination and its arrival action are left alone, also one to the very tile the job was walking to.
    /// </summary>
    [HarmonyPatch(typeof(Caravan_PathFollower), nameof(Caravan_PathFollower.StartPath))]
    public static class Patch_PathFollower_RoadStartPath
    {
        private static int outsideDepth;

        /// <summary>
        /// Another order is inside StartPath right now (it may arrive at once and run its action: vanilla handles that). A depth, not a
        /// flag: an arrival action can order a second job caravan inside the first order's call.
        /// </summary>
        internal static bool OutsideOrder => outsideDepth > 0;

        public static void Prefix(Caravan_PathFollower __instance, bool resetPauseStatus, out bool __state)
        {
            __state = false;
            if (CompRoadBuilder.InternalStart || !resetPauseStatus) return;
            if (!(Patch_PathFollower_RoadHopArrived.CaravanField(__instance)?.GetComponent<CompRoadBuilder>() is { HasJob: true })) return;
            __state = true;
            outsideDepth++;
        }

        public static Exception Finalizer(Exception __exception, Caravan_PathFollower __instance, bool __result, bool __state)
        {
            if (!__state) return __exception;
            if (outsideDepth > 0) outsideDepth--;
            if (__exception == null && __result) Patch_PathFollower_RoadHopArrived.CaravanField(__instance)?.GetComponent<CompRoadBuilder>()?.Notify_ExternalOrder();
            return __exception;
        }
    }

    /// <summary>The world targeter for choosing where a road leads: remembers which targeting is ours so a stale one can be stopped.</summary>
    internal static class RoadTargeting
    {
        private static Func<GlobalTargetInfo, bool> current;
        private static readonly AccessTools.FieldRef<WorldTargeter, Func<GlobalTargetInfo, bool>> actionField =
            AccessTools.FieldRefAccess<WorldTargeter, Func<GlobalTargetInfo, bool>>("action");

        private static bool IsOurs => current != null && Find.WorldTargeter.IsTargeting && actionField(Find.WorldTargeter) == current;

        public static void Begin(CompRoadBuilder comp, RoadDef def)
        {
            Caravan c = comp.Caravan;
            if (c == null || !comp.StillAt(c.Tile)) return;
            PlanetTile start = c.Tile;
            current = target => comp.OnTargetChosen(def, start, target);
            Find.WorldTargeter.BeginTargeting(current, canTargetTiles: true, mouseAttachment: CompRoadBuilder.Icon, closeWorldTabWhenFinished: false,
                onUpdate: () =>
                {
                    // Roads switched off, or the caravan moved, merged, got a job or left the map: nothing left to aim for.
                    if (!Roads.Active || !comp.StillAt(start)) StopIfOurs();
                },
                extraLabelGetter: target => comp.HoverLabel(start, target),
                canSelectTarget: target => comp.CanTarget(start, target),
                originForClosest: start, showCancelButton: true);
        }

        public static void StopIfOurs()
        {
            // From the main menu (a settings change, a new game) there is no world to stop it in; the stale closure is dropped all the same.
            if (current != null && Current.ProgramState == ProgramState.Playing && Find.World != null && IsOurs) Find.WorldTargeter.StopTargeting();
            current = null;
        }
    }

    /// <summary>Adds <see cref="CompRoadBuilder"/> to every Caravan-class world object def.</summary>
    public static class RoadCompInjector
    {
        public static void Inject()
        {
            int n = 0;
            foreach (WorldObjectDef def in DefDatabase<WorldObjectDef>.AllDefsListForReading)
            {
                if (def.worldObjectClass == null || !typeof(Caravan).IsAssignableFrom(def.worldObjectClass)) continue;
                def.comps ??= new List<WorldObjectCompProperties>();
                if (def.comps.Exists(p => p.compClass == typeof(CompRoadBuilder))) continue;
                def.comps.Add(new WorldObjectCompProperties { compClass = typeof(CompRoadBuilder) });
                n++;
            }
            LWLog.Message($"road builder comp attached to {n} caravan defs");
        }
    }
}
