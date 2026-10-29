using System.Collections.Generic;
using System.IO;
using System.Net;
using Verse;

namespace LivingWorld
{
    /// <summary>A link to another part of the page: <c>anchor</c> is a fixed id (never game text), <c>label</c> the game text shown.</summary>
    public class LegendLink
    {
        public string label;
        public string anchor;
    }

    /// <summary>A bullet of the legends, with optional sub-bullets. <c>weight</c> is the chronicle importance (0 minor, 1 notable, 2 major).</summary>
    public class LegendItem
    {
        public string text;
        public int weight;
        /// <summary>Fixed id the page can link to (null = none; never game text).</summary>
        public string anchor;
        public List<string> notes = new List<string>();
        public readonly List<LegendLink> links = new List<LegendLink>();
    }

    /// <summary>A titled part of a section: lead lines, then bullets.</summary>
    public class LegendBlock
    {
        public string heading;
        /// <summary>Fixed id the page can link to (null = none; never game text).</summary>
        public string anchor;
        public readonly List<string> lines = new List<string>();
        public readonly List<LegendLink> links = new List<LegendLink>();
        public readonly List<LegendItem> items = new List<LegendItem>();

        public LegendItem Add(string text, int weight = 0)
        {
            var item = new LegendItem { text = text, weight = weight };
            items.Add(item);
            return item;
        }
    }

    public class LegendSection
    {
        /// <summary>Fixed anchor (never game text).</summary>
        public string id;
        public string heading;
        public readonly List<LegendBlock> blocks = new List<LegendBlock>();

        public LegendBlock Block(string heading, string anchor = null)
        {
            var b = new LegendBlock { heading = heading, anchor = anchor };
            blocks.Add(b);
            return b;
        }
    }

    /// <summary>
    /// The legends as plain data: every string is game text, in the order it will be read. The two renderers are the only places that
    /// turn it into a file, and the HTML one escapes every string it writes. Both write straight into a <see cref="TextWriter"/>.
    /// </summary>
    public class LegendDocument
    {
        public string title;
        public string subtitle;
        /// <summary>The lead of a row of links ("See also:").</summary>
        public string seeAlso;
        public readonly List<LegendSection> sections = new List<LegendSection>();

        public LegendSection Section(string id, string heading)
        {
            var s = new LegendSection { id = id, heading = heading };
            sections.Add(s);
            return s;
        }

        public string ToText()
        {
            var w = new StringWriter();
            ToText(w);
            return w.ToString();
        }

        public void ToText(TextWriter w)
        {
            w.Write(title);
            w.Write('\n');
            w.Write(new string('=', Width(title)));
            w.Write('\n');
            if (!subtitle.NullOrEmpty())
            {
                w.Write(subtitle);
                w.Write('\n');
            }
            foreach (LegendSection s in sections)
            {
                w.Write("\n\n");
                w.Write(s.heading);
                w.Write('\n');
                w.Write(new string('-', Width(s.heading)));
                w.Write('\n');
                foreach (LegendBlock b in s.blocks)
                {
                    w.Write('\n');
                    if (!b.heading.NullOrEmpty())
                    {
                        w.Write(b.heading);
                        w.Write('\n');
                    }
                    foreach (string line in b.lines)
                    {
                        w.Write(line);
                        w.Write('\n');
                    }
                    foreach (LegendItem item in b.items)
                    {
                        w.Write(item.weight >= 2 ? "  * " : "  - ");
                        w.Write(item.text);
                        w.Write('\n');
                        foreach (string note in item.notes)
                        {
                            w.Write("      ");
                            w.Write(note);
                            w.Write('\n');
                        }
                    }
                }
            }
        }

        private static int Width(string s) => System.Math.Max(3, System.Math.Min(s?.Length ?? 0, 100));

        public string ToHtml()
        {
            var w = new StringWriter();
            ToHtml(w);
            return w.ToString();
        }

        /// <summary>One self-contained page: inline style, no scripts, no links out (only to its own anchors). Every game string goes through <see cref="E"/>; attribute values are fixed ids and ints.</summary>
        public void ToHtml(TextWriter w)
        {
            var ids = new HashSet<string>();
            foreach (LegendSection s in sections)
            {
                ids.Add(s.id);
                foreach (LegendBlock b in s.blocks)
                {
                    if (b.anchor != null) ids.Add(b.anchor);
                    foreach (LegendItem item in b.items)
                        if (item.anchor != null) ids.Add(item.anchor);
                }
            }
            w.Write("<!DOCTYPE html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n");
            w.Write("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            w.Write("<title>");
            w.Write(E(title));
            w.Write("</title>\n<style>\n");
            w.Write(Css);
            w.Write("</style>\n</head>\n<body>\n");
            w.Write("<header>\n<h1>");
            w.Write(E(title));
            w.Write("</h1>\n");
            if (!subtitle.NullOrEmpty())
            {
                w.Write("<p class=\"sub\">");
                w.Write(E(subtitle));
                w.Write("</p>\n");
            }
            w.Write("</header>\n<nav>\n<ul>\n");
            foreach (LegendSection s in sections)
            {
                w.Write("<li><a href=\"#");
                w.Write(E(s.id));
                w.Write("\">");
                w.Write(E(s.heading));
                w.Write("</a></li>\n");
            }
            w.Write("</ul>\n</nav>\n<main>\n");
            foreach (LegendSection s in sections)
            {
                w.Write("<section id=\"");
                w.Write(E(s.id));
                w.Write("\">\n<h2>");
                w.Write(E(s.heading));
                w.Write("</h2>\n");
                foreach (LegendBlock b in s.blocks)
                {
                    w.Write("<div class=\"block\"");
                    WriteId(w, b.anchor);
                    w.Write(">\n");
                    if (!b.heading.NullOrEmpty())
                    {
                        w.Write("<h3>");
                        w.Write(E(b.heading));
                        w.Write("</h3>\n");
                    }
                    foreach (string line in b.lines)
                    {
                        w.Write("<p>");
                        w.Write(E(line));
                        w.Write("</p>\n");
                    }
                    WriteLinks(w, b.links, ids, "<p class=\"see\">", "</p>\n");
                    if (b.items.Count > 0)
                    {
                        w.Write("<ul>\n");
                        foreach (LegendItem item in b.items)
                        {
                            w.Write("<li class=\"w");
                            w.Write(item.weight < 0 ? 0 : item.weight > 2 ? 2 : item.weight);
                            w.Write('"');
                            WriteId(w, item.anchor);
                            w.Write('>');
                            w.Write(E(item.text));
                            WriteLinks(w, item.links, ids, " <span class=\"see\">", "</span>");
                            if (item.notes.Count > 0)
                            {
                                w.Write("\n<ul>\n");
                                foreach (string note in item.notes)
                                {
                                    w.Write("<li>");
                                    w.Write(E(note));
                                    w.Write("</li>\n");
                                }
                                w.Write("</ul>\n");
                            }
                            w.Write("</li>\n");
                        }
                        w.Write("</ul>\n");
                    }
                    w.Write("</div>\n");
                }
                w.Write("</section>\n");
            }
            w.Write("</main>\n</body>\n</html>\n");
        }

        private static void WriteId(TextWriter w, string anchor)
        {
            if (anchor == null) return;
            w.Write(" id=\"");
            w.Write(E(anchor));
            w.Write('"');
        }

        /// <summary>A row of links to anchors that exist on this page (a link to nothing is left out); false when none was written.</summary>
        private bool WriteLinks(TextWriter w, List<LegendLink> links, HashSet<string> ids, string open, string close)
        {
            bool any = false;
            foreach (LegendLink l in links)
            {
                if (l.anchor == null || !ids.Contains(l.anchor)) continue;
                if (!any)
                {
                    w.Write(open);
                    w.Write(E(seeAlso));
                    any = true;
                }
                w.Write(" <a href=\"#");
                w.Write(E(l.anchor));
                w.Write("\">");
                w.Write(E(l.label));
                w.Write("</a>");
            }
            if (any) w.Write(close);
            return any;
        }

        /// <summary>HTML-encoded text: &lt; &gt; &amp; and both quotes become entities.</summary>
        public static string E(string s) => WebUtility.HtmlEncode(s ?? "");

        private const string Css =
            "body { margin: 0 auto; padding: 1.5em; max-width: 56em; background: #f4ecd8; color: #2b2217; font-family: Georgia, 'Times New Roman', serif; line-height: 1.45; }\n" +
            "h1 { margin: 0; font-size: 2.2em; border-bottom: 3px double #7a5c2e; }\n" +
            "h2 { margin-top: 1.8em; font-size: 1.6em; border-bottom: 1px solid #7a5c2e; color: #5a3d14; }\n" +
            "h3 { margin: 0.2em 0; font-size: 1.15em; color: #4a3210; }\n" +
            ".sub { margin: 0.4em 0 1em 0; font-style: italic; color: #6b5a3c; }\n" +
            "nav ul { padding: 0; list-style: none; }\n" +
            "nav li { display: inline-block; margin-right: 1.2em; }\n" +
            "a { color: #7a3b14; }\n" +
            ".see { font-size: 0.9em; color: #6b5a3c; }\n" +
            ".see a { margin-right: 0.6em; }\n" +
            ".block { margin: 0.9em 0; padding: 0.4em 0.8em; background: #fbf6e8; border-left: 4px solid #b39358; }\n" +
            ".block p { margin: 0.15em 0; }\n" +
            ".block ul { margin: 0.3em 0; padding-left: 1.4em; }\n" +
            "li.w1 { font-weight: bold; }\n" +
            "li.w2 { font-weight: bold; color: #8a2b0a; }\n" +
            "li li { font-weight: normal; color: #4a3f2c; }\n" +
            "@media print { body { background: #ffffff; } .block { background: #ffffff; } }\n";
    }
}
