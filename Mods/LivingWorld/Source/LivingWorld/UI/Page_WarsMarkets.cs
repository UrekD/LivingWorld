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
            Widgets.Label(new Rect(0f, y, view.width, 30f), active.Count == 0 ? "LW_Wars_None".Translate() : "LW_Wars_Active".Translate(active.Count));
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
            Widgets.Label(new Rect(inner.x, inner.y + 20f, 560f, 22f), $"{w.cause.CapitalizeFirst()} · {state} · " + "LW_Wars_Battles".Translate(w.battles));
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

    /// <summary>Markets near the colony: what each settlement pays for each commodity (trade opportunities).</summary>
    public class Page_Markets : ChroniclePage
    {
        private Vector2 scroll;
        private readonly List<CompSettlementLife> rows = new List<CompSettlementLife>();

        public override string Label => "LW_Tab_Markets".Translate();

        public override void Draw(Rect rect)
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
    }
}
