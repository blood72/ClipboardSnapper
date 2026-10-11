# Runs inside FeatureSmoke.ps1 against the real published app.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class InstanceFixture {
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
}
'@
$instanceLaunches = [Collections.Generic.List[Diagnostics.Process]]::new()
function Launch-Again {
    $launch = Start-Process (Join-Path $publish 'ClipboardSnapper.exe') -WorkingDirectory $testFolder -PassThru
    $instanceLaunches.Add($launch)
    return $launch
}
function Assert-Forwarded($Launch, [int]$OwnerId, [IntPtr]$OwnerWindow) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not $Launch.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        $Launch.Refresh()
        if (-not $Launch.HasExited -and $Launch.MainWindowHandle -ne [IntPtr]::Zero) {
            throw "Repeated launch created its own window: PID $($Launch.Id)."
        }
        Start-Sleep -Milliseconds 25
    }
    if (-not $Launch.WaitForExit(1000)) { throw 'The activation forwarding process did not exit.' }
    if ($Launch.ExitCode -ne 0) { throw "Activation forwarding exited with $($Launch.ExitCode)." }
    $process.Refresh()
    if ($process.HasExited -or $process.Id -ne $OwnerId -or $process.MainWindowHandle -ne $OwnerWindow) {
        throw 'Repeated launch replaced the existing application owner/window.'
    }
    $owners = @(Get-Process -Name ClipboardSnapper -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq (Join-Path $publish 'ClipboardSnapper.exe') -and $_.MainWindowHandle -ne [IntPtr]::Zero })
    if ($owners.Count -ne 1 -or $owners[0].Id -ne $OwnerId) { throw 'More than one application owner remained.' }
}
function Repeat-Launch {
    Assert-Forwarded (Launch-Again) $process.Id $process.MainWindowHandle
}

$scopeFolder = Join-Path $env:RUNNER_TEMP ('ClipboardSnapper-instance-copy-' + [Guid]::NewGuid().ToString('N'))
$copyProcess = $null
try {
    Commit-Folder $testFolder
    Select-Format 'PNG'
    Clear-History
    $ownerId = $process.Id
    $ownerWindow = $process.MainWindowHandle
    $baselineConfig = Read-Config
    # An externally edited preference must not be loaded or rewritten by repeated launches.
    $externalConfig = $baselineConfig.Replace('ImageFormat=PNG', 'ImageFormat=BMP') + "`r`n[SingleInstanceTest]`r`nMarker=keep`r`n"
    [IO.File]::WriteAllText($config, $externalConfig)
    Repeat-Launch
    if ((Read-Config) -ne $externalConfig -or (Format-Value) -ne 'PNG' -or
        (Find-Control 'MonitoringStatus').Current.Name -ne 'Ready') {
        throw 'Repeated launch loaded, changed, or reinitialized the existing preferences/session.'
    }
    [IO.File]::WriteAllText($config, $baselineConfig)

    $windowPattern = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $normalBounds = [DesktopNative+Rect]::new()
    [void][DesktopNative]::GetWindowRect($ownerWindow, [ref]$normalBounds)
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    Wait-For { [DesktopNative]::IsIconic($ownerWindow) } 'minimized existing window'
    Repeat-Launch
    Wait-For { -not [DesktopNative]::IsIconic($ownerWindow) } 'existing window restored by repeated launch'
    $restoredBounds = [DesktopNative+Rect]::new()
    [void][DesktopNative]::GetWindowRect($ownerWindow, [ref]$restoredBounds)
    if ($normalBounds.Left -ne $restoredBounds.Left -or $normalBounds.Top -ne $restoredBounds.Top -or
        $normalBounds.Right -ne $restoredBounds.Right -or $normalBounds.Bottom -ne $restoredBounds.Bottom) {
        throw 'Repeated activation resized or recentered the restored window.'
    }
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Maximized)
    Wait-For { [DesktopNative]::IsZoomed($ownerWindow) } 'maximized existing window'
    Repeat-Launch
    if (-not [DesktopNative]::IsZoomed($ownerWindow)) { throw 'Repeated launch lost the maximized state.' }
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Minimized)
    Wait-For { [DesktopNative]::IsIconic($ownerWindow) } 'minimized previously maximized window'
    Repeat-Launch
    Wait-For { -not [DesktopNative]::IsIconic($ownerWindow) -and [DesktopNative]::IsZoomed($ownerWindow) } 'restore retains previous maximized state'
    $windowPattern.SetWindowVisualState([System.Windows.Automation.WindowVisualState]::Normal)

    Clear-History
    Start-Monitoring
    $beforeImages = @(Get-ChildItem $testFolder -Filter '*.png').Count
    Copy-Image
    Wait-For { (Find-Control 'QueueOutcomes').Current.Name -eq 'Saved: 1 · Failed: 0' -and $null -ne (Find-Control 'PreviewImage') } 'first monitored image and preview'
    $caption = (Find-Control 'PreviewCaption').Current.Name
    $monitorConfig = Read-Config
    $clipboardSequence = [DesktopNative]::GetClipboardSequenceNumber()
    $burst = @(1..3 | ForEach-Object { Launch-Again })
    foreach ($launch in $burst) { Assert-Forwarded $launch $ownerId $ownerWindow }
    if ((Read-Config) -ne $monitorConfig -or [DesktopNative]::GetClipboardSequenceNumber() -ne $clipboardSequence -or
        (Find-Control 'MonitoringStatus').Current.Name -ne 'Monitoring' -or
        (Find-Control 'QueueOutcomes').Current.Name -ne 'Saved: 1 · Failed: 0' -or
        (Find-Control 'PreviewCaption').Current.Name -ne $caption -or $null -eq (Find-Control 'PreviewImage') -or
        $null -eq (Find-Name (Find-Control 'HistoryList') 'Saved')) {
        throw 'Repeated launches reset monitoring/history/preview/counters/settings or touched the clipboard.'
    }
    Copy-Image
    Wait-For { (Find-Control 'QueueOutcomes').Current.Name -eq 'Saved: 2 · Failed: 0' } 'monitoring continues after repeated launches'
    if (@(Get-ChildItem $testFolder -Filter '*.png').Count -ne $beforeImages + 2) { throw 'Duplicate instances saved the same image independently.' }

    [DelayedClipboard]::Start()
    try {
        Wait-For { (Find-Control 'QueueActivity').Current.Name.Contains('Reading: 1') } 'accepted delayed image during monitoring'
        Repeat-Launch
        if ((Find-Control 'MonitoringStatus').Current.Name -ne 'Monitoring' -or
            -not (Find-Control 'QueueActivity').Current.Name.Contains('Reading: 1')) { throw 'Forwarding interrupted the accepted read.' }
        [DelayedClipboard]::Complete()
        Wait-For { (Find-Control 'QueueOutcomes').Current.Name -eq 'Saved: 3 · Failed: 0' } 'accepted image saved after activation'
    } finally { [DelayedClipboard]::Complete(); [DelayedClipboard]::Stop() }

    [DelayedClipboard]::Start()
    try {
        Wait-For { (Find-Control 'QueueActivity').Current.Name.Contains('Reading: 1') } 'accepted read before closing'
        ([System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
        Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq 'Finishing saves' } 'existing shutdown drains accepted work'
        Repeat-Launch
        if ((Find-Control 'MonitoringStatus').Current.Name -ne 'Finishing saves') { throw 'Repeated launch cancelled shutdown.' }
        [DelayedClipboard]::Complete()
        if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Owner did not finish shutdown normally.' }
        $script:root = $null
        if (@(Get-ChildItem $testFolder -Filter '*.png').Count -ne $beforeImages + 4) { throw 'Shutdown lost or duplicated accepted work.' }
    } finally { [DelayedClipboard]::Complete(); [DelayedClipboard]::Stop() }
    Start-App

    # Startup races: none of the launches begins with an existing application owner.
    Close-App
    $racing = @(1..4 | ForEach-Object { Launch-Again })
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $winner = $null
    do {
        foreach ($candidate in $racing) {
            $candidate.Refresh()
            if (-not $candidate.HasExited -and $candidate.MainWindowHandle -ne [IntPtr]::Zero) {
                if ($null -ne $winner -and $candidate.Id -ne $winner.Id) { throw 'Simultaneous launch created multiple windows.' }
                $winner = $candidate
            }
        }
        if ($null -ne $winner) { break }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $winner) { throw 'Simultaneous launch did not produce an owner window.' }
    $script:process = $winner
    $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($winner.MainWindowHandle)
    foreach ($launch in $racing) {
        if ($launch.Id -ne $winner.Id) { Assert-Forwarded $launch $winner.Id $winner.MainWindowHandle }
    }
    Wait-For { (Find-Control 'FormatPicker').Current.IsEnabled -and (Find-Control 'MonitoringStatus').Current.Name -eq 'Ready' } 'single owner ready after simultaneous startup'
    Repeat-Launch

    # The crash fixture has no accepted image jobs; only the app started by this test is terminated.
    Stop-Process -Id $process.Id
    if (-not $process.WaitForExit(15000)) { throw 'Crash fixture did not exit.' }
    $script:root = $null
    Start-App
    Repeat-Launch

    # Suspend only our idle test owner, including its activation threads, to exercise a live unreachable owner.
    $unresponsiveConfig = Read-Config
    $unresponsiveFiles = @(Get-ChildItem $testFolder -File).Count
    $ownerHandle = $process.Handle
    if ([InstanceFixture]::NtSuspendProcess($ownerHandle) -ne 0) { throw 'Could not suspend the idle test owner.' }
    try {
        $unreachable = Launch-Again
        Wait-For { $unreachable.Refresh(); $unreachable.MainWindowHandle -ne [IntPtr]::Zero } 'bounded activation failure dialog'
        $errorWindow = $unreachable.MainWindowHandle
        $class = [Text.StringBuilder]::new(256)
        [void][DesktopNative]::GetClassName($errorWindow, $class, $class.Capacity)
        if ($class.ToString() -ne '#32770') { throw 'Unreachable owner caused another application window instead of an error dialog.' }
        $errorRoot = [System.Windows.Automation.AutomationElement]::FromHandle($errorWindow)
        $errorTexts = $errorRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Text))
        if (-not (($errorTexts | ForEach-Object { $_.Current.Name }) -join ' ').Contains('ClipboardSnapper')) {
            throw 'The forwarding failure did not explain the application error.'
        }
        Click-PickerButton $errorWindow 1
        if (-not $unreachable.WaitForExit(15000) -or $unreachable.ExitCode -ne 1) { throw 'Unreachable-owner launch did not report a nonzero exit.' }
        if ((Read-Config) -ne $unresponsiveConfig -or @(Get-ChildItem $testFolder -File).Count -ne $unresponsiveFiles) {
            throw 'Failed forwarding wrote preferences or created capture files.'
        }
    } finally {
        if ([InstanceFixture]::NtResumeProcess($ownerHandle) -ne 0) { throw 'Could not resume the idle test owner.' }
    }
    Repeat-Launch

    # Observe the SDK identity boundary rather than assuming a global cross-directory mutex.
    [void](New-Item -ItemType Directory -Path $scopeFolder)
    Copy-Item (Join-Path $publish '*') $scopeFolder -Recurse
    $copyConfig = Join-Path $scopeFolder 'config.ini'
    $copyConfigBefore = [IO.File]::ReadAllText($copyConfig)
    $copyProcess = Start-Process (Join-Path $scopeFolder 'ClipboardSnapper.exe') -WorkingDirectory $testFolder -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $copyProcess.Refresh()
        if ($copyProcess.HasExited -or $copyProcess.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 25
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($copyProcess.HasExited) {
        if ($copyProcess.ExitCode -ne 0 -or [IO.File]::ReadAllText($copyConfig) -ne $copyConfigBefore) { throw 'Copied executable failed activation or wrote its own preferences.' }
        Write-Output '::notice::AppInstance identity observation: a copy in another directory redirected to the original owner.'
    } else {
        if ($copyProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Copied executable did not finish startup.' }
        $copyRoot = [System.Windows.Automation.AutomationElement]::FromHandle($copyProcess.MainWindowHandle)
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'MonitoringStatus')
        Wait-For { $null -ne $copyRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition) } 'copied app monitoring state'
        if ($copyRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition).Current.Name -ne 'Ready') { throw 'Copied app began monitoring automatically.' }
        ([System.Windows.Automation.WindowPattern]$copyRoot.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
        if (-not $copyProcess.WaitForExit(15000) -or $copyProcess.ExitCode -ne 0) { throw 'Copied application did not close normally.' }
        Write-Output '::notice::AppInstance identity observation: separate executable directories have independent owners; neither copy auto-started monitoring.'
    }
    Repeat-Launch
    Clear-History
    Write-Output '::notice::Single-instance UI passed: forwarding processes exit without sampled windows, external config remains untouched, minimized/maximized restoration, unchanged owner/history/preview/counters/clipboard, no duplicate saves, delayed acquisition and closing drain, simultaneous startup, normal/crash relaunch, bounded live-owner timeout/error without competing work, and copied-directory SDK scope.'
} finally {
    [DelayedClipboard]::Complete(); [DelayedClipboard]::Stop()
    foreach ($launch in $instanceLaunches) {
        if ($launch.Id -ne $process.Id -and -not $launch.HasExited) { Stop-Process -Id $launch.Id; [void]$launch.WaitForExit(15000) }
    }
    if ($null -ne $copyProcess -and -not $copyProcess.HasExited) { Stop-Process -Id $copyProcess.Id; [void]$copyProcess.WaitForExit(15000) }
    if (Test-Path $scopeFolder) { Remove-Item $scopeFolder -Recurse -Force }
}
