using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// Breakpoint replay without PRT: ProfilerHelper's replay command (ProfilerHelper/Replay.cs) runs the user's plug-in on a real capture's context
// in its own .NET Framework process, attaches Visual Studio to that process, and gives the plug-in a REAL organization service.
// In step mode the replay keeps running in the background and debug_step drives Visual Studio through ProfilerHelper's debug command (ProfilerHelper/Debug.cs).
public static partial class PluginTools
{
    // shortcut: one step-mode replay per server; a dictionary by id if parallel replays are ever needed.
    static HelperRun? _stepReplay;
    static readonly object StepLock = new();

    [McpServerTool(Name = "debug_profile")]
    [Description("Replay a real trace-log profiler capture with breakpoints, without PRT: runs your local plug-in DLL on the captured execution context in a local .NET Framework host " +
        "(ProfilerHelper, needs PRT in PRT_DIRECTORY for its SDK and context classes) and attaches the Visual Studio that has the plug-in's project open, so breakpoints hit. " +
        "Build the plug-in with its PDB and open it in Visual Studio first. Pass profileId or filePath as for get_profile. " +
        "WRITES ARE REAL: every IOrganizationService call the plug-in makes (Create, Update, Delete, Execute...) goes to this server's Dataverse environment, as the captured user, outside any transaction. " +
        "Requires allowWrites=true. Returns the trace output, the plug-in's exception, the duration and the list of organization-service calls. " +
        "stepMode=true returns as soon as Visual Studio stops at the first breakpoint (default: the plug-in's Execute), with the line, source and locals; then drive it with debug_step.")]
    public static string DebugProfile(Dataverse dv,
        [Description("Path to your local plug-in .dll, with its .pdb next to it")] string dllPath,
        [Description("Must be true: the plug-in's writes really happen in this server's environment")] bool allowWrites = false,
        [Description("plugintracelogid, as for get_profile")] string? profileId = null,
        [Description("Path to a capture file, as for get_profile")] string? filePath = null,
        [Description("Plug-in class full name (default: the step's)")] string? pluginType = null,
        [Description("Unsecure configuration for the constructor (default: the step's)")] string? unsecureConfiguration = null,
        [Description("Secure configuration for the constructor (default: the step's)")] string? secureConfiguration = null,
        [Description("Process id of the Visual Studio to attach (default: the one whose solution or project folder contains dllPath, or the only one running)")] int? vsProcessId = null,
        [Description("Breakpoints to add in Visual Studio before the run, comma-separated: C:\\src\\AccountCreate.cs:42, or a method such as Contoso.Plugins.AccountCreate.Execute. They stay in Visual Studio afterwards. Default: none, or the plug-in's Execute in step mode")] string? breakpoints = null,
        [Description("true: return at the first breakpoint and step with debug_step. false: wait until the replay ends (up to 30 minutes) while you debug in Visual Studio yourself")] bool stepMode = false) => Run(() =>
    {
        if (!allowWrites)
            throw new McpException("debug_profile runs your plug-in against this server's real environment: its Create / Update / Delete / Execute calls really happen. Pass allowWrites=true to confirm.");
        CheckOneCapture(profileId, filePath);
        if (!File.Exists(dllPath)) throw new McpException($"DLL not found: {dllPath}");
        var helper = ProfilerHelper();
        var (context, source) = LoadCapture(dv, profileId, filePath);

        // Type and configuration aren't in the capture: they come from the step that ran, the context's OwningExtension.
        if (pluginType == null || unsecureConfiguration == null || secureConfiguration == null)
        {
            var decoded = DecodeProtobufContext(context);
            var stepId = (decoded as Dictionary<string, object?>)?.GetValueOrDefault("OwningExtension") is Dictionary<string, object?> owner && owner.GetValueOrDefault("Id") is string id
                ? Guid.Parse(id) : throw new McpException($"Couldn't read the step from the capture's context ({(decoded?.GetType().GetProperty("Note")?.GetValue(decoded) as string) ?? "no OwningExtension"}). Pass pluginType, unsecureConfiguration and secureConfiguration.");
            var svc = dv.Client;
            var step = svc.Retrieve("sdkmessageprocessingstep", stepId, new ColumnSet("configuration", "plugintypeid", "sdkmessageprocessingstepsecureconfigid"));
            pluginType ??= step.GetAttributeValue<EntityReference>("plugintypeid") is { } t ? svc.Retrieve("plugintype", t.Id, new ColumnSet("typename")).GetAttributeValue<string>("typename") : null;
            unsecureConfiguration ??= step.GetAttributeValue<string>("configuration");
            secureConfiguration ??= step.GetAttributeValue<EntityReference>("sdkmessageprocessingstepsecureconfigid") is { } sc
                ? svc.Retrieve("sdkmessageprocessingstepsecureconfig", sc.Id, new ColumnSet("secureconfig")).GetAttributeValue<string>("secureconfig") : null;
        }

        var points = (breakpoints ?? (stepMode ? $"{pluginType}.Execute" : "")).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var request = new Dictionary<string, object?>
        {
            ["Context"] = context,
            ["DllPath"] = Path.GetFullPath(dllPath),
            ["PluginType"] = pluginType,
            ["UnsecureConfiguration"] = unsecureConfiguration,
            ["SecureConfiguration"] = secureConfiguration,
            ["VsProcessId"] = vsProcessId,
            ["OrgUrl"] = dv.Client.ConnectedOrgUriActual.GetLeftPart(UriPartial.Authority),
            ["AccessToken"] = dv.AccessToken,
            ["Breakpoints"] = points,
        };
        var notes = File.Exists(Path.ChangeExtension(dllPath, ".pdb")) ? [] : new[] { "No .pdb next to the DLL: Visual Studio attached, but breakpoints can't bind." };
        if (!stepMode)
            // Generous timeout: the host stays stopped at breakpoints while the user debugs.
            return new { Source = source, Replay = RunHelper(helper, "replay", request, timeoutMinutes: 30).GetProperty("Replay"), Notes = notes };

        lock (StepLock)
        {
            if (_stepReplay is { Process.HasExited: false })
                throw new McpException("A step-mode replay is already running. End it with debug_step(continue) or debug_step(stop).");
            _stepReplay?.Dispose();
            _stepReplay = StartHelper(helper, "replay", request);
            // A replay paused at a breakpoint can't notice its server is gone, so the server ends it.
            // shortcut: graceful shutdown only (stdin closed); a job object if hard-killed servers leave paused replays behind.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _stepReplay?.Process.Kill(); } catch (InvalidOperationException) { } };
            // Connecting and attaching Visual Studio take a while before the first break.
            return new { Source = source, Breakpoints = points, Notes = notes, State = StepState(helper, "wait", 1, null, 120) };
        }
    });

    [McpServerTool(Name = "debug_step")]
    [Description("Drive a replay started with debug_profile(stepMode=true) through Visual Studio. action: state (where it is stopped), over / into / out (step `count` statements; " +
        "each step lists its line, source, the locals that changed, the `evaluate` values and the trace lines and organization-service calls it caused), continue (run to the next breakpoint or the end), stop (kill the replay; writes already made stay). " +
        "While stopped, the result has the function, file, line, source text, locals (an Entity shows as 'logicalname id (n attributes): name=value, ...'), the stack, the exception at an exception break, " +
        "the values of `evaluate`, and the trace lines and organization-service calls so far (Output). Once the replay has ended it has the replay's final result instead.")]
    public static string DebugStep(
        [Description("state | over | into | out | continue | stop")] string action = "state",
        [Description("over / into / out: statements to step (1-100); stops early when the replay leaves break mode")] int count = 1,
        [Description("Expressions to evaluate after every step and where it stops, in C# (they can call methods: any Dataverse call they make is real), e.g. target.GetAttributeValue<string>(\"name\")")] string[]? evaluate = null,
        [Description("Seconds to wait for Visual Studio to break again (default 60). On timeout the replay keeps running: call debug_step(state) later")] int timeoutSeconds = 60) => Run(() =>
    {
        lock (StepLock)
        {
            var run = _stepReplay ?? throw new McpException("No step-mode replay is running. Start one with debug_profile(stepMode=true).");
            if (action != "stop") return StepState(ProfilerHelper(), action, count, evaluate, timeoutSeconds);
            if (!run.Process.HasExited) run.Process.Kill();
            run.Process.WaitForExit();
            _stepReplay = null;
            run.Dispose();
            return new { Stopped = true, Output = run.StderrLines(), Note = "The replay was killed. The organization-service calls in Output already happened in Dataverse." };
        }
    });

    static object StepState((string Exe, string PrtDirectory) helper, string action, int count, string[]? evaluate, int timeoutSeconds)
    {
        var run = _stepReplay!;
        JsonNode? state = null;
        var since = DateTime.UtcNow.Ticks;
        if (!run.Process.HasExited)
        {
            try
            {
                state = JsonNode.Parse(RunHelper(helper, "debug", new()
                {
                    ["DebuggeePid"] = run.Process.Id, ["Action"] = action, ["Count"] = count, ["Expressions"] = evaluate, ["TimeoutSeconds"] = timeoutSeconds,
                }, timeoutMinutes: (timeoutSeconds + 30) / 60.0).GetProperty("Debug").GetRawText());
                // Each step gets the trace lines and organization-service calls it caused: the replay stamps every line, the debug command every step's end.
                foreach (var step in state?["Steps"]?.AsArray() ?? [])
                {
                    var at = (long)step!["At"]!;
                    if (run.StderrLines(since, at) is { Length: > 0 } output) step["Output"] = new JsonArray(output.Select(o => (JsonNode?)o).ToArray());
                    step.AsObject().Remove("At");
                    since = at;
                }
            }
            catch (TimeoutException)
            {
                return new { Mode = "Run", Note = $"Visual Studio didn't break again within {timeoutSeconds} s; the replay keeps running. Call debug_step(state) later, or debug_step(stop).", Output = run.StderrLines() };
            }
        }
        // The replay ended (it ran to the end, or this step finished it): return its final result and end the session.
        if (run.Process.WaitForExit((string?)state?["Mode"] is "Break" or "Run" ? 0 : 10_000))
        {
            _stepReplay = null;
            using (run) return new { Debug = state, Replay = run.Result().GetProperty("Replay") };
        }
        return new { Debug = state, Output = run.StderrLines() };
    }
}
