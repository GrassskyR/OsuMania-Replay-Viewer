# Runs inside a clean offline Windows Sandbox using only Windows PowerShell/.NET Framework.
$ErrorActionPreference = 'Stop'
Start-Transcript -Path 'C:\AotResults\acceptance.log' -Force
try {
    $runtimes = @('C:\Program Files\dotnet\shared\Microsoft.NETCore.App', 'C:\Program Files (x86)\dotnet\shared\Microsoft.NETCore.App')
    foreach ($path in $runtimes) {
        if (Test-Path -LiteralPath $path) { throw "Modern .NET runtime found: $path; this is not the required clean environment." }
    }
    Write-Host 'PASS modern .NET runtime absent; network disabled by Sandbox configuration'
    $settings = Join-Path $env:LOCALAPPDATA 'ManiaReplayViewer/ui-settings.json'
    New-Item -ItemType Directory -Force (Split-Path -Parent $settings) | Out-Null
    '{"language":"en","speed":29,"codec":"libx264","codecPreset":"ultrafast","future":{"keep":true}}' | Set-Content -LiteralPath $settings -Encoding utf8
    $options = @{
        Exe = 'C:\AotPackage\ManiaReplayViewer.exe'
        Replay = "C:\AotFixtures\Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr"
        Beatmap = "C:\AotFixtures\Doin - Pine nut (Night Bunny 7) [ATTang's Lv.27].osu"
    }
    & 'C:\AotChecks\desktop-smoke.ps1' @options -Screenshot 'C:\AotResults\judge.png'
    if ($LASTEXITCODE -ne 0) { throw 'Clean-environment judgement failed' }
    & 'C:\AotChecks\desktop-smoke.ps1' @options -Render -Fps 59 -Width 180 -Height 320 -OutputDir 'C:\AotResults\exports' -Screenshot 'C:\AotResults\render.png'
    if ($LASTEXITCODE -ne 0) { throw 'Clean-environment export failed' }
    $saved = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    if (-not $saved.future.keep -or $saved.speed -ne 29 -or $saved.language -ne 'en') { throw 'Legacy preferences did not survive' }
    '{"passed":true,"modernDotnetInstalled":false,"offline":true}' | Set-Content 'C:\AotResults\result.json'
} catch {
    $_ | Out-String | Set-Content 'C:\AotResults\failure.txt'
    '{"passed":false}' | Set-Content 'C:\AotResults\result.json'
    throw
} finally { Stop-Transcript }
