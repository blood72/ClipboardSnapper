# Runs inside FeatureSmoke.ps1 against the published app and its existing fixtures.
function Format-Value {
    return ([System.Windows.Automation.SelectionPattern](Find-Control 'FormatPicker').GetCurrentPattern(
        [System.Windows.Automation.SelectionPattern]::Pattern)).Current.GetSelection()[0].Current.Name
}
function Quality-Value {
    return ([System.Windows.Automation.RangeValuePattern](Find-Control 'JpegQuality').GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)).Current.Value
}
function Wait-ImagePreference([string]$Format, [int]$Quality) {
    Wait-For {
        $text = Read-Config
        $text -match "(?m)^ImageFormat=$Format\r?$" -and $text -match "(?m)^JpegQuality=$Quality\r?$"
    } "persisted $Format/$Quality without Start or captures"
}

Close-App
$preserved = Read-Config
$marker = "; image preference preservation`r`n[FutureOption]`r`nKeep=one=two;#three`r`n"
[IO.File]::WriteAllText($config, $preserved + $marker)
$beforeStartup = Read-Config
Start-App
if ((Read-Config) -ne $beforeStartup) { throw 'Startup applied image settings through save handlers.' }
$imagesBefore = @(Get-ChildItem $testFolder -Recurse -File).Count

# Every supported pair survives a real process restart, without Start or captures.
foreach ($quality in @(1, 90, 100)) {
    foreach ($format in @('JPEG', 'PNG', 'BMP')) {
        Select-Format 'JPEG'
        Set-Quality $quality
        Select-Format $format
        Wait-ImagePreference $format $quality
        if ($format -ne 'JPEG' -and $null -ne (Find-Control 'JpegQuality')) { throw 'Quality is visible for a non-JPEG format.' }
        Close-App
        Start-App
        if ((Format-Value) -ne $format) { throw "Format $format did not survive restart." }
        Select-Format 'JPEG'
        if ((Quality-Value) -ne $quality) { throw "JPEG quality $quality was lost by restart or format switching." }
    }
}
if (@(Get-ChildItem $testFolder -Recurse -File).Count -ne $imagesBefore) { throw 'Image preference persistence required a capture or modified stored files.' }
foreach ($entry in @('SaveFolder=', 'Formula=', 'SelectedPreset=', 'Preset.')) {
    $originalLines = @($preserved -split '\r?\n' | Where-Object { $_.StartsWith($entry) })
    foreach ($line in $originalLines) {
        if (-not (Read-Config).Contains($line)) { throw "Image writes replaced unrelated $entry data." }
    }
}
if (-not (Read-Config).Contains($marker)) { throw 'Image writes removed unknown entries/comments.' }

# Fast edits must not let older save callbacks replace the latest pair.
foreach ($value in @(15, 28, 49, 72, 84)) { Set-Quality $value }
Select-Format 'BMP'
Wait-ImagePreference 'BMP' 84
Close-App
Start-App
if ((Format-Value) -ne 'BMP') { throw 'A stale format save won after restart.' }
Select-Format 'JPEG'
if ((Quality-Value) -ne 84) { throw 'A stale quality save won after restart.' }

# Start and Close must flush the last slider edit, even before its timer fires.
Set-Quality 63
Start-Monitoring
Wait-ImagePreference 'JPEG' 63
if ((Find-Control 'JpegQuality').Current.IsEnabled) { throw 'Persisting quality broke the monitoring lock.' }
Stop-Monitoring
Set-Quality 41
Close-App
Start-App
if ((Format-Value) -ne 'JPEG' -or (Quality-Value) -ne 41) { throw 'Close lost the last quality edit.' }

# Failed writes keep the session choice, warn, and leave the durable value untouched.
$beforeFailure = Read-Config
[IO.File]::SetAttributes($config, [IO.FileAttributes]::ReadOnly)
try {
    Set-Quality 42
    Wait-For { $null -ne (Find-Name (Find-Control 'SettingsMessage') 'Image format and quality settings') } 'image settings write warning'
    if ((Quality-Value) -ne 42 -or (Read-Config) -ne $beforeFailure) { throw 'Failed image write lost the session choice or modified the file.' }
    Clear-History
    Set-Quality 43
    Clear-History
    # Allow the debounce and bounded storage retries to complete after Clear.
    Start-Sleep -Milliseconds 800
    if ($null -ne (Find-Name (Find-Control 'SettingsMessage') 'Image format and quality settings')) { throw 'A pre-Clear image preference warning revived the cleared screen.' }
    Close-App
} finally { [IO.File]::SetAttributes($config, [IO.FileAttributes]::Normal) }
Start-App
if ((Quality-Value) -ne 41) { throw 'Failed image preference write was treated as durable.' }

# External values use the same startup loader; defaults are isolated per field.
Close-App
$valid = Read-Config
foreach ($case in @(
    @('BMP', '100', 'BMP', 100, $false),
    @('GIF', '27', 'PNG', 27, $true),
    @('JPEG', '101', 'JPEG', 90, $true),
    @($null, $null, 'PNG', 90, $false)
)) {
    $edited = [regex]::Replace($valid, '(?m)^ImageFormat=[^\r\n]*\r?\n', '')
    $edited = [regex]::Replace($edited, '(?m)^JpegQuality=[^\r\n]*\r?\n', '')
    if ($null -ne $case[0]) {
        $edited = $edited.Replace('[Storage]', "[Storage]`r`nImageFormat=$($case[0])`r`nJpegQuality=$($case[1])")
    }
    [IO.File]::WriteAllText($config, $edited)
    Start-App
    if ((Format-Value) -ne $case[2] -or (Read-Config) -ne $edited) { throw 'Startup format fallback/restoration rewrote configuration or chose the wrong format.' }
    if ($case[4] -and $null -eq (Find-Name (Find-Control 'SettingsMessage') 'Image format and quality settings')) { throw 'Invalid saved image options were not explained.' }
    # Check fallback quality before this selection can save the corrected format.
    Select-Format 'JPEG'
    if ((Quality-Value) -ne $case[3]) { throw 'Startup quality fallback/restoration is wrong.' }
    Close-App
}
[IO.File]::WriteAllText($config, $valid)
Start-App
Select-Format 'JPEG'
Set-Quality 90
Select-Format 'PNG'
Wait-ImagePreference 'PNG' 90
Clear-History
Write-Output '::notice::Image preference UI passed: all format/quality-limit restart pairs without captures, PNG/BMP retention, rapid edits, Start/Close flush, preserved unrelated data, read-only warning/session-only choice, generation-safe Clear and external/missing/invalid startup values without automatic rewrite.'
