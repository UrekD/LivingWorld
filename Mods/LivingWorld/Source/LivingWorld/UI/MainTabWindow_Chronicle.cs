using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>One page of the chronicle window.</summary>
    public abstract class ChroniclePage
    {
        public abstract string Label { get; }
        public abstract void Draw(Rect rect);
        public virtual void OnOpen() { }
    }

    /// <summary>The "Chronicle" main tab: the living world at a glance (news, factions, settlements).</summary>
    public class MainTabWindow_Chronicle : MainTabWindow
    {
        private readonly List<ChroniclePage> pages = new List<ChroniclePage> { new Page_News(), new Page_Factions(), new Page_Relations(), new Page_Wars(), new Page_Settlements(), new Page_Markets(), new Page_People(), new Page_Legends() };
        private int current;

        public override Vector2 RequestedTabSize => new Vector2(1010f, 684f);

        public void SelectPage(int index)
        {
            current = Mathf.Clamp(index, 0, pages.Count - 1);
            pages[current].OnOpen();
        }

        public override void PreOpen()
        {
            base.PreOpen();
            LivingWorldComponent.Instance?.RebuildIndexes(seedMissing: false);
            pages[current].OnOpen();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (LivingWorldComponent.Instance == null) return;
            var tabs = new List<TabRecord>();
            for (int i = 0; i < pages.Count; i++)
            {
                int idx = i;
                tabs.Add(new TabRecord(pages[i].Label, () =>
                {
                    current = idx;
                    pages[idx].OnOpen();
                }, current == i));
            }
            Rect body = inRect;
            body.yMin += 32f;
            TabDrawer.DrawTabs(body, tabs);
            pages[current].Draw(body.ContractedBy(6f));
        }
    }

    public class Page_News : ChroniclePage
    {
        private enum Filter { All, Notable, Player }

        private Vector2 scroll;
        private Filter filter = Filter.Notable;
        private readonly List<ChronicleEntry> shown = new List<ChronicleEntry>();
        private int builtCount = -1;
        private Filter builtFilter;

        public override string Label => "LW_Tab_News".Translate();

        public override void Draw(Rect rect)
        {
            ChronicleLog log = LivingWorldComponent.Instance.chronicle;
            Rect top = rect.TopPartPixels(30f);
            if (Widgets.ButtonText(top.LeftPartPixels(220f), ("LW_NewsFilter_" + filter).Translate()))
            {
                filter = (Filter)(((int)filter + 1) % 3);
                builtCount = -1;
            }
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(top.RightPartPixels(300f), CountLabel.Of("LW_NewsCount", log.Count));
            Text.Anchor = TextAnchor.UpperLeft;
            if (builtCount != log.Count || builtFilter != filter) Rebuild(log);
            Rect list = rect;
            list.yMin += 36f;
            const float rowH = 46f;
            var view = new Rect(0f, 0f, list.width - 16f, shown.Count * rowH);
            Widgets.BeginScrollView(list, ref scroll, view);
            int first = Mathf.Max(0, (int)(scroll.y / rowH) - 1);
            int last = Mathf.Min(shown.Count, first + (int)(list.height / rowH) + 3);
            for (int i = first; i < last; i++)
            {
                ChronicleEntry e = shown[i];
                var row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);
                if (e.def.importance == ChronicleImportance.Major) Widgets.DrawBoxSolid(row.LeftPartPixels(4f), new Color(0.9f, 0.6f, 0.2f));
                else if (e.involvesPlayer) Widgets.DrawBoxSolid(row.LeftPartPixels(4f), new Color(0.3f, 0.7f, 1f));
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(row.x + 10f, row.y, 220f, 18f), e.DateString);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(row.x + 10f, row.y + 16f, row.width - 20f, rowH - 16f), e.text.Truncate(row.width - 20f));
                if (Mouse.IsOver(row))
                {
                    Widgets.DrawHighlight(row);
                    TooltipHandler.TipRegion(row, e.text);
                }
                if (Widgets.ButtonInvisible(row) && e.Target.IsValid) CameraJumper.TryJumpAndSelect(e.Target);
            }
            Widgets.EndScrollView();
        }

        private void Rebuild(ChronicleLog log)
        {
            shown.Clear();
            for (int i = log.entries.Count - 1; i >= 0; i--)
            {
                ChronicleEntry e = log.entries[i];
                if (filter == Filter.Notable && e.def.importance == ChronicleImportance.Minor && !e.involvesPlayer) continue;
                if (filter == Filter.Player && !e.involvesPlayer) continue;
                shown.Add(e);
            }
            builtCount = log.Count;
            builtFilter = filter;
        }
    }

    public class Page_Factions : ChroniclePage
    {
        private Vector2 scroll;
        private readonly List<Faction> bordering = new List<Faction>();

        public override string Label => "LW_Tab_Factions".Translate();

        public override void Draw(Rect rect)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            var states = new List<FactionState>();
            foreach (FactionState fs in comp.factionStates)
                if (fs.faction != null && !fs.faction.defeated) states.Add(fs);
            states.SortByDescending(fs => fs.strength);
            const float rowH = 96f, nameW = 360f, statsW = 190f;
            var view = new Rect(0f, 0f, rect.width - 16f, states.Count * rowH);
            Widgets.BeginScrollView(rect, ref scroll, view);
            for (int i = 0; i < states.Count; i++)
            {
                FactionState fs = states[i];
                Faction f = fs.faction;
                var row = new Rect(0f, i * rowH, view.width, rowH - 4f);
                Widgets.DrawMenuSection(row);
                Rect inner = row.ContractedBy(6f);
                var icon = new Rect(inner.x, inner.y, 42f, 42f);
                GUI.color = f.Color;
                if (f.def.FactionIcon != null) GUI.DrawTexture(icon, f.def.FactionIcon);
                GUI.color = Color.white;
                float x = icon.xMax + 8f;
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(x, inner.y - 2f, nameW, 30f), f.Name);
                Text.Font = GameFont.Small;
                var traits = new List<string>();
                foreach (FactionTraitDef t in fs.traits) traits.Add(t.label);
                GUI.color = new Color(0.8f, 0.8f, 0.8f);
                Widgets.Label(new Rect(x, inner.y + 26f, nameW, 22f), $"{KnowHow.Label(fs)} · {string.Join(", ", traits)}");
                if (Rulers.Line(comp, fs) is string ruler)
                {
                    // Two lines for the ruler (name and temperament, then the goal); the full text in the tooltip.
                    var rulerRect = new Rect(x, inner.y + 46f, nameW, 40f);
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(rulerRect, ruler);
                    Text.Font = GameFont.Small;
                    TooltipHandler.TipRegion(rulerRect, ruler);
                }
                GUI.color = Color.white;
                float cx = x + nameW + 10f;
                Widgets.Label(new Rect(cx, inner.y, statsW, 22f), "LW_Faction_Settlements".Translate(CountLabel.Of("LW_Count_Settlements", fs.settlements.Count), CountLabel.Of("LW_Count_People", Mathf.RoundToInt(fs.population))));
                Widgets.Label(new Rect(cx, inner.y + 20f, statsW, 22f), "LW_Faction_Strength".Translate(Mathf.RoundToInt(fs.strength)));
                Widgets.Label(new Rect(cx, inner.y + 40f, statsW, 22f), "LW_Faction_Treasury".Translate(Mathf.RoundToInt(fs.treasury)));
                if (Territory.Ready)
                {
                    // Whose land touches this faction's (the tooltip has the length of each border).
                    Territory.Bordering(f, bordering);
                    var names = new List<string>();
                    var detail = new List<string>();
                    foreach (Faction o in bordering)
                    {
                        names.Add(o.Name);
                        detail.Add($"{o.Name}: {Territory.SharedBorder(f, o)}");
                    }
                    string line = names.Count == 0 ? "LW_Faction_BordersNone".Translate().Resolve() : "LW_Faction_Borders".Translate(string.Join(", ", names)).Resolve();
                    var borderRect = new Rect(cx, inner.y + 60f, statsW, 22f);
                    Widgets.Label(borderRect, line.Truncate(statsW));
                    if (names.Count > 0) TooltipHandler.TipRegion(borderRect, string.Join("\n", detail));
                }
                float rx = cx + statsW + 10f;
                bool button = PlayerDiplomacy.Enabled && !f.def.permanentEnemy && f.HasGoodwill;
                var enemies = new List<string>();
                foreach (FactionState o in states)
                    if (o != fs && f.HostileTo(o.faction)) enemies.Add(o.faction.Name);
                string rel = f.def.permanentEnemy ? "LW_Faction_HostileToAll".Translate().Resolve() : enemies.Count == 0 ? "LW_Faction_AtPeace".Translate().Resolve() : "LW_Faction_HostileTo".Translate(string.Join(", ", enemies)).Resolve();
                // The whole column for the text; the Diplomacy button below it.
                Rect relRect = new Rect(rx, inner.y, inner.xMax - rx, inner.height - (button ? 28f : 0f));
                Text.Font = GameFont.Tiny;
                string mood = "LW_Faction_PlayerRelation".Translate(f.PlayerRelationKind.GetLabelCap(), f.PlayerGoodwill.ToStringWithSign());
                if (fs.warWeariness >= 0.4f) mood += " · " + "LW_Faction_WarWeary".Translate(fs.warWeariness.ToStringPercent());
                Grievance worst = null;
                foreach (Grievance g in fs.grievances)
                    if (g.otherFactionId == Faction.OfPlayer.loadID && g.amount >= 5f && (worst == null || g.amount > worst.amount)) worst = g;
                if (worst != null) mood += "\n" + "LW_Faction_GrudgeVsYou".Translate(worst.reason);
                if (Tributaries.FactionLine(f) is string freeTown) mood += "\n" + freeTown;
                if (PlayerDiplomacy.EnvoyTo(f) is WorldParty envoy) mood += "\n" + PlayerDiplomacy.EnvoyStatus(envoy);
                string relText = rel + "\n" + mood;
                Widgets.Label(relRect, relText);
                TooltipHandler.TipRegion(relRect, relText);
                Text.Font = GameFont.Small;
                if (button && Widgets.ButtonText(new Rect(inner.xMax - 120f, inner.yMax - 26f, 120f, 26f), "LW_Dipl_Button".Translate()))
                    Find.WindowStack.Add(new Dialog_Diplomacy(f));
            }
            Widgets.EndScrollView();
        }
    }

    public class Page_Settlements : ChroniclePage
    {
        private enum SortBy { Name, Faction, Population, Prosperity, Garrison, Distance }

        private Vector2 scroll;
        private SortBy sort = SortBy.Population;
        private bool descending = true;
        private readonly List<CompSettlementLife> rows = new List<CompSettlementLife>();

        public override string Label => "LW_Tab_Settlements".Translate();

        public override void OnOpen() => scroll = Vector2.zero;

        public override void Draw(Rect rect)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            rows.Clear();
            rows.AddRange(comp.activeSettlements);
            Map home = Find.AnyPlayerHomeMap;
            float Dist(CompSettlementLife s) => home == null ? 0f : Find.WorldGrid.ApproxDistanceInTiles(home.Tile, s.Settlement.Tile);
            switch (sort)
            {
                case SortBy.Name: rows.SortBy(s => s.Settlement.Label); break;
                case SortBy.Faction: rows.SortBy(s => s.Settlement.Faction.Name); break;
                case SortBy.Population: rows.SortBy(s => s.population); break;
                case SortBy.Prosperity: rows.SortBy(s => s.prosperity); break;
                case SortBy.Garrison: rows.SortBy(s => s.garrison); break;
                case SortBy.Distance: rows.SortBy(Dist); break;
            }
            if (descending) rows.Reverse();
            float[] cols = { 190f, 190f, 100f, 90f, 110f, 80f, 70f };
            string[] heads = { "LW_Col_Name", "LW_Col_Faction", "LW_Col_Population", "LW_Col_Prosperity", "LW_Col_Specialty", "LW_Col_Garrison", "LW_Col_Distance" };
            SortBy?[] sorts = { SortBy.Name, SortBy.Faction, SortBy.Population, SortBy.Prosperity, null, SortBy.Garrison, SortBy.Distance };
            float x = rect.x;
            for (int c = 0; c < cols.Length; c++)
            {
                var h = new Rect(x, rect.y, cols[c], 24f);
                Widgets.Label(h, heads[c].Translate());
                if (sorts[c].HasValue && Widgets.ButtonInvisible(h))
                {
                    if (sort == sorts[c].Value) descending = !descending;
                    else
                    {
                        sort = sorts[c].Value;
                        descending = true;
                    }
                }
                x += cols[c];
            }
            Widgets.Label(new Rect(x, rect.y, rect.xMax - x, 24f), "LW_Col_Needs".Translate());
            Rect list = rect;
            list.yMin += 26f;
            const float rowH = 24f;
            var view = new Rect(0f, 0f, list.width - 16f, rows.Count * rowH);
            Widgets.BeginScrollView(list, ref scroll, view);
            for (int i = 0; i < rows.Count; i++)
            {
                CompSettlementLife s = rows[i];
                var row = new Rect(0f, i * rowH, view.width, rowH);
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);
                float cx = 0f;
                string[] vals =
                {
                    s.Settlement.Label, s.Settlement.Faction.Name, $"{s.Tier.label} {Mathf.RoundToInt(s.population)}",
                    s.prosperity.ToStringPercent(), s.specialty?.specialtyLabel ?? "-", Mathf.RoundToInt(s.garrison).ToString(),
                    home == null ? "-" : Dist(s).ToString("F0"),
                };
                for (int c = 0; c < cols.Length; c++)
                {
                    if (c == 1) GUI.color = s.Settlement.Faction.Color;
                    Widgets.Label(new Rect(cx, row.y, cols[c], rowH), vals[c].Truncate(cols[c] - 4f));
                    GUI.color = Color.white;
                    cx += cols[c];
                }
                Widgets.Label(new Rect(cx, row.y, row.width - cx, rowH), NeedsSummary(s).Truncate(row.width - cx));
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                if (Widgets.ButtonInvisible(row)) CameraJumper.TryJumpAndSelect(s.Settlement);
            }
            Widgets.EndScrollView();
        }

        private static string NeedsSummary(CompSettlementLife s)
        {
            var parts = new List<string>();
            foreach (CommodityDef c in Commodities.All)
            {
                float f = s.PriceFactor(c);
                if (f >= 1.4f) parts.Add($"{c.label} ×{f:F1}");
            }
            return parts.Count == 0 ? "-" : string.Join(", ", parts);
        }
    }
}
