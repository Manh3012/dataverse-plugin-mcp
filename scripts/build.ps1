# Builds the MCP server and ProfilerHelper for the Claude Code plugin, once per version of the sources, and prints "<server exe>|<PRT folder>"
# for start-server.cmd. Run by the plugin's SessionStart hook (-Hook: prints nothing) and by start-server.cmd before every server start.
# Logs go to stderr: stdout is start-server.cmd's result, and a SessionStart hook's stdout would become session context.
param([switch]$Hook)
$ErrorActionPreference = 'Stop'

function Log($message) { [Console]::Error.WriteLine("[dataverse-plugin-tools] $message") }

# -Hook: setup problems go to stdout as SessionStart hook JSON: systemMessage for the user, additionalContext for Claude.
function Remind($items) {
    $text = "Dataverse Plugin Tools setup:`n" + (($items | ForEach-Object { "- $_" }) -join "`n")
    @{
        systemMessage = $text
        hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = "$text`nThe first time the user asks for something that uses the dataverse-plugin-tools plugin, tell them these setup steps before calling its tools." }
    } | ConvertTo-Json -Compress
}

# A failed build in the hook becomes a reminder instead of a hook error.
trap { if ($Hook) { Remind @("The server build failed: $_ Fix it, then start a new session.") ; exit 0 } ; break }

function Publish($project, $output) {
    # Continue: under Stop, Windows PowerShell turns the first line a native tool writes to stderr into a terminating error.
    $ErrorActionPreference = 'Continue'
    & dotnet publish $project -c Release -o $output --nologo 2>&1 | ForEach-Object { [Console]::Error.WriteLine("$_") }
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed (exit $LASTEXITCODE)." }
}

$root = Split-Path -Parent $PSScriptRoot
# A fixed folder, not CLAUDE_PLUGIN_DATA: install.ps1 builds outside Claude Code, and the session that follows must find that build
# (a first build takes longer than the MCP startup timeout).
$data = Join-Path $env:LOCALAPPDATA 'DataversePluginMcp'

# One build folder per content hash of the sources: a pull or plugin update builds next to the running version, never over it (Windows locks a running exe).
$sources = Get-ChildItem $root -Recurse -File -Include *.cs, *.csproj | Where-Object { $_.FullName -notmatch '\\(bin|obj|tests)\\' } | Sort-Object FullName
$hashes = ($sources | ForEach-Object { $_.FullName.Substring($root.Length) + ':' + (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join "`n"
$version = (Get-FileHash -Algorithm SHA256 -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($hashes)))).Hash.Substring(0, 12)
$build = Join-Path $data "build\$version"
$exe = Join-Path $build 'DataversePluginMcp.exe'

if (-not (Test-Path (Join-Path $build '.complete'))) {
    # The hook and the server start can both arrive here on a first run: one builds, the other waits for it.
    $lock = [Threading.Mutex]::new($false, 'Local\DataversePluginMcpBuild')
    try { [void]$lock.WaitOne([TimeSpan]::FromMinutes(10)) } catch [Threading.AbandonedMutexException] { }
    try {
        if (-not (Test-Path (Join-Path $build '.complete'))) {
            if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The .NET 8 SDK is required to build the server: https://dotnet.microsoft.com/download' }
            Log "Building the server into $build (first start after an install or update, about a minute)..."
            if (Test-Path $build) { Remove-Item $build -Recurse -Force }
            Publish (Join-Path $root 'DataversePluginMcp.csproj') $build
            Publish (Join-Path $root 'ProfilerHelper\ProfilerHelper.csproj') (Join-Path $build 'ProfilerHelper')
            New-Item (Join-Path $build '.complete') -ItemType File | Out-Null
            Log 'Build done.'
            # Older builds: best effort, a running server keeps its own folder locked.
            Get-ChildItem (Join-Path $data 'build') -Directory | Where-Object Name -ne $version | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    finally { $lock.ReleaseMutex() }
}

if ($Hook) {
    # Plugin options reach hooks as CLAUDE_PLUGIN_OPTION_<KEY>; nothing is printed once everything is set.
    $todo = @()
    $prtOption = $env:CLAUDE_PLUGIN_OPTION_PRT_DIRECTORY
    if ($prtOption -and $prtOption -notlike '${*' -and -not (Test-Path (Join-Path $prtOption 'PluginProfiler.Plugins.dll'))) {
        $todo += "The Plugin Registration Tool folder option ($prtOption) has no PluginProfiler.Plugins.dll; the NuGet PRT is used instead. Clear or fix it in /plugin configure."
    }
    if ($todo) { Remind $todo }
    exit 0
}

# PRT: the folder the user configured, else the PRT package that ProfilerHelper's build restored from NuGet (ProfilerHelper.csproj, PrtVersion).
$prt = @($env:PLUGIN_PRT_DIRECTORY, $env:PRT_DIRECTORY) | Where-Object { $_ -and $_ -notlike '${*' -and (Test-Path (Join-Path $_ 'PluginProfiler.Plugins.dll')) } | Select-Object -First 1
if (-not $prt) {
    $prtVersion = ([regex]::Match((Get-Content (Join-Path $root 'ProfilerHelper\ProfilerHelper.csproj') -Raw), '<PrtVersion>([^<]+)</PrtVersion>')).Groups[1].Value
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $prt = Join-Path $packages "microsoft.crmsdk.xrmtooling.pluginregistrationtool\$prtVersion\tools"
}
"$exe|$prt"
