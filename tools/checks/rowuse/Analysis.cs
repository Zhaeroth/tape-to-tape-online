// The comparison between the plugin's use of the game's members and the hook lists: every member the plugin refers to has a
// row, and every row of hooks.txt and menus_initdata.txt is used. The judgment calls are rules here, each with its reason;
// what a rule can't decide goes in the allow file with its own reason (rowuse_allow.txt).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>Where a hook row comes from, which decides how the check treats it.</summary>
internal enum RowKind
{
    /// <summary>A row of hooks.txt or menus_initdata.txt: patched by name, or called, read or written by the plugin's code. Must be used.</summary>
    Own,

    /// <summary>A row of clientmode.txt: the plugin reads the list at run time and patches or disables each row, so the code never names it.</summary>
    ClientMode,

    /// <summary>An event row of presentation.txt (a bus and an event: 'GameplayEvents.OnGamePause'): read by the plugin as a list at run time.</summary>
    PresentationEvent,
}

internal sealed class Row
{
    public Row(string file, int line, RowKind kind, string typeName, string member, string comment)
    {
        File = file;
        Line = line;
        Kind = kind;
        TypeName = typeName;
        Member = member;
        Comment = comment;
    }

    public string File { get; }

    public int Line { get; }

    public RowKind Kind { get; }

    public string TypeName { get; }

    public string Member { get; }

    public string Comment { get; }

    /// <summary>The interop type the row resolves to by the start-up check's rules, or null.</summary>
    public Type? Type { get; set; }

    /// <summary>The row as the lists write it.</summary>
    public string Text => TypeName + "::" + Member;
}

/// <summary>What the built plugin refers to: the game's members and types it names, and every string it holds.</summary>
internal sealed class UseData
{
    /// <summary>Member references into the interop assemblies: (the interop type's full name, the member's name) to a count.</summary>
    public Dictionary<(string Type, string Member), int> Uses { get; } = new();

    /// <summary>Full names of the interop types the plugin refers to at all.</summary>
    public HashSet<string> TypeRefs { get; } = new(StringComparer.Ordinal);

    /// <summary>String literals of the plugin, Core and the Steam library (names passed to Harmony, to icall lookups, to <c>nameof</c>).</summary>
    public HashSet<string> Strings { get; } = new(StringComparer.Ordinal);

    public UseData Copy()
    {
        var copy = new UseData();
        foreach (KeyValuePair<(string Type, string Member), int> use in Uses)
        {
            copy.Uses[use.Key] = use.Value;
        }

        copy.TypeRefs.UnionWith(TypeRefs);
        copy.Strings.UnionWith(Strings);
        return copy;
    }
}

/// <summary>One line of the allow file: a row kept on purpose, or a use with no row on purpose.</summary>
internal sealed class AllowEntry
{
    public AllowEntry(string kind, string key, string reason, int line)
    {
        Kind = kind;
        Key = key;
        Reason = reason;
        Line = line;
    }

    /// <summary>"row" (a hook row that nothing uses) or "use" (a game member the plugin uses that no row lists).</summary>
    public string Kind { get; }

    /// <summary>'Namespace.Type::Member' as the hook list or the report writes it.</summary>
    public string Key { get; }

    public string Reason { get; }

    public int Line { get; }
}

internal sealed class Report
{
    public List<string> Unlisted { get; } = new();

    public List<string> Unused { get; } = new();

    public List<string> Stale { get; } = new();

    /// <summary>The unlisted uses as (interop type full name, member).</summary>
    public HashSet<(string Type, string Member)> UnlistedKeys { get; } = new();

    /// <summary>The rows reported as unused, as the lists write them.</summary>
    public HashSet<string> UnusedKeys { get; } = new(StringComparer.Ordinal);

    public int RowCount { get; set; }

    public int RowsInScope { get; set; }

    public int UseCount { get; set; }

    public int StringCount { get; set; }

    public int AllowedRows { get; set; }

    /// <summary>True when there is nothing to judge (no member reference at all, or no row): a pass on that would be empty.</summary>
    public bool ReadNothing { get; set; }

    public bool Clean => Unlisted.Count == 0 && Unused.Count == 0 && Stale.Count == 0 && !ReadNothing;
}

internal static class Analyzer
{
    private static readonly Regex BackingField = new(@"^_(.+)_k__BackingField$", RegexOptions.Compiled);

    /// <summary>
    /// Members of the interop that are not the game's: the plugin refers to them, no hook row can list them. Each is an
    /// interop proxy's own machinery.
    /// </summary>
    public static readonly (string Type, string Member, string Why)[] Glue =
    {
        ("Il2CppSystem.Object", "GetIl2CppType", "the interop's own type lookup (an IL2CPP class pointer), not a game member"),
        ("Il2CppSystem.Object", "op_Implicit", "the interop's conversion between a managed and an IL2CPP string or object, not a game member"),
    };

    /// <summary>
    /// The names a hook row may carry for a use of <paramref name="member"/>: the member itself, a property's name for its
    /// accessor, and the property for its compiler-made backing field (the interop names it <c>_Name_k__BackingField</c>;
    /// the row lists the property, and its comment says the code reads it through the field).
    /// </summary>
    public static IEnumerable<string> NamesOf(string member)
    {
        yield return member;
        string stripped = member.StartsWith("get_", StringComparison.Ordinal) || member.StartsWith("set_", StringComparison.Ordinal) ? member[4..] : member;
        if (stripped != member)
        {
            yield return stripped;
        }

        Match field = BackingField.Match(stripped);
        if (field.Success)
        {
            yield return field.Groups[1].Value;
        }
    }

    /// <summary>The name a report and the allow file give an interop type: its full name without the interop's prefix.</summary>
    public static string Display(string interopTypeName) =>
        interopTypeName.StartsWith("Il2Cpp", StringComparison.Ordinal) && interopTypeName.Length > 6 ? interopTypeName[6..].TrimStart('.') : interopTypeName;

    /// <summary>
    /// The hook rows that list a use of <paramref name="member"/> on <paramref name="type"/>: a row on the type or one of its
    /// bases (a row names the member where the code reaches it), or on a type derived from it (a use through a base).
    /// </summary>
    public static List<Row> Listing(IReadOnlyList<Row> rows, Dictionary<(Type, string), List<Row>> byTarget, Type? type, string member)
    {
        var hits = new List<Row>();
        if (type == null)
        {
            return hits;
        }

        string[] names = NamesOf(member).ToArray();
        for (Type? current = type; current != null; current = current.BaseType)
        {
            foreach (string name in names)
            {
                if (byTarget.TryGetValue((current, name), out List<Row>? found))
                {
                    hits.AddRange(found);
                }
            }
        }

        if (hits.Count == 0)
        {
            foreach (Row row in rows)
            {
                if (row.Type != null && row.Type.IsSubclassOf(type) && names.Contains(row.Member))
                {
                    hits.Add(row);
                }
            }
        }

        return hits;
    }

    public static Dictionary<(Type, string), List<Row>> Index(IReadOnlyList<Row> rows)
    {
        var byTarget = new Dictionary<(Type, string), List<Row>>();
        foreach (Row row in rows)
        {
            for (Type? type = row.Type; type != null; type = type.BaseType)
            {
                if (!byTarget.TryGetValue((type, row.Member), out List<Row>? list))
                {
                    byTarget[(type, row.Member)] = list = new List<Row>();
                }

                list.Add(row);
            }
        }

        return byTarget;
    }

    /// <summary>
    /// True when a string literal of the plugin names the member: a Harmony patch by name, <c>nameof</c>, an icall by its
    /// 'Type::get_Name', or the compiler's backing field looked up by name at run time ('&lt;Name&gt;k__BackingField'). The
    /// member's name alone is enough (a literal equal to it, or ending in '::Name' or '.Name'), so a common name such as
    /// 'Update' counts as named wherever any string is 'Update': the check can't tell which call a string reaches. Never asked
    /// of a constructor row (<see cref="Constructed"/> judges those).
    /// </summary>
    public static bool NamedByString(Row row, HashSet<string> strings)
    {
        string name = row.Member;
        string bare = name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal) ? name[4..] : name;
        string[] forms = { name, "get_" + bare, "set_" + bare, bare };
        if (forms.Any(strings.Contains) || strings.Contains("<" + bare + ">k__BackingField"))
        {
            return true;
        }

        return strings.Any(s => s.Length < 200 && forms.Any(f => s.EndsWith("::" + f, StringComparison.Ordinal) || s.EndsWith("." + f, StringComparison.Ordinal)));
    }

    /// <summary>
    /// True when the plugin constructs the row's type: a constructor reference to it. A constructor row is judged by this alone,
    /// never by a string literal, because the literal ".ctor" (a reflection lookup of any constructor) would name every
    /// constructor row at once and a stale one would pass. A row kept only so each game build is checked for the type is
    /// listed in the allow file with that reason.
    /// </summary>
    private static bool Constructed(Row row, UseData data) =>
        row.Type?.FullName is { } typeName && data.Uses.ContainsKey((typeName, ".ctor"));

    /// <summary>A row of hooks.txt or menus_initdata.txt that clientmode.txt or presentation.txt lists too (a hand-picked row kept beside the generated list).</summary>
    private static bool ListedAlso(Row row, IReadOnlyList<Row> rows) =>
        rows.Any(other => other.Kind != RowKind.Own && other.Member == row.Member
            && (other.Kind == RowKind.PresentationEvent
                ? row.TypeName == other.TypeName || row.TypeName.EndsWith("." + other.TypeName, StringComparison.Ordinal)
                : other.Type != null && other.Type == row.Type || other.TypeName == row.TypeName));

    public static Report Analyze(IReadOnlyList<Row> rows, UseData data, IReadOnlyList<AllowEntry> allow)
    {
        var report = new Report
        {
            RowCount = rows.Count,
            RowsInScope = rows.Count(r => r.Kind == RowKind.Own),
            UseCount = data.Uses.Count,
            StringCount = data.Strings.Count,
        };
        if (data.Uses.Count == 0 || rows.Count == 0)
        {
            report.ReadNothing = true;
            return report;
        }

        Dictionary<(Type, string), List<Row>> byTarget = Index(rows);
        var usedRows = new HashSet<Row>();
        var unlisted = new List<((string Type, string Member) Key, int Count)>();
        foreach (KeyValuePair<(string Type, string Member), int> use in data.Uses.OrderBy(u => u.Key.Type, StringComparer.Ordinal).ThenBy(u => u.Key.Member, StringComparer.Ordinal))
        {
            (string typeName, string member) = use.Key;
            if (member is ".ctor" or ".cctor" || Glue.Any(g => g.Type == typeName && g.Member == member))
            {
                continue;
            }

            Type? type = Types.Index.GetValueOrDefault(typeName);
            List<Row> listing = Listing(rows, byTarget, type, member);
            if (listing.Count == 0)
            {
                unlisted.Add((use.Key, use.Value));
            }
            else
            {
                usedRows.UnionWith(listing);
            }
        }

        var allowedUses = new HashSet<string>(allow.Where(a => a.Kind == "use").Select(a => a.Key), StringComparer.Ordinal);
        var usedAllowedUses = new HashSet<string>(StringComparer.Ordinal);
        foreach (((string typeName, string member), int count) in unlisted)
        {
            string key = Display(typeName) + "::" + member;
            if (allowedUses.Contains(key))
            {
                usedAllowedUses.Add(key);
                continue;
            }

            report.UnlistedKeys.Add((typeName, member));
            report.Unlisted.Add($"UNLISTED  {key}  ({count} use(s)): the plugin uses it and no hook row lists it");
        }

        var allowedRows = new HashSet<string>(allow.Where(a => a.Kind == "row").Select(a => a.Key), StringComparer.Ordinal);
        var unusedWithoutAllow = new HashSet<string>(StringComparer.Ordinal);
        var usedAllowedRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (Row row in rows.Where(r => r.Kind == RowKind.Own))
        {
            if (row.Member == ".ctor" ? Constructed(row, data) : (usedRows.Contains(row) || NamedByString(row, data.Strings)))
            {
                continue;
            }

            // A member of an enum is a constant in the plugin's code (no member reference), listed for the start-up check: used when the plugin names the enum.
            if (row.Type is { IsEnum: true } && data.TypeRefs.Contains(row.Type.FullName!))
            {
                continue;
            }

            if (ListedAlso(row, rows))
            {
                continue;
            }

            unusedWithoutAllow.Add(row.Text);
            if (allowedRows.Contains(row.Text))
            {
                usedAllowedRows.Add(row.Text);
                report.AllowedRows++;
                continue;
            }

            report.UnusedKeys.Add(row.Text);
            report.Unused.Add($"UNUSED    {row.File}:{row.Line}  {row.Text}  : nothing in the plugin uses it (remove the row, or keep it with a reason in the allow file)");
        }

        foreach (AllowEntry entry in allow)
        {
            if (entry.Kind == "row" && !unusedWithoutAllow.Contains(entry.Key))
            {
                report.Stale.Add($"STALE     allow file line {entry.Line}: row {entry.Key} is used now, or is not in the lists: remove the entry");
            }
            else if (entry.Kind == "use" && !usedAllowedUses.Contains(entry.Key))
            {
                report.Stale.Add($"STALE     allow file line {entry.Line}: use {entry.Key} has a row now, or the plugin no longer uses it: remove the entry");
            }
        }

        return report;
    }
}

/// <summary>The interop types by full name, loaded once (the analysis resolves a use's type through it).</summary>
internal static class Types
{
    public static Dictionary<string, Type> Index { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The type a row names, by the start-up check's rules ("Il2Cpp" prefixes, nested types with a dot).</summary>
    public static Type? Find(string name)
    {
        foreach (string candidate in new[] { "Il2Cpp" + name, "Il2Cpp." + name, name })
        {
            if (Index.TryGetValue(candidate, out Type? type))
            {
                return type;
            }

            string nested = candidate;
            for (int depth = 0; depth < 3; depth++)
            {
                int dot = nested.LastIndexOf('.');
                if (dot <= 0)
                {
                    break;
                }

                nested = string.Concat(nested.AsSpan(0, dot), "+", nested.AsSpan(dot + 1));
                if (Index.TryGetValue(nested, out type))
                {
                    return type;
                }
            }
        }

        return null;
    }
}
