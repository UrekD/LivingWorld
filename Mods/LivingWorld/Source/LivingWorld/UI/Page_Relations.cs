using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Who stands how with whom: goodwill matrix of the living factions (and you).</summary>
    public class Page_Relations : ChroniclePage
    {
        public override string Label => "LW_Tab_Relations".Translate();

        public override void Draw(Rect rect)
        {
            var factions = new List<Faction> { Faction.OfPlayer };
            foreach (FactionState fs in LivingWorldComponent.Instance.factionStates)
                if (fs.faction != null && !fs.faction.defeated && fs.settlements.Count > 0) factions.Add(fs.faction);
            int n = factions.Count;
            float nameW = 170f;
            float cell = Mathf.Min(64f, (rect.width - nameW) / Mathf.Max(1, n));
            Text.Font = GameFont.Tiny;
            // Column headers: faction icons with names on hover.
            for (int j = 0; j < n; j++)
            {
                var h = new Rect(rect.x + nameW + j * cell + (cell - 28f) / 2f, rect.y, 28f, 28f);
                Icon(factions[j], h);
                TooltipHandler.TipRegion(h, factions[j].Name);
            }
            for (int i = 0; i < n; i++)
            {
                float y = rect.y + 34f + i * (cell * 0.6f);
                Faction a = factions[i];
                GUI.color = a.Color;
                Widgets.Label(new Rect(rect.x, y, nameW - 4f, cell * 0.6f), a.Name.Truncate(nameW - 6f));
                GUI.color = Color.white;
                for (int j = 0; j < n; j++)
                {
                    Faction b = factions[j];
                    var c = new Rect(rect.x + nameW + j * cell + 1f, y + 1f, cell - 2f, cell * 0.6f - 2f);
                    if (a == b)
                    {
                        Widgets.DrawBoxSolid(c, new Color(0.1f, 0.1f, 0.1f));
                        continue;
                    }
                    FactionRelation rel = a.RelationWith(b, allowNull: true);
                    if (rel == null) continue;
                    int gw = a.GoodwillWith(b);
                    Color col = rel.kind switch
                    {
                        FactionRelationKind.Hostile => new Color(0.55f, 0.15f, 0.15f),
                        FactionRelationKind.Ally => new Color(0.15f, 0.45f, 0.2f),
                        _ => new Color(0.3f, 0.3f, 0.3f),
                    };
                    Widgets.DrawBoxSolid(c, col);
                    Text.Anchor = TextAnchor.MiddleCenter;
                    Widgets.Label(c, gw.ToStringWithSign());
                    Text.Anchor = TextAnchor.UpperLeft;
                    TooltipHandler.TipRegion(c, $"{a.Name} → {b.Name}: {rel.kind.GetLabelCap()} ({gw.ToStringWithSign()})");
                }
            }
            Text.Font = GameFont.Small;
        }

        private static void Icon(Faction f, Rect r)
        {
            Texture2D tex = f.def.FactionIcon;
            if (tex == null) return;
            GUI.color = f.Color;
            GUI.DrawTexture(r, tex);
            GUI.color = Color.white;
        }
    }
}
