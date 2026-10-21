using System.ComponentModel;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// Read-only access to captures made by PRT's trace-log profiler (PRT 9.1.0.184+; started and stopped by start_profiling / stop_profiling in PluginTools.ProfilerControl.cs or PRT).
// A capture is plugintracelog.profile: bare base64 of the protobuf plug-in context (observed), decoded by PRT's public PluginExecutionContextWrapper
// in ProfilerHelper. Replayed by debug_profile (PluginTools.Debug.cs).
public static partial class PluginTools
{
    static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    [McpServerTool(Name = "list_profiles", ReadOnly = true)]
    [Description("Show trace-log profiler state and captures, newest first: steps being profiled (enablepluginprofiler), the environment's trace-log setting, " +
        "and trace-log captures (plugintracelog.profile). Start/stop profiling with start_profiling / stop_profiling; read a capture with get_profile.")]
    public static string ListProfiles(Dataverse dv,
        [Description("Plug-in type full name, or the start of it (begins-with match)")] string? typeName = null,
        [Description("Maximum captures (1-100)")] int top = 20) => Run(() =>
    {
        var svc = dv.Client;
        // The step itself is flagged. enablepluginprofiler is documented; that it turns on profiling was observed.
        var flagged = StepQuery();
        flagged.Criteria.AddCondition("enablepluginprofiler", ConditionOperator.Equal, true);
        var profiledSteps = svc.RetrieveMultiple(flagged).Entities.Select(s => new
            {
                s.Id, Name = s.GetAttributeValue<string>("name"),
                Message = Aliased(s, "m.name"), Entity = Aliased(s, "f.primaryobjecttypecode") ?? "any",
                Enabled = s.GetAttributeValue<OptionSetValue>("statecode").Value == 0,
            })
            .ToList();
        var setting = Organization(svc).GetAttributeValue<OptionSetValue>("plugintracelogsetting")?.Value ?? 0;

        var tq = Q("plugintracelog", "typename", "messagename", "primaryentity", "mode", "depth", "createdon", "correlationid", "pluginstepid");
        tq.TopCount = Math.Clamp(top, 1, 100);
        tq.AddOrder("createdon", OrderType.Descending);
        tq.Criteria.AddCondition("profile", ConditionOperator.NotNull);
        if (typeName != null) tq.Criteria.AddCondition("typename", ConditionOperator.BeginsWith, typeName);
        var traced = svc.RetrieveMultiple(tq).Entities.Select(r => new
        {
            TraceLogId = r.Id,
            TypeName = r.GetAttributeValue<string>("typename"),
            Message = r.GetAttributeValue<string>("messagename"),
            Entity = r.GetAttributeValue<string>("primaryentity"),
            Mode = r.GetAttributeValue<OptionSetValue>("mode")?.Value == 1 ? "Asynchronous" : "Synchronous",
            Depth = r.GetAttributeValue<int>("depth"),
            CorrelationId = r.GetAttributeValue<Guid>("correlationid"),
            StepId = r.GetAttributeValue<Guid?>("pluginstepid"),
            CreatedOnUtc = r.GetAttributeValue<DateTime>("createdon"),
        }).ToList();

        var notes = new List<string>();
        if (profiledSteps.Count == 0)
            notes.Add("No step is being profiled. Start with start_profiling (or PRT > Start Profiling), then run the operation that triggers it.");
        else if (traced.Count == 0)
            notes.Add("Profiling is on but nothing has been captured yet. Trigger the operation. " +
                "Steps flagged enablepluginprofiler write their capture to the trace log about 25 seconds after the run: at All every run, at Exception only runs that throw, at Off none.");
        if (traced.Count > 0) notes.Add("Trace-log captures are purged with the trace log (about 24 hours).");

        return new
        {
            ProfiledSteps = profiledSteps,
            TraceLogSetting = TraceLevels[setting],
            TraceLogProfiles = traced,
            Notes = notes,
        };
    });

    [McpServerTool(Name = "get_profile", ReadOnly = true)]
    [Description("Decode one trace-log profiler capture with PRT's own PluginExecutionContextWrapper (needs PRT in PRT_DIRECTORY and ProfilerHelper next to this server): " +
        "the execution context (message, stage, depth, users, InputParameters/OutputParameters, Pre/PostEntityImages, SharedVariables, parent context). " +
        "Pass profileId (a TraceLogProfiles id from list_profiles) or filePath (a saved trace-log profile: the bare base64 of plugintracelog.profile). " +
        "Captures contain real record data. Strings over 4000 characters are truncated. For breakpoints, replay the same capture with debug_profile.")]
    public static string GetProfile(Dataverse dv,
        [Description("plugintracelogid")] string? profileId = null,
        [Description("Path to a saved trace-log profile (bare base64)")] string? filePath = null) => Run(() =>
    {
        CheckOneCapture(profileId, filePath);
        var (text, source) = LoadCapture(dv, profileId, filePath);
        return new { Source = source, Context = DecodeProtobufContext(text) };
    });

    static void CheckOneCapture(string? profileId, string? filePath)
    {
        if ((profileId == null) == (filePath == null)) throw new McpException("Pass exactly one of profileId or filePath.");
    }

    static (string Text, string Source) LoadCapture(Dataverse dv, string? profileId, string? filePath)
    {
        if (filePath == null) return FetchProfile(dv.Client, Guid.Parse(profileId!));
        if (!File.Exists(filePath)) throw new McpException($"File not found: {filePath}");
        var text = File.ReadAllText(filePath).Trim();
        // Checked here so a wrong file fails clearly even without PRT on the machine.
        if (!Convert.TryFromBase64String(text, new byte[text.Length], out _))
            throw new McpException($"Not a trace-log profile: expected the bare base64 of plugintracelog.profile.");
        return (text, filePath);
    }

    static (string Text, string Source) FetchProfile(IOrganizationService svc, Guid id)
    {
        var tq = Q("plugintracelog", "profile");
        tq.Criteria.AddCondition("plugintracelogid", ConditionOperator.Equal, id);
        var trace = Single(svc, tq, $"No trace log {id}. It may have been deleted (trace logs are purged after about 24 hours); run list_profiles.");
        return (trace.GetAttributeValue<string>("profile") ?? throw new McpException($"Trace log {id} has no profile. Only runs of a step flagged enablepluginprofiler (PRT's trace-log profiler) write one."), $"plugintracelog {id}");
    }

    // PRT's own PluginExecutionContextWrapper (via ProfilerHelper) turns the protobuf into DataContract XML.
    static object? DecodeProtobufContext(string context)
    {
        try
        {
            var xml = RunHelper(ProfilerHelper(), "decode-context", new() { ["Context"] = context }).GetProperty("ContextXml").GetString()!;
            return ToValue(XDocument.Parse(xml).Root!);
        }
        catch (Exception e) when (e is McpException or TimeoutException)
        {
            return new { Format = "protobuf", Note = $"Not decoded: {e.Message}", Length = context.Length };
        }
    }

    // Xrm DataContract XML to plain values: KeyValuePairOf* lists become dictionaries, repeated elements become lists,
    // nil and empty members of other objects are dropped.
    static object? ToValue(XElement e)
    {
        if (IsNil(e)) return null;
        var children = e.Elements().ToList();
        if (children.Count == 0) return e.IsEmpty ? null : Truncate(e.Value);
        if (children.All(c => c.Name.LocalName.StartsWith("KeyValuePairOf")))
            return children.ToDictionary( // nulls kept: an attribute set to null is meaningful in a Target
                c => c.Elements().First(k => k.Name.LocalName == "key").Value,
                c => ToValue(c.Elements().First(v => v.Name.LocalName == "value")));
        if (children.Count > 1 && children.All(c => c.Name == children[0].Name))
            return children.Select(ToValue).ToList();
        var dict = new Dictionary<string, object?>();
        foreach (var c in children) dict[c.Name.LocalName] = ToValue(c);
        return Compact(dict);
    }

    static Dictionary<string, object?>? Compact(Dictionary<string, object?> d)
    {
        foreach (var k in d.Where(kv => kv.Value is null or Dictionary<string, object?> { Count: 0 }).Select(kv => kv.Key).ToList()) d.Remove(k);
        return d.Count == 0 ? null : d;
    }

    static bool IsNil(XElement e) => (string?)e.Attribute(Xsi + "nil") == "true";
}
