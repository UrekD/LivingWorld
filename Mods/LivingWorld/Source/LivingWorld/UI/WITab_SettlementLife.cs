using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>World inspect tab for NPC settlements: their people, market, notable people and history.</summary>
    public class WITab_SettlementLife : WITab
    {
        private Vector2 scroll;
        private float lastHeight = 600f;

        public WITab_SettlementLife()
        {
            size = new Vector2(520f, 560f);
            labelKey = "LW_Tab_SettlementLife";
        }

        private CompSettlementLife Life => (SelObject as Settlement)?.GetComponent<CompSettlementLife>();

        public override bool IsVisible => Life != null && Life.Active;

        protected override void FillTab()
        {
            CompSettlementLife s = Life;
            if (s == null) return;
            Rect outer = new Rect(0f, 0f, size.x, size.y).ContractedBy(10f);
            outer.yMin += 14f;
            var view = new Rect(0f, 0f, outer.width - 16f, lastHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            Settlement st = s.Settlement;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, view.width, 30f), st.LabelCap);
            y += 30f;
            Text.Font = GameFont.Small;
            Line(ref y, view.width, "LW_Inspect_Settlement".Translate(s.Tier.LabelCap, Mathf.RoundToInt(s.population), CompSettlementLife.ProsperityLabel(s.prosperity)));
            Line(ref y, view.width, "LW_Tab_Garrison".Translate(Mathf.RoundToInt(s.garrison), s.unrest.ToStringPercent()));
            if (s.specialty != null) Line(ref y, view.width, "LW_Tab_KnownFor".Translate(s.specialty.specialtyLabel));
            if (s.growthState != GrowthState.Unknown) Line(ref y, view.width, ("LW_Tab_Growth_" + s.growthState).Translate()); // known after the first day
            float season = s.SeasonFactor(LWDefOf.LW_Food, s.TwelfthNow);
            float eat = s.DailyConsumption(LWDefOf.LW_Food);
            int reserve = eat <= 0f ? 0 : Mathf.RoundToInt(s.Stock(LWDefOf.LW_Food) / eat);
            if (Mathf.Abs(season - 1f) >= 0.15f)
                Line(ref y, view.width, (season > 1f ? "LW_Tab_Harvest" : "LW_Tab_LeanSeason").Translate(season.ToString("F2"), reserve));
            else Line(ref y, view.width, "LW_Tab_FoodReserve".Translate(reserve));
            if (s.HoldsFairs) Line(ref y, view.width, "LW_Tab_YearlyFair".Translate(s.FairTwelfth.GetQuadrum().Label()));
            if (Mathf.Abs(s.fame) > 0.05f) Line(ref y, view.width, "LW_Market_Fame".Translate(s.fame.ToStringPercent()));
            AidRequest aid = AidRequests.ActiveFor(st.ID);
            if (aid != null) Line(ref y, view.width, "LW_Inspect_Aid".Translate(aid.commodity.label, Mathf.RoundToInt(aid.wanted - aid.delivered), (aid.deadlineTicks - Find.TickManager.TicksGame).ToStringTicksToPeriod()));
            y += 8f;
            // Market: stock against what the town wants, and the price it pays.
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(0f, y, view.width, 18f), "LW_Tab_MarketHeader".Translate());
            GUI.color = Color.white;
            y += 18f;
            foreach (CommodityDef c in Commodities.All)
            {
                float target = s.TargetStock(c);
                float fill = target <= 0f ? 1f : Mathf.Clamp01(s.Stock(c) / (target * 2f));
                float f = s.PriceFactor(c);
                Widgets.Label(new Rect(0f, y, 110f, 18f), c.LabelCap);
                var bar = new Rect(115f, y + 3f, 200f, 12f);
                Widgets.DrawBoxSolid(bar, new Color(0.15f, 0.15f, 0.15f));
                Widgets.DrawBoxSolid(bar.LeftPart(fill), c.color * 0.8f);
                Widgets.DrawBoxSolid(new Rect(bar.x + bar.width * 0.5f, bar.y - 2f, 1f, bar.height + 4f), Color.white * 0.6f); // target
                GUI.color = f > 1.15f ? new Color(1f, 0.5f, 0.4f) : f < 0.87f ? new Color(0.5f, 1f, 0.5f) : Color.white;
                Widgets.Label(new Rect(325f, y, 80f, 18f), "×" + f.ToString("F2"));
                GUI.color = Color.white;
                y += 18f;
            }
            Text.Font = GameFont.Small;
            y += 8f;
            // Notable people from here.
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            foreach (Figure fig in comp.figures)
            {
                if (fig.homeId != st.ID) continue;
                Line(ref y, view.width, $"{fig.name}, {fig.RoleLabel} ({("LW_People_Status_" + fig.status).Translate()})");
                if (fig.FeudOpen && fig.Alive)
                {
                    GUI.color = ColorLibrary.RedReadable;
                    Line(ref y, view.width, "   " + "LW_Inspect_Sworn".Translate(fig.nemesisName, fig.avenges ?? "?"));
                    GUI.color = Color.white;
                }
            }
            y += 4f;
            // Recent history of the town.
            Text.Font = GameFont.Tiny;
            int shown = 0;
            for (int i = comp.chronicle.entries.Count - 1; i >= 0 && shown < 12; i--)
            {
                ChronicleEntry e = comp.chronicle.entries[i];
                if (e.worldObjectId != st.ID) continue;
                shown++;
                string text = $"{e.DateString}: {e.text}";
                float h = Text.CalcHeight(text, view.width);
                Widgets.Label(new Rect(0f, y, view.width, h), text);
                y += h + 2f;
            }
            Text.Font = GameFont.Small;
            lastHeight = y + 10f;
            Widgets.EndScrollView();
        }

        private static void Line(ref float y, float width, string text)
        {
            float h = Text.CalcHeight(text, width);
            Widgets.Label(new Rect(0f, y, width, h), text);
            y += h;
        }
    }
}
