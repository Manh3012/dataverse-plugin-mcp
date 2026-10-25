using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// Start / Stop Profiling as PRT's trace-log profiler does it: set or clear sdkmessageprocessingstep.enablepluginprofiler on the step itself
// (observed). PRT has no public method for it, so this server writes the column. Also the ProfilerHelper launcher for get_profile and debug_profile.
public static partial class PluginTools
{
    [McpServerTool(Name = "start_profiling")]
    [Description("Start Profiling as PRT's trace-log profiler does: sets enablepluginprofiler=true on the plug-in step itself. Doesn't change the environment's trace-log setting, " +
        "but captures (plugintracelog.profile) need it: Exception captures only runs that throw, All every run. The result reports it. " +
        "Read captures with list_profiles / get_profile; end with stop_profiling.")]
    public static string StartProfiling(Dataverse dv, [Description("sdkmessageprocessingstepid of the plug-in step to profile")] string stepId) => Run(() =>
    {
        var gid = Guid.Parse(stepId);
        var svc = dv.Client;
        var step = svc.Retrieve("sdkmessageprocessingstep", gid, new ColumnSet("eventhandler", "enablepluginprofiler"));
        if (step.GetAttributeValue<EntityReference>("eventhandler")?.LogicalName != "plugintype")
            throw new McpException("Only plug-in steps can be profiled. This step sends to a service endpoint or webhook.");
        if (step.GetAttributeValue<bool>("enablepluginprofiler"))
            throw new McpException("This step is already being profiled. End it with stop_profiling.");
        svc.Update(new Entity("sdkmessageprocessingstep", gid) { ["enablepluginprofiler"] = true });
        var setting = Organization(svc).GetAttributeValue<OptionSetValue>("plugintracelogsetting")?.Value ?? 0;
        return new
        {
            StepId = gid,
            TraceLogSetting = TraceLevels[setting],
            Hint = setting switch
            {
                0 => "The trace-log setting is Off, so nothing is captured. Call set_trace_log_setting(Exception) to capture only runs that throw, or All to capture every run, and keep its Previous value to restore afterwards.",
                1 => "The trace-log setting is Exception, so only runs that throw are captured. To capture a run that succeeds, call set_trace_log_setting(All) and keep its Previous value to restore afterwards.",
                _ => null,
            },
            Next = "Run the operation that triggers the step, then list_profiles and get_profile(profileId). End with stop_profiling(stepId).",
        };
    });

    [McpServerTool(Name = "stop_profiling")]
    [Description("Stop Profiling as PRT's trace-log profiler does: sets enablepluginprofiler=false on the step. Captures already written stay until the trace log is purged. " +
        "Doesn't change the trace-log setting; restore it with set_trace_log_setting.")]
    public static string StopProfiling(Dataverse dv, [Description("sdkmessageprocessingstepid of the profiled step (list_profiles ProfiledSteps)")] string stepId) => Run(() =>
    {
        var gid = Guid.Parse(stepId);
        var svc = dv.Client;
        if (!svc.Retrieve("sdkmessageprocessingstep", gid, new ColumnSet("enablepluginprofiler")).GetAttributeValue<bool>("enablepluginprofiler"))
            throw new McpException("This step isn't being profiled (enablepluginprofiler is false). See list_profiles.");
        svc.Update(new Entity("sdkmessageprocessingstep", gid) { ["enablepluginprofiler"] = false });
        return new { StepId = gid, Stopped = true };
    });

    static string ProfilerHelper()
    {
        // Own folder: next to the server, probing would load the server's net8 SDK instead of the helper's .NET Framework SDK.
        var exe = Path.Combine(AppContext.BaseDirectory, "ProfilerHelper", "ProfilerHelper.exe");
        if (!File.Exists(exe))
            throw new McpException($"{exe} not found. Build it: dotnet publish ProfilerHelper -c Release -o <server folder>/ProfilerHelper");
        return exe;
    }

    static JsonElement RunHelper(string helper, string command, Dictionary<string, object?> request, double timeoutMinutes = 3)
    {
        using var run = StartHelper(helper, command, request);
        if (!run.Process.WaitForExit(TimeSpan.FromMinutes(timeoutMinutes)))
        {
            run.Process.Kill();
            throw new TimeoutException($"ProfilerHelper timed out after {timeoutMinutes} minutes.");
        }
        return run.Result();
    }

    // stderr is collected line by line, so a running replay's live trace and service calls can be read before it ends (debug_step).
    static HelperRun StartHelper(string helper, string command, Dictionary<string, object?> request)
    {
        var p = Process.Start(new ProcessStartInfo(helper, command)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), UseShellExecute = false, CreateNoWindow = true,
        })!;
        var run = new HelperRun(command, p, p.StandardOutput.ReadToEndAsync());
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            // The replay stamps its trace and call lines with DateTime ticks ("638... trace: ..."), so debug_step can tell which step wrote them.
            var stamped = Regex.Match(e.Data, @"^(\d{17,19}) (.*)$");
            lock (run.Stderr) run.Stderr.Add(stamped.Success ? (long.Parse(stamped.Groups[1].Value), stamped.Groups[2].Value) : (0, e.Data));
        };
        p.BeginErrorReadLine();
        // Tokens and captures go on stdin, never on the command line.
        p.StandardInput.Write(JsonSerializer.Serialize(request));
        p.StandardInput.Close();
        return run;
    }

    sealed record HelperRun(string Command, Process Process, Task<string> Stdout) : IDisposable
    {
        public List<(long Ticks, string Text)> Stderr { get; } = [];

        public string[] StderrLines() { lock (Stderr) return [.. Stderr.Select(l => l.Text)]; }

        public string[] StderrLines(long after, long upTo) { lock (Stderr) return [.. Stderr.Where(l => l.Ticks > after && l.Ticks <= upTo).Select(l => l.Text)]; }

        // Only once the process has exited.
        public JsonElement Result()
        {
            Process.WaitForExit(); // also drains stderr
            JsonElement r;
            try { r = JsonDocument.Parse(Stdout.Result).RootElement; }
            catch (JsonException) { throw new McpException($"ProfilerHelper failed (exit {Process.ExitCode}): {string.Join("\n", StderrLines())}{Stdout.Result}"); }
            if (r.TryGetProperty("Error", out var error)) throw new McpException((Command == "replay" ? "Replay failed: " : "ProfilerHelper failed: ") + error.GetString());
            return r;
        }

        public void Dispose() => Process.Dispose();
    }
}
