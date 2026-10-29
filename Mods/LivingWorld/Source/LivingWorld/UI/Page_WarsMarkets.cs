using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Wars: who fights whom, why, since when, and who is winning; recent peace treaties below.</summary>
    public class Page_Wars : ChroniclePage
    {
        private Vector2 scroll;

        public override string Label => "LW_Tab_Wars".Translate();

        public override void Draw(Rect rect)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            var active = new List<War>();
            var ended = new List<War>();
            foreach (War w in comp.wars) (w.Active ? active : ended).Add(w);
            ended.SortByDescending(w => w.endTicks);
            if (ended.Count > 15) ended.RemoveRange(15, ended.Count - 15);
            const float rowH = 52f;
            float height = (active.Count + ended.Count) * rowH + 70f;
            var view = new Rect(0f, 0f, rect.width - 16f, height);
            Widgets.BeginScrollView(rect, ref scroll, view);
            float y = 0f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, view.width, 30f), active.Count == 0 ? "LW_Wars_None".Translate().Resolve() : CountLabel.Of("LW_Wars_Active", active.Count));
            Text.Font = GameFont.Small;
            y += 32f;
            foreach (War w in active) y = DrawWar(comp, w, view.width, y, rowH);
            if (ended.Count > 0)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0f, y + 6f, view.width, 30f), "LW_Wars_Ended".Translate());
                Text.Font = GameFont.Small;
                y += 38f;
                foreach (War w in ended) y = DrawWar(comp, w, view.width, y, rowH);
            }
            Widgets.EndScrollView();
        }

        private static float DrawWar(LivingWorldComponent comp, War w, float width, float y, float rowH)
        {
            var row = new Rect(0f, y, width, rowH - 4f);
            Widgets.DrawMenuSection(row);
            Rect inner = row.ContractedBy(6f);
            Faction a = comp.FactionById(w.factionA), b = comp.FactionById(w.factionB);
            GUI.color = a?.Color ?? Color.gray;
            Widgets.Label(new Rect(inner.x, inner.y, 260f, 22f), w.nameA);
            GUI.color = Color.white;
            Widgets.Label(new Rect(inner.x + 262f, inner.y, 30f, 22f), "vs");
            GUI.color = b?.Color ?? Color.gray;
            Widgets.Label(new Rect(inner.x + 292f, inner.y, 260f, 22f), w.nameB);
            GUI.color = new Color(0.75f, 0.75f, 0.75f);
            string since = (SimClock.Now - w.startTicks).ToStringTicksToPeriod(allowSeconds: false, shortForm: true);
            string state = w.Active ? "LW_Wars_Since".Translate(since).Resolve() : "LW_Wars_Lasted".Translate((w.endTicks - w.startTicks).ToStringTicksToPeriod(false, true)).Resolve();
            Widgets.Label(new Rect(inner.x, inner.y + 20f, 560f, 22f), $"{w.cause.CapitalizeFirst()} · {state} · " + CountLabel.Of("LW_Wars_Battles", w.battles));
            GUI.color = Color.white;
            // Score bar: who is winning.
            var bar = new Rect(inner.xMax - 220f, inner.y + 8f, 220f, 18f);
            Widgets.DrawBoxSolid(bar, new Color(0.15f, 0.15f, 0.15f));
            float share = Mathf.Clamp01(0.5f + w.score / 4000f);
            Widgets.DrawBoxSolid(bar.LeftPart(share), (a?.Color ?? Color.gray) * 0.8f);
            Widgets.DrawBoxSolid(bar.RightPart(1f - share), (b?.Color ?? Color.gray) * 0.8f);
            TooltipHandler.TipRegion(bar, "LW_Wars_ScoreTip".Translate(w.nameA, w.nameB, Mathf.RoundToInt(w.score)));
            return y + rowH;
        }
    }

    /// <summary>
    /// Markets near the colony: what each settlement pays for each commodity (the Prices view), and where goods fetch the most and which trips
    /// are worth making (the Routes view, <see cref="TradePlanner"/>).
    /// </summary>
    public class Page_Markets : ChroniclePage
    {
        internal enum MarketsView : byte { Prices, Routes }

        /// <summary>The view showing (it stays as the player left it; the UI tour switches it).</summary>
        internal static MarketsView View = MarketsView.Prices;

        private static readonly MarketsView[] Views = { MarketsView.Prices, MarketsView.Routes };

        private Vector2 scroll;
        private CommodityDef goodFilter;
        private bool onlyHave;
        private readonly List<CompSettlementLife> rows = new List<CompSettlementLife>();

        public override string Label => "LW_Tab_Markets".Translate();

        public override void OnOpen()
        {
            scroll = Vector2.zero;
            TradePlanner.Invalidate();
        }

        public override void Draw(Rect rect)
        {
            float x = rect.x;
            foreach (MarketsView v in Views)
            {
                var button = new Rect(x, rect.y, 120f, 26f);
                if (Widgets.ButtonText(button, ("LW_Markets_View" + v).Translate()) && View != v)
                {
                    View = v;
                    scroll = Vector2.zero;
                }
                if (View == v) Widgets.DrawHighlightSelected(button);
                x += 126f;
            }
            rect.yMin += 30f;
            if (View == MarketsView.Routes) DrawRoutes(rect);
            else DrawPrices(rect);
        }

        private void DrawPrices(Rect rect)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Map home = Find.AnyPlayerHomeMap;
            rows.Clear();
            foreach (CompSettlementLife s in comp.activeSettlements)
                if (!s.Settlement.Faction.HostileTo(Faction.OfPlayer)) rows.Add(s);
            if (home != null) rows.SortBy(s => Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile));
            if (rows.Count > 30) rows.RemoveRange(30, rows.Count - 30);
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(rect.TopPartPixels(20f), "LW_Markets_Help".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            float nameW = 200f, distW = 50f;
            int n = Commodities.Count;
            float colW = Mathf.Max(56f, (rect.width - nameW - distW - 16f) / Mathf.Max(1, n));
            float y0 = rect.y + 22f;
            Text.Anchor = TextAnchor.MiddleCenter;
            for (int i = 0; i < n; i++)
            {
                CommodityDef c = Commodities.All[i];
                var h = new Rect(rect.x + nameW + distW + i * colW, y0, colW, 24f);
                GUI.color = c.color;
                Widgets.Label(h, c.LabelCap.Truncate(colW));
                TooltipHandler.TipRegion(h, c.description);
            }
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            var list = new Rect(rect.x, y0 + 26f, rect.width, rect.yMax - y0 - 26f);
            const float rowH = 24f;
            var view = new Rect(0f, 0f, list.width - 16f, rows.Count * rowH);
            Widgets.BeginScrollView(list, ref scroll, view);
            for (int r = 0; r < rows.Count; r++)
            {
                CompSettlementLife s = rows[r];
                var row = new Rect(0f, r * rowH, view.width, rowH);
                if (r % 2 == 0) Widgets.DrawLightHighlight(row);
                GUI.color = s.Settlement.Faction.Color;
                Widgets.Label(new Rect(0f, row.y, nameW, rowH), s.Settlement.Label.Truncate(nameW - 4f));
                GUI.color = Color.white;
                Widgets.Label(new Rect(nameW, row.y, distW, rowH), home == null ? "-" : Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile).ToString("F0"));
                Text.Anchor = TextAnchor.MiddleCenter;
                for (int i = 0; i < n; i++)
                {
                    float f = s.PriceFactor(Commodities.All[i]);
                    var cell = new Rect(nameW + distW + i * colW, row.y, colW, rowH);
                    // Red: they pay a lot (sell to them); green: cheap there (buy from them).
                    GUI.color = f > 1.15f ? Color.Lerp(Color.white, new Color(1f, 0.45f, 0.35f), Mathf.Clamp01((f - 1f) / 1.2f))
                        : f < 0.87f ? Color.Lerp(Color.white, new Color(0.45f, 1f, 0.45f), Mathf.Clamp01((1f - f) / 0.5f)) : Color.white;
                    Widgets.Label(cell, "×" + f.ToString("F1"));
                }
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                if (Widgets.ButtonInvisible(row)) CameraJumper.TryJumpAndSelect(s.Settlement);
            }
            Widgets.EndScrollView();
        }

        private void DrawRoutes(Rect rect)
        {
            TradePlanner.ValidateShown();
            TradePlanner.RebuildIfStale(Neighbourhood.Home, goodFilter, onlyHave);
            TradePlanner.StepPaths();
            float x = rect.x;
            var goodButton = new Rect(x, rect.y, 220f, 26f);
            if (Widgets.ButtonText(goodButton, "LW_Routes_Good".Translate((goodFilter?.LabelCap ?? "LW_Routes_AllGoods".Translate()).Resolve()))) Find.WindowStack.Add(new FloatMenu(GoodOptions()));
            x += 228f;
            var haveBox = new Rect(x, rect.y, 190f, 26f);
            Widgets.CheckboxLabeled(haveBox, "LW_Routes_OnlyHave".Translate(), ref onlyHave);
            TooltipHandler.TipRegion(haveBox, "LW_Routes_OnlyHaveDesc".Translate());
            x += 198f;
            if (Widgets.ButtonText(new Rect(x, rect.y, 100f, 26f), "LW_Routes_Refresh".Translate())) TradePlanner.Invalidate();
            x += 108f;
            var mapButton = new Rect(x, rect.y, 150f, 26f);
            string cannot = WorldOverlay.CannotUse(WorldOverlayMode.Shortage);
            if (Widgets.ButtonText(mapButton, "LW_Routes_ShowOnMap".Translate(), active: cannot == null))
            {
                WorldOverlay.SetMode(WorldOverlayMode.Shortage, goodFilter);
                CameraJumper.TryShowWorld();
            }
            TooltipHandler.TipRegion(mapButton, cannot ?? "LW_Routes_ShowOnMapDesc".Translate().Resolve());
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x, rect.y + 30f, rect.width, 32f), "LW_Routes_Help".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Rect list = rect;
            list.yMin += 64f;
            string unavailable = TradePlanner.Unavailable;
            if (unavailable != null)
            {
                Widgets.Label(new Rect(list.x, list.y, list.width, 30f), unavailable);
                return;
            }
            IReadOnlyList<SellRow> sell = TradePlanner.Sell;
            IReadOnlyList<RouteRow> routes = TradePlanner.Routes;
            const float rowH = 26f, headH = 50f;
            float height = headH * 2f + (Mathf.Max(1, sell.Count) + Mathf.Max(1, routes.Count)) * rowH;
            var view = new Rect(0f, 0f, list.width - 16f, height);
            Widgets.BeginScrollView(list, ref scroll, view);
            float y = DrawHeader(view.width, 0f, "LW_Routes_SellHeader", "LW_Routes_SellHelp");
            if (sell.Count == 0) y = DrawNote(view.width, y, TradePlanner.Searching ? "LW_Routes_Searching" : "LW_Routes_NoSell");
            for (int i = 0; i < sell.Count; i++) y = DrawSellRow(sell[i], i, view.width, y);
            y = DrawHeader(view.width, y, "LW_Routes_RoutesHeader", "LW_Routes_RoutesHelp");
            if (routes.Count == 0) y = DrawNote(view.width, y, TradePlanner.Searching ? "LW_Routes_Searching" : "LW_Routes_NoRoutes");
            for (int i = 0; i < routes.Count; i++) y = DrawRouteRow(routes[i], i, view.width, y);
            Widgets.EndScrollView();
        }

        private List<FloatMenuOption> GoodOptions()
        {
            var options = new List<FloatMenuOption> { new FloatMenuOption("LW_Routes_AllGoods".Translate(), () => goodFilter = null) };
            foreach (CommodityDef c in Commodities.All)
            {
                // A good no trader can stand for (animals) never has a row.
                if (TradePlanner.RepsOf(c).Count == 0) continue;
                CommodityDef good = c;
                options.Add(new FloatMenuOption(good.LabelCap, () => goodFilter = good));
            }
            return options;
        }

        private static float DrawHeader(float width, float y, string titleKey, string helpKey)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, width, 30f), titleKey.Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(0f, y + 28f, width, 20f), helpKey.Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            return y + 50f;
        }

        private static float DrawNote(float width, float y, string key)
        {
            GUI.color = Color.gray;
            Widgets.Label(new Rect(6f, y, width - 6f, 26f), key.Translate());
            GUI.color = Color.white;
            return y + 26f;
        }

        private static float DrawSellRow(SellRow r, int index, float width, float y)
        {
            Settlement s = r.town.Settlement;
            string timeKey = r.exact ? "LW_Routes_Days" : TradePlanner.HomeLegFailed(r.town) ? "LW_Routes_DaysApprox" : "LW_Routes_DaysEstimating";
            string time = timeKey.Translate(Days(r.ticks)).Resolve();
            string text = "LW_Routes_SellRow".Translate(s.Label.Named("TOWN"), r.factor.ToString("F2").Named("FACTOR"), r.good.label.Named("GOOD"), time.Named("TIME"),
                (r.road ? "LW_Routes_ByRoad".Translate().Resolve() : "").Named("ROAD"), (r.note.NullOrEmpty() ? "" : " " + r.note).Named("NOTE"),
                (r.have < 0 ? "LW_Routes_NoneCounted".Translate().Resolve() : r.have.ToString()).Named("HAVE")).Resolve();
            return DrawRow(new Rect(0f, y, width, 26f), index, s, text, "LW_Routes_SellTip".Translate().Resolve());
        }

        private static float DrawRouteRow(RouteRow r, int index, float width, float y)
        {
            string leg1Key = TradePlanner.HomeLegExact(r.buyAt) ? "LW_Routes_Leg" : TradePlanner.HomeLegFailed(r.buyAt) ? "LW_Routes_LegApprox" : "LW_Routes_LegEstimating";
            string leg1 = leg1Key.Translate(Days(r.ticksToBuy)).Resolve();
            string leg2 = "LW_Routes_LegApprox".Translate(Days(r.ticksOnward)).Resolve();
            string text = "LW_Routes_RouteRow".Translate(r.rep.LabelCap.Named("REP"), r.buyAt.Settlement.Label.Named("BUY"), r.sellAt.Settlement.Label.Named("SELL"),
                Mathf.RoundToInt((r.estProfit - 1f) * 100f).Named("PROFIT"), r.negotiator.Named("NEGOTIATOR"), leg1.Named("LEG1"), leg2.Named("LEG2")).Resolve();
            return DrawRow(new Rect(0f, y, width, 26f), index, r.buyAt.Settlement, text, "LW_Routes_RouteTip".Translate().Resolve());
        }

        private static float DrawRow(Rect row, int index, Settlement jumpTo, string text, string tip)
        {
            if (index % 2 == 0) Widgets.DrawLightHighlight(row);
            Widgets.DrawBoxSolid(new Rect(row.x + 6f, row.y + 8f, 10f, 10f), jumpTo.Faction.Color);
            Text.Anchor = TextAnchor.MiddleLeft;
            float textWidth = row.width - 26f;
            if (textWidth != truncatedFor || truncated.Count > 200)
            {
                truncated.Clear();
                truncatedFor = textWidth;
            }
            Widgets.Label(new Rect(row.x + 22f, row.y, row.width - 22f, row.height), text.Truncate(textWidth, truncated));
            Text.Anchor = TextAnchor.UpperLeft;
            if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            // A long row is cut short: the whole of it is in the tooltip.
            TooltipHandler.TipRegion(row, text + "\n\n" + tip);
            if (Widgets.ButtonInvisible(row)) CameraJumper.TryJumpAndSelect(jumpTo);
            return row.yMax;
        }

        /// <summary>Rows cut to the width they were drawn at (cutting measures the text once for each letter taken off, so the answers are kept).</summary>
        private static readonly Dictionary<string, string> truncated = new Dictionary<string, string>();
        private static float truncatedFor = -1f;

        private static string Days(int ticks) => (ticks / (float)GenDate.TicksPerDay).ToString("F1");
    }
}
