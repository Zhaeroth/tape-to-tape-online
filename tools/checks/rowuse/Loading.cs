// Reads the inputs: the interop assemblies' types, the hook lists' rows, the built plugin's metadata and the allow file.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class Loading
{
    /// <summary>The interop assemblies' types by full name, and the interop assemblies' names (a reference into one is a game member).</summary>
    public static (Dictionary<string, Type> Index, HashSet<string> AssemblyNames) LoadInterop(string gameFolder)
    {
        string melon = Path.Combine(gameFolder, "MelonLoader");
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

        var index = new Dictionary<string, Type>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folders[0]))
        {
            return (index, names);
        }

        foreach (string file in Directory.EnumerateFiles(folders[0], "*.dll"))
        {
            names.Add(Path.GetFileNameWithoutExtension(file));
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

        return (index, names);
    }

    /// <summary>
    /// The rows of the given lists. clientmode.txt's rows and presentation.txt's event rows are read as data by the plugin;
    /// every other list's rows are the ones the plugin's code names.
    /// </summary>
    public static List<Row> LoadRows(IEnumerable<string> lists)
    {
        var rows = new List<Row>();
        foreach (string list in lists)
        {
            string file = Path.GetFileName(list);
            string[] lines = File.ReadAllLines(list);
            string? section = null;
            for (int n = 0; n < lines.Length; n++)
            {
                string raw = lines[n];
                int hash = raw.IndexOf('#');
                string line = (hash < 0 ? raw : raw[..hash]).Trim();
                string comment = hash < 0 ? string.Empty : raw[(hash + 1)..].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith('['))
                {
                    section = line.Trim('[', ']', ' ');
                    continue;
                }

                if (file == "presentation.txt")
                {
                    if (section is "local" or "menu")
                    {
                        string eventName = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                        int dot = eventName.LastIndexOf('.');
                        if (dot > 0)
                        {
                            rows.Add(new Row(file, n + 1, RowKind.PresentationEvent, eventName[..dot], eventName[(dot + 1)..], comment));
                        }
                    }

                    continue;
                }

                int at = line.IndexOf("::", StringComparison.Ordinal);
                if (at <= 0)
                {
                    continue;
                }

                var row = new Row(file, n + 1, file == "clientmode.txt" ? RowKind.ClientMode : RowKind.Own, line[..at].Replace('/', '+'), line[(at + 2)..], comment);
                row.Type = Types.Find(row.TypeName);
                rows.Add(row);
            }
        }

        return rows;
    }

    /// <summary>The allow file's entries: '&lt;row|use&gt; &lt;Type::Member&gt; | &lt;reason&gt;', one per line, the reason never empty.</summary>
    public static List<AllowEntry> LoadAllow(string path)
    {
        var entries = new List<AllowEntry>();
        string[] lines = File.ReadAllLines(path);
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            entries.Add(ParseAllowLine(line, n + 1, Path.GetFileName(path)));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AllowEntry entry in entries)
        {
            if (!seen.Add(entry.Kind + " " + entry.Key))
            {
                throw new FormatException($"{Path.GetFileName(path)}:{entry.Line}: {entry.Kind} {entry.Key} is listed twice");
            }
        }

        return entries;
    }

    public static AllowEntry ParseAllowLine(string line, int number, string file)
    {
        int bar = line.IndexOf('|');
        string head = bar < 0 ? line : line[..bar].Trim();
        string reason = bar < 0 ? string.Empty : line[(bar + 1)..].Trim();
        string[] parts = head.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] is not ("row" or "use") || !parts[1].Contains("::", StringComparison.Ordinal) || reason.Length == 0)
        {
            throw new FormatException($"{file}:{number}: expected '<row|use> <Type::Member> | <reason>' with a reason");
        }

        return new AllowEntry(parts[0], parts[1].Trim(), reason, number);
    }

    /// <summary>The plugin's member references into the interop, its type references and the string literals of it, Core and the Steam library.</summary>
    public static UseData LoadUses(string pluginDll, HashSet<string> interopNames)
    {
        var data = new UseData();
        using (FileStream stream = File.OpenRead(pluginDll))
        using (var pe = new PEReader(stream))
        {
            MetadataReader md = pe.GetMetadataReader();
            var provider = new NameProvider(md);
            foreach (TypeReferenceHandle handle in md.TypeReferences)
            {
                (string assembly, string type) = provider.Name(handle);
                if (interopNames.Contains(assembly))
                {
                    data.TypeRefs.Add(type);
                }
            }

            foreach (MemberReferenceHandle handle in md.MemberReferences)
            {
                MemberReference member = md.GetMemberReference(handle);
                (string assembly, string type) = member.Parent.Kind switch
                {
                    HandleKind.TypeReference => provider.Name((TypeReferenceHandle)member.Parent),
                    HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(provider, null),
                    _ => (string.Empty, string.Empty),
                };
                if (!interopNames.Contains(assembly))
                {
                    continue;
                }

                (string Type, string Member) key = (type, md.GetString(member.Name));
                data.Uses[key] = data.Uses.GetValueOrDefault(key) + 1;
            }

            AddStrings(md, data.Strings);
        }

        // Core's and the Steam library's literals too: names the plugin takes from Core's tables.
        foreach (string other in new[] { "T2TOnline.Core.dll", "T2TOnline.Steam.dll" })
        {
            string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginDll))!, other);
            if (!File.Exists(path))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            AddStrings(pe.GetMetadataReader(), data.Strings);
        }

        return data;
    }

    private static void AddStrings(MetadataReader md, HashSet<string> strings)
    {
        for (UserStringHandle handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = md.GetNextHandle(handle))
        {
            strings.Add(md.GetUserString(handle));
        }
    }
}

/// <summary>Names a type reference as (assembly, reflection full name of its definition).</summary>
internal sealed class NameProvider : ISignatureTypeProvider<(string Assembly, string Type), object?>
{
    private readonly MetadataReader _md;

    public NameProvider(MetadataReader md) => _md = md;

    public (string Assembly, string Type) Name(TypeReferenceHandle handle)
    {
        TypeReference reference = _md.GetTypeReference(handle);
        string name = _md.GetString(reference.Name);
        string ns = _md.GetString(reference.Namespace);
        switch (reference.ResolutionScope.Kind)
        {
            case HandleKind.AssemblyReference:
                string assembly = _md.GetString(_md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
                return (assembly, ns.Length > 0 ? ns + "." + name : name);
            case HandleKind.TypeReference:
                (string outerAssembly, string outer) = Name((TypeReferenceHandle)reference.ResolutionScope);
                return (outerAssembly, outer + "+" + name);
            default:
                return (string.Empty, name);
        }
    }

    public (string Assembly, string Type) GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Name(handle);

    public (string Assembly, string Type) GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => (string.Empty, "def");

    public (string Assembly, string Type) GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public (string Assembly, string Type) GetGenericInstantiation((string Assembly, string Type) genericType, ImmutableArray<(string Assembly, string Type)> typeArguments) => genericType;

    public (string Assembly, string Type) GetArrayType((string Assembly, string Type) elementType, ArrayShape shape) => (string.Empty, "array");

    public (string Assembly, string Type) GetSZArrayType((string Assembly, string Type) elementType) => (string.Empty, "array");

    public (string Assembly, string Type) GetByReferenceType((string Assembly, string Type) elementType) => elementType;

    public (string Assembly, string Type) GetPointerType((string Assembly, string Type) elementType) => elementType;

    public (string Assembly, string Type) GetPinnedType((string Assembly, string Type) elementType) => elementType;

    public (string Assembly, string Type) GetModifiedType((string Assembly, string Type) modifier, (string Assembly, string Type) unmodifiedType, bool isRequired) => unmodifiedType;

    public (string Assembly, string Type) GetFunctionPointerType(MethodSignature<(string Assembly, string Type)> signature) => (string.Empty, "fnptr");

    public (string Assembly, string Type) GetGenericMethodParameter(object? genericContext, int index) => (string.Empty, "!!" + index);

    public (string Assembly, string Type) GetGenericTypeParameter(object? genericContext, int index) => (string.Empty, "!" + index);

    public (string Assembly, string Type) GetPrimitiveType(PrimitiveTypeCode typeCode) => (string.Empty, typeCode.ToString());
}
