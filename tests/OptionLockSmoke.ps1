# Runs inside FeatureSmoke.ps1 against the published app and its existing fixtures.
function Assert-DisabledInput($Action) {
    try { & $Action }
    catch {
        $exception = $_.Exception
        while ($null -ne $exception) {
            if ($exception -is [System.Windows.Automation.ElementNotEnabledException]) { return }
            $exception = $exception.InnerException
        }
        throw
    }
}

function Assert-ImageOptionsLocked([string]$Phase) {
    $picker = Find-Control 'FormatPicker'
    $slider = Find-Control 'JpegQuality'
    if ($picker.Current.IsEnabled -or $slider.Current.IsEnabled) {
        throw "Image options are editable during $Phase."
    }
    $selection = [System.Windows.Automation.SelectionPattern]$picker.GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)
    $formatBefore = $selection.Current.GetSelection()[0].Current.Name
    $expand = [System.Windows.Automation.ExpandCollapsePattern]$picker.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $quality = [System.Windows.Automation.RangeValuePattern]$slider.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    $qualityBefore = $quality.Current.Value
    Assert-DisabledInput { $expand.Expand() }
    Assert-DisabledInput { $quality.SetValue(99) }
    if ($expand.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded -or
        $selection.Current.GetSelection()[0].Current.Name -ne $formatBefore -or
        $quality.Current.Value -ne $qualityBefore) {
        throw "Disabled input changed image options during $Phase."
    }
}

Commit-Folder $testFolder
Select-Format 'JPEG'
Set-Quality 37
Clear-History
$jpgBefore = @(Get-ChildItem $testFolder -Filter '*.jpg').Count

# Temporarily prevent INI replacement so the real asynchronous Start remains observable.
# Existing bounded replacement retries keep this fixture from hanging the app.
$configRead = [IO.FileStream]::new($config, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $start = Find-Control 'StartButton'
    $stop = Find-Control 'StopButton'
    $picker = Find-Control 'FormatPicker'
    $slider = Find-Control 'JpegQuality'
    Invoke-Control $start
    Wait-For {
        -not $start.Current.IsEnabled -and -not $stop.Current.IsEnabled -and
        -not $picker.Current.IsEnabled -and -not $slider.Current.IsEnabled
    } 'format/quality locked before asynchronous Start completes'
} finally { $configRead.Dispose() }
Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq 'Monitoring' } 'monitoring after the Start transition'
Assert-ImageOptionsLocked 'monitoring'

[DelayedClipboard]::Start()
try {
    Wait-For { (Find-Control 'QueueActivity').Current.Name -eq 'Reading: 1 · Waiting: 0 · Saving: 0' } 'accepted JPEG image awaiting the clipboard provider'
    Invoke-Id 'StopButton'
    Wait-For { (Find-Control 'QueueStatus').Current.Name -eq 'Stopping monitoring' } 'pending native Stop'
    Assert-ImageOptionsLocked 'pending Stop'
    [DelayedClipboard]::Complete()
    Wait-For {
        (Find-Control 'MonitoringStatus').Current.Name -eq 'Stopped' -and
        (Find-Control 'FormatPicker').Current.IsEnabled -and (Find-Control 'JpegQuality').Current.IsEnabled
    } 'format and quality editable after Stop completes'
    Set-Quality 100
    Select-Format 'BMP'
    Wait-For {
        @(Get-ChildItem $testFolder -Filter '*.jpg').Count -gt $jpgBefore -and
        $null -ne (Find-Name (Find-Control 'QueueList') 'JPEG (37)') -and
        (Find-Control 'QueueActivity').Current.Name -eq 'Reading: 0 · Waiting: 0 · Saving: 0'
    } 'accepted image saved with its original JPEG/37 metadata after Stop'
    $saved = Get-ChildItem $testFolder -Filter '*.jpg' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $image = [System.Drawing.Bitmap]::new($saved.FullName)
    try {
        if ($image.RawFormat.Guid -ne [System.Drawing.Imaging.ImageFormat]::Jpeg.Guid) {
            throw 'The accepted JPEG image was encoded using the later format selection.'
        }
    } finally { $image.Dispose() }
} finally { [DelayedClipboard]::Complete(); [DelayedClipboard]::Stop() }

$bmpBefore = @(Get-ChildItem $testFolder -Filter '*.bmp').Count
Start-Monitoring
if ((Find-Control 'FormatPicker').Current.IsEnabled) { throw 'BMP format is editable during the next run.' }
Copy-Image
Wait-For { @(Get-ChildItem $testFolder -Filter '*.bmp').Count -gt $bmpBefore } 'next run uses the post-Stop BMP selection'
Stop-Monitoring
if (-not (Find-Control 'FormatPicker').Current.IsEnabled) { throw 'Format did not unlock after the next Stop.' }
Select-Format 'JPEG'
Set-Quality 90
Select-Format 'PNG'
Clear-History
Write-Output '::notice::Image-option locking passed: Start transition, rejected format/quality automation input while monitoring and pending Stop, restored editing, retained JPEG/37 capture and next-run BMP selection.'
