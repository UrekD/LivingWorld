using RimWorld;
using RimWorld.Planet;
using Verse;

namespace LivingWorld
{
    /// <summary>Turns daily state changes into chronicle entries (growth, famine, successions).</summary>
    public static class Narrative
    {
        public static ChronicleLog Log => LivingWorldComponent.Instance.chronicle;

        public static void SettlementDay(CompSettlementLife s, int tierChange)
        {
            Settlement st = s.Settlement;
            if (tierChange != 0)
            {
                ChronicleEventDef def = tierChange > 0 ? LWChronicleDefOf.LW_SettlementGrew : LWChronicleDefOf.LW_SettlementShrank;
                Log.Record(def, def.text.Formatted(st.Label.Named("SETTLEMENT"), st.Faction.Name.Named("FACTION"), s.Tier.label.Named("TIER")).Resolve(),
                    st.Faction, wo: st);
            }
            if (s.famineDays >= 3 && s.hunger > 0.25f && !s.famineAnnounced)
            {
                ChronicleEventDef def = LWChronicleDefOf.LW_Famine;
                Log.Record(def, def.text.Formatted(st.Label.Named("SETTLEMENT"), st.Faction.Name.Named("FACTION")).Resolve(), st.Faction, wo: st);
                s.famineAnnounced = true;
                Refugees.Notify_Disaster(st, razed: false);
            }
            if (s.famineDays == 0) s.famineAnnounced = false;
        }

        public static void FactionDay(FactionState fs)
        {
            Faction f = fs.faction;
            Pawn leader = f.leader;
            int id = leader?.thingIDNumber ?? -1;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            // The old ruler's goal is not carried out by the successor: goals are checked only for a known ruler.
            if (leader != null && fs.rulerThingId == id) Rulers.DailyCheck(comp, fs);
            bool succession = fs.lastLeaderThingId >= 0 && leader != null && id != fs.lastLeaderThingId;
            // The house is decided when vanilla picks the leader (TryGenerateNewLeader); here only rulers that came another
            // way, first sight (new factions, old saves) and the chronicle of a new house.
            if (leader != null && fs.houseLeaderId != id) Rulers.Succeed(fs, leader, succession);
            bool newHouse = fs.pendingNewHouse;
            if (newHouse && leader != null)
            {
                fs.pendingNewHouse = false;
                ChronicleEventDef nh = LWChronicleDefOf.LW_NewHouse;
                Log.Record(nh, nh.text.Formatted(fs.house.Named("HOUSE"), f.Name.Named("FACTION"), leader.Name.ToStringFull.Named("NAME"), fs.pendingOldHouse.Named("OLD"),
                    fs.pendingOldGenerations.Named("GENERATIONS")).Resolve(), f);
            }
            if (succession)
            {
                ChronicleEventDef def = LWChronicleDefOf.LW_LeaderChanged;
                Log.Record(def, def.text.Formatted(f.Name.Named("FACTION"), (fs.lastLeaderName ?? "?").Named("OLD"), leader.Name.ToStringFull.Named("NEW"),
                    f.LeaderTitle.Named("TITLE"), Rulers.HouseLabel(fs).Named("HOUSE")).Resolve(), f, silent: true);
            }
            fs.lastLeaderThingId = id;
            fs.lastLeaderName = leader?.Name?.ToStringFull;
            // A new ruler (or one never seen: old saves, new factions) gets a temperament and a goal, after the succession entry.
            if (leader != null && fs.rulerThingId != id) Rulers.Notify_Leader(comp, fs, leader, succession, newHouse);
        }
    }
}
