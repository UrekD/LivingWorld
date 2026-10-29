using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace LivingWorld
{
    /// <summary>
    /// Dwarf Fortress "legends mode" for the world: the factions, wars, settlements, figures, the colony's deeds and the whole
    /// chronicle, written to one self-contained web page and a plain-text twin in the save-data folder (LivingWorld/Legends).
    /// The text is gathered into a <see cref="LegendDocument"/>; only its renderers write files, and the HTML one escapes everything.
    /// </summary>
    public static class LegendsExport
    {
        public class Result
        {
            public string htmlPath, textPath;
            /// <summary>Milliseconds spent gathering the document and writing the two files.</summary>
            public long buildMs, writeMs;
        }

        /// <summary>Longest world-name part of a file name, in UTF-16 units.</summary>
        public const int MaxStem = 60;

        /// <summary>Counters tried before giving up (one export a day, a few a day at most).</summary>
        private const int MaxCounter = 1000;

        /// <summary>The Windows set on every platform, so a file copied from Linux or macOS keeps a name Windows accepts (control characters are added in <see cref="FileStem(string)"/>).</summary>
        private const string InvalidNameChars = "<>:\"/\\|?*";

        private static readonly Regex NameMarker = new Regex("\\([\\*/][^)]*\\)");

        private static readonly Regex ColorTag = new Regex("</?color(=[^>]*)?>", RegexOptions.IgnoreCase);

        public static string FolderPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "LivingWorld", "Legends");

        /// <summary>Game text as one plain line: the colour tags letters add around names are dropped (nothing else is touched), line breaks become spaces.</summary>
        public static string Plain(string s) => s == null ? "" : ColorTag.Replace(s, "").Replace("\r", "").Replace('\n', ' ').Trim();

        /// <summary>The world's name and today's date, the name both files share before an optional counter.</summary>
        public static string BaseName() => FileStem() + "_" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>Writes both files into <paramref name="folder"/> (default: <see cref="FolderPath"/>), never over an existing file: a counter is added to the name. A failure leaves no half-written file behind.</summary>
        public static Result Export(string folder = null)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            if (comp == null) throw new InvalidOperationException("the world simulation is not loaded");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LegendDocument doc = Build(comp);
            long buildMs = clock.ElapsedMilliseconds;
            clock.Restart();
            folder ??= FolderPath;
            Directory.CreateDirectory(folder);
            string baseName = BaseName();
            for (int n = 1; n <= MaxCounter; n++)
            {
                string stem = n == 1 ? baseName : baseName + "_" + n;
                string htmlPath = Path.Combine(folder, stem + ".html"), textPath = Path.Combine(folder, stem + ".txt");
                if (File.Exists(htmlPath) || File.Exists(textPath)) continue;
                if (!TryCreate(htmlPath, doc.ToHtml, new UTF8Encoding(false))) continue;
                bool textMade;
                try
                {
                    textMade = TryCreate(textPath, doc.ToText, new UTF8Encoding(true));
                }
                catch
                {
                    DeleteQuietly(htmlPath);
                    throw;
                }
                if (!textMade)
                {
                    DeleteQuietly(htmlPath);
                    continue;
                }
                return new Result { htmlPath = htmlPath, textPath = textPath, buildMs = buildMs, writeMs = clock.ElapsedMilliseconds };
            }
            throw new IOException("no free file name in " + folder);
        }

        /// <summary>
        /// False only when creating the file failed because it exists (CreateNew never overwrites; it appeared meanwhile). A failure while
        /// writing is not "exists": the half-written file this call made is deleted and the error thrown.
        /// </summary>
        private static bool TryCreate(string path, Action<TextWriter> write, Encoding encoding)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
            try
            {
                using (stream)
                using (var writer = new StreamWriter(stream, encoding))
                    write(writer);
            }
            catch
            {
                DeleteQuietly(path);
                throw;
            }
            return true;
        }

        private static void DeleteQuietly(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                LWLog.Warning("legends export: could not delete " + path + ": " + e.Message);
            }
        }

        /// <summary>The current world's name as a file name.</summary>
        public static string FileStem() => FileStem(Find.World?.info?.name);

        /// <summary>
        /// A world name as a file name part: no path or control characters and no spaces (the same set on every platform), no leading dot,
        /// at most <see cref="MaxStem"/> UTF-16 units (never cut inside a surrogate pair), not a Windows device name, never empty.
        /// </summary>
        public static string FileStem(string worldName)
        {
            var sb = new StringBuilder();
            if (worldName != null)
            {
                string name = worldName.Trim();
                for (int i = 0; i < name.Length; i++)
                {
                    char c = name[i];
                    if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
                        sb.Append(c).Append(name[++i]);
                    else
                        sb.Append(char.IsSurrogate(c) || char.IsWhiteSpace(c) || char.IsControl(c) || InvalidNameChars.IndexOf(c) >= 0 ? '_' : c);
                }
            }
            string stem = sb.ToString().Trim('.', '_');
            if (IsDeviceName(stem)) stem = "_" + stem;
            if (stem.Length > MaxStem)
            {
                int cut = MaxStem;
                if (char.IsHighSurrogate(stem[cut - 1])) cut--;
                stem = stem.Substring(0, cut);
            }
            stem = stem.TrimEnd('.', '_');
            return stem.Length == 0 ? "World" : stem;
        }

        /// <summary>CON, PRN, AUX, NUL, COM0-9 and LPT0-9 (also with the superscript digits) before the first dot: on Windows the device, whatever the extension.</summary>
        private static bool IsDeviceName(string stem)
        {
            int dot = stem.IndexOf('.');
            string head = (dot < 0 ? stem : stem.Substring(0, dot)).ToUpperInvariant();
            switch (head)
            {
                case "CON":
                case "PRN":
                case "AUX":
                case "NUL":
                case "CONIN$":
                case "CONOUT$":
                    return true;
            }
            if (head.Length != 4 || !(head.StartsWith("COM", StringComparison.Ordinal) || head.StartsWith("LPT", StringComparison.Ordinal))) return false;
            char d = head[3];
            return (d >= '0' && d <= '9') || d == '\u00B9' || d == '\u00B2' || d == '\u00B3';
        }

        /// <summary>Whether the game can open a folder in the system's file browser here: vanilla (Dialog_Options, Page_ModsConfig) does it only on Windows.</summary>
        public static bool CanOpenFolder => Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

        /// <summary>The folder's full path, what vanilla shows on the other platforms (Dialog_MessageBox).</summary>
        public static string FullFolderPath => Path.GetFullPath(FolderPath);

        /// <summary>Windows only (see <see cref="CanOpenFolder"/>), as vanilla: the path itself goes to OpenURL. Elsewhere the page shows <see cref="FullFolderPath"/>.</summary>
        public static void OpenFolder()
        {
            Directory.CreateDirectory(FolderPath);
            Application.OpenURL(FolderPath);
        }

        public static LegendDocument Build(LivingWorldComponent comp)
        {
            comp.RebuildIndexes(seedMissing: false);
            return new Builder(comp).Build();
        }

        private class Place
        {
            public PlanetTile tile;
            public string name;
            /// <summary>Fixed id for links to this place (never game text).</summary>
            public string anchor;
            /// <summary>The simulated settlement standing here, if any.</summary>
            public CompSettlementLife live;
            /// <summary>A settlement that stands here but is not simulated (the player's, one not yet seeded, one the simulation skips).</summary>
            public Settlement other;
            public readonly List<ChronicleEntry> history = new List<ChronicleEntry>();
        }

        private class Builder
        {
            private readonly LivingWorldComponent comp;
            private readonly Vector2 longLat;
            // One build reads one snapshot: each entry's date and line are made once, however many lists it appears in.
            private readonly Dictionary<int, string> dates = new Dictionary<int, string>();
            private readonly Dictionary<int, string> lines = new Dictionary<int, string>();
            private readonly HashSet<string> usedAnchors = new HashSet<string>();
            private Dictionary<PlanetTile, Place> places;
            private int ruins;
            private Dictionary<int, List<ChronicleEntry>> byFaction;

            private static readonly HashSet<string> RulerDefs = new HashSet<string>
            {
                LWChronicleDefOf.LW_RulerVow.defName, LWChronicleDefOf.LW_RulerFell.defName, LWChronicleDefOf.LW_NewHouse.defName, LWChronicleDefOf.LW_LeaderChanged.defName,
            };

            private static readonly HashSet<string> FactionHistoryDefs = new HashSet<string>
            {
                LWChronicleDefOf.LW_NewPeopleArrive.defName, LWChronicleDefOf.LW_KnowHowGained.defName, LWChronicleDefOf.LW_FactionDefeated.defName,
            };

            public Builder(LivingWorldComponent comp)
            {
                this.comp = comp;
                longLat = Neighbourhood.Home is Map home ? Find.WorldGrid.LongLatOf(home.Tile) : Vector2.zero;
            }

            /// <summary>Game text as plain text: colour tags and (*Name) markers are gone. Keyed lines are not resolved (vanilla caches every resolved string for good), their tags are only stripped.</summary>
            private static string Q(string s) => Plain(s);

            /// <summary>A keyed line: the implicit TaggedString-to-string conversion would strip every &lt;...&gt; from it, the markup in game names and entries that the page must escape instead. Only the (*Name) markers go (and nothing is cached, as Resolve does).</summary>
            private static string Q(TaggedString s) => Plain(NameMarker.Replace(s.RawText ?? "", ""));

            private static string Count(string key, int count) => Q((count == 1 ? key + "_One" : key).Translate(count));

            /// <summary>"{0}: {1}" in the player's language.</summary>
            private static string Entry(string label, string text) => Q("LW_Export_Entry".Translate(label, text));

            private static string FactionAnchor(int loadId) => "f" + loadId;

            private string Anchor(string prefix, int id)
            {
                string a = prefix + id;
                for (int n = 2; !usedAnchors.Add(a); n++) a = prefix + id + "x" + n;
                return a;
            }

            private static void AddLink(List<LegendLink> links, string label, string anchor)
            {
                foreach (LegendLink l in links)
                    if (l.anchor == anchor) return;
                links.Add(new LegendLink { label = label, anchor = anchor });
            }

            private string DateAbs(long abs) => Q(GenDate.DateFullStringAt(abs, longLat));

            /// <summary>A sim-clock time (wars, scars) as a date: the sim clock runs in the past during history, so shift by the difference to the absolute one.</summary>
            private string DateSim(int simTicks) => DateAbs((long)simTicks + SimClock.TicksAbs - SimClock.Now);

            private string Date(ChronicleEntry e)
            {
                if (!dates.TryGetValue(e.id, out string d)) dates[e.id] = d = Q(e.DateString);
                return d;
            }

            private string Line(ChronicleEntry e)
            {
                if (!lines.TryGetValue(e.id, out string l)) lines[e.id] = l = Entry(Date(e), Q(e.text));
                return l;
            }

            private static int Weight(ChronicleEntry e) => (int)e.def.importance;

            private string FactionName(int id, string fallback = null) => comp.FactionById(id) is Faction f ? Q(f.Name) : fallback ?? Q("LW_Export_GoneFaction".Translate());

            private LegendLink FactionLink(int id) => comp.FactionById(id) is Faction f ? new LegendLink { label = Q(f.Name), anchor = FactionAnchor(f.loadID) } : null;

            /// <summary>The chronicle by the faction it is about (factionA), in order: indexed once instead of scanned for every faction.</summary>
            private List<ChronicleEntry> About(int loadId)
            {
                if (byFaction == null)
                {
                    byFaction = new Dictionary<int, List<ChronicleEntry>>();
                    foreach (ChronicleEntry e in comp.chronicle.entries)
                    {
                        if (e.factionA < 0) continue;
                        if (!byFaction.TryGetValue(e.factionA, out List<ChronicleEntry> list)) byFaction[e.factionA] = list = new List<ChronicleEntry>();
                        list.Add(e);
                    }
                }
                return byFaction.TryGetValue(loadId, out List<ChronicleEntry> found) ? found : new List<ChronicleEntry>();
            }

            public LegendDocument Build()
            {
                string world = Q(Find.World?.info?.name ?? "?");
                var doc = new LegendDocument
                {
                    title = Q("LW_Export_Title".Translate(world)),
                    subtitle = Q("LW_Export_Subtitle".Translate(DateAbs(GenTicks.TicksAbs), DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))),
                    seeAlso = Q("LW_Export_SeeAlso".Translate()),
                };
                Factions(doc, world);
                Wars(doc);
                Settlements(doc);
                Figures(doc);
                Colony(doc);
                Chronicle(doc);
                return doc;
            }

            private void Factions(LegendDocument doc, string world)
            {
                LegendSection sec = doc.Section("world", Q("LW_Export_Sec_World".Translate()));
                var states = new List<FactionState>();
                foreach (FactionState fs in comp.factionStates)
                    if (fs.faction != null) states.Add(fs);
                states.Sort((a, b) =>
                {
                    int d = a.faction.defeated.CompareTo(b.faction.defeated);
                    return d != 0 ? d : b.strength.CompareTo(a.strength);
                });
                int living = 0;
                foreach (FactionState fs in states)
                    if (!fs.faction.defeated) living++;
                Dictionary<PlanetTile, Place> all = Places();
                LegendBlock overview = sec.Block(Q("LW_Export_Overview".Translate()));
                overview.lines.Add(Q("LW_Export_World".Translate(world, Find.World?.info?.seedString ?? "?")));
                overview.lines.Add(Q("LW_Export_Colony".Translate(Faction.OfPlayer.Name)));
                overview.lines.Add(Q("LW_Export_Summary".Translate(Count("LW_Export_Count_Factions", living), Count("LW_Export_Count_Wars", comp.wars.Count),
                    Count("LW_Export_Count_Places", all.Count), Count("LW_Export_Count_Figures", comp.figures.Count), Count("LW_NewsCount", comp.chronicle.Count))));
                if (states.Count == 0) sec.Block(null).lines.Add(Q("LW_Export_NoFactions".Translate()));
                foreach (FactionState fs in states) FactionBlock(sec, fs, states, all);
            }

            private void FactionBlock(LegendSection sec, FactionState fs, List<FactionState> states, Dictionary<PlanetTile, Place> all)
            {
                Faction f = fs.faction;
                string name = Q(f.Name);
                LegendBlock b = sec.Block(name, FactionAnchor(f.loadID));
                b.lines.Add(Q("LW_Export_FactionKind".Translate(f.def.LabelCap, KnowHow.Label(fs))));
                if (fs.traits.Count > 0)
                {
                    var traits = new List<string>();
                    foreach (FactionTraitDef t in fs.traits) traits.Add(Q(t.label));
                    b.lines.Add(Q("LW_Export_Traits".Translate(traits.ToCommaList())));
                }
                if (Rulers.Line(comp, fs) is string ruler) b.lines.Add(Q(ruler));
                if (f.defeated)
                {
                    string when = null;
                    foreach (ChronicleEntry e in About(f.loadID))
                        if (e.defName == LWChronicleDefOf.LW_FactionDefeated.defName) when = Date(e);
                    b.lines.Add(Q((when == null ? "LW_Export_Defeated" : "LW_Export_DefeatedOn").Translate(when ?? "")));
                }
                else
                {
                    b.lines.Add(Q("LW_Faction_Settlements".Translate(Count("LW_Count_Settlements", fs.settlements.Count), Count("LW_Count_People", Mathf.RoundToInt(fs.population)))));
                    b.lines.Add(Q("LW_Faction_Strength".Translate(Mathf.RoundToInt(fs.strength))));
                    b.lines.Add(Q("LW_Faction_Treasury".Translate(Mathf.RoundToInt(fs.treasury))));
                    var enemies = new List<string>();
                    var allies = new List<string>();
                    foreach (FactionState o in states)
                    {
                        if (o == fs || o.faction.defeated) continue;
                        if (f.HostileTo(o.faction))
                        {
                            enemies.Add(Q(o.faction.Name));
                            AddLink(b.links, Q(o.faction.Name), FactionAnchor(o.faction.loadID));
                        }
                        else if (f.RelationKindWith(o.faction) == FactionRelationKind.Ally)
                        {
                            allies.Add(Q(o.faction.Name));
                            AddLink(b.links, Q(o.faction.Name), FactionAnchor(o.faction.loadID));
                        }
                    }
                    b.lines.Add(Q(f.def.permanentEnemy ? "LW_Faction_HostileToAll".Translate() : enemies.Count == 0 ? "LW_Faction_AtPeace".Translate() : "LW_Faction_HostileTo".Translate(enemies.ToCommaList())));
                    if (allies.Count > 0) b.lines.Add(Q("LW_Export_AlliedWith".Translate(allies.ToCommaList())));
                    if (f.HasGoodwill) b.lines.Add(Q("LW_Faction_PlayerRelation".Translate(f.PlayerRelationKind.GetLabelCap(), f.PlayerGoodwill.ToStringWithSign())));
                    foreach (CompSettlementLife s in fs.settlements)
                        if (all.TryGetValue(s.Settlement.Tile, out Place pl)) AddLink(b.links, PlaceTitle(pl), pl.anchor);
                }
                List<ChronicleEntry> about = About(f.loadID);
                LegendBlock rulers = null, history = null;
                foreach (ChronicleEntry e in about)
                {
                    if (RulerDefs.Contains(e.defName)) (rulers ??= sec.Block(Q("LW_Export_FactionRulers".Translate(name)))).Add(Line(e), Weight(e));
                    else if (FactionHistoryDefs.Contains(e.defName)) (history ??= sec.Block(Q("LW_Export_FactionHistory".Translate(name)))).Add(Line(e), Weight(e));
                }
            }

            private void Wars(LegendDocument doc)
            {
                LegendSection sec = doc.Section("wars", Q("LW_Export_Sec_Wars".Translate()));
                var wars = new List<War>(comp.wars);
                wars.Sort((a, b) => a.startTicks.CompareTo(b.startTicks));
                int active = 0;
                foreach (War w in wars)
                    if (w.Active) active++;
                sec.Block(Q("LW_Export_Overview".Translate())).lines.Add(active == 0 ? Q("LW_Wars_None".Translate()) : Count("LW_Wars_Active", active));
                var byCause = new Dictionary<int, List<ChronicleEntry>>();
                foreach (ChronicleEntry e in comp.chronicle.entries)
                {
                    if (e.causeId < 0) continue;
                    if (!byCause.TryGetValue(e.causeId, out List<ChronicleEntry> list)) byCause[e.causeId] = list = new List<ChronicleEntry>();
                    list.Add(e);
                }
                foreach (War w in wars)
                {
                    LegendBlock b = sec.Block(Q("LW_Export_WarTitle".Translate(w.nameA ?? "?", w.nameB ?? "?")));
                    if (!w.cause.NullOrEmpty()) b.lines.Add(Q("LW_Export_WarCause".Translate(w.cause)));
                    b.lines.Add(Q(w.ancient ? "LW_Export_WarAncient".Translate() : "LW_Export_WarStarted".Translate(DateSim(w.startTicks))));
                    b.lines.Add(Q(w.Active
                        ? "LW_Export_WarOngoing".Translate((SimClock.Now - w.startTicks).ToStringTicksToPeriod(allowSeconds: false, shortForm: true))
                        : "LW_Export_WarEnded".Translate(DateSim(w.endTicks), (w.endTicks - w.startTicks).ToStringTicksToPeriod(allowSeconds: false, shortForm: true))));
                    b.lines.Add(Count("LW_Wars_Battles", w.battles));
                    b.lines.Add(Q(Mathf.Abs(w.score) < 1f
                        ? "LW_Export_WarEven".Translate()
                        : "LW_Export_WarScore".Translate(Mathf.RoundToInt(Mathf.Abs(w.score)), (w.score > 0f ? w.nameA : w.nameB) ?? "?")));
                    if (w.factionA >= 0) AddLink(b.links, Q(w.nameA ?? "?"), FactionAnchor(w.factionA));
                    if (w.factionB >= 0) AddLink(b.links, Q(w.nameB ?? "?"), FactionAnchor(w.factionB));
                    if (w.chronicleId >= 0 && byCause.TryGetValue(w.chronicleId, out List<ChronicleEntry> course))
                        foreach (ChronicleEntry e in course) b.Add(Line(e), Weight(e));
                }
            }

            private static List<ChronicleEventDef> PlaceDefs() => new List<ChronicleEventDef>
            {
                LWChronicleDefOf.LW_SettlementFounded, LWChronicleDefOf.LW_RuinResettled, LWChronicleDefOf.LW_SettlementCaptured, LWChronicleDefOf.LW_SettlementRazed,
                LWChronicleDefOf.LW_SettlementPlundered, LWChronicleDefOf.LW_AttackRepelled, LWChronicleDefOf.LW_TownLiberated, LWChronicleDefOf.LW_Rebellion,
            };

            private string PlaceTitle(Place p) => p.name ?? Q("LW_Export_UnnamedPlace".Translate(p.tile.tileId));

            /// <summary>
            /// Every place with a settlement now or with settlement history (ruins keep the name the chronicle snapshotted), by tile, made once per
            /// build. A place is a ruin only when no settlement stands there: one the simulation does not run (the player's, a new one) is not.
            /// </summary>
            private Dictionary<PlanetTile, Place> Places()
            {
                if (places != null) return places;
                places = new Dictionary<PlanetTile, Place>();
                foreach (CompSettlementLife s in comp.activeSettlements)
                    places[s.Settlement.Tile] = new Place { tile = s.Settlement.Tile, live = s, name = Q(s.Settlement.Label) };
                List<ChronicleEventDef> defs = PlaceDefs();
                foreach (ChronicleEntry e in comp.chronicle.entries)
                {
                    if (!e.tile.Valid || !defs.Contains(e.def)) continue;
                    if (!places.TryGetValue(e.tile, out Place p)) places[e.tile] = p = new Place { tile = e.tile };
                    p.history.Add(e);
                    if (p.live == null && !e.placeName.NullOrEmpty()) p.name = Q(e.placeName);
                }
                foreach (Settlement s in Find.WorldObjects.Settlements)
                {
                    if (!places.TryGetValue(s.Tile, out Place p)) places[s.Tile] = p = new Place { tile = s.Tile };
                    if (p.live != null) continue;
                    p.other = s;
                    p.name = Q(s.Label);
                }
                ruins = 0;
                foreach (Place p in places.Values)
                {
                    p.anchor = Anchor("t", p.tile.tileId);
                    if (p.live == null && p.other == null) ruins++;
                }
                return places;
            }

            /// <summary>"Founded by X" from the chronicle (a ruin settled again: "Resettled by X"); null when it names nobody still known.</summary>
            private string FoundedLine(Place p, out int factionId)
            {
                factionId = -1;
                foreach (ChronicleEntry e in p.history)
                    if (e.defName == LWChronicleDefOf.LW_SettlementFounded.defName && comp.FactionById(e.factionA) is Faction founders)
                    {
                        factionId = founders.loadID;
                        return Q("LW_Export_PlaceFounded".Translate(Q(founders.Name)));
                    }
                foreach (ChronicleEntry e in p.history)
                    if (e.defName == LWChronicleDefOf.LW_RuinResettled.defName && comp.FactionById(e.factionA) is Faction settlers)
                    {
                        factionId = settlers.loadID;
                        return Q("LW_Export_PlaceResettled".Translate(Q(settlers.Name)));
                    }
                return null;
            }

            private void Settlements(LegendDocument doc)
            {
                LegendSection sec = doc.Section("settlements", Q("LW_Export_Sec_Settlements".Translate()));
                var list = new List<Place>(Places().Values);
                list.Sort((a, b) =>
                {
                    if ((a.name == null) != (b.name == null)) return a.name == null ? 1 : -1;
                    int d = string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                    return d != 0 ? d : a.tile.tileId.CompareTo(b.tile.tileId);
                });
                LegendBlock overview = sec.Block(Q("LW_Export_Overview".Translate()));
                overview.lines.Add(list.Count == 0 ? Q("LW_Export_NoPlaces".Translate()) : Q("LW_Export_PlacesSummary".Translate(Count("LW_Export_Count_Places", list.Count), Count("LW_Export_Count_Ruins", ruins))));
                foreach (Place p in list)
                {
                    LegendBlock b = sec.Block(PlaceTitle(p), p.anchor);
                    CompSettlementLife s = p.live;
                    if (s != null)
                    {
                        b.lines.Add(Q("LW_Export_PlaceHeld".Translate(Q(s.Settlement.Faction?.Name ?? "?"), s.Tier.LabelCap, Count("LW_Count_People", Mathf.RoundToInt(s.population)))));
                        if (s.Settlement.Faction != null) AddLink(b.links, Q(s.Settlement.Faction.Name), FactionAnchor(s.Settlement.Faction.loadID));
                        if (s.originalFactionId >= 0)
                        {
                            b.lines.Add(Q(s.Occupied && s.occupiedSinceTicks >= 0
                                ? "LW_Export_PlaceFoundedOccupied".Translate(FactionName(s.originalFactionId), DateSim(s.occupiedSinceTicks))
                                : "LW_Export_PlaceFounded".Translate(FactionName(s.originalFactionId))));
                            if (FactionLink(s.originalFactionId) is LegendLink original) AddLink(b.links, original.label, original.anchor);
                        }
                        if (s.sackedTicks > FactionState.Never && s.sackSeverity > 0f)
                        {
                            b.lines.Add(Q("LW_Export_PlaceSacked".Translate(DateSim(s.sackedTicks), s.sackSeverity.ToStringPercent())));
                            if (s.Scar() >= CompSettlementLife.ScarVisible) b.lines.Add(Q("LW_Export_PlaceScarShows".Translate(s.Scar().ToStringPercent())));
                        }
                    }
                    else
                    {
                        if (p.other != null)
                        {
                            Faction owner = p.other.Faction;
                            b.lines.Add(Q("LW_Export_PlaceHeldPlain".Translate(Q(owner?.Name ?? "?"))));
                            if (owner != null) AddLink(b.links, Q(owner.Name), FactionAnchor(owner.loadID));
                        }
                        else b.lines.Add(Q("LW_Export_PlaceRuins".Translate()));
                        string founded = FoundedLine(p, out int founderId);
                        if (founded != null)
                        {
                            b.lines.Add(founded);
                            if (FactionLink(founderId) is LegendLink founder) AddLink(b.links, founder.label, founder.anchor);
                        }
                    }
                    foreach (ChronicleEntry e in p.history) b.Add(Line(e), Weight(e));
                }
            }

            private void Figures(LegendDocument doc)
            {
                LegendSection sec = doc.Section("figures", Q("LW_Export_Sec_Figures".Translate()));
                if (comp.figures.Count == 0) sec.Block(null).lines.Add(Q("LW_People_None".Translate()));
                (string key, FigureRole[] roles)[] groups =
                {
                    ("LW_Export_Figures_Warlords", new[] { FigureRole.Warlord }),
                    ("LW_Export_Figures_Merchants", new[] { FigureRole.Merchant }),
                    ("LW_Export_Figures_Kin", new[] { FigureRole.Kin }),
                    ("LW_Export_Figures_Others", new[] { FigureRole.Traveller, FigureRole.Freed }),
                };
                foreach ((string key, FigureRole[] roles) in groups)
                {
                    var members = new List<Figure>();
                    foreach (Figure f in comp.figures)
                        if (Array.IndexOf(roles, f.role) >= 0) members.Add(f);
                    if (members.Count == 0) continue;
                    members.Sort((a, b) => (b.visits + b.deeds.Count).CompareTo(a.visits + a.deeds.Count));
                    LegendBlock b = sec.Block(Count(key, members.Count));
                    foreach (Figure f in members)
                    {
                        LegendItem item = b.Add(Q("LW_Export_Figure".Translate(Q(f.name), Q(f.RoleLabel.CapitalizeFirst()), FactionName(f.factionId, "?"), ("LW_People_Status_" + f.status).Translate())), f.Renowned ? 1 : 0);
                        item.anchor = "h" + f.id;
                        if (FactionLink(f.factionId) is LegendLink home) AddLink(item.links, home.label, home.anchor);
                        if (f.visits > 0) item.notes.Add(Q("LW_People_Visits".Translate(f.visits)));
                        if (f.kills > 0) item.notes.Add(Q("LW_Export_Kills".Translate(f.kills)));
                        if (f.tradedSilver >= 1f) item.notes.Add(Q("LW_Inspect_Traded".Translate(Mathf.RoundToInt(f.tradedSilver))));
                        foreach (int id in f.deeds)
                            if (comp.chronicle.Get(id) is ChronicleEntry e) item.notes.Add(Line(e));
                    }
                }
                LegendBlock feuds = sec.Block(Q("LW_Export_Feuds".Translate()));
                foreach (Figure f in comp.figures)
                    if (f.FeudOpen && f.Alive) feuds.Add(Entry(Q(f.name), Q("LW_Inspect_Sworn".Translate(Q(f.nemesisName), Q(f.avenges ?? "?")))), 1);
                var feudDefs = new List<string>
                {
                    LWChronicleDefOf.LW_FeudSworn.defName, LWChronicleDefOf.LW_FeudAvenged.defName, LWChronicleDefOf.LW_FeudEnded.defName, LWChronicleDefOf.LW_FeudBroken.defName,
                    LWChronicleDefOf.LW_FeudSettled.defName, LWChronicleDefOf.LW_WarlordFell.defName,
                };
                foreach (ChronicleEntry e in comp.chronicle.entries)
                    if (feudDefs.Contains(e.defName)) feuds.Add(Line(e), Weight(e));
                if (feuds.items.Count == 0) feuds.lines.Add(Q("LW_Export_NoFeuds".Translate()));
            }

            private void Colony(LegendDocument doc)
            {
                LegendSection sec = doc.Section("colony", Q("LW_Export_Sec_Colony".Translate()));
                int renown = Legends.Renown(comp);
                LegendBlock top = sec.Block(Q(Faction.OfPlayer.Name));
                top.lines.Add(Q("LW_Legends_Renown".Translate(Faction.OfPlayer.Name, renown, ("LW_Renown_" + Legends.Tier(renown)).Translate())));
                // Deeds by person, in the order of each person's first deed.
                var order = new List<string>();
                var byKey = new Dictionary<string, List<Deed>>();
                foreach (Deed d in comp.deeds)
                {
                    string key = d.pawn != null && !d.pawn.Discarded ? "p" + d.pawn.thingIDNumber : "n" + d.name;
                    if (!byKey.TryGetValue(key, out List<Deed> list))
                    {
                        byKey[key] = list = new List<Deed>();
                        order.Add(key);
                    }
                    list.Add(d);
                }
                if (order.Count == 0) top.lines.Add(Q("LW_Legends_None".Translate()));
                foreach (string key in order)
                {
                    List<Deed> deeds = byKey[key];
                    Pawn p = deeds[0].pawn != null && !deeds[0].pawn.Discarded ? deeds[0].pawn : null;
                    string name = Q(deeds[0].name ?? p?.LabelShort ?? "?");
                    LegendBlock b = sec.Block(Q(p == null || p.Dead ? "LW_Legends_Dead".Translate(name) : (TaggedString)name));
                    foreach (Deed d in deeds) b.Add(Entry(DateAbs(d.ticksAbs), Q(d.text)));
                }
                LegendBlock known = sec.Block(Q("LW_Export_ColonyChronicle".Translate()));
                foreach (ChronicleEntry e in comp.chronicle.entries)
                    if (e.involvesPlayer) known.Add(Line(e), Weight(e));
                if (known.items.Count == 0) known.lines.Add(Q("LW_Export_NothingYet".Translate()));
            }

            private void Chronicle(LegendDocument doc)
            {
                LegendSection sec = doc.Section("chronicle", Q("LW_Export_Sec_Chronicle".Translate()));
                var years = new SortedDictionary<int, List<ChronicleEntry>>();
                foreach (ChronicleEntry e in comp.chronicle.entries)
                {
                    Vector2 ll = e.tile.Valid ? Find.WorldGrid.LongLatOf(e.tile) : Vector2.zero;
                    int year = GenDate.Year(e.ticksAbs, ll.x);
                    if (!years.TryGetValue(year, out List<ChronicleEntry> list)) years[year] = list = new List<ChronicleEntry>();
                    list.Add(e);
                }
                if (years.Count == 0) sec.Block(null).lines.Add(Q("LW_Export_NothingYet".Translate()));
                foreach (KeyValuePair<int, List<ChronicleEntry>> kv in years)
                {
                    LegendBlock b = sec.Block(Q("LW_Export_Year".Translate(kv.Key.ToString())));
                    b.lines.Add(Count("LW_Export_Count_Events", kv.Value.Count));
                    foreach (ChronicleEntry e in kv.Value) b.Add(Line(e), Weight(e));
                }
            }
        }
    }
}
