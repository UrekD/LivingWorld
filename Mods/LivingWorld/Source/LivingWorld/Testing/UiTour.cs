using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// Plan "uitour" (windowed runs): steps through the mod's UI on a real-time timer and logs
    /// "[LivingWorld:ui] step N name" so tools/uitour.sh can screenshot each step. Quits at the end.
    /// </summary>
    public static class UiTour
    {
        private const float StepSeconds = 25f;
        private static int step = -1;
        private static float nextAt;
        private static readonly List<(string name, System.Action action)> steps = new List<(string, System.Action)>();
        private const int TownMaxLinks = 8;
        private const int BareLinks = 5;
        private const float TradeShotTiles = 12f;
        private static Caravan roadCaravan;
        private static readonly List<(int a, int b, RoadDef was)> roadLinks = new List<(int, int, RoadDef)>();

        public static void Update()
        {
            if (steps.Count == 0) Build();
            if (Time.realtimeSinceStartup < nextAt) return;
            step++;
            if (step >= steps.Count)
            {
                CleanUpRoads();
                CleanUpTownShots();
                Log.Message("[LivingWorld:ui] done");
                Log.Message($"[LivingWorld:test] {(AutoTest.ErrorCount == 0 ? "PASS" : "FAIL")} plan=uitour errors={AutoTest.ErrorCount} {string.Join(" | ", AutoTest.FirstErrors)}");
                Root.Shutdown();
                return;
            }
            try
            {
                steps[step].action();
                Log.Message($"[LivingWorld:ui] step {step} {steps[step].name}");
            }
            catch (System.Exception e)
            {
                Log.Error($"[LivingWorld] ui tour step {steps[step].name} failed: {e}");
            }
            nextAt = Time.realtimeSinceStartup + StepSeconds;
        }

        private static void Build()
        {
            MainButtonDef chronicle = DefDatabase<MainButtonDef>.GetNamed("LW_Chronicle");
            steps.Add(("map", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                // An envoy on its way, so the Factions page shows one.
                Faction foe = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(f => PlayerDiplomacy.CannotSend(f) == null && f.HostileTo(Faction.OfPlayer));
                WorldParty envoy = foe == null ? null : PlayerDiplomacy.Send(foe, EnvoyMission.PlayerPeace);
                Log.Message($"[LivingWorld:ui] envoy to {foe?.Name ?? "nobody"}: {(envoy != null ? PlayerDiplomacy.EnvoyStatus(envoy) : "not sent")}");
                // A blood feud with the faction the diplomacy dialog will show (People, Legends and the blood price offer).
                Faction feudFaction = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => PlayerDiplomacy.CannotSend(x) == null && x.HostileTo(Faction.OfPlayer));
                Settlement town = feudFaction == null ? null : Find.WorldObjects.Settlements.FirstOrDefault(x => x.Faction == feudFaction);
                Pawn slayer = Find.CurrentMap?.mapPawns.FreeColonistsSpawned.FirstOrDefault();
                if (town != null && slayer != null)
                {
                    Pawn warlord = PawnGenerator.GeneratePawn(new PawnGenerationRequest(feudFaction.RandomPawnKind(), feudFaction, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                        fixedBiologicalAge: 50f, fixedChronologicalAge: 50f));
                    Find.WorldPawns.PassToWorld(warlord);
                    Figures.FindFor(town.ID, FigureRole.Warlord)?.pawn?.Kill(null);
                    Figures.Register(warlord, FigureRole.Warlord, town);
                    warlord.Kill(new DamageInfo(DamageDefOf.Cut, 500f, instigator: slayer));
                    Log.Message($"[LivingWorld:ui] feud with {feudFaction.Name}: {Feuds.HasOpenFeud(feudFaction)}");
                }
            }));
            string[] pages = { "news", "factions", "relations", "wars", "settlements", "markets", "people", "legends" };
            for (int i = 0; i < pages.Length; i++)
            {
                int index = i;
                steps.Add(("chronicle-" + pages[i], () =>
                {
                    // The Markets page is left on its prices view again, and what the routes view did is logged.
                    if (pages[index] == "people") Log.Message($"[LivingWorld:ui] {TradePlanner.MetricsLine()}");
                    Page_Markets.View = Page_Markets.MarketsView.Prices;
                    Find.MainTabsRoot.SetCurrentTab(chronicle, playSound: false);
                    Find.WindowStack.WindowOfType<MainTabWindow_Chronicle>()?.SelectPage(index);
                }));
                if (pages[i] != "markets") continue;
                steps.Add(("markets-routes", () =>
                {
                    Page_Markets.View = Page_Markets.MarketsView.Routes;
                    Find.MainTabsRoot.SetCurrentTab(chronicle, playSound: false);
                    Find.WindowStack.WindowOfType<MainTabWindow_Chronicle>()?.SelectPage(index);
                }));
            }
            steps.Add(("diplomacy-dialog", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                Faction f = Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => PlayerDiplomacy.CannotSend(x) == null && x.HostileTo(Faction.OfPlayer))
                            ?? Find.FactionManager.AllFactionsListForReading.FirstOrDefault(x => PlayerDiplomacy.CannotSend(x) == null);
                if (f != null) Find.WindowStack.Add(new Dialog_Diplomacy(f));
            }));
            steps.Add(("world-warband", () =>
            {
                Find.WindowStack.TryRemove(typeof(Dialog_Diplomacy), doCloseSound: false);
            }));
            steps.Add(("world-warband-select", () =>
            {
                Find.MainTabsRoot.EscapeCurrentTab(playSound: false);
                // A marching war band out in the open reads best; one sitting in a town hides under its icon.
                WorldParty p = WorldParty.All.FirstOrDefault(x => x.kind == LWPartyDefOf.LW_WarBand && Marching(x) && TilesToNearestSettlement(x) >= 3f)
                               ?? WorldParty.All.FirstOrDefault(x => x.kind == LWPartyDefOf.LW_WarBand) ?? WorldParty.All.FirstOrDefault();
                if (p != null) CameraJumper.TryJumpAndSelect(p);
                else CameraJumper.TryShowWorld();
            }));
            steps.Add(("world-settlement", () =>
            {
                Find.WorldSelector.ClearSelection();
                Map home = Find.AnyPlayerHomeMap;
                // The avenger's town (its tab shows the oath), else the nearest town.
                Figure avenger = LivingWorldComponent.Instance.figures.FirstOrDefault(x => x.FeudOpen && x.Alive);
                Settlement s = (avenger == null ? null : LivingWorldComponent.WorldObjectById(avenger.homeId) as Settlement)
                    ?? LivingWorldComponent.Instance.activeSettlements.Select(c => c.Settlement)
                    .OrderBy(x => home == null ? 0f : Find.WorldGrid.ApproxDistanceInTiles(home.Tile, x.Tile)).FirstOrDefault();
                if (s != null) CameraJumper.TryJumpAndSelect(s);
            }));
            steps.Add(("world-settlement-tab", () =>
            {
                WorldObject sel = Find.WorldSelector.SingleSelectedObject;
                if (sel != null) Find.World.UI.inspectPane.OpenTabType = typeof(WITab_SettlementLife);
            }));
            steps.Add(("world-overview", () =>
            {
                Find.WorldSelector.ClearSelection();
                CameraJumper.TryShowWorld();
            }));
            steps.Add(("world-borders", () =>
            {
                // The overlay on, centred on the colony and its neighbours.
                WorldOverlay.SetMode(WorldOverlayMode.Borders);
                Find.WorldSelector.ClearSelection();
                Map home = Find.AnyPlayerHomeMap;
                if (home != null) CameraJumper.TryJump(home.Tile);
                Log.Message($"[LivingWorld:ui] {Territory.Summary()}");
            }));
            steps.Add(("world-overlay-prosperity", () => OverlayShot(WorldOverlayMode.Prosperity, null)));
            steps.Add(("world-overlay-shortage", () => OverlayShot(WorldOverlayMode.Shortage, LWDefOf.LW_Food)));
            steps.Add(("world-overlay-war", () => OverlayShot(WorldOverlayMode.WarFronts, null)));
            steps.Add(("world-overlay-trade", () => OverlayShot(WorldOverlayMode.Trade, null)));
            steps.Add(("world-overlay-menu", () =>
            {
                // The button's menu over the trade routes map (the second menu, for the good, is the same list with a check mark on food).
                Find.WindowStack.Add(new FloatMenu(WorldOverlay.MenuOptions()));
                Log.Message($"[LivingWorld:ui] overlay menu: {string.Join(" | ", WorldOverlay.MenuEntries().Select(e => e.label + (e.ticked ? " [current]" : "")))}; shortage menu has {WorldOverlay.ShortageMenuEntries().Count} entries");
            }));
            steps.Add(("settings", () =>
            {
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                WorldOverlay.SetMode(WorldOverlayMode.Borders); // the overlay is left as it was before the heatmap and war steps
                CameraJumper.TryHideWorld();
                Mod mod = LoadedModManager.GetMod<LivingWorldMod>();
                Find.WindowStack.Add(new Dialog_ModSettings(mod));
            }));
            steps.Add(("world-road-gizmo", () =>
            {
                Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), doCloseSound: false);
                Map home = Neighbourhood.Home;
                PlanetLayer surface = Roads.Surface();
                if (!Roads.Active || home == null || Roads.SurfaceId(surface, home.Tile) < 0)
                {
                    Log.Message("[LivingWorld:ui] roads: off or no colony on the surface, road steps skipped");
                    return;
                }
                // Colonists next to the colony, as the road tests form them, with stone blocks for the stone tier.
                var around = new List<PlanetTile>();
                surface.GetTileNeighbors(home.Tile, around);
                PlanetTile at = PlanetTile.Invalid;
                foreach (PlanetTile n in around)
                    if (Roads.Buildable(surface, n.tileId, home.Tile.tileId) && !Find.WorldObjects.AnyWorldObjectAt(n))
                    {
                        at = n;
                        break;
                    }
                var pawns = new List<Pawn>();
                for (int i = 0; at.Valid && i < 3; i++)
                {
                    Pawn p = RoadChecks.TempColonist();
                    if (p != null) pawns.Add(p);
                }
                if (pawns.Count == 0)
                {
                    Log.Message("[LivingWorld:ui] roads: no free tile beside the colony or no colonist to send, gizmo step skipped");
                    return;
                }
                GiveBlocksForLinks(pawns, 2);
                roadCaravan = RoadChecks.MakeCaravan(at, pawns.ToArray());
                CameraJumper.TryJumpAndSelect(roadCaravan);
                // The build gizmo is only offered while the caravan is the single selection; its click opens the tier menu.
                Command_Action build = roadCaravan.GetComponent<CompRoadBuilder>()?.GetGizmos().OfType<Command_Action>().FirstOrDefault();
                build?.action();
                Log.Message($"[LivingWorld:ui] road gizmo: {(build == null ? "missing" : build.Disabled ? build.disabledReason : "ready")} (blocks {CompRoadBuilder.StoneBlocksOwned(roadCaravan)}, mass {roadCaravan.MassUsage:F0}/{roadCaravan.MassCapacity:F0})");
            }));
            steps.Add(("world-road-built", () =>
            {
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                Find.WorldSelector.ClearSelection();
                RoadChecks.DisposeCaravan(roadCaravan);
                roadCaravan = null;
                Map home = Neighbourhood.Home;
                PlanetLayer surface = Roads.Surface();
                if (!Roads.Active || home == null || Roads.SurfaceId(surface, home.Tile) < 0) return;
                List<int> route = RoadSite(surface, home, out Settlement town);
                if (route.Count < 3)
                {
                    Log.Message("[LivingWorld:ui] roads: no bare land beside the colony, built-road step skipped");
                    return;
                }
                for (int i = 0; i + 1 < route.Count; i++)
                {
                    RoadDef was = Roads.RoadBetween(route[i], route[i + 1]);
                    if (Roads.LaySegment(route[i], route[i + 1], LWRoadDefOf.StoneRoad, player: true) == LayResult.Changed) roadLinks.Add((route[i], route[i + 1], was));
                }
                Roads.FlushRedraw();
                CameraJumper.TryShowWorld();
                CameraJumper.TryJump(new PlanetTile(route[route.Count / 2], surface));
                // The joined town's inspect pane carries the "Joined to <colony> by road" line.
                if (town != null) Find.WorldSelector.Select(town, playSound: false);
                Log.Message($"[LivingWorld:ui] roads layer dirty after the flush: {surface.GetLayer<WorldDrawLayer_Roads>()?.Dirty.ToString() ?? "no layer"}");
                Log.Message($"[LivingWorld:ui] road of {route.Count - 1} links ({roadLinks.Count} laid), town {town?.Label ?? "none"}: {(town == null ? "-" : Roads.InspectLine(town) ?? "not joined")}");
            }));
            // The nearest town's map as an attack would find it: healthy stores, then sacked and starving. Each map goes at the next step (and at the end).
            steps.Add(("town-healthy", () =>
            {
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                Find.WorldSelector.ClearSelection();
                TownShots.Show(sacked: false);
            }));
            steps.Add(("town-sacked", () => TownShots.Show(sacked: true)));
        }

        /// <summary>At the end the tour hands the world back: a town map still on screen goes, the colony's map is current again.</summary>
        private static void CleanUpTownShots()
        {
            try
            {
                TownShots.Clear();
            }
            catch (System.Exception e)
            {
                Log.Error($"[LivingWorld] ui tour town shot clean-up failed: {e}");
            }
        }

        /// <summary>A heatmap on, centred on the colony, or the war fronts on, centred on something that shows them (see <see cref="WarShotTarget"/>), with how many towns are in each band (or the wars, fronts, battles and lines) logged.</summary>
        private static void OverlayShot(WorldOverlayMode mode, CommodityDef commodity)
        {
            Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
            WorldOverlay.SetMode(mode, commodity);
            Find.WorldSelector.ClearSelection();
            CameraJumper.TryShowWorld();
            Map home = Find.AnyPlayerHomeMap;
            if (mode == WorldOverlayMode.WarFronts)
            {
                // Wars, battles and marching bands are rarely near the colony: the shot is centred on the nearest thing that has one, at the game's playing zoom
                // (close enough to see a front's band and a marker's halo, which the expanded town icons at a distance would hide).
                PlanetTile at = WarShotTarget(home, out string why);
                if (at.Valid)
                {
                    Find.WorldCameraDriver.ResetAltitude();
                    CameraJumper.TryJump(at);
                }
                Log.Message($"[LivingWorld:ui] war shot centred on: {why}");
            }
            else if (mode == WorldOverlayMode.Trade)
            {
                PlanetTile at = TradeShotTarget(home, out string why);
                if (at.Valid)
                {
                    Find.WorldCameraDriver.ResetAltitude();
                    CameraJumper.TryJump(at);
                }
                Log.Message($"[LivingWorld:ui] trade shot centred on: {why}");
            }
            else if (home != null) CameraJumper.TryJump(home.Tile);
            // Regenerated now, so the towns' bands are worked out and can be logged (the layer would draw them on its next frame anyway).
            var tint = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>();
            if (mode != WorldOverlayMode.Trade) tint?.RegenerateNow(); // Trade routes draws no tint: a full rebuild is wasted
            if (mode == WorldOverlayMode.WarFronts) WarSummary(tint);
            else if (mode == WorldOverlayMode.Trade) TradeSummary();
            else Log.Message($"[LivingWorld:ui] overlay {WorldOverlay.ModeLabel(mode, commodity)}: usable={WorldOverlay.CannotUse(mode) == null} legend={OverlayLegend.SwatchCount(mode, commodity)} swatches, " +
                        $"bands: {WorldOverlay.HeatSummary() ?? "none"}, tint {tint?.LastVerts ?? 0} vertices full regen {tint?.LastFullRegenMs ?? 0d:F1} ms, legend box {OverlayLegend.BoxRect()}");
        }

        /// <summary>
        /// Where the war fronts shot looks: a tile on the border of two factions at war (the one nearest the newest battle marker of the window, else the colony), else
        /// the newest battle marker, else a marching war band that has a line, else the colony. <paramref name="why"/> says which, for the log.
        /// </summary>
        private static PlanetTile WarShotTarget(Map home, out string why)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            var battles = new List<ChronicleEntry>();
            WorldDrawLayer_LWOverlayMarks.ScanBattles(comp?.chronicle, surface, SimClock.TicksAbs, battles, out _, out _);
            // The shot is about the fronts first: the front tile nearest the newest battle marker (so the marker is in the frame when a battle was fought on one), else the
            // one nearest the colony. A marker alone is the fallback for a world with no front.
            ChronicleEntry newest = battles.Count > 0 ? battles[0] : null; // newest first
            TerritorySnapshot snap = Territory.Current;
            if (snap != null && Territory.Ready && WorldOverlay.WarCount > 0)
            {
                PlanetTile reference = newest != null ? newest.tile : home != null ? home.Tile : PlanetTile.Invalid;
                var neighbours = new List<PlanetTile>();
                PlanetTile best = PlanetTile.Invalid;
                float bestDistance = float.MaxValue;
                foreach (int t in snap.Claimed)
                {
                    int own = snap.FactionIdAt(t);
                    surface.GetTileNeighbors(t, neighbours);
                    bool front = false;
                    foreach (PlanetTile nb in neighbours)
                        if (WorldOverlay.AtWar(own, snap.FactionIdAt(nb.tileId))) front = true;
                    if (!front) continue;
                    float d = reference.Valid ? Find.WorldGrid.ApproxDistanceInTiles(reference, new PlanetTile(t, surface)) : 0f;
                    if (d >= bestDistance) continue;
                    bestDistance = d;
                    best = new PlanetTile(t, surface);
                    if (!reference.Valid) break;
                }
                if (best.Valid)
                {
                    why = $"a tile on the border of two factions at war (tile {best.tileId}, {WorldOverlay.WarCount} wars; " +
                          (newest != null ? $"{bestDistance:F1} tiles from the newest battle marker, {battles.Count} in the window)" : "no battle marker in the window)");
                    return best;
                }
            }
            if (newest != null)
            {
                WorldDrawLayer_LWOverlayMarks.TryKindOf(newest, out BattleKind kind);
                why = $"the newest battle marker ({kind}, {(SimClock.TicksAbs - newest.ticksAbs) / (float)GenDate.TicksPerDay:F1} days old, tile {newest.tile.tileId}; {battles.Count} in the window; no front found)";
                return newest.tile;
            }
            WorldParty band = WorldParty.All.FirstOrDefault(x => x.kind == LWPartyDefOf.LW_WarBand && x.phase == PartyPhase.Outbound && x.Spawned && x.Tile.Valid && x.Tile.Layer == surface
                                                                 && x.GoalTile.Valid && x.GoalTile.Layer == surface);
            if (band != null)
            {
                why = $"a marching war band (tile {band.Tile.tileId}, to tile {band.GoalTile.tileId})";
                return band.Tile;
            }
            why = home != null ? "the colony (no battle in the window, no front between factions at war, no war band marching)" : "nothing: no colony either";
            return home != null ? home.Tile : PlanetTile.Invalid;
        }

        /// <summary>
        /// Where the trade routes shot looks: the colony when a drawn ribbon ends within <see cref="TradeShotTiles"/> tiles of it (the shot is "centred on the colony"), else the
        /// town at the nearer end of the drawn ribbon nearest the colony, else the colony. <paramref name="why"/> says which, for the log.
        /// </summary>
        private static PlanetTile TradeShotTarget(Map home, out string why)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            var pairs = new List<KeyValuePair<long, float>>();
            int n = WorldDrawLayer_LWOverlayMarks.PickPairs(LivingWorldComponent.Instance, surface, pairs);
            PlanetTile best = PlanetTile.Invalid;
            float bestDistance = float.MaxValue;
            if (n > 0 && home != null)
                foreach (KeyValuePair<long, float> kv in pairs)
                {
                    Roads.SplitKey(kv.Key, out int ida, out int idb);
                    foreach (int id in new[] { ida, idb })
                    {
                        if (!(LivingWorldComponent.WorldObjectById(id) is Settlement s)) continue;
                        float d = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Tile);
                        if (d >= bestDistance) continue;
                        bestDistance = d;
                        best = s.Tile;
                    }
                }
            if (home != null && best.Valid && bestDistance <= TradeShotTiles)
            {
                why = $"the colony ({n} ribbons drawn, the nearest ends {bestDistance:F1} tiles away)";
                return home.Tile;
            }
            if (best.Valid)
            {
                why = $"the town nearest the colony on a ribbon ({bestDistance:F1} tiles from the colony; {n} ribbons drawn)";
                return best;
            }
            why = home != null ? $"the colony (no ribbons: {(n == 0 ? "no pair has traffic yet" : "the colony is missing")})" : "nothing: no colony either";
            return home != null ? home.Tile : PlanetTile.Invalid;
        }

        /// <summary>What the trade routes shot should show: the ribbons by width band, the busiest pair's traffic, the merchants' lines and the legend.</summary>
        private static void TradeSummary()
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            var marks = surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            marks?.RegenerateNow();
            int segments = marks?.DrawLines(false, Tuning.T.overlayLineMaxSegments, LWPartyDefOf.LW_Merchants) ?? 0;
            int merchants = WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_Merchants && p.phase == PartyPhase.Outbound);
            RoadState st = LivingWorldComponent.Instance?.roadState;
            float most = 0f;
            if (st != null)
                foreach (float v in st.traffic.Values) most = Mathf.Max(most, v);
            string bands = marks == null ? "-" : $"thin {marks.WidthCounts[0]}, middle {marks.WidthCounts[1]}, wide {marks.WidthCounts[2]}";
            Log.Message($"[LivingWorld:ui] overlay {WorldOverlay.ModeLabel(WorldOverlayMode.Trade)}: usable={WorldOverlay.CannotUse(WorldOverlayMode.Trade) == null} legend={OverlayLegend.SwatchCount(WorldOverlayMode.Trade, null)} swatches, " +
                        $"{st?.traffic.Count ?? 0} pairs with traffic (busiest {most:F0}), {marks?.Pairs ?? 0} ribbons drawn ({bands}; {marks?.LastVerts ?? 0} vertices, regen {marks?.LastRegenMs ?? 0d:F2} ms), " +
                        $"{merchants} merchants marching, {segments} line segments, roads {(Roads.Active ? "on" : "off")}, legend box {OverlayLegend.BoxRect()}");
        }

        /// <summary>What the war fronts shot should show: the wars, the front bands the tint layer drew, the battle markers by kind and the war band lines (the party the tour showed earlier is a marching war band when there is one).</summary>
        private static void WarSummary(WorldDrawLayer_LWBorders tint)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            var marks = surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            marks?.RegenerateNow();
            int segments = marks?.DrawLines(false, Tuning.T.overlayLineMaxSegments) ?? 0;
            int bands = WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_WarBand && p.phase == PartyPhase.Outbound);
            string kinds = marks == null ? "-" : $"repelled {marks.KindCounts[(int)BattleKind.Repelled]}, plundered {marks.KindCounts[(int)BattleKind.Plundered]}, captured {marks.KindCounts[(int)BattleKind.Captured]}, razed {marks.KindCounts[(int)BattleKind.Razed]}";
            Log.Message($"[LivingWorld:ui] overlay {WorldOverlay.ModeLabel(WorldOverlayMode.WarFronts)}: usable={WorldOverlay.CannotUse(WorldOverlayMode.WarFronts) == null} legend={OverlayLegend.SwatchCount(WorldOverlayMode.WarFronts, null)} swatches, " +
                        $"{WorldOverlay.WarCount} wars, {tint?.LastWarEdges ?? 0} front edges on the tint layer ({tint?.LastVerts ?? 0} vertices, full regen {tint?.LastFullRegenMs ?? 0d:F1} ms, territory {(Territory.Ready ? "ready" : "off")}), " +
                        $"{marks?.Battles ?? 0} battle markers of the last {Tuning.T.overlayBattleDays} days ({kinds}), {bands} war bands marching, {segments} line segments, legend box {OverlayLegend.BoxRect()}");
        }

        /// <summary>
        /// Stone blocks for this many links, but no more than the colonists can carry (the caravan stays mobile, so the build gizmo is
        /// enabled in the shot). The temporary colonists' meals go: they are for the live tests and only weigh the caravan down.
        /// </summary>
        private static void GiveBlocksForLinks(List<Pawn> pawns, int links)
        {
            foreach (Pawn p in pawns) p.inventory.innerContainer.ClearAndDestroyContents();
            int left = Tuning.T.roadStoneBlocksPerEdge * links;
            float mass = ThingDefOf.BlocksGranite.BaseMass;
            foreach (Pawn p in pawns)
            {
                int n = Mathf.Min(left, Mathf.FloorToInt(MassUtility.FreeSpace(p) / mass));
                if (n <= 0) continue;
                RoadChecks.Give(p, ThingDefOf.BlocksGranite, n);
                left -= n;
            }
        }

        /// <summary>
        /// A route out of the colony to show: to the nearest friendly town not yet joined when one lies within <see cref="TownMaxLinks"/>
        /// links (the road then joins it), else a bare stretch of <see cref="BareLinks"/> links leading straight away from the colony
        /// (every step to the neighbour farthest from it: the first one that fits curls round the colony, under its icon and territory tint).
        /// </summary>
        private static List<int> RoadSite(PlanetLayer surface, Map home, out Settlement town)
        {
            town = null;
            var route = new List<int>();
            foreach (CompSettlementLife s in LivingWorldComponent.Instance.activeSettlements.OrderBy(x => Find.WorldGrid.ApproxDistanceInTiles(home.Tile, x.Settlement.Tile)).Take(12))
            {
                Faction f = s.Settlement.Faction;
                if (!s.Active || f.HostileTo(Faction.OfPlayer) || f.def.permanentEnemy || Roads.Connected(home, s.Settlement)) continue;
                if (!Roads.PlanRoute(home.Tile, s.Settlement.Tile, TownMaxLinks, route, out RouteReject _)) continue;
                town = s.Settlement;
                return route;
            }
            route.Add(home.Tile.tileId);
            var around = new List<PlanetTile>();
            while (route.Count <= BareLinks)
            {
                int last = route[route.Count - 1], next = -1;
                float farthest = -1f;
                surface.GetTileNeighbors(last, around);
                foreach (PlanetTile n in around)
                {
                    if (route.Contains(n.tileId) || !Roads.Buildable(surface, last, n.tileId) || Roads.RoadBetween(last, n.tileId) != null) continue;
                    float away = Find.WorldGrid.ApproxDistanceInTiles(home.Tile, n);
                    if (away <= farthest) continue;
                    farthest = away;
                    next = n.tileId;
                }
                if (next < 0) break;
                route.Add(next);
            }
            return route;
        }

        /// <summary>At the end of the tour: the road steps' caravan and links are taken away again, the links back to what they were.</summary>
        private static void CleanUpRoads()
        {
            try
            {
                Find.WindowStack.TryRemove(typeof(FloatMenu), doCloseSound: false);
                Find.WorldSelector.ClearSelection();
                RoadChecks.DisposeCaravan(roadCaravan);
                roadCaravan = null;
                for (int i = roadLinks.Count - 1; i >= 0; i--) Roads.RestoreLink(roadLinks[i].a, roadLinks[i].b, roadLinks[i].was);
                roadLinks.Clear();
            }
            catch (System.Exception e)
            {
                Log.Error($"[LivingWorld] ui tour road clean-up failed: {e}");
            }
        }

        private static bool Marching(WorldParty p)
        {
            PlanetTile goal = p.GoalTile;
            return p.Spawned && goal.Valid && goal != p.Tile;
        }

        private static float TilesToNearestSettlement(WorldParty p)
        {
            float best = float.MaxValue;
            foreach (Settlement s in Find.WorldObjects.Settlements)
                if (s.Tile.Layer == p.Tile.Layer) best = Mathf.Min(best, Find.WorldGrid.ApproxDistanceInTiles(p.Tile, s.Tile));
            return best;
        }
    }
}
