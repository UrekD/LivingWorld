using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Town maps follow the world (plan "townmaps", and the end of the normal and history plans).
    /// The scars data: the severity of a lost attack, how a scar fades, that a new one never lightens an older one that still shows, and that
    /// a won attack reaches the town's comp through the real <see cref="Battle.Resolve"/> while a repulse does not.
    /// The map parameters: the points bounds, the quest relation and the inspect words (every plan above), and, in the plan "townmaps" only,
    /// real maps of one town in several states (size, defenders, loot, switched off, bare gen step, skipped resolver, Empire).
    /// The state post pass: the thinning shares and the hunger severity (every plan), and, in the plan "townmaps", maps of a sacked and starving town (twice,
    /// for determinism), a healthy one, one whose scar has faded and one made without the loot hook.
    /// Each case restores what it touched; the world's chronicle entries made by the battle are removed again, and generated maps are removed
    /// with the pawns they made.
    /// </summary>
    public static class TownMapChecks
    {
        private const string Tag = "[LivingWorld:test] townmap: ";
        private const float Eps = 0.001f;

        public static void Register() => AutoTest.Checks.Add(Controlled);

        /// <summary>What the player lands among, logged when the world's history is done: towns whose last lost attack still shows, and the defenders' points by tier.</summary>
        internal static void LogLanding(LivingWorldComponent comp)
        {
            LogScarred(comp);
            Log.Message($"[LivingWorld:history] {TownMaps.PointsDistribution(comp)}");
            Log.Message($"[LivingWorld:townmap] {TownMaps.StockDistribution(comp)}");
            WorldChecks.LogTotals(comp);
        }

        private static void LogScarred(LivingWorldComponent comp)
        {
            int n = 0;
            float max = 0f;
            foreach (CompSettlementLife s in comp.activeSettlements)
            {
                float scar = s.Scar();
                if (scar < CompSettlementLife.ScarVisible) continue;
                n++;
                max = Math.Max(max, scar);
            }
            Log.Message($"[LivingWorld:history] scarred towns at landing: {n} (max {max:F2})");
        }

        public static IEnumerable<string> Controlled()
        {
            var bad = new List<string>();
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (c == null || Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") == "1") return bad;
            if (AutoTest.Plan != "normal" && AutoTest.Plan != "history" && AutoTest.Plan != "townmaps") return bad;
            Guard(bad, "sack severity", () => SackSeverity(bad));
            Guard(bad, "scar fade", () => ScarFade(bad));
            Guard(bad, "notify sacked", () => NotifySacked(bad));
            Guard(bad, "battle hook", () => BattleHook(bad, c));
            Guard(bad, "hooks", () => Hooks(bad));
            Guard(bad, "points bounds", () => PointsBounds(bad, c));
            Guard(bad, "defender words", () => DefenderWords(bad, c));
            Guard(bad, "quest relation", () => QuestRelation(bad));
            Guard(bad, "state units", () => StateUnits(bad));
            if (AutoTest.Plan != "history") Guard(bad, "stock distribution", () => Log.Message($"[LivingWorld:townmap] {TownMaps.StockDistribution(c)}"));
            if (AutoTest.Plan == "townmaps") Guard(bad, "maps", () => Maps(bad, c));
            return bad;
        }

        /// <summary>One case failing (even by exception) must not stop the others.</summary>
        private static void Guard(List<string> bad, string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception e)
            {
                bad.Add($"townmap ({name}): {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool Near(float a, float b) => Math.Abs(a - b) < Eps;

        // ---- Unit cases ----

        /// <summary>Even match 0.35, twice the defence 0.85, captured 1, never past 1.</summary>
        private static void SackSeverity(List<string> bad)
        {
            Expect(bad, "ratio 1.0", Battle.SackSeverity(1f, false), 0.35f);
            Expect(bad, "ratio 2.0", Battle.SackSeverity(2f, false), 0.85f);
            Expect(bad, "ratio 5.0", Battle.SackSeverity(5f, false), 1f);
            Expect(bad, "captured at ratio 1.2", Battle.SackSeverity(1.2f, true), 1f);
            Expect(bad, "captured at ratio 3", Battle.SackSeverity(3f, true), 1f);
        }

        private static void Expect(List<string> bad, string what, float got, float want)
        {
            if (!Near(got, want)) bad.Add($"townmap (severity {what}): {got:F3} instead of {want:F3}");
        }

        /// <summary>Whole for the first days, half at the middle of the fade, none at the end and after it.</summary>
        private static void ScarFade(List<string> bad)
        {
            var life = new CompSettlementLife { sackSeverity = 0.8f };
            int now = SimClock.Now;
            foreach ((float days, float share) in new[] { (0f, 1f), (5f, 1f), (10f, 1f), (35f, 0.5f), (60f, 0f), (61f, 0f), (400f, 0f) })
            {
                life.sackedTicks = now - Mathf.RoundToInt(days * GenDate.TicksPerDay);
                if (!Near(life.Scar(), 0.8f * share)) bad.Add($"townmap (scar fade): {days:F0} days after the attack the scar is {life.Scar():F3}, expected {0.8f * share:F3}");
            }
            life.sackedTicks = FactionState.Never;
            if (life.Scar() != 0f) bad.Add($"townmap (scar fade): a town never attacked has a scar of {life.Scar():F3}");
            life.sackedTicks = now;
            life.sackSeverity = 0f;
            if (life.Scar() != 0f) bad.Add($"townmap (scar fade): a time with no severity has a scar of {life.Scar():F3}");
            // The pure fade, with a degenerate window (fade end not after the full days): all or nothing, never a division by zero.
            if (CompSettlementLife.ScarFade(5f, 10, 10) != 1f || CompSettlementLife.ScarFade(11f, 10, 10) != 0f || CompSettlementLife.ScarFade(11f, 10, 5) != 0f)
                bad.Add("townmap (scar fade): a fade window of no length is not all-or-nothing");
        }

        /// <summary>A lighter attack keeps a heavier scar that still shows (and resets the time); a heavier one, or any one after the old scar faded, replaces it.</summary>
        private static void NotifySacked(List<string> bad)
        {
            int now = SimClock.Now;
            var life = new CompSettlementLife { sackSeverity = 0.9f, sackedTicks = now - 2 * GenDate.TicksPerDay };
            life.Notify_Sacked(0.3f);
            if (!Near(life.sackSeverity, 0.9f) || life.sackedTicks != now) bad.Add($"townmap (notify): 0.3 after a fresh 0.9 scar gave severity {life.sackSeverity:F3}, time {life.sackedTicks - now:+#;-#;0} ticks from now");
            life.sackedTicks = now - 2 * GenDate.TicksPerDay;
            life.Notify_Sacked(0.95f);
            if (!Near(life.sackSeverity, 0.95f)) bad.Add($"townmap (notify): 0.95 after a 0.9 scar gave {life.sackSeverity:F3}");
            life.sackSeverity = 0.9f;
            life.sackedTicks = now - 61 * GenDate.TicksPerDay;
            life.Notify_Sacked(0.3f);
            if (!Near(life.sackSeverity, 0.3f) || life.sackedTicks != now) bad.Add($"townmap (notify): 0.3 after a faded 0.9 scar gave {life.sackSeverity:F3}");
            life.Notify_Sacked(1.7f);
            if (life.sackSeverity > 1f) bad.Add($"townmap (notify): a severity of 1.7 was kept as {life.sackSeverity:F3}");
        }

        // ---- Battle hook ----

        /// <summary>Everything a battle writes to a town, its two factions and the chronicle, to put back.</summary>
        private sealed class Saved
        {
            private readonly CompSettlementLife town;
            private readonly FactionState attacker, defender;
            private readonly LivingWorldComponent comp;
            private readonly float population, prosperity, unrest;
            public readonly float garrison;
            private readonly float[] stock;
            private readonly int sackedTicks;
            private readonly float sackSeverity, attackerTreasury, defenderTreasury, attackerWeariness, defenderWeariness;
            private readonly List<Grievance> attackerGrievances, defenderGrievances;
            private readonly int lastEntryId;

            public Saved(LivingWorldComponent comp, CompSettlementLife town, FactionState attacker, FactionState defender)
            {
                this.comp = comp;
                this.town = town;
                this.attacker = attacker;
                this.defender = defender;
                garrison = town.garrison;
                population = town.population;
                prosperity = town.prosperity;
                unrest = town.unrest;
                stock = (float[])town.stock.Clone();
                sackedTicks = town.sackedTicks;
                sackSeverity = town.sackSeverity;
                attackerTreasury = attacker.treasury;
                defenderTreasury = defender.treasury;
                attackerWeariness = attacker.warWeariness;
                defenderWeariness = defender.warWeariness;
                attackerGrievances = Copy(attacker.grievances);
                defenderGrievances = Copy(defender.grievances);
                lastEntryId = comp.chronicle.entries.Count == 0 ? 0 : comp.chronicle.entries[comp.chronicle.entries.Count - 1].id;
            }

            private static List<Grievance> Copy(List<Grievance> list)
            {
                var copy = new List<Grievance>();
                foreach (Grievance g in list)
                    copy.Add(new Grievance { otherFactionId = g.otherFactionId, amount = g.amount, reason = g.reason, sinceTicks = g.sinceTicks });
                return copy;
            }

            /// <summary>Writes the saved list back into the live one (no new list object, in case something holds the old one).</summary>
            private static void Put(List<Grievance> live, List<Grievance> saved)
            {
                live.Clear();
                live.AddRange(Copy(saved));
            }

            public void RestoreTown()
            {
                town.garrison = garrison;
                town.population = population;
                town.prosperity = prosperity;
                town.unrest = unrest;
                Array.Copy(stock, town.stock, stock.Length);
                town.sackedTicks = sackedTicks;
                town.sackSeverity = sackSeverity;
            }

            public void Restore()
            {
                RestoreTown();
                attacker.treasury = attackerTreasury;
                defender.treasury = defenderTreasury;
                attacker.warWeariness = attackerWeariness;
                defender.warWeariness = defenderWeariness;
                Put(attacker.grievances, attackerGrievances);
                Put(defender.grievances, defenderGrievances);
                comp.chronicle.RollbackAfter(lastEntryId);
            }
        }

        /// <summary>A town that can be attacked in a test without touching the colony: simulated, no map, not protected, nobody of the colony rooted there; and a faction not at war with it.</summary>
        private static bool FindFixture(LivingWorldComponent c, out CompSettlementLife town, out Faction attacker)
        {
            town = null;
            attacker = null;
            foreach (CompSettlementLife s in c.activeSettlements)
            {
                Settlement ts = s.Settlement;
                Faction owner = ts.Faction;
                if (!s.Active || owner == null || SettlementOps.Protected(ts) || c.ExistingStateFor(owner) == null || c.roots.Exists(r => r.settlementId == ts.ID)) continue;
                foreach (FactionState fs in c.factionStates)
                {
                    Faction f = fs.faction;
                    if (f == null || f == owner || f.IsPlayer || f.defeated || Diplomacy.ActiveWar(c, f, owner) != null) continue;
                    town = s;
                    attacker = f;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The real <see cref="Battle.Resolve"/> with the raze/capture roll off and a fixed seed: an overwhelming attack stamps the town's comp with
        /// the time and a severity of at least 0.35; an attack that is repulsed leaves the scar fields as they were. The town, both factions and the
        /// chronicle are put back whatever happens.
        /// </summary>
        private static void BattleHook(List<string> bad, LivingWorldComponent c)
        {
            if (!FindFixture(c, out CompSettlementLife town, out Faction attacker))
            {
                // The plan built for this case must not pass without it; the other plans only note it.
                if (AutoTest.Plan == "townmaps") bad.Add("townmap (battle): no town and attacker without a war or a rooted colonist for the battle case");
                else Log.Message(Tag + "no town and attacker without a war or a rooted colonist for the battle case (skipped)");
                return;
            }
            Settlement ts = town.Settlement;
            Faction defender = ts.Faction;
            var saved = new Saved(c, town, c.StateFor(attacker), c.StateFor(defender));
            int now = SimClock.Now;
            Rand.PushState(12345);
            Battle.TestNoFall = true;
            try
            {
                town.sackedTicks = FactionState.Never;
                town.sackSeverity = 0f;
                float strength = 10f * (town.garrison + 10f) * town.Tier.fortification * 1.2f;
                BattleResult won = Battle.Resolve(attacker, null, town, strength);
                if (!won.attackerWon) bad.Add($"townmap (battle): an attack of {strength:F0} against {ts.LabelCap} (garrison {saved.garrison:F0}) did not win");
                else
                {
                    if (won.razed || won.captured) bad.Add($"townmap (battle): the town fell with the raze and capture roll switched off (razed {won.razed}, captured {won.captured})");
                    if (town.sackedTicks != now) bad.Add($"townmap (battle): after a won attack sackedTicks is {town.sackedTicks} (now {now})");
                    if (town.sackSeverity < 0.35f - Eps || town.sackSeverity > 1f) bad.Add($"townmap (battle): after a won attack the severity is {town.sackSeverity:F3}, expected 0.35..1");
                    if (ts.Destroyed || ts.Faction != defender) bad.Add("townmap (battle): the town changed hands or was destroyed with the roll switched off");
                    Log.Message($"{Tag}battle on {ts.LabelCap}: strength {strength:F0} against garrison {saved.garrison:F0}, severity {town.sackSeverity:F2}, scar {town.Scar():F2}");
                }
                // A repulse: the old scar (a known one) is left exactly as it was.
                saved.RestoreTown();
                town.sackedTicks = now - 3 * GenDate.TicksPerDay;
                town.sackSeverity = 0.5f;
                BattleResult held = Battle.Resolve(attacker, null, town, 1f);
                if (held.attackerWon)
                {
                    if (AutoTest.Plan == "townmaps") bad.Add("townmap (battle): an attack of strength 1 won, so the repulse was not checked");
                    else Log.Message(Tag + "an attack of strength 1 won, no repulse to check (skipped)");
                }
                else if (town.sackedTicks != now - 3 * GenDate.TicksPerDay || !Near(town.sackSeverity, 0.5f))
                    bad.Add($"townmap (battle): a repulsed attack changed the scar (time {town.sackedTicks - now:+#;-#;0} ticks from now, severity {town.sackSeverity:F3})");
            }
            finally
            {
                Battle.TestNoFall = false;
                Rand.PopState();
                saved.Restore();
            }
        }

        // ---- Map parameters: unit cases (every plan) ----

        /// <summary>Both hooks are really on their targets (a class that patched nothing, or the wrong method, would leave vanilla maps without a failed patch class).</summary>
        private static void Hooks(List<string> bad)
        {
            const string owner = LivingWorldStartup.HarmonyId;
            System.Reflection.MethodBase scatter = HarmonyLib.AccessTools.Method(typeof(GenStep_Settlement), "ScatterAt");
            System.Reflection.MethodBase resolve = HarmonyLib.AccessTools.Method(typeof(RimWorld.BaseGen.SymbolResolver_Settlement), nameof(RimWorld.BaseGen.SymbolResolver_Settlement.Resolve));
            HarmonyLib.Patches scatterPatches = scatter == null ? null : HarmonyLib.Harmony.GetPatchInfo(scatter);
            HarmonyLib.Patches resolvePatches = resolve == null ? null : HarmonyLib.Harmony.GetPatchInfo(resolve);
            bool Has(IEnumerable<HarmonyLib.Patch> list) => list != null && list.Any(p => p.owner == owner);
            if (scatterPatches == null || !Has(scatterPatches.Prefixes) || !Has(scatterPatches.Postfixes) || !Has(scatterPatches.Finalizers)) bad.Add("townmap (hooks): GenStep_Settlement.ScatterAt lacks our prefix, postfix or finalizer");
            if (resolvePatches == null || !Has(resolvePatches.Prefixes)) bad.Add("townmap (hooks): SymbolResolver_Settlement.Resolve lacks our prefix");
            System.Reflection.MethodBase loot = HarmonyLib.AccessTools.Method(typeof(RimWorld.BaseGen.SymbolResolver_LootScatter), nameof(RimWorld.BaseGen.SymbolResolver_LootScatter.Resolve));
            HarmonyLib.Patches lootPatches = loot == null ? null : HarmonyLib.Harmony.GetPatchInfo(loot);
            if (lootPatches == null || !Has(lootPatches.Prefixes) || !Has(lootPatches.Finalizers)) bad.Add("townmap (hooks): SymbolResolver_LootScatter.Resolve lacks our prefix or finalizer");
            System.Reflection.MethodBase removal = HarmonyLib.AccessTools.Method(typeof(MapParent), nameof(MapParent.Notify_MyMapAboutToBeRemoved));
            HarmonyLib.Patches removalPatches = removal == null ? null : HarmonyLib.Harmony.GetPatchInfo(removal);
            if (removalPatches == null || !Has(removalPatches.Prefixes)) bad.Add("townmap (hooks): MapParent.Notify_MyMapAboutToBeRemoved lacks our prefix");
            if (!TownMaps.LootHookOk) bad.Add("townmap (hooks): LootHookOk is false, so no stockpile would be thinned");
        }

        /// <summary>Points stay inside one maker's range (or are left to vanilla when no maker fits); strength and loot stay inside their bounds.</summary>
        private static void PointsBounds(List<string> bad, LivingWorldComponent c)
        {
            // The pure parts, with makers as plain numbers: a maker capped at 500, two makers with a gap between them, a floor above the cap.
            var one = new List<(float lo, float hi)> { (106f, 500f) };
            Expect(bad, "points clamp: 2000 into 106..500", TownMaps.ClampToIntervals(2000f, one) ?? -1f, 500f);
            Expect(bad, "points clamp: 50 into 106..500", TownMaps.ClampToIntervals(50f, one) ?? -1f, 106f);
            Expect(bad, "points clamp: 300 into 106..500", TownMaps.ClampToIntervals(300f, one) ?? -1f, 300f);
            var two = new List<(float lo, float hi)> { (106f, 500f), (800f, 3200f) };
            Expect(bad, "points clamp: 600 between makers goes down", TownMaps.ClampToIntervals(600f, two) ?? -1f, 500f);
            Expect(bad, "points clamp: 700 between makers goes up", TownMaps.ClampToIntervals(700f, two) ?? -1f, 800f);
            Expect(bad, "points clamp: 4000 over the last maker", TownMaps.ClampToIntervals(4000f, two) ?? -1f, 3200f);
            Expect(bad, "points clamp: 1000 inside the second maker", TownMaps.ClampToIntervals(1000f, two) ?? -1f, 1000f);
            if (TownMaps.ClampToIntervals(1000f, new List<(float lo, float hi)>()) != null) bad.Add("townmap (points clamp): no maker did not leave vanilla's points");
            if (TownMaps.MakerInterval(900f, 9999999f, 800f) != null) bad.Add("townmap (points clamp): a floor above the cap still gave an interval");
            (float lo, float hi)? capped = TownMaps.MakerInterval(100f, 500f, 3200f);
            if (capped == null || !Near(capped.Value.lo, 106f) || !Near(capped.Value.hi, 500f)) bad.Add($"townmap (points clamp): maker 100..500 gave {capped}");
            (float lo, float hi)? open = TownMaps.MakerInterval(100f, 9999999f, 3200f);
            if (open == null || !Near(open.Value.hi, 3200f)) bad.Add($"townmap (points clamp): an open maker is not capped at 3200 ({open})");

            // Every town now: strength and loot inside their bounds, points that vanilla's own predicate accepts.
            LivingWorldTuningDef t = Tuning.T;
            int towns = 0, vanilla = 0;
            foreach (CompSettlementLife life in c.activeSettlements)
            {
                if (!life.Active) continue;
                towns++;
                float strength = TownMaps.Strength(life);
                if (strength < t.townMapMinStrength - Eps || strength > t.townMapMaxStrength + Eps) bad.Add($"townmap (bounds): {life.Settlement.LabelCap} strength {strength:F2} outside {t.townMapMinStrength}..{t.townMapMaxStrength}");
                float loot = TownMaps.PlannedLoot(life);
                if (loot <= 0f || loot > t.townMapMaxLoot + Eps) bad.Add($"townmap (bounds): {life.Settlement.LabelCap} loot {loot:F0} outside 0..{t.townMapMaxLoot}");
                float? points = TownMaps.PlannedPoints(life);
                if (points == null)
                {
                    vanilla++;
                    if (TownMaps.MakerIntervals(life.Settlement.Faction.def, PawnGroupKindDefOf.Settlement, t.townMapMaxPoints).Count > 0) bad.Add($"townmap (bounds): {life.Settlement.LabelCap} has maker ranges but no planned points");
                }
                else if (!VanillaAccepts(life.Settlement, points.Value))
                    bad.Add($"townmap (bounds): {life.Settlement.LabelCap} planned points {points.Value:F0} fit no maker of {life.Settlement.Faction.def.defName} by PawnGroupMaker.CanGenerateFrom");
            }
            TownMaps.PointsReport report = TownMaps.Distribution(c);
            Log.Message($"{Tag}points bounds: {towns} towns, {vanilla} left to vanilla; {report}");
            DistributionShape(bad, report);
        }

        /// <summary>Vanilla's own test of the points: a maker of the settlement kind that can generate a group from them (what <c>SymbolResolver_Settlement</c> and the pawn group symbol ask).</summary>
        private static bool VanillaAccepts(Settlement s, float points)
        {
            Faction f = s.Faction;
            var parms = new PawnGroupMakerParms { faction = f, points = points, groupKind = PawnGroupKindDefOf.Settlement, inhabitants = true, tile = s.Tile };
            return f.def.pawnGroupMakers != null && f.def.pawnGroupMakers.Any(m => m.kindDef == PawnGroupKindDefOf.Settlement && m.CanGenerateFrom(parms));
        }

        /// <summary>Enough towns for the shares below to mean something.</summary>
        private const int MinTownsForShape = 20;
        /// <summary>No more than this share of the towns may sit on a strength clamp (the relative signal would be a constant) ...</summary>
        private const float MaxStrengthClampShare = 0.5f;
        /// <summary>... nor on the militarization maximum, where it is a faction trait and many towns share it: a warning at the first number, a failure at the second.</summary>
        private const float WarnMilClampShare = 0.5f;
        private const int MinWordsInUse = 3;

        /// <summary>
        /// The points must not collapse into a number per tier (P1-1): most towns off the strength clamps, at least three of the four words in
        /// use. A failure only in the history plan (the world the player lands in); the other plans log a warning.
        /// </summary>
        private static void DistributionShape(List<string> bad, TownMaps.PointsReport r)
        {
            if (r.towns < MinTownsForShape)
            {
                Log.Message($"{Tag}distribution: {r.towns} towns, too few to check its shape (skipped)");
                return;
            }
            bool hard = AutoTest.Plan == "history";
            void Check(bool failed, bool fatal, string what)
            {
                if (!failed) return;
                if (hard && fatal) bad.Add($"townmap (distribution): {what}");
                else Log.Warning($"{Tag}distribution: {what}");
            }
            Check(r.Fraction(r.atMaxStrength) > MaxStrengthClampShare, true, $"{r.atMaxStrength}/{r.towns} towns sit on the strength maximum {Tuning.T.townMapMaxStrength}");
            Check(r.Fraction(r.atMinStrength) > MaxStrengthClampShare, true, $"{r.atMinStrength}/{r.towns} towns sit on the strength minimum {Tuning.T.townMapMinStrength}");
            Check(r.Fraction(r.atMaxMil) > WarnMilClampShare, false, // world-dependent (a warlike faction mix saturates it): a warning only
                 $"{r.atMaxMil}/{r.towns} towns sit on the militarization maximum {Tuning.T.townMapMilMax}");
            Check(r.words.Count < MinWordsInUse, true, $"only {r.words.Count} of 4 defender words are in use");
        }

        /// <summary>The words follow the thresholds; a real town's inspect line names the word of its planned points.</summary>
        private static void DefenderWords(List<string> bad, LivingWorldComponent c)
        {
            Vector3 w = Tuning.T.townMapDefenderWords;
            foreach ((float points, string key) in new[]
                     {
                         (0f, "LW_Inspect_Defenders_Handful"), (w.x - 1f, "LW_Inspect_Defenders_Handful"), (w.x, "LW_Inspect_Defenders_Company"), (w.y - 1f, "LW_Inspect_Defenders_Company"),
                         (w.y, "LW_Inspect_Defenders_Strong"), (w.z - 1f, "LW_Inspect_Defenders_Strong"), (w.z, "LW_Inspect_Defenders_Fortress"), (3200f, "LW_Inspect_Defenders_Fortress"),
                     })
                if (TownMaps.DefenderWordKey(points) != key) bad.Add($"townmap (words): {points:F0} points gave {TownMaps.DefenderWordKey(points)} instead of {key}");
            // Full garrison, ordinary faction: each tier reads a different word, rising with the tier, at peace and at war.
            // So "a handful" and "a company" are what healthy hamlets and villages show.
            SettlementTierDef[] tiers = DefDatabase<SettlementTierDef>.AllDefsListForReading.OrderBy(d => d.order).ToArray();
            foreach ((string state, float war) in new[] { ("peace", 1f), ("war", Tuning.T.townMapWarFactor) })
            {
                int previous = -1;
                foreach (SettlementTierDef tier in tiers)
                {
                    float points = TownMaps.RawPoints(tier, 1f, 1f, war);
                    int word = TownMaps.DefenderWordIndex(points);
                    if (word <= previous) bad.Add($"townmap (words): a healthy {tier.label} at {state} has {points:F0} points, the word '{TownMaps.DefenderWordKey(points)}' that a smaller tier already has");
                    previous = word;
                }
            }
            foreach (CompSettlementLife life in c.activeSettlements)
            {
                if (!TownMaps.Eligible(life.Settlement) || TownMaps.PlannedPoints(life) is not float points) continue;
                string line = TownMaps.DefendersLine(life);
                string word = TownMaps.DefenderWordKey(points).Translate().Resolve();
                // The formatter capitalises what follows a colon ("Would defend with: A fortress"), as everywhere in RimWorld's UI text.
                if (line == null || line.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0) bad.Add($"townmap (words): {life.Settlement.LabelCap} inspect line '{line}' lacks '{word}'");
                if (line != null && line.Contains("(") != (TownMaps.Strength(life) < TownMaps.WeakenedBelow)) bad.Add($"townmap (words): {life.Settlement.LabelCap} inspect line '{line}' disagrees with strength {TownMaps.Strength(life):F2} about 'weakened'");
                Log.Message($"{Tag}inspect line of {life.Settlement.LabelCap}: {line}");
                return;
            }
            Log.Message(Tag + "no eligible town with planned points for the inspect line (skipped)");
        }

        /// <summary>Which quest tags tie a town to a live quest: live yes, historical no, unknown id no, another mod's format yes.</summary>
        private static void QuestRelation(List<string> bad)
        {
            bool? Live(int id) => id == 12 ? true : id == 13 ? false : (bool?)null;
            void Check(string what, string[] tags, bool want)
            {
                bool got = SettlementOps.QuestRelated(tags, Live);
                if (got != want) bad.Add($"townmap (quest relation): {what} gave {got}, expected {want}");
            }
            Check("live quest 12", new[] { "Quest12.settlement" }, true);
            Check("historical quest 13", new[] { "Quest13.settlement" }, false);
            Check("unknown quest 14", new[] { "Quest14.settlement" }, false);
            Check("another mod's tag", new[] { "SomeMod_Tag" }, true);
            Check("no tags", new string[0], false);
            Check("a stale tag then a live one", new[] { "Quest14.a", "Quest12.b" }, true);
            Check("a stale tag and a historical one", new[] { "Quest14.a", "Quest13.b" }, false);
            foreach ((string tag, int id, bool parses) in new[]
                     {
                         ("Quest12.settlement", 12, true), ("Quest0.x", 0, true), ("Quest.x", 0, false), ("Quest12", 0, false), ("Quest12settlement", 0, false),
                         ("Questx.y", 0, false), ("quest12.x", 0, false), ("SomeMod_Tag", 0, false), ("Quest99999999999.a", 0, false),
                     })
            {
                bool got = SettlementOps.TryParseQuestId(tag, out int parsed);
                if (got != parses || (parses && parsed != id)) bad.Add($"townmap (quest relation): '{tag}' parsed as {got} {parsed}, expected {parses} {id}");
            }
        }

        // ---- State: unit cases (every plan) ----

        /// <summary>The thinning share by stock and the hunger severity by famine days: the bounds, the proportions, and never the moderate stage.</summary>
        private static void StateUnits(List<string> bad)
        {
            LivingWorldTuningDef t = Tuning.T;
            void Check(string what, float got, float want)
            {
                if (!Near(got, want)) bad.Add($"townmap (state): {what} gave {got:F3} instead of {want:F3}");
            }
            Check("stock scale at the normal share", TownMaps.ScaleOf(t.townStockNormal, false), 1f);
            Check("stock scale above it", TownMaps.ScaleOf(3f, false), 1f);
            Check("stock scale at half the normal share", TownMaps.ScaleOf(t.townStockNormal / 2f, false), 0.5f);
            Check("stock scale of empty stores", TownMaps.ScaleOf(0f, false), t.townMinStockScale);
            Check("famine food at the normal share", TownMaps.ScaleOf(t.townStockNormal, true), t.townFamineFoodFactor);
            Check("famine food with empty stores", TownMaps.ScaleOf(0f, true), t.townMinStockScale * t.townFamineFoodFactor);
            Check("hunger after 0 days", TownMaps.HungerSeverity(0), 0.05f);
            Check("hunger after 3 days", TownMaps.HungerSeverity(3), 0.11f);
            Check("hunger after 8 days", TownMaps.HungerSeverity(8), 0.21f);
            Check("hunger after 10 days", TownMaps.HungerSeverity(10), Mathf.Min(0.25f, t.townMalnutritionMax));
            Check("hunger after 400 days", TownMaps.HungerSeverity(400), t.townMalnutritionMax);
            // The famine shows only while the economy is simulated: its day count does not move otherwise.
            var starving = new CompSettlementLife { famineDays = 5 };
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool economy = stored.simulateEconomy;
            try
            {
                stored.simulateEconomy = true;
                if (TownMaps.FamineDays(starving) != 5) bad.Add($"townmap (state): famine of 5 days shows as {TownMaps.FamineDays(starving)} with the economy on");
                stored.simulateEconomy = false;
                if (TownMaps.FamineDays(starving) != 0) bad.Add($"townmap (state): famine of 5 days shows as {TownMaps.FamineDays(starving)} with the economy off");
            }
            finally
            {
                stored.simulateEconomy = economy;
            }
            if (t.townMalnutritionMax >= 0.4f) bad.Add($"townmap (state): the malnutrition cap {t.townMalnutritionMax} reaches the moderate stage (0.4)");
        }

        // ---- Map parameters: real maps (plan "townmaps") ----

        /// <summary>The town's sim state that these cases (and the tour's town shots) change, to put back.</summary>
        internal sealed class TownState
        {
            private readonly CompSettlementLife town;
            private readonly float population, garrison, prosperity, sackSeverity;
            private readonly int famineDays, sackedTicks;
            private readonly float[] stock;

            public TownState(CompSettlementLife town)
            {
                this.town = town;
                population = town.population;
                garrison = town.garrison;
                prosperity = town.prosperity;
                famineDays = town.famineDays;
                sackedTicks = town.sackedTicks;
                sackSeverity = town.sackSeverity;
                stock = (float[])town.stock.Clone();
            }

            public void Restore()
            {
                town.population = population;
                town.garrison = garrison;
                town.prosperity = prosperity;
                town.famineDays = famineDays;
                town.sackedTicks = sackedTicks;
                town.sackSeverity = sackSeverity;
                if (town.stock.Length == stock.Length) Array.Copy(stock, town.stock, stock.Length);
            }

            /// <summary>The town at this size, with its garrison at this share of what it regrows toward (the strength the map is generated with).</summary>
            public void Set(float newPopulation, float garrisonShare)
            {
                town.population = newPopulation;
                town.garrison = garrisonShare * TownMaps.GarrisonGoal(town);
            }

            /// <summary>Stores at the normal share of their target (townStockNormal) in every commodity, so nothing is thinned, no famine, no scar. Set the population first: the target follows it.</summary>
            public void Healthy()
            {
                town.famineDays = 0;
                town.sackedTicks = FactionState.Never;
                town.sackSeverity = 0f;
                foreach (CommodityDef c in Commodities.All)
                    if (c.index < town.stock.Length) town.stock[c.index] = Mathf.Max(town.stock[c.index], Tuning.T.townStockNormal * town.TargetStock(c));
            }

            /// <summary>A town that lost an attack <paramref name="daysAgo"/> days ago, is in famine for <paramref name="famine"/> days, and holds this share of its target food. Set the population first.</summary>
            public void Sacked(float daysAgo, float severity, int famine, float foodShare)
            {
                town.sackedTicks = SimClock.Now - Mathf.RoundToInt(daysAgo * GenDate.TicksPerDay);
                town.sackSeverity = severity;
                town.famineDays = famine;
                town.stock[LWDefOf.LW_Food.index] = foodShare * town.TargetStock(LWDefOf.LW_Food);
            }
        }

        /// <summary>What one generated map showed, collected before it was removed.</summary>
        private sealed class Generated
        {
            public Map map;
            public TownMapStats stats;
            public string skip;
            public int entries, defenders, errors, landingBeacons;
            public double ms;
            public CellRect seen;
            /// <summary>The town's faction's buildings in the final grounds and an order-free hash of their defs and positions (determinism).</summary>
            public int buildings;
            public long layoutHash;
            /// <summary>Loot things recorded, and how many of them are gone or have another stack count at the end of the generation.</summary>
            public int lootThings, lootChanged;
            /// <summary>Defenders that can be hungry, how many have malnutrition, and the highest severity.</summary>
            public int hungryCandidates, hungryHave;
            /// <summary>Faction pawns with malnutrition on the map, whether or not our hooks ran (a map of vanilla's generation must have none).</summary>
            public int malnourished;
            public string malnourishedWho = "";
            /// <summary>Pawns the map made that still carried malnutrition beyond their baseline right after the map was removed (it must be none).</summary>
            public int malnourishedAfterRemoval;
            /// <summary>The faction's roof holders and doors on the finished map, counted again here (a later gen step that removed one would show).</summary>
            public int roofHoldersFinal, doorsFinal;
            /// <summary>Roof holders and doors the post pass found before its scars that are gone, destroyed or unspawned on the finished map (it must be none).</summary>
            public int holdersLost;
            public float maxMalnutrition;
        }

        /// <summary>
        /// A map of the town as an assault would make it (150 x 150), measured, then removed again with the pawns it made (those the world did not
        /// already have), the detection raids it started and the inhabitants the settlement remembers.
        /// </summary>
        private static Generated GenerateAndRemove(Settlement s, IEnumerable<GenStepWithParams> extraGenSteps = null)
        {
            var g = new Generated();
            TownMaps.Last = null;
            TownMaps.LastSkip = null;
            TownMaps.Entries = 0;
            TownMaps.SeenRect = CellRect.Empty;
            PawnBaseline baseline = PawnBaseline.Take();
            int errorsBefore = AutoTest.ErrorCount;
            var made = new List<Pawn>();
            Map map = null;
            var sw = Stopwatch.StartNew();
            try
            {
                map = GetOrGenerateMapUtility.GetOrGenerateMap(s.Tile, new IntVec3(150, 1, 150), null, extraGenSteps);
                g.ms = sw.Elapsed.TotalMilliseconds;
                g.map = map;
                if (map == null) return g;
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                {
                    if (p.Faction != s.Faction) continue;
                    made.Add(p);
                    if (p.RaceProps.Humanlike) g.defenders++;
                    Hediff malnutrition = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                    if (malnutrition != null && !baseline.Had(p, malnutrition.Severity))
                    {
                        g.malnourished++;
                        g.malnourishedWho += $" {p.LabelShort}{(baseline.Knew(p) ? " (the world's)" : "")}{(Figures.ShouldKeep(p) ? " (a figure)" : "")}";
                    }
                }
                g.landingBeacons = map.listerThings.ThingsOfDef(ThingDefOf.ShipLandingBeacon).Count;
                g.stats = TownMaps.Last;
                g.skip = TownMaps.LastSkip;
                g.entries = TownMaps.Entries;
                g.seen = TownMaps.SeenRect;
                Inspect(g, s);
            }
            finally
            {
                g.malnourishedAfterRemoval = Dispose(map, s, baseline, made);
                g.errors = AutoTest.ErrorCount - errorsBefore;
            }
            return g;
        }

        /// <summary>
        /// What the world's pawns were before a town map was generated: which pawns existed, and the malnutrition they had. Vanilla redresses world
        /// pawns into a new map's defenders (and keeps the hediffs it does not clear), and the figures the simulation keeps are never discarded, so
        /// whatever a case did to such a pawn would otherwise be seen by the next case.
        /// </summary>
        internal sealed class PawnBaseline
        {
            private readonly HashSet<Pawn> known = new HashSet<Pawn>();
            private readonly Dictionary<Pawn, float> malnutrition = new Dictionary<Pawn, float>();

            public static PawnBaseline Take()
            {
                var b = new PawnBaseline();
                foreach (Pawn p in Find.WorldPawns.AllPawnsAliveOrDead)
                {
                    b.known.Add(p);
                    Hediff h = p.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                    if (h != null) b.malnutrition[p] = h.Severity;
                }
                return b;
            }

            public bool Knew(Pawn p) => known.Contains(p);

            /// <summary>Whether the pawn had malnutrition of at most this severity before the map (so it is not ours).</summary>
            public bool Had(Pawn p, float severity) => malnutrition.TryGetValue(p, out float was) && severity <= was + Eps;

            /// <summary>The malnutrition the pawn has beyond what it had (the severity above its old one; all of it for a pawn that had none).</summary>
            public float Beyond(Pawn p)
            {
                Hediff h = p.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                if (h == null) return 0f;
                return malnutrition.TryGetValue(p, out float was) ? Mathf.Max(0f, h.Severity - was) : h.Severity;
            }

            /// <summary>Puts the pawn's malnutrition back as it was (none, if it had none).</summary>
            public void Restore(Pawn p)
            {
                Hediff h = p.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                if (!malnutrition.TryGetValue(p, out float severity))
                {
                    if (h != null) p.health.RemoveHediff(h);
                }
                else if (h != null) h.Severity = severity;
                else
                {
                    // The map's removal clears mild malnutrition of its defenders, the pawn's own included.
                    h = HediffMaker.MakeHediff(HediffDefOf.Malnutrition, p);
                    h.Severity = severity;
                    p.health.AddHediff(h);
                }
            }
        }

        /// <summary>
        /// Takes a generated town map away again, with the pawns it made (those the world did not already have), the detection raids it started and the
        /// inhabitants the settlement remembers (so the next generation starts from the same world). The pawns that stay in the world (ones it already
        /// had, and the simulation's figures) get their malnutrition back as it was. Also used by the tour's town shots. Returns how many of the pawns
        /// still had malnutrition beyond their baseline right after the map was removed (before it is put back): none, if the removal cleared the famine's.
        /// </summary>
        internal static int Dispose(Map map, Settlement s, PawnBaseline baseline, List<Pawn> made)
        {
            if (map != null && Current.Game.Maps.Contains(map)) Current.Game.DeinitAndRemoveMap(map, false);
            s.GetComponent<TimedDetectionRaids>()?.ResetCountdown();
            int hungry = 0;
            foreach (Pawn p in made)
                if (!p.Dead && baseline.Beyond(p) > Eps) hungry++;
            foreach (Pawn p in made)
            {
                if (!baseline.Knew(p) && !Figures.ShouldKeep(p))
                {
                    if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemoveAndDiscardPawnViaGC(p);
                }
                else baseline.Restore(p);
            }
            s.previouslyGeneratedInhabitants.Clear();
            return hungry;
        }

        /// <summary>What the finished map shows, read before it is removed: the buildings of the town, the loot that is still as it was, the hungry defenders.</summary>
        private static void Inspect(Generated g, Settlement s)
        {
            TownMapStats st = g.stats;
            Map map = g.map;
            if (st == null) return;
            var standing = new HashSet<int>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
                if (t.Spawned && !t.Destroyed) standing.Add(t.thingIDNumber);
            foreach (int id in st.holderIds)
                if (!standing.Contains(id)) g.holdersLost++;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
            {
                if (t is not Building b || b.Faction != s.Faction || !st.rect.Contains(b.Position)) continue;
                g.buildings++;
                if (b.def.holdsRoof) g.roofHoldersFinal++;
                if (b is Building_Door) g.doorsFinal++;
                g.layoutHash += Gen.HashCombineInt(Gen.HashCombineInt(b.def.shortHash, b.Position.x), b.Position.z);
            }
            var haulables = new Dictionary<int, Thing>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)) haulables[t.thingIDNumber] = t;
            foreach (KeyValuePair<int, int> loot in st.lootStacks)
            {
                g.lootThings++;
                if (!haulables.TryGetValue(loot.Key, out Thing t) || !t.Spawned || t.stackCount != loot.Value) g.lootChanged++;
            }
            foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
            {
                if (!TownMaps.HungryCandidate(p, s.Faction, st.rect)) continue;
                g.hungryCandidates++;
                Hediff h = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Malnutrition);
                if (h == null) continue;
                g.hungryHave++;
                g.maxMalnutrition = Mathf.Max(g.maxMalnutrition, h.Severity);
            }
        }

        private static void LogMs(string what, Generated g)
        {
            Log.Message($"{Tag}ms: {what} {g.ms:F0} ms");
            if (g.ms > 3000) Log.Warning($"{Tag}{what} took {g.ms:F0} ms (soft limit 3000)");
        }

        /// <summary>A map that went through our hooks: no errors, parameters set, and the main rect is the town's rect.</summary>
        private static bool Applied(List<string> bad, string what, Generated g)
        {
            LogMs(what, g);
            if (g.map == null)
            {
                bad.Add($"townmap ({what}): no map was generated");
                return false;
            }
            if (g.errors > 0) bad.Add($"townmap ({what}): {g.errors} errors logged while generating");
            if (g.stats == null)
            {
                bad.Add($"townmap ({what}): the parameters were not applied (skip: {g.skip ?? "none"})");
                return false;
            }
            if (g.stats.mainRect != g.stats.rect) bad.Add($"townmap ({what}): the main rect {g.stats.mainRect} is not the town's rect {g.stats.rect}");
            if (g.stats.genStepDef != "Settlement") bad.Add($"townmap ({what}): the step def was {g.stats.genStepDef}, expected Settlement");
            return true;
        }

        /// <summary>A map that went through vanilla's generation: no errors, nothing applied, and a rect of vanilla's 34..38.</summary>
        private static void Vanilla(List<string> bad, string what, Generated g, string skip)
        {
            LogMs(what, g);
            if (g.map == null)
            {
                bad.Add($"townmap ({what}): no map was generated");
                return;
            }
            if (g.errors > 0) bad.Add($"townmap ({what}): {g.errors} errors logged while generating");
            if (g.stats != null) bad.Add($"townmap ({what}): the parameters were applied although the case switches them off");
            if (g.malnourished > 0) bad.Add($"townmap ({what}): {g.malnourished} defenders have malnutrition on a map of vanilla's generation:{g.malnourishedWho}");
            if (g.skip != skip) bad.Add($"townmap ({what}): the skip reason was {g.skip ?? "none"}, expected {skip ?? "none"}");
            if (g.seen.Width < 34 || g.seen.Width > 38 || g.seen.Height < 34 || g.seen.Height > 38) bad.Add($"townmap ({what}): the rect is {g.seen.Width}x{g.seen.Height}, not vanilla's 34..38");
        }


        /// <summary>An active town of a non-Empire faction that can be attacked in a test: no map, no quest, nobody of the colony rooted there, and planned points to check.</summary>
        private static CompSettlementLife FindMapFixture(LivingWorldComponent c, bool empire)
        {
            foreach (CompSettlementLife s in c.activeSettlements)
            {
                Settlement ts = s.Settlement;
                Faction owner = ts.Faction;
                if (!s.Active || owner == null || (owner == Faction.OfEmpire) != empire || SettlementOps.Protected(ts) || c.ExistingStateFor(owner) == null
                    || c.roots.Exists(r => r.settlementId == ts.ID)) continue;
                if (!empire && TownMaps.PlannedPoints(s) == null) continue;
                return s;
            }
            return null;
        }

        private static void Maps(List<string> bad, LivingWorldComponent c)
        {
            CompSettlementLife town = FindMapFixture(c, empire: false);
            if (town == null)
            {
                bad.Add("townmap (maps): no active town to generate a map for");
                return;
            }
            var state = new TownState(town);
            try
            {
                Guard(bad, "size and points", () => SizeAndPoints(bad, c, town, state));
                Guard(bad, "state", () => MapState(bad, town, state));
                Guard(bad, "off", () => Off(bad, c, town, state));
                Guard(bad, "quest tags on a map", () => QuestOnMap(bad, c, town));
                Guard(bad, "bare gen step", () => BareGenStep(bad, c, town));
                Guard(bad, "skipped resolver", () => SkippedResolver(bad, town, state));
            }
            finally
            {
                state.Restore();
            }
            Guard(bad, "empire", () => EmpireTown(bad, c));
        }

        /// <summary>A healthy town, a depleted hamlet and a city of the same tile: the hamlet's grounds are smaller, its defenders fewer and its loot lower; the city's grounds are not smaller.</summary>
        private static void SizeAndPoints(List<string> bad, LivingWorldComponent c, CompSettlementLife town, TownState state)
        {
            Settlement ts = town.Settlement;
            state.Set(200f, 1f);
            float townPlanned = TownMaps.PlannedPoints(town) ?? -1f, townLoot = TownMaps.PlannedLoot(town);
            Generated healthy = GenerateAndRemove(ts);
            state.Set(30f, 0.2f);
            float hamletPlanned = TownMaps.PlannedPoints(town) ?? -1f, hamletLoot = TownMaps.PlannedLoot(town);
            Generated hamlet = GenerateAndRemove(ts);
            state.Set(600f, 1f);
            float cityPlanned = TownMaps.PlannedPoints(town) ?? -1f;
            Generated city = GenerateAndRemove(ts);
            bool okTown = Applied(bad, "healthy town", healthy), okHamlet = Applied(bad, "depleted hamlet", hamlet), okCity = Applied(bad, "city", city);
            if (okTown)
            {
                TownMapStats st = healthy.stats;
                if (st.rect != st.vanillaRect) bad.Add($"townmap (healthy town): a town's grounds changed from {st.vanillaRect} to {st.rect}");
                if (st.vanillaRect.Width < 34 || st.vanillaRect.Width > 38 || st.vanillaRect.Height < 34 || st.vanillaRect.Height > 38) bad.Add($"townmap (healthy town): vanilla's rect is {st.vanillaRect.Width}x{st.vanillaRect.Height}");
                if (!Near(st.points, townPlanned) || !VanillaAccepts(ts, st.points)) bad.Add($"townmap (healthy town): points {st.points:F0}, planned {townPlanned:F0}, in a maker's range: {VanillaAccepts(ts, st.points)}");
                if (!Near(st.loot, townLoot)) bad.Add($"townmap (healthy town): loot {st.loot:F0}, planned {townLoot:F0}");
            }
            if (okHamlet)
            {
                TownMapStats st = hamlet.stats;
                if (st.rect.Width < 20 || st.rect.Width > 27 || st.rect.Height < 20 || st.rect.Height > 27) bad.Add($"townmap (depleted hamlet): the rect is {st.rect.Width}x{st.rect.Height}, expected 20..27");
                if (!Near(st.points, hamletPlanned) || !VanillaAccepts(ts, st.points)) bad.Add($"townmap (depleted hamlet): points {st.points:F0}, planned {hamletPlanned:F0}, in a maker's range: {VanillaAccepts(ts, st.points)}");
                if (!Near(st.loot, hamletLoot)) bad.Add($"townmap (depleted hamlet): loot {st.loot:F0}, planned {hamletLoot:F0}");
                if (st.strength > 0.7f + Eps) bad.Add($"townmap (depleted hamlet): strength {st.strength:F2} at a garrison of a fifth of the target");
            }
            if (okTown && okHamlet)
            {
                if (hamlet.stats.rect.Width >= healthy.stats.rect.Width || hamlet.stats.rect.Height >= healthy.stats.rect.Height) bad.Add($"townmap (size): the hamlet's rect {hamlet.stats.rect.Width}x{hamlet.stats.rect.Height} is not smaller than the town's {healthy.stats.rect.Width}x{healthy.stats.rect.Height}");
                if (hamlet.stats.points >= healthy.stats.points) bad.Add($"townmap (points): the hamlet's {hamlet.stats.points:F0} points are not fewer than the town's {healthy.stats.points:F0}");
                if (hamlet.defenders >= healthy.defenders) bad.Add($"townmap (points): the hamlet has {hamlet.defenders} humanlike defenders, the town {healthy.defenders}");
                if (hamlet.stats.loot >= healthy.stats.loot) bad.Add($"townmap (loot): the hamlet's loot {hamlet.stats.loot:F0} is not lower than the town's {healthy.stats.loot:F0}");
            }
            if (okCity)
            {
                TownMapStats st = city.stats;
                bool grew = st.rect != st.vanillaRect;
                if (st.rect.Width < st.vanillaRect.Width || st.rect.Height < st.vanillaRect.Height || st.rect.Width > 49 || st.rect.Height > 49) bad.Add($"townmap (city): the rect is {st.rect.Width}x{st.rect.Height} from {st.vanillaRect.Width}x{st.vanillaRect.Height}");
                if (grew && (!new CellRect(10, 10, 130, 130).Contains(st.rect.Min) || !new CellRect(10, 10, 130, 130).Contains(st.rect.Max))) bad.Add($"townmap (city): the rect {st.rect} leaves the map's margin");
                if (okTown && st.points < healthy.stats.points) bad.Add($"townmap (city): {st.points:F0} points are fewer than the town's {healthy.stats.points:F0}");
                if (!Near(st.points, cityPlanned)) bad.Add($"townmap (city): points {st.points:F0}, planned {cityPlanned:F0}");
                Log.Message($"{Tag}city grounds {(grew ? "expanded" : "kept at the vanilla size (did not fit)")}: {st.vanillaRect.Width}x{st.vanillaRect.Height} -> {st.rect.Width}x{st.rect.Height}");
            }
            Log.Message($"{Tag}defenders: town {healthy.defenders} ({healthy.stats?.points:F0} points), hamlet {hamlet.defenders} ({hamlet.stats?.points:F0}), city {city.defenders} ({city.stats?.points:F0})");
        }

        /// <summary>
        /// The state post pass on real maps of one town (200 people, a full garrison): a sacked, starving town (scars, food thinned, loot untouched, roof
        /// holders and doors all there, hungry defenders) twice for determinism; a healthy one (nothing changed); a scar that has faded; and the loot hook
        /// missing (nothing thinned).
        /// </summary>
        private static void MapState(List<string> bad, CompSettlementLife town, TownState state)
        {
            Settlement ts = town.Settlement;
            LivingWorldTuningDef t = Tuning.T;
            CommodityDef food = LWDefOf.LW_Food;
            var skips = new List<string>();

            state.Set(200f, 1f);
            state.Sacked(daysAgo: 1f, severity: 1f, famine: 10, foodShare: 0.1f);
            float foodScale = TownMaps.StockScale(town, food, famine: true);
            Generated sacked = GenerateAndRemove(ts);
            Generated again = GenerateAndRemove(ts);

            state.Set(200f, 1f);
            state.Healthy();
            Generated healthy = GenerateAndRemove(ts);

            // A commodity at half the normal share (not food: it is the one the famine cuts again) loses about half of its value, in proportion.
            CommodityDef under = null;
            float underValue = 0f;
            if (healthy.stats != null)
                foreach (KeyValuePair<CommodityDef, (int countBefore, float valueBefore, int countAfter, float valueAfter)> pair in healthy.stats.byCommodity)
                    if (pair.Key != food && pair.Value.valueBefore > underValue)
                    {
                        under = pair.Key;
                        underValue = pair.Value.valueBefore;
                    }
            Generated thinned = null;
            float underScale = 0f;
            if (under != null)
            {
                state.Set(200f, 1f);
                state.Healthy();
                town.stock[under.index] = 0.5f * t.townStockNormal * town.TargetStock(under);
                underScale = TownMaps.StockScale(town, under, famine: false);
                thinned = GenerateAndRemove(ts);
            }

            // A scar that has run its course shows nothing.
            town.sackedTicks = SimClock.Now - 70 * GenDate.TicksPerDay;
            town.sackSeverity = 1f;
            Generated faded = GenerateAndRemove(ts);

            // The loot hook missing: the stockpiles are left alone rather than risk thinning the loot.
            state.Sacked(daysAgo: 1f, severity: 1f, famine: 10, foodShare: 0.1f);
            TownMaps.TestLootHookOff = true;
            Generated noHook;
            try
            {
                noHook = GenerateAndRemove(ts);
            }
            finally
            {
                TownMaps.TestLootHookOff = false;
            }

            // The hook is there, but its prefix does not see this map's loot scatter (another resolver, a prefix that skips ours): same.
            TownMaps.TestLootBeforeNull = true;
            Generated noRun;
            try
            {
                noRun = GenerateAndRemove(ts);
            }
            finally
            {
                TownMaps.TestLootBeforeNull = false;
            }

            if (Applied(bad, "sacked and starving town", sacked))
            {
                TownMapStats st = sacked.stats;
                if (st.thinSkip != null) bad.Add($"townmap (sacked): the stockpiles were not looked at ({st.thinSkip})");
                if (st.scar < 0.99f) bad.Add($"townmap (sacked): the scar of an attack a day ago is {st.scar:F2}, expected 1");
                if (st.ash <= 0) bad.Add("townmap (sacked): no ash on the grounds of a town just sacked");
                if (st.destroyed <= 0) bad.Add("townmap (sacked): no building destroyed in a town just sacked");
                if (st.roofHoldersBefore == 0) bad.Add("townmap (sacked): the town has no walls, doors or columns, so the roof case proves nothing");
                if (st.roofHoldersBefore != st.roofHoldersAfter) bad.Add($"townmap (sacked): roof holders {st.roofHoldersBefore} before the scars, {st.roofHoldersAfter} after");
                if (st.doorsBefore != st.doorsAfter) bad.Add($"townmap (sacked): doors {st.doorsBefore} before the scars, {st.doorsAfter} after");
                // Every roof holder and door the post pass saw is still standing on the finished map (later gen steps may add more, so the totals are only logged).
                if (sacked.holdersLost > 0) bad.Add($"townmap (sacked): {sacked.holdersLost} of the {st.holderIds.Count} roof holders and doors the post pass saw are gone from the finished map");
                if (!Thinned(bad, "sacked", st, food, foodScale)) Skip(skips, "sacked: no food in the stockpiles of this map, so the food share was not checked");
                if (st.loot > 0f && sacked.lootThings == 0) bad.Add($"townmap (sacked): a loot value of {st.loot:F0} but no loot thing was recorded");
                if (sacked.lootChanged > 0) bad.Add($"townmap (sacked): {sacked.lootChanged} of {sacked.lootThings} loot things were removed or had their stack changed");
                if (sacked.malnourishedAfterRemoval > 0) bad.Add($"townmap (sacked): {sacked.malnourishedAfterRemoval} defenders still had the famine's malnutrition after the map was removed (they go to the world)");
                if (sacked.hungryCandidates == 0) Skip(skips, "sacked: no defender can be hungry on this map, so the hunger was not checked");
                else
                {
                    if (sacked.hungryHave != sacked.hungryCandidates) bad.Add($"townmap (sacked): {sacked.hungryHave} of {sacked.hungryCandidates} defenders have malnutrition");
                    if (sacked.maxMalnutrition > t.townMalnutritionMax + Eps) bad.Add($"townmap (sacked): malnutrition {sacked.maxMalnutrition:F3} above the cap {t.townMalnutritionMax}");
                    if (sacked.maxMalnutrition < TownMaps.HungerSeverity(10) - Eps) bad.Add($"townmap (sacked): malnutrition {sacked.maxMalnutrition:F3}, expected {TownMaps.HungerSeverity(10):F3} after 10 days of famine");
                }
                Log.Message($"{Tag}sacked: scar {st.scar:F2}, destroyed {st.destroyed}, damaged {st.damaged}, weakened {st.weakened}, ash {st.ash}, rubble {st.rubble}, burned {st.burned}, " +
                            $"roof holders {st.roofHoldersBefore}/{st.roofHoldersAfter} (finished map {sacked.roofHoldersFinal}), doors {st.doorsBefore}/{st.doorsAfter} (finished map {sacked.doorsFinal}), items {st.itemsRemoved}/{st.itemsBefore} removed, " +
                            $"stockpile {st.stockpileValueBefore:F0} -> {st.stockpileValueAfter:F0}, loot {sacked.lootThings} things, hungry {sacked.hungryHave}/{sacked.hungryCandidates}");
            }

            // Same state, same tile: the same map (the items are logged only: stockpile contents come out of the generators' own random draws).
            if (Applied(bad, "sacked and starving town again", again) && sacked.stats != null)
            {
                TownMapStats a = sacked.stats, b = again.stats;
                if (a.rect != b.rect) bad.Add($"townmap (determinism): the rect was {a.rect}, then {b.rect}");
                if (sacked.buildings != again.buildings) bad.Add($"townmap (determinism): {sacked.buildings} buildings, then {again.buildings}");
                if (sacked.layoutHash != again.layoutHash) bad.Add("townmap (determinism): the buildings' defs and positions differ between the two generations");
                if (a.destroyed != b.destroyed || a.damaged != b.damaged || a.ash != b.ash || a.burned != b.burned)
                    bad.Add($"townmap (determinism): destroyed/damaged/ash/burned {a.destroyed}/{a.damaged}/{a.ash}/{a.burned}, then {b.destroyed}/{b.damaged}/{b.ash}/{b.burned}");
                Log.Message($"{Tag}determinism: {sacked.buildings} buildings, items {a.itemsBefore} then {b.itemsBefore}");
            }

            if (Applied(bad, "healthy stores", healthy))
            {
                TownMapStats st = healthy.stats;
                if (st.thinSkip != null) bad.Add($"townmap (healthy): the stockpiles were not looked at ({st.thinSkip})");
                if (st.itemsRemoved != 0) bad.Add($"townmap (healthy): {st.itemsRemoved} stockpile things were removed with the stores at the normal share");
                if (Math.Abs(st.stockpileValueBefore - st.stockpileValueAfter) > 0.01f) bad.Add($"townmap (healthy): the stockpile went from {st.stockpileValueBefore:F0} to {st.stockpileValueAfter:F0}");
                if (st.scar != 0f || st.ash != 0 || st.destroyed != 0 || st.burned != 0) bad.Add($"townmap (healthy): scar {st.scar:F2}, ash {st.ash}, destroyed {st.destroyed}, burned {st.burned} for a town never attacked");
                if (st.hungry != 0 || healthy.hungryHave != 0) bad.Add($"townmap (healthy): {st.hungry} defenders were given malnutrition ({healthy.hungryHave} have it)");
                if (st.roofHoldersBefore != st.roofHoldersAfter || st.doorsBefore != st.doorsAfter) bad.Add("townmap (healthy): roof holders or doors changed");
                if (healthy.holdersLost > 0) bad.Add($"townmap (healthy): {healthy.holdersLost} roof holders or doors are gone from the finished map");
                Log.Message($"{Tag}healthy: stockpile {st.stockpileValueBefore:F0} -> {st.stockpileValueAfter:F0}, {st.itemsBefore} things, {healthy.buildings} buildings");
            }

            // Monotone only: the sacked town has a scar and the healthy one none. Rect and points are the params cases' business.
            if (sacked.stats != null && healthy.stats != null && !(sacked.stats.scar > healthy.stats.scar)) bad.Add($"townmap (state): scar {sacked.stats.scar:F2} of the sacked town is not above the healthy town's {healthy.stats.scar:F2}");

            if (thinned != null && Applied(bad, "commodity under the normal share", thinned))
            {
                TownMapStats st = thinned.stats;
                if (st.thinSkip != null) bad.Add($"townmap (thinning): the stockpiles were not looked at ({st.thinSkip})");
                // The scale is a half; each stack is rounded at random, so the value kept lies between the roundings of its stacks, around that half.
                else if (!Thinned(bad, $"thinning of {under.defName}", st, under, underScale))
                    Skip(skips, $"thinning: {under.defName} is not in the stockpiles of the second map (their contents are random), so its proportional thinning was not checked");
            }
            else if (thinned == null) Skip(skips, "thinning: no non-food commodity in the stockpiles of the healthy map, so the proportional thinning was not checked");

            if (Applied(bad, "faded scar", faded))
            {
                TownMapStats st = faded.stats;
                if (st.scar != 0f || st.ash != 0 || st.destroyed != 0 || st.burned != 0) bad.Add($"townmap (fade): 70 days after the attack scar {st.scar:F2}, ash {st.ash}, destroyed {st.destroyed}, burned {st.burned}");
            }

            if (Applied(bad, "loot hook missing", noHook))
            {
                TownMapStats st = noHook.stats;
                if (st.thinSkip != "no loot hook" || st.itemsRemoved != 0 || st.itemsBefore != 0) bad.Add($"townmap (no loot hook): skip '{st.thinSkip ?? "none"}', {st.itemsRemoved}/{st.itemsBefore} things looked at or removed");
            }

            if (Applied(bad, "loot hook did not run", noRun))
            {
                TownMapStats st = noRun.stats;
                if (st.loot <= 0f) bad.Add("townmap (loot hook did not run): no loot value was set, so the case proves nothing");
                if (st.thinSkip != "loot hook did not run" || st.itemsRemoved != 0 || st.itemsBefore != 0) bad.Add($"townmap (loot hook did not run): skip '{st.thinSkip ?? "none"}', {st.itemsRemoved}/{st.itemsBefore} things looked at or removed");
                if (noRun.lootThings != 0) bad.Add($"townmap (loot hook did not run): {noRun.lootThings} loot things recorded although the prefix recorded nothing");
            }

            if (skips.Count > 0) Log.Warning($"{Tag}SKIPPED {skips.Count} checks of the state cases: {string.Join("; ", skips)}");
        }

        private static void Skip(List<string> skips, string what)
        {
            skips.Add(what);
            Log.Message($"{Tag}SKIP {what}");
        }

        /// <summary>
        /// The thinning of one commodity on a map: the stack rounding bounds enclose the share the scale asks for, and the value left lies between them.
        /// False when the commodity is not in the stockpiles.
        /// </summary>
        private static bool Thinned(List<string> bad, string what, TownMapStats st, CommodityDef commodity, float scale)
        {
            if (!st.byCommodity.TryGetValue(commodity, out var e) || e.valueBefore <= 0f || !st.afterBounds.TryGetValue(commodity, out var bounds)) return false;
            float want = scale * e.valueBefore;
            if (want < bounds.low - 0.01f || want > bounds.high + 0.01f)
                bad.Add($"townmap ({what}): the share {scale:F2} of {e.valueBefore:F0} is {want:F1}, outside what the stacks' rounding allows ({bounds.low:F1}..{bounds.high:F1})");
            if (e.valueAfter < bounds.low - 0.01f || e.valueAfter > bounds.high + 0.01f)
                bad.Add($"townmap ({what}): value {e.valueBefore:F0} -> {e.valueAfter:F0}, outside the rounding bounds {bounds.low:F1}..{bounds.high:F1} of the share {scale:F2}");
            Log.Message($"{Tag}{what}: {commodity.defName} {e.countBefore} -> {e.countAfter} units, value {e.valueBefore:F0} -> {e.valueAfter:F0} (ratio {e.valueAfter / e.valueBefore:F2}, scale {scale:F2}, bounds {bounds.low:F0}..{bounds.high:F0})");
            return true;
        }

        /// <summary>The setting off, and a save prepared for removal (which turns it off for the save): no context, vanilla's rect, and no state shown (the town is sacked and starving).</summary>
        private static void Off(List<string> bad, LivingWorldComponent c, CompSettlementLife town, TownState state)
        {
            state.Set(200f, 1f);
            state.Sacked(daysAgo: 1f, severity: 1f, famine: 10, foodShare: 0.1f);
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool was = stored.townMaps;
            stored.townMaps = false;
            try
            {
                Vanilla(bad, "town maps off", GenerateAndRemove(town.Settlement), "setting");
            }
            finally
            {
                stored.townMaps = was;
            }
            bool prepared = c.preparedForRemoval;
            c.preparedForRemoval = true;
            try
            {
                Vanilla(bad, "prepared for removal", GenerateAndRemove(town.Settlement), "setting");
            }
            finally
            {
                c.preparedForRemoval = prepared;
            }
        }

        /// <summary>A tag of a quest that does not exist leaves the town eligible; a live quest that names a settlement keeps it out.</summary>
        private static void QuestOnMap(List<string> bad, LivingWorldComponent c, CompSettlementLife town)
        {
            Settlement ts = town.Settlement;
            int id = 900000;
            while (Find.QuestManager.QuestsListForReading.Exists(q => q.id == id)) id++;
            List<string> original = ts.questTags;
            ts.questTags = new List<string>(original ?? new List<string>()) { $"Quest{id}.settlement" };
            try
            {
                if (SettlementOps.QuestRelated(ts) || SettlementOps.Protected(ts) || !TownMaps.Eligible(ts)) bad.Add("townmap (quest tags): a stale tag still ties the town to a quest");
                Applied(bad, "stale quest tag", GenerateAndRemove(ts));
            }
            finally
            {
                ts.questTags = original;
            }
            int tagged = 0;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                if (s.questTags.NullOrEmpty()) continue;
                tagged++;
                foreach (string tag in s.questTags)
                    foreach (Quest q in Find.QuestManager.QuestsListForReading)
                        if (!q.Historical && tag.StartsWith($"Quest{q.id}.", StringComparison.Ordinal) && (!SettlementOps.QuestRelated(s) || TownMaps.Eligible(s)))
                            bad.Add($"townmap (quest tags): {s.LabelCap} is named by the live quest {q.id} but is not kept out");
            }
            Log.Message($"{Tag}quest tags: {tagged} tagged settlements in the world");
        }

        /// <summary>
        /// A second gen step with no def (the abandoned colony mutator's kind) next to the town's own: the town's stats come from its own run only,
        /// and the second is refused for having no def (checked only if its scatterer found a place to run).
        /// </summary>
        private static void BareGenStep(List<string> bad, LivingWorldComponent c, CompSettlementLife town)
        {
            Settlement ts = town.Settlement;
            Faction other = c.factionStates.Select(fs => fs.faction).FirstOrDefault(f => f != null && f != ts.Faction && !f.IsPlayer && !f.defeated && f != Faction.OfEmpire);
            if (other == null)
            {
                Log.Message(Tag + "no second faction for the bare gen step case (skipped)");
                return;
            }
            var step = new GenStep_Settlement { generatePawns = false, overrideFaction = other, count = 1, warnOnFail = false };
            var def = new GenStepDef { defName = "LW_TestBareSettlement", order = 500f, genStep = step };
            Generated g = GenerateAndRemove(ts, new[] { new GenStepWithParams(def, default) });
            if (!Applied(bad, "bare gen step", g)) return;
            if (g.entries >= 2)
            {
                if (g.skip != "def null") bad.Add($"townmap (bare gen step): the second step ran and was skipped for '{g.skip ?? "none"}', expected 'def null'");
            }
            else Log.Message($"{Tag}bare gen step found no place to run (SKIP of the skip reason; entries {g.entries})");
        }

        /// <summary>Another mod skipping the resolver: the context opens, nothing is applied (no scars, thinning or hunger either, the town being sacked and starving), vanilla's rect stands and nothing logs an error.</summary>
        private static void SkippedResolver(List<string> bad, CompSettlementLife town, TownState state)
        {
            state.Set(200f, 1f);
            state.Sacked(daysAgo: 1f, severity: 1f, famine: 10, foodShare: 0.1f);
            TownMaps.TestSkipResolve = true;
            try
            {
                Vanilla(bad, "skipped resolver", GenerateAndRemove(town.Settlement), null);
            }
            finally
            {
                TownMaps.TestSkipResolve = false;
            }
        }

        /// <summary>An Empire town (Royalty only): its grounds are never resized, its landing pad is there and nothing logs an error.</summary>
        private static void EmpireTown(List<string> bad, LivingWorldComponent c)
        {
            if (!ModsConfig.RoyaltyActive || Faction.OfEmpire == null)
            {
                Log.Message(Tag + "no Royalty: Empire case skipped");
                return;
            }
            CompSettlementLife town = FindMapFixture(c, empire: true);
            if (town == null)
            {
                Log.Message(Tag + "no active Empire town for the Empire case (skipped)");
                return;
            }
            var state = new TownState(town);
            try
            {
                state.Set(600f, 1f);
                Generated g = GenerateAndRemove(town.Settlement);
                if (!Applied(bad, "empire city", g)) return;
                if (g.stats.rect != g.stats.vanillaRect) bad.Add($"townmap (empire): the grounds were resized from {g.stats.vanillaRect} to {g.stats.rect}");
                if (g.landingBeacons == 0) bad.Add("townmap (empire): no landing pad on the map");
            }
            finally
            {
                state.Restore();
            }
        }
    }
}
