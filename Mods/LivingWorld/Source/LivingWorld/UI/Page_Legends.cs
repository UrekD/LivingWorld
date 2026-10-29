using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>The colony's legend: its renown and the deeds of its people, newest first; buttons export the whole world's legends to a file and open the folder.</summary>
    public class Page_Legends : ChroniclePage
    {
        private Vector2 scroll;
        private float lastHeight = 400f;
        // Rows (a pawn, or the name of one gone from the game) and their deeds, rebuilt when deeds or colonists change.
        private readonly List<(Pawn pawn, string name, List<Deed> deeds)> rows = new List<(Pawn, string, List<Deed>)>();
        private int cachedDeeds = -1, cachedRoster = -1;
        private Deed cachedLast;

        public override string Label => "LW_Tab_Legends".Translate();

        public override void Draw(Rect rect)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            Text.Font = GameFont.Medium;
            int renown = Legends.Renown(comp), tier = Legends.Tier(renown);
            const float buttonW = 150f;
            float headW = rect.width - 2f * buttonW - 12f;
            Widgets.Label(new Rect(rect.x, rect.y, headW, 32f), "LW_Legends_Renown".Translate(Faction.OfPlayer.Name, renown, ("LW_Renown_" + tier).Translate()));
            if (tier > 0)
            {
                Rect hint = new Rect(rect.x, rect.y, headW, 32f);
                TooltipHandler.TipRegion(hint, "LW_Legends_RenownTip".Translate(Mathf.RoundToInt((Legends.RenownFactor(renown) - 1f) * 100f)));
            }
            Text.Font = GameFont.Small;
            var export = new Rect(rect.xMax - 2f * buttonW - 6f, rect.y, buttonW, 30f);
            if (Widgets.ButtonText(export, "LW_Legends_Export".Translate())) Export();
            TooltipHandler.TipRegion(export, "LW_Legends_ExportTip".Translate());
            var folder = new Rect(rect.xMax - buttonW, rect.y, buttonW, 30f);
            bool canOpen = LegendsExport.CanOpenFolder;
            if (Widgets.ButtonText(folder, (canOpen ? "LW_Legends_OpenFolder" : "LW_Legends_ShowFolder").Translate())) OpenFolder();
            TooltipHandler.TipRegion(folder, (canOpen ? "LW_Legends_OpenFolderTip" : "LW_Legends_ShowFolderTip").Translate(LegendsExport.FolderPath));
            var outer = new Rect(rect.x, rect.y + 38f, rect.width, rect.height - 38f);
            List<Pawn> colonists = new List<Pawn>();
            foreach (Map m in Find.Maps)
                if (m.IsPlayerHome) colonists.AddRange(m.mapPawns.FreeColonists);
            Deed last = comp.deeds.Count > 0 ? comp.deeds[comp.deeds.Count - 1] : null;
            if (cachedDeeds != comp.deeds.Count || cachedLast != last || cachedRoster != RosterKey(colonists)) Rebuild(comp, colonists);
            if (rows.Count == 0)
            {
                Widgets.Label(outer, "LW_Legends_None".Translate());
                return;
            }
            var view = new Rect(0f, 0f, outer.width - 16f, lastHeight);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            Vector2 longLat = Neighbourhood.Home is Map home ? Find.WorldGrid.LongLatOf(home.Tile) : Vector2.zero;
            foreach ((Pawn p, string name, List<Deed> deeds) in rows)
            {
                float h = 34f + Mathf.Max(1, deeds.Count) * 18f;
                var row = new Rect(0f, y, view.width, h);
                Widgets.DrawMenuSection(row);
                Rect inner = row.ContractedBy(6f);
                if (p != null && !p.Discarded) Widgets.ThingIcon(new Rect(inner.x, inner.y, 28f, 28f), p);
                string label = p?.LabelShort ?? name;
                Widgets.Label(new Rect(inner.x + 34f, inner.y, inner.width - 34f, 24f), p == null || p.Dead ? "LW_Legends_Dead".Translate(label).Resolve() : label);
                Text.Font = GameFont.Tiny;
                float ly = inner.y + 26f;
                if (deeds.Count == 0) Widgets.Label(new Rect(inner.x + 34f, ly, inner.width - 34f, 18f), "LW_Legends_NoDeeds".Translate());
                foreach (Deed d in deeds)
                {
                    Widgets.Label(new Rect(inner.x + 34f, ly, inner.width - 34f, 18f), $"{GenDate.DateFullStringAt(d.ticksAbs, longLat)}: {d.text}");
                    ly += 18f;
                }
                Text.Font = GameFont.Small;
                y += h + 4f;
            }
            lastHeight = y;
            Widgets.EndScrollView();
        }

        /// <summary>
        /// Writes the legends (page and text) behind a "writing" screen, since a long chronicle takes a moment, and says where they went;
        /// a failure is a message, not an exception in the window.
        /// </summary>
        private static void Export()
        {
            LongEventHandler.QueueLongEvent(DoExport, "LW_Export_Writing", doAsynchronously: false, exceptionHandler: null);
        }

        private static void DoExport()
        {
            try
            {
                LegendsExport.Result r = LegendsExport.Export();
                Messages.Message("LW_Export_Done".Translate(Path.GetFileName(r.htmlPath), Path.GetFileName(r.textPath)), MessageTypeDefOf.TaskCompletion, historical: false);
                LWLog.Message($"legends written: {r.htmlPath}, {r.textPath} (gathered in {r.buildMs} ms, written in {r.writeMs} ms)");
            }
            catch (System.Exception e)
            {
                LWLog.Error("legends export: " + e);
                Messages.Message("LW_Export_Failed".Translate(e.Message), MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        /// <summary>As vanilla's options dialog: the file browser on Windows, the folder's full path in a message box elsewhere.</summary>
        private static void OpenFolder()
        {
            try
            {
                if (LegendsExport.CanOpenFolder) LegendsExport.OpenFolder();
                else Find.WindowStack.Add(new Dialog_MessageBox(LegendsExport.FullFolderPath));
            }
            catch (System.Exception e)
            {
                LWLog.Error("legends folder: " + e);
                Messages.Message("LW_Export_Failed".Translate(e.Message), MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        /// <summary>Who the colonists are (not just how many): a departure and an arrival the same day change it.</summary>
        private static int RosterKey(List<Pawn> colonists)
        {
            int key = colonists.Count;
            foreach (Pawn c in colonists) key = key * 31 + c.thingIDNumber;
            return key;
        }

        /// <summary>Everyone with a deed (the dead too), the living without deeds after them.</summary>
        private void Rebuild(LivingWorldComponent comp, List<Pawn> colonists)
        {
            cachedDeeds = comp.deeds.Count;
            cachedLast = comp.deeds.Count > 0 ? comp.deeds[comp.deeds.Count - 1] : null;
            cachedRoster = RosterKey(colonists);
            rows.Clear();
            var seenPawns = new HashSet<Pawn>();
            var seenNames = new HashSet<string>();
            for (int i = comp.deeds.Count - 1; i >= 0; i--)
            {
                Deed d = comp.deeds[i];
                if (d.pawn != null ? seenPawns.Add(d.pawn) : seenNames.Add(d.name ?? "?")) rows.Add((d.pawn, d.name ?? "?", Legends.Of(comp, d.pawn, d.name)));
            }
            foreach (Pawn c in colonists)
                if (seenPawns.Add(c)) rows.Add((c, c.LabelShort, new List<Deed>()));
        }
    }
}
