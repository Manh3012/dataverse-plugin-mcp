using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Xml.Linq;

public record PluginTypeInfo(string TypeName, bool IsWorkflowActivity);

public record AssemblyInfo(
    string Path, string Name, string Version, string Culture, string? PublicKeyToken,
    string SourceHash, List<PluginTypeInfo> Types)
{
    public byte[] Content { get; init; } = [];
}

public record PackageInfo(string Path, string Id, string Version, List<AssemblyInfo> Assemblies)
{
    public byte[] Content { get; init; } = [];
}

// Reads a plug-in DLL's metadata without loading or executing it (works for .NET Framework DLLs).
public static class AssemblyInspector
{
    const string IPlugin = "Microsoft.Xrm.Sdk.IPlugin";
    const string CodeActivity = "System.Activities.CodeActivity";

    public static AssemblyInfo Inspect(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var an = AssemblyName.GetAssemblyName(path);
        var token = an.GetPublicKeyToken();
        var bytes = File.ReadAllBytes(path);

        using var pe = new PEReader(new MemoryStream(bytes));
        var md = pe.GetMetadataReader();
        var types = new List<PluginTypeInfo>();
        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            // Same filter as PRT (GetExportedTypes, !IsAbstract, IsClass).
            // shortcut: top-level public types only; nested public plug-in classes are not detected.
            if ((td.Attributes & TypeAttributes.VisibilityMask) != TypeAttributes.Public) continue;
            if ((td.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0) continue;

            var (plugin, workflow) = Classify(md, h);
            if (plugin || workflow) types.Add(new PluginTypeInfo(FullName(md, h)!, workflow));
        }

        return new AssemblyInfo(
            path, an.Name!, an.Version!.ToString(),
            string.IsNullOrEmpty(an.CultureName) ? "neutral" : an.CultureName,
            token is { Length: > 0 } ? Convert.ToHexString(token).ToLowerInvariant() : null,
            Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant(),
            types) { Content = bytes };
    }

    // Reads a plug-in package (.nupkg = zip): nuspec id/version like PRT, and every DLL under lib/.
    // Assembly paths in the result are the entry names inside the package.
    public static PackageInfo InspectPackage(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(path);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        var nuspec = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{path} has no .nuspec; is it a NuGet package?");
        XElement metadata;
        using (var s = nuspec.Open())
            metadata = XDocument.Load(s).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
                ?? throw new InvalidDataException("package/metadata not found in the .nuspec.");
        string Meta(string name) => metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value
            ?? throw new InvalidDataException($"package/metadata/{name} not found in the .nuspec.");

        var tmp = Directory.CreateTempSubdirectory("dvpkg");
        try
        {
            var assemblies = new List<AssemblyInfo>();
            foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase)
                                                  && e.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                var file = System.IO.Path.Combine(tmp.FullName, Guid.NewGuid().ToString("N") + ".dll");
                e.ExtractToFile(file);
                try { assemblies.Add(Inspect(file) with { Path = e.FullName }); }
                catch (BadImageFormatException) { throw new InvalidDataException($"{e.FullName} is not a .NET assembly; plug-in packages can't contain unmanaged code."); }
            }
            return new PackageInfo(path, Meta("id"), Meta("version"), assemblies) { Content = bytes };
        }
        finally { tmp.Delete(recursive: true); }
    }

    // Walks the base-class chain inside this DLL, matching IPlugin / CodeActivity by name so
    // Microsoft.Xrm.Sdk and System.Activities never need to be resolved.
    // shortcut: a base class living in another DLL (shared base library) or generic CodeActivity<T>
    // is not followed; open that DLL too if this ever matters.
    static (bool Plugin, bool Workflow) Classify(MetadataReader md, TypeDefinitionHandle h)
    {
        while (true)
        {
            var td = md.GetTypeDefinition(h);
            foreach (var ih in td.GetInterfaceImplementations())
                if (FullName(md, md.GetInterfaceImplementation(ih).Interface) == IPlugin) return (true, false);

            var b = td.BaseType;
            if (b.IsNil) return (false, false);
            if (b.Kind != HandleKind.TypeDefinition) return (false, FullName(md, b) == CodeActivity);
            h = (TypeDefinitionHandle)b;
        }
    }

    static string? FullName(MetadataReader md, EntityHandle h)
    {
        (StringHandle ns, StringHandle name) = h.Kind switch
        {
            HandleKind.TypeDefinition => (md.GetTypeDefinition((TypeDefinitionHandle)h).Namespace, md.GetTypeDefinition((TypeDefinitionHandle)h).Name),
            HandleKind.TypeReference => (md.GetTypeReference((TypeReferenceHandle)h).Namespace, md.GetTypeReference((TypeReferenceHandle)h).Name),
            _ => (default, default),
        };
        if (name.IsNil) return null;
        var n = md.GetString(ns);
        return n.Length == 0 ? md.GetString(name) : $"{n}.{md.GetString(name)}";
    }
}
