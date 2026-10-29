using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LivingWorld.Testing
{
    /// <summary>
    /// The legends export (Story/LegendsExport.cs): written into a temporary folder, with a faction named like markup and a chronicle entry
    /// of the same kind; checks the files, every faction's name, balanced tags, escaping, no scripts or external assets, the links between
    /// parts of the page, and that a second export never overwrites the first. Also the two text helpers (Plain, FileStem) against hostile
    /// input, a write that fails (no half pair is left) and the time the export takes. The faction's name and the chronicle are put back.
    /// </summary>
    public static class LegendsChecks
    {
        private const string Tag = "[LivingWorld:test] legends: ";
        private const string Nasty = "Test <Clan> & \"Sons\" O'Neil";
        private static readonly string[] Paired = { "html", "head", "title", "style", "body", "header", "nav", "main", "section", "div", "h1", "h2", "h3", "p", "ul", "li", "a" };
        private static readonly string[] SectionIds = { "world", "wars", "settlements", "figures", "colony", "chronicle" };
        /// <summary>Milliseconds the gathering and the writing may take together (a regression, not a measurement: the 5-year stress world is the big one).</summary>
        private static double BudgetMs => AutoTest.Plan == "stress" ? 15000d : 5000d;

        public static void Register() => AutoTest.Checks.Add(Controlled);

        private static IEnumerable<string> Controlled()
        {
            var bad = new List<string>();
            try
            {
                Plain(bad);
                Stems(bad);
                Run(bad);
            }
            catch (Exception e)
            {
                bad.Add("legends: the export threw " + e);
            }
            return bad;
        }

        private static void Run(List<string> bad)
        {
            LivingWorldComponent comp = LivingWorldComponent.Instance;
            comp.RebuildIndexes(seedMissing: false);
            FactionState victim = null;
            foreach (FactionState fs in comp.factionStates)
                if (fs.faction != null && !fs.faction.defeated)
                {
                    victim = fs;
                    break;
                }
            if (victim == null)
            {
                Log.Message(Tag + "no faction to rename, not checked");
                return;
            }
            var nameField = AccessTools.Field(typeof(Faction), "name");
            string oldName = (string)nameField.GetValue(victim.faction);
            int lastEntryId = comp.chronicle.entries.Count > 0 ? comp.chronicle.entries[comp.chronicle.entries.Count - 1].id : 0;
            string dir = Path.Combine(Path.GetTempPath(), "lw-legends-" + Guid.NewGuid().ToString("N"));
            try
            {
                victim.faction.Name = Nasty;
                // A notable def: a minor one at the cap would prune the oldest minor entries for good, which the rollback cannot undo.
                comp.chronicle.Record(LWChronicleDefOf.LW_RulerVow, "The " + Nasty + " rose <b>boldly</b> & fell, as 'tis said.", victim.faction, silent: true);
                comp.chronicle.Record(LWChronicleDefOf.LW_RulerVow, "Tinted <color=#ff0000>Red</color>\nline2 of the tale.", victim.faction, silent: true);
                string baseBefore = LegendsExport.BaseName();
                LegendsExport.Result first = LegendsExport.Export(dir);
                string html = File.Exists(first.htmlPath) ? File.ReadAllText(first.htmlPath) : "";
                string text = File.Exists(first.textPath) ? File.ReadAllText(first.textPath) : "";
                if (html.Length == 0) bad.Add("legends: the HTML file is missing or empty: " + first.htmlPath);
                if (text.Length == 0) bad.Add("legends: the text file is missing or empty: " + first.textPath);
                if (!first.htmlPath.EndsWith(".html") || !first.textPath.EndsWith(".txt") || Path.GetFileNameWithoutExtension(first.htmlPath) != Path.GetFileNameWithoutExtension(first.textPath))
                    bad.Add($"legends: file names do not pair up: {first.htmlPath}, {first.textPath}");
                if (!Path.GetFileName(first.htmlPath).StartsWith(LegendsExport.FileStem() + "_")) bad.Add("legends: the file name does not start with the world's name: " + first.htmlPath);
                Names(bad, comp, html, text);
                Markup(bad, html);
                // Escaping: the name's markup is text in the page and in the text file it is as it was.
                string escaped = System.Net.WebUtility.HtmlEncode(Nasty);
                if (!html.Contains(escaped)) bad.Add("legends: the HTML does not hold the escaped name " + escaped);
                if (html.Contains("<Clan>") || html.Contains("<b>boldly</b>") || html.Contains("\"Sons\"")) bad.Add("legends: markup from game text reached the HTML unescaped");
                if (!html.Contains("&lt;b&gt;boldly&lt;/b&gt;")) bad.Add("legends: the chronicle entry's markup is not escaped in the HTML");
                if (Regex.IsMatch(html, "&(?!(#[0-9]+|#x[0-9a-fA-F]+|[a-zA-Z]+);)")) bad.Add("legends: a bare & in the HTML");
                if (!text.Contains(Nasty)) bad.Add("legends: the text file does not hold the name as it is");
                // Plain in the real thing: the colour tag is gone, the line break is a space.
                if (!html.Contains("Tinted Red line2 of the tale.") || html.Contains("color=") || !text.Contains("Tinted Red line2 of the tale.")) bad.Add("legends: a colour tag or line break of game text reached the files");
                if (!html.Contains($"id=\"f{victim.faction.loadID}\"")) bad.Add("legends: the renamed faction has no anchor of its own");
                // Self-contained: nothing that loads or runs.
                foreach (string forbidden in new[] { "<script", "<link", "<img", "<iframe", "src=", "@import", "url(" })
                    if (html.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) >= 0) bad.Add($"legends: the HTML has '{forbidden}' (not self-contained)");
                // Never overwrite: the second export lands beside the first, with a counter.
                byte[] firstHtml = File.ReadAllBytes(first.htmlPath), firstText = File.ReadAllBytes(first.textPath);
                LegendsExport.Result second = LegendsExport.Export(dir);
                bool sameDay = baseBefore == LegendsExport.BaseName(); // two exports straddling midnight get two dates, no counter
                if (second.htmlPath == first.htmlPath || second.textPath == first.textPath) bad.Add("legends: a second export reused the first one's file name");
                else if (!File.Exists(second.htmlPath) || !File.Exists(second.textPath)) bad.Add("legends: the second export's files are missing");
                else if (sameDay && !Path.GetFileNameWithoutExtension(second.htmlPath).EndsWith("_2")) bad.Add("legends: the second export has no counter: " + second.htmlPath);
                if (!File.Exists(first.htmlPath) || !File.ReadAllBytes(first.htmlPath).SequenceEqual(firstHtml)) bad.Add("legends: the first export's page was changed by the second");
                if (!File.Exists(first.textPath) || !File.ReadAllBytes(first.textPath).SequenceEqual(firstText)) bad.Add("legends: the first export's text was changed by the second");
                Failure(bad, dir);
                long ms = first.buildMs + first.writeMs;
                if (ms > BudgetMs) bad.Add($"legends: the export took {ms} ms (gathering {first.buildMs}, writing {first.writeMs}), more than {BudgetMs:F0} ms");
                Log.Message(Tag + $"{first.htmlPath} ({firstHtml.Length} bytes, {text.Length} chars of text), second {Path.GetFileName(second.htmlPath)}; {comp.chronicle.Count} chronicle entries, gathered in {first.buildMs} ms, written in {first.writeMs} ms (budget {BudgetMs:F0} ms)");
            }
            finally
            {
                nameField.SetValue(victim.faction, oldName);
                comp.chronicle.RollbackAfter(lastEntryId);
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
            if (victim.faction.Name == Nasty) bad.Add("legends: the test left the faction renamed");
        }

        /// <summary>Plain: colour tags (written by hand and by the game's own (*Name) markers) go, line breaks become spaces, other markup stays for the escaping to handle.</summary>
        private static void Plain(List<string> bad)
        {
            string resolved = new TaggedString("(*Name)Bob(/Name) rode").Resolve();
            (string input, string expected)[] cases =
            {
                (null, ""),
                ("", ""),
                ("<color=#ff0000>Red</color>\nline2", "Red line2"),
                ("<COLOR=red>Loud</COLOR>", "Loud"),
                ("a\r\nb\nc", "a b c"),
                ("  padded \n", "padded"),
                ("<b>bold</b> & <i>x</i>", "<b>bold</b> & <i>x</i>"),
                (resolved, "Bob rode"),
            };
            foreach ((string input, string expected) in cases)
            {
                string got = LegendsExport.Plain(input);
                if (got != expected) bad.Add($"legends: Plain({(input ?? "null").Replace("\n", "\\n").Replace("\r", "\\r")}) gave '{got}', expected '{expected}'");
            }
            if (resolved.IndexOf("(*", StringComparison.Ordinal) >= 0) bad.Add("legends: the game's own tag resolving left a marker: " + resolved);
        }

        /// <summary>FileStem against hostile world names: whatever comes in, a short, safe, non-empty name goes out.</summary>
        private static void Stems(List<string> bad)
        {
            string emoji = char.ConvertFromUtf32(0x1F600);
            (string input, string expected)[] cases =
            {
                ("../x", "x"),
                ("a/b\\c:d*?", "a_b_c_d"),
                (" .hidden. ", "hidden"),
                ("World Name", "World_Name"),
                ("CON.x", "_CON.x"),
                ("nul", "_nul"),
                ("Com1", "_Com1"),
                ("lpt\u00B2.tar.gz", "_lpt\u00B2.tar.gz"),
                ("Console", "Console"),
                ("", "World"),
                ("   ", "World"),
                (null, "World"),
                ("...", "World"),
                (new string('a', 80), new string('a', LegendsExport.MaxStem)),
                (new string('a', LegendsExport.MaxStem - 1) + emoji, new string('a', LegendsExport.MaxStem - 1)),
                (new string('b', LegendsExport.MaxStem - 2) + emoji, new string('b', LegendsExport.MaxStem - 2) + emoji),
                (new string('c', LegendsExport.MaxStem) + "...", new string('c', LegendsExport.MaxStem)),
            };
            foreach ((string input, string expected) in cases)
            {
                string got = LegendsExport.FileStem(input);
                if (got != expected) bad.Add($"legends: FileStem('{input}') gave '{got}', expected '{expected}'");
            }
            string[] hostile =
            {
                "a\u0001b\u001b[31mred\u007f", "tab\there\nnew", "a\uD83Db", "\uDE00z", "q\"<>|r", "NUL", "aux.txt", "COM9", "LPT\u00B3", "CONIN$", "..", "\\\\server\\share", "C:\\x", Nasty,
                new string('\u00E9', 200), new string('\u2028', 5), emoji + emoji + emoji,
            };
            foreach (string input in hostile)
            {
                string got = LegendsExport.FileStem(input);
                if (got.Length == 0) bad.Add($"legends: FileStem('{input}') is empty");
                if (got.Length > LegendsExport.MaxStem) bad.Add($"legends: FileStem('{input}') is {got.Length} long");
                if (got.StartsWith(".")) bad.Add($"legends: FileStem('{input}') starts with a dot: '{got}'");
                foreach (char c in got)
                    if (char.IsControl(c) || char.IsWhiteSpace(c) || "<>:\"/\\|?*".IndexOf(c) >= 0) bad.Add($"legends: FileStem('{input}') keeps the character U+{(int)c:X4}: '{got}'");
                for (int i = 0; i < got.Length; i++)
                    if (char.IsSurrogate(got[i]) && !(char.IsHighSurrogate(got[i]) && i + 1 < got.Length && char.IsLowSurrogate(got[i + 1])) && !(char.IsLowSurrogate(got[i]) && i > 0 && char.IsHighSurrogate(got[i - 1])))
                        bad.Add($"legends: FileStem('{input}') has a lone surrogate at {i}");
                string head = got.Split('.')[0].ToUpperInvariant();
                if (head == "CON" || head == "PRN" || head == "AUX" || head == "NUL" || head == "CONIN$" || head == "CONOUT$" || Regex.IsMatch(head, "^(COM|LPT)[0-9\u00B9\u00B2\u00B3]$"))
                    bad.Add($"legends: FileStem('{input}') is a Windows device name: '{got}'");
            }
        }

        /// <summary>A write that fails halfway: the text file's name is taken by a folder, so creating it fails with an error that is not "exists". The page written first must not stay behind alone.</summary>
        private static void Failure(List<string> bad, string dir)
        {
            string sub = Path.Combine(dir, "failure");
            Directory.CreateDirectory(sub);
            Directory.CreateDirectory(Path.Combine(sub, LegendsExport.BaseName() + ".txt"));
            try
            {
                LegendsExport.Export(sub);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
            }
            string[] files = Directory.GetFiles(sub);
            foreach (string html in Directory.GetFiles(sub, "*.html"))
                if (!File.Exists(Path.ChangeExtension(html, ".txt"))) bad.Add("legends: a failed export left a page without its text file: " + html);
            if (files.Length > 2) bad.Add($"legends: a failed export left {files.Length} files");
        }

        /// <summary>Every faction of the simulation (the renamed one too) is in both files.</summary>
        private static void Names(List<string> bad, LivingWorldComponent comp, string html, string text)
        {
            int n = 0;
            foreach (FactionState fs in comp.factionStates)
            {
                if (fs.faction == null) continue;
                n++;
                string name = LegendsExport.Plain(fs.faction.Name);
                if (!html.Contains(System.Net.WebUtility.HtmlEncode(name))) bad.Add($"legends: faction '{name}' is not in the HTML");
                if (!text.Contains(name)) bad.Add($"legends: faction '{name}' is not in the text file");
            }
            string colony = LegendsExport.Plain(Faction.OfPlayer.Name);
            if (!html.Contains(System.Net.WebUtility.HtmlEncode(colony))) bad.Add($"legends: the colony '{colony}' is not in the HTML");
            if (n == 0) bad.Add("legends: no faction to look for");
        }

        /// <summary>Valid-ish HTML: a doctype, every paired tag closed as often as it is opened, the six sections with their anchors, the text file with its six headings.</summary>
        private static void Markup(List<string> bad, string html)
        {
            if (!html.StartsWith("<!DOCTYPE html>")) bad.Add("legends: no doctype");
            foreach (string tag in Paired)
            {
                int open = Regex.Matches(html, "<" + tag + "[ >]").Count, close = Regex.Matches(html, "</" + tag + ">").Count;
                if (open != close || open == 0) bad.Add($"legends: <{tag}> opened {open} times, closed {close}");
            }
            if (Regex.Matches(html, "<section[ >]").Count != SectionIds.Length) bad.Add($"legends: expected {SectionIds.Length} sections");
            foreach (string id in SectionIds)
                if (!html.Contains($"<section id=\"{id}\">") || !html.Contains($"href=\"#{id}\"")) bad.Add($"legends: section '{id}' or its link is missing");
            // Anchors: ids are the section names or a letter and a number; ids are unique; every link goes to an id on the page.
            var ids = new HashSet<string>();
            foreach (Match m in Regex.Matches(html, "\\sid=\"([^\"]*)\""))
            {
                string id = m.Groups[1].Value;
                if (!ids.Add(id)) bad.Add($"legends: the id '{id}' is used twice");
                if (Array.IndexOf(SectionIds, id) < 0 && !Regex.IsMatch(id, "^[fth][0-9]+(x[0-9]+)?$")) bad.Add($"legends: the id '{id}' is not a fixed one");
            }
            foreach (Match m in Regex.Matches(html, "href=\"#([^\"]*)\""))
                if (!ids.Contains(m.Groups[1].Value)) bad.Add($"legends: a link to '{m.Groups[1].Value}', which is not on the page");
            // Every angle bracket that is left is a tag we wrote: after dropping tags, none remain.
            string rest = Regex.Replace(html, "<[!/]?[a-zA-Z][^<>]*>", "");
            if (rest.IndexOf('<') >= 0 || rest.IndexOf('>') >= 0) bad.Add("legends: a stray angle bracket outside the tags");
        }
    }
}
