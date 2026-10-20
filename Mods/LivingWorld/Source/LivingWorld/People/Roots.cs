using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>Where a colonist comes from: the settlement they lived in before joining the colony.</summary>
    public class PawnRoot : IExposable
    {
        public Pawn pawn;
        public int settlementId = -1;
        public string settlementName;
        public int factionId = -1;

        public void ExposeData()
        {
            Scribe_References.Look(ref pawn, "pawn");
            Scribe_Values.Look(ref settlementId, "settlement", -1);
            Scribe_Values.Look(ref settlementName, "settlementName");
            Scribe_Values.Look(ref factionId, "faction", -1);
        }
    }

    public enum HomeNews : byte { Razed, Conquered, Plundered, Helped }

    /// <summary>
    /// Colonists recruited from a faction remember their home settlement; what happens to it reaches them as
    /// memories (razed, conquered, plundered, helped by the colony).
    /// </summary>
    public static class Roots
    {
        public static PawnRoot Of(Pawn p)
        {
            foreach (PawnRoot r in LivingWorldComponent.Instance.roots)
                if (r.pawn == p) return r;
            return null;
        }

        public static void Notify_Joined(Pawn p, Faction oldFaction, Lord oldLord)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || p == null || !p.RaceProps.Humanlike || oldFaction == null || !FactionState.IsSimulated(oldFaction) || Of(p) != null) return;
            Settlement home = null;
            IncidentContext ctx = Bridge.ContextOf(oldLord);
            if (ctx != null) home = LivingWorldComponent.WorldObjectById(ctx.originId) as Settlement;
            if (home == null && p.MapHeld != null) home = Bridge.NearestSettlement(oldFaction, p.MapHeld.Tile);
            if (home == null) return;
            comp.roots.Add(new PawnRoot { pawn = p, settlementId = home.ID, settlementName = home.Label, factionId = oldFaction.loadID });
            Kin.CreateFor(p, home); // maybe a parent stays behind there
        }

        /// <summary>Something happened to a settlement: tell the colonists who came from there.</summary>
        public static void Notify(Settlement s, HomeNews news)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || s == null || SimClock.InHistory) return;
            if (news == HomeNews.Razed) Kin.Notify_HomeRazed(s);
            ThoughtDef def = news switch
            {
                HomeNews.Razed => LWThoughtDefOf.LW_HomeRazed,
                HomeNews.Conquered => LWThoughtDefOf.LW_HomeConquered,
                HomeNews.Plundered => LWThoughtDefOf.LW_HomePlundered,
                _ => LWThoughtDefOf.LW_HomeHelped,
            };
            foreach (PawnRoot r in comp.roots)
            {
                Pawn p = r.pawn;
                if (r.settlementId != s.ID || p == null || p.Dead || p.Faction != Faction.OfPlayer || p.needs?.mood == null) continue;
                p.needs.mood.thoughts.memories.TryGainMemory(def);
                Messages.Message(("LW_HomeNews_" + news).Translate(p.Named("PAWN"), r.settlementName.Named("HOME")), p, news == HomeNews.Helped ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NegativeEvent);
            }
        }
    }

    [DefOf]
    public static class LWThoughtDefOf
    {
        public static ThoughtDef LW_HomeRazed;
        public static ThoughtDef LW_HomeConquered;
        public static ThoughtDef LW_HomePlundered;
        public static ThoughtDef LW_HomeHelped;
        // Visitors' news from home (VisitorStories).
        public static ThoughtDef LW_Visitor_HomeSacked;
        public static ThoughtDef LW_Visitor_HomeStarving;
        public static ThoughtDef LW_Visitor_HomeAtWar;
        public static ThoughtDef LW_Visitor_HomeFair;
        public static ThoughtDef LW_Visitor_HomeThriving;
        public static ThoughtDef LW_Visitor_KinReleased;
        public static ThoughtDef LW_Visitor_KinExecuted;
        public static ThoughtDef LW_HostedRuler;
        public static ThoughtDef LW_BetrayedHome;
        public static ThoughtDef LW_StoodUpForHome;
        public static ThoughtDef LW_PactWithHome;
        public static ThoughtDef LW_FamilyVisit;
        public static ThoughtDef LW_MarkedForVengeance;
        public static ThoughtDef LW_FeudOver;

        static LWThoughtDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWThoughtDefOf));
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
    public static class Patch_Pawn_SetFaction_Roots
    {
        public static void Prefix(Pawn __instance, out (Faction, Lord) __state) => __state = (__instance.Faction, __instance.GetLord());

        public static void Postfix(Pawn __instance, Faction newFaction, (Faction, Lord) __state)
        {
            if (Current.ProgramState != ProgramState.Playing || newFaction == null || !newFaction.IsPlayer || __state.Item1 == null || __state.Item1.IsPlayer) return;
            try
            {
                Roots.Notify_Joined(__instance, __state.Item1, __state.Item2);
            }
            catch (System.Exception e)
            {
                LWLog.Error("roots: " + e);
            }
        }
    }
}
