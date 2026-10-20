using RimWorld;
using Verse;

namespace LivingWorld
{
    public enum TalkTopic : byte { War, Markets, Famine, Home, Feud, Deeds, Rulers }

    /// <summary>Which world topic an interaction def is about.</summary>
    public class WorldTalkExtension : DefModExtension
    {
        public TalkTopic topic;
        public float weight = 0.12f;
    }

    /// <summary>
    /// Colonists talk about what is happening in the world: the wars, prices in the towns, a famine nearby, the
    /// home town they came from. Only when there is something to talk about (recent chronicle events, roots).
    /// </summary>
    public class InteractionWorker_WorldTalk : InteractionWorker
    {
        public override float RandomSelectionWeight(Pawn initiator, Pawn recipient)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            WorldTalkExtension ext = interaction.GetModExtension<WorldTalkExtension>();
            if (comp == null || ext == null || !initiator.IsColonist || !recipient.IsColonist) return 0f;
            if (!Relevant(comp, ext.topic, initiator)) return 0f;
            // The hunted colonist cannot stop talking about it.
            if (ext.topic == TalkTopic.Feud && IsHunted(comp, initiator)) return ext.weight * 3f;
            return ext.weight;
        }

        /// <summary>A faction with a town in the colony's neighbourhood (news of far-off rulers is not worth gossip).</summary>
        private static bool Nearby(LivingWorldComponent comp, int factionId)
        {
            Map home = Neighbourhood.Home;
            FactionState fs = comp.FactionById(factionId) is Faction f ? comp.ExistingStateFor(f) : null;
            if (home == null || fs == null) return false;
            float range = Neighbourhood.Radius(home.Tile, Neighbours.Range);
            foreach (CompSettlementLife s in fs.settlements)
                if (s.Active && Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile) <= range) return true;
            return false;
        }

        private static bool IsHunted(LivingWorldComponent comp, Pawn p)
        {
            foreach (Figure f in comp.figures)
                if (f.nemesis == p && f.FeudOpen && f.Alive) return true;
            return false;
        }

        private static bool Relevant(LivingWorldComponent comp, TalkTopic topic, Pawn initiator)
        {
            switch (topic)
            {
                case TalkTopic.Home:
                    return Roots.Of(initiator) != null;
                case TalkTopic.Markets:
                    return comp.activeSettlements.Count > 0; // always something to grumble about (low weight)
                case TalkTopic.Feud:
                    // A blood feud sworn against someone of the colony (the hunted one most of all: see the weight).
                    foreach (Figure f in comp.figures)
                        if (f.FeudOpen && f.Alive && f.nemesis.Faction == Faction.OfPlayer) return true;
                    return false;
                case TalkTopic.Deeds:
                    foreach (Deed d in comp.deeds)
                        if (d.pawn == initiator) return true;
                    return false;
                default:
                    int now = SimClock.TicksAbs;
                    for (int i = comp.chronicle.entries.Count - 1; i >= 0; i--)
                    {
                        ChronicleEntry e = comp.chronicle.entries[i];
                        if (now - e.ticksAbs > GenDate.TicksPerDay * 10) return false;
                        if (topic == TalkTopic.War && e.def.category == ChronicleCategory.War && e.def.importance >= ChronicleImportance.Notable) return true;
                        if (topic == TalkTopic.Famine && (e.def == LWChronicleDefOf.LW_Famine || e.def == LWChronicleDefOf.LW_Plague)) return true;
                        if (topic == TalkTopic.Rulers && (e.def == LWChronicleDefOf.LW_LeaderChanged || e.def == LWChronicleDefOf.LW_NewHouse || e.def == LWChronicleDefOf.LW_RulerFell)
                            && Nearby(comp, e.factionA)) return true;
                    }
                    return false;
            }
        }
    }
}
