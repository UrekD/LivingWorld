// Offline def linter: checks the mod's XML defs against the game's real types (via reflection) without running
// the game. Reports unknown fields, unknown classes, and references to defs that exist nowhere (game or mod).
// Usage: dotnet run --project tools/deflint -- <RimWorldDir> <ModDir>
using System.Reflection;
using System.Runtime.Loader;
using System.Xml.Linq;

string gameDir = Path.GetFullPath(args[0]), modDir = Path.GetFullPath(args[1]);
var alc = new AssemblyLoadContext("lint", isCollectible: false);
alc.Resolving += (ctx, name) =>
{
    foreach (string dir in new[] { gameDir, Path.Combine(modDir, "Assemblies"), Path.Combine(gameDir, "Mods/Harmony/1.7/Assemblies") })
    {
        string p = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
    }
    if (name.Name!.StartsWith("UnityEngine")) return ctx.LoadFromAssemblyPath(Path.Combine(gameDir, "UnityStubs.dll"));
    return null;
};
Assembly game = alc.LoadFromAssemblyPath(Path.Combine(gameDir, "Assembly-CSharp.dll"));
Assembly mod = alc.LoadFromAssemblyPath(Path.Combine(modDir, "Assemblies/LivingWorld.dll"));
var allTypes = new List<Type>();
foreach (Assembly a in new[] { game, mod })
{
    try { allTypes.AddRange(a.GetTypes()); }
    catch (ReflectionTypeLoadException e) { allTypes.AddRange(e.Types.Where(t => t != null)!); }
}
Type defType = game.GetType("Verse.Def")!;
if (Environment.GetEnvironmentVariable("DEFLINT_DEBUG") != null)
    Console.WriteLine($"types: {allTypes.Count}, mod types: {allTypes.Count(t => t.Namespace == "LivingWorld")}");
Type FindType(string name)
{
    foreach (Type t in allTypes) if (t.FullName == name) return t;
    foreach (string ns in new[] { "Verse.", "RimWorld.", "Verse.AI.", "Verse.AI.Group.", "RimWorld.Planet.", "LivingWorld." })
        foreach (Type t in allTypes) if (t.FullName == ns + name) return t;
    foreach (Type t in allTypes) if (t.Name == name) return t;
    return null;
}

// All defNames by def root type name (game + DLC + mod), for cross-reference checks.
var defNames = new Dictionary<string, HashSet<string>>();
var abstractNames = new HashSet<string>();
void Index(string dir)
{
    if (!Directory.Exists(dir)) return;
    foreach (string f in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
    {
        XDocument doc;
        try { doc = XDocument.Load(f); } catch { continue; }
        if (doc.Root?.Name.LocalName != "Defs") continue;
        foreach (XElement e in doc.Root.Elements())
        {
            string dn = e.Element("defName")?.Value;
            if (e.Attribute("Name") != null) abstractNames.Add(e.Attribute("Name")!.Value);
            if (dn == null) continue;
            Type t = FindType(e.Name.LocalName);
            for (Type b = t; b != null && b != defType; b = b.BaseType)
            {
                if (!defNames.TryGetValue(b.Name, out var set)) defNames[b.Name] = set = new HashSet<string>();
                set.Add(dn);
            }
            if (t == null) { if (!defNames.TryGetValue(e.Name.LocalName, out var s2)) defNames[e.Name.LocalName] = s2 = new(); s2.Add(dn); }
        }
    }
}
foreach (string d in Directory.GetDirectories(Path.Combine(gameDir, "Data"))) Index(Path.Combine(d, "Defs"));
Index(Path.Combine(modDir, "Defs"));
// Implied defs that are generated at runtime (meat, corpses, blueprints, frames).
bool Implied(string name) => name.StartsWith("Meat_") || name.StartsWith("Corpse_") || name.StartsWith("Blueprint_") || name.StartsWith("Frame_") || name.StartsWith("Minified");

int problems = 0;
void Report(string file, XElement e, string msg)
{
    problems++;
    Console.WriteLine($"{Path.GetFileName(file)}:{((System.Xml.IXmlLineInfo)e).LineNumber}: {msg}");
}

FieldInfo FindField(Type t, string name)
{
    for (Type b = t; b != null; b = b.BaseType)
    {
        FieldInfo f = b.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (f != null) return f;
        foreach (FieldInfo af in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            foreach (CustomAttributeData a in af.CustomAttributes)
                if (a.AttributeType.Name == "LoadAliasAttribute" && a.ConstructorArguments.Count > 0 && (string)a.ConstructorArguments[0].Value == name) return af;
    }
    return null;
}

bool IsDef(Type t) => t != null && defType.IsAssignableFrom(t);

void CheckValue(string file, XElement e, Type t)
{
    if (t == null) return;
    var classAttr = e.Attribute("Class")?.Value;
    if (classAttr != null)
    {
        Type ct = FindType(classAttr);
        if (ct == null) { Report(file, e, $"unknown Class '{classAttr}'"); return; }
        t = ct;
    }
    if (e.Attribute("IsNull")?.Value == "True") return;
    if (IsDef(t))
    {
        if (!e.HasElements)
        {
            string v = e.Value.Trim();
            if (v.Length > 0 && !Implied(v) && !(defNames.TryGetValue(t.Name, out var set) && set.Contains(v)))
            {
                bool anywhere = defNames.Values.Any(s => s.Contains(v));
                Report(file, e, $"{t.Name} '{v}' not found{(anywhere ? " (exists as another def type)" : "")}");
            }
        }
        return;
    }
    if (t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("List"))
    {
        Type it = t.GetGenericArguments()[0];
        foreach (XElement li in e.Elements())
        {
            if (li.Name.LocalName != "li") { if (!IsDef(it) ) { } continue; }
            CheckValue(file, li, it);
        }
        return;
    }
    if (t.IsGenericType && t.GetGenericTypeDefinition().Name.StartsWith("Dictionary"))
    {
        Type kt = t.GetGenericArguments()[0];
        if (IsDef(kt))
            foreach (XElement kv in e.Elements())
                if (kv.Name.LocalName != "li" && !(defNames.TryGetValue(kt.Name, out var set) && set.Contains(kv.Name.LocalName)))
                    Report(file, kv, $"dictionary key {kt.Name} '{kv.Name.LocalName}' not found");
        return;
    }
    if (t.IsEnum && !e.HasElements)
    {
        // Flags enums take "A, B"; names are case-sensitive in RimWorld's ParseHelper.
        string[] names = t.GetEnumNames();
        foreach (string part in e.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (!names.Contains(part) && !long.TryParse(part, out _)) Report(file, e, $"{t.Name} has no value '{part}'");
        return;
    }
    if (t.Name == "Boolean" && !e.HasElements)
    {
        if (!bool.TryParse(e.Value.Trim(), out _)) Report(file, e, $"not a bool: '{e.Value.Trim()}'");
        return;
    }
    if (t.IsPrimitive || t.IsEnum || t == typeof(string) || !e.HasElements) return;
    if (t.FullName!.StartsWith("UnityEngine") || t.Namespace == "System") return;
    CheckFields(file, e, t);
}

void CheckFields(string file, XElement e, Type t)
{
    // Custom loaders (LoadDataFromXmlCustom) take arbitrary shapes.
    if (t.GetMethod("LoadDataFromXmlCustom", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null) return;
    foreach (XElement child in e.Elements())
    {
        if (child.Name.LocalName == "li") continue;
        FieldInfo f = FindField(t, child.Name.LocalName);
        if (f == null)
        {
            if (child.Attribute("IgnoreIfNoMatchingField")?.Value == "True") continue;
            if (child.Attribute("MayRequire") != null) continue;
            Report(file, child, $"{t.Name} has no field '{child.Name.LocalName}'");
            continue;
        }
        CheckValue(file, child, f.FieldType);
    }
}

foreach (string f in Directory.EnumerateFiles(Path.Combine(modDir, "Defs"), "*.xml", SearchOption.AllDirectories))
{
    XDocument doc = XDocument.Load(f, LoadOptions.SetLineInfo);
    foreach (XElement e in doc.Root!.Elements())
    {
        Type t = FindType(e.Name.LocalName);
        if (t == null) { Report(f, e, $"unknown def type '{e.Name.LocalName}'"); continue; }
        string parent = e.Attribute("ParentName")?.Value;
        if (parent != null && !abstractNames.Contains(parent)) Report(f, e, $"unknown ParentName '{parent}'");
        CheckFields(f, e, t);
    }
}
// Patches: xpaths must at least name existing defs.
string patchDir = Path.Combine(modDir, "Patches");
if (Directory.Exists(patchDir))
    foreach (string f in Directory.EnumerateFiles(patchDir, "*.xml", SearchOption.AllDirectories))
        foreach (XElement op in XDocument.Load(f, LoadOptions.SetLineInfo).Root!.Elements())
        {
            string xp = op.Element("xpath")?.Value ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(xp, "defName=\"([^\"]+)\"");
            if (m.Success && !defNames.Values.Any(s => s.Contains(m.Groups[1].Value))) Report(f, op, $"patch targets unknown def '{m.Groups[1].Value}'");
        }
Console.WriteLine(problems == 0 ? "deflint: OK" : $"deflint: {problems} problem(s)");
return problems == 0 ? 0 : 1;
