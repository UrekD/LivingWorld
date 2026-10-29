using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// A small box at the top centre of the world view, under whatever the game draws there (the dev-mode toolbar, the colonist bar): the mode's
    /// name, then a colour swatch (a drawn box) with a short label for each colour the mode can show. The swatches are the colours as the map shows
    /// them (the wash over terrain), not the opaque palette. Only modes that need explaining get one (not Off and not Borders), and only while the
    /// map really shows them. What is drawn is worked out when the mode, the good, the sim day or the language changes, never per frame.
    /// </summary>
    public static class OverlayLegend
    {
        private const float MinWidth = 260f, Height = 44f, Top = 6f, Pad = 10f, SwatchSize = 11f, Gap = 4f, Between = 10f, TitleHeight = 20f, RowHeight = 16f;

        // DebugWindowsOpener.DevToolStarterOnGUI: the dev-mode toolbar is centred at the top, 25 high, from y = 3.
        private const float DevRowTop = 3f, DevRowHeight = 25f, DevRowGap = 4f;
        // The land of the world map under the wash, for the swatches: a mid brown-green, the average of the terrain colours.
        private static readonly Color Terrain = new Color(0.45f, 0.40f, 0.32f);
        private static readonly Color SwatchOutline = new Color(1f, 1f, 1f, 0.45f);
        private static readonly Color DiamondOutline = new Color(0.85f, 0.85f, 0.85f, 0.9f);

        private static bool built;
        private static WorldOverlayMode cachedMode;
        private static CommodityDef cachedCommodity;
        private static int cachedDay, cachedExtra;
        private static LoadedLanguage cachedLanguage;
        private static string title;
        private static float width = MinWidth;
        private static readonly List<string> names = new List<string>();
        private static readonly List<Color> colours = new List<Color>();
        private static readonly List<Swatch> shapes = new List<Swatch>();

        /// <summary>What a swatch is drawn as (all boxes, no glyphs): the heatmap bands are squares, the war fronts legend tells its items apart by shape as well as colour.</summary>
        private enum Swatch { Square, Strip, Line, Diamond, Ribbon }
        private static readonly List<float> nameWidths = new List<float>();

        /// <summary>The legend shows for this mode: it needs one, this build draws it, and nothing stands in its way.</summary>
        internal static bool Shown(WorldOverlayMode mode) =>
            mode != WorldOverlayMode.Off && mode != WorldOverlayMode.Borders && WorldOverlay.IsImplemented(mode) && WorldOverlay.Blocker(mode) == OverlayBlock.None;

        /// <summary>
        /// The legend is drawn: it is shown for the mode, the surface is the selected layer, and what it explains is really drawn: for a heatmap the tint layer (the
        /// picture is ready); for the war fronts and the trade routes the markers, ribbons and lines, which need no territory.
        /// </summary>
        internal static bool Visible(WorldOverlayMode mode) =>
            Shown(mode) && PlanetLayer.Selected is PlanetLayer layer && layer.IsRootSurface && (mode == WorldOverlayMode.WarFronts || mode == WorldOverlayMode.Trade || (WorldOverlay.HeatMode && Territory.Ready));

        /// <summary>The war fronts legend's items: the key of the text, the colour as the map shows it and the shape (the band along a front, a marker of each kind, the war band line).</summary>
        private static readonly (string key, Color32 colour, Swatch shape)[] WarItems =
        {
            ("LW_Overlay_Legend_Wars", WarPaint.FrontColour, Swatch.Strip),
            ("LW_Overlay_Legend_Repelled", Marker(BattleKind.Repelled), Swatch.Diamond),
            ("LW_Overlay_Legend_Plundered", Marker(BattleKind.Plundered), Swatch.Diamond),
            ("LW_Overlay_Legend_Captured", Marker(BattleKind.Captured), Swatch.Diamond),
            ("LW_Overlay_Legend_Razed", Marker(BattleKind.Razed), Swatch.Diamond),
            ("LW_Overlay_Legend_WarBand", WorldDrawLayer_LWOverlayMarks.WarLineColour, Swatch.Line),
        };

        /// <summary>The trade routes legend's items: a ribbon (the map's width varies with the traffic) and the merchant line. With no traffic the first reads "no trade recorded yet".</summary>
        private static readonly (string key, Color32 colour, Swatch shape)[] TradeItems =
        {
            ("LW_Overlay_Legend_TradeRoute", WorldDrawLayer_LWOverlayMarks.TradeColour, Swatch.Ribbon),
            ("LW_Overlay_Legend_Merchants", WorldDrawLayer_LWOverlayMarks.MerchantLineColour, Swatch.Line),
        };

        private static Color32 Marker(BattleKind kind)
        {
            Color32 c = WorldDrawLayer_LWOverlayMarks.MarkerColour(kind);
            c.a = (byte)Mathf.RoundToInt(WorldDrawLayer_LWOverlayMarks.MarkerAlpha(0, 1) * 255f);
            return c;
        }

        /// <summary>The heat paint a mode lists, null when it lists nothing.</summary>
        private static HeatPaint PaintOf(WorldOverlayMode mode, CommodityDef commodity)
        {
            switch (mode)
            {
                case WorldOverlayMode.Prosperity: return ProsperityPaint.Instance;
                case WorldOverlayMode.Shortage: return ShortagePaint.For(commodity);
                default: return null;
            }
        }

        /// <summary>The bands the legend lists, from <paramref name="first"/> on: all six of the heatmaps, but the blue ones are left out for any good. Shared by what is drawn and the tests.</summary>
        internal static void Bands(WorldOverlayMode mode, CommodityDef commodity, out int first, out int count)
        {
            first = count = 0;
            HeatPaint paint = PaintOf(mode, commodity);
            if (paint == null) return;
            first = mode == WorldOverlayMode.Shortage ? ShortagePaint.FirstBucket(commodity == null) : 0;
            count = paint.BucketCount - first;
        }

        /// <summary>How many colour swatches the mode lists (tests).</summary>
        internal static int SwatchCount(WorldOverlayMode mode, CommodityDef commodity)
        {
            if (mode == WorldOverlayMode.WarFronts) return WarItems.Length;
            if (mode == WorldOverlayMode.Trade) return TradeItems.Length;
            Bands(mode, commodity, out _, out int count);
            return count;
        }

        /// <summary>
        /// The top of the box: clear of the dev-mode toolbar when that is showing, and under the colonist bar (its lowest row, the portraits and the name
        /// labels below them), which the world view draws in the same place for every player. A pure function of what the game laid out.
        /// </summary>
        internal static float TopFor(bool devMode, IReadOnlyList<Vector2> barLocs, Vector2 barSize, float barScale)
        {
            float top = devMode ? DevRowTop + DevRowHeight + DevRowGap : Top;
            if (barLocs != null && barLocs.Count > 0)
            {
                float lowest = 0f;
                for (int i = 0; i < barLocs.Count; i++) lowest = Mathf.Max(lowest, barLocs[i].y); // several rows with many colonists
                top = Mathf.Max(top, lowest + barSize.y + 18f * barScale + 6f); // the name label under the portrait (GenMapUI.DrawPawnLabel, a Tiny line)
            }
            return top;
        }

        /// <summary>Where the box is drawn now: centred, under the dev-mode toolbar and the colonist bar (WorldInterfaceOnGUI has just laid the bar out). The bar's cached locations allocate nothing.</summary>
        internal static Rect BoxRect()
        {
            ColonistBar bar = Find.ColonistBar;
            float top = TopFor(Prefs.DevMode, bar?.DrawLocs, bar?.Size ?? Vector2.zero, bar?.Scale ?? 1f);
            return new Rect((UI.screenWidth - width) / 2f, top, width, Height);
        }

        /// <summary>A band as the map shows it: its colour at the wash alpha over the land, not the opaque palette colour.</summary>
        internal static Color SwatchColour(Color32 fill) =>
            Color.Lerp(Terrain, new Color(fill.r / 255f, fill.g / 255f, fill.b / 255f, 1f), fill.a / 255f);

        internal static void Draw(WorldOverlayMode mode)
        {
            CommodityDef commodity = mode == WorldOverlayMode.Shortage ? WorldOverlay.ShortageCommodity : null;
            int day = SimClock.Now / GenDate.TicksPerDay;
            // The war count changes at the day's first hourly check, a few ticks after the day begins: it is part of what the text depends on. The trade
            // legend depends on whether a ribbon is drawn (1), none is but some pair has traffic (2), or there is no traffic at all (0).
            int extra = mode == WorldOverlayMode.WarFronts ? WorldOverlay.LastWarSetChanges : mode == WorldOverlayMode.Trade ? TradeState() : 0;
            if (!built || mode != cachedMode || commodity != cachedCommodity || day != cachedDay || extra != cachedExtra || !ReferenceEquals(cachedLanguage, LanguageDatabase.activeLanguage))
                Build(mode, commodity, day, extra);
            Rect box = BoxRect();
            Widgets.DrawBoxSolid(box, new Color(0.08f, 0.08f, 0.08f, 0.75f));
            Widgets.DrawBox(box);
            try
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(box.x, box.y + 2f, box.width, TitleHeight), title);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                float x = box.x + Pad, y = box.y + 2f + TitleHeight + 2f;
                for (int i = 0; i < names.Count; i++)
                {
                    var swatch = new Rect(x, y + (RowHeight - SwatchSize) / 2f, SwatchSize, SwatchSize);
                    // Without territory no front band is drawn on the map, and without a ribbon no ribbon is: the label stays, its swatch does not.
                    if ((shapes[i] != Swatch.Strip || Territory.Ready) && (shapes[i] != Swatch.Ribbon || cachedExtra == 1)) DrawSwatch(swatch, shapes[i], colours[i]);
                    x += SwatchSize + Gap;
                    Widgets.Label(new Rect(x, y, nameWidths[i], RowHeight), names[i]);
                    x += nameWidths[i] + Between;
                }
            }
            finally
            {
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
            }
        }

        /// <summary>The trade legend's state: 1 a ribbon is drawn, 2 none is but some pair has traffic (all under the minimum, or towns not drawn), 0 no traffic at all.</summary>
        private static int TradeState()
        {
            if (Find.WorldGrid?.Surface?.GetLayer<WorldDrawLayer_LWOverlayMarks>()?.Pairs > 0) return 1;
            return LivingWorldComponent.Instance?.roadState?.traffic.Count > 0 ? 2 : 0;
        }

        private static void DrawSwatch(Rect r, Swatch shape, Color colour)
        {
            switch (shape)
            {
                case Swatch.Line: // a thin bar across the middle
                    Widgets.DrawBoxSolid(new Rect(r.x, r.center.y - 1.5f, r.width, 3f), colour);
                    return;
                case Swatch.Ribbon: // a thick bar, over land so its wash shows as on the map
                    Widgets.DrawBoxSolid(new Rect(r.x, r.y + 2f, r.width, r.height - 4f), Terrain);
                    Widgets.DrawBoxSolid(new Rect(r.x, r.center.y - 2.5f, r.width, 5f), colour);
                    return;
                case Swatch.Strip: // land with a band along its lower edge
                    Widgets.DrawBoxSolid(new Rect(r.x, r.y + 2f, r.width, r.height - 4f), Terrain);
                    Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 6f, r.width, 4f), colour);
                    return;
                case Swatch.Diamond: // a light rim first, so a near-black marker (razed) shows on the dark box
                    DrawDiamond(r.ContractedBy(-1.5f), DiamondOutline);
                    DrawDiamond(r, colour);
                    return;
                default:
                    Widgets.DrawBoxSolid(r, colour);
                    GUI.color = SwatchOutline; // a near-black swatch must not vanish into the box
                    Widgets.DrawBox(r);
                    GUI.color = Color.white;
                    return;
            }
        }

        /// <summary>Rows that narrow towards the top and bottom (a box cannot be rotated here).</summary>
        private static void DrawDiamond(Rect r, Color colour)
        {
            for (int row = 0; row < 5; row++)
            {
                float w = r.width * (row == 2 ? 1f : row == 1 || row == 3 ? 0.64f : 0.27f), h = r.height / 5f;
                Widgets.DrawBoxSolid(new Rect(r.center.x - w / 2f, r.y + row * h, w, h + 0.5f), colour);
            }
        }

        private static void Build(WorldOverlayMode mode, CommodityDef commodity, int day, int extra)
        {
            names.Clear();
            colours.Clear();
            shapes.Clear();
            nameWidths.Clear();
            if (mode == WorldOverlayMode.WarFronts)
            {
                title = "LW_Overlay_Legend_WarTitle".Translate(Tuning.T.overlayBattleDays).Resolve();
                foreach ((string key, Color32 colour, Swatch shape) in WarItems) Add(key == "LW_Overlay_Legend_Wars" ? key.Translate(WorldOverlay.WarCount).Resolve() : key.Translate().Resolve(), colour, shape);
            }
            else if (mode == WorldOverlayMode.Trade)
            {
                title = WorldOverlay.ModeLabel(mode).CapitalizeFirst();
                foreach ((string key, Color32 colour, Swatch shape) in TradeItems)
                {
                    string text = key == "LW_Overlay_Legend_TradeRoute" && extra != 1 ? (extra == 2 ? "LW_Overlay_Legend_TradeQuiet" : "LW_Overlay_Legend_TradeNone") : key;
                    Add(text.Translate().Resolve(), colour, shape);
                }
            }
            else
            {
                title = WorldOverlay.ModeLabel(mode, commodity).CapitalizeFirst();
                HeatPaint paint = PaintOf(mode, commodity);
                Bands(mode, commodity, out int first, out int count);
                for (int i = first; i < first + count; i++) Add(paint.NameOf(i), paint.FillOf(i), Swatch.Square);
            }
            try
            {
                Text.Font = GameFont.Tiny;
                float row = Pad * 2f - Between;
                for (int i = 0; i < names.Count; i++)
                {
                    float w = Mathf.Ceil(Text.CalcSize(names[i]).x) + 4f;
                    nameWidths.Add(w);
                    row += SwatchSize + Gap + w + Between;
                }
                Text.Font = GameFont.Small;
                width = Mathf.Max(MinWidth, row, Mathf.Ceil(Text.CalcSize(title).x) + Pad * 2f);
            }
            finally
            {
                Text.Font = GameFont.Small;
            }
            cachedMode = mode;
            cachedCommodity = commodity;
            cachedDay = day;
            cachedExtra = extra;
            cachedLanguage = LanguageDatabase.activeLanguage;
            built = true;
        }

        private static void Add(string name, Color32 fill, Swatch shape)
        {
            names.Add(name);
            shapes.Add(shape);
            colours.Add(SwatchColour(fill)); // solid, but the colour the wash gives over land
        }
    }

    [HarmonyPatch(typeof(WorldInterface), nameof(WorldInterface.WorldInterfaceOnGUI))]
    public static class Patch_WorldInterface_OnGUI_Legend
    {
        public static void Postfix()
        {
            if (Current.ProgramState != ProgramState.Playing || !WorldRendererUtility.WorldSelected) return;
            WorldOverlayMode mode = WorldOverlay.Mode;
            if (!OverlayLegend.Visible(mode) || Find.UIRoot.screenshotMode.FiltersCurrentEvent || !Find.WorldCamera.gameObject.activeInHierarchy) return;
            try
            {
                OverlayLegend.Draw(mode);
            }
            catch (System.Exception e)
            {
                LWLog.ErrorOnce($"overlay legend failed: {e.GetBaseException().Message}", 0x4C57_1E6D);
            }
        }
    }
}
