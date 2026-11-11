using System;
using System.Collections.Generic;
using System.IO;
using T2TOnline.Plugin.Hooks;

namespace T2TOnline.Plugin.Client;

/// <summary>What client mode does with one game method.</summary>
internal enum ClientModeAction
{
    /// <summary>Early-return prefix (calls counted for the leak audit).</summary>
    Block,

    /// <summary>Folded: the method's component is disabled instead of patching shared code.</summary>
    Disable,

    /// <summary>Left running; calls are counted only.</summary>
    Audit,

    /// <summary>A save write: refused (a bool result reports success).</summary>
    Save,

    /// <summary>Reviewed and left running (it only draws): never patched.</summary>
    Presentation,
}

/// <summary>One line of clientmode.txt.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Entry">The game member.</param>
internal sealed record ClientModeRule(ClientModeAction Action, HookEntry Entry);

/// <summary>Reads the embedded clientmode.txt: sections [block], [disable], [audit], [presentation] and [saves] of 'Type::Method' lines.</summary>
internal static class ClientModeList
{
    /// <summary>The rules, in file order.</summary>
    public static IReadOnlyList<ClientModeRule> LoadEmbedded()
    {
        using Stream stream = typeof(ClientModeList).Assembly.GetManifestResourceStream("T2TOnline.clientmode.txt")
            ?? throw new InvalidOperationException("Embedded resource T2TOnline.clientmode.txt is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses clientmode.txt text.</summary>
    public static IReadOnlyList<ClientModeRule> Parse(string text)
    {
        var rules = new List<ClientModeRule>();
        ClientModeAction? section = null;
        int lineNumber = 0;
        foreach (string raw in text.Split('\n'))
        {
            lineNumber++;
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                section = line switch
                {
                    "[block]" => ClientModeAction.Block,
                    "[disable]" => ClientModeAction.Disable,
                    "[audit]" => ClientModeAction.Audit,
                    "[saves]" => ClientModeAction.Save,
                    "[presentation]" => ClientModeAction.Presentation,
                    _ => throw new FormatException($"clientmode.txt:{lineNumber}: unknown section {line}."),
                };
                continue;
            }

            if (section == null)
            {
                throw new FormatException($"clientmode.txt:{lineNumber}: an entry before any section.");
            }

            foreach (HookEntry entry in HookManifest.Parse(line, "clientmode.txt", lineNumber))
            {
                rules.Add(new ClientModeRule(section.Value, entry));
            }
        }

        return rules;
    }
}
