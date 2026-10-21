using System.ComponentModel;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// Service endpoints and webhooks (PRT 'Register New Service Endpoint' / 'Register New Web Hook'): rows in serviceendpoint;
// their steps are ordinary steps whose eventhandler points at the endpoint (see register_step).
public static partial class PluginTools
{
    const int ComponentServiceEndpoint = 95, ContractWebhook = 8;
    static readonly Dictionary<string, int> Contracts = new(StringComparer.OrdinalIgnoreCase) { ["Queue"] = 6, ["Topic"] = 5, ["EventHub"] = 7, ["Webhook"] = ContractWebhook };
    static readonly Dictionary<string, int> WebhookAuth = new(StringComparer.OrdinalIgnoreCase) { ["WebhookKey"] = 4, ["HttpHeader"] = 5, ["HttpQueryString"] = 6 };
    static readonly Dictionary<string, int> MessageFormats = new(StringComparer.OrdinalIgnoreCase) { ["BinaryXml"] = 1, ["Json"] = 2, ["TextXml"] = 3 };
    static readonly Dictionary<string, int> UserClaims = new(StringComparer.OrdinalIgnoreCase) { ["None"] = 1, ["UserId"] = 2, ["UserInfo"] = 3 };

    [McpServerTool(Name = "register_service_endpoint")]
    [Description("Create or update (matched by name) a webhook or an Azure Service Bus queue / topic / Event Hub endpoint. Secrets are write-only: they are never returned, " +
        "and omitting them on update keeps the stored value. Register steps on it with register_step(serviceEndpoint=...).")]
    public static string RegisterServiceEndpoint(Dataverse dv,
        [Description("Endpoint name; also the key used to find an existing one")] string name,
        [Description("Webhook | Queue | Topic | EventHub (can't change after creation)")] string contract,
        [Description("Webhook only: absolute http(s) URL on port 80 or 443")] string? url = null,
        [Description("Webhook only: WebhookKey | HttpHeader | HttpQueryString")] string? authType = null,
        [Description("Webhook only: the key for WebhookKey, or a JSON object of name/value pairs for HttpHeader / HttpQueryString, e.g. {\"x-api-key\":\"...\"}")] string? authValue = null,
        [Description("Service Bus / Event Hub only: 'Endpoint=sb://<ns>.servicebus.windows.net/;SharedAccessKeyName=..;SharedAccessKey=..;EntityPath=..' (or SharedAccessSignature=.. instead of the key pair)")] string? connectionString = null,
        [Description("Service Bus / Event Hub only: Json | TextXml | BinaryXml (not for EventHub). Default Json on create; on update omitted = unchanged")] string? messageFormat = null,
        [Description("Service Bus / Event Hub only (webhooks accept only None): None | UserId | UserInfo, caller identity added to the message. Default None on create; on update omitted = unchanged")] string? userClaim = null,
        string? description = null,
        [Description("Unmanaged solution unique name to add the endpoint to (optional)")] string? solutionUniqueName = null) => Run(() =>
    {
        var svc = dv.Client;
        var contractValue = Lookup(Contracts, contract, "contract");
        var q = Q("serviceendpoint", "contract");
        q.Criteria.AddCondition("name", ConditionOperator.Equal, name);
        var hits = svc.RetrieveMultiple(q).Entities;
        if (hits.Count > 1) throw new McpException($"{hits.Count} service endpoints are named '{name}': {string.Join(", ", hits.Select(h => h.Id))}. Rename or unregister the extras.");
        var existing = hits.FirstOrDefault();
        var existingContract = existing?.GetAttributeValue<OptionSetValue>("contract").Value;
        if (existingContract == 2) existingContract = 6; // legacy Queue (2): Dataverse now only accepts PersistentQueue (6)
        if (existing != null && existingContract != contractValue)
            throw new McpException($"'{name}' already exists with another contract. The contract can't change; unregister it and create a new one.");

        var e = new Entity("serviceendpoint")
        {
            ["name"] = name,
        };
        if (existing == null)
        {
            e["contract"] = new OptionSetValue(contractValue);
            e["connectionmode"] = new OptionSetValue(1); // Normal
        }
        if (existing == null || userClaim != null) e["userclaim"] = new OptionSetValue(Lookup(UserClaims, userClaim ?? "None", "userClaim"));
        if (description != null) e["description"] = description;

        if (contractValue == ContractWebhook)
        {
            if (connectionString != null) throw new McpException("connectionString is for Service Bus / Event Hub, not webhooks.");
            if (url != null)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.Port is not (80 or 443))
                    throw new McpException("url must be an absolute http(s) URL on port 80 or 443.");
                e["url"] = url;
            }
            else if (existing == null) throw new McpException("A new webhook needs url.");
            if ((authType == null) != (authValue == null)) throw new McpException("Pass authType and authValue together.");
            if (authType != null)
            {
                var at = Lookup(WebhookAuth, authType, "authType");
                e["authtype"] = new OptionSetValue(at);
                e["authvalue"] = at == 4 ? authValue : SettingsXml(authValue!);
            }
            else if (existing == null) throw new McpException("A new webhook needs authType and authValue.");
        }
        else
        {
            if (url != null || authType != null || authValue != null) throw new McpException("url, authType and authValue are for webhooks; Service Bus and Event Hub take connectionString.");
            if (existing == null || messageFormat != null)
            {
                var fmt = Lookup(MessageFormats, messageFormat ?? "Json", "messageFormat");
                if (fmt == 1 && contractValue == 7) throw new McpException("Event Hub doesn't support BinaryXml.");
                e["messageformat"] = new OptionSetValue(fmt);
            }
            if (connectionString != null) ApplyConnectionString(e, connectionString);
            else if (existing == null) throw new McpException("A new Service Bus / Event Hub endpoint needs connectionString.");
        }

        Guid id;
        if (existing == null) id = svc.Create(e);
        else
        {
            e.Id = id = existing.Id;
            svc.Update(e);
        }
        if (solutionUniqueName != null) AddToSolution(svc, ComponentServiceEndpoint, id, solutionUniqueName, addRequired: false);

        var saved = svc.Retrieve("serviceendpoint", id, new ColumnSet("isauthvalueset", "issaskeyset", "issastokenset"));
        return new
        {
            ServiceEndpointId = id, Name = name, Contract = contract, Action = existing == null ? "created" : "updated", Solution = solutionUniqueName,
            SecretSet = saved.GetAttributeValue<bool>("isauthvalueset") || saved.GetAttributeValue<bool>("issaskeyset") || saved.GetAttributeValue<bool>("issastokenset"),
            Note = contractValue == ContractWebhook ? null : "The SAS key isn't exported with solutions; set it again after importing into another environment.",
        };
    });

    [McpServerTool(Name = "list_service_endpoints", ReadOnly = true)]
    [Description("List webhooks and Service Bus / Event Hub endpoints with their steps and images. Secrets are never returned, only whether one is set.")]
    public static string ListServiceEndpoints(Dataverse dv, [Description("Exact endpoint name (optional)")] string? name = null) => Run(() =>
    {
        var svc = dv.Client;
        var q = Q("serviceendpoint", "name", "contract", "url", "namespaceaddress", "path", "authtype", "saskeyname", "messageformat", "userclaim",
            "isauthvalueset", "issaskeyset", "issastokenset", "ismanaged");
        if (name != null) q.Criteria.AddCondition("name", ConditionOperator.Equal, name);
        var endpoints = svc.RetrieveMultiple(q).Entities;
        var steps = In(svc, StepQuery(), "eventhandler", endpoints.Select(e => e.Id));
        var images = ImagesFor(svc, steps.Select(s => s.Id));
        string? Label(Entity e, string attr, Dictionary<string, int> map) =>
            e.GetAttributeValue<OptionSetValue>(attr) is { } v ? map.FirstOrDefault(kv => kv.Value == v.Value).Key ?? v.Value.ToString() : null;
        return endpoints.Select(e => new
        {
            e.Id,
            Name = e.GetAttributeValue<string>("name"),
            Contract = e.GetAttributeValue<OptionSetValue>("contract")?.Value == 2 ? "Queue" : Label(e, "contract", Contracts),
            Url = e.GetAttributeValue<string>("url"),
            NamespaceAddress = e.GetAttributeValue<string>("namespaceaddress"),
            Path = e.GetAttributeValue<string>("path"),
            AuthType = e.GetAttributeValue<OptionSetValue>("authtype")?.Value switch { 2 => "SASKey", 3 => "SASToken", _ => Label(e, "authtype", WebhookAuth) },
            SasKeyName = e.GetAttributeValue<string>("saskeyname"),
            MessageFormat = Label(e, "messageformat", MessageFormats),
            UserClaim = Label(e, "userclaim", UserClaims),
            SecretSet = e.GetAttributeValue<bool>("isauthvalueset") || e.GetAttributeValue<bool>("issaskeyset") || e.GetAttributeValue<bool>("issastokenset"),
            IsManaged = e.GetAttributeValue<bool>("ismanaged"),
            Steps = steps.Where(s => s.GetAttributeValue<EntityReference>("eventhandler").Id == e.Id).Select(s => StepView(s, images)),
        }).ToList();
    });

    static Entity ResolveEndpoint(IOrganizationService svc, string nameOrId)
    {
        var q = Q("serviceendpoint", "name", "contract");
        if (Guid.TryParse(nameOrId, out var id)) q.Criteria.AddCondition("serviceendpointid", ConditionOperator.Equal, id);
        else q.Criteria.AddCondition("name", ConditionOperator.Equal, nameOrId);
        var hits = svc.RetrieveMultiple(q).Entities;
        return hits.Count switch
        {
            0 => throw new McpException($"Service endpoint '{nameOrId}' not found. Run register_service_endpoint first."),
            1 => hits[0],
            _ => throw new McpException($"{hits.Count} endpoints are named '{nameOrId}'. Pass one of these ids: {string.Join(", ", hits.Select(h => h.Id))}"),
        };
    }

    // PRT's connection-string parsing: Endpoint -> namespaceaddress, EntityPath -> path, key pair or SAS token.
    static void ApplyConnectionString(Entity e, string connectionString)
    {
        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        string Part(string key) => parts.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new McpException($"connectionString has no {key}.");
        var endpoint = Part("Endpoint");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u) || u.Scheme != "sb") throw new McpException("Endpoint must be sb://<namespace>.servicebus.windows.net/.");
        e["namespaceaddress"] = endpoint;
        e["namespaceformat"] = new OptionSetValue(2); // Namespace Address
        e["solutionnamespace"] = u.Host.Split('.')[0];
        e["path"] = Part("EntityPath");
        if (parts.ContainsKey("SharedAccessSignature"))
        {
            e["authtype"] = new OptionSetValue(3); // SASToken
            e["sastoken"] = Part("SharedAccessSignature");
        }
        else
        {
            e["authtype"] = new OptionSetValue(2); // SASKey
            e["saskeyname"] = Part("SharedAccessKeyName");
            e["saskey"] = Part("SharedAccessKey");
        }
    }

    // HttpHeader / HttpQueryString authvalue format used by PRT; XElement escapes quotes and ampersands.
    static string SettingsXml(string json)
    {
        Dictionary<string, string>? pairs;
        try { pairs = JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (JsonException) { throw new McpException("authValue must be a JSON object of name/value strings for HttpHeader / HttpQueryString."); }
        if (pairs is not { Count: > 0 }) throw new McpException("authValue needs at least one name/value pair.");
        return new XElement("settings", pairs.Select(p => new XElement("setting", new XAttribute("name", p.Key), new XAttribute("value", p.Value))))
            .ToString(SaveOptions.DisableFormatting);
    }

    static int Lookup(Dictionary<string, int> map, string value, string param) =>
        map.TryGetValue(value, out var v) ? v : throw new McpException($"{param} must be one of: {string.Join(", ", map.Keys)}.");
}
