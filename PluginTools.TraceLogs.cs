using System.ComponentModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// Plug-in Trace Log: plug-in trace output, and where PRT's trace-log profiler writes its captures (plugintracelog.profile, read by PluginTools.Profiler.cs).
public static partial class PluginTools
{
    static readonly string[] TraceLevels = ["Off", "Exception", "All"];

    [McpServerTool(Name = "get_trace_logs", ReadOnly = true)]
    [Description("Read recent Plug-in Trace Log rows (newest first) plus the environment's trace setting. Rows exist only while the setting is Exception or All and are purged after about 24 hours. " +
        "messageblock (the plug-in's ITracingService output, last 10 KB) is only returned to System Administrators. Long text is truncated to 4000 characters.")]
    public static string GetTraceLogs(Dataverse dv,
        [Description("Plug-in type full name, or the start of it (begins-with match)")] string? typeName = null,
        [Description("SDK message name, e.g. Create")] string? messageName = null,
        [Description("Table logical name, e.g. account")] string? primaryEntity = null,
        [Description("Correlation id: returns the whole pipeline chain of one request")] string? correlationId = null,
        [Description("Only rows with exception details")] bool onlyExceptions = false,
        [Description("Look back this many minutes")] int sinceMinutes = 60,
        [Description("Maximum rows (1-100)")] int top = 20) => Run(() =>
    {
        var svc = dv.Client;
        var setting = Organization(svc).GetAttributeValue<OptionSetValue>("plugintracelogsetting")?.Value ?? 0;
        var q = Q("plugintracelog", "typename", "messagename", "primaryentity", "mode", "depth", "createdon", "performanceexecutionduration",
            "correlationid", "pluginstepid", "exceptiondetails", "messageblock");
        q.TopCount = Math.Clamp(top, 1, 100);
        q.AddOrder("createdon", OrderType.Descending);
        q.Criteria.AddCondition("createdon", ConditionOperator.GreaterEqual, DateTime.UtcNow.AddMinutes(-sinceMinutes)); // OnOrAfter is date-only
        if (typeName != null) q.Criteria.AddCondition("typename", ConditionOperator.BeginsWith, typeName);
        if (messageName != null) q.Criteria.AddCondition("messagename", ConditionOperator.Equal, messageName);
        if (primaryEntity != null) q.Criteria.AddCondition("primaryentity", ConditionOperator.Equal, primaryEntity);
        if (correlationId != null) q.Criteria.AddCondition("correlationid", ConditionOperator.Equal, Guid.Parse(correlationId));
        if (onlyExceptions) q.Criteria.AddCondition("exceptiondetails", ConditionOperator.NotNull);

        return new
        {
            TraceLogSetting = TraceLevels[setting],
            Hint = setting == 0 ? "Trace logging is Off, so no new rows are written. Turn it on with set_trace_log_setting." : null,
            Logs = svc.RetrieveMultiple(q).Entities.Select(r => new
            {
                r.Id,
                TypeName = r.GetAttributeValue<string>("typename"),
                Message = r.GetAttributeValue<string>("messagename"),
                Entity = r.GetAttributeValue<string>("primaryentity"),
                Mode = r.GetAttributeValue<OptionSetValue>("mode")?.Value == 1 ? "Asynchronous" : "Synchronous",
                Depth = r.GetAttributeValue<int>("depth"),
                CreatedOnUtc = r.GetAttributeValue<DateTime>("createdon"),
                DurationMs = r.GetAttributeValue<int>("performanceexecutionduration"),
                CorrelationId = r.GetAttributeValue<Guid>("correlationid"),
                StepId = r.GetAttributeValue<Guid?>("pluginstepid"),
                Exception = Truncate(r.GetAttributeValue<string>("exceptiondetails")),
                MessageBlock = Truncate(r.GetAttributeValue<string>("messageblock")),
            }),
        };
    });

    [McpServerTool(Name = "set_trace_log_setting")]
    [Description("Set the environment-wide Plug-in Trace Log setting (Off | Exception | All) and return the previous value so it can be restored. Logging costs storage; keep it Off in production. Needs organization write privilege.")]
    public static string SetTraceLogSetting(Dataverse dv, [Description("Off | Exception | All")] string level) => Run(() =>
    {
        var value = Array.FindIndex(TraceLevels, l => l.Equals(level, StringComparison.OrdinalIgnoreCase));
        if (value < 0) throw new McpException("level must be Off, Exception or All.");
        var org = Organization(dv.Client);
        var previous = org.GetAttributeValue<OptionSetValue>("plugintracelogsetting")?.Value ?? 0;
        dv.Client.Update(new Entity("organization", org.Id) { ["plugintracelogsetting"] = new OptionSetValue(value) });
        return new { Previous = TraceLevels[previous], Current = TraceLevels[value] };
    });

    static Entity Organization(IOrganizationService svc) =>
        svc.RetrieveMultiple(Q("organization", "plugintracelogsetting")).Entities[0];

    static string? Truncate(string? s) => s is { Length: > 4000 } ? s[..4000] + " …(truncated)" : s;
}
