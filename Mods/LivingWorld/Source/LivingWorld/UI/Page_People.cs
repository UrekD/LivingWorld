using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>Notable people: merchants, warlords and their deeds.</summary>
    public class Page_People : ChroniclePage
    {
        private Vector2 scroll;

        public override string Label => "LW_Tab_People".Translate();

        public override void Draw(Rect rect)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            var list = new List<Figure>(comp.figures);
            if (list.Count == 0)
            {
                Widgets.Label(rect, "LW_People_None".Translate());
                return;
            }
            // Sworn avengers first: they are coming for someone.
            list.SortBy(f => f.FeudOpen && f.Alive ? 0 : f.status == FigureStatus.Active ? 1 : 2, f => -(f.visits + f.deeds.Count));
            const float rowH = 64f;
            var view = new Rect(0f, 0f, rect.width - 16f, list.Count * rowH);
            Widgets.BeginScrollView(rect, ref scroll, view);
            for (int i = 0; i < list.Count; i++)
            {
                Figure f = list[i];
                var row = new Rect(0f, i * rowH, view.width, rowH - 4f);
                Widgets.DrawMenuSection(row);
                Rect inner = row.ContractedBy(6f);
                if (f.pawn != null && !f.pawn.Discarded && !f.pawn.Dead) Widgets.ThingIcon(new Rect(inner.x, inner.y, 48f, 48f), f.pawn);
                float x = inner.x + 56f;
                Faction fac = comp.FactionById(f.factionId);
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(x, inner.y - 2f, 400f, 30f), f.name);
                Text.Font = GameFont.Small;
                GUI.color = fac?.Color ?? Color.gray;
                Widgets.Label(new Rect(x, inner.y + 26f, 400f, 22f), $"{f.RoleLabel.CapitalizeFirst()} ({fac?.Name ?? "?"})");
                GUI.color = Color.white;
                Widgets.Label(new Rect(x + 410f, inner.y, 160f, 22f), ("LW_People_Status_" + f.status).Translate());
                Widgets.Label(new Rect(x + 410f, inner.y + 22f, 160f, 22f), "LW_People_Visits".Translate(f.visits));
                ChronicleEntry last = f.deeds.Count > 0 ? comp.chronicle.Get(f.deeds[f.deeds.Count - 1]) : null;
                if (f.FeudOpen && f.Alive)
                {
                    GUI.color = ColorLibrary.RedReadable;
                    Widgets.Label(new Rect(x + 580f, inner.y, inner.xMax - x - 580f, inner.height), "LW_Inspect_Sworn".Translate(f.nemesisName, f.avenges ?? "?"));
                    GUI.color = Color.white;
                }
                else if (last != null)
                {
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(x + 580f, inner.y, inner.xMax - x - 580f, inner.height), last.text);
                    Text.Font = GameFont.Small;
                }
                if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                if (Widgets.ButtonInvisible(row) && f.pawn != null && f.pawn.Spawned) CameraJumper.TryJumpAndSelect(f.pawn);
            }
            Widgets.EndScrollView();
        }
    }
}
