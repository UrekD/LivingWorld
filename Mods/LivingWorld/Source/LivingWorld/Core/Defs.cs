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
        // Town maps (Settlements/TownMaps.cs); the defaults leave vanilla's generation as it is.
        /// <summary>Scales the width and height of the town's rect on its map (never below 20 cells).</summary>
        public float townMapRectScale = 1f;
        /// <summary>Scales the defenders' raid points of the tier's normal force.</summary>
        public float townMapPointsFactor = 1f;
        /// <summary>Scales the loot's market value.</summary>
        public float townMapLootFactor = 1f;
        /// <summary>Width of the edge defences (0 = open); -1 leaves vanilla's choice.</summary>
        public int townMapEdgeDefense = -1;
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
        // Roads (Systems/Roads.cs)
        /// <summary>Decayed goods delivered between two towns that make them want a road (calibrate: ~10–30 routes in a 10-year world).</summary>
        public float roadTrafficThreshold = 500f;
        public float roadTrafficDecayPerDay = 0.98f;
        public int roadCheckDays = 10;
        /// <summary>Path searches per check, world-wide.</summary>
        public int roadPlanAttempts = 3;
        public int roadNpcMaxEdges = 24;
        public int roadPlayerMaxEdges = 30;
        public int roadPlanBackoffDays = 60;
        public int roadAbandonBackoffDays = 120;
        public int roadOpenPerFaction = 2;
        public int roadOpenWorldwide = 20;
        public int roadBlockedDaysToAbandon = 30;
        public int roadDegradeDays = 30;
        /// <summary>Most often NPC road changes redraw the roads layer (a whole-planet rebuild) while the world view is drawn, in days; a player's link redraws at once.</summary>
        public int roadRedrawMinDays = 3;
        public float roadDegradeStep = 0.25f;
        /// <summary>Silver per laid link: dirt, stone; times the terrain factor (1 + step per hilliness level above flat).</summary>
        public float roadCostDirt = 20f;
        public float roadCostStone = 50f;
        public float roadTerrainStep = 0.25f;
        /// <summary>Silver an NPC faction keeps back: its routes are built only from the surplus above this, so roads never compete with founding (<c>Expansion</c> needs 600) or the military.</summary>
        public float roadTreasuryReserve = 800f;
        /// <summary>The player's caravan: work per link (at construction speed 1, in ticks), stone blocks per stone link, total XP per link.</summary>
        public float roadWorkPerEdge = 45000f;
        public int roadStoneBlocksPerEdge = 25;
        public float roadXpPerEdge = 1000f;
        /// <summary>Road links followed from a colony when deciding which towns it is joined to.</summary>
        public int roadConnectEdges = 60;
        public float roadNeighbourFactor = 1.5f;
        /// <summary>Fame a joined, friendly town gains every <see cref="roadFameDays"/>, up to the ceiling (never lowers fame above it).</summary>
        public float roadFameGain = 0.01f;
        public float roadFameCeiling = 0.5f;
        public int roadFameDays = 10;
        // Town maps (Settlements/CompSettlementLife.cs: scars)
        /// <summary>A lost attack shows in full this many days, then fades linearly to nothing at <see cref="townScarFadeDays"/>.</summary>
        public int townScarFullDays = 10;
        public int townScarFadeDays = 60;
        // World overlay (UI/WorldOverlay.cs)
        /// <summary>The prosperity heatmap changes a town's colour band only when its prosperity is this far past the band's edge (no flicker at an edge); the shortage heatmap the same for the price factor.</summary>
        public float overlayHeatHysteresis = 0.02f;
        public float overlayPriceHysteresis = 0.05f;
        /// <summary>The war fronts overlay marks battles (the chronicle's attacks on towns) of this many days back, fading with age.</summary>
        public int overlayBattleDays = 30;
        /// <summary>A line on the world map (a war band to its target) is drawn as great-circle segments of about this many tiles, so it follows the globe.</summary>
        public float overlayLineSegmentTiles = 3f;
        /// <summary>At most this many line segments (one draw call each) are drawn in a frame, over all lines.</summary>
        public int overlayLineMaxSegments = 400;
        /// <summary>The trade routes overlay draws a ribbon for the busiest this many pairs of towns, and only for pairs whose traffic (goods delivered, decaying daily) is at least the minimum.</summary>
        public int overlayTradePairs = 150;
        public float overlayTradeMinTraffic = 50f;
        // Town maps: defenders and loot follow the town (Settlements/TownMaps.cs)
        /// <summary>Raid points of a normal town defence: the middle of vanilla's 1150-1600.</summary>
        public float townMapBasePoints = 1375f;
        /// <summary>The garrison against what it regrows toward (war footing included, the pay cut not), bounded: a depleted town is no weaker than this, a reinforced one no stronger.</summary>
        public float townMapMinStrength = 0.7f;
        public float townMapMaxStrength = 1.1f;
        /// <summary>The faction's militarization (traits, permanent enemies) as it counts for defenders, bounded.</summary>
        public float townMapMilMin = 0.85f;
        public float townMapMilMax = 1.15f;
        /// <summary>A faction at war keeps this many times the defenders at home (the garrison's own war footing is already in the strength ratio).</summary>
        public float townMapWarFactor = 1.1f;
        /// <summary>Safety cap on the defenders' points (twice vanilla's most).</summary>
        public float townMapMaxPoints = 3200f;
        /// <summary>Market value of the loot never goes above this (vanilla: 1800).</summary>
        public float townMapMaxLoot = 3000f;
        /// <summary>Planned points below x: "a handful"; below y: "a company"; below z: "a strong garrison"; else "a fortress".</summary>
        public Vector3 townMapDefenderWords = new Vector3(1000f, 1300f, 1700f);
        // Town maps: the state a town's map shows (Settlements/TownMaps.cs, post pass)
        /// <summary>Food in the stockpiles of a town in famine is cut to this share on top of the thinning by stock.</summary>
        public float townFamineFoodFactor = 0.3f;
        /// <summary>The thinning of a stockpile never keeps less than this share of a commodity (before the famine cut).</summary>
        public float townMinStockScale = 0.05f;
        /// <summary>A commodity at this share of its target stock (or more) keeps the vanilla stockpile; below it the stockpile thins in proportion.</summary>
        public float townStockNormal = 1.3f;
        /// <summary>The most severe malnutrition famine defenders start with (0.4 would be the moderate stage).</summary>
        public float townMalnutritionMax = 0.25f;
        // Tributaries (Story/Tributaries.cs): a conquered town kept as a free town allied to the colony
        /// <summary>Tributary records live at once (held towns, and towns that yielded and wait for the colonists to leave).</summary>
        public int tributaryMax = 6;
        /// <summary>Client factions ever generated in one game (a town that submits again reuses its faction and costs none).</summary>
        public int tributaryFactionBudget = 10;
        /// <summary>Ended records kept for the factions page, at most this many (and not beyond three years).</summary>
        public int tributaryHistoryMax = 24;
        /// <summary>The goodwill a new free town starts with towards the colony (held back by whatever caps it).</summary>
        public int tributaryStartGoodwill = 80;
        /// <summary>A yielded town with a live quest waits this long for it to end after its map is gone, then stays with its old masters.</summary>
        public int tributaryTransferWaitDays = 15;
        // Tribute and loyalty (Story/Tributaries.cs): lists are indexed by the rate (suspended, light, normal, heavy)
        /// <summary>Tribute accrues daily and is paid every this many days of tribute (the rate can also change once in this time).</summary>
        public int tributeIntervalDays = 15;
        /// <summary>The share of the town's daily income the colony takes, by rate.</summary>
        public List<float> tributeShare = new List<float> { 0f, 0.25f, 0.5f, 0.8f };
        /// <summary>The tributary term of the town's unrest target (it includes being a subject at all), by rate.</summary>
        public List<float> tributeUnrest = new List<float> { 0.05f, 0.17f, 0.29f, 0.52f };
        /// <summary>The share of its target garrison a tributary's faction pays for, by rate.</summary>
        public List<float> tributeGarrisonFunding = new List<float> { 1f, 1f, 0.9f, 0.75f };
        /// <summary>Tribute is never taken from the part of the client's treasury below this.</summary>
        public float tributeTreasuryReserve = 100f;
        /// <summary>The goods in one payment are worth at most this much silver.</summary>
        public float tributeGoodsMaxValue = 400f;
        /// <summary>A held town this restless warns the colony (at most every ten days), and above the revolt line it may rise, at this daily chance per point above it.</summary>
        public float tributaryWarnUnrest = 0.45f;
        public float tributaryRevoltUnrest = 0.55f;
        public float tributaryRevoltHazard = 0.2f;
        /// <summary>A town that rises goes back to its old masters with this chance (if they live and fight its client), else it breaks free.</summary>
        public float tributaryLiberateShare = 0.4f;
        /// <summary>Terms of the unrest target: defenders paid for (A5), neglect (A5), and a road to the colony.</summary>
        public float tributaryProtectedTerm = -0.10f;
        public float tributaryAbandonedTerm = 0.15f;
        public float tributaryRoadTerm = -0.03f;
        /// <summary>A gift to a held town costs this much silver per inhabitant.</summary>
        public float tributaryGiftPerCapita = 2f;
        // Trade-route planner (Economy/TradePlanner.cs)
        /// <summary>The planner looks at this many of the nearest towns that can be reached and will trade.</summary>
        public int plannerTowns = 40;
        /// <summary>Path searches (a cold reachability check counts as one) the planner may make in a frame, or this many milliseconds, whichever comes first.</summary>
        public int plannerPathsPerFrame = 3;
        public float plannerPathMsPerFrame = 2f;
        /// <summary>"Where to sell" lists a good only where the player's sell price is at least this multiple of a normal price, and the town is within this many days.</summary>
        public float plannerMinSellFactor = 1.25f;
        public float plannerMaxDays = 20f;
        /// <summary>A trade route is listed when the estimated sell price at one town is at least this multiple of the buy price at the other (vanilla's own markup included).</summary>
        public float plannerMinProfit = 1.10f;
        // Hired war bands (Story/HiredBands.cs)
        /// <summary>The share of the source town's garrison a hired band is made of, and the price: silver per point of strength (before the hirer's temper), at least the minimum.</summary>
        public float hireShare = 0.5f;
        public float hirePricePerPoint = 1.5f;
        public int hireMinPrice = 250;
        /// <summary>The least goodwill with the colony a faction needs to be hired.</summary>
        public int hireMinGoodwill = 0;
        /// <summary>Contracts under way (envoy out or band marching): at most this many in all, and per hired faction.</summary>
        public int hireMaxActive = 2;
        public int hireMaxPerHirer = 1;
        /// <summary>The share of the fee that comes back, once, when a band is called off before it fights.</summary>
        public float hireRefundShare = 0.5f;
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
        public static ChronicleEventDef LW_PlayerHiredBand;
        public static ChronicleEventDef LW_RoadJoined;
        public static ChronicleEventDef LW_ColonyRoadJoined;
        public static ChronicleEventDef LW_PlayerTookTown;
        public static ChronicleEventDef LW_TributaryLost;
        public static ChronicleEventDef LW_TributaryReleased;
        public static ChronicleEventDef LW_TributaryRevolted;

        static LWChronicleDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(LWChronicleDefOf));
    }

    public static class Tuning
    {
        public static LivingWorldTuningDef T => LWDefOf.LW_Tuning;
    }
}
