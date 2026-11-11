// Resolves hook rows against the interop assemblies by the same rules as HookManifest.Check and Il2CppTypeLocator, without the game: a row that
// doesn't resolve at start-up turns online off, and hookcheck.py (which reads the decompiled listing) can't see that. In the release gate.
// Usage: dotnet run --project tools/checks/rowcheck -c Release -- <game folder> <hook list>... Exit 0: every row resolves. Exit 1: a row doesn't.
// Exit 2: bad usage, or the game folder has no interop assemblies (MelonLoader 0.7.3 makes them at the game's first launch).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

if (args.Length < 2)
{
    Console.WriteLine("Usage: rowcheck <game folder> <hook list>...");
    return 2;
}

string melon = Path.Combine(args[0], "MelonLoader");
string[] folders = { Path.Combine(melon, "Il2CppAssemblies"), Path.Combine(melon, "net6") };
AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string name = new AssemblyName(e.Name).Name + ".dll";
    foreach (string folder in folders)
    {
        string file = Path.Combine(folder, name);
        if (File.Exists(file))
        {
            return Assembly.LoadFrom(file);
        }
    }

    return null;
};

if (!Directory.Exists(folders[0]))
{
    Console.WriteLine($"No interop assemblies in {folders[0]}: launch the game once with MelonLoader 0.7.3 installed.");
    return 2;
}

var index = new Dictionary<string, Type>(StringComparer.Ordinal);
foreach (string file in Directory.EnumerateFiles(folders[0], "*.dll"))
{
    Type?[] types;
    try
    {
        types = Assembly.LoadFrom(file).GetTypes();
    }
    catch (ReflectionTypeLoadException ex)
    {
        types = ex.Types;
    }
    catch (Exception)
    {
        continue;
    }

    foreach (Type? type in types)
    {
        if (type?.FullName != null)
        {
            index.TryAdd(type.FullName, type);
        }
    }
}

Console.WriteLine($"{index.Count} interop types");
int checkedRows = 0;
int missing = 0;
foreach (string list in args[1..])
{
string[] lines = File.ReadAllLines(list);
for (int n = 0; n < lines.Length; n++)
{
    string raw = lines[n];
    int hash = raw.IndexOf('#');
    string line = (hash < 0 ? raw : raw[..hash]).Trim();
    if (line.Length == 0 || line.StartsWith('['))
    {
        continue;
    }

    int at = line.IndexOf("::", StringComparison.Ordinal);
    string typeName = line[..at].Replace('/', '+');
    string member = line[(at + 2)..];
    checkedRows++;
    Type? type = Find(typeName);
    if (type == null)
    {
        Console.WriteLine($"NO TYPE   line {n + 1}: {line}");
        missing++;
        continue;
    }

    bool found = false;
    for (Type? current = type; current != null && !found; current = current.BaseType)
    {
        found = current.GetMember(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Length > 0;
    }

    if (!found)
    {
        Console.WriteLine($"NO MEMBER line {n + 1}: {line} (on {type.FullName})");
        missing++;
    }
}

}

Console.WriteLine($"{checkedRows} rows checked, {missing} missing");
return missing == 0 ? 0 : 1;

Type? Find(string name)
{
    foreach (string candidate in new[] { "Il2Cpp" + name, "Il2Cpp." + name, name })
    {
        string form = candidate;
        if (index.TryGetValue(form, out Type? type))
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
            if (index.TryGetValue(nested, out type))
            {
                return type;
            }
        }
    }

    return null;
}
