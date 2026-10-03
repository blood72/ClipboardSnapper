param([Parameter(Mandatory = $true)][string]$PublishPath)
$ErrorActionPreference = 'Stop'
trap { Write-Output "::error::$($_.Exception.Message)"; exit 1 }

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopNative {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Monitor {
        public int Size; public Rect Bounds, Work; public uint Flags;
    }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr handle, ref Monitor monitor);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@
[void][DesktopNative]::SetThreadDpiAwarenessContext([IntPtr]::new(-4))
$cursor = [DesktopNative+Point]::new()
[void][DesktopNative]::GetCursorPos([ref]$cursor)
$display = [DesktopNative+Monitor]::new()
$display.Size = [Runtime.InteropServices.Marshal]::SizeOf($display)
if (-not [DesktopNative]::GetMonitorInfo([DesktopNative]::MonitorFromPoint($cursor, 2), [ref]$display)) {
    throw 'Could not read the monitor work area.'
}

function Wait-For($Condition, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        if (& $Condition) { return }
        if ($script:process.HasExited) { throw "App exited while waiting for $Description." }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $screen = ''
    if ($null -ne $script:root) {
        $texts = $script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text))
        $screen = ($texts | ForEach-Object { $_.Current.Name }) -join ' | '
    }
    throw "Timed out waiting for $Description. Screen text: $screen"
}
function Find-Control([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Find-Name($Parent, [string]$Name) {
    if ($null -eq $Parent) { return $null }
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    return $Parent.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Invoke-Control($Element) {
    ([System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Set-Folder([string]$Path) {
    ([System.Windows.Automation.ValuePattern](Find-Control 'FolderPath').GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Path)
}
function Select-Format([string]$Name) {
    $combo = Find-Control 'FormatPicker'
    $expand = [System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Wait-For { $null -ne (Find-Name $script:root $Name) } "the $Name format option"
    $choice = Find-Name $script:root $Name
    ([System.Windows.Automation.SelectionItemPattern]$choice.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    $expand.Collapse()
    $selection = [System.Windows.Automation.SelectionPattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)
    Wait-For { $selection.Current.GetSelection()[0].Current.Name -eq $Name } "$Name selection"
}
function Copy-Image {
    $bitmap = [System.Drawing.Bitmap]::new(32, 24)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::CornflowerBlue)
        [System.Windows.Forms.Clipboard]::SetImage($bitmap)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}
function Start-Monitoring {
    Invoke-Control (Find-Control 'StartButton')
    Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq 'Monitoring' } 'monitoring state'
}
function Stop-Monitoring {
    Invoke-Control (Find-Control 'StopButton')
    Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq 'Stopped' } 'stopped state'
}

$publish = (Resolve-Path $PublishPath).Path
$testFolder = Join-Path $env:RUNNER_TEMP ('ClipboardSnapper-smoke-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testFolder)
$process = Start-Process (Join-Path $publish 'ClipboardSnapper.exe') -WorkingDirectory $publish -PassThru
try {
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero } 'the main window'
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-For { $null -ne (Find-Control 'StartButton') } 'the feature screen'
    if ((Find-Control 'MonitoringStatus').Current.Name -ne 'Ready') { throw 'Initial state is not Ready.' }

    $rect = [DesktopNative+Rect]::new()
    [void][DesktopNative]::GetWindowRect($process.MainWindowHandle, [ref]$rect)
    $width = [Math]::Min(1300, $display.Work.Right - $display.Work.Left)
    $height = [Math]::Min(860, $display.Work.Bottom - $display.Work.Top)
    $left = $display.Work.Left + [Math]::Floor((($display.Work.Right - $display.Work.Left) - $width) / 2)
    $top = $display.Work.Top + [Math]::Floor((($display.Work.Bottom - $display.Work.Top) - $height) / 2)
    if ([Math]::Abs(($rect.Right - $rect.Left) - $width) -gt 8 -or
        [Math]::Abs(($rect.Bottom - $rect.Top) - $height) -gt 8 -or
        [Math]::Abs($rect.Left - $left) -gt 8 -or [Math]::Abs($rect.Top - $top) -gt 8) {
        throw "Unexpected physical window bounds: $($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)."
    }
    Write-Output "::notice::Initial physical bounds and work-area centering verified at $([DesktopNative]::GetDpiForWindow($process.MainWindowHandle)) DPI."

    foreach ($format in @(@('PNG', 'png'), @('JPEG', 'jpg'), @('BMP', 'bmp'))) {
        Set-Folder $testFolder
        Select-Format $format[0]
        Start-Monitoring
        Copy-Image
        $pattern = '*.' + $format[1]
        Wait-For { @(Get-ChildItem $testFolder -Filter $pattern).Count -gt 0 } "$($format[0]) image saving"
        $saved = Get-ChildItem $testFolder -Filter $pattern | Select-Object -First 1
        $image = [System.Drawing.Bitmap]::new($saved.FullName)
        try {
            if ($image.Width -ne 32 -or $image.Height -ne 24) { throw 'Saved image dimensions are incorrect.' }
            $color = $image.GetPixel(10, 10)
            if ([Math]::Abs($color.R - 100) -gt 12 -or [Math]::Abs($color.G - 149) -gt 12 -or [Math]::Abs($color.B - 237) -gt 12) {
                throw 'Saved image pixels do not match the clipboard image.'
            }
        } finally { $image.Dispose() }
        Wait-For { $null -ne (Find-Control 'PreviewImage') } 'the saved-image preview'
        Wait-For { $null -ne (Find-Name (Find-Control 'HistoryList') 'Saved') } 'the saved history entry'
        Stop-Monitoring
    }
    $count = @(Get-ChildItem $testFolder -File).Count
    Copy-Image
    Start-Sleep -Seconds 1
    if (@(Get-ChildItem $testFolder -File).Count -ne $count) { throw 'An image was saved after Stop.' }

    $blocked = Join-Path $testFolder 'blocked'
    Set-Content $blocked 'This file intentionally blocks a save directory.'
    Set-Folder (Join-Path $blocked 'images')
    Start-Monitoring
    Copy-Image
    Wait-For { $null -ne (Find-Name (Find-Control 'HistoryList') 'Failed') } 'the save failure entry'
    Invoke-Control (Find-Name (Find-Control 'HistoryList') 'Details')
    Wait-For { $null -ne (Find-Control 'FileDetails') } 'the failure details dialog'
    $details = Find-Control 'FileDetails'
    $texts = $details.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text))
    if (-not (($texts | ForEach-Object { $_.Current.Name }) -join ' ').Contains('Exception:')) {
        throw 'The failure details do not expose the exception reason.'
    }
    Invoke-Control (Find-Name $details 'Close')
    Stop-Monitoring

    $window = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    if (-not $window.Current.CanMaximize) { throw 'Maximizing is disabled.' }
    $window.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    Wait-For { $window.Current.WindowVisualState -eq [System.Windows.Automation.WindowVisualState]::Maximized } 'maximized state'
    $window.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)
    [void][DesktopNative]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero,
        $display.Work.Left, $display.Work.Top, [Math]::Min(600, $width), [Math]::Min(700, $height), 0x14)
    Wait-For {
        (Find-Name $root 'Browse').Current.BoundingRectangle.Top -gt (Find-Control 'FolderPath').Current.BoundingRectangle.Top
    } 'narrow layout reflow'
    Write-Output '::notice::Feature smoke passed: PNG/JPEG/BMP pixels, Start/Stop, preview/history, failure details, maximizing, and narrow layout reflow.'
    $window.Close()
    if (-not $process.WaitForExit(15000)) { throw 'The app did not close after finishing saves.' }
    if ($process.ExitCode -ne 0) { throw "App close failed: $($process.ExitCode)" }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
    Remove-Item $testFolder -Recurse -Force
}
