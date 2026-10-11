param([Parameter(Mandatory = $true)][string]$BundlePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$bundle = (Resolve-Path $BundlePath).Path
$files = @(Get-ChildItem $bundle -Recurse -File -Force)
if ($files.Count -ne 1 -or $files[0].Name -ne 'ClipboardSnapper.exe') {
    throw 'The single-file artifact must contain exactly ClipboardSnapper.exe.'
}
$fixture = Join-Path $env:RUNNER_TEMP ('ClipboardSnapper bundle 한글 ' + [Guid]::NewGuid().ToString('N'))
$appFolder = Join-Path $fixture 'application'
$cache = Join-Path $fixture 'runtime cache'
$working = Join-Path $fixture 'working directory'
New-Item -ItemType Directory -Path $appFolder, $working | Out-Null
$exe = Join-Path $appFolder 'ClipboardSnapper.exe'
$config = Join-Path $appFolder 'config.ini'
Copy-Item $files[0].FullName $exe
$oldCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $cache
$process = $null
$root = $null

function Find-BundleControl([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Wait-Bundle($Check, [string]$Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    do {
        if ($script:process.HasExited) { throw "Bundled app exited during $Description`: $($script:process.ExitCode)" }
        try { if (& $Check) { return } } catch [System.Windows.Automation.ElementNotAvailableException] { }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $details = if ($null -ne $script:root) {
        ($script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition) | Select-Object -First 100 | ForEach-Object {
            "$($_.Current.ControlType.ProgrammaticName): $($_.Current.Name) / $($_.Current.AutomationId)"
        }) -join ' | '
    } else { 'No automation root.' }
    $title = $process.MainWindowTitle
    $modules = ($process.Modules | ForEach-Object { $_.FileName }) -join ' | '
    $extracted = (Get-ChildItem $cache -Recurse -File | Where-Object { $_.Extension -in @('.dll', '.pri', '.manifest') } | ForEach-Object { $_.FullName }) -join ' | '
    Stop-Process -Id $process.Id
    [void]$process.WaitForExit(15000)
    $diagnostic = if (Test-Path (Join-Path $fixture 'application-error.txt')) { [IO.File]::ReadAllText((Join-Path $fixture 'application-error.txt')) } else { '' }
    throw "Timed out waiting for bundled $Description. Window: $title. Controls: $details. Diagnostic: $diagnostic. Modules: $modules. Extracted files: $extracted"
}
function Start-Bundle {
    $script:process = Start-Process $exe -WorkingDirectory $working -PassThru -RedirectStandardError (Join-Path $fixture 'application-error.txt')
    Wait-Bundle { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero } 'window handle'
    if ($process.MainWindowTitle -ne 'ClipboardSnapper') { throw 'Incorrect bundled window title.' }
    $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-Bundle { $null -ne (Find-BundleControl 'FormatPicker') -and (Find-BundleControl 'FormatPicker').Current.IsEnabled } 'settings load'
    $process.Refresh() # Start-Process can cache module metadata before bundle extraction completes.
    foreach ($moduleName in @('coreclr.dll', 'Microsoft.UI.Xaml.dll', 'vcruntime140.dll', 'msvcp140.dll')) {
        $module = $process.Modules | Where-Object ModuleName -eq $moduleName | Select-Object -First 1
        if ($null -eq $module -or -not $module.FileName.StartsWith($cache + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $loaded = ($process.Modules | ForEach-Object { $_.FileName }) -join ' | '
            throw "Bundled native module was not loaded from the isolated extraction cache: $moduleName. Actual: $($module.FileName). Expected root: $cache. Loaded: $loaded"
        }
    }
}
function Close-Bundle {
    ([System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -ne 0) { throw 'Bundled app failed to close normally.' }
    $script:root = $null
}
try {
    # Start with only the EXE, using a different working directory and an empty extraction cache.
    Start-Bundle
    if ((Find-BundleControl 'MonitoringStatus').Current.Name -notin @('Ready', '준비됨')) { throw 'Bundled first launch started monitoring.' }
    if (Test-Path (Join-Path $appFolder 'lang')) { throw 'Startup unexpectedly materialized external language packs.' }
    Close-Bundle

    # No external packs are needed for Korean; settings remain beside the original EXE.
    $preserved = "; Keep settings across replacement`r`n[Appearance]`r`nLanguage=ko`r`n[Updates]`r`nEnabled=false`r`n"
    [IO.File]::WriteAllText($config, $preserved, [Text.UTF8Encoding]::new($false))
    Start-Bundle
    if ((Find-BundleControl 'StartButton').Current.Name -ne '시작') { throw 'Embedded Korean did not load without external language files.' }
    Close-Bundle
    if ([IO.File]::ReadAllText($config) -ne $preserved) { throw 'Restart rewrote portable settings.' }

    # Delete only this test's stopped cache, replace the EXE, and repeat a cold launch.
    Remove-Item $cache -Recurse -Force
    Copy-Item $files[0].FullName $exe -Force
    Start-Bundle
    if ((Find-BundleControl 'StartButton').Current.Name -ne '시작' -or [IO.File]::ReadAllText($config) -ne $preserved) {
        throw 'Cache recreation or EXE replacement lost settings.'
    }
    Close-Bundle
    if (Test-Path (Join-Path $working 'config.ini')) { throw 'Configuration was written to the working directory.' }
    if (@(Get-ChildItem $cache -Recurse -Filter config.ini -File).Count -ne 0) { throw 'Configuration was written to the extraction cache.' }
    # A blocked extraction destination must fail before application work and preserve settings.
    $blockedCache = Join-Path $fixture 'blocked extraction root'
    [IO.File]::WriteAllText($blockedCache, 'This is a file, not a cache directory.')
    $oldGuiErrors = $env:DOTNET_DISABLE_GUI_ERRORS
    try {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $blockedCache
        $env:DOTNET_DISABLE_GUI_ERRORS = '1'
        $script:process = Start-Process $exe -WorkingDirectory $working -PassThru `
            -RedirectStandardError (Join-Path $fixture 'startup-error.txt') -RedirectStandardOutput (Join-Path $fixture 'startup-output.txt')
        if (-not $process.WaitForExit(15000)) { throw 'Blocked extraction did not fail within the startup deadline.' }
        if ($process.ExitCode -eq 0 -or [string]::IsNullOrWhiteSpace([IO.File]::ReadAllText((Join-Path $fixture 'startup-error.txt')))) {
            throw 'Blocked extraction must return failure with a host diagnostic.'
        }
        if ([IO.File]::ReadAllText($config) -ne $preserved) { throw 'Failed extraction modified user settings.' }
    } finally {
        $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $cache
        $env:DOTNET_DISABLE_GUI_ERRORS = $oldGuiErrors
    }
    Remove-Item $config

    # Exercise all existing contracts against this isolated EXE, including custom external packs,
    # settings reload, image encoding and AppInstance forwarding/cold-start/shutdown races.
    & powershell.exe -NoProfile -STA -File (Join-Path $PSScriptRoot 'FeatureSmoke.ps1') -PublishPath $appFolder
    if ($LASTEXITCODE -ne 0) { throw "Bundled feature tests failed: $LASTEXITCODE" }
    if (@(Get-ChildItem $appFolder -Recurse -File -Force).Count -ne 1) { throw 'Feature tests left unexpected files beside the bundle.' }
    Write-Output "::notice::Single-file verified: $($files[0].Length) bytes; EXE-only cold/repeated launches, native runtimes from isolated cache, embedded Korean, spaces/non-ASCII/different working directory, cache recreation and replacement with portable settings, blocked-extraction diagnostic, and the complete feature/single-instance suite."
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
        [void]$process.WaitForExit(15000)
    }
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $oldCache
    Remove-Item $fixture -Recurse -Force
}
