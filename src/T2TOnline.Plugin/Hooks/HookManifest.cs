using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace T2TOnline.Plugin.Hooks;

/// <summary>
/// The list of game members the mod touches, read from the embedded hooks.txt (the same file the offline
/// hookcheck.py tool checks against each new game build). At startup every entry is resolved against the running
/// game; any missing entry keeps online mode off.
/// </summary>
internal sealed class HookManifest
{
    private HookManifest(IReadOnlyList<HookEntry> entries) => Entries = entries;

    /// <summary>Every entry, in file order.</summary>
    public IReadOnlyList<HookEntry> Entries { get; }

    /// <summary>Loads hooks.txt, menus_initdata.txt and clientmode.txt from the mod assembly.</summary>
    public static HookManifest LoadEmbedded()
    {
        var entries = new List<HookEntry>();
        foreach (string resource in new[] { "T2TOnline.hooks.txt", "T2TOnline.menus_initdata.txt", "T2TOnline.clientmode.txt" })
        {
            using Stream stream = typeof(HookManifest).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded resource {resource} is missing.");
            using var reader = new StreamReader(stream);
            entries.AddRange(Parse(reader.ReadToEnd(), resource));
        }

        return new HookManifest(entries);
    }

    /// <summary>
    /// Parses manifest text: one 'Namespace.Type::Member' per line, '#' comments, '[section]' headers (clientmode.txt).
    /// A row is a patch target unless hookcheck.py is told it isn't: a 'called only' or 'interop only' comment, or a
    /// [disable] or [presentation] section. <paramref name="firstLine"/> is the number of the text's first line in its file (for a
    /// caller that parses one line of a file at a time).
    /// </summary>
    public static IEnumerable<HookEntry> Parse(string text, string source, int firstLine = 1)
    {
        int lineNumber = firstLine - 1;
        string? section = null;
        foreach (string raw in text.Split('\n'))
        {
            lineNumber++;
            int hash = raw.IndexOf('#', StringComparison.Ordinal);
            string line = (hash < 0 ? raw : raw[..hash]).Trim();
            string comment = hash < 0 ? string.Empty : raw[(hash + 1)..];
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']', ' ');
                continue;
            }

            int separator = line.IndexOf("::", StringComparison.Ordinal);
            if (separator <= 0 || separator + 2 >= line.Length)
            {
                throw new FormatException($"{source}:{lineNumber}: expected 'Type::Member', got '{line}'.");
            }

            bool notPatched = comment.Contains("called only", StringComparison.Ordinal) || comment.Contains("interop only", StringComparison.Ordinal)
                || section is "disable" or "presentation";
            yield return new HookEntry(line[..separator], line[(separator + 2)..], $"{source}:{lineNumber}", Patchable: !notPatched);
        }
    }

    /// <summary>
    /// The methods in <paramref name="patched"/> (what the mod's Harmony instance patched) that no row lists as a patch
    /// target: in no list, or listed only as not patched ('called only', 'interop only', [disable], [presentation]).
    /// hookcheck.py checks only listed patch targets for folding, so such a patch was never checked. Empty when every
    /// patch is listed. A row may name the member on a derived type; a property's accessor must be listed by its own name
    /// (get_X).
    /// </summary>
    public IReadOnlyList<string> UnlistedPatches(IEnumerable<MethodBase> patched)
    {
        ArgumentNullException.ThrowIfNull(patched);
        var targets = new HashSet<(Type Type, string Member)>();
        foreach (HookEntry entry in Entries)
        {
            if (!entry.Patchable)
            {
                continue;
            }

            for (Type? type = Il2CppTypeLocator.Find(entry.TypeName); type != null; type = type.BaseType)
            {
                targets.Add((type, entry.MemberName));
            }
        }

        var unlisted = new List<string>();
        foreach (MethodBase method in patched)
        {
            if (method.DeclaringType is not { } type || !targets.Contains((type, method.Name)))
            {
                unlisted.Add($"{method.DeclaringType?.FullName ?? "?"}::{method.Name}");
            }
        }

        return unlisted;
    }

    /// <summary>Resolves every entry against the loaded interop assemblies.</summary>
    public HookCheckResult Check()
    {
        var missing = new List<HookEntry>();
        foreach (HookEntry entry in Entries)
        {
            Type? type = Il2CppTypeLocator.Find(entry.TypeName);
            if (type == null || !HasMember(type, entry.MemberName))
            {
                missing.Add(entry);
            }
        }

        return new HookCheckResult(Entries.Count, missing);
    }

    private static bool HasMember(Type type, string memberName)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        for (Type? current = type; current != null; current = current.BaseType)
        {
            // Il2CppInterop exposes game fields as properties and keeps method names, so one name lookup covers all kinds.
            if (current.GetMember(memberName, All | BindingFlags.DeclaredOnly).Length > 0)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One manifest line.</summary>
/// <param name="TypeName">The game's type name, as the decompiled listing shows it.</param>
/// <param name="MemberName">Method, field or property name.</param>
/// <param name="Source">File and line, for error messages.</param>
/// <param name="Patchable">False when the row says the mod never patches it (hookcheck.py doesn't fold-check it then).</param>
internal sealed record HookEntry(string TypeName, string MemberName, string Source, bool Patchable = true)
{
    /// <inheritdoc />
    public override string ToString() => $"{TypeName}::{MemberName}";
}

/// <summary>Outcome of <see cref="HookManifest.Check"/>.</summary>
/// <param name="Total">Entries checked.</param>
/// <param name="Missing">Entries the running game doesn't have.</param>
internal sealed record HookCheckResult(int Total, IReadOnlyList<HookEntry> Missing)
{
    /// <summary>True when every entry resolved.</summary>
    public bool AllPresent => Missing.Count == 0;

    /// <summary>A one-line summary for the log.</summary>
    public string Summary => AllPresent
        ? $"all {Total} hook targets present"
        : $"{Missing.Count} of {Total} hook targets missing: {string.Join(", ", Missing.Take(8))}{(Missing.Count > 8 ? ", ..." : string.Empty)}";
}
