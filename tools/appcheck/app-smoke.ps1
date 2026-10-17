# app-smoke.ps1 - repeatable UI smoke test for the WPF app (Mania Replay Viewer).
#
# Why this exists: the app compiled for a long time without anyone clicking through it, and the very
# first automated pass found that the Render / Judge only buttons could never become enabled (CanRender
# was a computed property with no change notification). This script drives the real UI with UI Automation,
# so the same class of wiring bug is caught from now on.
#
# What it does:
#   1. launches ManiaReplayMaster.exe and waits for its main window;
#   2. writes a replay + beatmap path into the two path boxes (ValuePattern, i.e. a real user edit);
#   3. asserts the Render / Judge only buttons become ENABLED (the regression guard);
#   4. clicks "Judge only" and waits for the judgement summary in the log;
#   5. compares the summary with the expected values (defaults: the committed golden for the fixture);
#   6. saves a DPI-aware window screenshot and reports whether crash.log was written.
#
# Exit code 0 = every check passed. Non-zero = a check failed (the failing line is printed).
#
# Usage:
#   pwsh -File tools\appcheck\app-smoke.ps1
#   pwsh -File tools\appcheck\app-smoke.ps1 -Render          # also run a real render (slow, needs ffmpeg)
#   pwsh -File tools\appcheck\app-smoke.ps1 -Screenshot path\to\shot.png

[CmdletBinding()]
param(
    [string] $Exe = "",
    [string] $Replay = "",
    [string] $Beatmap = "",
    [string] $ExpectedNotes = "2711",
    [string] $ExpectedActions = "2515",
    [string] $ExpectedAccuracy = "75.89",
    [string] $ExpectedCounts = "805, 776, 585, 251, 16, 279",
    [switch] $Render,
    [string] $Fps = "",
    [string] $Width = "",
    [string] $Height = "",
    [string] $OutputDir = "",
    [string] $Screenshot = "",
    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Exe) {
    $Exe = Join-Path $repo "src\ManiaReplayMaster.App\bin\Debug\net8.0-windows\ManiaReplayMaster.exe"
}
if (-not $Replay) {
    $Replay = Join-Path $repo "fixtures\Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr"
}
if (-not $Beatmap) {
    $Beatmap = Join-Path $repo "fixtures\Doin - Pine nut (Night Bunny 7) [ATTang's Lv.27].osu"
}
if (-not $Screenshot) {
    $Screenshot = Join-Path $repo "out\appcheck\app-smoke.png"
}

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool] $condition, [string] $message, [string] $detail = "") {
    if ($condition) {
        Write-Host "  PASS  $message"
    } else {
        Write-Host "  FAIL  $message $detail"
        $failures.Add($message)
    }
}

if (-not (Test-Path -LiteralPath $Exe)) { Write-Host "FAIL: app not built at $Exe"; exit 1 }
if (-not (Test-Path -LiteralPath $Replay)) { Write-Host "FAIL: replay not found: $Replay"; exit 1 }
if (-not (Test-Path -LiteralPath $Beatmap)) { Write-Host "FAIL: beatmap not found: $Beatmap"; exit 1 }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class MrSmokeDpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

# This machine runs at 150% scaling. Without per-monitor DPI awareness, GetWindowRect and PrintWindow
# report/patched coordinates as if they were 100%, which crops the capture and makes a perfectly good
# layout look clipped. Set awareness BEFORE touching window handles.
$dpiAware = [MrSmokeDpi]::SetProcessDpiAwarenessContext([IntPtr](-4))
Write-Host "DPI awareness (per-monitor v2) set: $dpiAware"

$crashLog = Join-Path $env:LOCALAPPDATA "ManiaReplayViewer\crash.log"
$crashBefore = if (Test-Path $crashLog) { (Get-Item $crashLog).LastWriteTimeUtc } else { [datetime]::MinValue }

$proc = Start-Process -FilePath $Exe -PassThru
try {
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $proc.Refresh()
        if ($proc.HasExited -or $proc.MainWindowHandle -ne 0) { break }
    }
    Check (-not $proc.HasExited) "app process is running"
    if ($proc.HasExited) { exit 1 }
    Check ($proc.MainWindowHandle -ne 0) "main window created"
    Check ($proc.MainWindowTitle -eq "Mania Replay Viewer") "window title" "got '$($proc.MainWindowTitle)'"

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    function Find-ById([string] $id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    function Find-Name([string] $name) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)
        return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }

    foreach ($pair in @(@("ReplayBox", $Replay), @("BeatmapBox", $Beatmap))) {
        $element = Find-ById $pair[0]
        if ($null -eq $element) { Check $false "found $($pair[0])"; exit 1 }
        $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($pair[1])
    }

    # Optional render parameters. A full-size render of a real replay is minutes of work, so the smoke
    # test uses tiny settings when it exercises the encode path; -Fps 5 -Width 180 -Height 320 keeps it
    # in the tens of seconds while still going through ffmpeg for real.
    foreach ($pair in @(@("FpsBox", $Fps), @("WidthBox", $Width), @("HeightBox", $Height), @("OutputBox", $OutputDir))) {
        if ($pair[1]) {
            $element = Find-ById $pair[0]
            if ($null -eq $element) { Check $false "found $($pair[0])"; exit 1 }
            $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue([string]$pair[1])
        }
    }
    Start-Sleep -Milliseconds 700

    # THE regression guard: derived state must be announced. When this was missing, both action buttons
    # stayed disabled no matter what the user typed or browsed, making the app unusable.
    $judgeButton = Find-ById "JudgeOnlyButton"
    $renderButton = Find-ById "RenderButton"
    Check ($null -ne $judgeButton) "found the 'Judge only' button"
    Check ($null -ne $renderButton) "found the 'Render' button"
    if ($null -eq $judgeButton) { exit 1 }
    $enableDeadline = (Get-Date).AddSeconds(5)
    while (-not $judgeButton.Current.IsEnabled -and (Get-Date) -lt $enableDeadline) {
        Start-Sleep -Milliseconds 100
    }
    $actualReplay = (Find-ById "ReplayBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    $actualBeatmap = (Find-ById "BeatmapBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    Check $judgeButton.Current.IsEnabled "action buttons enable once both paths are set (CanRender notification)" `
        "replay='$actualReplay', beatmap='$actualBeatmap'"
    if (-not $judgeButton.Current.IsEnabled) { exit 1 }

    $button = if ($Render) { $renderButton } else { $judgeButton }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Host "  invoked '$($button.Current.Name)'"

    $logBox = Find-ById "LogBox"
    $lines = @()
    $expectedPattern = if ($Render) { "\.mp4" } else { "accuracy|准确率" }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $items = $logBox.FindAll([System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        $lines = @()
        foreach ($item in $items) { if ($item.Current.Name) { $lines += $item.Current.Name } }
        if ($lines -match $expectedPattern) { break }
        $proc.Refresh()
        if ($proc.HasExited) { break }
    }

    Write-Host "  log:"
    foreach ($line in $lines) { Write-Host "    $line" }

    Check (-not $proc.HasExited) "app survived the run"
    if ($Render) {
        $output = if ($OutputDir) { if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repo $OutputDir } } else { Join-Path $repo "out" }
        $mp4 = Get-ChildItem -LiteralPath $output -Filter "*.mp4" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Check ($null -ne $mp4 -and $mp4.Length -gt 0) "render produced an mp4" $output
        if ($null -ne $mp4) { Write-Host "  mp4: $($mp4.FullName) ($([math]::Round($mp4.Length / 1KB)) KB)" }
        Check (($lines -join "`n") -match "accuracy|准确率") "render log reports accuracy"
        Check (($lines -join "`n") -notmatch "Failed|失败") "no failure reported in the log"
    } else {
        $joined = $lines -join "`n"
        Check ($joined -match "notes $ExpectedNotes, actions $ExpectedActions|音符 $ExpectedNotes，操作 $ExpectedActions") "judgement: notes/actions" "expected $ExpectedNotes/$ExpectedActions"
        Check ($joined -match "(?:accuracy|准确率) $([regex]::Escape($ExpectedAccuracy))%") "judgement: accuracy" "expected $ExpectedAccuracy%"
        Check ($joined -match "(?:judgement counts \(ordered\): |判定数量（按等级）：)$([regex]::Escape($ExpectedCounts))") "judgement: counts" "expected $ExpectedCounts"
        Check ($joined -notmatch "Failed|error|失败") "no failure reported in the log"
    }

    Check (-not (Test-Path $crashLog) -or (Get-Item $crashLog).LastWriteTimeUtc -le $crashBefore) `
        "no new crash.log entry"

    # DPI-aware window screenshot. Failures here must not abort the run: the functional checks above are
    # the point, and a missing screenshot is reported as its own check.
    $proc.Refresh()
    $rect = New-Object MrSmokeDpi+RECT
    $gotRect = [MrSmokeDpi]::GetWindowRect($proc.MainWindowHandle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    Check ($gotRect -and $width -gt 0 -and $height -gt 0) "window rect is measurable for the screenshot" `
        "gotRect=$gotRect size=${width}x${height} handle=$($proc.MainWindowHandle)"
    if ($gotRect -and $width -gt 0 -and $height -gt 0) {
        try {
            # The RECT fields can arrive as strings through some call paths, so cast explicitly: the
            # Bitmap ctor rejects a string argument with an unhelpful "parameter is not valid".
            $bitmap = [System.Drawing.Bitmap]::new([int]$width, [int]$height)
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            $hdc = $graphics.GetHdc()
            [void][MrSmokeDpi]::PrintWindow($proc.MainWindowHandle, $hdc, 2)
            $graphics.ReleaseHdc($hdc)
            $graphics.Dispose()
            $screenshotDir = Split-Path -Parent $Screenshot
            if ($screenshotDir -and -not (Test-Path -LiteralPath $screenshotDir)) { New-Item -ItemType Directory -Path $screenshotDir | Out-Null }
            $bitmap.Save($Screenshot, [System.Drawing.Imaging.ImageFormat]::Png)
            $bitmap.Dispose()
            Write-Host "  screenshot: $Screenshot ($width x $height)"
        } catch {
            Check $false "screenshot captured" $_.Exception.Message
        }
    }
}
finally {
    try {
        if (-not $proc.HasExited) {
            [void]$proc.CloseMainWindow()
            if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
        }
    } catch {
        # Cleanup is best effort.
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "APP SMOKE TEST PASSED"
    exit 0
}

Write-Host "APP SMOKE TEST FAILED ($($failures.Count) check(s))"
$failures | ForEach-Object { Write-Host "  - $_" }
exit 1
