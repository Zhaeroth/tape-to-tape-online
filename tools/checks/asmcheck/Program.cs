// Reads the facts a release check needs from managed assemblies and Portable PDBs, from their metadata alone (nothing is
// loaded or run), and prints one JSON object per file. The checks themselves are in tools/checks/zipcheck.py.
// Usage: dotnet run --project tools/checks/asmcheck -c Release -- [--light] <file.dll | file.pdb>...
// --light reads only a DLL's name, version and managed flag (for the many DLLs a reference is resolved against).
// Exit 0: every file was read (a DLL without managed metadata is reported with "managed": false). Exit 1: a file could not
// be read as an assembly or a PDB (the object says why). Exit 2: bad usage.
//
// A DLL's object: file, kind "dll", managed, name, version, informational (AssemblyInformationalVersion), references
// (name, version), melon (MelonInfo's type, name, version, author, and the Melon kind from the type's base class),
// optionalDependencies (MelonOptionalDependencies' names, null when absent) and codeview (the PDB id the DLL points to,
// and the path it names). A PDB's object: file, kind "pdb", id (guid, stamp), documents (every source path), customDebugInfo
// (count per kind: SourceLink, EmbeddedSource, ...) and embeddedSourceDocuments.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.Json;

bool light = args.Length > 0 && args[0] == "--light";
string[] files = light ? args[1..] : args;
if (files.Length == 0)
{
    Console.WriteLine("Usage: asmcheck [--light] <file.dll | file.pdb>...");
    return 2;
}

var options = new JsonSerializerOptions { WriteIndented = false };
int exit = 0;
foreach (string file in files)
{
    object fact;
    try
    {
        fact = file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ? ReadPdb(file) : ReadDll(file, light);
    }
    catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or UnauthorizedAccessException)
    {
        fact = new { file = Path.GetFileName(file), error = ex.GetType().Name + ": " + ex.Message };
        exit = 1;
    }

    Console.WriteLine(JsonSerializer.Serialize(fact, options));
}

return exit;

static object ReadDll(string path, bool light)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    string leaf = Path.GetFileName(path);
    if (!pe.HasMetadata)
    {
        return new { file = leaf, kind = "dll", managed = false };
    }

    MetadataReader mr = pe.GetMetadataReader();
    AssemblyDefinition assembly = mr.GetAssemblyDefinition();
    if (light)
    {
        return new { file = leaf, kind = "dll", managed = true, name = mr.GetString(assembly.Name), version = assembly.Version.ToString() };
    }

    string? informational = null;
    string[]? optionalDependencies = null;
    object? melon = null;
    foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
    {
        CustomAttribute attribute = mr.GetCustomAttribute(handle);
        string type = AttributeTypeName(mr, attribute);
        BlobReader blob = mr.GetBlobReader(attribute.Value);
        if (blob.ReadUInt16() != 1)
        {
            continue;
        }

        switch (type)
        {
            case "System.Reflection.AssemblyInformationalVersionAttribute":
                informational = blob.ReadSerializedString();
                break;
            case "MelonLoader.MelonOptionalDependenciesAttribute":
                optionalDependencies = ReadStringArray(ref blob);
                break;
            case "MelonLoader.MelonInfoAttribute":
                string? typeName = blob.ReadSerializedString();
                string? name = blob.ReadSerializedString();
                string? version = blob.ReadSerializedString();
                string? author = blob.ReadSerializedString();
                melon = new { type = typeName, name, version, author, kind = MelonKind(mr, typeName) };
                break;
            default:
                break;
        }
    }

    var references = mr.AssemblyReferences
        .Select(h => mr.GetAssemblyReference(h))
        .Select(r => new { name = mr.GetString(r.Name), version = r.Version.ToString() })
        .OrderBy(r => r.name, StringComparer.Ordinal)
        .ToArray();

    object? codeview = null;
    foreach (DebugDirectoryEntry entry in pe.ReadDebugDirectory())
    {
        if (entry.Type == DebugDirectoryEntryType.CodeView)
        {
            CodeViewDebugDirectoryData data = pe.ReadCodeViewDebugDirectoryData(entry);
            codeview = new { guid = data.Guid.ToString(), stamp = entry.Stamp, path = data.Path };
        }
    }

    return new
    {
        file = leaf,
        kind = "dll",
        managed = true,
        name = mr.GetString(assembly.Name),
        version = assembly.Version.ToString(),
        informational,
        references,
        melon,
        optionalDependencies,
        codeview,
    };
}

static object ReadPdb(string path)
{
    using var stream = File.OpenRead(path);
    using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(stream, MetadataStreamOptions.PrefetchMetadata);
    MetadataReader mr = provider.GetMetadataReader();
    var documents = new List<string>();
    var documentNames = new Dictionary<DocumentHandle, string>();
    foreach (DocumentHandle handle in mr.Documents)
    {
        string name = mr.GetString(mr.GetDocument(handle).Name);
        documents.Add(name);
        documentNames[handle] = name;
    }

    var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
    var embedded = new List<string>();
    foreach (CustomDebugInformationHandle handle in mr.CustomDebugInformation)
    {
        CustomDebugInformation info = mr.GetCustomDebugInformation(handle);
        Guid kind = mr.GetGuid(info.Kind);
        string label = KindLabel(kind);
        kinds[label] = kinds.TryGetValue(label, out int count) ? count + 1 : 1;
        if (label == "EmbeddedSource" && info.Parent.Kind == HandleKind.Document
            && documentNames.TryGetValue((DocumentHandle)info.Parent, out string? embeddedName))
        {
            embedded.Add(embeddedName);
        }
    }

    ImmutableArrayId id = ImmutableArrayId.From(mr.DebugMetadataHeader);
    return new
    {
        file = Path.GetFileName(path),
        kind = "pdb",
        id = new { guid = id.Guid.ToString(), stamp = id.Stamp },
        documents,
        customDebugInfo = kinds,
        embeddedSourceDocuments = embedded,
    };
}

static string KindLabel(Guid kind) => kind.ToString() switch
{
    "cc110556-a091-4d38-9fec-25ab9a351a6a" => "SourceLink",
    "0e8a571b-6926-466e-b4ad-8ab04611f5fe" => "EmbeddedSource",
    "b5feec05-8cd0-4a83-96da-466284bb4bd8" => "CompilationOptions",
    "7e4d4708-096e-4c5c-aeda-cb10ba6a740d" => "CompilationMetadataReferences",
    "6da9a61e-f8c7-4874-be62-68bc5630df71" => "StateMachineHoistedLocalScopes",
    "755f52a8-91c5-45be-b4b8-209571e552bd" => "DynamicLocalVariables",
    "ed9fdf71-8879-4747-8ed3-fe5ede3ce710" => "TupleElementNames",
    "58b2eab6-209f-4e4e-a22c-b2d0f910c782" => "DefaultNamespace",
    "54fd2ac5-e925-401a-9c2a-f94f171072f8" => "AsyncMethodSteppingInformation",
    "a643004c-0240-496f-a783-30d64f4979de" => "EncLocalSlotMap",
    _ => kind.ToString(),
};

static string AttributeTypeName(MetadataReader mr, CustomAttribute attribute)
{
    if (attribute.Constructor.Kind != HandleKind.MemberReference)
    {
        return string.Empty;
    }

    MemberReference member = mr.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
    if (member.Parent.Kind != HandleKind.TypeReference)
    {
        return string.Empty;
    }

    TypeReference type = mr.GetTypeReference((TypeReferenceHandle)member.Parent);
    return mr.GetString(type.Namespace) + "." + mr.GetString(type.Name);
}

static string[]? ReadStringArray(ref BlobReader blob)
{
    int count = blob.ReadInt32();
    if (count < 0)
    {
        return null;
    }

    var names = new string[count];
    for (int i = 0; i < count; i++)
    {
        names[i] = blob.ReadSerializedString() ?? string.Empty;
    }

    return names;
}

// The Melon kind (MelonPlugin or MelonMod) is the name of the base class of the type MelonInfo names, found in this
// assembly; a base class that is itself in this assembly is followed.
static string? MelonKind(MetadataReader mr, string? serializedType)
{
    if (serializedType is null)
    {
        return null;
    }

    string fullName = serializedType.Split(',')[0].Trim();
    foreach (TypeDefinitionHandle handle in mr.TypeDefinitions)
    {
        TypeDefinition definition = mr.GetTypeDefinition(handle);
        string name = mr.GetString(definition.Name);
        string ns = mr.GetString(definition.Namespace);
        if ((ns.Length == 0 ? name : ns + "." + name) != fullName)
        {
            continue;
        }

        EntityHandle baseType = definition.BaseType;
        for (int depth = 0; depth < 16 && !baseType.IsNil; depth++)
        {
            if (baseType.Kind == HandleKind.TypeReference)
            {
                return mr.GetString(mr.GetTypeReference((TypeReferenceHandle)baseType).Name);
            }

            if (baseType.Kind != HandleKind.TypeDefinition)
            {
                return null;
            }

            baseType = mr.GetTypeDefinition((TypeDefinitionHandle)baseType).BaseType;
        }
    }

    return null;
}

// A Portable PDB's id is a 16-byte GUID followed by a 4-byte stamp; the DLL's CodeView entry carries the same two.
internal readonly record struct ImmutableArrayId(Guid Guid, uint Stamp)
{
    public static ImmutableArrayId From(DebugMetadataHeader? header)
    {
        if (header is null || header.Id.Length < 20)
        {
            return new ImmutableArrayId(Guid.Empty, 0);
        }

        byte[] bytes = header.Id.ToArray();
        return new ImmutableArrayId(new Guid(bytes.AsSpan(0, 16)), BitConverter.ToUInt32(bytes, 16));
    }
}
