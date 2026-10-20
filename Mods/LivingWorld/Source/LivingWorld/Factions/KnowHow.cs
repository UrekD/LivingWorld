using System.Linq;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Factions learn: a faction's know-how can rise above its kind's tech level (up to two ages, never past Ultra).
    /// Prosperity drives it slowly, trade with more advanced factions faster, taking a more advanced town gives a jump,
    /// famine sets it back. The effective tech level feeds the economy (production, how many people the land carries),
    /// battles, diplomacy, and the weapons raiders carry onto the colony map. The faction's kind (def) never changes.
    /// </summary>
    public static class KnowHow
    {
        public const int MaxBonus = 2;
        /// <summary>Per day at full prosperity factor, alone: an age in about six to seven years at ordinary prosperity.</summary>
        public const float BaseGain = 0.004f;
        /// <summary>Per day with full contact (trade) with a more advanced faction: an age in about three years together.</summary>
        public const float ContactGain = 0.004f;

        public static TechLevel Effective(Faction f)
        {
            if (f?.def == null) return TechLevel.Undefined;
            TechLevel baseTech = f.def.techLevel;
            FactionState fs = LivingWorldComponent.Instance?.StateFor(f);
            if (fs == null || fs.techBonus <= 0 || baseTech < TechLevel.Neolithic) return baseTech;
            return (TechLevel)Mathf.Min((int)baseTech + fs.techBonus, (int)TechLevel.Ultra);
        }

        private static bool CanLearn(FactionState fs) =>
            fs.faction != null && !fs.faction.defeated && fs.faction.def.techLevel >= TechLevel.Neolithic && fs.techBonus < MaxBonus
            && (int)fs.faction.def.techLevel + fs.techBonus < (int)TechLevel.Ultra;

        public static void DailyStep(LivingWorldComponent comp, float pace)
        {
            foreach (FactionState fs in comp.factionStates)
            {
                if (!CanLearn(fs) || fs.settlements.Count == 0) continue;
                float prosperity = 0f, hunger = 0f;
                foreach (CompSettlementLife s in fs.settlements)
                {
                    prosperity += s.prosperity;
                    hunger += s.hunger;
                }
                prosperity /= fs.settlements.Count;
                hunger /= fs.settlements.Count;
                // Contact: trade with factions that know more.
                TechLevel mine = Effective(fs.faction);
                float contact = 0f;
                foreach (FactionState o in comp.factionStates)
                    if (o != fs && o.faction != null && Effective(o.faction) > mine)
                        contact += Diplomacy.TradeBetween(comp, fs.faction, o.faction) / 2000f;
                float gain = BaseGain * Mathf.Clamp01((prosperity - 0.35f) / 0.4f) + ContactGain * Mathf.Min(1f, contact) - (hunger > 0.15f ? 0.002f : 0f);
                fs.techProgress = Mathf.Max(0f, fs.techProgress + gain * pace);
                if (fs.techProgress >= 1f) Advance(comp, fs);
            }
        }

        /// <summary>A town of a more advanced faction was taken: its craftsmen teach the conquerors.</summary>
        public static void Notify_Captured(LivingWorldComponent comp, Faction conqueror, Faction loser)
        {
            FactionState fs = comp?.StateFor(conqueror);
            if (fs == null || loser == null || !CanLearn(fs) || Effective(loser) <= Effective(conqueror)) return;
            fs.techProgress += 0.2f;
            if (fs.techProgress >= 1f) Advance(comp, fs);
        }

        private static void Advance(LivingWorldComponent comp, FactionState fs)
        {
            fs.techBonus++;
            fs.techProgress = 0f;
            foreach (CompSettlementLife s in fs.settlements) SettlementEconomy.RecomputeProfile(s);
            Faction f = fs.faction;
            ChronicleEventDef def = LWChronicleDefOf.LW_KnowHowGained;
            comp.chronicle.Record(def, def.text.Formatted(f.Name.Named("FACTION"), Effective(f).ToStringHuman().Named("TECH"),
                f.def.techLevel.ToStringHuman().Named("OLD")).Resolve(), f);
            // Advance can run inside loops over the faction states (daily step, battles): the split happens after them.
            if (!pendingSplits.Contains(fs.factionId)) pendingSplits.Add(fs.factionId);
        }

        private static readonly List<int> pendingSplits = new List<int>();

        /// <summary>After the day's loops: factions that entered a new age this day may split (old ways).</summary>
        public static void ProcessSplits(LivingWorldComponent comp)
        {
            if (pendingSplits.Count == 0) return;
            var ids = new List<int>(pendingSplits);
            pendingSplits.Clear();
            foreach (int id in ids)
                if (comp.StateFor(comp.FactionById(id)) is FactionState fs) KeepTheOldWays(comp, fs);
        }

        public static void ResetStatics() => pendingSplits.Clear();

        /// <summary>Tests: the next advance always splits off the old ways.</summary>
        public static bool TestForceSplit;

        /// <summary>Tests: advance a faction now (as if its know-how had filled up).</summary>
        public static void TestAdvance(LivingWorldComponent comp, FactionState fs) => Advance(comp, fs);

        /// <summary>
        /// Not everyone follows: when a big faction enters a new age, its most traditional town (poor, far from the
        /// capital) may break away as a faction of the old kind, keeping the old ways, at war with the reformers.
        /// </summary>
        private static void KeepTheOldWays(LivingWorldComponent comp, FactionState fs)
        {
            Faction f = fs.faction;
            if (!LivingWorldMod.Settings.allowConquest || fs.settlements.Count < 5 || !Rebellion.CanSplit(f.def) || !Rebellion.CanRise(comp) || !(TestForceSplit || Rand.Chance(0.5f))) return;
            Settlement capital = SettlementOps.CapitalOf(f);
            CompSettlementLife pick = null;
            float best = float.MinValue;
            foreach (CompSettlementLife s in fs.settlements)
            {
                if (!s.Active || s.Settlement == capital || SettlementOps.Protected(s.Settlement) || !SettlementOps.CanFall(s.Settlement)) continue;
                float far = capital == null ? 0f : Find.WorldGrid.ApproxDistanceInTiles(capital.Tile, s.Settlement.Tile);
                float score = far - 30f * s.prosperity;
                if (score > best)
                {
                    best = score;
                    pick = s;
                }
            }
            if (pick != null) Rebellion.Secede(comp, pick, f, "LW_WarCause_OldWays".Translate().Resolve());
        }

        /// <summary>"Neolithic" or "Neolithic → Medieval" (+ progress toward the next age).</summary>
        public static string Label(FactionState fs)
        {
            Faction f = fs.faction;
            string s = f.def.techLevel.ToStringHuman().CapitalizeFirst();
            if (fs.techBonus > 0) s += " → " + Effective(f).ToStringHuman();
            if (CanLearn(fs) && fs.techProgress >= 0.05f)
                s += " " + "LW_KnowHow_Learning".Translate(((TechLevel)((int)Effective(f) + 1)).ToStringHuman(), fs.techProgress.ToStringPercent()).Resolve();
            return s;
        }

        /// <summary>Strength factor in battle from know-how above the faction's kind.</summary>
        public static float BattleFactor(Faction f) => 1f + 0.15f * (LivingWorldComponent.Instance?.StateFor(f)?.techBonus ?? 0);
    }

    /// <summary>
    /// Raiders of a faction that has learned beyond its kind bring better weapons: when the raid's lord is made, some
    /// raiders swap their weapon for one of the faction's effective age (ranged for ranged, melee for melee).
    /// </summary>
    public static class RaidGear
    {
        private static readonly Dictionary<(TechLevel, bool), List<ThingDef>> candidates = new Dictionary<(TechLevel, bool), List<ThingDef>>();
        public static int StatUpgrades;

        private static HashSet<string> commonTags;

        /// <summary>Weapon tags carried by the pawns of ordinary visible factions (not cults, entities, special crews).</summary>
        private static HashSet<string> CommonTags()
        {
            if (commonTags != null) return commonTags;
            commonTags = new HashSet<string>();
            foreach (FactionDef fd in DefDatabase<FactionDef>.AllDefsListForReading)
            {
                if (!fd.humanlikeFaction || fd.hidden || fd.isPlayer || fd.pawnGroupMakers == null) continue;
                foreach (PawnGroupMaker maker in fd.pawnGroupMakers)
                    foreach (PawnGenOption o in maker.options ?? new List<PawnGenOption>())
                        if (o.kind?.weaponTags != null) commonTags.UnionWith(o.kind.weaponTags);
            }
            return commonTags;
        }

        private static List<ThingDef> Candidates(TechLevel tech, bool ranged)
        {
            if (candidates.TryGetValue((tech, ranged), out List<ThingDef> list)) return list;
            list = new List<ThingDef>();
            HashSet<string> common = CommonTags();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                if (d.IsWeapon && d.techLevel == tech && d.equipmentType == EquipmentType.Primary && !d.weaponTags.NullOrEmpty()
                    && d.IsRangedWeapon == ranged && d.tradeability != Tradeability.None && !d.destroyOnDrop && d.BaseMarketValue < 2500f
                    && !d.defName.EndsWith("_Unique") && (d.comps == null || !d.comps.Any(c => c.compClass?.Name == "CompUniqueWeapon")) // no named unique weapons
                    && d.weaponTags.Any(common.Contains) // gear ordinary factions carry
                    && (d.Verbs == null || !d.Verbs.Any(v => v.verbClass?.Name == "Verb_ShootOneUse"))) // no single-use launchers
                    list.Add(d);
            candidates[(tech, ranged)] = list;
            return list;
        }

        public static void Notify_RaidLordMade(Verse.AI.Group.Lord lord)
        {
            FactionState fs = LivingWorldComponent.Instance?.StateFor(lord.faction);
            if (fs == null || fs.techBonus <= 0) return;
            TechLevel tech = KnowHow.Effective(lord.faction);
            int upgraded = 0;
            foreach (Pawn p in lord.ownedPawns)
                if (p.RaceProps.Humanlike && Rand.Chance(0.35f * fs.techBonus) && Upgrade(p, tech)) upgraded++;
            if (upgraded > 0 && Testing.AutoTest.Active) LWLog.Message($"[raid gear] {lord.faction.Name}: {upgraded} raiders carry {tech.ToStringHuman()} weapons");
        }

        /// <summary>Swaps the pawn's weapon for one of the given age; true if it changed.</summary>
        public static bool Upgrade(Pawn p, TechLevel tech)
        {
            ThingWithComps old = p.equipment?.Primary;
            if (old == null || old.def.techLevel >= tech) return false;
            // The most advanced age that has such weapons, at or below what they know (vanilla has no ultra guns).
            List<ThingDef> list = null;
            // Within reason: at most five times the old weapon's worth (a rifle becomes a charge rifle, not a doomsday launcher).
            float cap = Mathf.Max(200f, old.def.BaseMarketValue * 5f);
            for (TechLevel t = tech; t > old.def.techLevel && (list == null || list.Count == 0); t--) list = Candidates(t, old.def.IsRangedWeapon).Where(d => d.BaseMarketValue <= cap).ToList();
            if (list == null || list.Count == 0 || !list.TryRandomElementByWeight(d => 1f / (1f + d.BaseMarketValue / 200f), out ThingDef def)) return false;
            ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
            var weapon = (ThingWithComps)ThingMaker.MakeThing(def, stuff);
            p.equipment.DestroyEquipment(old);
            p.equipment.AddEquipment(weapon);
            StatUpgrades++;
            return true;
        }
    }
}
