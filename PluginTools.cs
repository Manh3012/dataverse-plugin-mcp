using System.ComponentModel;
using System.Text.Json;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// PRT-equivalent operations.
[McpServerToolType]
public static partial class PluginTools
{
    const int ComponentPluginAssembly = 91, ComponentStep = 92;
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [McpServerTool(Name = "inspect_assembly", ReadOnly = true)]
    [Description("Read a compiled plug-in DLL locally (no Dataverse call): name, version, culture, public key token, SHA1 and the IPlugin / CodeActivity classes that would be registered.")]
    public static string InspectAssembly([Description("Absolute path to the plug-in .dll")] string dllPath) => Run(() =>
    {
        var i = AssemblyInspector.Inspect(dllPath);
        return new { i.Path, i.Name, i.Version, i.Culture, i.PublicKeyToken, Signed = i.PublicKeyToken != null, i.SourceHash, i.Types };
    });

    [McpServerTool(Name = "list_registrations", ReadOnly = true)]
    [Description("List registered plug-in assemblies with their plug-in types, steps, images and the solutions each assembly belongs to. With no filter, Microsoft.* assemblies are skipped.")]
    public static string ListRegistrations(Dataverse dv,
        [Description("Exact plug-in assembly name (optional)")] string? assemblyName = null,
        [Description("Only assemblies in this solution, by unique name (optional)")] string? solutionUniqueName = null) => Run(() =>
    {
        var svc = dv.Client;
        var q = Q("pluginassembly", "name", "version", "culture", "publickeytoken", "isolationmode", "packageid");
        if (assemblyName != null) q.Criteria.AddCondition("name", ConditionOperator.Equal, assemblyName);
        else q.Criteria.AddCondition("name", ConditionOperator.NotLike, "Microsoft.%");
        if (solutionUniqueName != null)
        {
            var sc = q.AddLink("solutioncomponent", "pluginassemblyid", "objectid");
            sc.LinkCriteria.AddCondition("componenttype", ConditionOperator.Equal, ComponentPluginAssembly);
            sc.AddLink("solution", "solutionid", "solutionid").LinkCriteria.AddCondition("uniquename", ConditionOperator.Equal, solutionUniqueName);
        }
        var asms = svc.RetrieveMultiple(q).Entities;
        var asmIds = asms.Select(a => a.Id).ToList();

        var types = In(svc, Q("plugintype", "typename", "isworkflowactivity", "pluginassemblyid"), "pluginassemblyid", asmIds);
        var steps = In(svc, StepQuery(), "eventhandler", types.Select(t => t.Id));
        var images = ImagesFor(svc, steps.Select(s => s.Id));
        var solQ = Q("solutioncomponent", "objectid");
        solQ.Criteria.AddCondition("componenttype", ConditionOperator.Equal, ComponentPluginAssembly);
        var sl = solQ.AddLink("solution", "solutionid", "solutionid");
        sl.EntityAlias = "s";
        sl.Columns = new ColumnSet("uniquename");
        var sols = In(svc, solQ, "objectid", asmIds);

        return asms.Select(a => new
        {
            a.Id,
            Name = a.GetAttributeValue<string>("name"),
            Version = a.GetAttributeValue<string>("version"),
            Culture = a.GetAttributeValue<string>("culture"),
            PublicKeyToken = a.GetAttributeValue<string>("publickeytoken"),
            IsolationMode = a.GetAttributeValue<OptionSetValue>("isolationmode")?.Value == 2 ? "Sandbox" : "None",
            PackageId = a.GetAttributeValue<EntityReference>("packageid")?.Id,
            Solutions = sols.Where(s => s.GetAttributeValue<Guid>("objectid") == a.Id).Select(s => Aliased(s, "s.uniquename")),
            Types = types.Where(t => t.GetAttributeValue<EntityReference>("pluginassemblyid").Id == a.Id).Select(t => new
            {
                t.Id,
                TypeName = t.GetAttributeValue<string>("typename"),
                IsWorkflowActivity = t.GetAttributeValue<bool>("isworkflowactivity"),
                Steps = steps.Where(s => s.GetAttributeValue<EntityReference>("eventhandler").Id == t.Id).Select(s => StepView(s, images)),
            }),
        }).ToList();
    });

    // Steps from StepQuery(), with their images.
    static object StepView(Entity s, List<Entity> images) => new
    {
        s.Id,
        Name = s.GetAttributeValue<string>("name"),
        Message = Aliased(s, "m.name"),
        Entity = Aliased(s, "f.primaryobjecttypecode") ?? "any",
        Stage = StageName(s.GetAttributeValue<OptionSetValue>("stage").Value),
        Mode = s.GetAttributeValue<OptionSetValue>("mode").Value == 1 ? "Asynchronous" : "Synchronous",
        Rank = s.GetAttributeValue<int>("rank"),
        Enabled = s.GetAttributeValue<OptionSetValue>("statecode").Value == 0,
        FilteringAttributes = s.GetAttributeValue<string>("filteringattributes"),
        UnsecureConfiguration = s.GetAttributeValue<string>("configuration"),
        RunAsUserId = s.GetAttributeValue<EntityReference>("impersonatinguserid")?.Id,
        Images = images.Where(i => i.GetAttributeValue<EntityReference>("sdkmessageprocessingstepid").Id == s.Id).Select(i => new
        {
            i.Id,
            Alias = i.GetAttributeValue<string>("entityalias"),
            ImageType = (i.GetAttributeValue<OptionSetValue>("imagetype").Value) switch { 0 => "PreImage", 1 => "PostImage", _ => "Both" },
            Attributes = i.GetAttributeValue<string>("attributes"),
            MessagePropertyName = i.GetAttributeValue<string>("messagepropertyname"),
        }),
    };

    static List<Entity> ImagesFor(IOrganizationService svc, IEnumerable<Guid> stepIds) =>
        In(svc, Q("sdkmessageprocessingstepimage", "entityalias", "imagetype", "attributes", "messagepropertyname", "sdkmessageprocessingstepid"),
            "sdkmessageprocessingstepid", stepIds);

    [McpServerTool(Name = "register_assembly")]
    [Description("Register or update a plug-in assembly from a DLL (PRT 'Register New Assembly' / 'Update'). Matches an existing registration by name + culture + public key token. Same major.minor: updates in place (skipped when the SHA1 is unchanged). Creates plugintype rows for new IPlugin/CodeActivity classes. Refuses when a registered plug-in type is missing from the DLL. Optionally adds the assembly to an unmanaged solution.")]
    public static string RegisterAssembly(Dataverse dv,
        [Description("Absolute path to the built plug-in .dll")] string dllPath,
        [Description("Unmanaged solution unique name to add the assembly to (optional; otherwise it is only in the Default Solution)")] string? solutionUniqueName = null,
        [Description("true only when the DLL's major or minor version differs from the registered one: registers a second, side-by-side assembly whose steps must be recreated. Default false refuses.")] bool registerSideBySide = false) => Run(() =>
    {
        var svc = dv.Client;
        var info = AssemblyInspector.Inspect(dllPath);
        if (info.Types.Count == 0)
            throw new McpException($"{info.Name} contains no public, non-abstract IPlugin or CodeActivity classes.");
        if (info.PublicKeyToken == null && info.Types.Any(t => !t.IsWorkflowActivity))
            throw new McpException("Assemblies containing plug-ins must be strong-name signed (enable signing in the project, or ship it as a plug-in package).");

        var q = Q("pluginassembly", "version", "publickeytoken", "sourcehash", "packageid");
        q.Criteria.AddCondition("name", ConditionOperator.Equal, info.Name);
        q.Criteria.AddCondition("culture", ConditionOperator.Equal, info.Culture);
        var sameName = svc.RetrieveMultiple(q).Entities;
        if (sameName.FirstOrDefault(e => e.Contains("packageid")) is { } owned)
            throw new McpException($"{info.Name} belongs to plug-in package {owned.GetAttributeValue<EntityReference>("packageid").Id}. " +
                "Rebuild the .nupkg and use register_package instead.");
        var sameIdentity = sameName
            .Where(e => string.Equals(e.GetAttributeValue<string>("publickeytoken"), info.PublicKeyToken, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var v = Version.Parse(info.Version);
        var match = sameIdentity.FirstOrDefault(e =>
            Version.TryParse(e.GetAttributeValue<string>("version"), out var rv) && rv.Major == v.Major && rv.Minor == v.Minor);
        if (match == null && sameIdentity.Count > 0 && !registerSideBySide)
            throw new McpException($"{info.Name} is registered as {string.Join(", ", sameIdentity.Select(e => e.GetAttributeValue<string>("version")))} but the DLL is {info.Version}. " +
                "A major/minor change cannot be updated in place (Dataverse treats it as a different assembly). Keep major.minor and bump build/revision, " +
                "or call again with registerSideBySide=true and move the steps to the new types.");

        Guid asmId;
        string action;
        List<Entity> registered;
        if (match == null)
        {
            asmId = svc.Create(new Entity("pluginassembly")
            {
                ["name"] = info.Name,
                ["version"] = info.Version,
                ["culture"] = info.Culture,
                ["publickeytoken"] = info.PublicKeyToken,
                ["sourcehash"] = info.SourceHash,
                ["content"] = Convert.ToBase64String(info.Content),
                ["isolationmode"] = new OptionSetValue(2), // Sandbox: the only option online
                ["sourcetype"] = new OptionSetValue(0),    // Database
            });
            action = "created";
            registered = [];
        }
        else
        {
            asmId = match.Id;
            registered = TypesOf(svc, asmId);
            var orphans = registered.Select(t => t.GetAttributeValue<string>("typename"))
                .Where(n => !info.Types.Any(t => string.Equals(t.TypeName, n, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (orphans.Count > 0)
                throw new McpException($"Registered plug-in types missing from the new DLL: {string.Join(", ", orphans)}. " +
                    "Dataverse rejects the update ('does not contain the required types'). Unregister their steps and the types first, then retry.");

            if (match.GetAttributeValue<string>("sourcehash") == info.SourceHash && match.GetAttributeValue<string>("version") == info.Version)
                action = "unchanged";
            else
            {
                svc.Update(new Entity("pluginassembly", asmId)
                {
                    ["content"] = Convert.ToBase64String(info.Content),
                    ["version"] = info.Version,
                    ["sourcehash"] = info.SourceHash,
                });
                action = "updated";
            }
        }

        // Dataverse does not create plugintype rows for classic assemblies; PRT, spkl and Daxif all create them.
        var created = new List<string>();
        foreach (var t in info.Types.Where(t => !registered.Any(r =>
                     string.Equals(r.GetAttributeValue<string>("typename"), t.TypeName, StringComparison.OrdinalIgnoreCase))))
        {
            var pt = new Entity("plugintype")
            {
                ["pluginassemblyid"] = new EntityReference("pluginassembly", asmId),
                ["typename"] = t.TypeName,
                ["name"] = t.TypeName,
                ["friendlyname"] = Guid.NewGuid().ToString(), // same as PRT
            };
            if (t.IsWorkflowActivity) pt["workflowactivitygroupname"] = $"{info.Name} ({info.Version})";
            svc.Create(pt);
            created.Add(t.TypeName);
        }

        if (solutionUniqueName != null) AddToSolution(svc, ComponentPluginAssembly, asmId, solutionUniqueName, addRequired: true);
        return new { AssemblyId = asmId, info.Name, info.Version, Action = action, TypesCreated = created, TypesTotal = info.Types.Count, Solution = solutionUniqueName };
    });

    [McpServerTool(Name = "register_step")]
    [Description("Create or update an SDK message processing step (PRT 'Register New Step'). The handler is a plug-in type or a service endpoint / webhook (pass exactly one). " +
        "The existing step is found by name on the same handler, or without name by handler + message + primaryEntity; with neither name nor primaryEntity, a handler that already has steps on that message is refused rather than given a second, any-entity step. " +
        "The message of an existing step can't change. On update, an omitted field stays unchanged (stage, mode, entity, ...) and an empty string clears it (filteringAttributes, unsecureConfiguration, runAsUserId; primaryEntity \"\" = any entity).")]
    public static string RegisterStep(Dataverse dv,
        [Description("SDK message name, e.g. Create, Update, Delete, Assign, SetState, or a Custom API unique name")] string message,
        [Description("PreValidation | PreOperation | PostOperation. Required on create; on update omitted = unchanged")] string? stage = null,
        [Description("Plug-in type full name (e.g. Contoso.Plugins.AccountCreate) or plugintype id")] string? pluginType = null,
        [Description("Service endpoint or webhook name or id, instead of pluginType. Service Bus / Event Hub steps must be PostOperation + Asynchronous; no configuration")] string? serviceEndpoint = null,
        [Description("Synchronous | Asynchronous (Asynchronous requires PostOperation). Default on create: Asynchronous for a service endpoint / webhook in PostOperation, otherwise Synchronous")] string? mode = null,
        [Description("Table logical name, e.g. account. Omit for any entity")] string? primaryEntity = null,
        [Description("Comma-separated column logical names that trigger the step (recommended for Update)")] string? filteringAttributes = null,
        [Description("Execution order within the stage (default 1 on create)")] int? rank = null,
        [Description("Step name; default on create '<type>: <message> of <entity>'. When passed, the key used to find an existing step")] string? name = null,
        [Description("Step description; defaults to the name")] string? description = null,
        [Description("Unsecure configuration passed to the plug-in constructor")] string? unsecureConfiguration = null,
        [Description("Secure configuration (not exported with solutions)")] string? secureConfiguration = null,
        [Description("systemuserid to run as; omit for the calling user")] string? runAsUserId = null,
        [Description("Delete the system job when it succeeds (asynchronous only; default false on create)")] bool? asyncAutoDelete = null,
        [Description("Unmanaged solution unique name to add the step to (add the assembly too)")] string? solutionUniqueName = null) => Run(() =>
    {
        var svc = dv.Client;
        int? stageRequested = stage?.ToLowerInvariant() switch
        {
            null => null, "prevalidation" => 10, "preoperation" => 20, "postoperation" => 40,
            _ => throw new McpException("stage must be PreValidation, PreOperation or PostOperation."),
        };
        bool? modeAsync = mode?.ToLowerInvariant() switch
        {
            null => null, "synchronous" or "sync" => false, "asynchronous" or "async" => true,
            _ => throw new McpException("mode must be Synchronous or Asynchronous."),
        };

        if ((pluginType == null) == (serviceEndpoint == null)) throw new McpException("Pass exactly one of pluginType or serviceEndpoint.");
        EntityReference handler;
        string handlerName;
        int? contract = null;
        if (pluginType != null)
        {
            var type = ResolvePluginType(svc, pluginType);
            handler = type.ToEntityReference();
            handlerName = type.GetAttributeValue<string>("typename");
        }
        else
        {
            var endpoint = ResolveEndpoint(svc, serviceEndpoint!);
            handler = endpoint.ToEntityReference();
            handlerName = endpoint.GetAttributeValue<string>("name");
            if (unsecureConfiguration != null || secureConfiguration != null)
                throw new McpException("Service endpoint and webhook steps take no configuration.");
            contract = endpoint.GetAttributeValue<OptionSetValue>("contract").Value;
        }

        var mq = Q("sdkmessage");
        mq.Criteria.AddCondition("name", ConditionOperator.Equal, message);
        var messageId = Single(svc, mq, $"SDK message '{message}' not found.").Id;

        Guid? filterId = null;
        if (!string.IsNullOrEmpty(primaryEntity))
        {
            var fq = Q("sdkmessagefilter");
            fq.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, messageId);
            fq.Criteria.AddCondition("primaryobjecttypecode", ConditionOperator.Equal, primaryEntity);
            fq.Criteria.AddCondition("iscustomprocessingstepallowed", ConditionOperator.Equal, true);
            filterId = Single(svc, fq, $"Message '{message}' does not allow custom steps on '{primaryEntity}'.").Id;
        }

        var eq = Q("sdkmessageprocessingstep", "name", "sdkmessageid", "sdkmessagefilterid", "stage", "sdkmessageprocessingstepsecureconfigid", "mode");
        eq.Criteria.AddCondition("eventhandler", ConditionOperator.Equal, handler.Id);
        Entity? existing;
        if (name != null)
        {
            eq.Criteria.AddCondition("name", ConditionOperator.Equal, name);
            existing = svc.RetrieveMultiple(eq).Entities.FirstOrDefault();
            if (existing != null && existing.GetAttributeValue<EntityReference>("sdkmessageid").Id != messageId)
                throw new McpException($"Step '{name}' is registered on another message. The message can't change on update: pass that message, or unregister the step and register a new one.");
        }
        else
        {
            // No name: the step is found by handler + message + entity, so a step named by PRT or a step whose entity is kept
            // is never duplicated by a default-named one.
            eq.Criteria.AddCondition("sdkmessageid", ConditionOperator.Equal, messageId);
            var onMessage = svc.RetrieveMultiple(eq).Entities;
            var matches = onMessage.Where(s => s.GetAttributeValue<EntityReference>("sdkmessagefilterid")?.Id == filterId).ToList();
            if (matches.Count > 1)
                throw new McpException($"{matches.Count} steps of {handlerName} match {message}: {string.Join(", ", matches.Select(m => $"'{m.GetAttributeValue<string>("name")}'"))}. Pass name.");
            existing = matches.FirstOrDefault();
            if (existing == null && primaryEntity == null && onMessage.Count > 0)
                throw new McpException($"{handlerName} already has {message} step(s) on a table: {string.Join(", ", onMessage.Select(m => $"'{m.GetAttributeValue<string>("name")}'"))}. " +
                    "Pass name (or primaryEntity) to update one, or primaryEntity=\"\" to register a new step for any entity.");
        }
        name ??= existing?.GetAttributeValue<string>("name") ?? $"{handlerName}: {message} of {(string.IsNullOrEmpty(primaryEntity) ? "any Entity" : primaryEntity)}";
        var stageValue = stageRequested ?? existing?.GetAttributeValue<OptionSetValue>("stage").Value ?? throw new McpException("A new step needs stage.");

        // Omitted mode keeps the step's mode; a new endpoint step defaults to Asynchronous where allowed,
        // so a failing endpoint doesn't fail the user's save.
        var async = modeAsync ?? (existing != null ? existing.GetAttributeValue<OptionSetValue>("mode").Value == 1 : contract != null && stageValue == 40);
        if (async && stageValue != 40) throw new McpException("Asynchronous steps can only be registered in PostOperation.");
        if (contract != null && contract != ContractWebhook && !(async && stageValue == 40))
            throw new McpException("Service Bus and Event Hub steps must be PostOperation + Asynchronous (same rule as PRT).");
        CheckCustomApiStep(svc, message, async, stageValue);

        var step = new Entity("sdkmessageprocessingstep")
        {
            ["stage"] = new OptionSetValue(stageValue),
            ["mode"] = new OptionSetValue(async ? 1 : 0),
        };
        if (existing == null)
        {
            step["name"] = name;
            step["eventhandler"] = handler;
            step["sdkmessageid"] = new EntityReference("sdkmessage", messageId);
            step["supporteddeployment"] = new OptionSetValue(0); // server only
        }
        if (existing == null || description != null) step["description"] = description ?? name;
        if (existing == null || rank != null) step["rank"] = rank ?? 1;
        if (existing == null || asyncAutoDelete != null || !async) step["asyncautodelete"] = async && asyncAutoDelete == true;
        // Omitted (null) = unchanged on update; "" clears. Like PRT, empty fields are left out on create (avoids dependency-calculation errors).
        void Optional(string key, string? value, Func<string, object> set, object? cleared = null)
        {
            if (value == "") { if (existing != null) step[key] = cleared; }
            else if (value != null) step[key] = set(value);
        }
        Optional("sdkmessagefilterid", primaryEntity, _ => new EntityReference("sdkmessagefilter", filterId!.Value));
        Optional("filteringattributes", filteringAttributes?.Replace(" ", ""), a => a, cleared: "");
        Optional("configuration", unsecureConfiguration, c => c);
        Optional("impersonatinguserid", runAsUserId, u => new EntityReference("systemuser", Guid.Parse(u)));

        if (secureConfiguration != null)
        {
            var secureRef = existing?.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid");
            if (secureRef != null)
                svc.Update(new Entity(secureRef.LogicalName, secureRef.Id) { ["secureconfig"] = secureConfiguration });
            else
                step["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig",
                    svc.Create(new Entity("sdkmessageprocessingstepsecureconfig") { ["secureconfig"] = secureConfiguration }));
        }

        Guid stepId;
        if (existing == null) stepId = svc.Create(step);
        else
        {
            step.Id = stepId = existing.Id;
            svc.Update(step);
        }
        if (solutionUniqueName != null)
        {
            AddToSolution(svc, ComponentStep, stepId, solutionUniqueName, addRequired: false);
            // An endpoint step without its endpoint fails solution import on the missing dependency.
            if (handler.LogicalName == "serviceendpoint") AddToSolution(svc, ComponentServiceEndpoint, handler.Id, solutionUniqueName, addRequired: false);
        }
        return new
        {
            StepId = stepId, Name = name, Action = existing == null ? "created" : "updated", Handler = handlerName,
            Message = message, Entity = primaryEntity == null && existing != null ? "(unchanged)" : string.IsNullOrEmpty(primaryEntity) ? "any" : primaryEntity, Stage = StageName(stageValue),
            Mode = async ? "Asynchronous" : "Synchronous", Solution = solutionUniqueName,
        };
    });

    [McpServerTool(Name = "register_image")]
    [Description("Create or update (matched by step + alias) a pre/post entity image on a step. messagePropertyName defaults like PRT: Create=Id, SetState=EntityMoniker, Send/DeliverIncoming/DeliverPromote=EmailId, otherwise Target. Images travel with their step in solutions.")]
    public static string RegisterImage(Dataverse dv,
        [Description("sdkmessageprocessingstepid")] string stepId,
        [Description("Alias used to read PreEntityImages / PostEntityImages")] string alias,
        [Description("PreImage | PostImage | Both. Required on create; on update omitted = unchanged")] string? imageType = null,
        [Description("Comma-separated columns to capture. Empty = all columns (slow, avoid). Omitted: all columns on create, unchanged on update")] string? attributes = null,
        [Description("Override the message property name (rarely needed). On update omitted = unchanged")] string? messagePropertyName = null) => Run(() =>
    {
        var svc = dv.Client;
        int? requested = imageType?.ToLowerInvariant() switch
        {
            null => null, "preimage" or "pre" => 0, "postimage" or "post" => 1, "both" => 2,
            _ => throw new McpException("imageType must be PreImage, PostImage or Both."),
        };
        var iq = Q("sdkmessageprocessingstepimage", "attributes", "messagepropertyname", "imagetype");
        iq.Criteria.AddCondition("sdkmessageprocessingstepid", ConditionOperator.Equal, Guid.Parse(stepId));
        iq.Criteria.AddCondition("entityalias", ConditionOperator.Equal, alias);
        var existing = svc.RetrieveMultiple(iq).Entities.FirstOrDefault();
        var type = requested ?? existing?.GetAttributeValue<OptionSetValue>("imagetype").Value ?? throw new McpException("A new image needs imageType.");
        var sq = StepQuery();
        sq.Criteria.AddCondition("sdkmessageprocessingstepid", ConditionOperator.Equal, Guid.Parse(stepId));
        var step = Single(svc, sq, $"Step {stepId} not found.");
        var message = Aliased(step, "m.name");
        if (type != 0 && step.GetAttributeValue<OptionSetValue>("stage").Value != 40)
            throw new McpException("Post images require a PostOperation step.");
        if (type != 1 && message == "Create") throw new McpException("Create has no pre image (the row does not exist yet).");
        if (type != 0 && message == "Delete") throw new McpException("Delete has no post image (the row no longer exists).");

        var prop = messagePropertyName ?? message switch
        {
            "Create" => "Id",
            "SetState" or "SetStateDynamicEntity" => "EntityMoniker",
            "Send" or "DeliverIncoming" or "DeliverPromote" => "EmailId",
            _ => "Target",
        };
        // Step and alias are the match key (unchanged values); Dataverse fails an image update without them.
        var image = new Entity("sdkmessageprocessingstepimage")
        {
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", step.Id),
            ["entityalias"] = alias,
        };
        if (existing == null) image["name"] = alias; // on update the name is kept (PRT lets it differ from the alias)
        if (existing == null || requested != null) image["imagetype"] = new OptionSetValue(type);
        // Omitted attributes / messagePropertyName stay unchanged on update.
        if (existing == null || messagePropertyName != null) image["messagepropertyname"] = prop;
        else prop = existing.GetAttributeValue<string>("messagepropertyname");
        if (existing == null || attributes != null) image["attributes"] = attributes?.Replace(" ", "") ?? "";
        var savedAttributes = image.Contains("attributes") ? image["attributes"] : existing!.GetAttributeValue<string>("attributes") ?? "";
        Guid imageId;
        if (existing == null) imageId = svc.Create(image);
        else
        {
            image.Id = imageId = existing.Id;
            svc.Update(image);
        }
        return new { ImageId = imageId, Action = existing == null ? "created" : "updated", Alias = alias, ImageType = type switch { 0 => "PreImage", 1 => "PostImage", _ => "Both" }, MessagePropertyName = prop, Attributes = savedAttributes };
    });

    [McpServerTool(Name = "set_step_state")]
    [Description("Enable or disable a step (PRT Enable / Disable).")]
    public static string SetStepState(Dataverse dv, [Description("sdkmessageprocessingstepid")] string stepId, bool enabled) => Run(() =>
    {
        dv.Client.Update(new Entity("sdkmessageprocessingstep", Guid.Parse(stepId))
        {
            ["statecode"] = new OptionSetValue(enabled ? 0 : 1),
            ["statuscode"] = new OptionSetValue(enabled ? 1 : 2),
        });
        return new { StepId = stepId, Enabled = enabled };
    });

    [McpServerTool(Name = "unregister", Destructive = true)]
    [Description("Delete a registration (PRT Unregister). kind: image | step (also deletes its images and secure config) | type (refused while steps use it) | assembly (refused while any of its types has steps; its types are deleted with it) | package (refused while any of its types has steps; its assemblies and types are deleted with it) | endpoint (service endpoint or webhook; refused while steps use it) | customapi (refused while steps are registered on its message; its parameters and properties are deleted with it) | customapiparameter | customapiresponse. " +
        "type / assembly / package are also refused while a Custom API uses one of their types as its main operation.")]
    public static string Unregister(Dataverse dv,
        [Description("image | step | type | assembly | package | endpoint | customapi | customapiparameter | customapiresponse")] string kind,
        [Description("Id of the row to delete")] string id) => Run(() =>
    {
        var svc = dv.Client;
        var gid = Guid.Parse(id);
        switch (kind.ToLowerInvariant())
        {
            case "image":
                svc.Delete("sdkmessageprocessingstepimage", gid);
                break;
            case "step":
                DeleteStep(svc, gid);
                break;
            case "type":
                RefuseIfSteps(StepsFor(svc, [gid]));
                RefuseIfCustomApis(svc, [gid]);
                svc.Delete("plugintype", gid);
                break;
            case "assembly":
                if (svc.Retrieve("pluginassembly", gid, new ColumnSet("packageid")).GetAttributeValue<EntityReference>("packageid") is { } pkg)
                    throw new McpException($"This assembly belongs to plug-in package {pkg.Id}. Unregister kind=package instead.");
                var assemblyTypes = TypesOf(svc, gid).Select(t => t.Id).ToList();
                RefuseIfSteps(StepsFor(svc, assemblyTypes));
                RefuseIfCustomApis(svc, assemblyTypes);
                svc.Delete("pluginassembly", gid);
                break;
            case "package":
                var packageTypes = PackageTypes(svc, gid).Select(t => t.Id).ToList();
                RefuseIfSteps(StepsFor(svc, packageTypes));
                RefuseIfCustomApis(svc, packageTypes);
                svc.Delete("pluginpackage", gid); // cascades to its assemblies and types
                break;
            case "endpoint":
                RefuseIfSteps(StepsFor(svc, [gid]));
                svc.Delete("serviceendpoint", gid);
                break;
            case "customapi":
                if (svc.Retrieve("customapi", gid, new ColumnSet("sdkmessageid")).GetAttributeValue<EntityReference>("sdkmessageid") is { } msg)
                    RefuseIfSteps(In(svc, StepQuery(), "sdkmessageid", [msg.Id]));
                svc.Delete("customapi", gid); // cascades to its parameters and properties
                break;
            case "customapiparameter":
                svc.Delete("customapirequestparameter", gid);
                break;
            case "customapiresponse":
                svc.Delete("customapiresponseproperty", gid);
                break;
            default:
                throw new McpException("kind must be image, step, type, assembly, package, endpoint, customapi, customapiparameter or customapiresponse.");
        }
        return new { Deleted = kind, Id = gid };
    });

    // Order used by PRT/crstPawel: images, unlink + delete secure config, then the step.
    static void DeleteStep(IOrganizationService svc, Guid stepId)
    {
        foreach (var img in ImagesFor(svc, [stepId]))
            svc.Delete(img.LogicalName, img.Id);
        var secure = svc.Retrieve("sdkmessageprocessingstep", stepId, new ColumnSet("sdkmessageprocessingstepsecureconfigid"))
            .GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid");
        if (secure != null)
        {
            svc.Update(new Entity("sdkmessageprocessingstep", stepId) { ["sdkmessageprocessingstepsecureconfigid"] = null });
            svc.Delete(secure.LogicalName, secure.Id);
        }
        svc.Delete("sdkmessageprocessingstep", stepId);
    }

    static void RefuseIfSteps(List<Entity> steps)
    {
        if (steps.Count > 0)
            throw new McpException($"Still used by {steps.Count} step(s): {string.Join("; ", steps.Select(s => $"{s.GetAttributeValue<string>("name")} ({s.Id})"))}. Unregister those steps first.");
    }

    static Entity ResolvePluginType(IOrganizationService svc, string typeNameOrId)
    {
        var q = Q("plugintype", "typename");
        if (Guid.TryParse(typeNameOrId, out var id)) q.Criteria.AddCondition("plugintypeid", ConditionOperator.Equal, id);
        else q.Criteria.AddCondition("typename", ConditionOperator.Equal, typeNameOrId);
        var hits = svc.RetrieveMultiple(q).Entities;
        return hits.Count switch
        {
            0 => throw new McpException($"Plug-in type '{typeNameOrId}' is not registered. Run register_assembly first."),
            1 => hits[0],
            _ => throw new McpException($"'{typeNameOrId}' exists in {hits.Count} assemblies (side-by-side). Pass one of these plugintype ids instead: {string.Join(", ", hits.Select(h => h.Id))}"),
        };
    }

    // Never set DoNotIncludeSubcomponents here: it fails for non-entity components.
    static void AddToSolution(IOrganizationService svc, int componentType, Guid id, string solution, bool addRequired) =>
        svc.Execute(new AddSolutionComponentRequest
        {
            ComponentType = componentType, ComponentId = id, SolutionUniqueName = solution, AddRequiredComponents = addRequired,
        });

    static QueryExpression StepQuery()
    {
        var q = Q("sdkmessageprocessingstep", "name", "stage", "mode", "rank", "statecode", "filteringattributes",
            "eventhandler", "configuration", "impersonatinguserid");
        var m = q.AddLink("sdkmessage", "sdkmessageid", "sdkmessageid");
        m.EntityAlias = "m";
        m.Columns = new ColumnSet("name");
        var f = q.AddLink("sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.LeftOuter);
        f.EntityAlias = "f";
        f.Columns = new ColumnSet("primaryobjecttypecode");
        return q;
    }

    static List<Entity> TypesOf(IOrganizationService svc, Guid assemblyId) =>
        In(svc, Q("plugintype", "typename"), "pluginassemblyid", [assemblyId]);

    static List<Entity> StepsFor(IOrganizationService svc, IEnumerable<Guid> typeIds) =>
        In(svc, Q("sdkmessageprocessingstep", "name"), "eventhandler", typeIds);

    // shortcut: single page (5000 rows); add paging if one query ever returns more plug-in rows than that.
    static List<Entity> In(IOrganizationService svc, QueryExpression q, string attribute, IEnumerable<Guid> ids)
    {
        var values = ids.Cast<object>().ToArray();
        if (values.Length == 0) return [];
        q.Criteria.AddCondition(attribute, ConditionOperator.In, values);
        return svc.RetrieveMultiple(q).Entities.ToList();
    }

    static Entity Single(IOrganizationService svc, QueryExpression q, string notFound) =>
        svc.RetrieveMultiple(q).Entities.FirstOrDefault() ?? throw new McpException(notFound);

    static QueryExpression Q(string entity, params string[] columns) => new(entity) { ColumnSet = new ColumnSet(columns) };

    static string? Aliased(Entity e, string key) => e.GetAttributeValue<AliasedValue>(key)?.Value?.ToString();

    static string StageName(int stage) => stage switch
    {
        10 => "PreValidation", 20 => "PreOperation", 40 => "PostOperation", _ => stage.ToString(),
    };

    // Surface the real Dataverse fault text instead of the SDK's generic "An error occurred invoking ...".
    static string Run(Func<object> body)
    {
        try { return JsonSerializer.Serialize(body(), Json); }
        catch (McpException) { throw; }
        catch (Exception e) { throw new McpException(e.Message, e); }
    }
}
