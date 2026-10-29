using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>The kinds of battle the war fronts picture marks, from the chronicle's entries about attacks on towns.</summary>
    internal enum BattleKind : byte { Repelled, Plundered, Captured, Razed }

    /// <summary>
    /// The overlay's second layer: small geometry that is not territory (markers, ribbons) and lines, for the modes that have any. It
    /// draws above the territory tint and below the game's own world lines, on a material of its own (two layers on the same material
    /// and queue have no defined order). In the war fronts mode it marks the battles of the last <see cref="LivingWorldTuningDef.overlayBattleDays"/> days
    /// (a diamond halo around the town's icon, by kind, fading with age; the newest battle of a tile wins) and, every frame, draws a line from each marching war band
    /// to its target, as lifted great-circle segments within a budget per frame. In the trade routes mode it draws a ribbon along the great circle between each
    /// of the busiest pairs of trading towns (<see cref="RoadState.traffic"/>: goods delivered, decaying; wider for more traffic) and, the same way, a line from each
    /// marching merchant party to its destination.
    /// </summary>
    public class WorldDrawLayer_LWOverlayMarks : WorldDrawLayer
    {
        private const int MarksRenderQueue = 3546, LineRenderQueue = 3590;
        private const float MarkerLift = 0.05f, LineLift = 0.08f, WarLineWidth = 0.6f, MerchantLineWidth = 0.35f;
        // A trade ribbon: its points every half tile, a hair off the surface (under the lines), a half-width in tiles by traffic band.
        private const float TradeLift = 0.04f, TradeSegmentTiles = 0.5f;
        private static readonly float[] RibbonHalfWidthTiles = { 0.06f, 0.10f, 0.15f };
        private const float NewAlpha = 0.95f, OldAlpha = 0.35f;
        private const int PointBufferSize = 64;

        private static Material marksMat, warLineMat, merchantLineMat;

        /// <summary>The war band line's colour (the material and the legend's swatch use this one).</summary>
        internal static readonly Color32 WarLineColour = new Color32(230, 25, 25, 230);

        /// <summary>A trade ribbon's colour: gold at alpha 0.55 (the legend's swatch uses this one).</summary>
        internal static readonly Color32 TradeColour = new Color32(240, 205, 75, 140);

        /// <summary>The merchant line's colour: a brighter, nearly opaque gold, so it reads over the ribbons.</summary>
        internal static readonly Color32 MerchantLineColour = new Color32(255, 214, 64, 235);

        // One buffer for every line of a frame (the points of one arc at a time): nothing is allocated while drawing.
        private static readonly Vector3[] arcBuffer = new Vector3[PointBufferSize];

        // The scan's working data, reused.
        private static readonly List<ChronicleEntry> scanned = new List<ChronicleEntry>();
        private static readonly HashSet<int> scannedTiles = new HashSet<int>();

        /// <summary>A copy of the game's vertex colour material one queue step above it (created on first use, on the main thread).</summary>
        internal static Material MarksMat
        {
            get
            {
                // Not ??=, which tests for a real null only: a destroyed Material is a "fake null" that only Unity's == sees, and would be reused.
                if (marksMat == null) marksMat = new Material(WorldMaterials.VertexColorTransparent) { renderQueue = MarksRenderQueue };
                return marksMat;
            }
        }

        /// <summary>The material of a war band's line: the game's world line texture and shader, in red, at the queue of the game's own world lines.</summary>
        private static Material WarLineMat
        {
            get
            {
                if (warLineMat == null) warLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.WorldOverlayTransparent, new Color(WarLineColour.r / 255f, WarLineColour.g / 255f, WarLineColour.b / 255f, WarLineColour.a / 255f), LineRenderQueue);
                return warLineMat;
            }
        }

        /// <summary>The material of a merchant party's line: like the war band's, in gold.</summary>
        private static Material MerchantLineMat
        {
            get
            {
                if (merchantLineMat == null) merchantLineMat = MaterialPool.MatFrom(GenDraw.LineTexPath, ShaderDatabase.WorldOverlayTransparent, new Color(MerchantLineColour.r / 255f, MerchantLineColour.g / 255f, MerchantLineColour.b / 255f, MerchantLineColour.a / 255f), LineRenderQueue);
                return merchantLineMat;
            }
        }

        public override bool Visible =>
            base.Visible && planetLayer.IsRootSurface && LivingWorldComponent.Instance != null && WorldOverlay.MarksMode;

        public override bool VisibleWhenLayerNotSelected => false;

        public override bool VisibleInBackground => false;

        public int SubMeshCount => subMeshes.Count;

        /// <summary>Markers drawn by the last regeneration (distinct tiles), how many of each kind, and the segments of the last frame's lines (or of the last count).</summary>
        public int Battles, LastLineSegments;
        internal readonly int[] KindCounts = new int[4];
        /// <summary>Trade ribbons drawn by the last regeneration, and how many of them in each width band.</summary>
        public int Pairs;
        internal readonly int[] WidthCounts = new int[3];
        public double LastRegenMs;
        /// <summary>Vertices of the last regeneration.</summary>
        public int LastVerts;
        /// <summary>Line segments drawn since the world began, for tests: a layer that is not visible must draw none.</summary>
        public static long LineSegmentsDrawn;

        private int builtKey;
        private bool built;

        /// <summary>For the autotest: geometry that would not be drawn (built but never finalized), a submesh on another material than the marks one, or totals that disagree; null when all is well.</summary>
        public string GeometryProblem()
        {
            int verts = 0, unfinalized = 0, wrongMaterial = 0;
            foreach (LayerSubMesh sm in subMeshes)
            {
                verts += sm.verts.Count;
                if (sm.verts.Count > 0 && !sm.finalized) unfinalized++;
                if (sm.verts.Count > 0 && (sm.material == null || sm.material.renderQueue != MarksRenderQueue)) wrongMaterial++;
            }
            if (unfinalized > 0) return $"{unfinalized} submeshes hold geometry that was never finalized (not drawn)";
            if (wrongMaterial > 0) return $"{wrongMaterial} submeshes with geometry are not on the marks material (queue {MarksRenderQueue})";
            return verts != LastVerts ? $"the submeshes hold {verts} vertices, the running total says {LastVerts}" : null;
        }

        // ---- Battles ----

        /// <summary>The kind of battle a chronicle entry records, false for anything else.</summary>
        internal static bool TryKindOf(ChronicleEntry e, out BattleKind kind)
        {
            ChronicleEventDef def = e.def;
            if (def == LWChronicleDefOf.LW_AttackRepelled) kind = BattleKind.Repelled;
            else if (def == LWChronicleDefOf.LW_SettlementPlundered) kind = BattleKind.Plundered;
            else if (def == LWChronicleDefOf.LW_SettlementCaptured) kind = BattleKind.Captured;
            else if (def == LWChronicleDefOf.LW_SettlementRazed) kind = BattleKind.Razed;
            else
            {
                kind = default;
                return false;
            }
            return true;
        }

        /// <summary>
        /// The marker's width in tiles: a halo around the town icon (the game draws it 0.7 tile wide above this layer), smaller for a repulsed attack, larger for
        /// a town lost. The diamond's tips reach 0.78 to 1.06 tiles from the centre, clear of the icon's square (0.35 tile out, 0.5 at the corners).
        /// </summary>
        internal static float MarkerSize(BattleKind kind)
        {
            switch (kind)
            {
                case BattleKind.Repelled: return 1.1f;
                case BattleKind.Plundered: return 1.3f;
                default: return 1.5f;
            }
        }

        /// <summary>The marker's colour (alpha 1): amber, orange, crimson (not the red of a front, which the captured towns usually lie on), near-black.</summary>
        internal static Color32 MarkerColour(BattleKind kind)
        {
            switch (kind)
            {
                case BattleKind.Repelled: return new Color32(245, 175, 40, 255);
                case BattleKind.Plundered: return new Color32(235, 105, 30, 255);
                case BattleKind.Captured: return new Color32(190, 25, 110, 255);
                default: return new Color32(35, 30, 30, 255);
            }
        }

        /// <summary>A marker's alpha from its age: 0.95 when new, falling linearly to 0.35 at the end of the window.</summary>
        internal static float MarkerAlpha(int ageTicks, int windowTicks) =>
            Mathf.Lerp(NewAlpha, OldAlpha, windowTicks <= 0 ? 1f : Mathf.Clamp01(ageTicks / (float)windowTicks));

        internal static int WindowTicks => Mathf.Max(1, Tuning.T.overlayBattleDays) * GenDate.TicksPerDay;

        /// <summary>
        /// Walks the chronicle from the newest entry back until one is older than the window, over the entries that are battles on a tile of
        /// <paramref name="layer"/>: how many there are and the newest one's id. The distinct tiles' newest entries go to <paramref name="into"/> when given.
        /// </summary>
        internal static void ScanBattles(ChronicleLog log, PlanetLayer layer, int nowAbs, List<ChronicleEntry> into, out int newestId, out int count)
        {
            newestId = -1;
            count = 0;
            into?.Clear();
            scannedTiles.Clear();
            if (log == null || layer == null) return;
            int cutoff = nowAbs - WindowTicks;
            List<ChronicleEntry> entries = log.entries;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                ChronicleEntry e = entries[i];
                if (e.ticksAbs < cutoff) break;
                if (!TryKindOf(e, out _) || !e.tile.Valid || e.tile.Layer != layer) continue;
                if (count++ == 0) newestId = e.id;
                if (into != null && scannedTiles.Add(e.tile.tileId)) into.Add(e);
            }
        }

        /// <summary>
        /// What the picture depends on. Trade routes: the pairs drawn and the width band of each. Otherwise (the battle markers): the newest battle in the window,
        /// how many there are, and (while markers fade) the sim day.
        /// </summary>
        internal static int ContentKey(LivingWorldComponent comp, PlanetLayer layer, int day)
        {
            if (WorldOverlay.Mode == WorldOverlayMode.Trade) return TradeKey(comp, layer);
            ScanBattles(comp?.chronicle, layer, SimClock.TicksAbs, null, out int newest, out int count);
            return System.HashCode.Combine(newest, count, day);
        }

        private static int Today => SimClock.Now / GenDate.TicksPerDay;

        /// <summary>The marks' content as of now differs from what was last built (a new battle, an old one out of the window, a new day: the markers fade).</summary>
        internal bool ContentChanged(LivingWorldComponent comp) => !built || ContentKey(comp, planetLayer, Today) != builtKey;

        // ---- Trade ribbons ----

        private static readonly List<KeyValuePair<long, float>> candidates = new List<KeyValuePair<long, float>>(), keyPairs = new List<KeyValuePair<long, float>>(), drawnPairs = new List<KeyValuePair<long, float>>();

        /// <summary>The town is a live simulated settlement, spawned on <paramref name="layer"/>.</summary>
        private static bool DrawnTown(int objectId, PlanetLayer layer, out Settlement town)
        {
            town = LivingWorldComponent.WorldObjectById(objectId) as Settlement;
            if (town == null || !town.Spawned || !town.Tile.Valid || town.Tile.Layer != layer) return false;
            CompSettlementLife life = town.GetComponent<CompSettlementLife>();
            return life != null && life.Active;
        }

        /// <summary>
        /// The pairs of towns that get a ribbon, busiest first (the pair key breaks ties, so the order never depends on dictionary order): at most
        /// <see cref="LivingWorldTuningDef.overlayTradePairs"/> pairs, each with at least <see cref="LivingWorldTuningDef.overlayTradeMinTraffic"/> traffic
        /// and both towns live, simulated and on <paramref name="layer"/>. Returns how many went into <paramref name="into"/> (cleared first).
        /// </summary>
        internal static int PickPairs(LivingWorldComponent comp, PlanetLayer layer, List<KeyValuePair<long, float>> into)
        {
            into.Clear();
            RoadState st = comp?.roadState;
            if (st == null || layer == null) return 0;
            float min = Tuning.T.overlayTradeMinTraffic;
            int cap = Tuning.T.overlayTradePairs;
            candidates.Clear();
            foreach (KeyValuePair<long, float> kv in st.traffic)
                if (kv.Value >= min) candidates.Add(kv);
            candidates.Sort((x, y) => x.Value != y.Value ? y.Value.CompareTo(x.Value) : x.Key.CompareTo(y.Key));
            for (int i = 0; i < candidates.Count && into.Count < cap; i++)
            {
                Roads.SplitKey(candidates[i].Key, out int a, out int b);
                if (DrawnTown(a, layer, out _) && DrawnTown(b, layer, out _)) into.Add(candidates[i]);
            }
            candidates.Clear();
            return into.Count;
        }

        /// <summary>
        /// The ribbon's width band (0 thin, 1 middle, 2 wide) of the pair at <paramref name="rank"/> (0 the busiest) of <paramref name="count"/> pairs drawn: terciles of the
        /// ranking, the busiest third wide, so there are always three widths however far one pair outdoes the rest, and a lone pair is wide. Traffic decays by one factor for
        /// every pair, so the ranking (and the bands) change only when a pair comes, goes or overtakes another.
        /// </summary>
        internal static int WidthBand(int rank, int count) => rank * 3 < count ? 2 : rank * 3 < 2 * count ? 1 : 0;

        /// <summary>A hash of the first <paramref name="count"/> pairs of <paramref name="pairs"/> (busiest first) and their width bands.</summary>
        internal static int TradeKeyOf(List<KeyValuePair<long, float>> pairs, int count)
        {
            var h = new System.HashCode();
            h.Add((int)WorldOverlayMode.Trade);
            for (int i = 0; i < count; i++)
            {
                h.Add(pairs[i].Key);
                h.Add(WidthBand(i, count));
            }
            return h.ToHashCode();
        }

        /// <summary>A hash of the pairs that would be drawn now and their width bands.</summary>
        internal static int TradeKey(LivingWorldComponent comp, PlanetLayer layer) =>
            TradeKeyOf(keyPairs, PickPairs(comp, layer, keyPairs));

        /// <summary>Builds the ribbons from one pick, which also gives the content key the layer is built for (the pick is not run a second time for it).</summary>
        private void BuildTrade(LivingWorldComponent comp)
        {
            int n = PickPairs(comp, planetLayer, drawnPairs);
            builtKey = TradeKeyOf(drawnPairs, n);
            if (n == 0) return;
            float tile = Find.WorldGrid.AverageTileSize;
            Material mat = MarksMat;
            for (int i = 0; i < n; i++)
            {
                Roads.SplitKey(drawnPairs[i].Key, out int ida, out int idb);
                if (!(LivingWorldComponent.WorldObjectById(ida) is Settlement a) || !(LivingWorldComponent.WorldObjectById(idb) is Settlement b)) continue; // picked a moment ago: both are there
                int band = WidthBand(i, n);
                int k = BuildArc(planetLayer.GetTileCenter(a.Tile), planetLayer.GetTileCenter(b.Tile), TradeLift, TradeSegmentTiles, arcBuffer);
                PrintRibbon(GetSubMesh(mat), arcBuffer, k, RibbonHalfWidthTiles[band] * tile, TradeColour);
                Pairs++;
                WidthCounts[band]++;
            }
            drawnPairs.Clear();
            FinalizeSubMeshes();
        }

        /// <summary>
        /// A strip of quads along the points 0..<paramref name="segments"/> of <paramref name="pts"/> (already lifted off the surface): two vertices at each point, a
        /// <paramref name="halfWidth"/> to each side along the planet's surface, every one in <paramref name="colour"/>, wound to face outwards.
        /// </summary>
        internal static void PrintRibbon(LayerSubMesh sm, Vector3[] pts, int segments, float halfWidth, Color32 colour)
        {
            int first = sm.verts.Count;
            for (int i = 0; i <= segments; i++)
            {
                Vector3 normal = pts[i].normalized;
                Vector3 side = Vector3.Cross(normal, pts[System.Math.Min(i + 1, segments)] - pts[System.Math.Max(i - 1, 0)]);
                side = side.sqrMagnitude > 1e-12f ? side.normalized * halfWidth : Vector3.Cross(normal, Vector3.up).normalized * halfWidth;
                sm.verts.Add(pts[i] - side);
                sm.verts.Add(pts[i] + side);
                sm.colors.Add(colour);
                sm.colors.Add(colour);
            }
            // The strip's sides run the same way all along it: one test of the first quad decides the winding for all (a triangle faces the viewer when its cross product points at them).
            bool outwards = Vector3.Dot(Vector3.Cross(sm.verts[first + 1] - sm.verts[first], sm.verts[first + 3] - sm.verts[first]), pts[0].normalized + pts[segments].normalized) >= 0f;
            for (int i = 0; i < segments; i++)
            {
                int a = first + 2 * i, b = a + 1, c = a + 3, d = a + 2;
                if (outwards)
                {
                    sm.tris.Add(a);
                    sm.tris.Add(b);
                    sm.tris.Add(c);
                    sm.tris.Add(a);
                    sm.tris.Add(c);
                    sm.tris.Add(d);
                }
                else
                {
                    sm.tris.Add(c);
                    sm.tris.Add(b);
                    sm.tris.Add(a);
                    sm.tris.Add(d);
                    sm.tris.Add(c);
                    sm.tris.Add(a);
                }
            }
        }

        private void FinalizeSubMeshes()
        {
            foreach (LayerSubMesh sm in subMeshes)
            {
                if (sm.verts.Count == 0) continue;
                sm.FinalizeMesh(MeshParts.All);
                LastVerts += sm.verts.Count;
            }
        }

        // ---- Regeneration ----

        public override IEnumerable Regenerate()
        {
            foreach (object item in base.Regenerate()) yield return item;
            var sw = Stopwatch.StartNew();
            Battles = Pairs = 0;
            LastVerts = 0;
            System.Array.Clear(KindCounts, 0, KindCounts.Length);
            System.Array.Clear(WidthCounts, 0, WidthCounts.Length);
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            built = true;
            bool trade = comp != null && WorldOverlay.Mode == WorldOverlayMode.Trade && WorldOverlay.MarksMode;
            builtKey = trade ? 0 : ContentKey(comp, planetLayer, Today); // the ribbons' key comes out of their own pick
            try
            {
                if (comp != null && WorldOverlay.Mode == WorldOverlayMode.WarFronts && WorldOverlay.MarksMode) BuildBattles(comp);
            }
            catch (System.Exception e)
            {
                scanned.Clear();
                LWLog.ErrorOnce($"overlay battle markers failed: {e.GetBaseException().Message}", 0x4C57_1E71);
            }
            try
            {
                if (trade) BuildTrade(comp);
            }
            catch (System.Exception e)
            {
                drawnPairs.Clear();
                LWLog.ErrorOnce($"overlay trade ribbons failed: {e.GetBaseException().Message}", 0x4C57_1E72);
            }
            LastRegenMs = sw.Elapsed.TotalMilliseconds;
        }

        private void BuildBattles(LivingWorldComponent comp)
        {
            int now = SimClock.TicksAbs, window = WindowTicks;
            ScanBattles(comp.chronicle, planetLayer, now, scanned, out _, out _);
            float tile = Find.WorldGrid.AverageTileSize;
            Material mat = MarksMat;
            for (int i = 0; i < scanned.Count; i++)
            {
                ChronicleEntry e = scanned[i];
                TryKindOf(e, out BattleKind kind);
                LayerSubMesh sm = GetSubMesh(mat);
                Color32 c = MarkerColour(kind);
                c.a = (byte)Mathf.RoundToInt(MarkerAlpha(now - e.ticksAbs, window) * 255f);
                WorldRendererUtility.PrintQuadTangentialToPlanet(planetLayer.GetTileCenter(e.tile), MarkerSize(kind) * tile, MarkerLift, sm, rotation: 45f, printUVs: false);
                for (int k = 0; k < 4; k++) sm.colors.Add(c);
                Battles++;
                KindCounts[(int)kind]++;
            }
            scanned.Clear();
            FinalizeSubMeshes();
        }

        // ---- Lines ----

        /// <summary>
        /// The great circle from <paramref name="from"/> to <paramref name="to"/> as points in <paramref name="buf"/>, split into segments of about
        /// <paramref name="segTiles"/> tiles (as many as the buffer holds, at most), every point lifted off the surface by <paramref name="lift"/> along its
        /// normal as the game does its own world lines (a long straight chord would sink into the planet). The radius is interpolated between the ends.
        /// Returns the number of segments (at least 1, the points are 0..segments); the arc's length in tiles is that of the surface layer's tiles.
        /// </summary>
        internal static int BuildArc(Vector3 from, Vector3 to, float lift, float segTiles, Vector3[] buf)
        {
            Vector3 a = from.normalized, b = to.normalized;
            float angle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f));
            float tiles = Find.WorldGrid.Surface.ApproxDistanceInTiles(angle);
            int k = Mathf.Max(1, Mathf.CeilToInt(tiles / Mathf.Max(0.1f, segTiles)));
            k = Mathf.Min(k, buf.Length - 1);
            float ra = from.magnitude, rb = to.magnitude;
            // Opposite (or nearly opposite) ends: the great circle is undefined, and the slerp's sine is so small that float noise in a + b throws the middle of the
            // arc through the centre. Go by a fixed way point instead, a direction perpendicular to a (through the up axis, or any perpendicular at the poles): two quarter
            // turns, both well conditioned, deterministic, and the arc ends exactly at b.
            bool opposite = angle > 1f && Mathf.PI - angle < 0.02f;
            Vector3 way = opposite ? Vector3.Cross(a, Mathf.Abs(a.y) < 0.9f ? Vector3.up : Vector3.right).normalized : default;
            for (int i = 0; i <= k; i++)
            {
                float t = i / (float)k;
                Vector3 dir;
                if (opposite) dir = t <= 0.5f ? Slerp(a, way, t * 2f) : Slerp(way, b, t * 2f - 1f);
                else if (Mathf.Sin(angle) >= 1e-4f) dir = Slerp(a, b, t, angle);
                else dir = (a + (b - a) * t).normalized;
                buf[i] = dir * (Mathf.Lerp(ra, rb, t) + lift);
            }
            return k;
        }

        private static Vector3 Slerp(Vector3 a, Vector3 b, float t) =>
            Slerp(a, b, t, Mathf.Acos(Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f)));

        /// <summary>The unit direction a fraction <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/> (unit vectors <paramref name="angle"/> apart, not near 0 or pi) on their great circle.</summary>
        private static Vector3 Slerp(Vector3 a, Vector3 b, float t, float angle) =>
            ((a * Mathf.Sin((1f - t) * angle) + b * Mathf.Sin(t * angle)) / Mathf.Sin(angle)).normalized;

        /// <summary>A party of the kind on its way to a target the line is drawn to: outbound, spawned on this layer, with a goal on this layer.</summary>
        private bool WantsLine(WorldParty p, PartyKindDef kind, out Vector3 from, out Vector3 to)
        {
            from = to = default;
            if (p.kind != kind || p.phase != PartyPhase.Outbound || !p.Spawned || !p.Tile.Valid || p.Tile.Layer != planetLayer) return false;
            PlanetTile goal = p.GoalTile;
            if (!goal.Valid || goal.Layer != planetLayer) return false;
            from = p.DrawPos;
            to = planetLayer.GetTileCenter(goal) + planetLayer.Origin;
            return true;
        }

        /// <summary>
        /// The lines of this frame (the war bands', or the merchants' in the trade routes mode): each line is cut into segments (<see cref="BuildArc"/>) and the lines are taken in the parties' order until the
        /// budget of <paramref name="budget"/> segments is spent (a line that does not fit whole is not drawn, nor is any after it). Returns the segments
        /// (drawn, or only counted when <paramref name="draw"/> is false: the tests count without a frame).
        /// </summary>
        internal int DrawLines(bool draw, int budget) => DrawLines(draw, budget, LWPartyDefOf.LW_WarBand);

        /// <summary>The lines of the parties of one kind (war bands: red, merchants: gold and thinner), the same segments, lift and budget.</summary>
        internal int DrawLines(bool draw, int budget, PartyKindDef kind)
        {
            int used = 0;
            float segTiles = Tuning.T.overlayLineSegmentTiles;
            bool merchants = kind == LWPartyDefOf.LW_Merchants;
            float width = merchants ? MerchantLineWidth : WarLineWidth;
            Material mat = draw ? (merchants ? MerchantLineMat : WarLineMat) : null;
            List<WorldParty> all = WorldParty.All;
            for (int i = 0; i < all.Count; i++)
            {
                WorldParty p = all[i];
                if (!WantsLine(p, kind, out Vector3 from, out Vector3 to)) continue;
                int k = BuildArc(from, to, LineLift, segTiles, arcBuffer);
                if (used + k > budget) break;
                if (draw)
                    for (int j = 0; j < k; j++) GenDraw.DrawWorldLineBetween(arcBuffer[j], arcBuffer[j + 1], mat, width);
                used += k;
            }
            if (draw) LineSegmentsDrawn += used;
            LastLineSegments = used;
            return used;
        }

        /// <summary>Called by the world renderer every frame, so it returns before doing anything when hidden, and nothing escapes it (it would be logged every frame).</summary>
        public override void Render()
        {
            if (!Visible) return;
            base.Render();
            WorldOverlayMode mode = WorldOverlay.Mode;
            if (mode != WorldOverlayMode.WarFronts && mode != WorldOverlayMode.Trade) return;
            try
            {
                DrawLines(draw: true, Tuning.T.overlayLineMaxSegments, mode == WorldOverlayMode.Trade ? LWPartyDefOf.LW_Merchants : LWPartyDefOf.LW_WarBand);
            }
            catch (System.Exception e)
            {
                LWLog.ErrorOnce($"overlay lines failed: {e.GetBaseException().Message}", 0x4C57_1E70);
            }
        }
    }
}
