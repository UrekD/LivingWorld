using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// What the colony does to the world is remembered: destroying a settlement (vanilla assault) is chronicled, grieved
    /// by colonists from there and avenged by its faction. Visitors bring news of their region.
    /// </summary>
    public static class PlayerDeeds
    {
        public static void Notify_SettlementDestroyedByPlayer(Settlement s, Faction victim, string name, PlanetTile tile)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null || victim == null) return;
            ChronicleEventDef def = LWChronicleDefOf.LW_PlayerDestroyedSettlement;
            ChronicleEntry e = comp.chronicle.Record(def, def.text.Formatted(Faction.OfPlayer.Name.Named("COLONY"), name.Named("SETTLEMENT"), victim.Name.Named("FACTION")).Resolve(),
                Faction.OfPlayer, victim, tile: tile, silent: true);
            Roots.Notify(s, HomeNews.Razed);
            Demands.Notify_SettlementDestroyed(s.ID, victim);
            FactionState fs = comp.StateFor(victim);
            Diplomacy.AddGrudge(fs, Faction.OfPlayer, 50f, "LW_Grudge_DestroyedTown".Translate(name).Resolve());
            Settlement avenger = Bridge.NearestSettlement(victim, tile);
            if (avenger != null && avenger != s) Revenge.Schedule(victim, avenger.ID, e.id);
        }

        /// <summary>A group from a settlement arrived: they tell what happened lately around their home.</summary>
        public static void TellNews(IncidentContext ctx)
        {
            if (ctx.kind == ContextKind.Raid || LivingWorldComponent.WorldObjectById(ctx.originId) is not Settlement home) return;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            int now = SimClock.TicksAbs;
            var news = new List<string>();
            for (int i = comp.chronicle.entries.Count - 1; i >= 0 && news.Count < 1; i--)
            {
                ChronicleEntry e = comp.chronicle.entries[i];
                if (now - e.ticksAbs > GenDate.TicksPerDay * 20) break;
                if (e.involvesPlayer || e.def.importance == ChronicleImportance.Minor || !e.tile.Valid) continue;
                if (Find.WorldGrid.ApproxDistanceInTiles(e.tile, home.Tile) > 25f) continue;
                news.Add(e.text);
            }
            if (news.Count == 0) return;
            // One line: the message stack does not make room for multi-line messages.
            Messages.Message("LW_NewsFromVisitors".Translate(home.Label).Resolve() + " " + news[0], home, MessageTypeDefOf.NeutralEvent);
        }
    }

    [HarmonyPatch(typeof(SettlementDefeatUtility), nameof(SettlementDefeatUtility.CheckDefeated))]
    public static class Patch_SettlementDefeatUtility_CheckDefeated
    {
        public static void Prefix(Settlement factionBase, out (Faction, string, PlanetTile) __state) =>
            __state = (factionBase.Faction, factionBase.Label, factionBase.Tile);

        public static void Postfix(Settlement factionBase, (Faction, string, PlanetTile) __state)
        {
            if (!factionBase.Destroyed || __state.Item1 == null || __state.Item1.IsPlayer) return;
            try
            {
                PlayerDeeds.Notify_SettlementDestroyedByPlayer(factionBase, __state.Item1, __state.Item2, __state.Item3);
            }
            catch (System.Exception e)
            {
                LWLog.Error("player deed: " + e);
            }
        }
    }
}
