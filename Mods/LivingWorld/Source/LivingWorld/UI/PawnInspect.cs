using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace LivingWorld
{
    /// <summary>
    /// What the world knows about a visitor, in their inspect pane: the town they came from (and what is going on
    /// there, and the colony's name there); for a recurring figure their history with the colony (visits, trade,
    /// colonists they know, an open order); for a ruler their temperament and aim.
    /// </summary>
    public static class PawnInspect
    {
        public static string ExtraLines(Pawn p)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || p.Faction == null || p.Faction.IsPlayer || !p.RaceProps.Humanlike || !p.Spawned || p.Map?.IsPlayerHome != true) return null;
            var sb = new StringBuilder();
            Settlement home = HomeOf(comp, p);
            if (home != null)
            {
                CompSettlementLife life = home.GetComponent<CompSettlementLife>();
                string story = life == null ? null : StoryLabel(VisitorStories.StoryOf(comp, home));
                sb.Append("LW_Inspect_From".Translate(home.Label, home.Faction?.Name ?? "?"));
                if (story != null) sb.Append(" · ").Append(story);
                if (life != null && Mathf.Abs(life.fame) >= 0.05f) sb.Append(" · ").Append("LW_Inspect_FameThere".Translate(life.fame.ToStringPercent()));
            }
            if (Figures.Of(p) is Figure f)
            {
                sb.AppendLine().Append("LW_Inspect_Figure".Translate(f.RoleLabel, f.visits));
                if (f.tradedSilver >= 1f) sb.Append(" · ").Append("LW_Inspect_Traded".Translate(Mathf.RoundToInt(f.tradedSilver)));
                if (f.FeudOpen) sb.AppendLine().Append("LW_Inspect_Sworn".Translate(f.nemesisName, f.avenges ?? "?"));
                string knows = KnownColonists(p.Map, f.friends);
                if (knows != null) sb.AppendLine().Append("LW_Inspect_Knows".Translate(knows));
                if (f.commissionDef != null)
                    sb.AppendLine().Append("LW_Inspect_Order".Translate(f.commissionCount, f.commissionDef.label, Mathf.Max(0, f.commissionDue - Find.TickManager.TicksGame).ToStringTicksToPeriod()));
            }
            else if (p.Faction.leader == p && comp.StateFor(p.Faction) is FactionState fs && fs.rulerTraits.Count > 0)
            {
                sb.AppendLine().Append("LW_Inspect_Ruler".Translate(Rulers.TraitsLabel(fs), Rulers.GoalLabel(comp, fs)));
                string knows = KnownColonists(p.Map, fs.rulerFriends);
                if (knows != null) sb.AppendLine().Append("LW_Inspect_Knows".Translate(knows));
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>The town the pawn's group came from (its incident context), else a figure's home.</summary>
        private static Settlement HomeOf(LivingWorldComponent comp, Pawn p)
        {
            Lord lord = p.GetLord();
            if (lord != null)
                foreach (IncidentContext ctx in comp.contexts)
                    if (ctx.lordId == lord.loadID && LivingWorldComponent.WorldObjectById(ctx.originId) is Settlement s) return s;
            return Figures.Of(p) is Figure f ? LivingWorldComponent.WorldObjectById(f.homeId) as Settlement : null;
        }

        private static string StoryLabel(HomeStory story) => story == HomeStory.None ? null : ("LW_Inspect_Story_" + story).Translate().Resolve();

        private static string KnownColonists(Map map, List<int> ids)
        {
            if (ids.NullOrEmpty() || map == null) return null;
            var names = new List<string>();
            foreach (Pawn c in map.mapPawns.FreeColonists)
                if (ids.Contains(c.thingIDNumber)) names.Add(c.LabelShort);
            return names.Count == 0 ? null : string.Join(", ", names);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
    public static class Patch_Pawn_GetInspectString
    {
        public static void Postfix(Pawn __instance, ref string __result)
        {
            string extra;
            try
            {
                extra = PawnInspect.ExtraLines(__instance);
            }
            catch (System.Exception e)
            {
                LWLog.ErrorOnce("pawn inspect: " + e, 0x4c57_1001);
                return;
            }
            if (extra.NullOrEmpty()) return;
            __result = __result.NullOrEmpty() ? extra : __result.TrimEnd() + "\n" + extra;
        }
    }
}
