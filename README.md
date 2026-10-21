# DataversePluginMcp

A stdio MCP server that lets Claude do what the Plug-in Registration Tool does: register and update plug-in assemblies, steps, images, packages, service endpoints and Custom APIs in Dataverse, and add them to an unmanaged solution. It also reads trace logs, starts and stops PRT's trace-log profiler, decodes its captures, and replays a capture with Visual Studio breakpoints (`debug_profile`). Windows only.

## Tools

| Tool | PRT equivalent |
|---|---|
| `inspect_assembly` | Reads a DLL locally: identity, signing and the IPlugin/CodeActivity classes it contains |
| `list_registrations` | The assembly → type → step → image tree, plus the solutions each assembly belongs to |
| `register_assembly` | Register New Assembly / Update. Also creates the plug-in types and optionally adds the assembly to a solution |
| `register_step` | Register New Step / Update Step (create or update, matched by step name, or without a name by handler + message + entity), for a plug-in type or a service endpoint |
| `register_image` | Register New Image (create or update, matched by alias) |
| `set_step_state` | Enable / Disable |
| `inspect_package` | Reads a .nupkg locally: id, version, and the plug-ins in each DLL under `lib/` |
| `register_package` | Register New Package / Update (plug-in package with dependent assemblies) |
| `register_service_endpoint` | Register New Web Hook / Service Endpoint (create or update, matched by name) |
| `list_service_endpoints` | Webhooks and Service Bus / Event Hub endpoints with their steps. Secrets are never returned |
| `register_custom_api` | Custom API with its request parameters and response properties (create or update) |
| `list_custom_apis` | Custom APIs with their main operation, fields and steps |
| `get_trace_logs` | Plug-in Trace Log viewer |
| `list_profiles` | Trace-log profiler state: steps being profiled, the environment's trace-log setting, and captures (read-only) |
| `get_profile` | Decodes one trace-log capture (from Dataverse or a saved file) into its execution context (read-only) |
| `start_profiling` | Start Profiling (trace-log profiler): sets `enablepluginprofiler=true` on a plug-in step and reports the trace-log setting |
| `stop_profiling` | Stop Profiling: sets `enablepluginprofiler=false` on the step |
| `debug_profile` | Debug / Replay, without PRT: runs your local plug-in DLL on a real capture in a local host that Visual Studio attaches to. **Its writes are real** (needs `allowWrites=true`) |
| `set_trace_log_setting` | Environment trace setting: Off, Exception or All |
| `unregister` | Unregister an image, step, type, assembly, package, endpoint, Custom API, parameter or property (marked destructive) |

## Install as a Claude Code plugin

The repository is a Claude Code plugin (`.claude-plugin/`): the MCP server `dataverse`, built from source, and five skills: `dataverse-plugin-registration`, `dataverse-plugin-troubleshooting`, `dataverse-custom-api`, `dataverse-webhooks` and `dataverse-plugin-debugging`. The skills drive the bundled server's tools, so install the plugin rather than copying the skills alone.

Requirements:

- Windows 10 or 11, Claude Code, and the .NET 8 SDK on `PATH`.
- Internet access to NuGet: the first build restores the Dataverse client and the Plugin Registration Tool (PRT) package on your machine.
- Visual Studio, running on the same machine, only for `debug_profile`.
- A Dataverse dev or sandbox environment.

```bash
claude plugin marketplace add Manh3012/dataverse-plugin-mcp
```

```bash
claude plugin install dataverse-plugin-tools@dataverse-plugin-mcp
```

The first session start builds the server into `%LOCALAPPDATA%\DataversePluginMcp\build` (about a minute). If the `dataverse` server shows as failed while that build runs, wait for it and reconnect with `/mcp`, or start a new session. Updates come with `claude plugin marketplace update`.

Ask Claude to select a Dataverse environment: `select_environment` opens the browser so you can sign in, using the Global Discovery Service (commercial cloud) and Microsoft's sample AppId (Microsoft supports it for development and testing; a tenant that blocks it needs its own app registration, which this server doesn't support yet), then shows you a picker of your environments. Every tool connects to the one you pick, and in the desktop app a band above the prompt shows it. The sign-in (an encrypted MSAL cache) and the choice are kept in `%LOCALAPPDATA%\DataversePluginMcp`, so later sessions start where you left off. Switch environments at any time the same way. Only the commercial cloud is supported (no GCC, USG, DOD or China), and there is no ClientSecret / Certificate auth.

The optional **Plugin Registration Tool folder** option (`/plugin` > `dataverse-plugin-tools` > **Configure options**) points at another PRT.

Until the setup is complete, every session start shows what is missing (a PRT folder without `PluginProfiler.Plugins.dll`, or a failed build), and Claude repeats it the first time you ask for one of the plugin's tools.

### Safety and privacy

- Use a dev or sandbox environment. `debug_profile`'s replays, and the register, unregister and profiling tools, write to the selected environment.
- `allowWrites` is set by Claude, not by you. Keep Claude Code asking before `debug_profile`, `unregister` and the `register_*` tools (don't "always allow" them).
- Tool arguments and results, including SAS keys, webhook keys, secure configuration and captured record data, go to Claude and stay in the session transcript.

### From a local clone (development)

```bash
git clone https://github.com/Manh3012/dataverse-plugin-mcp.git
```

```bash
powershell -ExecutionPolicy Bypass -File dataverse-plugin-mcp/scripts/install.ps1
```

The installer registers the clone as a local marketplace, installs the plugin at user scope and builds the server. Claude Code loads the plugin from the clone in place: after a `git pull`, the next session's start hook rebuilds the server if its sources changed.

## Manual build (without the plugin)

```bash
dotnet publish -c Release -o publish
dotnet publish ProfilerHelper -c Release -o publish/ProfilerHelper
```

The second command is needed only for `get_profile`'s decoding and for `debug_profile`. It builds a small .NET Framework 4.8 helper (Windows only) that runs PRT's public `PluginExecutionContextWrapper` and hosts `debug_profile`'s replay. Keep it in its own `ProfilerHelper` folder. It references PRT's DLLs at compile time only and loads them at runtime from the folder in `PRT_DIRECTORY`, so PRT must be on the machine:

- Download the [Microsoft.CrmSdk.XrmTooling.PluginRegistrationTool](https://www.nuget.org/packages/Microsoft.CrmSdk.XrmTooling.PluginRegistrationTool) package (or use the copy `pac tool prt` installed) and set `PRT_DIRECTORY` to its `tools` folder, the one with `PluginProfiler.Plugins.dll`. Pass it to the server with `-e "PRT_DIRECTORY=C:\path\to\prt\tools"` in the `claude mcp add` command below.
- For `debug_profile`, the helper reuses the server's access token from the browser sign-in.
- `debug_profile` also needs Visual Studio running on the same machine, at the same elevation as the server.

Why a setting instead of bundling PRT's DLLs: PRT is licensed under Microsoft's [SDK license terms](https://go.microsoft.com/fwlink/?linkid=2108407). They allow distributing its `.dll` files only with conditions: you must pass equivalent protective terms on to your users and indemnify Microsoft. With `PRT_DIRECTORY`, this repository and its builds contain none of Microsoft's files. Each user gets PRT from Microsoft and accepts that license directly, and the helper uses whichever PRT version they have. Don't commit PRT's DLLs here.

## Connect to Claude Code

The server uses the browser sign-in and environment picker described above:

```bash
claude mcp add --transport stdio -s user -e "PRT_DIRECTORY=C:\path\to\prt\tools" dataverse -- C:\path\to\publish\DataversePluginMcp.exe
```

The sign-in uses Microsoft's sample app ID, intended for development only.

## Typical prompt

> Build my plug-in project, register `bin\Release\net462\Contoso.Plugins.dll` into solution `ContosoCore`, then add a PostOperation async step for `Contoso.Plugins.AccountCreate` on Create of account, with a post image `Image` on `name,accountnumber`.

> Replay the newest trace-log capture of `Contoso.Plugins.AccountCreate` with my Debug build in `bin\Debug\net462`, in the dev environment. Writes are OK.

## Debugging with the trace-log profiler

This server supports only PRT's trace-log profiler (PRT 9.1.0.184+ with `LegacyPluginProfiler=false`). It needs no profiler solution. The legacy Plug-in Profiler (the PluginProfiler solution) is not supported; PRT 9.1.0.199's notes say its Install Profiler no longer works on Online.

PRT's Start Profiling sets `enablepluginprofiler=true` on the step, and Stop Profiling sets it back to false. While the environment's trace-log setting is All, each run of a flagged step writes its context to `plugintracelog.profile` about 25 seconds later. At Exception only runs that throw are captured; at Off nothing is. Asynchronous steps pick up a changed setting a few minutes late.

1. **Start:** call `start_profiling(stepId=...)`, or use PRT > **Start Profiling**. It refuses steps that aren't plug-in steps and steps already flagged. Like PRT, it doesn't change the trace-log setting. The result reports the setting: Off captures nothing, Exception only runs that throw (enough to debug a crash), All every run. Call `set_trace_log_setting(Exception)` or `(All)` and keep the `Previous` value it returns.
2. **Dataverse:** run the operation that triggers the plug-in (save the record, call the API).
3. **Claude:** `list_profiles` shows the flagged steps, the trace-log setting and the captures (`TraceLogProfiles`). `get_profile(profileId=...)` decodes one: the execution context (message, stage, depth, users, InputParameters / OutputParameters, Pre/Post images, SharedVariables, parent context). A capture holds only the context: no timings, exception or configuration. For a profile saved to a file (the bare base64 of `plugintracelog.profile`), use `get_profile(filePath=...)`. Decoding runs PRT's own `PluginExecutionContextWrapper` through the helper; without the helper and `PRT_DIRECTORY`, `get_profile` says why it couldn't decode.
4. **Visual Studio:** for breakpoints, open the plug-in's project in Visual Studio, build it (Debug, with its PDB), set breakpoints, and call `debug_profile(profileId=..., dllPath=..., allowWrites=true)`. See below.
5. **Stop:** `stop_profiling(stepId=...)`, or PRT > **Stop Profiling**. Then restore the trace-log setting with `set_trace_log_setting`. Captures already written stay until the trace log is purged (about 24 hours).

A capture holds real record data, so treat `get_profile`'s output as sensitive.

### `debug_profile`

`debug_profile` is our own replay host, not PRT's replay (PRT's replay code isn't public). `ProfilerHelper replay` runs in its own .NET Framework 4.8 process and does this:

1. It builds the captured `IPluginExecutionContext` from the protobuf profile with PRT's public `PluginExecutionContextWrapper`.
2. It loads your DLL. `Microsoft.Xrm.Sdk` always comes from PRT, and other references come from the DLL's folder first.
3. It finds Visual Studio in the Running Object Table and attaches it to itself through EnvDTE (`Process.Attach`). It picks the instance whose solution or project folder contains the DLL, or the only instance running; `vsProcessId` picks one explicitly. It waits until the debugger is attached (60 s).
4. It constructs the plug-in the way the platform does, with `(unsecure, secure)`, `(unsecure)` or no arguments, and calls `Execute`.

The plug-in type and configuration aren't in the capture. By default they come from the step that ran (the context's `OwningExtension`), including the secure configuration. `pluginType`, `unsecureConfiguration` and `secureConfiguration` override them; pass all three and the step isn't read.

The service provider gives the plug-in the captured context and an `ITracingService` whose output is returned. It also gives an `IOrganizationServiceFactory` backed by a **real connection** to this server's environment, with the server's own token. Every call the plug-in makes really happens, outside any transaction, as the captured user (`CallerId`). A `null` user runs as the connected user, not SYSTEM. Other services (`ILogger`, `IServiceEndpointNotificationService`, ...) return null. The result has the trace output, the plug-in's exception (type, message, stack trace), the duration, and every organization-service call (method, target and attribute names, user, result or error). The call waits up to 30 minutes while you debug; the access token may expire during a long session.

Use it only against an environment where those writes are acceptable. The replay acts on the captured records: a PostOperation Create replay updates the record the captured run created, which fails if that record has been deleted since.

`breakpoints` adds breakpoints in Visual Studio before the run, comma-separated: `C:\src\AccountCreate.cs:42` or a method such as `Contoso.Plugins.AccountCreate.Execute`. Ones Visual Studio already has are skipped, and the added ones stay in Visual Studio afterwards.

### Step mode: `debug_profile(stepMode=true)` and `debug_step`

With `stepMode=true`, `debug_profile` doesn't wait for the replay to end. It puts a breakpoint on the plug-in's `Execute` (unless `breakpoints` says otherwise), starts the replay in the background and returns as soon as Visual Studio stops there. Then `debug_step` drives Visual Studio through EnvDTE (`ProfilerHelper debug`):

- `state`: where it is stopped.
- `over`, `into`, `out`: step `count` statements (up to 100). Each step lists its line, its source text, the locals that changed (including Visual Studio's return values of the calls on that line), the `evaluate` values after it, and the trace lines and organization-service calls it caused (`Output`).
- `continue`: run to the next breakpoint or the end.
- `stop`: kill the replay. Organization-service calls already made stay in Dataverse.

While stopped, each result has the function, file, line and source text, the locals, the stack (10 frames), the exception at an exception break, the values of the `evaluate` expressions, and the trace lines and organization-service calls so far (`Output`). An `Entity` local shows as `logicalname id (n attributes): name=value, ...`; `evaluate` takes C# expressions, evaluated in the paused replay. Once the replay has ended, the result has the replay's final result instead. Every action waits until Visual Studio is in break mode again (`timeoutSeconds`, default 60). After a timeout the replay keeps running; call `debug_step(state)` later.

One step-mode replay runs per server at a time. When the server shuts down normally it kills its replay; a server that is killed outright leaves a paused `ProfilerHelper.exe` behind.

## Tests

`tests/smoke.py` drives the built server over stdio without Dataverse credentials. It checks the tool schemas, input validation (including `debug_profile`'s), and that every Dataverse tool fails cleanly.

```bash
python tests/smoke.py publish/DataversePluginMcp.exe --dll path/to/SamplePlugins.dll --profile path/to/real-capture.txt --prt path/to/prt/tools
```

`--dll`, `--profile` and `--prt` are optional. `--profile` also decodes a real saved trace-log profile. `--prt` runs `ProfilerHelper.exe` against PRT's real libraries and decodes a synthetic protobuf capture (`tests/fixtures`, made-up values written with PRT's public API). It also replays that context as far as loading the DLL. It needs the helper built into `publish/ProfilerHelper`. It also checks that `ProfilerHelper debug` reports a replay that no longer runs as ended. The Visual Studio attach, stepping and real writes are not covered by the smoke test.

## Not covered

- `get_profile` returns only the context, not the rest of its trace row. The duration, exception details and trace output of that row come from `get_trace_logs`; the unsecure configuration from neither.
- Protobuf contexts of workflow-activity captures (only plug-in contexts are decoded), and decoding on machines without PRT.
- Replaying workflow activities with `debug_profile` (plug-ins only), and platform behavior it doesn't reproduce: the transaction, the SYSTEM user, sandbox isolation, and services other than context, tracing and organization service.

## Status

Tested on Windows with PRT 9.1.0.235 and Visual Studio 18 against a Dataverse Developer environment: registration and updates, the trace-log profiler on synchronous, asynchronous and plug-in package steps, `get_profile`, and `debug_profile` with step mode. Visual Studio 2022 is untested.

## License

MIT, see [LICENSE](LICENSE). This covers the code in this repository only. The Plugin Registration Tool, `PluginProfiler.Plugins.dll` and the Dataverse SDK DLLs are Microsoft's, under [Microsoft's SDK license terms](https://go.microsoft.com/fwlink/?linkid=2108407). This repository doesn't include them; the build downloads them from NuGet, and by building you accept those terms. That license forbids reverse engineering or decompiling them.

Not affiliated with or endorsed by Microsoft. Dataverse, Dynamics 365, Power Platform and Visual Studio are trademarks of Microsoft.
