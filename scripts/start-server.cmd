@echo off
rem MCP entry point of the Claude Code plugin: builds the server if this version isn't built yet (build.ps1), then runs it.
rem cmd, not PowerShell, starts the server: cmd's child inherits stdin and stdout untouched, which the MCP stdio transport needs.
setlocal
set "SERVER="
for /f "usebackq delims=" %%a in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" ^<nul`) do (
    set "SERVER=%%a"
)
if not defined SERVER exit /b 1
"%SERVER%"
