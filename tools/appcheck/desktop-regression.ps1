# Native Avalonia UIA regression: language, picker, editing, cancellation, restart and close cleanup.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class NativePickerCheck {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, string text);
    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
"@
$scope = [System.Windows.Automation.TreeScope]::Descendants
$settings = Join-Path $env:LOCALAPPDATA 'ManiaReplayViewer/ui-settings.json'
$original = if (Test-Path -LiteralPath $settings) { [IO.File]::ReadAllBytes($settings) } else { $null }
$testDirectory = Join-Path $repo ('out/aot-check/ui-regression-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $testDirectory | Out-Null
$proc = $null
function Wait-For([scriptblock]$Condition, [int]$Seconds = 30) {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out: $Condition"
}
function Find-Id([string]$Id) {
    $root.FindFirst($scope, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id))
}
function Set-Text([string]$Id, [string]$Value) {
    (Find-Id $Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}
function Invoke-Button([string]$Id) {
    (Find-Id $Id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Children {
    @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($proc.Id)" | Where-Object Name -EQ 'ffmpeg.exe')
}
try {
    $proc = Start-Process -FilePath $Exe -WorkingDirectory $repo -PassThru
    Wait-For { $proc.Refresh(); $proc.MainWindowHandle -ne 0 }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $language = Find-Id 'LanguageBox'
    foreach ($name in @('English', '中文')) {
        $language.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $item = $language.FindFirst($scope, [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty, $true)))
        if (-not $item) { throw "Language menu did not expose $name" }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $expected = if ($name -eq 'English') { 'Render' } else { '渲染视频' }
        Wait-For { (Find-Id 'RenderButton').Current.Name -eq $expected }
        Write-Output "PASS live language switch: $name"
    }
    # The native common picker must open and accept a Chinese file path.
    $mc = Join-Path $testDirectory '中文谱面.mc'
    Copy-Item -LiteralPath (Join-Path $repo 'fixtures/aot-minimal.mc') -Destination $mc
    Invoke-Button 'BrowseBeatmapButton'
    $dialog = $null
    Wait-For {
        $script:dialog = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst($scope,
            [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')))
        $null -ne $dialog
    }
    # Windows may expose standard dialog controls as legacy panes without ValuePattern.
    # Drive the observed native Edit HWND and the dialog's actual Open command in that case.
    $fileNames = $dialog.FindAll($scope, [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, '1148'))
    $fileName = @($fileNames | Where-Object { $_.Current.ClassName -eq 'Edit' }) | Select-Object -First 1
    if (-not $fileName) { throw 'Native file-name Edit control was not found' }
    [void][NativePickerCheck]::SendMessage([IntPtr]$fileName.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $mc)
    [void][NativePickerCheck]::PostMessage([IntPtr]$dialog.Current.NativeWindowHandle, 0x0111, [IntPtr]1, [IntPtr]::Zero)
    Wait-For { (Find-Id 'BeatmapBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $mc }
    Write-Output 'PASS native file picker/Chinese path'
    Set-Text 'ReplayBox' (Join-Path $repo 'fixtures/aot-minimal.mr')
    Invoke-Button 'SwapButton'
    Wait-For { (Find-Id 'ReplayBox').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $mc }
    Invoke-Button 'SwapButton'
    (Find-Id 'ReplayBox').SetFocus()
    if (-not (Find-Id 'ReplayBox').Current.HasKeyboardFocus) { throw 'Keyboard focus not assigned' }
    Write-Output 'PASS path swap/keyboard focus'
    Set-Text 'ReplayBox' (Join-Path $repo "fixtures/Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr")
    Set-Text 'BeatmapBox' (Join-Path $repo "fixtures/Doin - Pine nut (Night Bunny 7) [ATTang's Lv.27].osu")
    Set-Text 'WidthBox' '1080'
    Set-Text 'HeightBox' '1920'
    Set-Text 'FpsBox' '120'
    Set-Text 'OutputBox' $testDirectory
    Invoke-Button 'RenderButton'
    Wait-For { (Children).Count -gt 0 }
    Invoke-Button 'CancelButton'
    Wait-For { (Find-Id 'RenderButton').Current.IsEnabled }
    if ((Children).Count -gt 0) { throw 'FFmpeg remained after cancellation' }
    Write-Output 'PASS running export cancellation/child cleanup'
    Set-Text 'WidthBox' '180'
    Set-Text 'HeightBox' '320'
    Set-Text 'FpsBox' '59'
    Set-Text 'ReplayBox' (Join-Path $repo 'fixtures/aot-minimal.mr')
    Set-Text 'BeatmapBox' $mc
    Invoke-Button 'RenderButton'
    Wait-For { (Find-Id 'RenderButton').Current.IsEnabled -and @(Get-ChildItem -LiteralPath $testDirectory -Filter '*.mp4').Count -gt 0 }
    Write-Output 'PASS export again after cancellation'
    Set-Text 'ReplayBox' (Join-Path $repo "fixtures/Kuit - Doin - Pine nut [ATTang's Lv.27] (2020-10-03) OsuMania.osr")
    Set-Text 'BeatmapBox' (Join-Path $repo "fixtures/Doin - Pine nut (Night Bunny 7) [ATTang's Lv.27].osu")
    Set-Text 'WidthBox' '1080'
    Set-Text 'HeightBox' '1920'
    Set-Text 'FpsBox' '120'
    Invoke-Button 'RenderButton'
    Wait-For { (Children).Count -gt 0 }
    $childIds = (Children).ProcessId
    [void]$proc.CloseMainWindow()
    if (-not $proc.WaitForExit(15000)) { throw 'Window did not wait for cancellation and close' }
    foreach ($idToCheck in $childIds) {
        if (Get-Process -Id $idToCheck -ErrorAction SilentlyContinue) { throw "FFmpeg process $idToCheck remained after close" }
    }
    Write-Output 'PASS close during export/process cleanup'
} finally {
    if ($proc -and -not $proc.HasExited) {
        [void]$proc.CloseMainWindow()
        if (-not $proc.WaitForExit(15000)) { Stop-Process -Id $proc.Id }
    }
    if ($null -ne $original) { [IO.File]::WriteAllBytes($settings, $original) }
    elseif (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings }
}
