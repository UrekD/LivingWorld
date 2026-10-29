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
            AutoTest.DailyMetrics.Add(TerritoryMetrics);
            AutoTest.DailyMetrics.Add(OverlayChecks.Metrics);
            AutoTest.Checks.Add(Invariants);
            AutoTest.Checks.Add(TerritoryProblems);
            AutoTest.Checks.Add(TerritoryChecks.Controlled);
            TownMapChecks.Register();
            LegendsChecks.Register();
            AutoTest.Checks.Add(OverlayChecks.Controlled);
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
                // The picture is built when the load finishes, so a paused game has its borders before the first tick.
                if (LivingWorldMod.Settings.borders && !Territory.Ready) problems.Add("borders not ready after loading a save");
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
            double pop = 0, gar = 0, stock = 0, scarSum = 0;
            long scarTicks = 0;
            int init = 0, scars = 0;
            foreach (Settlement st in Find.WorldObjects.Settlements)
            {
                CompSettlementLife s = st.GetComponent<CompSettlementLife>();
                if (s == null || !s.initialized) continue;
                init++;
                pop += s.population;
                gar += s.garrison;
                foreach (float v in s.stock) stock += v;
                if (s.sackedTicks == FactionState.Never) continue;
                scars++;
                scarSum += s.sackSeverity;
                scarTicks += s.sackedTicks;
            }
            double treasury = 0;
            int traits = 0;
            foreach (FactionState fs in c.factionStates)
            {
                treasury += fs.treasury;
                traits += fs.traits.Count;
            }
            // Held towns: the count and each record's state in order, the factions made for them and the budget spent; each one's rate, payments made and days into the period.
            string held = $"{c.tributaries.Count}:{string.Join(",", c.tributaries.Select(r => (int)r.state))}:{c.clientFactionIds.Count}:{c.clientFactionsCreated}:{string.Join(",", c.tributaries.Select(r => (int)r.rate))}:{c.tributaries.Sum(r => r.payments)}:{string.Join(",", c.tributaries.Select(r => r.accrualDays.ToString("F1")))}";
            return $"settlements={init} pop={pop:F1} garrison={gar:F1} stock={stock:F1} factions={c.factionStates.Count} traits={traits} treasury={treasury:F1} chronicle={c.chronicle.Count} scars={scars} scarSum={scarSum:F4} scarTicks={scarTicks} tributaries={held}";
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
                        $"pairs hostile/neutral/ally={hostile}/{neutral}/{ally} avgGoodwill={(pairs == 0 ? 0 : gw / pairs):F0} parties={WorldParty.All.Count} " +
                        $"settled={Expansion.StatSettled} settleRejected={Expansion.StatSettleRejected} {Territory.Summary()} {Roads.Summary()}");
        }

        /// <summary>
        /// The final history line: the counts the balance targets of the DLC steps compare against the golden files (specs/dlc.md, D0). Printed
        /// last of the history lines; later steps append their own lines, this one stays as it is.
        /// </summary>
        public static void LogTotals(LivingWorldComponent c) =>
            Log.Message($"[LivingWorld:history] totals rebellions={c.rebellions} liberations={HistoryTotals.Liberations} liberationsOccupied={HistoryTotals.LiberationsOccupied} " +
                        $"risingsOccupied={HistoryTotals.RisingsOccupied} warsStarted={HistoryTotals.WarsStarted} battles={HistoryTotals.Battles} battlesEmpire={HistoryTotals.BattlesEmpire} " +
                        $"captures={HistoryTotals.Captures} razes={HistoryTotals.Razes} defeated={HistoryTotals.Defeated}");

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

        /// <summary>The territory snapshot's cost (rebuild ms, the overlay's diff included) and the border layer's regenerations: last, full, incremental, regions touched.</summary>
        private static string TerritoryMetrics()
        {
            if (!Territory.Ready) return null;
            var layer = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>();
            return $"{Territory.Summary()}" + (layer == null ? "" : $" layer regen ms={layer.LastRegenMs:F1} regens={layer.Regens} fullMs={layer.LastFullRegenMs:F1} fulls={layer.FullRegens} " +
                                                                    $"incrementalMs={layer.LastIncrementalRegenMs:F2} incrementals={layer.IncrementalRegens} regions={layer.LastRegionsRebuilt}/{layer.RegionCount} " +
                                                                    $"syncMs={layer.LastSyncMs:F2} submeshes={layer.SubMeshCount} drawn={layer.DrawnSubMeshCount}");
        }

        /// <summary>
        /// Territory against a freshly committed snapshot (the daily one can be a day old): own tiles, no dead owners, counts,
        /// exact once-only edge counts, surface-only queries, independence of source order, a forced capture, and the layer.
        /// </summary>
        private static IEnumerable<string> TerritoryProblems()
        {
            var bad = new List<string>();
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (!LivingWorldMod.Settings.borders || System.Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") == "1")
            {
                Log.Message("[LivingWorld:test] territory: off, not checked");
                return bad;
            }
            Territory.Rebuild(c);
            TerritorySnapshot snap = Territory.Current;
            if (snap == null)
            {
                bad.Add("territory: no snapshot after a rebuild");
                return bad;
            }
            PlanetLayer surface = Find.WorldGrid.Surface;
            int wrong = 0, simulated = 0;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                CompSettlementLife life = s.GetComponent<CompSettlementLife>();
                if (life == null || !life.Active) continue;
                simulated++;
                if (Territory.OwnerOf(s.Tile) != s || Territory.FactionOf(s.Tile) != s.Faction) wrong++;
            }
            if (wrong > 0) bad.Add($"territory: {wrong} of {simulated} settlements do not own their own tile");
            int dead = 0, sum = 0;
            foreach (int t in snap.Claimed)
            {
                WorldObject o = snap.sources[snap.SourceIndexAt(t)].obj;
                if (o.Destroyed || !o.Spawned) dead++;
            }
            foreach (int n in snap.tileCounts) sum += n;
            if (dead > 0) bad.Add($"territory: {dead} tiles owned by a destroyed source");
            if (sum != snap.claimedCount) bad.Add($"territory: counts sum to {sum}, {snap.claimedCount} tiles claimed");
            BorderProblems(bad, snap, "fresh");
            // Layer-local tile ids: the same number on another layer, past the end and the invalid tile own nothing.
            if (snap.claimedCount > 0)
            {
                int id = snap.Claimed[0];
                foreach (KeyValuePair<int, PlanetLayer> kv in Find.WorldGrid.PlanetLayers)
                    if (kv.Value != surface && Territory.OwnerOf(new PlanetTile(id, kv.Value)) != null) bad.Add($"territory: tile {id} of layer {kv.Key} reads the surface owner");
                if (Territory.OwnerOf(new PlanetTile(id, surface)) == null) bad.Add($"territory: claimed tile {id} has no owner");
            }
            if (Territory.OwnerOf(new PlanetTile(surface.TilesCount, surface)) != null || Territory.OwnerOf(PlanetTile.Invalid) != null || Territory.FactionOf(new PlanetTile(-5, surface)) != null)
                bad.Add("territory: out-of-bounds tile has an owner");
            // The result must not depend on the order sources are listed in.
            var byTile = new Dictionary<int, int>();
            foreach (int t in snap.Claimed) byTile[t] = snap.sources[snap.SourceIndexAt(t)].objectId;
            int claimedBefore = snap.claimedCount;
            Territory.Rebuild(c, reverseSources: true);
            snap = Territory.Current;
            int differ = 0;
            foreach (int t in snap.Claimed)
                if (!byTile.TryGetValue(t, out int oid) || oid != snap.sources[snap.SourceIndexAt(t)].objectId) differ++;
            if (differ > 0 || snap.claimedCount != claimedBefore) bad.Add($"territory: reversed source order changes {differ} tiles ({claimedBefore} → {snap.claimedCount})");
            Territory.Rebuild(c);
            var layer = surface.GetLayer<WorldDrawLayer_LWBorders>();
            if (layer == null) bad.Add("territory: the border draw layer is missing from the surface layer (Patches/Borders.xml)");
            else
            {
                layer.RegenerateNow(); // what the map shows before the capture: the baseline the incremental update works from
                // Headless, this is the layer's first regeneration: every occupied region's meshes are new and must be finalized now.
                if (layer.GeometryProblem() is string coldGeo) bad.Add("territory (first regeneration): " + coldGeo);
                if ((Territory.Current?.claimedCount ?? 0) > 0 && layer.LastVerts == 0) bad.Add("territory (first regeneration): claimed land but no geometry");
                Territory.Rebuild(c);
                if (layer.Dirty) bad.Add("territory: a rebuild that changed nothing marked the layer dirty");
            }
            int baseVerts = layer?.LastVerts ?? 0, baseTris = layer?.LastTris ?? 0;
            // A forced capture (no economy side effects): the tile's faction flips, the pair counts follow, the revision moves; then undone.
            if (!FindCapture(out Settlement victim, out Faction taker)) Log.Message("[LivingWorld:test] territory: no settlement to capture in the test");
            else
            {
                Faction owner = victim.Faction;
                int rev = Territory.Revision, ownerBorders = Bordering(owner), takerBorders = Bordering(taker);
                try
                {
                    victim.SetFaction(taker);
                    Territory.Rebuild(c);
                    if (layer != null) IncrementalMatchesFull(bad, layer, "capture");
                    if (Territory.FactionOf(victim.Tile) != taker) bad.Add("territory: capture did not flip the tile's faction");
                    if (Territory.Revision <= rev) bad.Add("territory: capture did not change the revision");
                    BorderProblems(bad, Territory.Current, "captured");
                    Log.Message($"[LivingWorld:test] territory capture of {victim.LabelCap} ({Territory.TileCount(victim)} tiles) {owner.Name} → {taker.Name}: bordering factions {ownerBorders}→{Bordering(owner)} / {takerBorders}→{Bordering(taker)}, revision {rev}→{Territory.Revision}");
                    rev = Territory.Revision;
                }
                finally
                {
                    victim.SetFaction(owner); // whatever failed above, the world is left as it was
                    Territory.Rebuild(c);
                }
                if (layer != null)
                {
                    IncrementalMatchesFull(bad, layer, "undone capture");
                    if (layer.LastVerts != baseVerts || layer.LastTris != baseTris)
                        bad.Add($"territory: after the capture was undone the layer has {layer.LastVerts} vertices / {layer.LastTris} triangles, before it {baseVerts} / {baseTris}");
                }
                if (Territory.FactionOf(victim.Tile) != owner || Territory.Revision <= rev) bad.Add("territory: undoing the capture did not restore the tile's faction");
            }
            // The overlay: a full regeneration repeated keeps no extra meshes.
            if (layer != null)
            {
                layer.Invalidate();
                layer.RegenerateNow();
                int subMeshes = layer.SubMeshCount;
                double total = 0d;
                for (int i = 0; i < 5; i++)
                {
                    layer.Invalidate();
                    layer.RegenerateNow();
                    total += layer.LastRegenMs;
                }
                if (layer.SubMeshCount != subMeshes) bad.Add($"territory: layer meshes grew from {subMeshes} to {layer.SubMeshCount} over repeated regeneration");
                if (layer.LastRegionsRebuilt != layer.RegionCount || !layer.LastRegenFull) bad.Add("territory: an invalidated layer did not regenerate in full");
                if (layer.GeometryProblem() is string geo) bad.Add("territory layer: " + geo);
                Log.Message($"[LivingWorld:test] territory layer: {layer.LastVerts} vertices / {layer.LastTris} triangles in {layer.SubMeshCount} meshes ({layer.DrawnSubMeshCount} drawn, {layer.RegionCount} regions), " +
                            $"full regen {total / 5:F1} ms (visible now: {layer.Visible})");
            }
            Log.Message($"[LivingWorld:test] {Territory.Summary()} worst rebuild {Territory.WorstRebuildMs:F2} ms");
            return bad;
        }

        /// <summary>
        /// The layer has just been told about a new picture: regenerating what it marked (incrementally) must give the vertex and triangle
        /// totals that a full regeneration of the same picture gives. Logs both costs and the regions touched. Returns whether the first
        /// regeneration was a full one, and how many regions it rebuilt.
        /// </summary>
        internal static (bool full, int regions) IncrementalMatchesFull(List<string> bad, WorldDrawLayer_LWBorders layer, string what)
        {
            layer.RegenerateNow();
            if (layer.GeometryProblem() is string incGeo) bad.Add($"territory ({what}, before the full regeneration): " + incGeo);
            int verts = layer.LastVerts, tris = layer.LastTris, regions = layer.LastRegionsRebuilt;
            bool full = layer.LastRegenFull;
            double ms = layer.LastRegenMs;
            layer.Invalidate();
            layer.RegenerateNow();
            if (layer.GeometryProblem() is string geo) bad.Add($"territory ({what}): " + geo);
            if (verts != layer.LastVerts || tris != layer.LastTris)
                bad.Add($"territory ({what}): the {(full ? "full" : "incremental")} update gives {verts} vertices / {tris} triangles, a full regeneration {layer.LastVerts} / {layer.LastTris}");
            Log.Message($"[LivingWorld:test] territory {what}: {(full ? "full" : "incremental")} regen {ms:F2} ms, {regions} of {layer.RegionCount} regions, {verts} vertices / {tris} triangles; " +
                        $"full regen {layer.LastRegenMs:F1} ms, rebuild {Territory.LastRebuildMs:F2} ms (sync {layer.LastSyncMs:F2} ms)");
            return (full, regions);
        }

        /// <summary>A simulated settlement that owns its tile, and another simulated faction to take it (a forced capture needs a second faction).</summary>
        internal static bool FindCapture(out Settlement victim, out Faction taker)
        {
            victim = null;
            taker = null;
            foreach (Settlement s in Find.WorldObjects.Settlements)
            {
                CompSettlementLife life = s.GetComponent<CompSettlementLife>();
                if (life == null || !life.Active || Territory.OwnerOf(s.Tile) != s) continue;
                foreach (Faction f in Territory.Current.factions)
                    if (f != s.Faction && !f.IsPlayer && FactionState.IsSimulated(f)) taker = f;
                if (taker == null) continue;
                victim = s;
                return true;
            }
            return false;
        }

        private static readonly List<Faction> borderBuf = new List<Faction>();

        private static int Bordering(Faction f)
        {
            Territory.Bordering(f, borderBuf);
            return borderBuf.Count;
        }

        /// <summary>SharedBorder is symmetric, zero against itself, and equals an independent count of tile edges (each once).</summary>
        internal static void BorderProblems(List<string> bad, TerritorySnapshot snap, string when)
        {
            PlanetLayer surface = snap.surface;
            var expected = new Dictionary<long, int>();
            var neighbours = new List<PlanetTile>();
            foreach (int t in snap.Claimed)
            {
                int fa = snap.FactionIdAt(t);
                surface.GetTileNeighbors(t, neighbours);
                foreach (PlanetTile n in neighbours)
                {
                    int fb = snap.FactionIdAt(n.tileId);
                    if (n.tileId <= t || fa < 0 || fb < 0 || fb == fa) continue; // factionless land borders no one
                    long key = Diplomacy.Key(fa, fb);
                    expected[key] = (expected.TryGetValue(key, out int e) ? e : 0) + 1;
                }
            }
            int seen = 0, asymmetric = 0, selfPairs = 0, mismatched = 0;
            for (int i = 0; i < snap.factions.Count; i++)
            {
                Faction a = snap.factions[i];
                if (Territory.SharedBorder(a, a) != 0) selfPairs++;
                for (int j = i + 1; j < snap.factions.Count; j++)
                {
                    Faction b = snap.factions[j];
                    int ab = Territory.SharedBorder(a, b);
                    if (ab != Territory.SharedBorder(b, a)) asymmetric++;
                    expected.TryGetValue(Diplomacy.Key(a.loadID, b.loadID), out int want);
                    if (ab != want) mismatched++;
                    if (want > 0) seen++;
                }
            }
            if (asymmetric > 0) bad.Add($"territory ({when}): {asymmetric} faction pairs with unequal SharedBorder(a,b) and SharedBorder(b,a)");
            if (selfPairs > 0) bad.Add($"territory ({when}): {selfPairs} factions border themselves");
            if (mismatched > 0 || seen != expected.Count) bad.Add($"territory ({when}): {mismatched} pair counts differ from an independent edge count ({seen} of {expected.Count} pairs matched up)");
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
