using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>What is going on at a group's home town, as its people carry it onto the colony map.</summary>
    public enum HomeStory : byte { None, Sacked, Starving, War, Fair, Thriving, KinReleased, KinExecuted }

    /// <summary>
    /// Groups that walk in from a real town bring its story with them, without touching their AI: a mood thought for
    /// what is happening at home (sacked, starving, at war, a fair, thriving), hunger when their town starves (guests
    /// then eat and buy food first), news they tell colonists they meet, and warm reunions of returning figures with
    /// colonists who know them.
    /// </summary>
    public static class VisitorStories
    {
        private static readonly List<Pawn> colonists = new List<Pawn>();

        public static HomeStory StoryOf(LivingWorldComponent comp, Settlement home)
        {
            CompSettlementLife life = home?.GetComponent<CompSettlementLife>();
            if (life == null || !life.Active) return HomeStory.None;
            int now = SimClock.TicksAbs;
            // What the colony did with one of their own, in the last month: that is what they talk about.
            if (life.captiveNews != CaptiveNews.None && Find.TickManager.TicksGame - life.captiveNewsTick < GenDate.TicksPerDay * 30)
                return life.captiveNews == CaptiveNews.Executed ? HomeStory.KinExecuted : HomeStory.KinReleased;
            // Sacked: plundered, captured or burned in the last fortnight.
            for (int i = comp.chronicle.entries.Count - 1; i >= 0; i--)
            {
                ChronicleEntry e = comp.chronicle.entries[i];
                if (now - e.ticksAbs > GenDate.TicksPerDay * 15) break;
                if (e.worldObjectId == home.ID && (e.def == LWChronicleDefOf.LW_SettlementPlundered || e.def == LWChronicleDefOf.LW_SettlementCaptured)) return HomeStory.Sacked;
            }
            if (life.famineDays > 0 || life.hunger > 0.2f) return HomeStory.Starving;
            if (comp.AtWar(comp.StateFor(home.Faction))) return HomeStory.War;
            if (life.FairOn) return HomeStory.Fair;
            if (life.prosperity > 0.7f) return HomeStory.Thriving;
            return HomeStory.None;
        }

        /// <summary>A group from a known town arrived on a player home map.</summary>
        public static void Notify_GroupArrived(Lord lord, IncidentContext ctx)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (LivingWorldComponent.WorldObjectById(ctx.originId) is not Settlement home) return;
            HomeStory story = StoryOf(comp, home);
            ctx.story = (byte)story;
            ThoughtDef thought = story switch
            {
                HomeStory.Sacked => LWThoughtDefOf.LW_Visitor_HomeSacked,
                HomeStory.Starving => LWThoughtDefOf.LW_Visitor_HomeStarving,
                HomeStory.War => LWThoughtDefOf.LW_Visitor_HomeAtWar,
                HomeStory.Fair => LWThoughtDefOf.LW_Visitor_HomeFair,
                HomeStory.Thriving => LWThoughtDefOf.LW_Visitor_HomeThriving,
                HomeStory.KinReleased => LWThoughtDefOf.LW_Visitor_KinReleased,
                HomeStory.KinExecuted => LWThoughtDefOf.LW_Visitor_KinExecuted,
                _ => null,
            };
            foreach (Pawn p in lord.ownedPawns)
            {
                if (!p.RaceProps.Humanlike) continue;
                if (thought != null) p.needs?.mood?.thoughts.memories.TryGainMemory(thought);
                // People from a starving town arrive hungry (and eat or buy food first).
                if (story == HomeStory.Starving && p.needs?.food != null) p.needs.food.CurLevelPercentage = Mathf.Min(p.needs.food.CurLevelPercentage, 0.25f);
            }
        }

        /// <summary>Hourly: someone from each group tells a colonist nearby about home; old acquaintances greet each other.</summary>
        public static void HourlyTick(LivingWorldComponent comp)
        {
            foreach (IncidentContext ctx in comp.contexts)
            {
                if (ctx.kind == ContextKind.Raid || ctx.lordId < 0) continue;
                Lord lord = FindLord(ctx.lordId);
                if (lord?.Map == null || !lord.Map.IsPlayerHome) continue;
                colonists.Clear();
                colonists.AddRange(lord.Map.mapPawns.FreeColonistsSpawned);
                if (colonists.Count == 0) continue;
                Reunions(comp, ctx, lord);
                VisitorPleas.TryPlea(comp, ctx, lord);
                if (ctx.story != (byte)HomeStory.None && ctx.newsTold < 3 && Rand.Chance(0.5f)) TellNews(ctx, lord);
                if (!ctx.warnedOfFeud && (TestWarnAlways || Rand.Chance(0.25f))) WarnOfFeud(comp, ctx, lord);
            }
        }

        /// <summary>Tests: a visiting group warns of an open feud at the first chance.</summary>
        public static bool TestWarnAlways;
        public static int StatFeudWarnings;

        /// <summary>
        /// Friendly visitors who heard of a blood feud sworn against one of the colonists warn them (once per visit): who is
        /// sworn, and whether the avenger is already gathering a war band (their return raid is pending).
        /// </summary>
        private static void WarnOfFeud(LivingWorldComponent comp, IncidentContext ctx, Lord lord)
        {
            if (lord.faction == null || lord.faction.HostileTo(Faction.OfPlayer)) return;
            Figure avenger = null;
            foreach (Figure f in comp.figures)
                if (f.FeudOpen && f.Alive && f.factionId != lord.faction.loadID && f.nemesis.MapHeld == lord.Map) avenger = f;
            if (avenger == null) return;
            foreach (Pawn teller in lord.ownedPawns)
            {
                if (!teller.RaceProps.Humanlike || !teller.Spawned || teller.Downed || teller.interactions == null) continue;
                InteractionDef warning = LWInteractionDefOf.LW_VisitorFeudWarning;
                // The hunted colonist if nearby and free to listen (not asleep or busy), else whoever is.
                Pawn listener = avenger.nemesis.Spawned && avenger.nemesis.Position.InHorDistOf(teller.Position, 12f) && CanTalk(teller, avenger.nemesis, warning)
                    ? avenger.nemesis : NearestColonist(teller, 8f, warning);
                if (listener == null || !teller.interactions.TryInteractWith(listener, warning)) continue;
                ctx.warnedOfFeud = true;
                StatFeudWarnings++;
                bool coming = comp.revenge.Any(r => r.figureId == avenger.id);
                Messages.Message((coming ? "LW_FeudWarningComing" : "LW_FeudWarning").Translate(teller.Named("TELLER"), avenger.name.Named("AVENGER"), (avenger.homeName ?? "?").Named("HOME"),
                    avenger.nemesisName.Named("SLAYER")), new LookTargets(teller, avenger.nemesis), coming ? MessageTypeDefOf.ThreatSmall : MessageTypeDefOf.NeutralEvent);
                return;
            }
        }

        private static Lord FindLord(int loadId)
        {
            foreach (Map m in Find.Maps)
                foreach (Lord l in m.lordManager.lords)
                    if (l.loadID == loadId) return l;
            return null;
        }

        private static void TellNews(IncidentContext ctx, Lord lord)
        {
            InteractionDef def = (HomeStory)ctx.story switch
            {
                HomeStory.Sacked => LWInteractionDefOf.LW_VisitorNewsSacked,
                HomeStory.Starving => LWInteractionDefOf.LW_VisitorNewsStarving,
                HomeStory.War => LWInteractionDefOf.LW_VisitorNewsWar,
                HomeStory.Fair => LWInteractionDefOf.LW_VisitorNewsFair,
                HomeStory.KinReleased => LWInteractionDefOf.LW_VisitorNewsKinReleased,
                HomeStory.KinExecuted => LWInteractionDefOf.LW_VisitorNewsKinExecuted,
                _ => LWInteractionDefOf.LW_VisitorNewsThriving,
            };
            foreach (Pawn teller in lord.ownedPawns)
            {
                if (!teller.RaceProps.Humanlike || !teller.Spawned || teller.Downed || teller.interactions == null) continue;
                Pawn listener = NearestColonist(teller, 8f, def);
                if (listener != null && teller.interactions.TryInteractWith(listener, def))
                {
                    ctx.newsTold++;
                    Acquaint(teller, listener);
                    return;
                }
            }
        }

        /// <summary>The colonist and this figure or ruler now know each other (from news told, a reunion, a trade).</summary>
        private static void Acquaint(Pawn visitor, Pawn colonist)
        {
            if (Figures.Of(visitor) is Figure f) Figures.Befriend(f, colonist);
            else if (visitor.Faction?.leader == visitor && LivingWorldComponent.Instance.StateFor(visitor.Faction) is FactionState fs && !fs.rulerFriends.Contains(colonist.thingIDNumber))
                fs.rulerFriends.Add(colonist.thingIDNumber);
        }

        /// <summary>Colonists this visitor has met before: a returning figure's friends, a ruler's acquaintances.</summary>
        private static bool Knows(Pawn visitor, Pawn colonist)
        {
            if (Figures.Of(visitor) is Figure f) return f.visits >= 2 && f.friends.Contains(colonist.thingIDNumber);
            return visitor.Faction?.leader == visitor && LivingWorldComponent.Instance.StateFor(visitor.Faction) is FactionState fs && fs.rulerFriends.Contains(colonist.thingIDNumber);
        }

        /// <summary>A returning figure or ruler meets a colonist they have dealt with before: they greet each other.</summary>
        private static void Reunions(LivingWorldComponent comp, IncidentContext ctx, Lord lord)
        {
            if (ctx.reunited) return;
            foreach (Pawn visitor in lord.ownedPawns)
            {
                if (!visitor.Spawned || visitor.Downed || visitor.interactions == null) continue;
                if (Figures.Of(visitor) == null && visitor.Faction?.leader != visitor) continue;
                foreach (Pawn c in colonists)
                {
                    if (c.Downed || !Knows(visitor, c)) continue;
                    if (!c.Position.InHorDistOf(visitor.Position, 10f) || !CanTalk(visitor, c, LWInteractionDefOf.LW_Reunion) || !visitor.interactions.TryInteractWith(c, LWInteractionDefOf.LW_Reunion)) continue;
                    ctx.reunited = true;
                    Messages.Message("LW_Reunion".Translate(visitor.Named("VISITOR"), c.Named("COLONIST")), new LookTargets(visitor, c), MessageTypeDefOf.PositiveEvent);
                    return;
                }
            }
        }

        /// <summary>
        /// Vanilla would refuse (asleep, busy, mental state) or log an error (talked under 120 ticks ago) for this pair: ask
        /// first, as its own random interactions do.
        /// </summary>
        private static bool CanTalk(Pawn teller, Pawn listener, InteractionDef def) =>
            teller.interactions != null && (def.ignoreTimeSinceLastInteraction || !teller.interactions.InteractedTooRecentlyToInteract()) && teller.interactions.CanInteractNowWith(listener, def);

        private static Pawn NearestColonist(Pawn p, float range, InteractionDef def)
        {
            Pawn best = null;
            float bestD = range * range;
            foreach (Pawn c in colonists)
            {
                if (c.Downed || !c.Awake() || !CanTalk(p, c, def)) continue;
                float d = (c.Position - p.Position).LengthHorizontalSquared;
                if (d < bestD)
                {
                    bestD = d;
                    best = c;
                }
            }
            return best;
        }
    }

    [DefOf]
    public static class LWInteractionDefOf
    {
        public static InteractionDef LW_VisitorNewsSacked;
        public static InteractionDef LW_VisitorNewsStarving;
        public static InteractionDef LW_VisitorNewsWar;
        public static InteractionDef LW_VisitorNewsFair;
        public static InteractionDef LW_VisitorNewsThriving;
        public static InteractionDef LW_Reunion;
        public static InteractionDef LW_VisitorNewsKinReleased;
        public static InteractionDef LW_VisitorNewsKinExecuted;
        public static InteractionDef LW_VisitorFeudWarning;

        static LWInteractionDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWInteractionDefOf));
    }
}
