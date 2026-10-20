using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Wars between NPC factions: settlements with men to spare send war bands against enemy settlements in reach;
    /// battles are abstract (strength × fortification × luck) and end in a repulse, plunder, capture or razing.
    /// </summary>
    public static class Military
    {
        public const float MaxRaidRange = 16f;
        private static readonly List<CompSettlementLife> near = new List<CompSettlementLife>();

        public static void DailyStep(LivingWorldComponent comp, float pace)
        {
            if (!LivingWorldMod.Settings.simulateWarfare) return;
            foreach (FactionState fs in comp.factionStates)
            {
                if (fs.faction == null || fs.faction.defeated || fs.settlements.Count == 0) continue;
                if (PartyBudget.CountOf(LWPartyDefOf.LW_WarBand, fs.faction) >= 3) continue;
                // About one expedition every two weeks for an ordinary faction at war.
                float eagerness = 0.07f * pace * fs.TraitFactor(t => t.warPropensity) * (1f - 0.6f * fs.warWeariness);
                if (!Rand.Chance(eagerness)) continue;
                CompSettlementLife from = null, target = null;
                foreach (CompSettlementLife s in fs.settlements)
                {
                    // Indexes are daily: skip towns lost or destroyed earlier today.
                    if (!s.Active || s.Settlement.Faction != fs.faction) continue;
                    if (s.garrison < 120f || s.garrison < SettlementEconomy.TargetGarrison(s, fs) * 0.6f) continue;
                    if (from != null && s.garrison <= from.garrison) continue;
                    CompSettlementLife t = PickTarget(comp, fs, s);
                    if (t == null) continue;
                    from = s;
                    target = t;
                }
                if (from != null) Attack(comp, fs, from, target, from.garrison * Rand.Range(0.4f, 0.6f));
            }
        }

        private static CompSettlementLife PickTarget(LivingWorldComponent comp, FactionState fs, CompSettlementLife from)
        {
            CompSettlementLife best = null;
            float bestW = 0f;
            PlanetTile tile = from.Settlement.Tile;
            comp.spatial.Near(tile, MaxRaidRange, near);
            foreach (CompSettlementLife t in near)
            {
                if (!t.Active) continue;
                Faction tf = t.Settlement.Faction;
                if (tf == fs.faction || !fs.faction.HostileTo(tf)) continue;
                float d = Find.WorldGrid.ApproxDistanceInTiles(tile, t.Settlement.Tile);
                if (d > MaxRaidRange) continue;
                float w = (t.prosperity + 0.3f) * (1f + t.Stock(LWDefOf.LW_Food) / 500f) / ((t.garrison + 60f) * Mathf.Pow(d + 3f, 1.5f));
                if (fs.GoalAgainst(tf)) w *= 2f; // the ruler's sworn enemy
                if (w > bestW && Find.WorldReachability.CanReach(tile, t.Settlement.Tile))
                {
                    bestW = w;
                    best = t;
                }
            }
            return best;
        }

        public static void Attack(LivingWorldComponent comp, FactionState fs, CompSettlementLife from, CompSettlementLife target, float strength)
        {
            from.garrison -= strength;
            if (PartyBudget.CanLaunchNpc())
            {
                WorldParty p = WorldParty.Launch(LWPartyDefOf.LW_WarBand, fs.faction, from.Settlement, target.Settlement);
                p.strength = strength;
                p.strengthPaid = strength;
                return;
            }
            // No room on the map: the battle happens at once and the survivors are home again.
            BattleResult r = Battle.Resolve(fs.faction, from.Settlement, target, strength);
            from.garrison += r.attackerSurvivors;
            FactionState home = comp.StateFor(fs.faction);
            if (home != null) home.treasury += r.loot;
        }
    }

    public struct BattleResult
    {
        public bool attackerWon;
        public float attackerSurvivors;
        public float loot;
        public float[] plunder;
        public bool captured, razed;
    }

    public static class Battle
    {
        /// <summary>An attack on a settlement. Applies everything to the defender and the war; returns what the attacker keeps.</summary>
        public static BattleResult Resolve(Faction attacker, Settlement origin, CompSettlementLife target, float strength)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Settlement ts = target.Settlement;
            Faction defender = ts.Faction;
            FactionState fa = comp.StateFor(attacker), fd = comp.StateFor(defender);
            float att = strength * Rand.Range(0.75f, 1.25f) * (fa?.TraitFactor(t => t.militarization) ?? 1f) * KnowHow.BattleFactor(attacker);
            float hills = ts.Tile.Tile?.hilliness >= Hilliness.LargeHills ? 1.2f : 1f;
            float def = (target.garrison + 10f) * target.Tier.fortification * hills * Rand.Range(0.75f, 1.25f) * KnowHow.BattleFactor(defender);
            War war = defender != null ? Diplomacy.ActiveWar(comp, attacker, defender) : null;
            var r = new BattleResult { plunder = new float[Commodities.Count] };
            ChronicleEventDef ce;
            string text;
            if (att > def)
            {
                r.attackerWon = true;
                r.attackerSurvivors = strength * Mathf.Clamp(1f - 0.5f * def / att, 0.3f, 0.95f);
                float lost = target.garrison * Rand.Range(0.7f, 0.9f);
                target.garrison -= lost;
                target.population *= 1f - Rand.Range(0.03f, 0.1f);
                target.prosperity = Mathf.Clamp01(target.prosperity - 0.15f);
                target.unrest = Mathf.Clamp01(target.unrest + 0.1f);
                for (int i = 0; i < target.stock.Length; i++)
                {
                    float take = target.stock[i] * Rand.Range(0.15f, 0.35f);
                    target.stock[i] -= take;
                    r.plunder[i] = take;
                }
                if (fd != null)
                {
                    r.loot = fd.treasury * 0.08f;
                    fd.treasury -= r.loot;
                    Diplomacy.AddGrudge(fd, attacker, 15f, "LW_Grudge_Plundered".Translate(ts.Label).Resolve());
                }
                war?.AddScore(attacker.loadID, lost + 50f);
                float ratio = att / def;
                bool marauder = attacker.def.permanentEnemy || (fa != null && fa.TraitFactor(t => t.tradeVolume) < 0.5f);
                if (SettlementOps.CanFall(ts) && ratio > 1.5f)
                {
                    float razeChance = marauder ? 0.12f : 0.015f;
                    // Big factions overreach: each town beyond the typical size makes holding a new one harder.
                    float overreach = fa == null ? 1f : 1f / (1f + 0.15f * Mathf.Max(0f, fa.settlements.Count - LivingWorldComponent.Instance.initialAvgSettlements));
                    float captureChance = attacker.def.permanentEnemy ? 0f : 0.35f * (fa?.TraitFactor(t => t.expansion) ?? 1f) * Mathf.Clamp01(ratio - 1.4f) * overreach;
                    if (Rand.Chance(razeChance)) r.razed = true;
                    else if (Rand.Chance(captureChance)) r.captured = true;
                }
                if (r.razed)
                {
                    ce = LWChronicleDefOf.LW_SettlementRazed;
                    text = ce.text.Formatted(attacker.Name.Named("ATTACKER"), ts.Label.Named("SETTLEMENT"), defender.Name.Named("DEFENDER")).Resolve();
                    comp.chronicle.Record(ce, text, attacker, defender, ts, causeId: war?.chronicleId ?? -1);
                    Roots.Notify(ts, HomeNews.Razed);
                    Refugees.Notify_Disaster(ts, razed: true);
                    SettlementOps.Raze(ts, attacker);
                }
                else if (r.captured)
                {
                    ce = LWChronicleDefOf.LW_SettlementCaptured;
                    text = ce.text.Formatted(attacker.Name.Named("ATTACKER"), ts.Label.Named("SETTLEMENT"), defender.Name.Named("DEFENDER")).Resolve();
                    comp.chronicle.Record(ce, text, attacker, defender, ts, causeId: war?.chronicleId ?? -1);
                    // Part of the war band stays as the new garrison.
                    target.garrison = r.attackerSurvivors * 0.6f;
                    r.attackerSurvivors *= 0.4f;
                    Roots.Notify(ts, HomeNews.Conquered);
                    SettlementOps.Capture(ts, attacker);
                }
                else
                {
                    ce = LWChronicleDefOf.LW_SettlementPlundered;
                    text = ce.text.Formatted(attacker.Name.Named("ATTACKER"), ts.Label.Named("SETTLEMENT"), defender.Name.Named("DEFENDER"),
                        (origin?.Label ?? "?").Named("ORIGIN")).Resolve();
                    comp.chronicle.Record(ce, text, attacker, defender, ts, causeId: war?.chronicleId ?? -1);
                    Roots.Notify(ts, HomeNews.Plundered);
                }
            }
            else
            {
                r.attackerSurvivors = strength * Rand.Range(0.1f, 0.5f);
                target.garrison = Mathf.Max(0f, target.garrison - att * Rand.Range(0.2f, 0.5f));
                war?.AddScore(defender.loadID, strength - r.attackerSurvivors + 30f);
                ce = LWChronicleDefOf.LW_AttackRepelled;
                text = ce.text.Formatted(defender.Name.Named("DEFENDER"), ts.Label.Named("SETTLEMENT"), attacker.Name.Named("ATTACKER")).Resolve();
                comp.chronicle.Record(ce, text, defender, attacker, ts, causeId: war?.chronicleId ?? -1);
            }
            if (fa != null) fa.warWeariness = Mathf.Clamp01(fa.warWeariness + (r.attackerWon ? 0.01f : 0.04f));
            if (fd != null) fd.warWeariness = Mathf.Clamp01(fd.warWeariness + (r.attackerWon ? 0.05f : 0.01f));
            return r;
        }
    }
}
