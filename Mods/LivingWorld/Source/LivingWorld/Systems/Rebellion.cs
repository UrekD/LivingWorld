using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// A deeply unhappy town of a large faction rises up and becomes a new faction (same kind of people), at war with
    /// its old masters. Renews a world that would otherwise only lose factions. Capped per game.
    /// </summary>
    public static class Rebellion
    {
        /// <summary>At most one new faction (rebellion or contested succession) in this time, so the world keeps renewing.</summary>
        public const int GapTicks = GenDate.TicksPerYear * 2;

        public static bool CanRise(LivingWorldComponent comp) => SimClock.Now - comp.lastRebellionTick >= GapTicks;

        public static void DailyStep(LivingWorldComponent comp, float pace)
        {
            if (!LivingWorldMod.Settings.allowConquest) return;
            foreach (CompSettlementLife s in comp.activeSettlements)
            {
                Faction f = s.Settlement.Faction;
                if (s.unrest < 0.6f || f.def.permanentEnemy || Tributaries.HeldFor(s.Settlement) != null || SettlementOps.Protected(s.Settlement) || !SettlementOps.CanFall(s.Settlement)) continue;
                FactionState fs = comp.StateFor(f);
                if (fs == null || !Rand.Chance(0.004f * pace * (s.unrest - 0.5f) * 10f)) continue;
                // An occupied town returns to its founders if they still exist; otherwise a big faction's town breaks away.
                Faction founders = s.Occupied ? comp.FactionById(s.originalFactionId) : null;
                if (founders != null && !founders.defeated && founders.HostileTo(f) && !founders.def.permanentEnemy)
                {
                    Liberate(comp, s, f, founders);
                    return;
                }
                if (!CanRise(comp) || fs.settlements.Count < 5 || !CanSplit(f.def)) continue;
                Rise(comp, s, f);
                return; // one per day at most
            }
        }

        internal static void Liberate(LivingWorldComponent comp, CompSettlementLife s, Faction occupier, Faction founders)
        {
            Settlement town = s.Settlement;
            string name = town.Label;
            HistoryTotals.Liberations++;
            if (s.Occupied) HistoryTotals.LiberationsOccupied++;
            SettlementOps.Capture(town, founders);
            s.unrest = 0.1f;
            s.garrison = s.population * 1.5f;
            ChronicleEventDef def = LWChronicleDefOf.LW_TownLiberated;
            comp.chronicle.Record(def, def.text.Formatted(name.Named("SETTLEMENT"), occupier.Name.Named("OCCUPIER"), founders.Name.Named("FOUNDERS")).Resolve(),
                founders, occupier, town);
            Roots.Notify(town, HomeNews.Helped);
        }

        /// <summary>
        /// Only ordinary faction kinds split or arrive anew: humanlike, visible, settling the planet's surface, no fixed name,
        /// no royal titles (the Empire). Not "required at game start": that excluded every outlander faction.
        /// </summary>
        public static bool CanSplit(FactionDef def) =>
            def.humanlikeFaction && !def.isPlayer && !def.hidden && def.fixedName == null && def.settlementGenerationWeight > 0f && !def.HasRoyalTitles
            && (def.layerWhitelist.NullOrEmpty() || def.layerWhitelist.Contains(PlanetLayerDefOf.Surface)) && (def.layerBlacklist == null || !def.layerBlacklist.Contains(PlanetLayerDefOf.Surface));

        /// <summary>A town breaks away under a rival claimant (contested succession); the war carries the given cause. A deposed
        /// ruling house may lead it: its line goes on there.</summary>
        public static void Secede(LivingWorldComponent comp, CompSettlementLife s, Faction parent, string cause, string claimantHouse = null, int claimantGenerations = 0) =>
            Rise(comp, s, parent, cause, claimantHouse, claimantGenerations);

        /// <summary>
        /// A new faction of the given kind, with its own ideology and leader but no settlement yet: generated hidden (so the
        /// generator places no random settlement), then made visible.
        /// </summary>
        public static Faction CreateFaction(FactionDef def)
        {
            Faction f = FactionGenerator.NewGeneratedFaction(Find.WorldGrid.Surface, new FactionGeneratorParms(def, IdeoParms(def), hidden: true));
            Find.FactionManager.Add(f);
            f.hidden = null; // the generator already made a leader
            return f;
        }

        /// <summary>What a new faction of the kind is made with for its ideology (a faith handed to it is checked against the same).</summary>
        internal static IdeoGenerationParms IdeoParms(FactionDef def) =>
            new IdeoGenerationParms(def, forceNoExpansionIdeo: false, null, null, name: def.ideoName, styles: def.styles, deities: def.deityPresets,
                hidden: def.hiddenIdeo, description: def.ideoDescription, forcedMemes: def.forcedMemes, classicExtra: false, forceNoWeaponPreference: false,
                forNewFluidIdeo: false, fixedIdeo: def.fixedIdeo, requiredPreceptsOnly: def.requiredPreceptsOnly);

        private static void Rise(LivingWorldComponent comp, CompSettlementLife s, Faction parent, string cause = null, string claimantHouse = null, int claimantGenerations = 0)
        {
            Faction rebels = CreateFaction(parent.def);
            Settlement town = s.Settlement;
            string townName = town.Label;
            if (s.Occupied) HistoryTotals.RisingsOccupied++;
            s.unrest = 0.1f;
            s.prosperity = Mathf.Clamp01(s.prosperity + 0.1f);
            SettlementOps.Capture(town, rebels);
            // These people chose this: the town is the rebels' own, not occupied.
            s.unrest = 0.1f;
            s.originalFactionId = rebels.loadID;
            s.occupiedSinceTicks = -1;
            // The rebels inherit their people's friends and enemies, start neutral with the colony, and fight their old masters.
            foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
            {
                if (other == rebels || other == parent || other.def.hidden || rebels.RelationWith(other, allowNull: true) == null) continue;
                int target = other.IsPlayer ? 0 : parent.BaseGoodwillWith(other);
                int delta = target - rebels.BaseGoodwillWith(other);
                if (delta != 0) rebels.TryAffectGoodwillWith(other, delta, canSendMessage: false, canSendHostilityLetter: false);
            }
            rebels.TryAffectGoodwillWith(parent, rebels.GoodwillToMakeHostile(parent), canSendMessage: false, canSendHostilityLetter: false);
            comp.rebellions++;
            comp.lastRebellionTick = SimClock.Now;
            // The claimant of a deposed house leads the breakaway (a freshly made leader nobody knows: safe to name).
            if (claimantHouse != null && rebels.leader?.Name is NameTriple nt && nt.IsValid)
                rebels.leader.Name = new NameTriple(nt.First, nt.Nick == nt.Last ? claimantHouse : nt.Nick == nt.First ? null : nt.Nick, claimantHouse);
            Rulers.InitNewFaction(comp, rebels);
            if (claimantHouse != null && comp.StateFor(rebels) is FactionState rfs && rfs.house == claimantHouse) rfs.houseGenerations = claimantGenerations + 1;
            FactionState rs = comp.StateFor(rebels);
            if (rs != null) rs.treasury = 300f;
            ChronicleEventDef def2 = LWChronicleDefOf.LW_Rebellion;
            ChronicleEntry e = comp.chronicle.Record(def2, def2.text.Formatted(townName.Named("SETTLEMENT"), parent.Name.Named("PARENT"), rebels.Name.Named("REBELS")).Resolve(),
                rebels, parent, town);
            War w = Diplomacy.StartWar(comp, rebels, parent, cause ?? "LW_WarCause_Rebellion".Translate().Resolve(), ancient: false, announce: false);
            w.chronicleId = e.id;
            LWLog.Message($"rebellion: {townName} of {parent.Name} became {rebels.Name}");
        }
    }
}
