using System.ComponentModel;
using System.Text.Json;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// Custom APIs: customapi + customapirequestparameter + customapiresponseproperty. Dataverse creates the sdkmessage.
// Solution membership goes through the SolutionUniqueName request parameter (the component type numbers are undocumented).
public static partial class PluginTools
{
    record ApiField(string UniqueName, string Type, bool? IsOptional = null, string? LogicalEntityName = null, string? DisplayName = null, string? Description = null);

    static readonly string[] FieldTypes = ["Boolean", "DateTime", "Decimal", "Entity", "EntityCollection", "EntityReference", "Float", "Integer", "Money", "Picklist", "String", "StringArray", "Guid"];
    static readonly string[] Bindings = ["Global", "Entity", "EntityCollection"];
    static readonly string[] StepTypes = ["None", "AsyncOnly", "SyncAndAsync"];
    static readonly JsonSerializerOptions FieldJson = new() { PropertyNameCaseInsensitive = true };

    [McpServerTool(Name = "register_custom_api")]
    [Description("Create or update (matched by uniqueName) a Custom API with its request parameters and response properties. " +
        "bindingType, boundEntity, isFunction, allowedStepType and each field's type / isOptional / logicalEntityName can't change after creation: " +
        "a change is refused (it needs unregister + recreate); omitted on update, they keep their value. On update, the fields in a passed list are added or updated and fields left out are kept; " +
        "remove one with unregister (kind=customapiparameter / customapiresponse). " +
        "Fields as JSON array: [{\"uniqueName\":\"Amount\",\"type\":\"Decimal\",\"isOptional\":false,\"logicalEntityName\":null,\"displayName\":null,\"description\":null}]. " +
        "Types: Boolean, DateTime, Decimal, Entity, EntityCollection, EntityReference, Float, Integer, Money, Picklist, String, StringArray, Guid.")]
    public static string RegisterCustomApi(Dataverse dv,
        [Description("Unique name with the publisher prefix, e.g. contoso_CalculateDiscount. This becomes the message name")] string uniqueName,
        [Description("None | AsyncOnly | SyncAndAsync: which extra steps others may register on this message. Required on create; can't change later")] string? allowedStepType = null,
        [Description("Global | Entity | EntityCollection (default Global on create)")] string? bindingType = null,
        [Description("Table logical name for Entity / EntityCollection binding")] string? boundEntity = null,
        [Description("true = Function (GET); needs at least one response property (default false on create)")] bool? isFunction = null,
        [Description("Hide from $metadata and Web API discovery (default false on create; on update omitted = unchanged)")] bool? isPrivate = null,
        string? displayName = null,
        string? description = null,
        [Description("Main operation plug-in type full name or id. On update: omitted = unchanged, empty string = unlink")] string? pluginType = null,
        [Description("Existing privilege name required to call the API. On update: omitted = unchanged, empty string = clear")] string? executePrivilegeName = null,
        [Description("JSON array of request parameters (see tool description)")] string? requestParameters = null,
        [Description("JSON array of response properties (isOptional not allowed)")] string? responseProperties = null,
        [Description("Unmanaged solution unique name; the uniqueName must start with its publisher prefix")] string? solutionUniqueName = null) => Run(() =>
    {
        int? bindingRequested = bindingType != null ? IndexOf(Bindings, bindingType, "bindingType") : null;
        int? stepTypeRequested = allowedStepType != null ? IndexOf(StepTypes, allowedStepType, "allowedStepType") : null;
        var req = ParseFields(requestParameters, "requestParameters");
        var resp = ParseFields(responseProperties, "responseProperties");
        if (resp?.FirstOrDefault(f => f.IsOptional == true) is { } opt) throw new McpException($"Response property {opt.UniqueName}: isOptional isn't allowed (it breaks solution import).");
        if (resp?.FirstOrDefault(f => f.Type == "EntityCollection" && f.LogicalEntityName != null) is { } ec)
            throw new McpException($"Response property {ec.UniqueName}: EntityCollection can't have logicalEntityName.");

        var svc = dv.Client;
        var q = Q("customapi", "bindingtype", "boundentitylogicalname", "isfunction", "allowedcustomprocessingsteptype");
        q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        var existing = svc.RetrieveMultiple(q).Entities.FirstOrDefault();
        // Omitted on update = the stored value, so the immutability checks below only fire on a real change.
        var binding = bindingRequested ?? existing?.GetAttributeValue<OptionSetValue>("bindingtype").Value ?? 0;
        var stepType = stepTypeRequested ?? existing?.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype").Value ?? throw new McpException("A new Custom API needs allowedStepType.");
        boundEntity ??= existing?.GetAttributeValue<string>("boundentitylogicalname");
        var function = isFunction ?? existing?.GetAttributeValue<bool>("isfunction") ?? false;
        if ((binding == 0) != string.IsNullOrEmpty(boundEntity))
            throw new McpException(binding == 0 ? "boundEntity is only for Entity / EntityCollection binding." : "Entity / EntityCollection binding needs boundEntity.");
        if (binding == 1 && req?.Any(f => f.UniqueName.Equals("Target", StringComparison.OrdinalIgnoreCase)) == true)
            throw new McpException("Entity-bound APIs get a 'Target' parameter from Dataverse; don't declare one.");
        if (solutionUniqueName != null && !uniqueName.StartsWith(PublisherPrefix(svc, solutionUniqueName) + "_", StringComparison.OrdinalIgnoreCase))
            throw new McpException($"uniqueName must start with '{PublisherPrefix(svc, solutionUniqueName)}_', the prefix of {solutionUniqueName}'s publisher.");

        if (function && (resp is { Count: 0 } || (resp == null && existing == null)))
            throw new McpException("A Function needs at least one response property (otherwise it is invisible and returns 404).");

        var api = new Entity("customapi");
        if (existing == null || isPrivate != null) api["isprivate"] = isPrivate == true;
        if (existing == null || displayName != null) api["displayname"] = displayName ?? uniqueName;
        if (existing == null || description != null) api["description"] = description ?? displayName ?? uniqueName;
        if (pluginType != null) api["plugintypeid"] = pluginType == "" ? null : ResolvePluginType(svc, pluginType).ToEntityReference();
        if (executePrivilegeName != null) api["executeprivilegename"] = executePrivilegeName == "" ? null : executePrivilegeName;

        var actions = new List<object>();
        Guid id;
        if (existing == null)
        {
            api["uniquename"] = uniqueName;
            api["name"] = uniqueName;
            api["bindingtype"] = new OptionSetValue(binding);
            if (binding != 0) api["boundentitylogicalname"] = boundEntity;
            api["isfunction"] = function;
            api["allowedcustomprocessingsteptype"] = new OptionSetValue(stepType);
            // Deep insert, as in Microsoft's sample: one transaction for the API and its fields.
            api.RelatedEntities[new Relationship("customapi_customapirequestparameter")] =
                new EntityCollection((req ?? []).Select(f => FieldEntity("customapirequestparameter", f, uniqueName)).ToList());
            api.RelatedEntities[new Relationship("customapi_customapiresponseproperty")] =
                new EntityCollection((resp ?? []).Select(f => FieldEntity("customapiresponseproperty", f, uniqueName)).ToList());
            id = CreateIn(svc, api, solutionUniqueName);
            actions.AddRange((req ?? []).Concat(resp ?? []).Select(f => new { f.UniqueName, Action = "created" }));
        }
        else
        {
            id = existing.Id;
            var errors = new List<string>();
            void Immutable(string field, object? was, object? now) { if (!Equals(was, now)) errors.Add($"{field}: {was ?? "(none)"} -> {now ?? "(none)"}"); }
            Immutable("bindingType", Bindings[existing.GetAttributeValue<OptionSetValue>("bindingtype").Value], Bindings[binding]);
            Immutable("boundEntity", NullIfEmpty(existing.GetAttributeValue<string>("boundentitylogicalname")), NullIfEmpty(boundEntity));
            Immutable("isFunction", existing.GetAttributeValue<bool>("isfunction"), function);
            Immutable("allowedStepType", StepTypes[existing.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype").Value], StepTypes[stepType]);
            var plans = new[] { ("customapirequestparameter", req), ("customapiresponseproperty", resp) }
                .Where(p => p.Item2 != null)
                .Select(p => (Table: p.Item1, Spec: p.Item2!, Current: In(svc, Q(p.Item1, "uniquename", "type", "isoptional", "logicalentityname"), "customapiid", [id])))
                .ToList();
            foreach (var (table, spec, current) in plans)
                foreach (var f in spec)
                    if (current.FirstOrDefault(c => c.GetAttributeValue<string>("uniquename") == f.UniqueName) is { } c)
                    {
                        Immutable($"{f.UniqueName}.type", FieldTypes[c.GetAttributeValue<OptionSetValue>("type").Value], f.Type);
                        if (table == "customapirequestparameter" && f.IsOptional != null) Immutable($"{f.UniqueName}.isOptional", c.GetAttributeValue<bool>("isoptional"), f.IsOptional.Value);
                        if (f.LogicalEntityName != null) Immutable($"{f.UniqueName}.logicalEntityName", NullIfEmpty(c.GetAttributeValue<string>("logicalentityname")), NullIfEmpty(f.LogicalEntityName));
                    }
            if (errors.Count > 0)
                throw new McpException($"These can't change after creation: {string.Join("; ", errors)}. Unregister the Custom API (kind=customapi) and register it again; steps on its message must be recreated.");

            api.Id = id;
            UpdateIn(svc, api, solutionUniqueName);
            foreach (var (table, spec, current) in plans)
            {
                foreach (var f in spec)
                {
                    var e = FieldEntity(table, f, uniqueName);
                    if (current.FirstOrDefault(c => c.GetAttributeValue<string>("uniquename") == f.UniqueName) is { } c)
                    {
                        // Omitted displayName / description stay unchanged.
                        var u = new Entity(table, c.Id);
                        if (f.DisplayName != null) u["displayname"] = f.DisplayName;
                        if (f.Description != null) u["description"] = f.Description;
                        if (u.Attributes.Count > 0) UpdateIn(svc, u, solutionUniqueName);
                        actions.Add(new { f.UniqueName, Action = "updated" });
                    }
                    else
                    {
                        e["customapiid"] = new EntityReference("customapi", id);
                        CreateIn(svc, e, solutionUniqueName);
                        actions.Add(new { f.UniqueName, Action = "created" });
                    }
                }
            }
        }

        var saved = svc.Retrieve("customapi", id, new ColumnSet("sdkmessageid", "plugintypeid"));
        return new
        {
            CustomApiId = id, UniqueName = uniqueName, Action = existing == null ? "created" : "updated", Solution = solutionUniqueName,
            SdkMessageId = saved.GetAttributeValue<EntityReference>("sdkmessageid")?.Id,
            MainOperationPluginTypeId = saved.GetAttributeValue<EntityReference>("plugintypeid")?.Id,
            Fields = actions,
        };
    });

    [McpServerTool(Name = "list_custom_apis", ReadOnly = true)]
    [Description("List Custom APIs with their main-operation plug-in type, request parameters, response properties and the extra steps registered on their message.")]
    public static string ListCustomApis(Dataverse dv,
        [Description("Exact unique name (optional)")] string? uniqueName = null,
        [Description("Only APIs in this solution (optional)")] string? solutionUniqueName = null) => Run(() =>
    {
        var svc = dv.Client;
        var q = Q("customapi", "uniquename", "displayname", "bindingtype", "boundentitylogicalname", "isfunction", "isprivate",
            "allowedcustomprocessingsteptype", "executeprivilegename", "plugintypeid", "sdkmessageid");
        if (uniqueName != null) q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, uniqueName);
        var pt = q.AddLink("plugintype", "plugintypeid", "plugintypeid", JoinOperator.LeftOuter);
        pt.EntityAlias = "pt";
        pt.Columns = new ColumnSet("typename");
        if (solutionUniqueName != null)
            q.AddLink("solutioncomponent", "customapiid", "objectid")
                .AddLink("solution", "solutionid", "solutionid").LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);
        var apis = svc.RetrieveMultiple(q).Entities;
        var ids = apis.Select(a => a.Id).ToList();
        var reqs = In(svc, Q("customapirequestparameter", "uniquename", "type", "isoptional", "logicalentityname", "customapiid"), "customapiid", ids);
        var resps = In(svc, Q("customapiresponseproperty", "uniquename", "type", "logicalentityname", "customapiid"), "customapiid", ids);
        var sq = StepQuery();
        sq.ColumnSet.AddColumn("sdkmessageid");
        var steps = In(svc, sq, "sdkmessageid", apis.Select(a => a.GetAttributeValue<EntityReference>("sdkmessageid")?.Id).OfType<Guid>());
        var images = ImagesFor(svc, steps.Select(s => s.Id));
        object Fields(List<Entity> rows, Guid apiId) => rows.Where(r => r.GetAttributeValue<EntityReference>("customapiid").Id == apiId).Select(r => new
        {
            r.Id,
            UniqueName = r.GetAttributeValue<string>("uniquename"),
            Type = FieldTypes[r.GetAttributeValue<OptionSetValue>("type").Value],
            IsOptional = r.GetAttributeValue<bool>("isoptional"),
            LogicalEntityName = r.GetAttributeValue<string>("logicalentityname"),
        });
        return apis.Select(a => new
        {
            a.Id,
            UniqueName = a.GetAttributeValue<string>("uniquename"),
            DisplayName = a.GetAttributeValue<string>("displayname"),
            BindingType = Bindings[a.GetAttributeValue<OptionSetValue>("bindingtype").Value],
            BoundEntity = a.GetAttributeValue<string>("boundentitylogicalname"),
            IsFunction = a.GetAttributeValue<bool>("isfunction"),
            IsPrivate = a.GetAttributeValue<bool>("isprivate"),
            AllowedStepType = StepTypes[a.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype").Value],
            ExecutePrivilegeName = a.GetAttributeValue<string>("executeprivilegename"),
            MainOperation = Aliased(a, "pt.typename"),
            RequestParameters = Fields(reqs, a.Id),
            ResponseProperties = Fields(resps, a.Id),
            Steps = steps.Where(s => s.GetAttributeValue<EntityReference>("sdkmessageid").Id == a.GetAttributeValue<EntityReference>("sdkmessageid")?.Id)
                .Select(s => StepView(s, images)),
        }).ToList();
    });

    // A step on a Custom API's message must respect the API's allowedcustomprocessingsteptype.
    static void CheckCustomApiStep(IOrganizationService svc, string message, bool async, int stage)
    {
        var q = Q("customapi", "allowedcustomprocessingsteptype");
        q.Criteria.AddCondition("uniquename", ConditionOperator.Equal, message);
        if (svc.RetrieveMultiple(q).Entities.FirstOrDefault() is not { } api) return;
        switch (api.GetAttributeValue<OptionSetValue>("allowedcustomprocessingsteptype").Value)
        {
            case 0: throw new McpException($"Custom API {message} allows no extra steps (allowedStepType None).");
            case 1 when !(async && stage == 40): throw new McpException($"Custom API {message} only allows PostOperation + Asynchronous steps (allowedStepType AsyncOnly).");
        }
    }

    // plugintype -> customapi is RemoveLink: deleting the type would leave the API running with no logic.
    static void RefuseIfCustomApis(IOrganizationService svc, IEnumerable<Guid> typeIds)
    {
        var apis = In(svc, Q("customapi", "uniquename"), "plugintypeid", typeIds);
        if (apis.Count > 0)
            throw new McpException($"Main operation of Custom API(s) {string.Join(", ", apis.Select(a => a.GetAttributeValue<string>("uniquename")))}. " +
                "Deleting it would silently unlink them. Point them at another type (register_custom_api pluginType) or unregister them first.");
    }

    static Entity FieldEntity(string table, ApiField f, string apiUniqueName)
    {
        var e = new Entity(table)
        {
            ["uniquename"] = f.UniqueName,
            ["name"] = $"{apiUniqueName}.{f.UniqueName}",
            ["displayname"] = f.DisplayName ?? f.UniqueName,
            ["description"] = f.Description ?? f.DisplayName ?? f.UniqueName,
            ["type"] = new OptionSetValue(Array.IndexOf(FieldTypes, f.Type)),
        };
        if (f.LogicalEntityName != null) e["logicalentityname"] = f.LogicalEntityName;
        if (table == "customapirequestparameter") e["isoptional"] = f.IsOptional == true;
        return e;
    }

    static List<ApiField>? ParseFields(string? json, string param)
    {
        if (json == null) return null;
        List<ApiField>? fields;
        try { fields = JsonSerializer.Deserialize<List<ApiField>>(json, FieldJson); }
        catch (JsonException e) { throw new McpException($"{param} must be a JSON array of fields: {e.Message}"); }
        fields ??= [];
        foreach (var f in fields)
        {
            if (string.IsNullOrWhiteSpace(f.UniqueName)) throw new McpException($"{param}: every field needs uniqueName.");
            if (!FieldTypes.Contains(f.Type)) throw new McpException($"{param}: {f.UniqueName} has type '{f.Type}'; use one of {string.Join(", ", FieldTypes)}.");
        }
        if (fields.GroupBy(f => f.UniqueName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } dup)
            throw new McpException($"{param}: {dup.Key} is listed twice.");
        return fields;
    }

    // SolutionUniqueName as a request parameter adds the row (and deep-inserted children) to the solution.
    static Guid CreateIn(IOrganizationService svc, Entity e, string? solution)
    {
        var req = new CreateRequest { Target = e };
        if (solution != null) req["SolutionUniqueName"] = solution;
        return ((CreateResponse)svc.Execute(req)).id;
    }

    static void UpdateIn(IOrganizationService svc, Entity e, string? solution)
    {
        var req = new UpdateRequest { Target = e };
        if (solution != null) req["SolutionUniqueName"] = solution;
        svc.Execute(req);
    }

    static int IndexOf(string[] values, string value, string param)
    {
        var i = Array.FindIndex(values, v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : throw new McpException($"{param} must be one of: {string.Join(", ", values)}.");
    }

    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
