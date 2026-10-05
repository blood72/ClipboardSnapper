param([Parameter(Mandatory = $true)][string]$PublishPath)
$ErrorActionPreference = 'Stop'
trap {
    $trace = $_.ScriptStackTrace -replace '\r?\n', ' | '
    Write-Output "::error::$($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim()) / $trace"
    exit 1
}

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
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
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
    Scroll-ToTop
    (Find-Control 'FolderPath').SetFocus()
    ([System.Windows.Automation.ValuePattern](Find-Control 'FolderPath').GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Path)
}
function Folder-Value {
    return ([System.Windows.Automation.ValuePattern](Find-Control 'FolderPath').GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
}
function Read-Config {
    # Observe atomic replacement without holding a handle that forbids file deletion/replacement.
    $stream = [IO.FileStream]::new($config, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
function Start-App {
    $script:process = Start-Process (Join-Path $publish 'ClipboardSnapper.exe') -WorkingDirectory $testFolder -PassThru
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero } 'the main window'
    $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-For { $null -ne (Find-Control 'StartButton') -and (Find-Control 'StartButton').Current.IsEnabled } 'loaded save-folder settings'
    if ((Find-Control 'MonitoringStatus').Current.Name -ne 'Ready') { throw 'Initial state is not Ready.' }
}
function Close-App {
    $window = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $window.Close()
    if (-not $process.WaitForExit(15000)) { throw 'The app did not close after finishing saves/settings.' }
    if ($process.ExitCode -ne 0) { throw "App close failed: $($process.ExitCode)" }
    $script:root = $null
}
function Commit-Folder([string]$Path) {
    Set-Folder $Path
    (Find-Control 'FormatPicker').SetFocus()
    Wait-For { (Test-Path $config -PathType Leaf) -and (Read-Config).Contains("SaveFolder=$Path") } 'folder persistence on focus loss without Start'
}
function Find-PickerButton([string[]]$Names) {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($name in $Names) {
        $condition = [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Button))
        $button = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $button -and $button.Current.IsEnabled) { return $button }
    }
    return $null
}
function Scroll-ToTop {
    $scroll = [System.Windows.Automation.ScrollPattern](Find-Control 'MainScroll').GetCurrentPattern(
        [System.Windows.Automation.ScrollPattern]::Pattern)
    if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 0) }
    Wait-For { $null -ne (Find-Control 'StartButton') } 'monitoring controls after scrolling to the top'
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
    Scroll-ToTop
    Invoke-Control (Find-Control 'StartButton')
    Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq 'Monitoring' } 'monitoring state'
}
function Stop-Monitoring {
    Scroll-ToTop
    Invoke-Control (Find-Control 'StopButton')
    Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq 'Stopped' } 'stopped state'
}
function Set-Quality([double]$Quality) {
    ([System.Windows.Automation.RangeValuePattern](Find-Control 'JpegQuality').GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)).SetValue($Quality)
}
function Assert-EmptyHistory {
    Wait-For {
        $null -eq (Find-Control 'PreviewImage') -and
        $null -ne (Find-Name $root 'No images saved yet') -and
        $null -ne (Find-Name $root 'Saved images and failures from this session will be listed here.') -and
        (Find-Control 'PreviewCaption').Current.Name -eq 'The preview updates after an image has been saved.' -and
        $null -eq (Find-Name (Find-Control 'HistoryList') 'Saved') -and
        $null -eq (Find-Name (Find-Control 'HistoryList') 'Failed')
    } 'cleared history, preview and empty guidance'
    $texts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text))
    $names = ($texts | ForEach-Object { $_.Current.Name }) -join ' | '
    if ($names.Contains('Clipboard_') -or $names.Contains('Exception:') -or $names.Contains('Image saved') -or $names.Contains('Save failed')) {
        throw "Old file captions or save messages remain after Clear: $names"
    }
}
function Clear-History {
    $status = (Find-Control 'MonitoringStatus').Current.Name
    $sequence = [DesktopNative]::GetClipboardSequenceNumber()
    $before = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    $button = Find-Control 'ClearHistoryButton'
    if (-not $button.Current.IsEnabled) { throw 'Clear History is disabled.' }
    if (-not $button.Current.HelpText.Contains('Saved files are kept')) { throw 'Clear History does not explain file retention.' }
    Invoke-Control $button
    Assert-EmptyHistory
    if ((Find-Control 'MonitoringStatus').Current.Name -ne $status) { throw 'Clear changed the monitoring state.' }
    if ([DesktopNative]::GetClipboardSequenceNumber() -ne $sequence) { throw 'Clear modified the Windows clipboard.' }
    $after = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    if ($before -ne $after) { throw 'Clear modified saved files.' }
}

$publish = (Resolve-Path $PublishPath).Path
$config = Join-Path $publish 'config.ini'
$originalConfig = if (Test-Path $config -PathType Leaf) { [IO.File]::ReadAllBytes($config) } else { $null }
if (Test-Path $config) { Remove-Item $config -Force }
$testFolder = Join-Path $env:RUNNER_TEMP ('ClipboardSnapper-smoke-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testFolder)
$process = $null
try {
    Start-App
    $defaultFolder = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'ClipboardSnapper'
    if ((Folder-Value) -ne $defaultFolder) { throw 'First launch did not use the default folder.' }

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

    $unrelated = "[Updates]`r`nEnabled=false`r`nInterval=weekly`r`n"
    [IO.File]::WriteAllText($config, $unrelated)
    Commit-Folder $testFolder
    if (-not (Read-Config).Contains($unrelated)) { throw 'Folder persistence removed unrelated settings.' }
    if (Test-Path (Join-Path $testFolder 'config.ini')) { throw 'Settings were written to the working directory instead of beside the executable.' }
    Close-App
    Start-App
    if ((Folder-Value) -ne $testFolder) { throw 'Manual path did not survive restart without Start or captures.' }

    # Exercise the real Windows folder picker, including cancellation.
    $browseFolder = Join-Path $testFolder 'browse-selected'
    [void](New-Item -ItemType Directory -Path $browseFolder)
    Scroll-ToTop
    Invoke-Control (Find-Name $root 'Browse')
    Wait-For { $null -ne (Find-PickerButton @('Select Folder', 'Select folder', 'Choose this folder', 'Choose folder')) } 'the folder picker'
    $choose = Find-PickerButton @('Select Folder', 'Select folder', 'Choose this folder', 'Choose folder')
    $choose.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('%d')
    [System.Windows.Forms.SendKeys]::SendWait($browseFolder)
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 500
    Invoke-Control (Find-PickerButton @('Select Folder', 'Select folder', 'Choose this folder', 'Choose folder'))
    Wait-For { (Folder-Value) -eq $browseFolder } 'Browse selection'
    Wait-For { (Read-Config).Contains("SaveFolder=$browseFolder") } 'immediate Browse persistence'
    Close-App
    Start-App
    if ((Folder-Value) -ne $browseFolder) { throw 'Browse selection did not survive restart.' }
    $beforeCancel = Read-Config
    Invoke-Control (Find-Name $root 'Browse')
    Wait-For { $null -ne (Find-PickerButton @('Cancel')) } 'folder picker cancellation'
    Invoke-Control (Find-PickerButton @('Cancel'))
    Wait-For { $null -eq (Find-PickerButton @('Cancel')) } 'the app after cancelling Browse'
    if ((Folder-Value) -ne $browseFolder -or (Read-Config) -ne $beforeCancel) { throw 'Cancelling Browse changed the preference.' }

    [IO.File]::SetAttributes($config, [IO.FileAttributes]::ReadOnly)
    Set-Folder $testFolder
    (Find-Control 'FormatPicker').SetFocus()
    Wait-For { $null -ne (Find-Name (Find-Control 'SettingsMessage') 'Save folder settings') } 'configuration write warning'
    if ((Folder-Value) -ne $testFolder -or (Read-Config) -ne $beforeCancel) { throw 'Write failure lost the session selection or changed the read-only config.' }
    Close-App
    [IO.File]::SetAttributes($config, [IO.FileAttributes]::Normal)
    Start-App
    if ((Folder-Value) -ne $browseFolder) { throw 'Failed persistence was treated as durable after restart.' }

    $unavailable = Join-Path $testFolder 'unavailable'
    Set-Content $unavailable 'This file is not a folder.'
    Close-App
    [IO.File]::WriteAllText($config, $unrelated + "[Storage]`r`nSaveFolder=$unavailable`r`n")
    Start-App
    if ((Folder-Value) -ne $defaultFolder -or -not (Read-Config).Contains("SaveFolder=$defaultFolder")) {
        throw 'Unusable remembered folder did not fall back and update config.ini.'
    }
    if ($null -eq (Find-Name (Find-Control 'SettingsMessage') 'Save folder settings')) { throw 'Folder fallback is not explained.' }
    Close-App
    Start-App
    if ((Folder-Value) -ne $defaultFolder) { throw 'Fallback did not survive restart.' }
    Remove-Item $unavailable
    Write-Output '::notice::Folder UI persistence passed: manual focus loss without Start/captures, Browse selection/cancel, restart, executable-relative config, unrelated entries, read-only configuration warning/session selection and persisted default fallback.'

    foreach ($format in @(@('PNG', 'png'), @('JPEG', 'jpg'), @('BMP', 'bmp'))) {
        Set-Folder $testFolder
        Select-Format $format[0]
        Write-Output "::notice::Testing $($format[0]) controls and capture."
        if ($format[0] -eq 'JPEG') {
            Wait-For { $null -ne (Find-Control 'JpegQuality') } 'JPEG-only quality control'
            $quality = [System.Windows.Automation.RangeValuePattern](Find-Control 'JpegQuality').GetCurrentPattern(
                [System.Windows.Automation.RangeValuePattern]::Pattern)
            if ($quality.Current.Value -ne 90 -or $quality.Current.Minimum -ne 1 -or $quality.Current.Maximum -ne 100) {
                throw 'JPEG quality default/range is incorrect.'
            }
            Set-Quality 42
        } elseif ($null -ne (Find-Control 'JpegQuality')) { throw 'JPEG quality is shown for PNG/BMP.' }
        Start-Monitoring
        if ($format[0] -eq 'JPEG' -and (Find-Control 'JpegQuality').Current.IsEnabled) { throw 'Quality is editable while monitoring.' }
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
        Write-Output "::notice::$($format[0]) saved and displayed; testing Clear History."
        Clear-History
        Clear-History
        $previousCount = @(Get-ChildItem $testFolder -Filter $pattern).Count
        Copy-Image
        Wait-For { @(Get-ChildItem $testFolder -Filter $pattern).Count -gt $previousCount } 'a new capture after Clear'
        Wait-For { $null -ne (Find-Control 'PreviewImage') -and $null -ne (Find-Name (Find-Control 'HistoryList') 'Saved') } 'new history and preview after Clear'
        Stop-Monitoring
        if ($format[0] -eq 'JPEG') {
            if (-not (Find-Control 'JpegQuality').Current.IsEnabled) { throw 'Quality did not unlock after Stop.' }
            Set-Quality 100
        }
        Clear-History
    }
    $count = @(Get-ChildItem $testFolder -File).Count
    Copy-Image
    Start-Sleep -Seconds 1
    if (@(Get-ChildItem $testFolder -File).Count -ne $count) { throw 'An image was saved after Stop.' }

    $blocked = Join-Path $testFolder 'blocked'
    Set-Folder (Join-Path $blocked 'images')
    Start-Monitoring
    # Become unavailable after Start: accepted captures must not be redirected by preferences.
    Remove-Item $blocked -Recurse -Force
    Set-Content $blocked 'This file intentionally blocks a save directory.'
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
    Clear-History
    Clear-History
    Copy-Image
    Wait-For { $null -ne (Find-Name (Find-Control 'HistoryList') 'Failed') } 'a new failure after Clear'
    Stop-Monitoring
    Clear-History

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
    Write-Output '::notice::Feature smoke passed: PNG/JPEG/BMP pixels, JPEG quality visibility/default/range/edit/freeze, repeated Clear while monitoring and stopped, fresh captures/failures after Clear, file hashes and clipboard retention, preview/history, failure details, maximizing and narrow layout reflow.'
    Close-App
} finally {
    if ($null -ne $process -and -not $process.HasExited) { Stop-Process -Id $process.Id }
    if (Test-Path $config) { Remove-Item $config -Force }
    if ($null -ne $originalConfig) { [IO.File]::WriteAllBytes($config, $originalConfig) }
    Remove-Item $testFolder -Recurse -Force
}
