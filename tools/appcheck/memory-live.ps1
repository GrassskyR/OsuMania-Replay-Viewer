# With osu!stable on a saved score screen, verify WPF auto-fills without typing paths.
# Example: powershell -File tools/appcheck/memory-live.ps1 -ExpectedReplay 'D:\osu\Data\r\....osr'
param([string]$ExpectedReplay = '', [string]$ExpectedBeatmap = '', [int]$TimeoutSeconds = 30)
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$app=Start-Process -FilePath (Join-Path $repo 'src/ManiaReplayMaster.App/bin/Debug/net8.0-windows/ManiaReplayMaster.exe') -PassThru
$until=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$replay=''
while ([DateTime]::UtcNow -lt $until) {
  Start-Sleep -Milliseconds 300
  $app.Refresh()
  if ($app.HasExited) { throw 'App exited before detection' }
  if ($app.MainWindowHandle -eq 0) { continue }
  $window=[System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
  $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'ReplayBox')
  $box=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
  if ($null -eq $box) {continue}
  $replay=([System.Windows.Automation.ValuePattern]$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
  if ($replay) { break }
}
$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'BeatmapBox')
$box=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
$beatmap=([System.Windows.Automation.ValuePattern]$box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
if (!$replay -or !(Test-Path -LiteralPath $replay)) {throw "Missing auto-filled replay: $replay"}
if ($ExpectedReplay -and $replay -ne $ExpectedReplay) {throw "Wrong replay: $replay"}
if (!(Test-Path -LiteralPath $beatmap)) {throw "Missing beatmap: $beatmap"}
if ($ExpectedBeatmap -and $beatmap -ne $ExpectedBeatmap) {throw "Wrong beatmap: $beatmap"}
$condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,'Read process memory')
$checkbox=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
$toggle=[System.Windows.Automation.TogglePattern]$checkbox.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
$toggle.Toggle()
Start-Sleep -Milliseconds 900
$texts=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
$disabled=@($texts | Where-Object {$_.Current.Name -eq 'Process memory reading is disabled.'}).Count -gt 0
$toggle.Toggle()
if (!$disabled) {throw 'Memory checkbox did not disable detection'}
$report="PASS GUI auto-filled the current score without manual input`nPID=$($app.Id)`nReplay=$replay`nBeatmap=$beatmap"
New-Item -ItemType Directory -Force (Join-Path $repo 'out') | Out-Null
$report | Set-Content (Join-Path $repo 'out/gui-live-check.log')
$report
