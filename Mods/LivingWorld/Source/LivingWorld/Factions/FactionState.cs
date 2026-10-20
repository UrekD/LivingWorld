using System.Collections.Generic;
using RimWorld;
using Verse;

namespace LivingWorld
{
    public enum RulerGoalKind : byte { Hold, Conquer, Avenge, Prosper, Expand }

    /// <summary>A grudge or debt one faction holds against another (or the player): read by diplomacy, decays.</summary>
    public class Grievance : IExposable
    {
        public int otherFactionId = -1;
        public float amount;
        public string reason;
        public int sinceTicks;

        public void ExposeData()
        {
            Scribe_Values.Look(ref otherFactionId, "other", -1);
            Scribe_Values.Look(ref amount, "amount");
            Scribe_Values.Look(ref reason, "reason");
            Scribe_Values.Look(ref sinceTicks, "since");
        }
    }

    /// <summary>
    /// Simulation state of one NPC faction, keyed by the faction's loadID (resolved after load; survives the faction
    /// being removed). Strength and settlement lists are derived daily from the settlement comps.
    /// </summary>
    public class FactionState : IExposable
    {
        public const int Schema = 1;

        public int factionId = -1;
        public int schemaVersion = Schema;
        public List<FactionTraitDef> traits = new List<FactionTraitDef>();
        public float treasury;
        /// <summary>0..1: tiredness of war; drives peace.</summary>
        public float warWeariness;
        public List<Grievance> grievances = new List<Grievance>();
        /// <summary>Leader at the last daily check (loadID-style pawn id), to chronicle successions.</summary>
        public int lastLeaderThingId = -1;
        public string lastLeaderName;
        // The ruler (vanilla faction.leader) as an actor: temperament and goal, rolled at each accession (Rulers).
        public int rulerThingId = -1;
        /// <summary>The ruling house (a surname) and how many rulers of it in a row.</summary>
        public string house;
        public int houseGenerations;
        /// <summary>The ruler the house was last decided for; a new house waiting to be chronicled (with the old one).</summary>
        public int houseLeaderId = -1;
        public bool pendingNewHouse;
        public string pendingOldHouse;
        public int pendingOldGenerations;
        public List<RulerTraitDef> rulerTraits = new List<RulerTraitDef>();
        public RulerGoalKind goal;
        /// <summary>Faction loadID the goal is about (Conquer, Avenge; the player's faction for vengeance on the colony).</summary>
        public int goalTargetId = -1;
        public int goalSince;
        public int goalProgress;

        /// <summary>When the colony last sent this faction an envoy (one every <see cref="PlayerDiplomacy.CooldownTicks"/>).</summary>
        public int lastPlayerEnvoyTick = Never;
        public int lastPlayerTributeTick = Never;
        public int lastRulerVisitTick = Never;
        /// <summary>Trade pact with the colony (a ruler's audience) until this tick: their merchants come more often.</summary>
        public int tradePactUntil = Never;
        /// <summary>Ages learned beyond the faction's kind (KnowHow), and progress toward the next one.</summary>
        public int techBonus;
        public float techProgress;
        /// <summary>Colonists the current ruler has met on a visit (greeted on the next one).</summary>
        public List<int> rulerFriends = new List<int>();
        /// <summary>"Long ago": far enough back for any cooldown, without overflowing a subtraction.</summary>
        public const int Never = int.MinValue / 2;

        // Derived daily (not saved).
        [Unsaved] public Faction faction;
        [Unsaved] public readonly List<CompSettlementLife> settlements = new List<CompSettlementLife>();
        [Unsaved] public float strength;
        [Unsaved] public float population;

        public string Name => faction?.Name ?? "?";

        /// <summary>Product of one multiplier over the faction's traits and its ruler's.</summary>
        public float TraitFactor(System.Func<TemperamentDef, float> pick)
        {
            float f = 1f;
            foreach (FactionTraitDef t in traits) f *= pick(t);
            foreach (RulerTraitDef t in rulerTraits) f *= pick(t);
            return f;
        }

        /// <summary>The ruler has sworn to conquer or avenge themselves on this faction.</summary>
        public bool GoalAgainst(Faction other) =>
            other != null && goalTargetId == other.loadID && (goal == RulerGoalKind.Conquer || goal == RulerGoalKind.Avenge);

        /// <summary>Factions the simulation models: visible, permanent, humanlike NPC factions (pirates included).</summary>
        public static bool IsSimulated(Faction f) =>
            f != null && !f.IsPlayer && !f.Hidden && !f.temporary && f.def.humanlikeFaction;

        public void ExposeData()
        {
            Scribe_Values.Look(ref factionId, "factionId", -1);
            Scribe_Values.Look(ref schemaVersion, "schema");
            Scribe_Collections.Look(ref traits, "traits", LookMode.Def);
            Scribe_Values.Look(ref treasury, "treasury");
            Scribe_Values.Look(ref warWeariness, "warWeariness");
            Scribe_Collections.Look(ref grievances, "grievances", LookMode.Deep);
            Scribe_Values.Look(ref lastLeaderThingId, "lastLeader", -1);
            Scribe_Values.Look(ref lastLeaderName, "lastLeaderName");
            Scribe_Values.Look(ref lastPlayerEnvoyTick, "lastPlayerEnvoy", Never);
            Scribe_Values.Look(ref lastPlayerTributeTick, "lastPlayerTribute", Never);
            Scribe_Values.Look(ref lastRulerVisitTick, "lastRulerVisit", Never);
            Scribe_Values.Look(ref tradePactUntil, "tradePactUntil", Never);
            Scribe_Values.Look(ref techBonus, "techBonus");
            Scribe_Values.Look(ref techProgress, "techProgress");
            Scribe_Collections.Look(ref rulerFriends, "rulerFriends", LookMode.Value);
            Scribe_Values.Look(ref rulerThingId, "ruler", -1);
            Scribe_Values.Look(ref house, "house");
            Scribe_Values.Look(ref houseGenerations, "houseGenerations");
            Scribe_Values.Look(ref houseLeaderId, "houseLeader", -1);
            Scribe_Values.Look(ref pendingNewHouse, "pendingNewHouse");
            Scribe_Values.Look(ref pendingOldHouse, "pendingOldHouse");
            Scribe_Values.Look(ref pendingOldGenerations, "pendingOldGenerations");
            Scribe_Collections.Look(ref rulerTraits, "rulerTraits", LookMode.Def);
            Scribe_Values.Look(ref goal, "goal");
            Scribe_Values.Look(ref goalTargetId, "goalTarget", -1);
            Scribe_Values.Look(ref goalSince, "goalSince");
            Scribe_Values.Look(ref goalProgress, "goalProgress");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                traits ??= new List<FactionTraitDef>();
                traits.RemoveAll(t => t == null);
                grievances ??= new List<Grievance>();
                rulerTraits ??= new List<RulerTraitDef>();
                rulerFriends ??= new List<int>();
                rulerTraits.RemoveAll(t => t == null);
                schemaVersion = Schema;
            }
        }

        /// <summary>Picks traits for a new faction (deterministic per faction).</summary>
        public void RollTraits(Faction f)
        {
            traits.Clear();
            Rand.PushState(f.loadID * 7919 + f.randomKey);
            try
            {
                int want = 2;
                for (int i = 0; i < want; i++)
                {
                    var candidates = new List<FactionTraitDef>();
                    foreach (FactionTraitDef t in DefDatabase<FactionTraitDef>.AllDefsListForReading)
                    {
                        if (traits.Contains(t) || !t.CanApplyTo(f)) continue;
                        bool clash = false;
                        foreach (FactionTraitDef have in traits)
                            if (have.conflictsWith.Contains(t) || t.conflictsWith.Contains(have)) clash = true;
                        if (!clash) candidates.Add(t);
                    }
                    if (candidates.TryRandomElementByWeight(t => t.commonality, out FactionTraitDef pick)) traits.Add(pick);
                }
            }
            finally
            {
                Rand.PopState();
            }
        }
    }
}
