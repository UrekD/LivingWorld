using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// The world overlay at the end of a run (normal and stress plans): the borders paint is the picture the borders layer always drew
    /// (colours, band width and vertex counts read back from the meshes), the mode setting migrates (through a real settings file too) and
    /// the all-off set switches both layers off, a mode change made outside a running game touches no layer, the button and its menu, the
    /// marks layer exists (the war fronts: battle markers, the war pairs and bands, lines in segments within a budget, the hidden render guard), the trade routes (traffic counted and decayed with the economy gate, ribbons per pair, merchant lines), the heatmaps (palette per town, incremental redraw equal to a full one, hysteresis, what each
    /// mode needs) and the legend. Each case restores what it touched and runs under <see cref="Guard"/>, so one failing never stops the others.
    /// </summary>
    public static class OverlayChecks
    {
        private const string Tag = "[LivingWorld:test] overlay: ";
        private const double StressFullRegenLimitMs = 400d;
        private static readonly List<PlanetTile> neighbourBuf = new List<PlanetTile>();

        public static IEnumerable<string> Controlled()
        {
            var bad = new List<string>();
            LivingWorldComponent c = LivingWorldComponent.Instance;
            if (c == null || Environment.GetEnvironmentVariable("LIVINGWORLD_TEST_NOSIM") == "1") return bad;
            if (AutoTest.Plan != "normal" && AutoTest.Plan != "stress") return bad;
            Guard(bad, "borders paint", () => WithMode(WorldOverlayMode.Borders, null, () => BordersPaintIsLegacy(bad, c)));
            Guard(bad, "settings", () => Settings(bad, c));
            Guard(bad, "button rule", () => ButtonRule(bad, c));
            Guard(bad, "marks layer", () => WithMode(WorldOverlayMode.Borders, null, () => MarksLayer(bad)));
            Guard(bad, "heat modes", () => HeatModes(bad, c));
            Guard(bad, "heat incremental", () => HeatIncremental(bad, c));
            Guard(bad, "hysteresis", () => Hysteresis(bad, c));
            Guard(bad, "needs", () => Needs(bad, c));
            Guard(bad, "legend", () => Legend(bad));
            Guard(bad, "war set", () => WarSet(bad, c));
            Guard(bad, "war fronts", () => WarFronts(bad, c));
            Guard(bad, "battle markers", () => BattleMarkers(bad, c));
            Guard(bad, "lines", () => Lines(bad, c));
            Guard(bad, "trade gate", () => TradeGate(bad, c));
            Guard(bad, "trade ribbons", () => TradeRibbons(bad, c));
            Guard(bad, "trade lines", () => TradeLines(bad, c));
            Guard(bad, "render guard", () => RenderGuard(bad));
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
                bad.Add($"overlay ({name}): {e.GetType().Name}: {e.Message}");
            }
        }

        private static void Expect(List<string> bad, string group, bool ok, string what)
        {
            if (!ok) bad.Add($"overlay ({group}): {what}");
        }

        // ---- Borders paint ----

        /// <summary>The colours the borders layer drew before painters existed, worked out here independently of <see cref="BordersPaint"/>.</summary>
        private static Color32 LegacyTint(Color c, float shade, float alpha) =>
            new Color32((byte)Mathf.RoundToInt(c.r * shade * 255f), (byte)Mathf.RoundToInt(c.g * shade * 255f), (byte)Mathf.RoundToInt(c.b * shade * 255f), (byte)Mathf.RoundToInt(alpha * 255f));

        private static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        /// <summary>
        /// Every claimed tile of a faction-owned source is drawn with the legacy fill (18 %), edge (shade 0.85, 70 %) and band width 0.2 in its
        /// faction's group, factionless land is not drawn, and the layer's vertex and triangle totals equal an independent count of the legacy
        /// geometry: a fan per tile and one quad per neighbouring tile of another group. The meshes themselves are read back: every vertex of a
        /// tile's fan carries the legacy fill, every vertex of its band quads the legacy edge colour, and the first band is 0.2 of the way to the
        /// tile's centre (the lift removed), so a wrong band colour or width in what is emitted cannot pass.
        /// </summary>
        private static void BordersPaintIsLegacy(List<string> bad, LivingWorldComponent c)
        {
            try
            {
                BordersPaintBody(bad, c);
            }
            finally
            {
                Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>()?.DropSpans(); // the recorded geometry is planet-sized: not kept after the case
            }
        }

        private static void BordersPaintBody(List<string> bad, LivingWorldComponent c)
        {
            if (!Territory.Ready)
            {
                Log.Message(Tag + "no territory snapshot, borders paint case skipped");
                return;
            }
            PlanetLayer surface = Find.WorldGrid.Surface;
            var layer = surface.GetLayer<WorldDrawLayer_LWBorders>();
            if (layer == null)
            {
                bad.Add("overlay (borders paint): the border draw layer is missing from the surface layer (Patches/Borders.xml)");
                return;
            }
            Territory.Rebuild(c);
            TerritorySnapshot snap = Territory.Current;
            layer.RecordSpans = true;
            try
            {
                layer.Invalidate();
                layer.RegenerateNow();
            }
            finally
            {
                layer.RecordSpans = false;
            }
            if (layer.GeometryProblem() is string geo) bad.Add("overlay (borders paint): " + geo);
            int layerVerts = layer.LastVerts, layerTris = layer.LastTris;
            double fullMs = layer.LastFullRegenMs;
            var allVerts = surface.UnsafeVerts;
            var vertOffsets = surface.UnsafeTileIDToVerts_offsets;
            int drawn = 0, wrong = 0, expectedVerts = 0, expectedTris = 0, noSpan = 0, wrongColours = 0, bandTiles = 0, wrongWidth = 0;
            foreach (int t in snap.Claimed)
            {
                TerritorySource src = snap.sources[snap.SourceIndexAt(t)];
                bool has = layer.TryGetVisual(t, out int group, out Color32 fill, out Color32 edge, out float width);
                if (src.faction == null)
                {
                    if (has) wrong++;
                    continue;
                }
                Color32 legacyFill = LegacyTint(src.color, 1f, 0.18f), legacyEdge = LegacyTint(src.color, 0.85f, 0.7f);
                if (!has || group != src.factionId || !Same(fill, legacyFill) || !Same(edge, legacyEdge) || width != 0.2f)
                {
                    wrong++;
                    continue;
                }
                drawn++;
                int n = (t + 1 < vertOffsets.Length ? vertOffsets[t + 1] : allVerts.Length) - vertOffsets[t];
                if (n < 3) continue;
                surface.GetTileNeighbors(t, neighbourBuf);
                int bands = 0;
                foreach (PlanetTile nb in neighbourBuf)
                    if (snap.FactionIdAt(nb.tileId) != src.factionId) bands++;
                expectedVerts += n + 4 * bands;
                expectedTris += n - 2 + 2 * bands;
                if (!layer.TryGetSpan(t, out LayerSubMesh mesh, out int first, out int count) || count != n + 4 * bands)
                {
                    noSpan++;
                    continue;
                }
                for (int j = 0; j < count; j++)
                    if (!Same(mesh.colors[first + j], j < n ? legacyFill : legacyEdge)) wrongColours++;
                if (bands == 0 || count < n + 4) continue;
                bandTiles++;
                Vector3 centre = Vector3.zero;
                for (int j = 0; j < n; j++) centre += Unlift(mesh.verts[first + j], WorldDrawLayer_LWBorders.FillLift);
                centre /= n;
                Vector3 b = Unlift(mesh.verts[first + n + 1], WorldDrawLayer_LWBorders.EdgeLift), inner = Unlift(mesh.verts[first + n + 2], WorldDrawLayer_LWBorders.EdgeLift);
                float reach = (inner - b).magnitude / (centre - b).magnitude;
                if (Mathf.Abs(reach - 0.2f) > 0.01f) wrongWidth++;
            }
            if (wrong > 0) bad.Add($"overlay (borders paint): {wrong} claimed tiles are not drawn as the legacy borders picture (fill, edge, band width or group)");
            if (layerVerts != expectedVerts || layerTris != expectedTris)
                bad.Add($"overlay (borders paint): the layer has {layerVerts} vertices / {layerTris} triangles, the legacy geometry of the same picture {expectedVerts} / {expectedTris}");
            if (noSpan > 0) bad.Add($"overlay (borders paint): {noSpan} drawn tiles have no geometry, or a different number of vertices than a fan and one quad per bordering side");
            if (wrongColours > 0) bad.Add($"overlay (borders paint): {wrongColours} vertices in the meshes carry a colour other than the legacy fill (fan) or edge (bands)");
            if (wrongWidth > 0) bad.Add($"overlay (borders paint): the band of {wrongWidth} tiles is not 0.2 of the way to the tile's centre");
            if (drawn > 0 && bandTiles == 0) bad.Add("overlay (borders paint): no drawn tile has an edge band, so band colour and width were not checked");
            layer.Invalidate();
            layer.RegenerateNow();
            if (layer.LastVerts != layerVerts || layer.LastTris != layerTris)
                bad.Add($"overlay (borders paint): a second full regeneration gives {layer.LastVerts} / {layer.LastTris} instead of {layerVerts} / {layerTris}");
            if (AutoTest.Plan == "stress" && fullMs > StressFullRegenLimitMs) bad.Add($"overlay (borders paint): full regeneration took {fullMs:F0} ms (limit {StressFullRegenLimitMs:F0})");
            Log.Message($"{Tag}borders paint: {drawn} of {snap.claimedCount} claimed tiles drawn, {layerVerts} vertices / {layerTris} triangles (legacy count {expectedVerts} / {expectedTris}), " +
                        $"colours and band width read back on {drawn - noSpan} tiles ({bandTiles} with bands), full regen {fullMs:F1} ms, {layer.RegionCount} regions");
        }

        /// <summary>A vertex as the layer lifted it above the surface, back on the surface.</summary>
        private static Vector3 Unlift(Vector3 v, float lift) => v - v.normalized * lift;

        // ---- Settings ----

        private static void Settings(List<string> bad, LivingWorldComponent c)
        {
            const string g = "settings";
            // The migration: a file without the mode (-1) follows the old checkbox, anything outside the enum is treated as missing, a valid mode wins.
            Expect(bad, g, LivingWorldSettings.MigrateOverlayMode(-1, true) == (int)WorldOverlayMode.Borders, "no mode and showBorders on does not migrate to Borders");
            Expect(bad, g, LivingWorldSettings.MigrateOverlayMode(-1, false) == (int)WorldOverlayMode.Off, "no mode and showBorders off does not migrate to Off");
            Expect(bad, g, LivingWorldSettings.MigrateOverlayMode(99, true) == (int)WorldOverlayMode.Borders, "an out-of-range mode (showBorders on) is not migrated");
            Expect(bad, g, LivingWorldSettings.MigrateOverlayMode(-7, false) == (int)WorldOverlayMode.Off, "a negative mode (showBorders off) is not migrated");
            Expect(bad, g, LivingWorldSettings.MigrateOverlayMode((int)WorldOverlayMode.Off, true) == (int)WorldOverlayMode.Off, "a saved Off was overridden by showBorders");
            Expect(bad, g, LivingWorldSettings.MigrateOverlayMode((int)WorldOverlayMode.Shortage, false) == (int)WorldOverlayMode.Shortage, "a saved valid mode was overridden by showBorders");
            // The property clamps; a fresh settings object starts on Borders with no commodity.
            var scratch = new LivingWorldSettings();
            Expect(bad, g, scratch.overlayMode == WorldOverlayMode.Borders && scratch.overlayCommodity == "", "the defaults are not Borders and no commodity");
            scratch.overlayModeRaw = 99;
            Expect(bad, g, scratch.overlayMode == WorldOverlayMode.Shortage, "99 does not clamp to the last mode");
            scratch.overlayModeRaw = -1;
            Expect(bad, g, scratch.overlayMode == WorldOverlayMode.Off, "-1 does not clamp to Off");
            scratch.overlayMode = WorldOverlayMode.WarFronts;
            Expect(bad, g, scratch.overlayModeRaw == (int)WorldOverlayMode.WarFronts, "the property does not write the raw value");

            // A mode change outside a running game changes the settings object and nothing else; inside one it marks the tint layer and redraws it in full.
            LivingWorldSettings stored = LivingWorldMod.Stored;
            int storedRaw = stored.overlayModeRaw, calls = WorldOverlay.LayerCalls;
            CommodityDef commodity = Commodities.All.FirstOrDefault();
            WorldOverlay.ApplyMode(scratch, WorldOverlayMode.Shortage, commodity, playing: false);
            Expect(bad, g, scratch.overlayMode == WorldOverlayMode.Shortage && scratch.overlayCommodity == (commodity?.defName ?? ""), "ApplyMode did not set the mode and the commodity");
            WorldOverlay.ApplyMode(scratch, WorldOverlayMode.Borders, null, playing: false);
            Expect(bad, g, scratch.overlayCommodity == (commodity?.defName ?? ""), "a switch to Borders did not keep the shortage good (Shortage of steel, Off, Shortage means steel)");
            WorldOverlay.ApplyMode(scratch, WorldOverlayMode.Shortage, null, playing: false);
            Expect(bad, g, scratch.overlayCommodity == "", "Shortage without a good (any) kept the old one");
            WorldOverlay.ApplyMode(scratch, WorldOverlayMode.Borders, null, playing: false);
            Expect(bad, g, WorldOverlay.LayerCalls == calls, "ApplyMode(playing: false) touched a layer");
            Expect(bad, g, stored.overlayModeRaw == storedRaw, "ApplyMode on another settings object changed the player's");
            var tint = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>();
            WorldOverlay.ApplyMode(scratch, WorldOverlayMode.Borders, null, playing: true);
            if (tint != null)
            {
                Expect(bad, g, WorldOverlay.LayerCalls > calls, "ApplyMode(playing: true) touched no layer");
                Expect(bad, g, tint.Dirty, "ApplyMode(playing: true) left the tint layer clean");
                tint.RegenerateNow();
                Expect(bad, g, tint.LastRegenFull, "the tint layer did not redraw in full after a mode change");
            }
            // The shortage commodity: a defName that no longer exists means any.
            string savedCommodity = stored.overlayCommodity;
            try
            {
                stored.overlayCommodity = "LW_NoSuchCommodity";
                Expect(bad, g, WorldOverlay.ShortageCommodity == null, "an unknown commodity defName did not read as any");
                if (commodity != null)
                {
                    stored.overlayCommodity = commodity.defName;
                    Expect(bad, g, WorldOverlay.ShortageCommodity == commodity, "a known commodity defName did not resolve");
                }
            }
            finally
            {
                stored.overlayCommodity = savedCommodity;
            }

            // The all-off set (a save prepared for removing the mod) has the mode off, written explicitly, and neither layer shows. The check must be
            // able to fail: with the player's mode Borders and borders on, the tint mode is on until the save is prepared.
            bool prepared = c.preparedForRemoval, borders0 = stored.borders;
            WorldOverlayMode mode0 = stored.overlayMode;
            try
            {
                stored.overlayMode = WorldOverlayMode.Borders;
                stored.borders = true;
                Expect(bad, g, WorldOverlay.TintMode, "Borders with borders on is not a tint mode before the save is prepared (so the all-off check could not fail)");
                c.preparedForRemoval = true;
                Expect(bad, g, LivingWorldMod.Settings.overlayModeRaw == (int)WorldOverlayMode.Off, "the all-off set does not carry mode Off");
                Expect(bad, g, WorldOverlay.Mode == WorldOverlayMode.Off && !WorldOverlay.TintMode && !WorldOverlay.MarksMode, "preparing the save for removal did not switch the tint mode off");
                var marks = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
                Expect(bad, g, tint == null || !tint.Visible, "the tint layer is visible in the all-off set");
                Expect(bad, g, marks == null || !marks.Visible, "the marks layer is visible in the all-off set");
                foreach (WorldOverlayMode mode in Enum.GetValues(typeof(WorldOverlayMode)))
                    Expect(bad, g, WorldOverlay.CannotUse(mode) != null, $"{mode} can be used in the all-off set");
            }
            finally
            {
                c.preparedForRemoval = prepared;
                stored.overlayMode = mode0;
                stored.borders = borders0;
            }
            SettingsFile(bad);
        }

        /// <summary>
        /// The settings through <c>ExposeData</c> and real files, as the game reads and writes mod settings: a file from before the overlay modes follows
        /// the old checkbox, the mode is always written, and the old checkbox is kept in step (written only when false) for older builds.
        /// </summary>
        private static void SettingsFile(List<string> bad)
        {
            const string g = "settings file";
            if (Scribe.mode != LoadSaveMode.Inactive)
            {
                Log.Message(Tag + "a Scribe session is open, settings file case skipped");
                return;
            }
            string dir = Path.Combine(Path.GetTempPath(), "lw-overlay-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                // (body of the file, mode after loading, written back: the mode, the old checkbox)
                RoundTrip(bad, g, dir, "old file, checkbox off", "<showBorders>False</showBorders>", WorldOverlayMode.Off, showBordersWritten: true);
                RoundTrip(bad, g, dir, "old file, no checkbox", "", WorldOverlayMode.Borders, showBordersWritten: false);
                RoundTrip(bad, g, dir, "mode wins over the checkbox", "<showBorders>False</showBorders><overlayMode>4</overlayMode>", WorldOverlayMode.Prosperity, showBordersWritten: false);
                RoundTrip(bad, g, dir, "explicit Off", "<overlayMode>0</overlayMode>", WorldOverlayMode.Off, showBordersWritten: true);
                RoundTrip(bad, g, dir, "mode out of range", "<overlayMode>99</overlayMode>", WorldOverlayMode.Borders, showBordersWritten: false);
                RoundTrip(bad, g, dir, "shortage good", "<overlayMode>5</overlayMode><overlayCommodity>LW_Food</overlayCommodity>", WorldOverlayMode.Shortage, showBordersWritten: false, commodity: "LW_Food");
                RoundTrip(bad, g, dir, "no shortage good (any: not written)", "<overlayMode>5</overlayMode>", WorldOverlayMode.Shortage, showBordersWritten: false);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        private static void RoundTrip(List<string> bad, string g, string dir, string what, string body, WorldOverlayMode expected, bool showBordersWritten, string commodity = "")
        {
            string file = Path.Combine(dir, "in.xml"), saved = Path.Combine(dir, "out.xml");
            File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SettingsBlock><ModSettings Class=\"LivingWorld.LivingWorldSettings\">" + body + "</ModSettings></SettingsBlock>");
            LivingWorldSettings loaded = null;
            Scribe.loader.InitLoading(file);
            try
            {
                Scribe_Deep.Look(ref loaded, "ModSettings");
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }
            Expect(bad, g, loaded != null && loaded.overlayMode == expected, $"{what}: loaded as {(loaded == null ? "nothing" : loaded.overlayMode.ToString())}, expected {expected}");
            Expect(bad, g, loaded != null && loaded.overlayCommodity == commodity, $"{what}: the shortage good loaded as '{loaded?.overlayCommodity}', expected '{commodity}'");
            if (loaded == null) return;
            Scribe.saver.InitSaving(saved, "SettingsBlock");
            try
            {
                Scribe_Deep.Look(ref loaded, "ModSettings");
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }
            string text = File.ReadAllText(saved);
            Expect(bad, g, text.Contains($"<overlayMode>{(int)expected}</overlayMode>"), $"{what}: the file written back has no <overlayMode>{(int)expected}</overlayMode>");
            Expect(bad, g, text.Contains("<overlayCommodity>") == (commodity != ""), $"{what}: the shortage good is {(commodity != "" ? "missing from" : "written to")} the file written back (it is written only when set)");
            if (commodity != "") Expect(bad, g, text.Contains($"<overlayCommodity>{commodity}</overlayCommodity>"), $"{what}: the file written back has no <overlayCommodity>{commodity}</overlayCommodity>");
            Expect(bad, g, text.Contains("<showBorders>False</showBorders>") == showBordersWritten,
                $"{what}: the old checkbox is {(showBordersWritten ? "missing from" : "written to")} the file written back (it is written only when false)");
        }

        // ---- Button ----

        /// <summary>
        /// With more than Off and Borders implemented, the button opens a menu of the implemented modes (every one of them, in the menu's order), shown
        /// exactly when a mode other than Off can draw (and never in a prepared save). A mode that cannot draw is listed disabled.
        /// </summary>
        private static void ButtonRule(List<string> bad, LivingWorldComponent c)
        {
            const string g = "button";
            IReadOnlyList<WorldOverlayMode> modes = WorldOverlay.ImplementedModes;
            WorldOverlayMode[] expected = { WorldOverlayMode.Off, WorldOverlayMode.Borders, WorldOverlayMode.WarFronts, WorldOverlayMode.Trade, WorldOverlayMode.Prosperity, WorldOverlayMode.Shortage };
            Expect(bad, g, modes.SequenceEqual(expected), $"the implemented modes are {string.Join(", ", modes)}, not Off, Borders, War fronts, Trade routes, Prosperity, Shortage");
            Expect(bad, g, WorldOverlay.UsesMenu, "the button does not use a menu with more than Off and Borders implemented");
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool borders = stored.borders, economy = stored.simulateEconomy, prepared = c.preparedForRemoval;
            try
            {
                stored.borders = true;
                stored.simulateEconomy = true;
                Expect(bad, g, WorldOverlay.ButtonVisible == Territory.Active && WorldOverlay.ButtonVisible, "the button is hidden with borders and the economy on");
                List<WorldOverlay.MenuEntry> menu = WorldOverlay.MenuEntries();
                Expect(bad, g, menu.Count == modes.Count, $"the menu lists {menu.Count} entries for {modes.Count} implemented modes");
                Expect(bad, g, menu.All(o => !o.Disabled), "a menu entry is disabled although every mode can be used");
                int goods = WorldOverlay.ShortageMenuEntries().Count;
                Expect(bad, g, goods == 1 + Commodities.Count, $"the shortage menu has {goods} entries, expected any good plus {Commodities.Count} commodities");
                stored.simulateEconomy = false;
                Expect(bad, g, WorldOverlay.ButtonVisible, "the button is hidden with the economy off, although Borders can still be drawn");
                menu = WorldOverlay.MenuEntries();
                Expect(bad, g, menu.Count == modes.Count && !menu[0].Disabled && !menu[1].Disabled && !menu[2].Disabled && menu[3].Disabled && menu[4].Disabled && menu[5].Disabled,
                    "with the economy off Off, Borders and War fronts must be enabled and Trade routes, Prosperity and Shortage disabled");
                Expect(bad, g, menu.Count > 4 && menu[3].label.Contains(WorldOverlay.CannotUse(WorldOverlayMode.Trade) ?? "?") && menu[4].label.Contains(WorldOverlay.CannotUse(WorldOverlayMode.Prosperity) ?? "?"), "a disabled entry does not give its reason");
                stored.simulateEconomy = true;
                stored.borders = false;
                Expect(bad, g, !Territory.Active && WorldOverlay.ButtonVisible, "the button is hidden with borders off, although War fronts (markers and lines) can still be drawn");
                Expect(bad, g, WorldOverlay.CannotUse(WorldOverlayMode.Borders) != null, "Borders can be used with borders off");
                Expect(bad, g, WorldOverlay.CannotUse(WorldOverlayMode.WarFronts) == null, "War fronts cannot be used with borders off (its markers and lines need no territory)");
                menu = WorldOverlay.MenuEntries();
                Expect(bad, g, menu.Count == modes.Count && menu[1].Disabled && !menu[2].Disabled, "with borders off the menu must list Borders disabled and War fronts enabled");
                stored.borders = true;
                Expect(bad, g, Territory.Active && WorldOverlay.ButtonVisible, "the button is hidden with borders on");
                Expect(bad, g, WorldOverlay.CannotUse(WorldOverlayMode.Borders) == null && WorldOverlay.CannotUse(WorldOverlayMode.Off) == null, "Off or Borders cannot be used with borders on");
                c.preparedForRemoval = true;
                Expect(bad, g, !Territory.Active && !WorldOverlay.ButtonVisible, "the button shows in a save prepared for removal");
            }
            finally
            {
                c.preparedForRemoval = prepared;
                stored.borders = borders;
                stored.simulateEconomy = economy;
            }
        }

        // ---- Marks layer ----

        /// <summary>The marks layer is on the surface (the patch's second entry), is not visible in a mode without marks (Borders here), holds no geometry, and has its own material one queue step above the shared one.</summary>
        private static void MarksLayer(List<string> bad)
        {
            const string g = "marks layer";
            var marks = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            if (marks == null)
            {
                bad.Add("overlay (marks layer): missing from the surface layer (Patches/Borders.xml)");
                return;
            }
            Expect(bad, g, !marks.Visible, "visible in a mode that has no marks");
            marks.RegenerateNow();
            Expect(bad, g, marks.SubMeshCount == 0, $"holds {marks.SubMeshCount} submeshes with nothing to draw");
            Expect(bad, g, marks.GeometryProblem() == null, $"geometry problem with nothing to draw: {marks.GeometryProblem()}");
            marks.Render(); // hidden: returns at once, draws nothing, must not throw
            Material mat = WorldDrawLayer_LWOverlayMarks.MarksMat, shared = WorldMaterials.VertexColorTransparent;
            Expect(bad, g, mat != null && !ReferenceEquals(mat, shared), "the marks material is the shared one, not a copy");
            Expect(bad, g, mat != null && mat.renderQueue == 3546, $"the marks material's render queue is {mat?.renderQueue}, not 3546");
            Expect(bad, g, shared.renderQueue != 3546, "the shared material's render queue was changed");
            Expect(bad, g, ReferenceEquals(mat, WorldDrawLayer_LWOverlayMarks.MarksMat), "the marks material is created again on every use");
            // A destroyed material is made again (Unity's null check), not handed out as a dead object.
            UnityEngine.Object.DestroyImmediate(mat);
            Material again = WorldDrawLayer_LWOverlayMarks.MarksMat;
            Expect(bad, g, again != null && !ReferenceEquals(again, mat) && again.renderQueue == 3546, "the marks material was not made again after it was destroyed");
            // Submeshes built with the destroyed material would keep it (from the modes that draw markers on): the layer is built again with the new one.
            marks.SetDirty();
            marks.RegenerateNow();
            Expect(bad, g, marks.SubMeshCount == 0, $"holds {marks.SubMeshCount} submeshes with nothing to draw after the material was made again");
            Log.Message($"{Tag}marks layer ok: material queue {again?.renderQueue} (shared {shared.renderQueue})");
        }

        // ---- Heat modes ----

        /// <summary>Runs the body with the player's mode and shortage good set in memory (nothing is written), and puts them back, with the tint layer to be drawn afresh.</summary>
        private static void WithMode(WorldOverlayMode mode, CommodityDef commodity, Action body)
        {
            LivingWorldSettings stored = LivingWorldMod.Stored;
            WorldOverlayMode was = stored.overlayMode;
            string wasCommodity = stored.overlayCommodity;
            try
            {
                stored.overlayMode = mode;
                if (mode == WorldOverlayMode.Shortage) stored.overlayCommodity = commodity?.defName ?? "";
                WorldOverlay.HeatBuckets.Clear();
                body();
            }
            finally
            {
                stored.overlayMode = was;
                stored.overlayCommodity = wasCommodity;
                WorldOverlay.HeatBuckets.Clear();
                Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>()?.Invalidate();
            }
        }

        private static bool HeatUsable(LivingWorldComponent c, out WorldDrawLayer_LWBorders layer)
        {
            layer = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>();
            if (layer == null)
            {
                Log.Message(Tag + "the border draw layer is missing, heat cases skipped (the borders paint case reports it)");
                return false;
            }
            if (!Territory.Ready || WorldOverlay.Blocker(WorldOverlayMode.Prosperity) != OverlayBlock.None)
            {
                Log.Message(Tag + "no territory snapshot, or the economy is off: heat cases skipped");
                return false;
            }
            return true;
        }

        // The spec's palettes and alphas as literals, not read from the paint: a wrong colour or alpha there must fail the case. Fill alpha 0.32 (balanced 0.10) is 82 (26) of 255.
        private static readonly byte[][] ProsperityRgb =
        {
            new byte[] { 140, 25, 25 }, new byte[] { 215, 100, 40 }, new byte[] { 230, 205, 65 }, new byte[] { 140, 205, 65 }, new byte[] { 40, 180, 75 }, new byte[] { 175, 70, 205 },
        };

        private static readonly byte[][] ShortageRgb =
        {
            new byte[] { 50, 115, 230 }, new byte[] { 115, 180, 240 }, new byte[] { 150, 150, 150 }, new byte[] { 240, 150, 50 }, new byte[] { 230, 65, 40 }, new byte[] { 140, 15, 25 },
        };

        private const byte HeatFillAlpha = 82, BalancedFillAlpha = 26;

        /// <summary>
        /// Each heat mode, switched in memory: a full regeneration is sound and has vertices for claimed land, and every claimed tile is drawn
        /// (or not) as the mode says: only simulated towns of a faction, in the palette colour of the band of the owner's value (a fresh start, so
        /// no hysteresis), with the faction's own shade as the band along its border.
        /// </summary>
        private static void HeatModes(List<string> bad, LivingWorldComponent c)
        {
            const string g = "heat";
            if (!HeatUsable(c, out WorldDrawLayer_LWBorders layer)) return;
            foreach ((WorldOverlayMode mode, CommodityDef commodity) in new[] { (WorldOverlayMode.Prosperity, (CommodityDef)null), (WorldOverlayMode.Shortage, null), (WorldOverlayMode.Shortage, LWDefOf.LW_Food) })
            {
                string name = WorldOverlay.ModeLabel(mode, commodity);
                WithMode(mode, commodity, () =>
                {
                    Expect(bad, g, WorldOverlay.TintMode && WorldOverlay.HeatMode && !WorldOverlay.MarksMode, $"{name}: not a tint-only heat mode");
                    Expect(bad, g, WorldOverlay.Paint is HeatPaint, $"{name}: the paint is not a heat paint");
                    Territory.Rebuild(c);
                    TerritorySnapshot snap = Territory.Current;
                    layer.Invalidate();
                    layer.RegenerateNow();
                    if (layer.GeometryProblem() is string geo) bad.Add($"overlay ({g}): {name}: {geo}");
                    int drawn = 0, wrong = 0, wrongColour = 0, wrongEdge = 0;
                    var seen = new HashSet<int>();
                    foreach (int t in snap.Claimed)
                    {
                        TerritorySource src = snap.sources[snap.SourceIndexAt(t)];
                        CompSettlementLife life = src.obj.GetComponent<CompSettlementLife>();
                        bool expectDrawn = src.faction != null && !src.player && life != null && life.Active;
                        bool has = layer.TryGetVisual(t, out int group, out Color32 fill, out Color32 edge, out float width);
                        if (has != expectDrawn)
                        {
                            wrong++;
                            continue;
                        }
                        if (!has) continue;
                        drawn++;
                        seen.Add(src.objectId);
                        int bucket;
                        byte fillAlpha = HeatFillAlpha;
                        if (mode == WorldOverlayMode.Prosperity) bucket = ProsperityPaint.BucketOf(life.prosperity, life.famineDays, -1, Tuning.T.overlayHeatHysteresis);
                        else
                        {
                            bucket = Mathf.Max(ShortagePaint.FirstBucket(commodity == null), ShortagePaint.BucketOf(ShortagePaint.ValueOf(life, commodity), -1, Tuning.T.overlayPriceHysteresis));
                            if (bucket == 2) fillAlpha = BalancedFillAlpha;
                        }
                        byte[] rgb = mode == WorldOverlayMode.Prosperity ? ProsperityRgb[bucket] : ShortageRgb[bucket];
                        if (!Same(fill, new Color32(rgb[0], rgb[1], rgb[2], fillAlpha))) wrongColour++;
                        if (group != src.factionId || !Same(edge, LegacyTint(src.color, 0.85f, 0.35f)) || width != 0.2f) wrongEdge++;
                    }
                    Expect(bad, g, wrong == 0, $"{name}: {wrong} claimed tiles are drawn or left out against the rule (simulated towns of a faction only)");
                    Expect(bad, g, wrongColour == 0, $"{name}: {wrongColour} tiles do not have the palette colour of their owner's band");
                    Expect(bad, g, wrongEdge == 0, $"{name}: {wrongEdge} tiles have another group, edge colour or band width than the faction's shade at alpha 0.35, width 0.2");
                    Expect(bad, g, drawn == 0 || layer.LastVerts > 0, $"{name}: claimed land but no geometry");
                    var drawnTowns = new HashSet<int>();
                    foreach (TerritorySource src in snap.sources)
                    {
                        CompSettlementLife l = src.obj?.GetComponent<CompSettlementLife>();
                        if (src.faction != null && !src.player && l != null && l.Active) drawnTowns.Add(src.objectId);
                    }
                    Expect(bad, g, WorldOverlay.HeatBuckets.Count == drawnTowns.Count && drawnTowns.All(WorldOverlay.HeatBuckets.ContainsKey), $"{name}: the hysteresis table holds {WorldOverlay.HeatBuckets.Count} towns, {drawnTowns.Count} are drawn");
                    Expect(bad, g, seen.IsSubsetOf(drawnTowns), $"{name}: a town with drawn land is not one the paint draws");
                    if (AutoTest.Plan == "stress") Expect(bad, g, layer.LastFullRegenMs < StressFullRegenLimitMs, $"{name}: full regeneration took {layer.LastFullRegenMs:F0} ms (limit {StressFullRegenLimitMs:F0})");
                    Log.Message($"{Tag}heat {name}: {drawn} of {snap.claimedCount} claimed tiles drawn ({seen.Count} towns), {layer.LastVerts} vertices, full regen {layer.LastFullRegenMs:F1} ms, bands: {WorldOverlay.HeatSummary()}");
                });
            }
        }

        /// <summary>
        /// One town's prosperity goes from 0.1 to 0.95 and then into famine, and the day's notification is run each time: it counts the change, the layer
        /// is dirty, and redrawing what it marked equals a full redraw (and, on a world with enough regions, rebuilds only some of them). A day with
        /// no change dirties nothing.
        /// </summary>
        private static void HeatIncremental(List<string> bad, LivingWorldComponent c)
        {
            const string g = "heat incremental";
            if (!HeatUsable(c, out WorldDrawLayer_LWBorders layer)) return;
            CompSettlementLife life = c.activeSettlements.FirstOrDefault(s => s.Active && Territory.OwnerOf(s.Settlement.Tile) == s.Settlement && Territory.TileCount(s.Settlement) > 0);
            if (life == null)
            {
                Log.Message(Tag + "no town that owns land, heat incremental case skipped");
                return;
            }
            float prosperity = life.prosperity;
            int famine = life.famineDays;
            WithMode(WorldOverlayMode.Prosperity, null, () =>
            {
                try
                {
                    life.prosperity = 0.1f;
                    life.famineDays = 0;
                    Territory.Rebuild(c);
                    layer.Invalidate();
                    layer.RegenerateNow();
                    WorldOverlay.Notify_DayDone(c);
                    Expect(bad, g, WorldOverlay.LastBucketChanges == 0 && !layer.Dirty, $"a day with no change counted {WorldOverlay.LastBucketChanges} band changes or dirtied the layer");
                    int sources = WorldOverlay.LastBucketSources;
                    life.prosperity = 0.95f;
                    WorldOverlay.Notify_DayDone(c);
                    Expect(bad, g, WorldOverlay.LastBucketChanges == 1, $"prosperity 0.1 to 0.95 counted {WorldOverlay.LastBucketChanges} band changes, expected 1");
                    Expect(bad, g, layer.Dirty, "the layer is clean after a town changed band");
                    if (AutoTest.Plan == "stress") Expect(bad, g, WorldOverlay.LastNotifyMs < 5d, $"the daily notification took {WorldOverlay.LastNotifyMs:F2} ms (limit 5)");
                    (bool full, int regions) = WorldChecks.IncrementalMatchesFull(bad, layer, "heat prosperity 0.1 to 0.95");
                    if (layer.RegionCount >= 8) Expect(bad, g, !full && regions < layer.RegionCount, $"one town changing band rebuilt {regions} of {layer.RegionCount} regions{(full ? " (a full redraw)" : "")}");
                    life.famineDays = 3;
                    WorldOverlay.Notify_DayDone(c);
                    Expect(bad, g, WorldOverlay.LastBucketChanges == 1 && layer.Dirty, "a town going hungry did not change band at once (famine is exact)");
                    WorldChecks.IncrementalMatchesFull(bad, layer, "heat famine");
                    WorldOverlay.Notify_DayDone(c);
                    Expect(bad, g, WorldOverlay.LastBucketChanges == 0 && !layer.Dirty, "a second notification with nothing changed dirtied the layer");
                    Log.Message($"{Tag}heat incremental: {life.Settlement.LabelCap}, {regions} of {layer.RegionCount} regions rebuilt ({(full ? "full" : "incremental")}), {sources} towns drawn, notify {WorldOverlay.LastNotifyMs:F2} ms");
                }
                finally
                {
                    life.prosperity = prosperity;
                    life.famineDays = famine;
                }
            });
        }

        /// <summary>
        /// The hysteresis: pure bands (prosperity at 0.2/0.4/0.6/0.8 with margin 0.02, famine exact and leaving it starts afresh, price factor at
        /// 0.7/0.9/1.15/1.5/2.0 with margin 0.05, any good never blue), and through the real paint on one town, whose colour holds across an edge until the
        /// value is past it by the margin.
        /// </summary>
        private static void Hysteresis(List<string> bad, LivingWorldComponent c)
        {
            const string g = "hysteresis";
            const float h = 0.02f, ph = 0.05f;
            Expect(bad, g, Tuning.T.overlayHeatHysteresis == h && Tuning.T.overlayPriceHysteresis == ph, "the tuning margins are not 0.02 and 0.05");
            // Fresh bands: an edge belongs to the band above it (prosperity below 0.2 is destitute, the price factor up to and including 0.7 is surplus).
            Expect(bad, g, ProsperityPaint.BucketOf(0.199f, 0, -1, h) == 0 && ProsperityPaint.BucketOf(0.2f, 0, -1, h) == 1 && ProsperityPaint.BucketOf(0.8f, 0, -1, h) == 4 && ProsperityPaint.BucketOf(0f, 0, -1, h) == 0 && ProsperityPaint.BucketOf(1f, 0, -1, h) == 4, "fresh prosperity bands are wrong");
            Expect(bad, g, ShortagePaint.BucketOf(0.7f, -1, ph) == 0 && ShortagePaint.BucketOf(0.71f, -1, ph) == 1 && ShortagePaint.BucketOf(0.9f, -1, ph) == 1 && ShortagePaint.BucketOf(0.91f, -1, ph) == 2
                                && ShortagePaint.BucketOf(1.149f, -1, ph) == 2 && ShortagePaint.BucketOf(1.15f, -1, ph) == 3 && ShortagePaint.BucketOf(1.5f, -1, ph) == 4 && ShortagePaint.BucketOf(2.0f, -1, ph) == 5, "fresh price bands are wrong");
            // Prosperity: 0.41 then 0.39 stays modest, 0.37 drops to poor; going up it takes 0.42.
            Expect(bad, g, ProsperityPaint.BucketOf(0.41f, 0, -1, h) == 2, "0.41 is not modest");
            Expect(bad, g, ProsperityPaint.BucketOf(0.39f, 0, 2, h) == 2, "0.39 did not keep modest");
            Expect(bad, g, ProsperityPaint.BucketOf(0.37f, 0, 2, h) == 1, "0.37 did not drop to poor");
            Expect(bad, g, ProsperityPaint.BucketOf(0.41f, 0, 1, h) == 1 && ProsperityPaint.BucketOf(0.43f, 0, 1, h) == 2, "poor does not hold until 0.42");
            Expect(bad, g, ProsperityPaint.BucketOf(0.95f, 0, 0, h) == 4 && ProsperityPaint.BucketOf(0.05f, 0, 4, h) == 0, "a big jump is not followed in one step");
            // Famine is exact, and leaving it starts afresh.
            Expect(bad, g, ProsperityPaint.BucketOf(0.9f, 1, 4, h) == ProsperityPaint.Famine, "famine did not show at once");
            Expect(bad, g, ProsperityPaint.BucketOf(0.9f, 0, ProsperityPaint.Famine, h) == 4 && ProsperityPaint.BucketOf(0.41f, 0, ProsperityPaint.Famine, h) == 2, "leaving famine did not give the plain band of the value");
            // Price factor: 0.92 balanced, 0.88 stays, 0.84 drops to plenty.
            Expect(bad, g, ShortagePaint.BucketOf(0.92f, -1, ph) == 2 && ShortagePaint.BucketOf(0.88f, 2, ph) == 2 && ShortagePaint.BucketOf(0.84f, 2, ph) == 1, "the price hysteresis at 0.9 is wrong");
            Expect(bad, g, ShortagePaint.BucketOf(1.17f, 2, ph) == 2 && ShortagePaint.BucketOf(1.21f, 2, ph) == 3 && ShortagePaint.BucketOf(1.12f, 3, ph) == 3 && ShortagePaint.BucketOf(1.09f, 3, ph) == 2, "the price hysteresis at 1.15 is wrong");
            // Any good never shows the blue bands; one good can.
            Expect(bad, g, ShortagePaint.FirstBucket(any: true) == ShortagePaint.Balanced && ShortagePaint.FirstBucket(any: false) == 0, "any good does not start at balanced");
            // And is the mean of the price factors of the consumed goods, weighted by each one's share of the town's consumption, floored at 1 (worked out here by shares).
            foreach (CompSettlementLife life in c.activeSettlements.Where(s => s.Active).Take(200))
            {
                float any = ShortagePaint.ValueOf(life, null);
                if (any < 1f) bad.Add($"overlay ({g}): the any-good value of {life.Settlement.LabelCap} is {any:F2}, below 1");
                float total = Commodities.All.Sum(life.DailyConsumption), expected = 0f, max = 1f;
                if (total > 0f)
                    foreach (CommodityDef cd in Commodities.All)
                    {
                        float f = life.PriceFactor(cd);
                        expected += life.DailyConsumption(cd) / total * f;
                        if (life.DailyConsumption(cd) > 0f) max = Mathf.Max(max, f);
                    }
                expected = Mathf.Max(1f, expected);
                if (Mathf.Abs(any - expected) > 0.001f || any > max + 0.001f) bad.Add($"overlay ({g}): the any-good value of {life.Settlement.LabelCap} is {any:F3}, the consumption-weighted mean {expected:F3} (highest good {max:F3})");
            }

            // Through the paint on a real town.
            if (!Territory.Ready || WorldOverlay.Blocker(WorldOverlayMode.Prosperity) != OverlayBlock.None) return;
            CompSettlementLife town = c.activeSettlements.FirstOrDefault(s => s.Active && Territory.Current.indexByObject.ContainsKey(s.Settlement.ID));
            if (town == null) return;
            float prosperity = town.prosperity;
            int famine = town.famineDays;
            try
            {
                WorldOverlay.HeatBuckets.Clear();
                TerritorySnapshot snap = Territory.Current;
                int index = snap.indexByObject[town.Settlement.ID];
                TerritorySource src = snap.sources[index];
                town.famineDays = 0;
                Color32 FillAt(float p)
                {
                    town.prosperity = p;
                    ProsperityPaint.Instance.Paint(index, in src, out _, out Color32 fill, out _, out _);
                    return fill;
                }
                Color32 modest = FillAt(0.41f);
                Expect(bad, g, Same(modest, ProsperityPaint.Instance.FillOf(2)), "the paint did not start at modest for 0.41");
                Expect(bad, g, Same(FillAt(0.39f), modest), "the paint changed colour at 0.39");
                Expect(bad, g, Same(FillAt(0.37f), ProsperityPaint.Instance.FillOf(1)), "the paint did not change colour at 0.37");
                Expect(bad, g, WorldOverlay.HeatBuckets.TryGetValue(src.objectId, out int kept) && kept == 1, "the remembered band is not poor");
            }
            finally
            {
                town.prosperity = prosperity;
                town.famineDays = famine;
                WorldOverlay.HeatBuckets.Clear();
            }
        }

        /// <summary>What each mode needs: the economy (and territory) for the heatmaps, nothing but the prepared-save rule for the rest; a mode that cannot draw falls back to the borders picture and no legend.</summary>
        private static void Needs(List<string> bad, LivingWorldComponent c)
        {
            const string g = "needs";
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool economy = stored.simulateEconomy, borders = stored.borders;
            WithMode(WorldOverlayMode.Prosperity, null, () =>
            {
                try
                {
                    stored.borders = true;
                    stored.simulateEconomy = false;
                    foreach (WorldOverlayMode m in new[] { WorldOverlayMode.Prosperity, WorldOverlayMode.Shortage, WorldOverlayMode.Trade })
                    {
                        Expect(bad, g, WorldOverlay.CannotUse(m) != null, $"{m} can be used with the economy simulation off");
                        Expect(bad, g, WorldOverlay.Blocker(m) == OverlayBlock.NeedsEconomy, $"{m} is not blocked by the economy rule");
                    }
                    Expect(bad, g, WorldOverlay.CannotUse(WorldOverlayMode.Borders) == null && WorldOverlay.CannotUse(WorldOverlayMode.Off) == null && WorldOverlay.CannotUse(WorldOverlayMode.WarFronts) == null, "Off, Borders or War fronts need the economy");
                    stored.overlayMode = WorldOverlayMode.Trade;
                    Expect(bad, g, !WorldOverlay.MarksMode, "the trade routes mode draws with the economy simulation off");
                    stored.overlayMode = WorldOverlayMode.Prosperity;
                    Expect(bad, g, !WorldOverlay.TintMode && !WorldOverlay.HeatMode, "Prosperity is a tint mode with the economy off");
                    Expect(bad, g, WorldOverlay.Paint is BordersPaint, "a heatmap that cannot draw does not fall back to the borders paint");
                    Expect(bad, g, !OverlayLegend.Shown(WorldOverlayMode.Prosperity), "the legend shows for a heatmap that cannot draw");
                    stored.simulateEconomy = true;
                    stored.borders = false;
                    Expect(bad, g, WorldOverlay.Blocker(WorldOverlayMode.Prosperity) == OverlayBlock.NeedsTerritory && WorldOverlay.Blocker(WorldOverlayMode.Shortage) == OverlayBlock.NeedsTerritory, "the heatmaps are not blocked by territory being off");
                    Expect(bad, g, WorldOverlay.Blocker(WorldOverlayMode.Trade) == OverlayBlock.None, "Trade needs territory");
                    stored.borders = true;
                    Expect(bad, g, WorldOverlay.CannotUse(WorldOverlayMode.Prosperity) == null && WorldOverlay.CannotUse(WorldOverlayMode.Shortage) == null && WorldOverlay.TintMode && WorldOverlay.Paint is ProsperityPaint,
                        "Prosperity cannot be used with borders and the economy on");
                }
                finally
                {
                    stored.simulateEconomy = economy;
                    stored.borders = borders;
                }
            });
        }

        // ---- Legend ----

        /// <summary>The legend: shown for the heatmaps only, one swatch per band (any good leaves the blue ones out), a mode this build cannot draw reads as off, and the texts are made once.</summary>
        private static void Legend(List<string> bad)
        {
            const string g = "legend";
            CommodityDef food = LWDefOf.LW_Food;
            Expect(bad, g, OverlayLegend.SwatchCount(WorldOverlayMode.Prosperity, null) == 6, "the prosperity legend has not 6 swatches");
            Expect(bad, g, OverlayLegend.SwatchCount(WorldOverlayMode.Shortage, null) == 4, "the any-good shortage legend has not 4 swatches (no blue ones)");
            Expect(bad, g, OverlayLegend.SwatchCount(WorldOverlayMode.Shortage, food) == 6, "the shortage legend for one good has not 6 swatches");
            Expect(bad, g, OverlayLegend.SwatchCount(WorldOverlayMode.Off, null) == 0 && OverlayLegend.SwatchCount(WorldOverlayMode.Borders, null) == 0, "Off or Borders list swatches");
            Expect(bad, g, !OverlayLegend.Shown(WorldOverlayMode.Off) && !OverlayLegend.Shown(WorldOverlayMode.Borders), "the legend shows for Off or Borders");
            Expect(bad, g, OverlayLegend.Shown(WorldOverlayMode.WarFronts), "the legend does not show for War fronts");
            Expect(bad, g, OverlayLegend.SwatchCount(WorldOverlayMode.WarFronts, null) == 6, "the war fronts legend has not 6 swatches (wars, four battle kinds, war band)");
            Expect(bad, g, OverlayLegend.SwatchCount(WorldOverlayMode.Trade, null) == 2, "the trade routes legend has not 2 swatches (a route ribbon, the merchants' line)");
            Expect(bad, g, OverlayLegend.Shown(WorldOverlayMode.Trade) == (WorldOverlay.Blocker(WorldOverlayMode.Trade) == OverlayBlock.None), "the legend does not show for Trade routes exactly when it can draw");
            // A mode this build does not know (a setting from a newer build) reads as off and gets no legend.
            const WorldOverlayMode unknown = (WorldOverlayMode)99;
            Expect(bad, g, !OverlayLegend.Shown(unknown), "the legend shows for a mode this build cannot draw");
            Expect(bad, g, WorldOverlay.ModeLabel(unknown) == WorldOverlay.ModeLabel(WorldOverlayMode.Off), "a mode this build cannot draw is not labelled as off");
            Expect(bad, g, WorldOverlay.ModeLabel(WorldOverlayMode.Trade) != WorldOverlay.ModeLabel(WorldOverlayMode.Off), "Trade routes is labelled as off");
            Expect(bad, g, WorldOverlay.ModeLabel(WorldOverlayMode.WarFronts) != WorldOverlay.ModeLabel(WorldOverlayMode.Off), "War fronts is labelled as off");
            Expect(bad, g, WorldOverlay.ModeTooltip(unknown) == WorldOverlay.ModeTooltip(WorldOverlayMode.Off), "the tooltip of a mode this build cannot draw is not the one for off");
            Expect(bad, g, WorldOverlay.ModeTooltip(WorldOverlayMode.Trade).IndexOf(WorldOverlay.ModeLabel(WorldOverlayMode.Trade), System.StringComparison.OrdinalIgnoreCase) >= 0, "the Trade routes tooltip does not name the mode");
            Expect(bad, g, WorldOverlay.ModeTooltip(WorldOverlayMode.WarFronts).IndexOf(WorldOverlay.ModeLabel(WorldOverlayMode.WarFronts), System.StringComparison.OrdinalIgnoreCase) >= 0, "the War fronts tooltip does not name the mode");
            string label = WorldOverlay.ModeLabel(WorldOverlayMode.Shortage, food);
            Expect(bad, g, label.Contains(food.label) && label != WorldOverlay.ModeLabel(WorldOverlayMode.Shortage, null), "the shortage label does not name the good");
            Expect(bad, g, ReferenceEquals(label, WorldOverlay.ModeLabel(WorldOverlayMode.Shortage, food)) && ReferenceEquals(WorldOverlay.ModeTooltip(WorldOverlayMode.Shortage, food), WorldOverlay.ModeTooltip(WorldOverlayMode.Shortage, food)),
                "the mode label or tooltip is made again on every ask");
            LegendPlacement(bad);
            LegendSwatches(bad);
            foreach (string key in new[] { "LW_Overlay_Mode_WarFronts", "LW_Overlay_Legend_WarTitle", "LW_Overlay_Legend_Wars", "LW_Overlay_Legend_Repelled", "LW_Overlay_Legend_Plundered", "LW_Overlay_Legend_Captured", "LW_Overlay_Legend_Razed", "LW_Overlay_Legend_WarBand", "LW_Overlay_Mode_Trade", "LW_Overlay_Legend_TradeRoute", "LW_Overlay_Legend_TradeNone", "LW_Overlay_Legend_TradeQuiet", "LW_Overlay_Legend_Merchants", "LW_Overlay_Mode_Prosperity", "LW_Overlay_Mode_Shortage", "LW_Overlay_Mode_ShortageOf", "LW_Overlay_Mode_ShortageAnyTip", "LW_Overlay_Famine", "LW_Overlay_Surplus", "LW_Overlay_Plenty", "LW_Overlay_Balanced", "LW_Overlay_Short", "LW_Overlay_Scarce", "LW_Overlay_Desperate", "LW_Overlay_AnyGood", "LW_Overlay_Menu_Shortage" })
                Expect(bad, g, key.CanTranslate(), $"the string {key} is missing");
        }

        /// <summary>The legend's box is under the dev-mode toolbar (when it shows) and under the lowest colonist bar row with its names, and stays at the top with neither.</summary>
        private static void LegendPlacement(List<string> bad)
        {
            const string g = "legend";
            float plain = OverlayLegend.TopFor(false, null, Vector2.zero, 1f);
            Expect(bad, g, plain > 0f && plain < 30f, $"the legend is at {plain} with nothing else at the top");
            float dev = OverlayLegend.TopFor(true, null, Vector2.zero, 1f);
            Expect(bad, g, dev >= 28f, $"the legend at {dev} overlaps the dev-mode toolbar (y 3 to 28)");
            // A bar of two rows (portraits 48 high at scale 1, rows 70 apart): the box clears the second row, its portraits and its name labels.
            var locs = new List<Vector2> { new Vector2(500f, 21f), new Vector2(580f, 21f), new Vector2(500f, 91f) };
            float bar = OverlayLegend.TopFor(true, locs, new Vector2(48f, 48f), 1f);
            Expect(bad, g, bar >= 91f + 48f + 18f, $"the legend at {bar} overlaps the colonist bar (lowest portraits end at {91f + 48f}, then the names)");
            Expect(bad, g, OverlayLegend.TopFor(true, new List<Vector2>(), Vector2.zero, 1f) == dev, "an empty colonist bar moved the legend");
            Rect live = OverlayLegend.BoxRect();
            Expect(bad, g, live.yMin >= plain && live.width > 0f, $"the legend's box is {live}");
            Log.Message($"{Tag}legend box {live} (dev mode {Prefs.DevMode}), bar rows reach y {(Find.ColonistBar?.DrawLocs.Count > 0 ? Find.ColonistBar.DrawLocs.Max(v => v.y) + Find.ColonistBar.Size.y : 0f):F0}");
        }

        /// <summary>The swatches are the colours the map shows (the wash over land): a lighter famine than destitute, and a faint balanced that is not the opaque grey.</summary>
        private static void LegendSwatches(List<string> bad)
        {
            const string g = "legend";
            Color balanced = OverlayLegend.SwatchColour(ShortagePaint.For(null).FillOf(ShortagePaint.Balanced));
            Expect(bad, g, balanced.r < 0.55f && balanced.b < 0.5f, $"the balanced swatch {balanced} is the opaque grey, not the faint wash it is on the map");
            Color opaque = OverlayLegend.SwatchColour(new Color32(10, 20, 30, 255));
            Expect(bad, g, Mathf.Abs(opaque.r - 10f / 255f) < 0.01f && Mathf.Abs(opaque.g - 20f / 255f) < 0.01f && Mathf.Abs(opaque.b - 30f / 255f) < 0.01f, "an opaque fill does not give its own colour");
            Color destitute = OverlayLegend.SwatchColour(ProsperityPaint.Instance.FillOf(0)), famine = OverlayLegend.SwatchColour(ProsperityPaint.Instance.FillOf(ProsperityPaint.Famine));
            Expect(bad, g, Mathf.Abs(destitute.b - famine.b) > 0.1f, "the famine and destitute swatches are hard to tell apart");
        }

        // ---- War fronts ----

        /// <summary>The war pairs are the active war records, and nothing else (a pirate's standing hostility is not a front); a forced pair counts only while forced.</summary>
        private static void WarSet(List<string> bad, LivingWorldComponent c)
        {
            const string g = "war set";
            WorldOverlay.InvalidateAll(); // the set is worked out again from the wars when next asked
            int active = 0, permanentEnemyWars = 0;
            var pairs = new HashSet<long>();
            foreach (War w in c.wars)
            {
                if (!w.Active) continue;
                active++;
                if (c.FactionById(w.factionA)?.def.permanentEnemy == true || c.FactionById(w.factionB)?.def.permanentEnemy == true) permanentEnemyWars++;
                pairs.Add(Diplomacy.Key(w.factionA, w.factionB));
                Expect(bad, g, WorldOverlay.AtWar(w.factionA, w.factionB) && WorldOverlay.AtWar(w.factionB, w.factionA), $"the active war {w.nameA} / {w.nameB} is not in the war pair set (or not symmetric)");
                Expect(bad, g, WorldOverlay.InWar(w.factionA) && WorldOverlay.InWar(w.factionB), $"a side of the active war {w.nameA} / {w.nameB} is not in the war faction set");
            }
            Expect(bad, g, permanentEnemyWars == 0, $"{permanentEnemyWars} active wars have a permanent enemy (pirates) on a side: a standing hostility would be drawn as a front");
            Expect(bad, g, WorldOverlay.WarCount == pairs.Count, $"the war pair set has {WorldOverlay.WarCount} pairs, the active wars make {pairs.Count}");
            // Every pair of factions: at war exactly when a war record says so (pirates against everyone are not fronts).
            List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
            int checkedPairs = 0, pirateHostile = 0;
            for (int i = 0; i < factions.Count; i++)
                for (int j = i + 1; j < factions.Count; j++)
                {
                    Faction a = factions[i], b = factions[j];
                    bool expected = pairs.Contains(Diplomacy.Key(a.loadID, b.loadID));
                    checkedPairs++;
                    if (WorldOverlay.AtWar(a.loadID, b.loadID) != expected)
                        bad.Add($"overlay ({g}): {a.Name} and {b.Name} are {(expected ? "not " : "")}in the war pair set although {(expected ? "an active war says so" : "no active war")}");
                    if (!expected && (a.def.permanentEnemy || b.def.permanentEnemy) && a.HostileTo(b)) pirateHostile++;
                }
            Expect(bad, g, !WorldOverlay.AtWar(factions[0].loadID, factions[0].loadID), "a faction is at war with itself");
            // The test override adds a pair without touching diplomacy, and removes it again.
            Faction x = factions.FirstOrDefault(f => !f.IsPlayer && !f.defeated);
            Faction y = x == null ? null : factions.FirstOrDefault(f => f != x && !f.IsPlayer && !f.defeated && !WorldOverlay.AtWar(x.loadID, f.loadID));
            if (x != null && y != null)
            {
                int count = WorldOverlay.WarCount, changes = WorldOverlay.LastWarSetChanges;
                try
                {
                    WorldOverlay.ForceWarPairForTest(x.loadID, y.loadID, true);
                    Expect(bad, g, WorldOverlay.AtWar(x.loadID, y.loadID) && WorldOverlay.WarCount == count + 1 && WorldOverlay.InWar(x.loadID), "a forced pair is not in the war set");
                    Expect(bad, g, Diplomacy.ActiveWar(c, x, y) == null, "forcing a pair created a war record");
                    WorldOverlay.ForceWarPairForTest(x.loadID, y.loadID, true);
                    Expect(bad, g, WorldOverlay.LastWarSetChanges == changes + 1, "forcing the same pair twice counted two changes");
                }
                finally
                {
                    WorldOverlay.ForceWarPairForTest(x.loadID, y.loadID, false);
                }
                Expect(bad, g, !WorldOverlay.AtWar(x.loadID, y.loadID) && WorldOverlay.WarCount == count, "a pair is still at war after the override was removed");
            }
            Log.Message($"{Tag}war set: {active} active wars ({pairs.Count} pairs), {checkedPairs} faction pairs checked, {pirateHostile} hostile pairs with a permanent enemy that are not fronts");
        }

        /// <summary>The picture of the war fronts mode and its war edges (see the cases below).</summary>
        private static void WarFronts(List<string> bad, LivingWorldComponent c)
        {
            if (Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>() == null)
            {
                Log.Message(Tag + "the border draw layer is missing, war fronts case skipped (the borders paint case reports it)");
                return;
            }
            try
            {
                WithMode(WorldOverlayMode.WarFronts, null, () => WarFrontsBody(bad, c));
            }
            finally
            {
                Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>()?.DropSpans();
            }
        }

        /// <summary>
        /// War fronts: every claimed tile is drawn in the faction colour at alpha 0.14 (the faction is a side in a war) or 0.05, with its own edge (shade 0.85, 0.3)
        /// and width 0.2; the war edges the layer counts are twice the shared border of the war pairs (a band on each side of every tile edge, within 1 %); forcing a
        /// pair with a shared border into the war set invalidates the layer, adds red bands (255,40,35, 0.45 wide) on both sides of it (twice its shared border, within 1 %),
        /// and removing it brings the count back; a day's notification with the war set unchanged leaves the layer alone.
        /// </summary>
        private static void WarFrontsBody(List<string> bad, LivingWorldComponent c)
        {
            const string g = "war fronts";
            var layer = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>();
            if (!Territory.Ready)
            {
                Log.Message(Tag + "no territory snapshot, war fronts case skipped");
                return;
            }
            Expect(bad, g, WorldOverlay.TintMode && WorldOverlay.MarksMode && !WorldOverlay.HeatMode, "War fronts is not a tint and marks mode");
            Expect(bad, g, WorldOverlay.Paint is WarPaint, "the paint is not the war paint");
            WorldOverlay.InvalidateAll();
            Territory.Rebuild(c);
            TerritorySnapshot snap = Territory.Current;
            layer.Invalidate();
            layer.RegenerateNow();
            if (layer.GeometryProblem() is string geo) bad.Add($"overlay ({g}): {geo}");
            // The war pairs and sides, worked out here from the war records.
            var pairs = new HashSet<long>();
            var sides = new HashSet<int>();
            foreach (War w in c.wars)
            {
                if (!w.Active) continue;
                pairs.Add(Diplomacy.Key(w.factionA, w.factionB));
                sides.Add(w.factionA);
                sides.Add(w.factionB);
            }
            int drawn = 0, wrong = 0, atWar = 0;
            foreach (int t in snap.Claimed)
            {
                TerritorySource src = snap.sources[snap.SourceIndexAt(t)];
                bool has = layer.TryGetVisual(t, out int group, out Color32 fill, out Color32 edge, out float width);
                if (src.faction == null)
                {
                    if (has) wrong++;
                    continue;
                }
                bool inWar = sides.Contains(src.factionId);
                if (inWar) atWar++;
                if (!has || group != src.factionId || !Same(fill, LegacyTint(src.color, 1f, inWar ? 0.14f : 0.05f)) || !Same(edge, LegacyTint(src.color, 0.85f, 0.3f)) || width != 0.2f) wrong++;
                else drawn++;
            }
            Expect(bad, g, wrong == 0, $"{wrong} claimed tiles are not drawn as the war picture (group, fill alpha 0.14 at war and 0.05 otherwise, edge shade 0.85 at 0.3, band width 0.2)");
            Expect(bad, g, drawn == 0 || layer.LastVerts > 0, "claimed land but no geometry");

            // The war edges against an independent count of the shared border of the war pairs.
            int realBorder = 0;
            foreach (long key in pairs) realBorder += snap.sharedEdges.TryGetValue(key, out int n) ? n : 0;
            int baseEdges = layer.LastWarEdges;
            Expect(bad, g, WarEdgesMatch(baseEdges, realBorder), $"{baseEdges} war edges where the war pairs share {realBorder} tile edges (each has a band on each side: {2 * realBorder} within 1 %)");
            Log.Message($"{Tag}war fronts: {drawn} claimed tiles drawn ({atWar} of factions at war), {pairs.Count} real war pairs sharing {realBorder} tile edges, {baseEdges} war edges, {layer.LastVerts} vertices, full regen {layer.LastFullRegenMs:F1} ms");

            // A forced pair with a shared border (not already at war).
            Faction fa = null, fb = null;
            int shared = 0;
            for (int i = 0; i < snap.factions.Count && fa == null; i++)
                for (int j = i + 1; j < snap.factions.Count; j++)
                {
                    Faction a = snap.factions[i], b = snap.factions[j];
                    if (WorldOverlay.AtWar(a.loadID, b.loadID) || (shared = Territory.SharedBorder(a, b)) <= 0) continue;
                    fa = a;
                    fb = b;
                    break;
                }
            if (fa == null)
            {
                Log.Message(Tag + "no two factions that share a border and are not at war, forced front case skipped");
                return;
            }
            layer.RecordSpans = true;
            try
            {
                WorldOverlay.ForceWarPairForTest(fa.loadID, fb.loadID, true);
                Expect(bad, g, layer.Dirty, "putting a pair at war did not invalidate the tint layer");
                layer.RegenerateNow();
                Expect(bad, g, layer.LastRegenFull, "the layer did not redraw in full after the war set changed");
                int delta = layer.LastWarEdges - baseEdges;
                Expect(bad, g, WarEdgesMatch(delta, shared), $"the forced pair {fa.Name} / {fb.Name} (shared border {shared}) added {delta} war edges, expected {2 * shared} (within 1 %)");
                if (layer.GeometryProblem() is string geo2) bad.Add($"overlay ({g}): {geo2}");
                ReadBackFront(bad, g, layer, snap, fa, fb);
                // A day's notification with nothing changed leaves the layer alone; the faction sides are at war, so their fills are now the 0.14 ones.
                double notifyMs = NotifyDayQuietly(c);
                Expect(bad, g, !layer.Dirty, "a notification with an unchanged war set dirtied the tint layer");
                if (AutoTest.Plan == "stress") Expect(bad, g, notifyMs < 5d, $"the daily notification took {notifyMs:F2} ms (limit 5)");
                Log.Message($"{Tag}war fronts forced {fa.Name} / {fb.Name}: shared border {shared}, +{delta} war edges ({layer.LastWarEdges} in all), full regen {layer.LastFullRegenMs:F1} ms");
            }
            finally
            {
                layer.DropSpans();
                WorldOverlay.ForceWarPairForTest(fa.loadID, fb.loadID, false);
            }
            Expect(bad, g, layer.Dirty, "taking the pair out of the war set did not invalidate the tint layer");
            layer.RegenerateNow();
            Expect(bad, g, layer.LastWarEdges == baseEdges, $"after the override was removed there are {layer.LastWarEdges} war edges, before it {baseEdges}");
            if (AutoTest.Plan == "stress") Expect(bad, g, layer.LastFullRegenMs < StressFullRegenLimitMs, $"full regeneration took {layer.LastFullRegenMs:F0} ms (limit {StressFullRegenLimitMs:F0})");
        }

        /// <summary>
        /// The day's notification outside the sim's schedule, for a case that checks what it does to the layers. Its shadow count pass would add a sample to the
        /// metrics line (<c>bucketChanges=</c>) that is no day of the sim's, so the counters of the last real day are put back. Returns the notification's cost (ms).
        /// </summary>
        private static double NotifyDayQuietly(LivingWorldComponent c)
        {
            int changes = WorldOverlay.LastBucketChanges, sources = WorldOverlay.LastBucketSources, seeded = WorldOverlay.LastBucketSeeded;
            int marked = WorldOverlay.LastRegionsMarked, regions = WorldOverlay.LastRegionCount;
            bool full = WorldOverlay.LastRegionsFull;
            double ms = WorldOverlay.LastNotifyMs;
            WorldOverlay.Notify_DayDone(c);
            double cost = WorldOverlay.LastNotifyMs;
            WorldOverlay.LastBucketChanges = changes;
            WorldOverlay.LastBucketSources = sources;
            WorldOverlay.LastBucketSeeded = seeded;
            WorldOverlay.LastRegionsMarked = marked;
            WorldOverlay.LastRegionCount = regions;
            WorldOverlay.LastRegionsFull = full;
            WorldOverlay.LastNotifyMs = ms;
            return cost;
        }

        /// <summary>Adjacent tiles share both corners, so every shared tile edge of a war pair is a band on each side: twice <paramref name="sharedEdges"/>, within 1 % for edges the paint cannot see.</summary>
        private static bool WarEdgesMatch(int warEdges, int sharedEdges) => Mathf.Abs(warEdges - 2 * sharedEdges) <= 0.01f * 2 * sharedEdges;

        /// <summary>A tile of one side whose only band is against the other side: the band's vertices are the front colour and its width is 0.45 of the way to the centre.</summary>
        private static void ReadBackFront(List<string> bad, string g, WorldDrawLayer_LWBorders layer, TerritorySnapshot snap, Faction fa, Faction fb)
        {
            PlanetLayer surface = Find.WorldGrid.Surface;
            var allVerts = surface.UnsafeVerts;
            var vertOffsets = surface.UnsafeTileIDToVerts_offsets;
            int read = 0, wrongColour = 0, wrongWidth = 0;
            foreach (int t in snap.Claimed)
            {
                int own = snap.FactionIdAt(t);
                if (own != fa.loadID && own != fb.loadID) continue;
                int other = own == fa.loadID ? fb.loadID : fa.loadID;
                surface.GetTileNeighbors(t, neighbourBuf);
                int bands = 0;
                bool front = false;
                foreach (PlanetTile nb in neighbourBuf)
                {
                    int f = snap.FactionIdAt(nb.tileId);
                    if (f == own) continue;
                    bands++;
                    if (f == other) front = true;
                }
                int n = (t + 1 < vertOffsets.Length ? vertOffsets[t + 1] : allVerts.Length) - vertOffsets[t];
                if (bands != 1 || !front || n < 3) continue;
                if (!layer.TryGetSpan(t, out LayerSubMesh mesh, out int first, out int count) || count != n + 4) continue;
                read++;
                for (int j = n; j < count; j++)
                    if (!Same(mesh.colors[first + j], WarPaint.FrontColour)) wrongColour++;
                Vector3 centre = Vector3.zero;
                for (int j = 0; j < n; j++) centre += Unlift(mesh.verts[first + j], WorldDrawLayer_LWBorders.FillLift);
                centre /= n;
                Vector3 b = Unlift(mesh.verts[first + n + 1], WorldDrawLayer_LWBorders.EdgeLift), inner = Unlift(mesh.verts[first + n + 2], WorldDrawLayer_LWBorders.EdgeLift);
                if (Mathf.Abs((inner - b).magnitude / (centre - b).magnitude - WarPaint.FrontWidth) > 0.01f) wrongWidth++;
                if (read >= 200) break;
            }
            Expect(bad, g, wrongColour == 0, $"{wrongColour} vertices of front bands do not carry the front red (255,40,35, opaque)");
            Expect(bad, g, wrongWidth == 0, $"{wrongWidth} front bands are not 0.45 of the way to the tile's centre");
            if (read == 0) Log.Message($"{Tag}war fronts: no tile of {fa.Name} or {fb.Name} has just one band against the other side, front colours not read back");
        }

        // ---- Battle markers ----

        private static readonly BattleKind[] AllKinds = { BattleKind.Repelled, BattleKind.Plundered, BattleKind.Captured, BattleKind.Razed };

        /// <summary>The distinct tiles of the chronicle's battles in the window and the newest battle's kind on each, counted here without the layer's scan (every entry, not from the newest back).</summary>
        private static Dictionary<int, BattleKind> ExpectedBattles(LivingWorldComponent c, PlanetLayer surface)
        {
            var result = new Dictionary<int, (int ticks, int id, BattleKind kind)>();
            int cutoff = SimClock.TicksAbs - Tuning.T.overlayBattleDays * GenDate.TicksPerDay;
            foreach (ChronicleEntry e in c.chronicle.entries)
            {
                if (e.ticksAbs < cutoff || !e.tile.Valid || e.tile.Layer != surface) continue;
                BattleKind kind;
                string d = e.defName;
                if (d == "LW_AttackRepelled") kind = BattleKind.Repelled;
                else if (d == "LW_SettlementPlundered") kind = BattleKind.Plundered;
                else if (d == "LW_SettlementCaptured") kind = BattleKind.Captured;
                else if (d == "LW_SettlementRazed") kind = BattleKind.Razed;
                else continue;
                if (!result.TryGetValue(e.tile.tileId, out var old) || e.ticksAbs > old.ticks || (e.ticksAbs == old.ticks && e.id > old.id)) result[e.tile.tileId] = (e.ticksAbs, e.id, kind);
            }
            return result.ToDictionary(kv => kv.Key, kv => kv.Value.kind);
        }

        /// <summary>
        /// The battle markers: one diamond (4 vertices) on each distinct tile of the chronicle's battles of the last 30 days, the newest battle's kind on a tile
        /// (counted independently from every entry), none for entries outside the window; the marker's size, colour and fade; the content key changes with a new
        /// battle and with the day; regenerating again keeps the submeshes; the day's notification marks the layer dirty when a battle came in.
        /// </summary>
        private static void BattleMarkers(List<string> bad, LivingWorldComponent c)
        {
            const string g = "battle markers";
            PlanetLayer surface = Find.WorldGrid.Surface;
            var marks = surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            if (marks == null)
            {
                bad.Add("overlay (marks layer): missing from the surface layer (Patches/Borders.xml)");
                return;
            }
            // The pure parts: sizes, colours, fade.
            Expect(bad, g, WorldDrawLayer_LWOverlayMarks.MarkerSize(BattleKind.Repelled) == 1.1f && WorldDrawLayer_LWOverlayMarks.MarkerSize(BattleKind.Plundered) == 1.3f
                           && WorldDrawLayer_LWOverlayMarks.MarkerSize(BattleKind.Captured) == 1.5f && WorldDrawLayer_LWOverlayMarks.MarkerSize(BattleKind.Razed) == 1.5f, "the marker sizes are not 1.1 / 1.3 / 1.5 / 1.5 tiles");
            // A halo: even the smallest marker reaches past the corners of the town icon the game draws above it (0.7 tile wide: 0.35 from the centre at a side, 0.495 at a corner).
            Expect(bad, g, WorldDrawLayer_LWOverlayMarks.MarkerSize(BattleKind.Repelled) / Mathf.Sqrt(2f) > 0.7f * 0.5f * Mathf.Sqrt(2f), "the smallest marker does not reach past the town icon");
            Expect(bad, g, WorldDrawLayer_LWOverlayMarks.MarkerAlpha(0, 100) == 0.95f && Mathf.Abs(WorldDrawLayer_LWOverlayMarks.MarkerAlpha(100, 100) - 0.35f) < 1e-5f
                           && Mathf.Abs(WorldDrawLayer_LWOverlayMarks.MarkerAlpha(50, 100) - 0.65f) < 1e-5f && Mathf.Abs(WorldDrawLayer_LWOverlayMarks.MarkerAlpha(500, 100) - 0.35f) < 1e-5f, "the marker alpha does not fall linearly from 0.95 to 0.35");
            var seen = new HashSet<int>();
            foreach (BattleKind k in AllKinds)
            {
                Color32 col = WorldDrawLayer_LWOverlayMarks.MarkerColour(k);
                Expect(bad, g, col.a == 255 && seen.Add(col.r << 16 | col.g << 8 | col.b), $"the colour of {k} is not opaque and distinct");
            }
            Expect(bad, g, WorldDrawLayer_LWOverlayMarks.MarkerColour(BattleKind.Razed).r < 60, "razed is not near-black");
            Color32 capturedColour = WorldDrawLayer_LWOverlayMarks.MarkerColour(BattleKind.Captured), frontColour = WarPaint.FrontColour;
            Expect(bad, g, Mathf.Abs(capturedColour.b - frontColour.b) >= 60, "the captured marker is the red of a front (it usually lies on one)");

            WindowCutoff(bad, g, surface);

            WithMode(WorldOverlayMode.WarFronts, null, () =>
            {
                int day = SimClock.Now / GenDate.TicksPerDay;
                Expect(bad, g, WorldDrawLayer_LWOverlayMarks.ContentKey(c, surface, day) != WorldDrawLayer_LWOverlayMarks.ContentKey(c, surface, day + 1), "the content key does not change with the sim day");
                marks.SetDirty();
                marks.RegenerateNow();
                Dictionary<int, BattleKind> expected = ExpectedBattles(c, surface);
                Expect(bad, g, marks.Battles == expected.Count, $"{marks.Battles} markers for {expected.Count} distinct battle tiles in the window");
                Expect(bad, g, marks.LastVerts == 4 * marks.Battles, $"{marks.LastVerts} vertices for {marks.Battles} diamonds");
                for (int k = 0; k < 4; k++)
                    Expect(bad, g, marks.KindCounts[k] == expected.Values.Count(v => (int)v == k), $"{marks.KindCounts[k]} markers of kind {AllKinds[k]}, expected {expected.Values.Count(v => (int)v == k)}");
                if (marks.GeometryProblem() is string geo) bad.Add($"overlay ({g}): {geo}");
                int subMeshes = marks.SubMeshCount, battles0 = marks.Battles;
                int[] kinds0 = (int[])marks.KindCounts.Clone();
                for (int i = 0; i < 3; i++)
                {
                    marks.SetDirty();
                    marks.RegenerateNow();
                }
                Expect(bad, g, marks.SubMeshCount == subMeshes && marks.Battles == battles0, $"repeated regeneration changed the submeshes from {subMeshes} to {marks.SubMeshCount} or the markers from {battles0} to {marks.Battles}");
                Expect(bad, g, battles0 == 0 || marks.SubMeshCount >= 1, "markers but no submesh");
                Expect(bad, g, !marks.ContentChanged(c), "the layer reports changed content straight after a regeneration");
                if (AutoTest.Plan == "stress") Expect(bad, g, marks.LastRegenMs < 50d, $"the marks layer took {marks.LastRegenMs:F1} ms to regenerate (limit 50)");

                // Synthetic battles (removed again): two on one tile of which the newer wins, and one on another tile (the window's cutoff is WindowCutoff's).
                PlanetTile[] free = FreeTiles(surface, expected.Keys, 3);
                if (free.Length < 3)
                {
                    Log.Message(Tag + "fewer than three tiles free of markers, synthetic battles skipped");
                    return;
                }
                int now = SimClock.TicksAbs, dayTicks = GenDate.TicksPerDay, serial = SyntheticIdBase + 100;
                var added = new List<ChronicleEntry>();
                ChronicleEntry Make(ChronicleEventDef def, PlanetTile tile, int ticks) => new ChronicleEntry { id = serial++, ticksAbs = ticks, def = def, text = "overlay test", tile = tile };
                try
                {
                    int key0 = WorldDrawLayer_LWOverlayMarks.ContentKey(c, surface, day);
                    ChronicleEntry repelled = Make(LWChronicleDefOf.LW_AttackRepelled, free[1], now - 5 * dayTicks), captured = Make(LWChronicleDefOf.LW_SettlementCaptured, free[1], now - dayTicks), plundered = Make(LWChronicleDefOf.LW_SettlementPlundered, free[2], now - 10);
                    foreach (ChronicleEntry e in new[] { repelled, captured, plundered })
                    {
                        c.chronicle.entries.Add(e);
                        added.Add(e);
                    }
                    Expect(bad, g, marks.ContentChanged(c), "the layer does not report changed content after battles came in");
                    Expect(bad, g, WorldDrawLayer_LWOverlayMarks.ContentKey(c, surface, day) != key0, "the content key did not change with new battles");
                    marks.SetDirty();
                    marks.RegenerateNow();
                    Expect(bad, g, marks.Battles == battles0 + 2, $"{marks.Battles} markers, expected {battles0 + 2} (two entries on a tile give one marker)");
                    Expect(bad, g, marks.KindCounts[(int)BattleKind.Captured] == kinds0[(int)BattleKind.Captured] + 1 && marks.KindCounts[(int)BattleKind.Repelled] == kinds0[(int)BattleKind.Repelled]
                                   && marks.KindCounts[(int)BattleKind.Razed] == kinds0[(int)BattleKind.Razed] && marks.KindCounts[(int)BattleKind.Plundered] == kinds0[(int)BattleKind.Plundered] + 1,
                        "the newer battle on a tile did not win, or the older one was drawn");
                    Expect(bad, g, marks.LastVerts == 4 * marks.Battles && marks.GeometryProblem() == null, $"geometry after synthetic battles: {marks.GeometryProblem()}");
                    Expect(bad, g, marks.Battles == ExpectedBattles(c, surface).Count, "the markers do not equal the independent count after synthetic battles");
                    // The day's notification marks the layer dirty when a battle came in.
                    ChronicleEntry later = Make(LWChronicleDefOf.LW_SettlementPlundered, free[0], now);
                    c.chronicle.entries.Add(later);
                    added.Add(later);
                    Expect(bad, g, !marks.Dirty, "the marks layer is dirty before the notification");
                    NotifyDayQuietly(c);
                    Expect(bad, g, marks.Dirty, "a day's notification with a new battle did not mark the marks layer dirty");
                    marks.RegenerateNow();
                    Expect(bad, g, marks.Battles == battles0 + 3, $"{marks.Battles} markers, expected {battles0 + 3}");
                    NotifyDayQuietly(c);
                    Expect(bad, g, !marks.Dirty, "a notification with unchanged content marked the marks layer dirty");
                    Log.Message($"{Tag}battle markers: {battles0} from the chronicle ({string.Join(", ", AllKinds.Select(k => $"{k} {kinds0[(int)k]}"))}), +3 synthetic, {marks.SubMeshCount} submeshes, regen {marks.LastRegenMs:F2} ms");
                }
                finally
                {
                    foreach (ChronicleEntry e in added) c.chronicle.entries.Remove(e);
                    marks.SetDirty();
                    marks.RegenerateNow();
                }
                Expect(bad, g, marks.Battles == battles0, $"{marks.Battles} markers after the synthetic battles were removed, before {battles0}");
            });
        }

        /// <summary>
        /// The window's cutoff, on logs made here (the real chronicle's old entries lie long before anything a test could add to it, so the scan never reaches them):
        /// an entry one tick older than the window is ignored, one exactly at its start is not, and the scan stops at the first old entry from the newest.
        /// </summary>
        private static void WindowCutoff(List<string> bad, string g, PlanetLayer surface)
        {
            int now = SimClock.TicksAbs, cutoff = now - WorldDrawLayer_LWOverlayMarks.WindowTicks, serial = SyntheticIdBase;
            var tile = new PlanetTile(0, surface);
            ChronicleEntry At(int ticks, ChronicleEventDef def) => new ChronicleEntry { id = serial++, ticksAbs = ticks, def = def, text = "overlay test", tile = tile };
            int Count(params ChronicleEntry[] entries)
            {
                var log = new ChronicleLog();
                log.entries.AddRange(entries);
                WorldDrawLayer_LWOverlayMarks.ScanBattles(log, surface, now, null, out _, out int count);
                return count;
            }
            ChronicleEventDef razed = LWChronicleDefOf.LW_SettlementRazed;
            Expect(bad, g, Count(At(cutoff - 1, razed)) == 0, "a battle one tick before the window is counted");
            Expect(bad, g, Count(At(cutoff, razed)) == 1, "a battle exactly at the start of the window is not counted");
            Expect(bad, g, Count(At(cutoff - 1, razed), At(now - 10, razed)) == 1, "an old entry before a new one changed the count (expected only the new one)");
            Expect(bad, g, Count(At(cutoff - 5, razed), At(cutoff - 1, razed), At(cutoff, razed), At(now, razed)) == 2, "the entries of the window are not exactly the two inside it");
            Expect(bad, g, Count() == 0 && Count(At(now, LWChronicleDefOf.LW_Unknown)) == 0, "an empty log or an entry that is no battle is counted");
        }

        /// <summary>Ids of the entries made by tests start here, far above any the world has given out, so a test consumes none of the world's own.</summary>
        private const int SyntheticIdBase = 1_000_000_000;

        /// <summary>Some surface tiles with no battle marker (searched from tile 0, spread out).</summary>
        private static PlanetTile[] FreeTiles(PlanetLayer surface, IEnumerable<int> used, int count)
        {
            var taken = new HashSet<int>(used);
            var found = new List<PlanetTile>();
            for (int t = 0; t < surface.TilesCount && found.Count < count; t += 97)
                if (!taken.Contains(t)) found.Add(new PlanetTile(t, surface));
            return found.ToArray();
        }

        // ---- Lines ----

        /// <summary>
        /// The war band lines: an arc of about 20 tiles is split into ⌈20 / 3⌉ = 7 segments with every point lifted by 0.08 above the surface, the segments are at
        /// most 3 tiles long, a short buffer caps the segments instead of overrunning, and the frame's segments over every party stay within the budget it is given
        /// (and the tuning's 400).
        /// </summary>
        private static void Lines(List<string> bad, LivingWorldComponent c)
        {
            const string g = "lines";
            PlanetLayer surface = Find.WorldGrid.Surface;
            var marks = surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            if (marks == null)
            {
                bad.Add("overlay (marks layer): missing from the surface layer (Patches/Borders.xml)");
                return;
            }
            const float lift = 0.08f, eps = 0.02f;
            float seg = Tuning.T.overlayLineSegmentTiles;
            Expect(bad, g, seg == 3f && Tuning.T.overlayLineMaxSegments == 400 && Tuning.T.overlayBattleDays == 30, "the tuning is not 3 tiles a segment, 400 segments a frame and 30 battle days");
            // A tile and the tile about 20 tiles from it: walked outwards one neighbour at a time.
            PlanetTile a = PlanetTile.Invalid;
            foreach (CompSettlementLife life in c.activeSettlements)
                if (life.Settlement.Tile.Valid && life.Settlement.Tile.Layer == surface)
                {
                    a = life.Settlement.Tile;
                    break;
                }
            if (!a.Valid)
            {
                Log.Message(Tag + "no settlement on the surface, line geometry skipped");
                return;
            }
            PlanetTile cur = a;
            for (int step = 0; step < 60 && surface.ApproxDistanceInTiles(a.tileId, cur.tileId) < 19f; step++)
            {
                surface.GetTileNeighbors(cur.tileId, neighbourBuf);
                PlanetTile best = cur;
                float bestDistance = -1f;
                foreach (PlanetTile nb in neighbourBuf)
                {
                    float d = surface.ApproxDistanceInTiles(a.tileId, nb.tileId);
                    if (d > bestDistance)
                    {
                        bestDistance = d;
                        best = nb;
                    }
                }
                cur = best;
            }
            float tiles = surface.ApproxDistanceInTiles(a.tileId, cur.tileId);
            Vector3 from = surface.GetTileCenter(a), to = surface.GetTileCenter(cur);
            var buf = new Vector3[64];
            int k = WorldDrawLayer_LWOverlayMarks.BuildArc(from, to, lift, seg, buf);
            Expect(bad, g, k == Mathf.CeilToInt(tiles / seg), $"an arc of {tiles:F1} tiles has {k} segments, expected {Mathf.CeilToInt(tiles / seg)}");
            if (tiles > 18f && tiles <= 21f) Expect(bad, g, k == 7, $"an arc of {tiles:F1} tiles has {k} segments, expected 7 (20 / 3 rounded up)");
            float minRadius = float.MaxValue, longest = 0f;
            for (int i = 0; i <= k; i++)
            {
                minRadius = Mathf.Min(minRadius, buf[i].magnitude);
                if (i > 0) longest = Mathf.Max(longest, (buf[i] - buf[i - 1]).magnitude);
            }
            Expect(bad, g, minRadius >= surface.Radius + lift - eps, $"a point of the arc is at radius {minRadius:F3}, below the surface radius {surface.Radius:F1} plus the lift {lift}");
            Expect(bad, g, (buf[0] - (from + from.normalized * lift)).magnitude < 1e-3f && (buf[k] - (to + to.normalized * lift)).magnitude < 1e-3f, "the arc does not start and end at the lifted ends");
            Expect(bad, g, longest <= seg * Find.WorldGrid.AverageTileSize * 1.02f, $"the longest segment is {longest:F3} units, more than {seg} tiles ({seg * Find.WorldGrid.AverageTileSize:F3})");
            // The segment is shorter when the buffer is: no overrun.
            var small = new Vector3[4];
            Expect(bad, g, WorldDrawLayer_LWOverlayMarks.BuildArc(from, to, lift, seg, small) == 3, "a four-point buffer does not give three segments");
            // The same point at both ends: one segment, finite points.
            int same = WorldDrawLayer_LWOverlayMarks.BuildArc(from, from, lift, seg, buf);
            Expect(bad, g, same == 1 && float.IsFinite(buf[0].x) && float.IsFinite(buf[1].x), "an arc from a point to itself is not one finite segment");

            // The frame: parties in order, within the budget.
            int budget = Tuning.T.overlayLineMaxSegments;
            int all = marks.DrawLines(false, budget);
            Expect(bad, g, all >= 0 && all <= budget, $"the war band lines have {all} segments, budget {budget}");
            int bands = WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_WarBand && p.phase == PartyPhase.Outbound && p.Spawned && p.Tile.Valid && p.Tile.Layer == surface && p.GoalTile.Valid && p.GoalTile.Layer == surface);
            Expect(bad, g, bands == 0 || all >= 1, $"{bands} war bands marching but no line segments");
            Expect(bad, g, marks.DrawLines(false, 0) == 0, "a budget of 0 still gives segments");
            foreach (int tiny in new[] { 1, 3, 10 })
                Expect(bad, g, marks.DrawLines(false, tiny) <= tiny, $"a budget of {tiny} gives more segments");
            int last = marks.DrawLines(false, 7);
            Expect(bad, g, marks.LastLineSegments == last && last <= 7, $"the last count {marks.LastLineSegments} is not the {last} the call just made (budget 7)");
            // The ends exactly opposite each other: the arc goes round the globe on one great circle, not through the centre.
            var opposite = new Vector3[64];
            Vector3 antipode = -from;
            int round = WorldDrawLayer_LWOverlayMarks.BuildArc(from, antipode, lift, seg, opposite);
            float throughCentre = float.MaxValue;
            bool finite = true;
            for (int i = 0; i <= round; i++)
            {
                throughCentre = Mathf.Min(throughCentre, opposite[i].magnitude);
                finite &= float.IsFinite(opposite[i].x) && float.IsFinite(opposite[i].y) && float.IsFinite(opposite[i].z);
            }
            Expect(bad, g, finite && throughCentre >= surface.Radius + lift - eps, $"an arc between opposite points passes {throughCentre:F2} from the centre (finite: {finite}), the surface is at {surface.Radius:F1}");
            Expect(bad, g, (opposite[round] - (antipode + antipode.normalized * lift)).magnitude < 1e-2f, "the arc between opposite points does not end at the lifted far end");
            // Nearly opposite (float noise from a point and its antipode, a hair off): the same, whichever way the rounding fell.
            foreach (Vector3 nudge in new[] { new Vector3(1e-6f, 0f, 0f), new Vector3(0f, 3e-5f, 0f), new Vector3(0f, 0f, 1e-3f) })
            {
                var near = new Vector3[64];
                Vector3 almost = (antipode.normalized + nudge).normalized * antipode.magnitude;
                int nearK = WorldDrawLayer_LWOverlayMarks.BuildArc(from, almost, lift, seg, near);
                float nearMin = float.MaxValue;
                bool nearFinite = true;
                for (int i = 0; i <= nearK; i++)
                {
                    nearMin = Mathf.Min(nearMin, near[i].magnitude);
                    nearFinite &= float.IsFinite(near[i].x) && float.IsFinite(near[i].y) && float.IsFinite(near[i].z);
                }
                Expect(bad, g, nearFinite && nearMin >= surface.Radius + lift - eps, $"an arc between nearly opposite points (off by {nudge.magnitude}) passes {nearMin:F2} from the centre (finite: {nearFinite}), the surface is at {surface.Radius:F1}");
            }
            Log.Message($"{Tag}lines: arc of {tiles:F1} tiles in {k} segments (longest {longest:F2}, radius {minRadius:F2} up), {bands} war bands marching, {all} segments of {budget}");
        }

        // ---- Trade routes ----

        /// <summary>Up to <paramref name="count"/> live simulated towns on the surface, the first one found and those nearest to it.</summary>
        private static List<CompSettlementLife> TradeTowns(LivingWorldComponent c, PlanetLayer surface, int count)
        {
            List<CompSettlementLife> all = c.activeSettlements.Where(s => s.Active && s.Settlement.Spawned && s.Settlement.Tile.Valid && s.Settlement.Tile.Layer == surface).ToList();
            if (all.Count == 0) return all;
            int anchor = all[0].Settlement.Tile.tileId;
            return all.OrderBy(s => surface.ApproxDistanceInTiles(anchor, s.Settlement.Tile.tileId)).ThenBy(s => s.Settlement.ID).Take(count).ToList();
        }

        /// <summary>
        /// Traffic is counted and decays with the economy, roads or not: with roads switched off (in memory) and the economy on, a delivery raises the pair's traffic
        /// by exactly its amount (a real <see cref="Trade.Deliver"/>, not only <see cref="Roads.NoteDelivery"/>) and one daily step decays it once (and builds, changes
        /// and plans nothing); with the economy off nothing is counted and nothing decays, and a save prepared for removal counts nothing either. Everything is put back.
        /// </summary>
        private static void TradeGate(List<string> bad, LivingWorldComponent c)
        {
            const string g = "trade gate";
            RoadState st = c.roadState;
            List<CompSettlementLife> towns = TradeTowns(c, Find.WorldGrid.Surface, 2);
            if (st == null || towns.Count < 2)
            {
                Log.Message(Tag + "no road state or fewer than two towns, trade gate cases skipped");
                return;
            }
            Settlement a = towns[0].Settlement, b = towns[1].Settlement;
            CompSettlementLife buyer = towns[1];
            long key = Roads.PairKey(a.ID, b.ID);
            LivingWorldSettings stored = LivingWorldMod.Stored;
            bool roads = stored.roads, economy = stored.simulateEconomy, prepared = c.preparedForRemoval;
            var trafficBefore = new Dictionary<long, float>(st.traffic);
            float maxBefore = Roads.StatMaxTraffic;
            float stockBefore = buyer.stock[0], tradeBefore = buyer.recentTrade;
            float importBefore = buyer.importsDaily != null && buyer.importsDaily.Length > 0 ? buyer.importsDaily[0] : 0f;
            List<(PairValue link, float value)> linksBefore = c.tradeLinks.Select(l => (l, l.value)).ToList();
            float Traffic() => st.traffic.TryGetValue(key, out float v) ? v : 0f;
            try
            {
                st.traffic.Clear();
                // The setting is read directly (Roads.Active), so no Roads.Notify_RoadsSettingChanged here: it would cancel the players' road jobs.
                stored.roads = false;
                stored.simulateEconomy = true;
                Expect(bad, g, !Roads.Active, "Roads.Active with the setting off");
                Trade.Deliver(c, a, buyer, 0, 40f, out _);
                Expect(bad, g, Math.Abs(Traffic() - 40f) < 0.001f, $"a delivery of 40 with roads off and the economy on left traffic {Traffic()}");
                Roads.NoteDelivery(b, a, 10f); // the pair is unordered
                Expect(bad, g, Math.Abs(Traffic() - 50f) < 0.001f && st.traffic.Count == 1, $"the opposite direction gave traffic {Traffic()} on {st.traffic.Count} pairs, expected 50 on one");
                st.traffic[key] = 1000f;
                int version = Roads.Version;
                Roads.DailyStep(c, 1f);
                Expect(bad, g, Math.Abs(Traffic() - 1000f * Tuning.T.roadTrafficDecayPerDay) < 0.01f, $"one daily step with roads off took traffic from 1000 to {Traffic()}, expected {1000f * Tuning.T.roadTrafficDecayPerDay} (decayed once)");
                Expect(bad, g, Roads.Version == version, "the daily step with roads off changed a road");

                stored.simulateEconomy = false;
                st.traffic[key] = 1000f;
                Roads.NoteDelivery(a, b, 40f);
                Roads.DailyStep(c, 1f);
                Expect(bad, g, Traffic() == 1000f, $"with the economy off a delivery or the daily step changed the traffic to {Traffic()}");

                stored.simulateEconomy = true;
                c.preparedForRemoval = true;
                Expect(bad, g, !LivingWorldMod.Settings.simulateEconomy, "the all-off set simulates the economy");
                st.traffic.Clear();
                Roads.NoteDelivery(a, b, 40f);
                Expect(bad, g, st.traffic.Count == 0, "a delivery was counted on a save prepared for removal");
            }
            finally
            {
                c.preparedForRemoval = prepared;
                stored.roads = roads;
                stored.simulateEconomy = economy;
                buyer.stock[0] = stockBefore; // what the delivery did to the buyer
                buyer.recentTrade = tradeBefore;
                if (buyer.importsDaily != null && buyer.importsDaily.Length > 0) buyer.importsDaily[0] = importBefore;
                c.tradeLinks.Clear(); // the faction trade volume the delivery added
                foreach ((PairValue link, float value) in linksBefore)
                {
                    link.value = value;
                    c.tradeLinks.Add(link);
                }
                st.traffic.Clear();
                foreach (KeyValuePair<long, float> kv in trafficBefore) st.traffic[kv.Key] = kv.Value;
                Roads.StatMaxTraffic = maxBefore;
            }
        }

        /// <summary>The width band's rank terciles, and a ribbon along an arc: its vertices, colours and triangles, every point off the surface, the strip's width, every triangle facing outwards.</summary>
        private static void RibbonGeometry(List<string> bad, LivingWorldComponent c)
        {
            const string g = "trade ribbons";
            // Bands: terciles of the ranking (the busiest third wide), whatever the traffic; a lone pair is wide.
            Expect(bad, g, WorldDrawLayer_LWOverlayMarks.WidthBand(0, 1) == 2 && WorldDrawLayer_LWOverlayMarks.WidthBand(0, 2) == 2 && WorldDrawLayer_LWOverlayMarks.WidthBand(1, 2) == 1, "a lone pair is not wide, or two pairs are not wide and middle");
            Expect(bad, g, Enumerable.Range(0, 3).Select(i => WorldDrawLayer_LWOverlayMarks.WidthBand(i, 3)).SequenceEqual(new[] { 2, 1, 0 }), "three pairs are not wide, middle and thin");
            var counts = new int[3];
            for (int i = 0; i < 150; i++) counts[WorldDrawLayer_LWOverlayMarks.WidthBand(i, 150)]++;
            Expect(bad, g, counts[0] == 50 && counts[1] == 50 && counts[2] == 50, $"150 pairs fall into the bands {string.Join("/", counts)}, not 50 each");
            bool monotone = true;
            for (int n = 1; n <= 40; n++)
                for (int i = 1; i < n; i++) monotone &= WorldDrawLayer_LWOverlayMarks.WidthBand(i, n) <= WorldDrawLayer_LWOverlayMarks.WidthBand(i - 1, n);
            Expect(bad, g, monotone, "a less busy pair has a wider band than a busier one");
            PlanetLayer surface = Find.WorldGrid.Surface;
            List<CompSettlementLife> towns = TradeTowns(c, surface, 12);
            if (towns.Count < 2)
            {
                Log.Message(Tag + "fewer than two towns on the surface, ribbon geometry skipped");
                return;
            }
            Vector3 from = surface.GetTileCenter(towns[0].Settlement.Tile), to = surface.GetTileCenter(towns[towns.Count - 1].Settlement.Tile);
            const float lift = 0.04f, eps = 0.02f;
            var buf = new Vector3[64];
            int k = WorldDrawLayer_LWOverlayMarks.BuildArc(from, to, lift, 0.5f, buf);
            float tiles = surface.ApproxDistanceInTiles(towns[0].Settlement.Tile.tileId, towns[towns.Count - 1].Settlement.Tile.tileId);
            if (tiles <= 30f) Expect(bad, g, Math.Abs(k - Mathf.CeilToInt(tiles / 0.5f)) <= 1, $"an arc of {tiles:F1} tiles has {k} half-tile segments, expected {Mathf.CeilToInt(tiles / 0.5f)}");
            float half = 0.15f * Find.WorldGrid.AverageTileSize;
            var sm = new LayerSubMesh(null, null);
            WorldDrawLayer_LWOverlayMarks.PrintRibbon(sm, buf, k, half, WorldDrawLayer_LWOverlayMarks.TradeColour);
            Expect(bad, g, sm.verts.Count == 2 * (k + 1) && sm.colors.Count == sm.verts.Count && sm.tris.Count == 6 * k, $"a ribbon of {k} segments has {sm.verts.Count} vertices, {sm.colors.Count} colours and {sm.tris.Count} triangle indices");
            bool colours = true, finite = true, indices = true;
            float minRadius = float.MaxValue, widthError = 0f;
            for (int i = 0; i < sm.verts.Count; i++)
            {
                colours &= sm.colors[i].r == 240 && sm.colors[i].g == 205 && sm.colors[i].b == 75 && sm.colors[i].a == 140;
                finite &= float.IsFinite(sm.verts[i].x) && float.IsFinite(sm.verts[i].y) && float.IsFinite(sm.verts[i].z);
                minRadius = Mathf.Min(minRadius, sm.verts[i].magnitude);
            }
            for (int i = 0; i <= k && 2 * i + 1 < sm.verts.Count; i++) widthError = Mathf.Max(widthError, Mathf.Abs((sm.verts[2 * i + 1] - sm.verts[2 * i]).magnitude - 2f * half));
            foreach (int t in sm.tris) indices &= t >= 0 && t < sm.verts.Count;
            Expect(bad, g, colours, "a ribbon vertex is not the trade colour (240,205,75) at alpha 0.55");
            Expect(bad, g, finite && indices, "a ribbon has a vertex that is not finite or a triangle index outside its vertices");
            Expect(bad, g, minRadius >= surface.Radius + lift - eps, $"a ribbon vertex is at radius {minRadius:F3}, below the surface radius {surface.Radius:F1} plus the lift {lift}");
            Expect(bad, g, widthError < half * 0.02f, $"the ribbon's width is off by {widthError:F4} (half-width {half:F4})");
            int backwards = 0;
            for (int i = 0; i + 2 < sm.tris.Count; i += 3)
            {
                Vector3 va = sm.verts[sm.tris[i]], vb = sm.verts[sm.tris[i + 1]], vc = sm.verts[sm.tris[i + 2]];
                if (Vector3.Dot(Vector3.Cross(vb - va, vc - va), (va + vb + vc).normalized) < 0f) backwards++;
            }
            Expect(bad, g, backwards == 0, $"{backwards} of the ribbon's {sm.tris.Count / 3} triangles face into the planet");
            // The same ribbon the other way round faces outwards too.
            var back = new LayerSubMesh(null, null);
            var reversed = new Vector3[k + 1];
            for (int i = 0; i <= k; i++) reversed[i] = buf[k - i];
            WorldDrawLayer_LWOverlayMarks.PrintRibbon(back, reversed, k, half, WorldDrawLayer_LWOverlayMarks.TradeColour);
            int backwards2 = 0;
            for (int i = 0; i + 2 < back.tris.Count; i += 3)
                if (Vector3.Dot(Vector3.Cross(back.verts[back.tris[i + 1]] - back.verts[back.tris[i]], back.verts[back.tris[i + 2]] - back.verts[back.tris[i]]), (back.verts[back.tris[i]] + back.verts[back.tris[i + 1]] + back.verts[back.tris[i + 2]]).normalized) < 0f) backwards2++;
            Expect(bad, g, backwards2 == 0, $"{backwards2} triangles of the ribbon drawn the other way face into the planet");
            // A degenerate arc (a town on itself) is a finite strip, not NaN.
            var same = new LayerSubMesh(null, null);
            int sk = WorldDrawLayer_LWOverlayMarks.BuildArc(from, from, lift, 0.5f, buf);
            WorldDrawLayer_LWOverlayMarks.PrintRibbon(same, buf, sk, half, WorldDrawLayer_LWOverlayMarks.TradeColour);
            Expect(bad, g, same.verts.All(v => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z)), "a ribbon from a point to itself has a vertex that is not finite");
            Log.Message($"{Tag}ribbon geometry: arc of {tiles:F1} tiles in {k} segments, {sm.verts.Count} vertices, radius {minRadius:F2} up, width {2f * half:F3}");
        }

        /// <summary>
        /// The ribbons: on the run's real traffic the pairs drawn are exactly the busiest that qualify (counted here from the dictionary), at most the cap, both ends live and
        /// simulated; with no traffic nothing is drawn and nothing fails; synthetic traffic (put back afterwards): the minimum, the cap, the order, a pair with a town
        /// that is gone, the width bands, the content key (a new pair or a new ranking changes it, decay of every pair at once does not) and the day's notification (its cost too, on the real traffic).
        /// </summary>
        private static void TradeRibbons(List<string> bad, LivingWorldComponent c)
        {
            const string g = "trade ribbons";
            PlanetLayer surface = Find.WorldGrid.Surface;
            var marks = surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            if (marks == null)
            {
                bad.Add("overlay (marks layer): missing from the surface layer (Patches/Borders.xml)");
                return;
            }
            RibbonGeometry(bad, c);
            RoadState st = c.roadState;
            if (st == null || WorldOverlay.Blocker(WorldOverlayMode.Trade) != OverlayBlock.None)
            {
                Log.Message(Tag + "no road state or the economy is off, ribbon cases skipped");
                return;
            }
            int cap = Tuning.T.overlayTradePairs;
            float min = Tuning.T.overlayTradeMinTraffic;
            Expect(bad, g, cap == 150 && min == 50f, "the tuning is not 150 pairs and 50 traffic");
            var trafficBefore = new Dictionary<long, float>(st.traffic);
            int capBefore = cap;
            WithMode(WorldOverlayMode.Trade, null, () =>
            {
                try
                {
                    // The real traffic: counted again here from the dictionary, without the layer's selection.
                    int qualifying = 0;
                    foreach (KeyValuePair<long, float> kv in trafficBefore)
                    {
                        if (kv.Value < min) continue;
                        Roads.SplitKey(kv.Key, out int ia, out int ib);
                        if (RibbonTown(ia, surface) && RibbonTown(ib, surface)) qualifying++;
                    }
                    marks.SetDirty();
                    marks.RegenerateNow();
                    Expect(bad, g, marks.Pairs == Math.Min(cap, qualifying), $"{marks.Pairs} ribbons for {qualifying} qualifying pairs of the real traffic (cap {cap})");
                    Expect(bad, g, marks.WidthCounts.Sum() == marks.Pairs && marks.GeometryProblem() == null, $"geometry or band counts with the real traffic: {marks.GeometryProblem()} (bands {string.Join("/", marks.WidthCounts)} of {marks.Pairs})");
                    Expect(bad, g, marks.Battles == 0 && marks.KindCounts.Sum() == 0, "battle markers were drawn in the trade routes mode");
                    Expect(bad, g, marks.Pairs == 0 ? marks.SubMeshCount == 0 || marks.LastVerts == 0 : marks.LastVerts >= 4 * marks.Pairs && marks.LastVerts % 2 == 0, $"{marks.LastVerts} vertices for {marks.Pairs} ribbons");
                    if (AutoTest.Plan == "stress") Expect(bad, g, marks.LastRegenMs < 50d, $"the marks layer took {marks.LastRegenMs:F1} ms to regenerate the ribbons (limit 50)");
                    Log.Message($"{Tag}trade ribbons on the real traffic: {st.traffic.Count} pairs counted, {qualifying} qualify, {marks.Pairs} drawn (bands {string.Join("/", marks.WidthCounts)}), {marks.LastVerts} vertices, regen {marks.LastRegenMs:F2} ms");
                    // The day's notification on the real traffic (one pick and a sort of every qualifying pair).
                    double notifyMs = NotifyDayQuietly(c);
                    if (AutoTest.Plan == "stress") Expect(bad, g, notifyMs < 5d, $"the daily notification in Trade routes took {notifyMs:F2} ms (limit 5)");

                    // No traffic at all: nothing to draw, no error.
                    st.traffic.Clear();
                    marks.SetDirty();
                    marks.RegenerateNow();
                    Expect(bad, g, marks.Pairs == 0 && marks.LastVerts == 0 && marks.GeometryProblem() == null, $"{marks.Pairs} ribbons and {marks.LastVerts} vertices with no traffic ({marks.GeometryProblem()})");
                    marks.Render(); // visible or not: no throw
                    int emptyKey = WorldDrawLayer_LWOverlayMarks.TradeKey(c, surface);

                    // Synthetic traffic between the towns near one town.
                    List<CompSettlementLife> towns = TradeTowns(c, surface, 6);
                    if (towns.Count < 5)
                    {
                        Log.Message(Tag + "fewer than five towns on the surface, synthetic ribbon cases skipped");
                        return;
                    }
                    long Key(int i, int j) => Roads.PairKey(towns[i].Settlement.ID, towns[j].Settlement.ID);
                    long gone = Roads.PairKey(towns[0].Settlement.ID, 2_000_000_000);
                    // Busiest first: 0-1 (4000), 0-2 (900), 1-2 and 0-3 tied (300; the lower key first), 2-3 (49.9: under the minimum), 0-4 (5000 with a town that is gone).
                    st.traffic[Key(0, 1)] = 4000f;
                    st.traffic[Key(0, 2)] = 900f;
                    st.traffic[Key(1, 2)] = 300f;
                    st.traffic[Key(0, 3)] = 300f;
                    st.traffic[Key(2, 3)] = min - 0.1f;
                    st.traffic[gone] = 5000f;
                    var picked = new List<KeyValuePair<long, float>>();
                    int n = WorldDrawLayer_LWOverlayMarks.PickPairs(c, surface, picked);
                    Expect(bad, g, n == 4 && picked.Count == 4, $"{n} pairs picked, expected 4 (one under the minimum, one with a town that is gone)");
                    if (picked.Count == 4)
                    {
                        long tieFirst = Math.Min(Key(1, 2), Key(0, 3)), tieSecond = Math.Max(Key(1, 2), Key(0, 3));
                        Expect(bad, g, picked[0].Key == Key(0, 1) && picked[1].Key == Key(0, 2) && picked[2].Key == tieFirst && picked[3].Key == tieSecond, "the pairs are not busiest first with the key breaking the tie");
                    }
                    marks.SetDirty();
                    marks.RegenerateNow();
                    Expect(bad, g, marks.Pairs == 4 && marks.WidthCounts.Sum() == 4, $"{marks.Pairs} ribbons for 4 qualifying pairs");
                    Expect(bad, g, marks.WidthCounts[2] == 2 && marks.WidthCounts[1] == 1 && marks.WidthCounts[0] == 1, $"the bands are {string.Join("/", marks.WidthCounts)}: expected 1/1/2 (thin/middle/wide) for 4 pairs");
                    Expect(bad, g, marks.GeometryProblem() == null && marks.LastVerts >= 4 * 4 && marks.LastVerts == marks.LastVerts / 2 * 2, $"geometry: {marks.GeometryProblem()}, {marks.LastVerts} vertices");
                    int subMeshes = marks.SubMeshCount, verts = marks.LastVerts;
                    for (int i = 0; i < 3; i++)
                    {
                        marks.SetDirty();
                        marks.RegenerateNow();
                    }
                    Expect(bad, g, marks.SubMeshCount == subMeshes && marks.LastVerts == verts && marks.Pairs == 4, "repeated regeneration changed the submeshes, the vertices or the ribbons");
                    Expect(bad, g, !marks.ContentChanged(c), "the layer reports changed content straight after a regeneration");

                    // The key: a new pair changes it; every pair decaying at once (the same bands) does not; a band change does.
                    int key0 = WorldDrawLayer_LWOverlayMarks.TradeKey(c, surface);
                    Expect(bad, g, key0 != emptyKey, "the content key is the same with and without pairs");
                    foreach (long k in st.traffic.Keys.ToList()) st.traffic[k] *= Tuning.T.roadTrafficDecayPerDay;
                    Expect(bad, g, WorldDrawLayer_LWOverlayMarks.TradeKey(c, surface) == key0 && !marks.ContentChanged(c), "decay of every pair at once (a day's decay) changed the content key");
                    st.traffic[Key(1, 3)] = 1000f;
                    Expect(bad, g, marks.ContentChanged(c), "a new pair did not change the content key");
                    // The day's notification dirties the layer for a change, and not without one.
                    Expect(bad, g, !marks.Dirty, "the marks layer is dirty before the notification");
                    NotifyDayQuietly(c);
                    Expect(bad, g, marks.Dirty, "a day's notification with a new pair did not mark the marks layer dirty");
                    marks.RegenerateNow();
                    Expect(bad, g, marks.Pairs == 5, $"{marks.Pairs} ribbons after a fifth pair, expected 5");
                    NotifyDayQuietly(c);
                    Expect(bad, g, !marks.Dirty, "a notification with unchanged traffic marked the marks layer dirty");
                    st.traffic[Key(1, 3)] = 400000f; // far busier than the rest and past the pair that led: the ranking changes, the others stay in three widths
                    Expect(bad, g, marks.ContentChanged(c), "a change of the ranking did not change the content key");
                    marks.SetDirty();
                    marks.RegenerateNow();
                    Expect(bad, g, marks.WidthCounts[2] == 2 && marks.WidthCounts[1] == 2 && marks.WidthCounts[0] == 1, $"the bands after one far busier pair are {string.Join("/", marks.WidthCounts)}, expected 1/2/2: an outlier thins no one");

                    // The cap: only the busiest pairs.
                    Tuning.T.overlayTradePairs = 2;
                    int capped = WorldDrawLayer_LWOverlayMarks.PickPairs(c, surface, picked);
                    Expect(bad, g, capped == 2 && picked[0].Key == Key(1, 3) && picked[1].Key == Key(0, 1), $"a cap of 2 picked {capped} pairs, not the two busiest");
                    marks.SetDirty();
                    marks.RegenerateNow();
                    Expect(bad, g, marks.Pairs == 2, $"{marks.Pairs} ribbons with a cap of 2");
                    Log.Message($"{Tag}trade ribbons: synthetic 5 pairs ok (bands {string.Join("/", marks.WidthCounts)} at the cap of 2), key {key0:X8}");
                }
                finally
                {
                    Tuning.T.overlayTradePairs = capBefore;
                    st.traffic.Clear();
                    foreach (KeyValuePair<long, float> kv in trafficBefore) st.traffic[kv.Key] = kv.Value;
                    marks.SetDirty();
                }
            });
        }

        /// <summary>A live simulated town on the layer (counted here without the layer's own check).</summary>
        private static bool RibbonTown(int id, PlanetLayer surface) =>
            LivingWorldComponent.WorldObjectById(id) is Settlement s && s.Spawned && s.Tile.Valid && s.Tile.Layer == surface && s.GetComponent<CompSettlementLife>() is CompSettlementLife l && l.Active;

        /// <summary>The merchant lines: only merchant parties on their way out, within the frame's budget, a budget of 0 giving none; war band lines are not counted among them.</summary>
        private static void TradeLines(List<string> bad, LivingWorldComponent c)
        {
            const string g = "trade lines";
            PlanetLayer surface = Find.WorldGrid.Surface;
            var marks = surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            if (marks == null) return;
            int budget = Tuning.T.overlayLineMaxSegments;
            int all = marks.DrawLines(false, budget, LWPartyDefOf.LW_Merchants);
            Expect(bad, g, all >= 0 && all <= budget, $"the merchant lines have {all} segments, budget {budget}");
            int merchants = WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_Merchants && p.phase == PartyPhase.Outbound && p.Spawned && p.Tile.Valid && p.Tile.Layer == surface && p.GoalTile.Valid && p.GoalTile.Layer == surface);
            Expect(bad, g, merchants == 0 ? all == 0 : all >= 1, $"{merchants} merchants marching but {all} line segments");
            Expect(bad, g, marks.DrawLines(false, 0, LWPartyDefOf.LW_Merchants) == 0, "a budget of 0 still gives merchant segments");
            foreach (int tiny in new[] { 1, 3, 10 })
                Expect(bad, g, marks.DrawLines(false, tiny, LWPartyDefOf.LW_Merchants) <= tiny, $"a budget of {tiny} gives more merchant segments");
            int wars = marks.DrawLines(false, budget, LWPartyDefOf.LW_WarBand);
            int bands = WorldParty.All.Count(p => p.kind == LWPartyDefOf.LW_WarBand && p.phase == PartyPhase.Outbound && p.Spawned && p.Tile.Valid && p.Tile.Layer == surface && p.GoalTile.Valid && p.GoalTile.Layer == surface);
            Expect(bad, g, bands == 0 ? wars == 0 : wars >= 1, $"{bands} war bands marching but {wars} line segments");
            Expect(bad, g, marks.DrawLines(false, budget) == wars, "the two-argument DrawLines is not the war band lines");
            Log.Message($"{Tag}trade lines: {merchants} merchants marching, {all} segments of {budget} (war bands {bands}, {wars} segments)");
        }

        /// <summary>With the layer hidden (a mode without marks, or in War fronts another planet layer selected) Render draws nothing, not even a line, and does not throw.</summary>
        private static void RenderGuard(List<string> bad)
        {
            const string g = "render guard";
            var marks = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            if (marks == null) return;
            long drawn = WorldDrawLayer_LWOverlayMarks.LineSegmentsDrawn;
            WithMode(WorldOverlayMode.Borders, null, () =>
            {
                Expect(bad, g, !marks.Visible, "the marks layer is visible in Borders");
                marks.Render();
            });
            Expect(bad, g, WorldDrawLayer_LWOverlayMarks.LineSegmentsDrawn == drawn, "a hidden marks layer drew line segments");
            // War fronts with the layer hidden on purpose: another planet layer (orbit) selected. The marks layer is not drawn for a layer that is not the selected one.
            // The map view is no such case: the layer reports itself visible there (only the renderer, which does not call it, is what hides it).
            PlanetLayer other = Find.WorldGrid.PlanetLayers.Values.FirstOrDefault(l => !l.IsRootSurface && l.TilesCount > 0);
            if (other == null)
            {
                Log.Message(Tag + "the world has only the surface layer, hidden render guard for War fronts skipped");
                return;
            }
            WorldSelector selector = Find.WorldSelector;
            PlanetLayer was = selector.SelectedLayer;
            PlanetTile tile = selector.SelectedTile;
            try
            {
                foreach (WorldOverlayMode mode in new[] { WorldOverlayMode.WarFronts, WorldOverlayMode.Trade })
                {
                    WorldOverlayMode m = mode;
                    WithMode(m, null, () =>
                    {
                        selector.SelectedLayer = other;
                        Expect(bad, g, !marks.Visible, $"the marks layer is visible in {m} with the {other.Def.defName} layer selected");
                        marks.Render();
                    });
                }
            }
            finally
            {
                selector.SelectedLayer = was;
                selector.SelectedTile = tile;
            }
            Expect(bad, g, WorldDrawLayer_LWOverlayMarks.LineSegmentsDrawn == drawn, "a hidden marks layer (War fronts or Trade routes, another layer selected) drew line segments");
        }

        // ---- Metrics ----

        private static readonly List<KeyValuePair<long, float>> metricPairs = new List<KeyValuePair<long, float>>();

        /// <summary>The ribbons the trade routes mode would draw now (the pairs that qualify), whatever the mode and whether the layer has been regenerated.</summary>
        private static int TradePairCount()
        {
            int n = WorldDrawLayer_LWOverlayMarks.PickPairs(LivingWorldComponent.Instance, Find.WorldGrid.Surface, metricPairs);
            metricPairs.Clear();
            return n;
        }

        /// <summary>
        /// One daily line: the mode, the tint layer's last full and incremental regeneration, how many towns changed prosperity band the last day (counted in any
        /// mode during autotests) of how many were looked at, and in a heat mode how many regions the tint layer has marked. A pass that gave towns their first band
        /// cannot tell changes: it says <c>seed</c> (all of them new) or lists the seeded towns apart, never a zero that is only the lack of a past.
        /// </summary>
        internal static string Metrics()
        {
            if (LivingWorldComponent.Instance == null || !Territory.Ready) return null;
            var layer = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWBorders>();
            int sources = WorldOverlay.LastBucketSources, seeded = WorldOverlay.LastBucketSeeded;
            string changes = sources > 0 && seeded == sources ? $"seed/{sources}" : $"{WorldOverlay.LastBucketChanges}/{sources - seeded}{(seeded > 0 ? $"+{seeded}seeded" : "")}";
            string regions = WorldOverlay.LastRegionsMarked < 0 ? "-" : $"{WorldOverlay.LastRegionsMarked}/{WorldOverlay.LastRegionCount}{(WorldOverlay.LastRegionsFull ? "(full)" : "")}";
            var marks = Find.WorldGrid.Surface.GetLayer<WorldDrawLayer_LWOverlayMarks>();
            return $"overlay mode={WorldOverlay.Mode} tintFullMs={layer?.LastFullRegenMs ?? 0d:F1} tintIncMs={layer?.LastIncrementalRegenMs ?? 0d:F2} marksMs={marks?.LastRegenMs ?? 0d:F2} " +
                   $"tradePairs={TradePairCount()}/{LivingWorldComponent.Instance?.roadState?.traffic.Count ?? 0} battles={marks?.Battles ?? 0} wars={WorldOverlay.WarCount} fronts={layer?.LastWarEdges ?? 0} lineSegments={marks?.LastLineSegments ?? 0} " +
                   $"bucketChanges={changes} regionsMarked={regions} notifyMs={WorldOverlay.LastNotifyMs:F2}";
        }
    }
}
