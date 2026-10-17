# Build/run helper for this repository.
#
# This machine has no system-wide .NET 8 SDK (only an x86 .NET 5 one), so the portable
# SDK extracted to D:\Tools\dotnet8\sdk is used explicitly. NuGet is likewise unreachable,
# so all packages are restored from the committed local feed in .\packages.
#
#   .\build.ps1 build        build the whole solution
#   .\build.ps1 test         run every test project
#   .\build.ps1 cli <args>   run the headless harness (mrm)
#   .\build.ps1 app          run the WPF application
#   .\build.ps1 appcheck     UI smoke test: drive the window, judge a fixture, verify numbers
#   .\build.ps1 e2e          render + encode one fixture end to end
#   .\build.ps1 gamecheck    run the game-integration validation harness
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = 'help',

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = 'Stop'

$env:DOTNET_ROOT = 'D:\Tools\dotnet8\sdk'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$dotnet = 'D:\Tools\dotnet8\sdk\dotnet.exe'
$repo = $PSScriptRoot
$sln = Join-Path $repo 'ManiaReplayMaster.sln'
$packages = Join-Path $repo 'packages'

if (-not (Test-Path $dotnet)) {
    throw "Portable .NET 8 SDK not found at $dotnet. See README for how it is provisioned."
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

switch ($Command) {
    'build' {
        if (Test-Path $sln) { Invoke-Dotnet @('build', $sln, '-v', 'q', '--nologo') }
        else { Invoke-Dotnet @('build', (Join-Path $repo 'src\ManiaReplayMaster.App\ManiaReplayMaster.App.csproj'), '-v', 'q', '--nologo') }
    }
    'test' {
        Invoke-Dotnet @('test', (Join-Path $repo 'tests\ManiaReplayMaster.Tests\ManiaReplayMaster.Tests.csproj'), '--nologo', '-v', 'q')
        $gameTests = Join-Path $repo 'tests\ManiaReplayMaster.GameTests\ManiaReplayMaster.GameTests.csproj'
        if (Test-Path $gameTests) { Invoke-Dotnet @('test', $gameTests, '--nologo', '-v', 'q') }
        $renderTests = Join-Path $repo 'tests\ManiaReplayMaster.RenderEncode.Tests\ManiaReplayMaster.RenderEncode.Tests.csproj'
        if (Test-Path $renderTests) { Invoke-Dotnet @('test', $renderTests, '--nologo', '-v', 'q') }
    }
    'cli' {
        Invoke-Dotnet (@('run', '--project', (Join-Path $repo 'src\ManiaReplayMaster.Cli\ManiaReplayMaster.Cli.csproj'), '--no-launch-profile', '-v', 'q', '--') + $Rest)
    }
    'app' {
        Invoke-Dotnet @('run', '--project', (Join-Path $repo 'src\ManiaReplayMaster.App\ManiaReplayMaster.App.csproj'))
    }
    'e2e' {
        Invoke-Dotnet @('run', '--project', (Join-Path $repo 'tools\e2e\e2e.csproj'), '-v', 'q', '--nologo')
    }
    'gamecheck' {
        # The harness lives one level below tools\gamecheck (its 32-bit synthetic target is a
        # sibling under tools\gamecheck\x86target), so resolve the project path explicitly.
        $gameCheck = Join-Path $repo 'tools\gamecheck\harness\ManiaReplayMaster.GameCheck.csproj'
        if (-not (Test-Path $gameCheck)) { throw "Game-link harness not found at $gameCheck" }
        if ($Rest.Count -gt 0) {
            Invoke-Dotnet (@('run', '--project', $gameCheck, '-v', 'q', '--') + $Rest)
        } else {
            Invoke-Dotnet @('run', '--project', $gameCheck, '-v', 'q', '--', 'all')
        }
    }
    'appcheck' {
        # Drives the real WPF window with UI Automation: paths -> Judge only -> golden numbers ->
        # screenshot. `appcheck -Render` is the same flow through the full render+encode path.
        $appCheck = Join-Path $repo 'tools\appcheck\app-smoke.ps1'
        if (-not (Test-Path $appCheck)) { throw "App smoke test not found at $appCheck" }
        & $appCheck @Rest
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    'restore' {
        Invoke-Dotnet @('restore', $sln, '--source', $packages)
    }
    default {
        Get-Help $PSCommandPath -Detailed | Out-String | Write-Host
        Write-Host 'Commands: build | test | cli <args> | app | appcheck [-Render] | e2e | gamecheck | restore'
    }
}
