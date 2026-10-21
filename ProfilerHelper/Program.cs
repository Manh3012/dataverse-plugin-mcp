using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Microsoft.Xrm.Tooling.Connector;
using PluginProfiler.Plugins.ServiceWrappers;

// Runs PRT's own code, loaded from the user's PRT folder:
//   decode-context: a trace-log capture's protobuf execution context to DataContract XML,
//   through PluginProfiler.Plugins' public PluginExecutionContextWrapper. No Dataverse connection.
//   replay: runs the user's plug-in on a capture's context in this process, after attaching Visual Studio to it (Replay.cs).
//   debug: steps the Visual Studio that is debugging a replay and reads where it stopped (Debug.cs).
// Usage: ProfilerHelper <command>, one JSON Request on stdin (the access token never goes on the command line),
// one JSON Response line on stdout. Exit code 0 on success.
// Fields are set by the serializer, which skips field initializers: the server always sends every field.
#pragma warning disable CS0649
[DataContract]
sealed class Request
{
    [DataMember] public string PrtDirectory = "";
    [DataMember] public string OrgUrl = "";
    [DataMember] public string AccessToken = "";
    [DataMember] public string Context = "";
    [DataMember] public string? DllPath;
    [DataMember] public string? PluginType;
    [DataMember] public string? UnsecureConfiguration;
    [DataMember] public string? SecureConfiguration;
    [DataMember] public int? VsProcessId;
    [DataMember] public string[]? Breakpoints;
    // debug only
    [DataMember] public int DebuggeePid;
    [DataMember] public string? Action;
    [DataMember] public int Count;
    [DataMember] public string[]? Expressions;
    [DataMember] public int TimeoutSeconds;
}
#pragma warning restore CS0649

[DataContract]
sealed class Response
{
    [DataMember(EmitDefaultValue = false)] public string? ContextXml;
    [DataMember(EmitDefaultValue = false)] public ReplayResult? Replay;
    [DataMember(EmitDefaultValue = false)] public DebugState? Debug;
    [DataMember(EmitDefaultValue = false)] public string? Error;
}

// Hands the server's existing access token to CrmServiceClient, so no second sign-in happens here.
sealed class TokenHook(string token) : IOverrideAuthHookWrapper
{
    public string GetAuthToken(Uri connectedUri) => token;
}

static partial class Program
{
    static string? _pluginDirectory;

    static int Main(string[] args)
    {
        var response = new Response();
        try
        {
            if (args.Length != 1 || args[0] is not ("decode-context" or "replay" or "debug")) throw new ArgumentException("Usage: ProfilerHelper decode-context|replay|debug < request.json");
            var request = (Request)new DataContractJsonSerializer(typeof(Request)).ReadObject(Console.OpenStandardInput());
            if (!File.Exists(Path.Combine(request.PrtDirectory, "PluginProfiler.Plugins.dll")))
                throw new FileNotFoundException($"PluginProfiler.Plugins.dll not found in PRT folder '{request.PrtDirectory}'.");
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                var name = new AssemblyName(e.Name).Name;
                // The SDK always comes from PRT, so a replayed plug-in binds to the same Microsoft.Xrm.Sdk as the context wrapper.
                // A plug-in's other references come from its own folder first.
                var dirs = name.StartsWith("Microsoft.Xrm.Sdk") || name.StartsWith("Microsoft.Crm.Sdk") ? new[] { request.PrtDirectory } : new[] { _pluginDirectory, request.PrtDirectory };
                var path = dirs.Where(d => d != null).Select(d => Path.Combine(d, name + ".dll")).FirstOrDefault(File.Exists);
                return path != null ? Assembly.LoadFrom(path) : null;
            };
            Execute(args[0], request, response);
        }
        // Process boundary: every failure goes back to the server as a JSON error.
        catch (Exception e)
        {
            var message = new StringBuilder();
            for (var x = e; x != null; x = x.InnerException) message.Append(message.Length > 0 ? " -> " : "").Append(x.Message);
            response.Error = message.ToString();
        }
        using (var stdout = Console.OpenStandardOutput())
            new DataContractJsonSerializer(typeof(Response)).WriteObject(stdout, response);
        return response.Error == null ? 0 : 1;
    }

    // NoInlining: PluginProfiler.Plugins must not load before AssemblyResolve is hooked.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Execute(string command, Request request, Response response)
    {
        if (command == "decode-context")
        {
            var xml = new StringBuilder();
            using (var writer = XmlWriter.Create(xml, new XmlWriterSettings { OmitXmlDeclaration = true }))
                new DataContractSerializer(typeof(object), new[] { typeof(PluginExecutionContextWrapper) })
                    .WriteObject(writer, new PluginExecutionContextWrapper(request.Context));
            response.ContextXml = xml.ToString();
            return;
        }
        if (command == "debug") response.Debug = Debug(request);
        else response.Replay = Replay(request);
    }

    static CrmServiceClient Connect(Request request)
    {
        CrmServiceClient.AuthOverrideHook = new TokenHook(request.AccessToken);
        var client = new CrmServiceClient(new Uri(request.OrgUrl), useUniqueInstance: true);
        if (!client.IsReady) throw new InvalidOperationException($"Dataverse connection failed: {client.LastCrmError}");
        return client;
    }
}
