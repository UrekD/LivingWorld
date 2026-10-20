using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>A traded good class in the world economy (food, weapons, …). Stock and prices are tracked per settlement.</summary>
    public class CommodityDef : Def
    {
        /// <summary>Things in these categories (recursively) count as this commodity in trade.</summary>
        public List<ThingCategoryDef> thingCategories = new List<ThingCategoryDef>();
        public List<ThingDef> thingDefs = new List<ThingDef>();
        /// <summary>Daily output per inhabitant before modifiers, in silver of value.</summary>
        public float productionPerCapita;
        /// <summary>Daily use per inhabitant, in silver of value.</summary>
        public float consumptionPerCapita;
        /// <summary>Production multiplier by faction tech level (x = (int)TechLevel).</summary>
        public SimpleCurve productionByTech;
        public SimpleCurve consumptionByTech;
        // Tile drivers (each 0..1) add weight × driver to the production multiplier.
        public float fertilityWeight;
        public float wildlifeWeight;
        public float hillsWeight;
        public float coastWeight;
        public float riverWeight;
        /// <summary>Land-bound goods: output per head falls as the settlement fills its land (pop / capacity).</summary>
        public float crowdingSensitivity;
        /// <summary>How much output follows the growing season (0: the same all year). The yearly total is unchanged.</summary>
        public float seasonality;
        /// <summary>Concrete goods a merchant may commission from the colony when their town is short of this (People/Commissions).</summary>
        public List<CommissionGood> commissionGoods = new List<CommissionGood>();
        /// <summary>Days of consumption a settlement wants in stock; stock beyond maxStockDays spoils or is used up.</summary>
        public float targetStockDays = 10f;
        public float maxStockDays = 40f;
        /// <summary>Price factor = clamp((target / stock) ^ elasticity).</summary>
        public float elasticity = 0.5f;
        /// <summary>Consumption multiplier while the owning faction is at war.</summary>
        public float warDemandFactor = 1f;
        /// <summary>Running out means hunger: population and prosperity fall.</summary>
        public bool essential;
        public string specialtyLabel;
        public Color color = Color.white;
        /// <summary>Silver worth of one unit (one unit of food feeds a person for a day); converts player trade.</summary>
        public float silverPerUnit = 20f;

        public float ProductionTechFactor(TechLevel tech) => productionByTech?.Evaluate((int)tech) ?? 1f;
        public float ConsumptionTechFactor(TechLevel tech) => consumptionByTech?.Evaluate((int)tech) ?? 1f;
    }

    /// <summary>A thing and how many make one commission.</summary>
    public class CommissionGood
    {
        public ThingDef thing;
        public int count = 1;
    }

    /// <summary>Multipliers on what a faction tends to do, shared by faction traits and ruler traits.</summary>
    public abstract class TemperamentDef : Def
    {
        public float commonality = 1f;
        public float warPropensity = 1f;
        public float tradeVolume = 1f;
        public float expansion = 1f;
        public float treatyKeeping = 1f;
        public float militarization = 1f;
        public float playerTolerance = 1f;
        public float growth = 1f;
    }

    /// <summary>A temperament a faction can have; multipliers on what the faction tends to do.</summary>
    public class FactionTraitDef : TemperamentDef
    {
        public TechLevel minTech = TechLevel.Undefined;
        public TechLevel maxTech = TechLevel.Archotech;
        /// <summary>Only for (or never for) permanent enemies such as pirates.</summary>
        public bool? permanentEnemy;
        public List<FactionTraitDef> conflictsWith = new List<FactionTraitDef>();

        public bool CanApplyTo(Faction f)
        {
            TechLevel t = f.def.techLevel;
            if (t < minTech || t > maxTech) return false;
            if (permanentEnemy.HasValue && permanentEnemy.Value != f.def.permanentEnemy) return false;
            return true;
        }
    }

    /// <summary>The temperament of a faction's current ruler (two per ruler, drawn at accession), and what it makes them want.</summary>
    public class RulerTraitDef : TemperamentDef
    {
        public List<RulerTraitDef> conflictsWith = new List<RulerTraitDef>();
        /// <summary>Weights for the ruler's goal at accession (multiplied over both traits).</summary>
        public float conquerWeight = 1f;
        public float avengeWeight = 1f;
        public float prosperWeight = 1f;
        public float expandWeight = 1f;
    }

    /// <summary>Size class of a settlement (hamlet … city), chosen by population.</summary>
    public class SettlementTierDef : Def
    {
        public int minPopulation;
        public int order;
        /// <summary>Garrison strength points per inhabitant at militarization 1.</summary>
        public float garrisonPerCapita = 2f;
        /// <summary>Multiplier on trader stock size and caravan capacity.</summary>
        public float tradeFactor = 1f;
        /// <summary>Defense multiplier in battles (walls, towers).</summary>
        public float fortification = 1f;
    }

    public enum ChronicleImportance : byte { Minor, Notable, Major }

    public enum ChronicleCategory : byte { Settlement, Economy, Diplomacy, War, People, Player, Disaster }

    /// <summary>A kind of world event recorded in the chronicle. <c>text</c> takes named arguments in braces.</summary>
    public class ChronicleEventDef : Def
    {
        [MustTranslate] public string text;
        public ChronicleImportance importance = ChronicleImportance.Minor;
        public ChronicleCategory category;
        /// <summary>Letter used when the event is sent individually (involves the player, or letter policy "all").</summary>
        public LetterDef letterDef;
        /// <summary>Events that involve the player get their own letter (otherwise they only go to the digest).</summary>
        public bool letterWhenPlayerInvolved = true;
    }

    /// <summary>All numeric tuning of the simulation, in one def (LW_Tuning) so balance needs no recompile.</summary>
    public class LivingWorldTuningDef : Def
    {
        // Population
        public FloatRange initialPopulationByTier = new FloatRange(20f, 400f);
        public float baseGrowthPerDay = 0.004f;
        public float famineDeclinePerDay = 0.06f;
        public int minPopulation = 8;
        public int maxPopulation = 1500;
        // Prosperity
        public float prosperityDriftPerDay = 0.03f;
        // Garrison
        public float garrisonRegenPerDay = 0.05f;
        public float garrisonUpkeepPerPoint = 0.02f;
        // Treasury
        public float taxPerCapitaPerDay = 0.15f;
        public float tradeTaxRate = 0.1f;
        // Markets
        public float minPriceFactor = 0.5f;
        public float maxPriceFactor = 2.5f;
        // Chronicle
        public int maxMinorEntries = 1500;
        public int digestMaxLines = 12;
    }

    [DefOf]
    public static class LWDefOf
    {
        public static LivingWorldTuningDef LW_Tuning;
        public static CommodityDef LW_Food;

        static LWDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWDefOf));
    }

    [DefOf]
    public static class LWChronicleDefOf
    {
        public static ChronicleEventDef LW_Unknown;
        public static ChronicleEventDef LW_SettlementGrew;
        public static ChronicleEventDef LW_SettlementShrank;
        public static ChronicleEventDef LW_Famine;
        public static ChronicleEventDef LW_LeaderChanged;
        public static ChronicleEventDef LW_WarBandMarches;
        public static ChronicleEventDef LW_RaidEnded;
        public static ChronicleEventDef LW_WarDeclared;
        public static ChronicleEventDef LW_JoinedWar;
        public static ChronicleEventDef LW_PeaceMade;
        public static ChronicleEventDef LW_AllianceFormed;
        public static ChronicleEventDef LW_WarEnded;
        public static ChronicleEventDef LW_FactionDefeated;
        public static ChronicleEventDef LW_SettlementRazed;
        public static ChronicleEventDef LW_SettlementCaptured;
        public static ChronicleEventDef LW_SettlementPlundered;
        public static ChronicleEventDef LW_AttackRepelled;
        public static ChronicleEventDef LW_SettlementFounded;
        public static ChronicleEventDef LW_RuinResettled;
        public static ChronicleEventDef LW_StoryBegins;
        public static ChronicleEventDef LW_WarlordFell;
        public static ChronicleEventDef LW_FeudSworn;
        public static ChronicleEventDef LW_NewHouse;
        public static ChronicleEventDef LW_FigureRenowned;
        public static ChronicleEventDef LW_FigureRetired;
        public static ChronicleEventDef LW_FeudAvenged;
        public static ChronicleEventDef LW_FeudEnded;
        public static ChronicleEventDef LW_FeudBroken;
        public static ChronicleEventDef LW_FeudSettled;
        public static ChronicleEventDef LW_FigureDied;
        public static ChronicleEventDef LW_FigureJoined;
        public static ChronicleEventDef LW_FigureCaptured;
        public static ChronicleEventDef LW_GuestsPraised;
        public static ChronicleEventDef LW_GuestsComplained;
        public static ChronicleEventDef LW_WarBandIntercepted;
        public static ChronicleEventDef LW_AidDelivered;
        public static ChronicleEventDef LW_Plague;
        public static ChronicleEventDef LW_Blight;
        public static ChronicleEventDef LW_GreatFair;
        public static ChronicleEventDef LW_Rebellion;
        public static ChronicleEventDef LW_TownLiberated;
        public static ChronicleEventDef LW_PlayerDestroyedSettlement;
        public static ChronicleEventDef LW_RulerFell;
        public static ChronicleEventDef LW_WarRequestDone;
        public static ChronicleEventDef LW_PlayerPeace;
        public static ChronicleEventDef LW_RulerVow;
        public static ChronicleEventDef LW_RulerGoalDone;
        public static ChronicleEventDef LW_RulerNewGoal;
        public static ChronicleEventDef LW_RulerVisited;
        public static ChronicleEventDef LW_PrisonerRansomed;
        public static ChronicleEventDef LW_KnowHowGained;
        public static ChronicleEventDef LW_NewPeopleArrive;
        public static ChronicleEventDef LW_CommissionDelivered;
        public static ChronicleEventDef LW_PrisonerDied;
        public static ChronicleEventDef LW_PrisonerReleased;
        public static ChronicleEventDef LW_RulerGoalAbandoned;
        public static ChronicleEventDef LW_PlayerEnvoyKilled;
        public static ChronicleEventDef LW_PlayerCallToArms;
        public static ChronicleEventDef LW_PlayerTributePaid;
        public static ChronicleEventDef LW_PlayerTributeRefused;

        static LWChronicleDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWChronicleDefOf));
    }

    public static class Tuning
    {
        public static LivingWorldTuningDef T => LWDefOf.LW_Tuning;
    }
}
