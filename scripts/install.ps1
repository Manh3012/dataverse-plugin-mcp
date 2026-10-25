# Installs this clone as a Claude Code plugin, once per machine: registers the clone as a local marketplace, installs the plugin
# at user scope and builds the server. Claude Code loads the plugin from this folder in place, so a later git pull applies
# at the next session start (the hook rebuilds the server when its sources changed).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

foreach ($tool in 'claude', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is not on PATH. Install Claude Code and the .NET 8 SDK first." }
}

# Native commands write progress to stderr; under Stop, Windows PowerShell would turn that into an error.
$ErrorActionPreference = 'Continue'
& claude plugin marketplace add $root
& claude plugin install dataverse-plugin-tools@dataverse-plugin-mcp --scope user
if ($LASTEXITCODE -ne 0) { throw "claude plugin install failed (exit $LASTEXITCODE)." }

# Not -Hook: a failed build must fail the install. stdout is the server path, which isn't needed here.
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "The server build failed (exit $LASTEXITCODE)." }

Write-Host @'
Installed. Before the first use:
- Environment: ask Claude to select a Dataverse environment; it signs you in through the browser and shows you a picker.
  Use a dev or sandbox environment: replays write to it.
- No Plugin Registration Tool installation or folder setting is needed; the build publishes the SDK dependencies from NuGet.
- debug_profile also needs Visual Studio running with the plug-in's solution open.
Then start a new Claude Code session.
'@
