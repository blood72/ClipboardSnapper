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
    [DllImport("user32.dll")] public static extern IntPtr GetLastActivePopup(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr handle);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr handle, System.Text.StringBuilder name, int count);
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

Add-Type -Path (Join-Path $PSScriptRoot 'DelayedClipboard.cs') -ReferencedAssemblies System.Windows.Forms,System.Drawing

function Wait-For($Condition, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        try { if (& $Condition) { return } }
        catch [System.Windows.Automation.ElementNotAvailableException] { }
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
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $windows = $desktop.FindAll([System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $buttons = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $desktopDetails = (($windows | ForEach-Object { "Window: $($_.Current.Name) / $($_.Current.ClassName)" }) +
        ($buttons | Select-Object -First 220 | ForEach-Object { "Control: $($_.Current.Name) / $($_.Current.AutomationId) / $($_.Current.ControlType.ProgrammaticName) / enabled=$($_.Current.IsEnabled)" })) -join ' | '
    throw "Timed out waiting for $Description. Screen text: $screen. Desktop: $desktopDetails"
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
    if ($null -eq $Element) { throw 'The requested UI control is not ready.' }
    ([System.Windows.Automation.InvokePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Invoke-Id([string]$Id) {
    $target = @{ Element = $null }
    Wait-For {
        $target.Element = Find-Control $Id
        $null -ne $target.Element -and $target.Element.Current.IsEnabled
    } "enabled $Id control"
    Invoke-Control $target.Element
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
function Get-Profiles {
    foreach ($match in [regex]::Matches((Read-Config), '(?m)^Preset\.[^=\r\n]+=([^\r\n]+)')) {
        $match.Groups[1].Value | ConvertFrom-Json
    }
}
function Start-App {
    $script:process = Start-Process (Join-Path $publish 'ClipboardSnapper.exe') -WorkingDirectory $testFolder -PassThru
    Wait-For { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero } 'the main window'
    $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-For { $null -ne (Find-Control 'FormatPicker') -and (Find-Control 'FormatPicker').Current.IsEnabled } 'loaded folder and profile settings'
    if ((Find-Control 'MonitoringStatus').Current.Name -notin @('Ready', '준비됨')) { throw 'Initial state is not Ready.' }
    Wait-For { (Find-Control 'QueueActivity').Current.Name -in @('Reading: 0 · Waiting: 0 · Saving: 0', '읽는 중: 0 · 저장 대기: 0 · 저장 중: 0') } 'fresh queue state'
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
function Picker-Dialog {
    $dialog = [DesktopNative]::GetLastActivePopup($process.MainWindowHandle)
    $name = [Text.StringBuilder]::new(256)
    [void][DesktopNative]::GetClassName($dialog, $name, $name.Capacity)
    if ($dialog -ne $process.MainWindowHandle -and $name.ToString() -eq '#32770') { return $dialog }
    return [IntPtr]::Zero
}
function Click-PickerButton([IntPtr]$Dialog, [int]$Id) {
    $button = [DesktopNative]::GetDlgItem($Dialog, $Id)
    if ($button -eq [IntPtr]::Zero) { throw "Missing native folder-picker button $Id." }
    [void][DesktopNative]::SetForegroundWindow($Dialog)
    [void][DesktopNative]::SendMessage($button, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero)
}
function Scroll-ToTop {
    $scroll = [System.Windows.Automation.ScrollPattern](Find-Control 'MainScroll').GetCurrentPattern(
        [System.Windows.Automation.ScrollPattern]::Pattern)
    if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 0) }
    Wait-For { $null -ne (Find-Control 'StartButton') } 'monitoring controls after scrolling to the top'
}
function Select-Option([string]$Id, [string]$Name) {
    $combo = Find-Control $Id
    $expand = [System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    Wait-For { $null -ne (Find-Name $script:root $Name) } "the $Name format option"
    $choice = Find-Name $script:root $Name
    ([System.Windows.Automation.SelectionItemPattern]$choice.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    # A live language change can replace automation peers and close the popup itself.
    Wait-For { $null -ne (Find-Control $Id) -and (Find-Control $Id).Current.IsEnabled } "ready $Id after selection"
    $combo = Find-Control $Id
    $expand = [System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) { $expand.Collapse() }
    $selection = [System.Windows.Automation.SelectionPattern]$combo.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)
    Wait-For { $selection.Current.GetSelection()[0].Current.Name -eq $Name } "$Name selection"
}
function Select-Format([string]$Name) { Select-Option 'FormatPicker' $Name }
function Set-Text([string]$Id, [string]$Value) {
    Wait-For { $null -ne (Find-Control $Id) -and (Find-Control $Id).Current.IsEnabled } "editable $Id control"
    ([System.Windows.Automation.ValuePattern](Find-Control $Id).GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Value)
}
function Text-Value([string]$Id) {
    return ([System.Windows.Automation.ValuePattern](Find-Control $Id).GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
}
function Expand-Naming([bool]$Expanded) {
    Scroll-ToTop
    $pattern = [System.Windows.Automation.ExpandCollapsePattern](Find-Control 'NamingExpander').GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($Expanded) { $pattern.Expand(); Wait-For { $null -ne (Find-Control 'RuleFormula') } 'filename controls' }
    else { $pattern.Collapse() }
}
function Confirm-Preset([string]$Action) {
    Wait-For { $null -ne (Find-Control 'PresetConfirmation') } 'preset confirmation'
    Invoke-Control (Find-Name (Find-Control 'PresetConfirmation') $Action)
    Wait-For { $null -eq (Find-Control 'PresetConfirmation') } 'closed preset confirmation'
    Wait-For { $null -ne (Find-Control 'NewProfileButton') -and (Find-Control 'NewProfileButton').Current.IsEnabled } 'finished profile change'
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
    Invoke-Id 'StartButton'
    Wait-For { (Find-Control 'MonitoringStatus').Current.Name -in @('Monitoring', '감시 중') } 'monitoring state'
}
function Stop-Monitoring {
    Scroll-ToTop
    Invoke-Id 'StopButton'
    Wait-For { (Find-Control 'MonitoringStatus').Current.Name -in @('Stopped', '중지됨') } 'stopped state'
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
    # A naming-rule example remains a setting; saved-image paths/messages must be cleared.
    if ($names.Contains('Exception:') -or $names.Contains('Image saved') -or $names.Contains('Save failed')) {
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
    Wait-For { (Find-Control 'QueueOutcomes').Current.Name -eq 'Saved: 0 · Failed: 0' -and $null -ne (Find-Control 'QueueEmpty') } 'cleared queue outcomes and rows'
    if ((Find-Control 'MonitoringStatus').Current.Name -ne $status) { throw 'Clear changed the monitoring state.' }
    if ([DesktopNative]::GetClipboardSequenceNumber() -ne $sequence) { throw 'Clear modified the Windows clipboard.' }
    $after = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    if ($before -ne $after) { throw 'Clear modified saved files.' }
}

function Select-Language([string]$Name, [string]$StartName) {
    Scroll-ToTop
    Select-Option 'LanguagePicker' $Name
    Wait-For { (Find-Control 'LanguagePicker').Current.IsEnabled -and (Find-Control 'StartButton').Current.Name -eq $StartName } "live language $Name"
}
function Assert-KoreanEmpty {
    Wait-For {
        $null -eq (Find-Control 'PreviewImage') -and
        $null -ne (Find-Name $root '아직 저장한 이미지가 없습니다') -and
        (Find-Control 'EmptyHistory').Current.Name -eq '현재 세션에서 저장한 이미지와 실패 내역이 여기에 표시됩니다.' -and
        (Find-Control 'PreviewCaption').Current.Name -eq '이미지를 저장하면 미리보기가 갱신됩니다.'
    } 'Korean empty guidance and cleared preview'
}
function Clear-KoreanHistory {
    $state = (Find-Control 'MonitoringStatus').Current.Name
    $sequence = [DesktopNative]::GetClipboardSequenceNumber()
    $before = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    if (-not (Find-Control 'ClearHistoryButton').Current.HelpText.Contains('저장 파일은 유지됩니다')) { throw 'Korean retention help is missing.' }
    Invoke-Id 'ClearHistoryButton'
    Assert-KoreanEmpty
    Wait-For { (Find-Control 'QueueOutcomes').Current.Name -eq '저장 성공: 0 · 실패: 0' -and $null -ne (Find-Control 'QueueEmpty') } 'cleared Korean queue outcomes and rows'
    $after = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    if ($before -ne $after -or $sequence -ne [DesktopNative]::GetClipboardSequenceNumber() -or (Find-Control 'MonitoringStatus').Current.Name -ne $state) { throw 'Korean Clear altered files, clipboard or monitoring.' }
}

$publish = (Resolve-Path $PublishPath).Path
$config = Join-Path $publish 'config.ini'
$originalConfig = if (Test-Path $config -PathType Leaf) { [IO.File]::ReadAllBytes($config) } else { $null }
if (Test-Path $config) { Remove-Item $config -Force }
$testFolder = Join-Path $env:RUNNER_TEMP ('ClipboardSnapper-smoke-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testFolder)
$process = $null
$customLanguage = Join-Path $publish 'lang/ja.json'
$invalidLanguage = Join-Path $publish 'lang/de.json'
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
    Wait-For { (Picker-Dialog) -ne [IntPtr]::Zero } 'the native folder picker'
    $picker = Picker-Dialog
    [void][DesktopNative]::SetForegroundWindow($picker)
    [System.Windows.Forms.SendKeys]::SendWait('%d')
    [System.Windows.Forms.SendKeys]::SendWait($browseFolder)
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Wait-For { [DesktopNative]::IsWindowEnabled([DesktopNative]::GetDlgItem($picker, 1)) } 'enabled folder selection'
    Click-PickerButton $picker 1
    Wait-For { (Folder-Value) -eq $browseFolder } 'Browse selection'
    Wait-For { (Read-Config).Contains("SaveFolder=$browseFolder") } 'immediate Browse persistence'
    Close-App
    Start-App
    if ((Folder-Value) -ne $browseFolder) { throw 'Browse selection did not survive restart.' }
    $beforeCancel = Read-Config
    Invoke-Control (Find-Name $root 'Browse')
    Wait-For { (Picker-Dialog) -ne [IntPtr]::Zero } 'folder picker cancellation'
    Click-PickerButton (Picker-Dialog) 2
    Wait-For { (Picker-Dialog) -eq [IntPtr]::Zero } 'the app after cancelling Browse'
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

    Expand-Naming $true
    $defaultRule = 'Clipboard_$YYYY$MM$DD_$hh$mm$ss_$fff'
    if ((Text-Value 'RuleFormula') -ne $defaultRule) { throw 'Unexpected default filename formula.' }
    $initialId = (Get-Profiles | Where-Object Name -eq 'Default').Id
    Set-Text 'RuleFormula' 'Preset_${start=10;padding=4;increment=2}'
    if ((Find-Control 'RulePreview').Current.Name -ne 'Example: Preset_0010.png') { throw 'Counter preview is wrong.' }
    Set-Text 'PresetName' 'Capture series'
    Invoke-Id 'SavePresetButton'
    Wait-For { (Get-Profiles | Where-Object Name -eq 'Capture series').Formula -eq 'Preset_${start=10;padding=4;increment=2}' -and (Find-Control 'NewProfileButton').Current.IsEnabled } 'ordinary default updated and renamed without a prompt'
    if (@(Get-Profiles).Count -ne 1 -or @(Get-Profiles)[0].Id -ne $initialId -or $null -ne (Find-Control 'PresetConfirmation')) { throw 'Save renamed by creating another profile or prompting.' }

    # Duplicate uses the selected saved formula, with an independent identity.
    Set-Text 'RuleFormula' 'Unsaved_${start=999}'
    Invoke-Id 'DuplicateProfileButton'
    Wait-For { @(Get-Profiles).Count -eq 2 -and (Find-Control 'NewProfileButton').Current.IsEnabled } 'duplicated profile'
    if ((Text-Value 'RuleFormula') -ne 'Preset_${start=10;padding=4;increment=2}') { throw 'Duplicate did not copy the saved source formula.' }
    $copyId = (Get-Profiles | Where-Object Name -eq 'Capture series copy').Id
    if ($copyId -eq $initialId) { throw 'Duplicate reused the source ID.' }
    Set-Text 'RuleFormula' 'Copy_${start=1}'
    Set-Text 'PresetName' 'My copy'
    Invoke-Id 'SavePresetButton'
    Wait-For { (Get-Profiles | Where-Object Name -eq 'My copy').Formula -eq 'Copy_${start=1}' -and (Find-Control 'NewProfileButton').Current.IsEnabled } 'independent duplicate update'
    if ((Get-Profiles | Where-Object Id -eq $initialId).Formula -ne 'Preset_${start=10;padding=4;increment=2}') { throw 'Editing a copy changed its source.' }
    Invoke-Id 'DeletePresetButton'
    Confirm-Preset 'Delete'

    # New always starts from the default, even when the selected rule is custom.
    Invoke-Id 'NewProfileButton'
    Wait-For { @(Get-Profiles).Count -eq 2 -and (Find-Control 'NewProfileButton').Current.IsEnabled } 'new profile'
    if ((Text-Value 'RuleFormula') -ne $defaultRule) { throw 'New copied the selected custom formula.' }
    Set-Text 'RuleFormula' 'Date_$YYYY$MM$DD'
    Set-Text 'PresetName' 'By date'
    Invoke-Id 'SavePresetButton'
    Wait-For { (Get-Profiles | Where-Object Name -eq 'By date').Formula -eq 'Date_$YYYY$MM$DD' -and (Find-Control 'NewProfileButton').Current.IsEnabled } 'new profile renamed and updated'
    $dateId = (Get-Profiles | Where-Object Name -eq 'By date').Id

    # A conflicting name is an error, never an overwrite or a creation/update prompt.
    Set-Text 'PresetName' 'CAPTURE SERIES'
    Invoke-Id 'SavePresetButton'
    Wait-For { $null -ne (Find-Name $root 'Could not change profile') -and (Find-Control 'NewProfileButton').Current.IsEnabled } 'conflicting name rejection'
    if (@(Get-Profiles).Count -ne 2 -or (Get-Profiles | Where-Object Id -eq $dateId).Name -ne 'By date' -or $null -ne (Find-Control 'PresetConfirmation')) { throw 'A conflicting name overwrote another profile or prompted.' }
    Select-Option 'PresetPicker' 'Capture series'
    Wait-For { (Text-Value 'RuleFormula') -eq 'Preset_${start=10;padding=4;increment=2}' } 'reused profile formula'
    Set-Text 'RuleFormula' 'Preset_${start=20;padding=4;increment=2}'
    Invoke-Id 'SavePresetButton'
    Wait-For { (Get-Profiles | Where-Object Id -eq $initialId).Formula -eq 'Preset_${start=20;padding=4;increment=2}' -and (Find-Control 'NewProfileButton').Current.IsEnabled } 'same-name update without confirmation'
    if (@(Get-Profiles).Count -ne 2 -or $null -ne (Find-Control 'PresetConfirmation')) { throw 'Save created another profile or prompted.' }
    Close-App
    Start-App
    Expand-Naming $true
    if ((Text-Value 'RuleFormula') -ne 'Preset_${start=20;padding=4;increment=2}' -or (Text-Value 'PresetName') -ne 'Capture series') { throw 'Saved profile and active selection did not survive restart.' }
    Commit-Folder $testFolder
    Start-Monitoring
    foreach ($id in @('RuleFormula', 'PresetPicker', 'PresetName', 'NewProfileButton', 'DuplicateProfileButton', 'SavePresetButton', 'DeletePresetButton')) {
        if ((Find-Control $id).Current.IsEnabled) { throw "$id is editable while monitoring." }
    }
    Expand-Naming $false
    Copy-Image
    Wait-For { Test-Path (Join-Path $testFolder 'Preset_0020.png') } 'profile filename'
    Wait-For { $null -ne (Find-Control 'PreviewImage') } 'profile image preview'
    Clear-History
    Copy-Image
    Wait-For { Test-Path (Join-Path $testFolder 'Preset_0022.png') } 'numbering continues after Clear'
    Stop-Monitoring
    Start-Monitoring
    Copy-Image
    Wait-For { Test-Path (Join-Path $testFolder 'Preset_0020 (2).png') } 'restart counter with safe collision suffix'
    Stop-Monitoring
    Clear-History
    Expand-Naming $true
    Set-Text 'RuleFormula' '../escape'
    Wait-For { -not (Find-Control 'StartButton').Current.IsEnabled -and (Find-Control 'RulePreview').Current.Name.StartsWith('Invalid formula:') } 'invalid filename validation'
    Set-Text 'RuleFormula' 'Preset_${start=20;padding=4;increment=2}'
    $beforeDelete = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    Invoke-Id 'DeletePresetButton'
    Confirm-Preset 'Cancel'
    if (-not (Read-Config).Contains('Capture series')) { throw 'Cancelled deletion removed a profile.' }
    Invoke-Id 'DeletePresetButton'
    Confirm-Preset 'Delete'
    if ((Text-Value 'RuleFormula') -ne 'Date_$YYYY$MM$DD' -or (Read-Config).Contains('Capture series')) { throw 'Deletion did not select a remaining profile.' }
    $afterDelete = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    if ($beforeDelete -ne $afterDelete) { throw 'Deleting a profile changed saved images.' }
    Close-App
    Start-App
    Expand-Naming $true
    if ((Text-Value 'PresetName') -ne 'By date') { throw 'Deleted profile returned on restart.' }
    Invoke-Id 'DeletePresetButton'
    Confirm-Preset 'Delete'
    if (@(Get-Profiles).Count -ne 0 -or (Text-Value 'RuleFormula') -ne '' -or (Find-Control 'StartButton').Current.IsEnabled) { throw 'Deleting the last profile did not leave an empty state.' }
    foreach ($id in @('RuleFormula', 'PresetName', 'DuplicateProfileButton', 'SavePresetButton', 'DeletePresetButton')) {
        if ((Find-Control $id).Current.IsEnabled) { throw "$id remains enabled with no profile." }
    }
    Close-App
    Start-App
    Expand-Naming $true
    if (@(Get-Profiles).Count -ne 0 -or -not (Find-Control 'RulePreview').Current.Name.StartsWith('No profiles.') -or (Find-Control 'StartButton').Current.IsEnabled) { throw 'An empty collection was silently recreated on restart.' }
    Invoke-Id 'NewProfileButton'
    Wait-For { @(Get-Profiles).Count -eq 1 -and (Find-Control 'StartButton').Current.IsEnabled } 'new profile after an empty collection'
    if ((Text-Value 'RuleFormula') -ne $defaultRule) { throw 'New did not recover the default rule.' }
    if (-not (Read-Config).Contains($unrelated)) { throw 'Profile CRUD lost unrelated INI settings.' }
    $beforePresetWriteFailure = Read-Config
    [IO.File]::SetAttributes($config, [IO.FileAttributes]::ReadOnly)
    Set-Text 'RuleFormula' 'Session_${start=1}'
    Set-Text 'PresetName' 'Session only'
    Invoke-Id 'SavePresetButton'
    Wait-For { (Find-Control 'NewProfileButton').Current.IsEnabled -and $null -ne (Find-Name (Find-Control 'SettingsMessage') 'Filename preset settings') } 'profile persistence warning'
    if ((Read-Config) -ne $beforePresetWriteFailure) { throw 'Saving a profile changed a read-only configuration.' }
    Close-App
    [IO.File]::SetAttributes($config, [IO.FileAttributes]::Normal)
    Start-App
    Expand-Naming $true
    if ((Text-Value 'RuleFormula') -ne $defaultRule -or (Read-Config).Contains('Session only')) { throw 'Failed profile write was treated as durable after restart.' }
    Expand-Naming $false
    Write-Output '::notice::Profile UI verified: New default / Duplicate saved source, stable-ID save and rename without prompts, conflicting-name rejection, ordinary default editing/deletion, delete cancellation, empty collection/restart/recovery, read-only session changes, counters across Clear/Start, collision suffixes and existing-image/config preservation.'

    # Hold real clipboard acquisition while Stop and repeated Clear run on the published app.
    Commit-Folder $testFolder
    Select-Format 'PNG'
    Clear-History
    Start-Monitoring
    Wait-For { (Find-Control 'QueueStatus').Current.Name -eq 'Waiting for new images' } 'listening queue state'
    $beforeDelayed = @(Get-ChildItem $testFolder -Filter '*.png').Count
    [DelayedClipboard]::Start()
    try {
        Wait-For { (Find-Control 'QueueActivity').Current.Name -eq 'Reading: 1 · Waiting: 0 · Saving: 0' -and $null -ne (Find-Control 'QueueBusy') -and $null -ne (Find-Name (Find-Control 'QueueList') 'Reading') } 'real delayed clipboard read, job row and busy indicator'
        Invoke-Id 'StopButton'
        Wait-For { (Find-Control 'QueueStatus').Current.Name -eq 'Stopping monitoring' } 'Stop requested while the native clipboard provider is blocked'
        Clear-History
        Clear-History
        if ((Find-Control 'QueueActivity').Current.Name -ne 'Reading: 1 · Waiting: 0 · Saving: 0') { throw 'Clear changed the active queue work.' }
        [DelayedClipboard]::Complete()
        Wait-For { @(Get-ChildItem $testFolder -Filter '*.png').Count -gt $beforeDelayed -and (Find-Control 'QueueStatus').Current.Name -eq 'All accepted work finished' -and (Find-Control 'MonitoringStatus').Current.Name -eq 'Stopped' } 'pre-clear image saved and post-Stop draining finished'
        if ((Find-Control 'QueueOutcomes').Current.Name -ne 'Saved: 0 · Failed: 0' -or $null -ne (Find-Control 'QueueBusy')) { throw 'Late completion restored counters or the busy indicator.' }
        Assert-EmptyHistory
    } finally { [DelayedClipboard]::Complete(); [DelayedClipboard]::Stop() }
    Start-Monitoring
    $beforeFresh = @(Get-ChildItem $testFolder -Filter '*.png').Count
    Copy-Image
    # SetImage/clipboard flush may produce multiple distinct Windows change notifications.
    # Compare actual files, rows and totals rather than assuming one notification per API call.
    Wait-For {
        $totals = (Find-Control 'QueueOutcomes').Current.Name
        $savedRows = (Find-Control 'QueueList').FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Saved')).Count
        $newFiles = @(Get-ChildItem $testFolder -Filter '*.png').Count - $beforeFresh
        $newFiles -gt 0 -and $totals -eq "Saved: $newFiles · Failed: 0" -and $savedRows -eq $newFiles -and
            (Find-Control 'QueueActivity').Current.Name -eq 'Reading: 0 · Waiting: 0 · Saving: 0' -and $null -ne (Find-Control 'PreviewImage')
    } 'fresh captures with matching saved files, per-image rows and totals after clearing/draining'
    Stop-Monitoring
    $beforeText = (Find-Control 'QueueOutcomes').Current.Name
    Start-Monitoring
    [System.Windows.Forms.Clipboard]::SetText('Queue test ignores text')
    Start-Sleep -Milliseconds 500
    if ((Find-Control 'QueueOutcomes').Current.Name -ne $beforeText) { throw 'Text clipboard content changed queue outcome totals.' }
    Stop-Monitoring
    Clear-History
    Close-App
    Start-App
    Write-Output '::notice::Queue UI passed: real gated clipboard read/job row, busy indicator, pending Stop/draining, repeated Clear with pending work, old result suppression with file preservation, new capture, ignored text and restart totals.'

    # Verify real Korean UI, live state/history translation and unchanged collection/saving.
    Select-Language '한국어' '시작'
    Wait-For { (Read-Config).Contains('Language=ko') } 'persisted Korean choice'
    Assert-KoreanEmpty
    if ((Find-Control 'QueueStatus').Current.Name -ne '남은 작업 없음' -or (Find-Control 'QueueActivity').Current.Name -ne '읽는 중: 0 · 저장 대기: 0 · 저장 중: 0') { throw 'Korean queue controls are not localized.' }
    if ((Find-Control 'MonitoringStatus').Current.Name -ne '준비됨' -or (Find-Control 'StopButton').Current.Name -ne '중지' -or (Find-Control 'FolderPath').Current.Name -ne '저장 폴더') { throw 'Korean ready controls/accessibility are incomplete.' }
    Expand-Naming $true
    if ((Find-Control 'SavePresetButton').Current.Name -ne '프리셋 저장') { throw 'Profile controls were not localized.' }
    Set-Text 'RuleFormula' '$unknown'
    Wait-For { (Find-Control 'RulePreview').Current.Name.StartsWith('잘못된 규칙: 알 수 없는 변수입니다.') -and -not (Find-Control 'StartButton').Current.IsEnabled } 'Korean formula validation'
    Set-Text 'RuleFormula' $defaultRule
    Invoke-Id 'DeletePresetButton'
    Wait-For { $null -ne (Find-Name (Find-Control 'PresetConfirmation') '프로필을 삭제할까요?') } 'Korean deletion question'
    Confirm-Preset '취소'
    Expand-Naming $false
    Commit-Folder $testFolder
    foreach ($format in @(@('PNG', 'png'), @('JPEG', 'jpg'), @('BMP', 'bmp'))) {
        Select-Format $format[0]
        if ($format[0] -eq 'JPEG') {
            if ((Find-Control 'JpegQuality').Current.Name -ne 'JPEG 품질') { throw 'Korean JPEG quality accessibility is missing.' }
            Set-Quality 42
        }
        $pattern = '*.' + $format[1]
        $before = @(Get-ChildItem $testFolder -Filter $pattern).Count
        Start-Monitoring
        Copy-Image
        Wait-For { @(Get-ChildItem $testFolder -Filter $pattern).Count -gt $before -and $null -ne (Find-Name (Find-Control 'HistoryList') '저장 성공') -and $null -ne (Find-Control 'PreviewImage') } "Korean $($format[0]) save/preview/history"
        Wait-For { $null -ne (Find-Name (Find-Control 'QueueList') '저장 성공') } 'localized per-image queue completion'
        if ($format[0] -eq 'JPEG') {
            if ((Find-Control 'JpegQuality').Current.IsEnabled -or -not (Find-Control 'LanguagePicker').Current.IsEnabled) { throw 'Language choice must stay available while save options are frozen.' }
            $filesBeforeSwitch = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
            Select-Language 'English' 'Start'
            Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq 'Monitoring' -and $null -ne (Find-Name (Find-Control 'HistoryList') 'Saved') } 'existing monitoring/history translated into English'
            Select-Language '한국어' '시작'
            Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq '감시 중' -and $null -ne (Find-Name (Find-Control 'HistoryList') '저장 성공') } 'existing monitoring/history translated into Korean'
            $filesAfterSwitch = @(Get-ChildItem $testFolder -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
            if ($filesBeforeSwitch -ne $filesAfterSwitch) { throw 'Switching language changed saved files.' }
        }
        Clear-KoreanHistory
        Clear-KoreanHistory
        $before = @(Get-ChildItem $testFolder -Filter $pattern).Count
        Copy-Image
        Wait-For { @(Get-ChildItem $testFolder -Filter $pattern).Count -gt $before -and $null -ne (Find-Name (Find-Control 'HistoryList') '저장 성공') } 'fresh Korean save after Clear'
        Stop-Monitoring
        if ($format[0] -eq 'JPEG') { Set-Quality 90 }
        Clear-KoreanHistory
    }
    Set-Folder 'relative-images'
    (Find-Control 'FormatPicker').SetFocus()
    Wait-For { $null -ne (Find-Name (Find-Control 'SettingsMessage') '저장 폴더 설정') } 'rendered localized folder warning'
    Wait-For { (Folder-Value) -eq $defaultFolder } 'Korean folder fallback'
    Commit-Folder $testFolder
    $koreanBlocked = Join-Path $testFolder 'blocked-korean'
    Set-Folder (Join-Path $koreanBlocked 'images')
    Start-Monitoring
    Remove-Item $koreanBlocked -Recurse -Force
    Set-Content $koreanBlocked 'This file intentionally blocks Korean save testing.'
    Copy-Image
    Wait-For { $null -ne (Find-Name (Find-Control 'HistoryList') '저장 실패') } 'Korean save failure'
    Invoke-Control (Find-Name (Find-Control 'HistoryList') '자세히')
    Wait-For { $null -ne (Find-Control 'FileDetails') } 'Korean failure dialog'
    $details = Find-Control 'FileDetails'
    Wait-For {
        $texts = $details.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $content = ($texts | ForEach-Object { $_.Current.Name }) -join ' '
        $content.Contains('기술적 오류 정보') -and $content.Contains('Exception:') -and $content.Contains($koreanBlocked)
    } 'Korean failure summary with original technical exception/path'
    Invoke-Control (Find-Name $details '닫기')
    Stop-Monitoring
    Clear-KoreanHistory
    Commit-Folder $testFolder
    Select-Format 'PNG'
    Close-App
    Start-App
    if ((Find-Control 'StartButton').Current.Name -ne '시작') { throw 'Korean choice did not survive restart.' }

    # A failed language write must remain session-only and preserve the config.
    $beforeLanguageFailure = Read-Config
    [IO.File]::SetAttributes($config, [IO.FileAttributes]::ReadOnly)
    Select-Language 'English' 'Start'
    Wait-For { $null -ne (Find-Name (Find-Control 'SettingsMessage') 'Language settings') } 'language persistence warning'
    if ((Read-Config) -ne $beforeLanguageFailure) { throw 'Changing language overwrote a read-only config.' }
    Close-App
    [IO.File]::SetAttributes($config, [IO.FileAttributes]::Normal)
    Start-App
    if ((Find-Control 'StartButton').Current.Name -ne '시작') { throw 'A failed language write was treated as durable.' }
    Select-Language 'English' 'Start'

    # Translation files are discovered on startup, without rebuilding the executable.
    if ($null -ne (Find-Control 'ReloadLanguagesButton')) { throw 'The language-only reload button remains visible.' }
    [IO.File]::WriteAllText($customLanguage, '{"languageName":"日本語","strings":{"Start":"開始"}}', [Text.Encoding]::UTF8)
    if ((Find-Control 'StartButton').Current.Name -ne 'Start') { throw 'Adding a file changed the running language.' }
    Close-App
    Start-App
    Select-Language '日本語' '開始'
    if ((Find-Control 'StopButton').Current.Name -ne 'Stop') { throw 'An absent custom translation key did not use English.' }
    Close-App
    Start-App
    if ((Find-Control 'StartButton').Current.Name -ne '開始') { throw 'A custom JSON language did not survive restart.' }
    [IO.File]::WriteAllText($customLanguage, '{"languageName":"日本語","strings":{"Start":"始める"}}', [Text.Encoding]::UTF8)
    if ((Find-Control 'StartButton').Current.Name -ne '開始') { throw 'Editing a file changed the running language.' }
    Close-App
    Start-App
    if ((Find-Control 'StartButton').Current.Name -ne '始める') { throw 'Startup did not load edited translation text.' }
    Remove-Item $customLanguage
    [IO.File]::WriteAllText($invalidLanguage, 'not-json', [Text.Encoding]::UTF8)
    if ((Find-Control 'StartButton').Current.Name -ne '始める') { throw 'Removing a file changed the running language.' }
    Close-App
    Start-App
    if ((Find-Control 'StartButton').Current.Name -ne 'Start' -or -not (Read-Config).Contains('Language=ja')) {
        throw 'Unavailable language fallback failed or rewrote the saved preference.'
    }
    Wait-For { $null -ne (Find-Name (Find-Control 'SettingsMessage') 'Language settings') } 'invalid JSON file explanation'
    Select-Language '한국어' '시작'
    Select-Language 'English' 'Start'
    Wait-For { (Read-Config).Contains('Language=en') } 'explicit English preference after fallback'
    Remove-Item $invalidLanguage
    Close-App
    Start-App
    if ($null -ne (Find-Control 'ReloadLanguagesButton')) { throw 'The language-only reload button reappeared.' }
    Clear-History
    Write-Output '::notice::Localization UI passed: Korean controls/accessibility/empty guidance/validation/dialogs, PNG/JPEG/BMP capture, live monitoring/history switches, frozen quality, repeated Clear/file/clipboard retention, localized failure with original diagnostics, restart/read-only language preference, startup-added/edited/removed JSON language and per-key English fallback.'

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
        $pattern = '*.' + $format[1]
        $beforeCapture = @(Get-ChildItem $testFolder -Filter $pattern).Count
        Copy-Image
        Wait-For { @(Get-ChildItem $testFolder -Filter $pattern).Count -gt $beforeCapture } "$($format[0]) image saving"
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
    Wait-For {
        $content = $details.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text))
        $text = ($content | ForEach-Object { $_.Current.Name }) -join ' '
        $text.Contains('Exception:') -and $text.Contains($blocked)
    } 'rendered failure details containing the exception and failed folder'
    Wait-For {
        $totals = (Find-Control 'QueueOutcomes').Current.Name
        $failure = [regex]::Match($totals, '^Saved: 0 · Failed: ([1-9][0-9]*)$')
        $failedRows = (Find-Control 'QueueList').FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, 'Save failed')).Count
        $failure.Success -and $failedRows -eq [int]$failure.Groups[1].Value -and
            (Find-Control 'QueueFailures').Current.Name -eq "Read failures: 0 · Rejected: 0 · Save failures: $failedRows" -and
            (Find-Control 'QueueActivity').Current.Name -eq 'Reading: 0 · Waiting: 0 · Saving: 0'
    } 'save failure rows, totals and stage breakdown agree'
    Wait-For { $null -ne (Find-Name (Find-Control 'QueueList') 'Save failed') } 'per-image save failure row'
    $texts = $details.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text))
    if (-not (($texts | ForEach-Object { $_.Current.Name }) -join ' ').Contains('Exception:')) {
        throw 'The failure details do not expose the exception reason.'
    }
    Invoke-Control (Find-Name $details 'Close')
    Wait-For { $null -eq (Find-Control 'FileDetails') } 'closed history details'
    Invoke-Control (Find-Name (Find-Control 'QueueList') 'Details')
    Wait-For { $null -ne (Find-Name (Find-Control 'FileDetails') 'Close') } 'rendered queue failure details and Close control'
    $queueDetails = Find-Control 'FileDetails'
    $queueDetailTexts = $queueDetails.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))
    $queueDiagnostic = ($queueDetailTexts | ForEach-Object { $_.Current.Name }) -join ' '
    if (-not $queueDiagnostic.Contains('Exception:') -or -not $queueDiagnostic.Contains($blocked)) { throw 'Queue details lost the original failure reason/path.' }
    Invoke-Control (Find-Name $queueDetails 'Close')
    Clear-History
    Clear-History
    Copy-Image
    Wait-For { $null -ne (Find-Name (Find-Control 'HistoryList') 'Failed') } 'a new failure after Clear'
    Stop-Monitoring
    Clear-History

    Commit-Folder $testFolder
    Select-Format 'PNG'
    Start-Monitoring
    Copy-Image
    Wait-For { $null -ne (Find-Name (Find-Control 'QueueList') 'Saved') } 'queue row for narrow-window accessibility'
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
    [void][DesktopNative]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero,
        $display.Work.Left, $display.Work.Top, [Math]::Min(380, $width), [Math]::Min(700, $height), 0x14)
    Scroll-ToTop
    Wait-For {
        $queueButton = Find-Name (Find-Control 'QueueList') 'Details'
        $null -ne $queueButton -and -not $queueButton.Current.IsOffscreen -and
            $queueButton.Current.BoundingRectangle.Width -gt 0 -and
            $queueButton.Current.BoundingRectangle.Right -le (Find-Control 'QueueList').Current.BoundingRectangle.Right
    } 'queue Details stays within the narrow list viewport'
    Write-Output '::notice::Queue outcomes/details verified: file and per-image row counts match, failure stage breakdown, original diagnostics, and accessible queue Details at narrow width.'
    Select-Language '한국어' '시작'
    Wait-For { (Find-Control 'BrowseButton').Current.BoundingRectangle.Top -gt (Find-Control 'FolderPath').Current.BoundingRectangle.Top } 'Korean narrow layout reflow'
    Invoke-Id 'ClearHistoryButton'
    Assert-KoreanEmpty
    Select-Language 'English' 'Start'
    Write-Output '::notice::Feature smoke passed: PNG/JPEG/BMP pixels, JPEG quality visibility/default/range/edit/freeze, repeated Clear while monitoring and stopped, fresh captures/failures after Clear, file hashes and clipboard retention, preview/history, failure details, maximizing and narrow layout reflow.'
    Close-App
} catch {
    $trace = $_.ScriptStackTrace -replace '\r?\n', ' | '
    $message = $_.Exception.Message -replace '\r?\n', ' | '
    Write-Output "::error::Feature failure before cleanup: $message / $trace"
    $process.Refresh()
    Write-Output "::error::Application exited=$($process.HasExited)"
    if (-not $process.HasExited -and $null -ne $root) {
        $controls = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        Write-Output (('::error::UI at failure: ') + (($controls | Select-Object -First 120 | ForEach-Object { "$($_.Current.Name) / $($_.Current.AutomationId) / enabled=$($_.Current.IsEnabled)" }) -join ' | '))
    }
    throw
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
        [void]$process.WaitForExit(15000)
    }
    [DelayedClipboard]::Stop()
    foreach ($fixture in @($customLanguage, $invalidLanguage)) { if (Test-Path $fixture) { Remove-Item $fixture -Force } }
    if (Test-Path $config) { Remove-Item $config -Force }
    if ($null -ne $originalConfig) { [IO.File]::WriteAllBytes($config, $originalConfig) }
    for ($attempt = 0; $attempt -lt 10 -and (Test-Path $testFolder); $attempt++) {
        try { Remove-Item $testFolder -Recurse -Force }
        catch {
            if ($attempt -eq 9) { Write-Output "::warning::Temporary smoke folder cleanup failed: $($_.Exception.Message)" }
            else { Start-Sleep -Milliseconds 200 }
        }
    }
}
