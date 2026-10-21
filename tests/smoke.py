"""Offline regression test: drives the built MCP server over stdio with no Dataverse credentials.

usage: python tests/smoke.py <path to DataversePluginMcp.exe> [--dll <plug-in DLL with
       Sample.Plugins.AccountCreate, ContactUpdate and MyActivity>]
       [--prt <PRT tools folder, to run ProfilerHelper.exe against PRT's real libraries>]
       [--profile <real trace-log profile: the bare base64 of plugintracelog.profile; needs --prt>]

Checks tool schemas, local DLL / .nupkg inspection, input validation, trace-log profile loading, and that every
Dataverse tool fails cleanly without credentials or a selected environment. start_profiling / stop_profiling / debug_profile input validation is checked
up to the Dataverse call; --prt also checks that ProfilerHelper.exe loads PRT's libraries, replays a protobuf context as far as
loading the DLL, and that get_profile decodes the synthetic protobuf context in tests/fixtures with PRT's own classes.
--profile decodes a real capture as well.
"""
import argparse, base64, json, os, subprocess, tempfile, zipfile

ap = argparse.ArgumentParser()
ap.add_argument("exe"); ap.add_argument("--dll"); ap.add_argument("--profile"); ap.add_argument("--prt")
args = ap.parse_args()

EXPECTED = {"inspect_assembly", "list_registrations", "register_assembly", "register_step", "register_image", "set_step_state", "unregister",
            "inspect_package", "register_package", "get_trace_logs", "set_trace_log_setting", "register_service_endpoint",
            "list_service_endpoints", "register_custom_api", "list_custom_apis", "list_profiles", "get_profile", "start_profiling", "stop_profiling",
            "debug_profile", "debug_step", "list_environments", "select_environment"}

fake_prt = tempfile.mkdtemp()  # PRT_DIRECTORY; empty until the profiler section adds a dummy PluginProfiler.Plugins.dll
# An empty LOCALAPPDATA: no saved environment or cached sign-in, so no Dataverse tool tries to sign in.
env = os.environ | {"PRT_DIRECTORY": fake_prt, "LOCALAPPDATA": tempfile.mkdtemp()}
p = subprocess.Popen([os.path.abspath(args.exe)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8", env=env)
ids = iter(range(1, 10_000))

def call(method, params=None):
    i = next(ids)
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": i, "method": method, "params": params or {}}) + "\n"); p.stdin.flush()
    while True:
        line = p.stdout.readline()
        assert line, "server closed stdout: " + p.stderr.read()
        msg = json.loads(line)  # any non-JSON on stdout fails here
        if msg.get("id") == i: return msg

def tool(name, **kw):
    return call("tools/call", {"name": name, "arguments": kw})["result"]

def ok(name, **kw):
    r = tool(name, **kw)
    assert not r.get("isError"), r
    return json.loads(r["content"][0]["text"])

def err(name, **kw):
    r = tool(name, **kw)
    assert r.get("isError"), r
    return r["content"][0]["text"]

call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "0"}})
p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n"); p.stdin.flush()

# --- schemas
tools = {t["name"]: t for t in call("tools/list")["result"]["tools"]}
assert set(tools) == EXPECTED, set(tools) ^ EXPECTED
for t in tools.values():
    assert "dv" not in t["inputSchema"]["properties"], "DI param leaked into schema: " + t["name"]
assert tools["unregister"]["annotations"]["destructiveHint"] is True
for name in ("list_profiles", "get_profile", "get_trace_logs", "list_registrations"):
    assert tools[name]["annotations"]["readOnlyHint"] is True, name
dp = tools["debug_profile"]
assert {"dllPath", "allowWrites", "profileId", "filePath", "pluginType", "unsecureConfiguration", "secureConfiguration",
        "vsProcessId", "breakpoints", "stepMode"} <= set(dp["inputSchema"]["properties"]), dp["inputSchema"]["properties"]
assert {"action", "count", "evaluate", "timeoutSeconds"} == set(tools["debug_step"]["inputSchema"]["properties"]), tools["debug_step"]["inputSchema"]
assert dp.get("annotations", {}).get("readOnlyHint") is not True
for name in ("start_profiling", "stop_profiling"):
    assert set(tools[name]["inputSchema"]["properties"]) == {"stepId"}, tools[name]["inputSchema"]["properties"]
print("schemas ok:", len(tools), "tools")

tmp = tempfile.mkdtemp()

# --- local assembly / package inspection (needs a sample DLL)
if args.dll:
    info = ok("inspect_assembly", dllPath=args.dll)
    types = {t["TypeName"]: t["IsWorkflowActivity"] for t in info["Types"]}
    assert types == {"Sample.Plugins.AccountCreate": False, "Sample.Plugins.ContactUpdate": False, "Sample.Plugins.MyActivity": True}, types

    def nupkg(name, extra=None):
        path = os.path.join(tmp, name)
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("Sample.Pkg.nuspec", '<?xml version="1.0"?><package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">'
                       "<metadata><id>Sample.Pkg</id><version>1.2.3</version></metadata></package>")
            z.write(args.dll, "lib/net462/SamplePlugins.dll")
            for k, v in (extra or {}).items(): z.writestr(k, v)
        return path

    pk = ok("inspect_package", nupkgPath=nupkg("ok.nupkg"))
    assert pk["Id"] == "Sample.Pkg" and pk["Version"] == "1.2.3"
    assert [a["Entry"] for a in pk["Assemblies"]] == ["lib/net462/SamplePlugins.dll"] and len(pk["Assemblies"][0]["Types"]) == 3
    assert "unmanaged" in err("inspect_package", nupkgPath=nupkg("native.nupkg", {"lib/net462/native.dll": "not a PE file"}))
    err("inspect_package", nupkgPath=args.dll)  # a DLL is not a zip
    print("inspection ok")
else:
    print("inspection skipped (no --dll)")
err("inspect_assembly", dllPath="C:/nope.dll")

# --- Custom API spec validation runs before any Dataverse call
def capi(**kw):
    return err("register_custom_api", **{"uniqueName": "new_Test", "allowedStepType": "None", **kw})
assert "allowedStepType" in capi(allowedStepType="Bogus")
# Binding checks need the stored API (omitted = unchanged on update), so they run after the Dataverse lookup.
assert "select_environment" in capi(bindingType="Entity")
assert "select_environment" in capi(boundEntity="account")
assert "isOptional" in capi(responseProperties='[{"uniqueName":"R","type":"String","isOptional":true}]')
assert "EntityCollection can't" in capi(responseProperties='[{"uniqueName":"R","type":"EntityCollection","logicalEntityName":"account"}]')
assert "type 'Text'" in capi(requestParameters='[{"uniqueName":"A","type":"Text"}]')
assert "listed twice" in capi(requestParameters='[{"uniqueName":"A","type":"String"},{"uniqueName":"a","type":"Integer"}]')
assert "JSON array" in capi(requestParameters='{"not":"array"}')
assert "select_environment" in capi(requestParameters='[{"uniqueName":"A","type":"String"}]')  # a valid spec reaches Dataverse
print("custom api validation ok")

# --- trace-log profile loading. A file holds the bare base64 of plugintracelog.profile; synthetic bytes here.
def write(name, text):
    path = os.path.join(tmp, name)
    with open(path, "w", encoding="utf-8") as f: f.write(text)
    return path

bare = write("tracelog.b64", base64.b64encode(b"\x00\x01\x02\x03\x04" + b"00000000-0000-0000-0000-000000000002").decode() + "\n")
# PRT_DIRECTORY has no PRT yet, so the context comes back undecoded with the reason.
ctx = ok("get_profile", filePath=bare)["Context"]
assert ctx["Format"] == "protobuf" and "PRT_DIRECTORY" in ctx["Note"], ctx
assert "Not a trace-log profile" in err("get_profile", filePath=write("junk.txt", "hello world, not a profile"))
assert "File not found" in err("get_profile", filePath=os.path.join(tmp, "missing.txt"))
assert "exactly one" in err("get_profile")
assert "exactly one" in err("get_profile", profileId="00000000-0000-0000-0000-000000000001", filePath=bare)
assert "select_environment" in err("get_profile", profileId="00000000-0000-0000-0000-000000000001")
print("profile loading ok")

# --- start / stop profiling: a plain update of the step, so only the step id is checked before Dataverse (no PRT, no ProfilerHelper)
STEP = "00000000-0000-0000-0000-000000000001"
for name in ("start_profiling", "stop_profiling"):
    assert "Guid" in err(name, stepId="not-a-guid"), name
    assert "select_environment" in err(name, stepId=STEP), name
print("profiling tools ok")

# debug_profile: allowWrites, profileId/filePath, dllPath, PRT_DIRECTORY, ProfilerHelper.exe, capture file, then Dataverse
SELF = os.path.abspath(args.exe)  # any existing file passes the dllPath check
def dbg(**kw):
    return err("debug_profile", **{"dllPath": SELF, "allowWrites": True, **kw})
assert "allowWrites" in err("debug_profile", dllPath=SELF, profileId=STEP)
assert "allowWrites" in dbg(allowWrites=False, profileId=STEP)
assert "exactly one" in dbg()
assert "exactly one" in dbg(profileId=STEP, filePath=SELF)
assert "DLL not found" in dbg(dllPath=os.path.join(tmp, "missing.dll"), profileId=STEP)
assert "PRT_DIRECTORY" in dbg(profileId=STEP)
open(os.path.join(fake_prt, "PluginProfiler.Plugins.dll"), "w").close()
helper = os.path.join(os.path.dirname(os.path.abspath(args.exe)), "ProfilerHelper", "ProfilerHelper.exe")
overrides = {"pluginType": "Sample.Plugins.AccountCreate", "unsecureConfiguration": "", "secureConfiguration": ""}
if os.path.exists(helper):  # the capture file is read only after the ProfilerHelper.exe check
    assert "File not found" in dbg(filePath=os.path.join(tmp, "missing.txt"))
    assert "Not a trace-log profile" in dbg(filePath=os.path.join(tmp, "junk.txt"))
    # The dummy PRT can't decode the context, so without the step's type and configurations there's no step to read them from.
    assert "Couldn't read the step" in dbg(filePath=bare)
    assert "select_environment" in dbg(filePath=bare, **overrides)
else:
    assert "ProfilerHelper.exe" in dbg(filePath=bare, **overrides)
print("debug_profile validation ok (helper", "present)" if os.path.exists(helper) else "absent)")

if args.prt:
    assert os.path.exists(helper), "--prt needs ProfilerHelper.exe next to the server: " + helper
    def run_helper(command, request):
        r = subprocess.run([helper, command], input=json.dumps(request), capture_output=True, text=True, timeout=300)
        return r.returncode, json.loads(r.stdout)
    assert "decode-context|replay|debug" in run_helper("bogus", {})[1]["Error"]
    assert "PluginProfiler.Plugins.dll not found" in run_helper("decode-context", {"PrtDirectory": fake_prt + "-missing"})[1]["Error"]
    fixture = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures", "synthetic-protobuf-capture.b64")
    with open(fixture) as f: inner = f.read().strip()
    # replay decodes the context and loads the DLL before connecting; ProfilerHelper.exe itself has no IPlugin types.
    code, out = run_helper("replay", {"PrtDirectory": args.prt, "Context": inner, "DllPath": helper})
    assert code == 1 and "IPlugin types there: none" in out["Error"], out
    assert run_helper("replay", {"PrtDirectory": args.prt, "Context": inner, "DllPath": os.path.join(tmp, "missing.dll")})[0] == 1
    print("ProfilerHelper replay ok against PRT libraries in", args.prt)
    # debug on a process that no longer exists: the replay ended, whatever Visual Studio is running.
    code, out = run_helper("debug", {"PrtDirectory": args.prt, "DebuggeePid": 2_000_000_000, "Action": "state"})
    assert code == 0 and out["Debug"]["Mode"] == "Ended", out
    print("ProfilerHelper debug ok")

# --- every Dataverse tool must fail cleanly (not hang, not crash) without credentials
assert "No step-mode replay" in err("debug_step", action="over")  # no Dataverse or Visual Studio needed
for url in ("not a url", "http://yourorg.crm.dynamics.com"):
    assert "Not an https environment URL" in err("select_environment", url=url), url  # checked before any sign-in

for name in ("list_registrations", "list_profiles", "get_trace_logs", "list_custom_apis", "list_service_endpoints"):
    assert "select_environment" in err(name), name

assert args.prt or not args.profile, "--profile needs --prt to decode the capture"
if args.prt:
    # A server whose PRT_DIRECTORY is real PRT decodes a protobuf context with PRT's own classes. The fixture is synthetic:
    # made-up values serialized with PRT 9.1.0.235's public API.
    p.stdin.close(); p.wait()
    p = subprocess.Popen([os.path.abspath(args.exe)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8",
                         env=env | {"PRT_DIRECTORY": args.prt})
    call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "smoke", "version": "0"}})
    ctx = ok("get_profile", filePath=fixture)["Context"]
    assert ctx.get("MessageName") == "Create" and ctx["PrimaryEntityName"] == "account", ctx
    assert ctx["Depth"] == "2" and ctx["InputParameters"]["Target"]["Attributes"]["name"] == "Contoso", ctx
    print("trace-log context decoded by PRT:", ctx["MessageName"], "of", ctx["PrimaryEntityName"])
    if args.profile:
        ctx = ok("get_profile", filePath=args.profile)["Context"]
        assert ctx.get("MessageName"), ctx
        print("real capture:", ctx["MessageName"], "of", ctx.get("PrimaryEntityName"))
print("ALL OK")
p.stdin.close(); p.wait(timeout=10)
