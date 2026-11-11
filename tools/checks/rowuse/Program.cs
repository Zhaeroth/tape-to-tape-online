// Keeps the hook lists complete: every game member the built plugin refers to has a row, and every row of hooks.txt and
// menus_initdata.txt is used. In the gate, after the plugin build. Its own tests run first (--selftest): each plants a case
// and asserts the analysis fails on it.
// Usage: dotnet run --project tools/checks/rowuse -c Release -- [--selftest] <game folder> <T2TOnline.dll> <allow file> <hook list>...
// Exit 0: complete. 1: an unlisted use, an unused row or a stale allow entry. 2: bad usage, an unreadable input, or nothing to
// judge (no interop assemblies, no member reference, no row). 3: the self-test failed (the check was not run).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

bool selfTest = args.Contains("--selftest");
string[] rest = args.Where(a => a != "--selftest").ToArray();
if (rest.Length < 4)
{
    Console.WriteLine("Usage: rowuse [--selftest] <game folder> <T2TOnline.dll> <allow file> <hook list>...");
    return 2;
}

string gameFolder = rest[0];
string pluginDll = rest[1];
string allowFile = rest[2];
string[] lists = rest[3..];
foreach (string file in new[] { pluginDll, allowFile }.Concat(lists))
{
    if (!File.Exists(file))
    {
        Console.WriteLine($"rowuse: no such file: {Path.GetFileName(file)}");
        return 2;
    }
}

(Dictionary<string, Type> index, HashSet<string> interopNames) = Loading.LoadInterop(gameFolder);
if (index.Count == 0)
{
    Console.WriteLine($"rowuse: no interop assemblies under {Path.Combine("<game folder>", "MelonLoader", "Il2CppAssemblies")}: launch the game once with MelonLoader 0.7.3 installed.");
    return 2;
}

Types.Index = index;
List<Row> rows;
UseData data;
List<AllowEntry> allow;
try
{
    rows = Loading.LoadRows(lists);
    data = Loading.LoadUses(pluginDll, interopNames);
    allow = Loading.LoadAllow(allowFile);
}
catch (Exception ex) when (ex is IOException or FormatException or BadImageFormatException)
{
    Console.WriteLine($"rowuse: {ex.Message}");
    return 2;
}

if (selfTest)
{
    (List<string> failures, int run) = SelfTest.Run(rows, data);
    foreach (string failure in failures)
    {
        Console.WriteLine(failure);
    }

    Console.WriteLine($"rowuse selftest: {run} tests, {failures.Count} failed");
    if (failures.Count > 0)
    {
        return 3;
    }
}

Report report = Analyzer.Analyze(rows, data, allow);
if (report.ReadNothing)
{
    Console.WriteLine($"rowuse: nothing to judge ({data.Uses.Count} member references, {rows.Count} rows): the check read nothing");
    return 2;
}

foreach (string line in report.Unlisted.Concat(report.Unused).Concat(report.Stale))
{
    Console.WriteLine(line);
}

Console.WriteLine($"rowuse: {report.RowCount} rows ({report.RowsInScope} of the plugin's own lists), {report.UseCount} distinct member uses, " +
    $"{report.Unlisted.Count} unlisted, {report.Unused.Count} unused, {report.AllowedRows} kept by the allow file, {report.Stale.Count} stale allow entries");
return report.Clean ? 0 : 1;
