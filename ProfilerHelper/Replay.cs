using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using PluginProfiler.Plugins.ServiceWrappers;

// replay: our own host, not PRT's replay (whose code is non-public). It runs the user's plug-in in this process on a capture's context,
// with a tracing service that collects output and an organization service that is a REAL connection: the plug-in's writes happen.
// Visual Studio is attached to this process through EnvDTE (late-bound COM, found in the Running Object Table) before the plug-in is built.
[DataContract]
sealed class ReplayResult
{
    [DataMember] public string PluginType = "";
    [DataMember] public int VisualStudioProcessId;
    [DataMember] public long DurationMs;
    [DataMember] public string Trace = "";
    [DataMember(EmitDefaultValue = false)] public ExceptionInfo? Exception;
    [DataMember] public List<ServiceCall> OrganizationServiceCalls = new();
}

[DataContract]
sealed class ExceptionInfo
{
    [DataMember] public string Type = "";
    [DataMember] public string Message = "";
    [DataMember] public string? StackTrace;
}

[DataContract]
sealed class ServiceCall
{
    [DataMember] public string Method = "";
    [DataMember] public string Target = "";
    [DataMember] public Guid UserId;
    [DataMember(EmitDefaultValue = false)] public string? Result;
    [DataMember(EmitDefaultValue = false)] public string? Error;
}

static partial class Program
{
    static ReplayResult Replay(Request request)
    {
        var context = new PluginExecutionContextWrapper(request.Context);

        var dll = Path.GetFullPath(request.DllPath ?? throw new ArgumentException("DllPath is required."));
        _pluginDirectory = Path.GetDirectoryName(dll);
        // LoadFile, not LoadFrom: every reference then goes through AssemblyResolve, which hands out PRT's Microsoft.Xrm.Sdk.
        var plugins = Assembly.LoadFile(dll).GetExportedTypes().Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        var type = plugins.FirstOrDefault(t => t.FullName == request.PluginType) ?? (request.PluginType == null && plugins.Count == 1 ? plugins[0] : null)
            ?? throw new InvalidOperationException($"Plug-in type '{request.PluginType}' not found in {dll}. IPlugin types there: {(plugins.Count == 0 ? "none" : string.Join(", ", plugins.Select(t => t.FullName)))}.");

        var client = Connect(request);
        var result = new ReplayResult { PluginType = type.FullName!, VisualStudioProcessId = AttachVisualStudio(request.VsProcessId, dll, request.Breakpoints) };
        var trace = new StringBuilder();
        var services = new Services(context, new Tracing(trace), new Factory(client, result.OrganizationServiceCalls));
        var watch = Stopwatch.StartNew();
        try
        {
            // Same constructor choice as the platform: (unsecure, secure), (unsecure), or none. Built after the attach, so constructor breakpoints hit too.
            var plugin = (IPlugin)(type.GetConstructor(new[] { typeof(string), typeof(string) }) is { } two ? two.Invoke(new object?[] { request.UnsecureConfiguration, request.SecureConfiguration })
                : type.GetConstructor(new[] { typeof(string) }) is { } one ? one.Invoke(new object?[] { request.UnsecureConfiguration })
                : Activator.CreateInstance(type));
            plugin.Execute(services);
        }
        // The plug-in's own failure is a result, not a host error.
        catch (Exception e)
        {
            if (e is TargetInvocationException { InnerException: { } inner }) e = inner;
            result.Exception = new ExceptionInfo { Type = e.GetType().FullName!, Message = e.Message, StackTrace = e.StackTrace };
        }
        result.DurationMs = watch.ElapsedMilliseconds;
        result.Trace = trace.ToString();
        return result;
    }

    // Tested with Visual Studio 18: finds Visual Studio through the ROT ("!VisualStudio.DTE.<version>:<pid>"), attaches its managed
    // .NET Framework engine to this process, then waits until the debugger is really attached.
    static int AttachVisualStudio(int? vsProcessId, string dll, string[]? breakpoints)
    {
        int vsPid;
        try { vsPid = OnSta(() => AttachFromSta(vsProcessId, dll, breakpoints)); }
        catch (Exception e) { throw new InvalidOperationException($"Visual Studio attach failed: {e.Message}", e); }
        // shortcut: fixed 60 s; a parameter if slow machines need more.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!Debugger.IsAttached)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Visual Studio {vsPid} accepted the attach but no debugger was attached after 60 seconds.");
            Thread.Sleep(200);
        }
        return vsPid;
    }

    // DTE is an STA COM server, and the retry message filter needs an STA.
    static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var sta = new Thread(() =>
        {
            CoRegisterMessageFilter(new RetryFilter(), out _);
            try { result = body(); }
            catch (Exception e) { failure = e; }
            finally { CoRegisterMessageFilter(null, out _); }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
        if (failure != null) throw failure;
        return result;
    }

    static int AttachFromSta(int? vsProcessId, string dll, string[]? breakpoints)
    {
        var instances = RunningVisualStudios();
        if (instances.Count == 0) throw new InvalidOperationException("No running Visual Studio found. Open the plug-in's solution in Visual Studio (not as administrator if this server isn't).");
        var pick = vsProcessId != null ? instances.Where(i => i.Pid == vsProcessId).ToList() : instances.Where(i => OpensProjectOf(i.Dte, dll)).ToList();
        if (vsProcessId == null && pick.Count == 0 && instances.Count == 1) pick = instances;
        if (pick.Count != 1)
            throw new InvalidOperationException((vsProcessId != null ? $"Visual Studio {vsProcessId} not found. " : $"Can't tell which Visual Studio has the project of {dll} open; pass vsProcessId. ") +
                "Running: " + string.Join("; ", instances.Select(i => $"{i.Pid} ({SolutionOf(i.Dte)})")));
        var (vsPid, dte) = pick[0];

        var self = Process.GetCurrentProcess().Id;
        var debugger = dte.Debugger;
        var processes = debugger.LocalProcesses;
        dynamic? process = null;
        for (int i = 1; i <= (int)processes.Count && process == null; i++)
            if ((int)processes.Item(i).ProcessID == self) process = processes.Item(i);
        if (process == null) throw new InvalidOperationException($"This process ({self}) isn't in Visual Studio's local process list.");
        // Process.Attach, not Process2.Attach2(engine): late binding only reaches DTE's default interfaces (Debugger has no Transports).
        // Automatic engine choice debugged this net48 process's managed code (breakpoint hit in VS 18).
        AddBreakpoints(debugger, breakpoints);
        process.Attach();
        return vsPid;
    }

    // "file:line", or a function name such as Contoso.Plugins.AccountCreate.Execute. Ones Visual Studio already has are skipped;
    // the added ones stay in Visual Studio afterwards, like any breakpoint the user sets.
    static void AddBreakpoints(dynamic debugger, string[]? breakpoints)
    {
        foreach (var b in breakpoints ?? new string[0])
        {
            var m = Regex.Match(b, @"^(.+):(\d+)$");
            string file = m.Success ? Path.GetFullPath(m.Groups[1].Value) : "";
            int line = m.Success ? int.Parse(m.Groups[2].Value) : 1;
            var existing = debugger.Breakpoints;
            var found = false;
            for (int i = 1; i <= (int)existing.Count && !found; i++)
            {
                var e = existing.Item(i);
                found = m.Success ? string.Equals((string)e.File, file, StringComparison.OrdinalIgnoreCase) && (int)e.FileLine == line : (string)e.FunctionName == b;
            }
            if (found) continue;
            if (m.Success) debugger.Breakpoints.Add("", file, line);
            else debugger.Breakpoints.Add(b);
        }
    }

    // A Visual Studio "has the project open" when the folder of its solution, or of one of its projects, contains the DLL (bin\... under the project).
    static bool OpensProjectOf(dynamic dte, string dll)
    {
        var folders = new List<string>();
        try
        {
            string sln = dte.Solution.FullName;
            if (sln != "") folders.Add(Path.GetDirectoryName(sln)!);
            var projects = dte.Solution.Projects;
            for (int i = 1; i <= (int)projects.Count; i++)
            {
                try { string p = projects.Item(i).FullName; if (p != "") folders.Add(Path.GetDirectoryName(p)!); }
                catch (COMException) { } // solution folders and unloaded projects have no file
            }
        }
        catch (COMException) { return false; }
        return folders.Any(f => dll.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    static string SolutionOf(dynamic dte)
    {
        try { string s = dte.Solution.FullName; return s == "" ? "no solution" : s; }
        catch (COMException) { return "busy"; }
    }

    static List<(int Pid, dynamic Dte)> RunningVisualStudios()
    {
        var found = new List<(int, dynamic)>();
        Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out var rot));
        Marshal.ThrowExceptionForHR(CreateBindCtx(0, out var ctx));
        rot.EnumRunning(out var monikers);
        var one = new IMoniker[1];
        while (monikers.Next(1, one, IntPtr.Zero) == 0)
        {
            one[0].GetDisplayName(ctx, null, out var name);
            var m = Regex.Match(name, @"^!VisualStudio\.DTE\.[\d.]+:(\d+)$");
            if (m.Success && rot.GetObject(one[0], out var dte) == 0) found.Add((int.Parse(m.Groups[1].Value), dte));
        }
        return found;
    }

    // DateTime ticks from the precise clock: .NET Framework's UtcNow moves in 15.6 ms jumps, too coarse to tell which step wrote a line (debug_step).
    internal static long Now() { GetSystemTimePreciseAsFileTime(out var t); return DateTime.FromFileTimeUtc(t).Ticks; }

    [DllImport("kernel32.dll")] static extern void GetSystemTimePreciseAsFileTime(out long fileTime);
    [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable rot);
    [DllImport("ole32.dll")] static extern int CreateBindCtx(int reserved, out IBindCtx ctx);
    [DllImport("ole32.dll")] static extern int CoRegisterMessageFilter(IOleMessageFilter? newFilter, out IOleMessageFilter? oldFilter);
}

// Visual Studio rejects automation calls while it is busy (RPC_E_CALL_REJECTED). This retries them for up to 30 s, as Microsoft's DTE samples do.
[ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IOleMessageFilter
{
    [PreserveSig] int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo);
    [PreserveSig] int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType);
    [PreserveSig] int MessagePending(IntPtr taskCallee, int tickCount, int pendingType);
}

sealed class RetryFilter : IOleMessageFilter
{
    public int HandleInComingCall(int callType, IntPtr taskCaller, int tickCount, IntPtr interfaceInfo) => 0; // SERVERCALL_ISHANDLED
    public int RetryRejectedCall(IntPtr taskCallee, int tickCount, int rejectType) => rejectType == 2 && tickCount < 30_000 ? 100 : -1; // SERVERCALL_RETRYLATER: retry in 100 ms
    public int MessagePending(IntPtr taskCallee, int tickCount, int pendingType) => 2; // PENDINGMSG_WAITDEFPROCESS
}

sealed class Services(IPluginExecutionContext context, ITracingService tracing, IOrganizationServiceFactory factory) : IServiceProvider
{
    // Context, tracing and organization service only; other platform services (e.g. ILogger, IServiceEndpointNotificationService) return null.
    public object? GetService(Type t) =>
        t == typeof(ITracingService) ? tracing : t == typeof(IOrganizationServiceFactory) ? factory : t.IsInterface && t.IsInstanceOfType(context) ? context : null;
}

sealed class Tracing(StringBuilder output) : ITracingService
{
    public void Trace(string format, params object[] args)
    {
        var line = args == null || args.Length == 0 ? format : string.Format(format, args);
        output.AppendLine(line);
        Console.Error.WriteLine($"{Program.Now()} trace: {line}"); // live and time-stamped, for debug_step while the replay is paused
    }
}

// Called by debug_step through Visual Studio's expression evaluator, inside this (paused) process: an Entity local as its record and attribute values.
public static class ProfilerHelperInspect
{
    public static string Entity(Entity e) =>
        $"{e.LogicalName} {e.Id} ({e.Attributes.Count} attributes)" + (e.Attributes.Count == 0 ? "" : ": " + string.Join(", ", e.Attributes.Select(a => $"{a.Key}={Value(a.Value)}")));

    static string Value(object? v) => v switch
    {
        null => "null",
        EntityReference r => $"{r.LogicalName} {r.Id}" + (r.Name != null ? $" ({r.Name})" : ""),
        OptionSetValue o => o.Value.ToString(),
        OptionSetValueCollection c => "[" + string.Join(", ", c.Select(o => o.Value)) + "]",
        Money m => m.Value.ToString(),
        AliasedValue a => Value(a.Value),
        EntityCollection c => $"{c.Entities.Count} rows",
        DateTime d => d.ToString("o"),
        _ => v.ToString(),
    };
}

sealed class Factory(CrmServiceClient client, List<ServiceCall> calls) : IOrganizationServiceFactory
{
    // The platform's null user is SYSTEM, which a client can't impersonate: null runs as the connected user.
    public IOrganizationService CreateOrganizationService(Guid? userId) => new RecordingService(client, userId ?? Guid.Empty, calls);
}

// Every call really goes to Dataverse, as the given user (CallerId), and is listed in the result.
sealed class RecordingService(CrmServiceClient client, Guid userId, List<ServiceCall> calls) : IOrganizationService
{
    T Call<T>(string method, string target, Func<T> body)
    {
        var call = new ServiceCall { Method = method, Target = target, UserId = userId };
        calls.Add(call);
        lock (client)
        {
            client.CallerId = userId;
            try
            {
                var r = body();
                call.Result = r switch { Guid g => g.ToString(), EntityCollection c => $"{c.Entities.Count} rows", OrganizationResponse o => o.ResponseName, _ => null };
                return r;
            }
            catch (Exception e) { call.Error = e.Message; throw; }
            finally { Console.Error.WriteLine($"{Program.Now()} call: {method} {target}" + (call.Error != null ? " failed: " + call.Error : "")); } // live, for debug_step
        }
    }

    static string Describe(Entity e) => $"{e.LogicalName} {e.Id} [{string.Join(", ", e.Attributes.Keys)}]";

    public Guid Create(Entity entity) => Call("Create", Describe(entity), () => client.Create(entity));
    public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet) => Call("Retrieve", $"{entityName} {id}", () => client.Retrieve(entityName, id, columnSet));
    public void Update(Entity entity) => Call<object?>("Update", Describe(entity), () => { client.Update(entity); return null; });
    public void Delete(string entityName, Guid id) => Call<object?>("Delete", $"{entityName} {id}", () => { client.Delete(entityName, id); return null; });
    public OrganizationResponse Execute(OrganizationRequest request) => Call("Execute", request.RequestName, () => client.Execute(request));
    public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        Call<object?>("Associate", $"{entityName} {entityId} {relationship.SchemaName} ({relatedEntities.Count})", () => { client.Associate(entityName, entityId, relationship, relatedEntities); return null; });
    public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities) =>
        Call<object?>("Disassociate", $"{entityName} {entityId} {relationship.SchemaName} ({relatedEntities.Count})", () => { client.Disassociate(entityName, entityId, relationship, relatedEntities); return null; });
    public EntityCollection RetrieveMultiple(QueryBase query) =>
        Call("RetrieveMultiple", query is QueryExpression q ? q.EntityName : query is FetchExpression ? "FetchXML" : query.GetType().Name, () => client.RetrieveMultiple(query));
}
