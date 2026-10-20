using System.Linq;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>Autotest metrics and invariants for the world simulation.</summary>
    public static class WorldChecks
    {
        private static string snapshot;
        private static readonly List<string> problems = new List<string>();
        private static double worstDailyMs;
        private static int slowDays;
        private static readonly List<double> historyDaysMs = new List<double>();

        public static void Register()
        {
            AutoTest.DailyMetrics.Add(Metrics);
            AutoTest.Checks.Add(Invariants);
            AutoTest.BeforeSave.Add(() =>
            {
                snapshot = Snapshot();
                Log.Message($"[LivingWorld:test] before save: {Diagnostics()}");
            });
            // Fairs held in the pre-landing history are over when the colony lands; yearly fairs of the landing twelfth
            // may start on day one, but none can run longer than a week from now.
            AutoTest.OnBegin.Add(() =>
            {
                int fairs = 0, overlong = 0, now = Find.TickManager.TicksGame;
                foreach (CompSettlementLife s in LivingWorldComponent.Instance.activeSettlements)
                {
                    if (s.FairOn) fairs++;
                    if (s.fairUntilTicks > now + GenDate.TicksPerDay * 7) overlong++;
                }
                Log.Message($"[LivingWorld:test] fairs on at landing: {fairs}");
                if (overlong > 0) problems.Add($"{overlong} fairs run longer than a week");
            });
            AutoTest.AfterReload.Add(() =>
            {
                LivingWorldComponent.Instance.RebuildIndexes(seedMissing: false);
                Log.Message($"[LivingWorld:test] after reload: {Diagnostics()}");
                string after = Snapshot();
                if (after != snapshot) problems.Add($"save/load round trip changed state: before [{snapshot}] after [{after}]");
                else Log.Message($"[LivingWorld:test] round trip ok: {after}");
            });
        }

        /// <summary>The player's performance log: the CSV exists, has its header and one line per in-game day so far.</summary>
        public static IEnumerable<string> PerfLogFile()
        {
            string path = PerfLog.FilePath;
            if (path == null || !System.IO.File.Exists(path))
            {
                yield return "performance log: no file";
                yield break;
            }
            string[] lines = System.IO.File.ReadAllLines(path);
            Log.Message($"[LivingWorld:test] performance log {path}: {lines.Length - 1} days; last: {(lines.Length > 1 ? lines[lines.Length - 1] : "-")}");
            if (lines.Length < 3 || !lines[0].StartsWith("realTime,gameTick")) yield return $"performance log: {lines.Length} lines";
        }

        /// <summary>Logged, not compared: the tick and the war bands with their strength and phase.</summary>
        private static string Diagnostics() =>
            $"tick={Find.TickManager.TicksGame} speed={Find.TickManager.CurTimeSpeed} bands=[" + string.Join("; ", WorldParty.All.Where(p => p.kind == LWPartyDefOf.LW_WarBand)
                .Select(p => $"{p.Faction?.Name}:{p.strength:F0}/{p.strengthPaid:F0} {p.phase} deferred={p.deferredParms != null}")) + "]";

        /// <summary>State that must survive a save/load exactly.</summary>
        private static string Snapshot()
        {
            LivingWorldComponent c = LivingWorldComponent.Instance;
            double pop = 0, gar = 0, stock = 0;
            int init = 0;
            foreach (Settlement st in Find.WorldObjects.Settlements)
            {
                CompSettlementLife s = st.GetComponent<CompSettlementLife>();
                if (s == null || !s.initialized) continue;
                init++;
                pop += s.population;
                gar += s.garrison;
                foreach (float v in s.stock) stock += v;
            }
            double treasury = 0;
            int traits = 0;
            foreach (FactionState fs in c.factionStates)
            {
                treasury += fs.treasury;
                traits += fs.traits.Count;
            }
            return $"settlements={init} pop={pop:F1} garrison={gar:F1} stock={stock:F1} factions={c.factionStates.Count} traits={traits} treasury={treasury:F1} chronicle={c.chronicle.Count}";
        }

        /// <summary>Yearly line during history generation (long-run balance).</summary>
        public static void LogYear(int year)
        {
            historyDaysMs.Add(LivingWorldComponent.Instance.lastDailyMs);
            LivingWorldComponent c = LivingWorldComponent.Instance;
            int alive = 0, wars = 0;
            foreach (FactionState fs in c.factionStates)
                if (fs.faction != null && !fs.faction.defeated && fs.settlements.Count > 0) alive++;
            foreach (War w in c.wars)
                if (w.Active) wars++;
            int hostile = 0, neutral = 0, ally = 0;
            float gw = 0f;
            var dip = new List<FactionState>();
            foreach (FactionState fs in c.factionStates)
                if (fs.faction != null && Diplomacy.Diplomatic(fs.faction) && fs.settlements.Count > 0) dip.Add(fs);
            for (int i = 0; i < dip.Count; i++)
                for (int j = i + 1; j < dip.Count; j++)
                {
                    Faction a = dip[i].faction, b = dip[j].faction;
                    gw += a.GoodwillWith(b);
                    switch (a.RelationKindWith(b))
                    {
                        case FactionRelationKind.Hostile: hostile++; break;
                        case FactionRelationKind.Ally: ally++; break;
                        default: neutral++; break;
                    }
                }
            int pairs = hostile + neutral + ally;
            if (year % 5 == 4)
            {
                var sb = new System.Text.StringBuilder("[LivingWorld:history] prices");
                foreach (CommodityDef cd in Commodities.All)
                {
                    float sum = 0f;
                    int high = 0, low = 0;
                    foreach (CompSettlementLife s in c.activeSettlements)
                    {
                        float f = s.PriceFactor(cd);
                        sum += f;
                        if (f > 1.5f) high++;
                        if (f < 0.7f) low++;
                    }
                    sb.Append($" {cd.defName.Replace("LW_", "")}={sum / System.Math.Max(1, c.activeSettlements.Count):F2}(+{high}/-{low})");
                }
                Log.Message(sb.ToString());
            }
            if (year % 5 == 4)
                foreach (FactionState fs in c.factionStates)
                {
                    if (fs.faction == null || fs.settlements.Count == 0) continue;
                    float pros = 0f, cap = 0f, hunger = 0f, garrison = 0f;
                    foreach (CompSettlementLife s in fs.settlements)
                    {
                        pros += s.prosperity;
                        cap += s.populationCapacity;
                        hunger += s.hunger;
                        garrison += s.garrison;
                    }
                    int n = fs.settlements.Count;
                    Log.Message($"[LivingWorld:history] faction {fs.Name} ({KnowHow.Label(fs)}, {string.Join("/", fs.traits.ConvertAll(t => t.defName.Replace("LW_", "")))}) " +
                                $"settlements={n} pop={fs.population:F0} capacity={cap:F0} prosperity={pros / n:F2} hunger={hunger / n:F2} garrison={garrison:F0} treasury={fs.treasury:F0} weariness={fs.warWeariness:F2}");
                }
            Log.Message($"[LivingWorld:history] year={year} {Metrics()} factionsAlive={alive} activeWars={wars} ruins={Find.WorldObjects.DestroyedSettlements.Count} " +
                        $"pairs hostile/neutral/ally={hostile}/{neutral}/{ally} avgGoodwill={(pairs == 0 ? 0 : gw / pairs):F0} parties={WorldParty.All.Count}");
        }

        private static string Metrics()
        {
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (c.lastDailyMs > worstDailyMs && GenDate.DaysPassed > 1) worstDailyMs = c.lastDailyMs;
            if (c.lastDailyMs > 40 && GenDate.DaysPassed > 1) slowDays++;
            float pop = 0f, pros = 0f;
            int famine = 0, n = c.activeSettlements.Count;
            var tiers = new Dictionary<string, int>();
            foreach (CompSettlementLife s in c.activeSettlements)
            {
                pop += s.population;
                pros += s.prosperity;
                if (s.famineDays > 0) famine++;
                tiers.TryGetValue(s.Tier.defName, out int k);
                tiers[s.Tier.defName] = k + 1;
            }
            var t = new List<string>();
            foreach (KeyValuePair<string, int> kv in tiers) t.Add($"{kv.Key.Replace("LW_", "")}={kv.Value}");
            return $"world settlements={n} pop={pop:F0} prosperity={(n == 0 ? 0 : pros / n):F2} famine={famine} {string.Join(" ", t)} chronicle={c.chronicle.Count} dailyMs={c.lastDailyMs:F2}";
        }

        private static IEnumerable<string> Invariants()
        {
            foreach (string p in problems) yield return p;
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (c.lastDailyTick < 0 && System.Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") != "1") yield return "daily step never ran";
            // Every ruler has a temperament and a goal; log what they want.
            if (System.Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") != "1")
            {
                var goals = new Dictionary<RulerGoalKind, int>();
                int unrolled = 0, houseless = 0, maxGenerations = 0;
                foreach (FactionState fs in c.factionStates)
                {
                    if (fs.faction == null || fs.faction.defeated || fs.faction.leader == null) continue;
                    if (fs.rulerTraits.Count != 2) unrolled++;
                    goals[fs.goal] = goals.TryGetValue(fs.goal, out int n) ? n + 1 : 1;
                    Log.Message($"[LivingWorld:test] ruler of {fs.faction.Name}: {fs.faction.leader.Name.ToStringShort}{Rulers.HouseLabel(fs)} — {Rulers.TraitsLabel(fs)}; {Rulers.GoalLabel(c, fs)} (progress {fs.goalProgress})");
                    if (fs.faction.leader.Name is NameTriple nt && nt.IsValid && nt.Last != fs.house) houseless++;
                    maxGenerations = System.Math.Max(maxGenerations, fs.houseGenerations);
                }
                Log.Message($"[LivingWorld:test] ruler goals: {string.Join(", ", goals.Select(kv => kv.Key + "=" + kv.Value))}");
                var growth = c.activeSettlements.GroupBy(x => x.growthState).Select(g => g.Key + "=" + g.Count());
                Log.Message($"[LivingWorld:test] growth states: {string.Join(", ", growth)}");
                if (unrolled > 0) yield return $"{unrolled} rulers without a temperament";
                Log.Message($"[LivingWorld:test] ruling houses: longest line {maxGenerations}, new houses {c.chronicle.entries.Count(e => e.def == LWChronicleDefOf.LW_NewHouse)}");
                if (houseless > 0) yield return $"{houseless} rulers not of their faction's ruling house";
            }
            int uninit = 0, bad = 0;
            foreach (Settlement st in Find.WorldObjects.Settlements)
            {
                CompSettlementLife s = st.GetComponent<CompSettlementLife>();
                if (s == null) { bad++; continue; }
                if (!CompSettlementLife.IsSimulatable(st)) continue;
                if (!s.initialized) uninit++;
                if (float.IsNaN(s.population) || s.population < Tuning.T.minPopulation - 0.01f || s.population > Tuning.T.maxPopulation + 0.01f) bad++;
                if (float.IsNaN(s.prosperity) || s.prosperity < 0f || s.prosperity > 1f) bad++;
                foreach (float v in s.stock)
                    if (float.IsNaN(v) || v < 0f) bad++;
            }
            if (uninit > 0) yield return $"{uninit} simulatable settlements not initialized";
            if (bad > 0) yield return $"{bad} settlement values out of range (NaN/negative/missing comp)";
            foreach (FactionState fs in c.factionStates)
                if (float.IsNaN(fs.treasury) || fs.treasury < 0f) yield return $"faction {fs.Name} treasury {fs.treasury}";
            if (AutoTest.Plan == "stress") Log.Message($"[LivingWorld:test] stress: settlements {c.activeSettlements.Count}, sampled history day ms {string.Join(", ", historyDaysMs.ConvertAll(x => x.ToString("F1")))}");
            // One slow day can be the machine (parallel test games, a GC pause); two are the code.
            if (slowDays >= 2) yield return $"daily step over budget on {slowDays} days (worst {worstDailyMs:F1} ms, budget 40)";
            Log.Message($"[LivingWorld:test] worst daily step {worstDailyMs:F2} ms");
        }
    }
}
