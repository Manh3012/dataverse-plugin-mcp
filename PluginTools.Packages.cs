using System.ComponentModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// Plug-in packages (.nupkg with dependent assemblies). Mirrors PRT's PackageRegistrationForm: Create/Update pluginpackage
// with base64 content; Dataverse itself creates the pluginassembly (packageid set) and plugintype rows during that call.
public static partial class PluginTools
{
    [McpServerTool(Name = "inspect_package", ReadOnly = true)]
    [Description("Read a plug-in package (.nupkg) locally (no Dataverse call): nuspec id and version, and each DLL under lib/ with its IPlugin / CodeActivity classes.")]
    public static string InspectPackage([Description("Absolute path to the .nupkg")] string nupkgPath) => Run(() =>
    {
        var p = AssemblyInspector.InspectPackage(nupkgPath);
        return new { p.Path, p.Id, p.Version, Assemblies = p.Assemblies.Select(a => new { Entry = a.Path, a.Name, a.Version, Signed = a.PublicKeyToken != null, a.Types }) };
    });

    [McpServerTool(Name = "register_package")]
    [Description("Register or update a plug-in package from a .nupkg (PRT 'Register New Package' / 'Update'). New packages are named '<publisher prefix>_<nuspec id>', so solutionUniqueName is required the first time. " +
        "An update only replaces the content: name and version can't change after creation, and the version plays no part in upgrades. Dataverse creates the assembly and plug-in type rows itself; " +
        "steps are then registered with register_step as usual. Refuses custom workflow activities (not supported in packages), a packed Microsoft.Xrm.Sdk, a classic assembly with the same name, and updates that drop plug-in types that still have steps.")]
    public static string RegisterPackage(Dataverse dv,
        [Description("Absolute path to the built .nupkg (pac plugin init projects produce it on build)")] string nupkgPath,
        [Description("Unmanaged solution unique name: required for a new package (its publisher prefix names the package), optional on update")] string? solutionUniqueName = null,
        [Description("pluginpackageid of the package to update (optional; otherwise found by '<prefix>_<nuspec id>')")] string? packageId = null) => Run(() =>
    {
        var svc = dv.Client;
        var pkg = AssemblyInspector.InspectPackage(nupkgPath);
        var plugins = pkg.Assemblies.Where(a => a.Types.Count > 0).ToList();
        if (plugins.Count == 0)
            throw new McpException($"No DLL under lib/ in {pkg.Id} contains public, non-abstract IPlugin classes.");
        if (plugins.SelectMany(a => a.Types).Where(t => t.IsWorkflowActivity).Select(t => t.TypeName).ToList() is { Count: > 0 } wf)
            throw new McpException($"Custom workflow activities aren't supported in plug-in packages: {string.Join(", ", wf)}. Register them in a classic assembly.");
        if (pkg.Assemblies.FirstOrDefault(a => a.Name is "Microsoft.Xrm.Sdk" or "Microsoft.Crm.Sdk.Proxy") is { } sdk)
            throw new McpException($"The package contains {sdk.Path}. Mark the Microsoft.CrmSdk.CoreAssemblies reference PrivateAssets=\"All\" and rebuild.");

        var classic = Q("pluginassembly", "name");
        classic.Criteria.AddCondition("name", ConditionOperator.In, plugins.Select(a => (object)a.Name).ToArray());
        classic.Criteria.AddCondition("packageid", ConditionOperator.Null);
        if (svc.RetrieveMultiple(classic).Entities.FirstOrDefault() is { } clash)
            throw new McpException($"A classic assembly named {clash.GetAttributeValue<string>("name")} ({clash.Id}) is registered. " +
                "Dataverse doesn't convert it: move or delete its steps, unregister it, then register the package.");

        var existing = FindPackage(svc, pkg.Id, packageId, solutionUniqueName);
        Guid id;
        string uniqueName, version;
        if (existing == null)
        {
            if (solutionUniqueName == null)
                throw new McpException("A new package needs solutionUniqueName: its publisher prefix becomes part of the package name.");
            uniqueName = $"{PublisherPrefix(svc, solutionUniqueName)}_{pkg.Id}";
            version = pkg.Version;
            id = svc.Create(new Entity("pluginpackage")
            {
                ["name"] = uniqueName,
                ["uniquename"] = uniqueName,
                ["version"] = version,
                ["content"] = Convert.ToBase64String(pkg.Content),
            });
        }
        else
        {
            id = existing.Id;
            uniqueName = existing.GetAttributeValue<string>("uniquename");
            version = existing.GetAttributeValue<string>("version");
            var newTypes = plugins.SelectMany(a => a.Types).Select(t => t.TypeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dropped = PackageTypes(svc, id).Where(t => !newTypes.Contains(t.GetAttributeValue<string>("typename"))).Select(t => t.Id);
            RefuseIfSteps(StepsFor(svc, dropped));
            svc.Update(new Entity("pluginpackage", id) { ["content"] = Convert.ToBase64String(pkg.Content) }); // never name/version
        }

        if (solutionUniqueName != null) AddToSolution(svc, PackageComponentType(svc), id, solutionUniqueName, addRequired: false);

        var asms = In(svc, Q("pluginassembly", "name"), "packageid", [id]);
        var types = In(svc, Q("plugintype", "typename", "pluginassemblyid"), "pluginassemblyid", asms.Select(a => a.Id));
        var missing = plugins.Where(p => !asms.Any(a => string.Equals(a.GetAttributeValue<string>("name"), p.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name).ToList();
        return new
        {
            PackageId = id, UniqueName = uniqueName, Version = version, Action = existing == null ? "created" : "updated", Solution = solutionUniqueName,
            VersionNote = version == pkg.Version ? null : $"The .nuspec says {pkg.Version}, but a package's version can't change after creation.",
            Assemblies = asms.Select(a => new
            {
                AssemblyId = a.Id,
                Name = a.GetAttributeValue<string>("name"),
                Types = types.Where(t => t.GetAttributeValue<EntityReference>("pluginassemblyid").Id == a.Id)
                    .Select(t => new { PluginTypeId = t.Id, TypeName = t.GetAttributeValue<string>("typename") }),
            }),
            NotRegistered = missing,
            Warning = missing.Count == 0 ? null : "Dataverse created no assembly rows for these plug-in DLLs. A DLL added to an existing package isn't registered on update " +
                "(microsoft/powerplatform-build-tools#1465); unregister and re-register the package, or ship that DLL in its own package.",
        };
    });

    static Entity? FindPackage(IOrganizationService svc, string nuspecId, string? packageId, string? solution)
    {
        var q = Q("pluginpackage", "uniquename", "version");
        if (packageId != null) q.Criteria.AddCondition("pluginpackageid", ConditionOperator.Equal, Guid.Parse(packageId));
        else if (solution != null) q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, $"{PublisherPrefix(svc, solution)}_{nuspecId}");
        else q.Criteria.AddCondition("uniquename", ConditionOperator.EndsWith, nuspecId); // '_' is a LIKE wildcard, so re-check below
        var hits = svc.RetrieveMultiple(q).Entities
            .Where(e => packageId != null || e.GetAttributeValue<string>("uniquename").EndsWith("_" + nuspecId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (packageId != null && hits.Count == 0) throw new McpException($"Plug-in package {packageId} not found.");
        if (hits.Count > 1)
            throw new McpException($"Several packages match '{nuspecId}': {string.Join(", ", hits.Select(h => $"{h.GetAttributeValue<string>("uniquename")} ({h.Id})"))}. Pass packageId.");
        return hits.FirstOrDefault();
    }

    static List<Entity> PackageTypes(IOrganizationService svc, Guid packageId) =>
        In(svc, Q("plugintype", "typename"), "pluginassemblyid", In(svc, Q("pluginassembly"), "packageid", [packageId]).Select(a => a.Id));

    static string PublisherPrefix(IOrganizationService svc, string solution)
    {
        var q = Q("solution");
        q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, solution);
        var p = q.AddLink("publisher", "publisherid", "publisherid");
        p.EntityAlias = "p";
        p.Columns = new ColumnSet("customizationprefix");
        return Aliased(Single(svc, q, $"Solution '{solution}' not found."), "p.customizationprefix")!;
    }

    // pluginpackage has no documented component type number; PRT looks it up per environment.
    static int PackageComponentType(IOrganizationService svc)
    {
        var q = Q("solutioncomponentdefinition", "solutioncomponenttype");
        q.Criteria.AddCondition("primaryentityname", ConditionOperator.Equal, "pluginpackage");
        return Single(svc, q, "No solution component type found for pluginpackage.").GetAttributeValue<int>("solutioncomponenttype");
    }
}
