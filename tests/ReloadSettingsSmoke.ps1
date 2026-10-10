# Runs inside FeatureSmoke.ps1 against the published app and its real desktop fixtures.
function Reload-Settings {
    Scroll-ToTop
    Invoke-Id 'ReloadSettingsButton'
    Wait-For {
        (Find-Control 'ReloadSettingsButton').Current.IsEnabled -and
        (Find-Control 'MonitoringStatus').Current.Name -in @('Stopped', '중지됨')
    } 'completed stopped-only settings reload'
    if ($null -ne (Find-Control 'ReloadConfirmation')) { throw 'Reload unexpectedly requested unsaved-edit confirmation.' }
}
function Confirm-Reload([string]$Action) {
    Wait-For { $null -ne (Find-Control 'ReloadConfirmation') } 'unsaved settings confirmation'
    Invoke-Control (Find-Name (Find-Control 'ReloadConfirmation') $Action)
    Wait-For { $null -eq (Find-Control 'ReloadConfirmation') -and (Find-Control 'ReloadSettingsButton').Current.IsEnabled } 'completed/cancelled reload confirmation'
}
function Assert-ReloadDisabled([string]$Phase) {
    $button = Find-Control 'ReloadSettingsButton'
    if ($button.Current.IsEnabled) { throw "Reload is available during $Phase." }
    Assert-DisabledInput { Invoke-Control $button }
}
function Saved-Hashes {
    return (@(Get-ChildItem $testFolder -Recurse -File | Sort-Object FullName | ForEach-Object {
        $_.FullName + ':' + (Get-FileHash $_.FullName).Hash
    })) -join '|'
}

Close-App
Start-App
$initialConfig = Read-Config
Reload-Settings
if ((Read-Config) -ne $initialConfig) { throw 'A clean reload wrote configuration.' }
Commit-Folder $testFolder
Select-Format 'JPEG'
Set-Quality 37
Start-Monitoring
Assert-ReloadDisabled 'monitoring'
Copy-Image
Wait-For { $null -ne (Find-Control 'PreviewImage') -and (Find-Control 'QueueOutcomes').Current.Name -eq 'Saved: 1 · Failed: 0' } 'session data before reload'
Stop-Monitoring
$caption = (Find-Control 'PreviewCaption').Current.Name
$hashes = Saved-Hashes
$sequence = [DesktopNative]::GetClipboardSequenceNumber()
$oldConfig = Read-Config
$profile = @(Get-Profiles)[0]
$newFolder = Join-Path $testFolder 'reloaded'
$profile.Name = 'Reloaded profile'
$profile.Formula = 'Reload_$YYYY'
$profileJson = $profile | ConvertTo-Json -Compress
$external = "; reload marker`r`n[Storage]`r`nSaveFolder=$newFolder`r`nImageFormat=JPEG`r`nJpegQuality=65`r`n[Naming]`r`nFormula=Reload_`$YYYY`r`nSelectedPreset=$($profile.Id)`r`n[NamingPresets]`r`nPreset.$($profile.Id)=$profileJson`r`n[Appearance]`r`nLanguage=en`r`n[Future]`r`nKeep=one=two;#three`r`n"
[IO.File]::WriteAllText($config, $external)
Expand-Naming $true
Set-Text 'RuleFormula' 'Unsaved_$MM'
Set-Text 'PresetName' 'Unsaved profile name'
Set-Folder (Join-Path $testFolder 'unsaved-folder')
# Exercise focus transfer to the actual command, not just an automation Invoke.
(Find-Control 'ReloadSettingsButton').SetFocus()
Invoke-Id 'ReloadSettingsButton'
Wait-For { $null -ne (Find-Control 'ReloadConfirmation') } 'warning before discarding focused folder/profile edits'
if ((Read-Config) -ne $external) { throw 'Reload focus/confirmation saved old edits over external settings.' }
Confirm-Reload 'Cancel'
if ((Text-Value 'RuleFormula') -ne 'Unsaved_$MM' -or (Text-Value 'PresetName') -ne 'Unsaved profile name' -or
    (Folder-Value) -ne (Join-Path $testFolder 'unsaved-folder') -or (Read-Config) -ne $external) {
    throw 'Cancelling reload discarded edits or changed the external file.'
}
Invoke-Id 'ReloadSettingsButton'
Confirm-Reload 'Discard and reload'
if ((Folder-Value) -ne $newFolder -or (Format-Value) -ne 'JPEG' -or (Quality-Value) -ne 65 -or
    (Text-Value 'RuleFormula') -ne 'Reload_$YYYY' -or (Text-Value 'PresetName') -ne 'Reloaded profile' -or
    (Read-Config) -ne $external) { throw 'Confirmed reload did not apply the fresh settings without rewriting the file.' }
[IO.File]::SetAttributes($config, [IO.FileAttributes]::ReadOnly)
try { Reload-Settings } finally { [IO.File]::SetAttributes($config, [IO.FileAttributes]::Normal) }
Reload-Settings
if ((Read-Config) -ne $external -or (Find-Control 'PreviewCaption').Current.Name -ne $caption -or
    $null -eq (Find-Control 'PreviewImage') -or (Find-Control 'QueueOutcomes').Current.Name -ne 'Saved: 1 · Failed: 0' -or
    (Saved-Hashes) -ne $hashes -or [DesktopNative]::GetClipboardSequenceNumber() -ne $sequence) {
    throw 'Repeated reload reset the session, changed images/clipboard, wrote the file or lost the preview.'
}
Expand-Naming $false

# The real clipboard provider holds acquisition during manual Stop. Reload cannot auto-stop.
Start-Monitoring
[DelayedClipboard]::Start()
try {
    Wait-For { (Find-Control 'QueueActivity').Current.Name -eq 'Reading: 1 · Waiting: 0 · Saving: 0' } 'accepted original JPEG/65 capture'
    $nextFolder = Join-Path $testFolder 'after-drain'
    $afterDrain = $external.Replace("SaveFolder=$newFolder", "SaveFolder=$nextFolder").Replace('ImageFormat=JPEG', 'ImageFormat=BMP')
    [IO.File]::WriteAllText($config, $afterDrain)
    Invoke-Id 'StopButton'
    Wait-For { (Find-Control 'QueueStatus').Current.Name -eq 'Stopping monitoring' } 'manual Stop awaiting the clipboard provider'
    Assert-ReloadDisabled 'pending manual Stop'
    [DelayedClipboard]::Complete()
    Wait-For { (Find-Control 'MonitoringStatus').Current.Name -eq 'Stopped' } 'completed manual Stop before reload'
    Reload-Settings
    if ((Folder-Value) -ne $nextFolder -or (Format-Value) -ne 'BMP' -or
        (Find-Control 'QueueActivity').Current.Name -ne 'Reading: 0 · Waiting: 0 · Saving: 0' -or
        @(Get-ChildItem $newFolder -Filter 'Reload_*.jpg').Count -ne 1 -or
        $null -eq (Find-Name (Find-Control 'QueueList') 'JPEG (65)')) {
        throw 'Reload did not drain accepted work with original folder/format/quality before applying new settings.'
    }
} finally { [DelayedClipboard]::Complete(); [DelayedClipboard]::Stop() }

# Already submitted writes settle before reading. Clear during that wait suppresses stale notices.
Select-Format 'JPEG'
Wait-ImagePreference 'JPEG' 65
$clearButton = Find-Control 'ClearHistoryButton'
$reloadButton = Find-Control 'ReloadSettingsButton'
$heldConfig = [IO.FileStream]::new($config, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    Select-Format 'BMP'
    Invoke-Control $reloadButton
    Wait-For { -not $reloadButton.Current.IsEnabled } 'reload waiting for a submitted settings write'
    Invoke-Control $clearButton
} finally { $heldConfig.Dispose() }
Wait-For { (Find-Control 'ReloadSettingsButton').Current.IsEnabled -and (Find-Control 'MonitoringStatus').Current.Name -eq 'Stopped' } 'reload after Clear and pending writes'
if ($null -ne (Find-Control 'ReloadConfirmation') -or (Format-Value) -ne 'BMP' -or
    $null -ne (Find-Name (Find-Control 'SettingsMessage') 'Reload settings')) { throw 'A stale callback/pending write revived cleared settings or failed to settle before reload.' }
Assert-EmptyHistory

# Discover added/edited/removed language packs in the running process and preserve values.
$reloadLanguage = Join-Path $publish 'lang/fr.json'
try {
    [IO.File]::WriteAllText($reloadLanguage, '{"languageName":"Français","strings":{"Start":"First start"}}', [Text.Encoding]::UTF8)
    [IO.File]::WriteAllText($config, (Read-Config).Replace('Language=en', 'Language=fr'))
    Reload-Settings
    if ((Find-Control 'StartButton').Current.Name -ne 'First start' -or (Format-Value) -ne 'BMP') { throw 'Added language/config preferences were not loaded.' }
    [IO.File]::WriteAllText($reloadLanguage, '{"languageName":"Français","strings":{"Start":"Second start"}}', [Text.Encoding]::UTF8)
    Reload-Settings
    if ((Find-Control 'StartButton').Current.Name -ne 'Second start') { throw 'Edited language pack remained stale.' }
    [IO.File]::WriteAllText($reloadLanguage, 'not-json', [Text.Encoding]::UTF8)
    Reload-Settings
    if ((Find-Control 'StartButton').Current.Name -ne 'Start') { throw 'Invalid language did not fall back to English.' }
    Remove-Item $reloadLanguage
    Reload-Settings
    if ((Find-Control 'StartButton').Current.Name -ne 'Start') { throw 'Removed saved language did not fall back to English.' }
    [IO.File]::WriteAllText($config, (Read-Config).Replace('Language=fr', 'Language=ko'))
    Reload-Settings
    if ((Find-Control 'ReloadSettingsButton').Current.Name -ne '설정 다시 읽기') { throw 'Reload command is not localized.' }
    Expand-Naming $true
    Set-Text 'PresetName' 'Not saved'
    Invoke-Id 'ReloadSettingsButton'
    Wait-For { $null -ne (Find-Name (Find-Control 'ReloadConfirmation') '저장하지 않은 설정') } 'Korean unsaved-edit warning'
    Confirm-Reload '편집 버리고 다시 읽기'
    [IO.File]::WriteAllText($config, (Read-Config).Replace('Language=ko', 'Language=en'))
    Reload-Settings
} finally { if (Test-Path $reloadLanguage) { Remove-Item $reloadLanguage -Force } }

# Invalid and removed entries replace stale choices with the same startup fallbacks.
$validReload = Read-Config
[IO.File]::WriteAllText($config, $validReload.Replace('ImageFormat=BMP', 'ImageFormat=GIF').Replace('JpegQuality=65', 'JpegQuality=101'))
$invalidReload = Read-Config
Reload-Settings
if ((Format-Value) -ne 'PNG' -or (Read-Config) -ne $invalidReload) { throw 'Invalid settings reload retained stale format or rewrote the file.' }
Select-Format 'JPEG'
if ((Quality-Value) -ne 90) { throw 'Invalid quality did not use startup default.' }
Wait-ImagePreference 'JPEG' 90
[IO.File]::WriteAllText($config, "[broken`r`n")
Reload-Settings
if ((Format-Value) -ne 'PNG' -or (Read-Config) -ne "[broken`r`n" -or (Folder-Value) -ne $defaultFolder) { throw 'Malformed reload did not fall back without overwriting the file.' }
[IO.File]::WriteAllText($config, "[Future]`r`nKeep=untouched`r`n")
Reload-Settings
Expand-Naming $true
if ((Text-Value 'RuleFormula') -ne $defaultRule -or (Folder-Value) -ne $defaultFolder -or (Format-Value) -ne 'PNG') { throw 'Removed entries retained stale settings.' }
[IO.File]::WriteAllText($config, $oldConfig)
Reload-Settings
Select-Format 'JPEG'
Set-Quality 90
Select-Format 'PNG'
Wait-ImagePreference 'PNG' 90
Expand-Naming $false
Close-App
Start-App
Clear-History
Write-Output '::notice::Reload UI passed: stopped-only/transition restrictions, focused unsaved edits and cancellation/confirmation, fresh all-setting loads, repeated reload without writes/prompts, preserved history/preview/totals/files/clipboard, original accepted save options/drain, pending writes/Clear, language-pack changes and Korean confirmation, malformed/removed/default recovery.'
