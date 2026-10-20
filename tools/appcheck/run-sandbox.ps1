# Prepare a reproducible Windows Sandbox acceptance run without changing the host's .NET setup.
[CmdletBinding()]
param([string]$PackagePath = '', [switch]$PrepareOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $PackagePath) { $PackagePath = Join-Path $repo 'artifacts/win-x64-aot' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
if (-not (Test-Path -LiteralPath (Join-Path $package 'ManiaReplayViewer.exe'))) { throw 'Publish the Native AOT package first.' }
$output = Join-Path $repo ('out/aot-check/sandbox-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
function Escape-Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
$wsb = Join-Path $output 'acceptance.wsb'
@"
<Configuration>
  <Networking>Disable</Networking>
  <MappedFolders>
    <MappedFolder><HostFolder>$(Escape-Xml $package)</HostFolder><SandboxFolder>C:\AotPackage</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(Escape-Xml (Join-Path $repo 'fixtures'))</HostFolder><SandboxFolder>C:\AotFixtures</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(Escape-Xml $PSScriptRoot)</HostFolder><SandboxFolder>C:\AotChecks</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$(Escape-Xml $output)</HostFolder><SandboxFolder>C:\AotResults</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\AotChecks\sandbox-validation.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $wsb -Encoding utf8
Write-Host "Sandbox definition: $wsb"
Write-Host "Acceptance logs: $output"
if (-not $PrepareOnly) {
    $sandbox = Get-Command WindowsSandbox.exe -ErrorAction SilentlyContinue
    if (-not $sandbox) { throw "Windows Sandbox is unavailable. Definition retained at $wsb. No clean-environment acceptance has run." }
    Start-Process -FilePath $sandbox.Source -ArgumentList ('"' + $wsb + '"')
}
