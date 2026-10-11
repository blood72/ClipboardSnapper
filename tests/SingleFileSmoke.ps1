param([Parameter(Mandatory = $true)][string]$BundlePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$bundle = (Resolve-Path $BundlePath).Path
$files = @(Get-ChildItem $bundle -Recurse -File -Force)
$expectedFiles = @('ClipboardSnapper.exe', 'lang/en.json', 'lang/ko.json')
$relativeFiles = @($files | ForEach-Object { $_.FullName.Substring($bundle.Length + 1).Replace('\', '/') } | Sort-Object)
if (($relativeFiles -join '|') -ne (($expectedFiles | Sort-Object) -join '|')) {
    throw "The bundled-runtime artifact must contain the EXE and two external language files only. Actual: $($relativeFiles -join ' | ')"
}
$sourceExe = Join-Path $bundle 'ClipboardSnapper.exe'
$sourceLanguageFolder = Join-Path $bundle 'lang'
$fixture = Join-Path $env:RUNNER_TEMP ('ClipboardSnapper bundle 한글 ' + [Guid]::NewGuid().ToString('N'))
$appFolder = Join-Path $fixture 'application'
$cache = Join-Path $fixture 'runtime cache'
$working = Join-Path $fixture 'working directory'
New-Item -ItemType Directory -Path $appFolder, $working | Out-Null
$exe = Join-Path $appFolder 'ClipboardSnapper.exe'
$config = Join-Path $appFolder 'config.ini'
Copy-Item $sourceExe $exe
$oldCache = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$oldHostTrace = $env:DOTNET_HOST_TRACE
$oldHostTraceFile = $env:DOTNET_HOST_TRACEFILE
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $cache
$env:DOTNET_HOST_TRACE = '1'
$env:DOTNET_HOST_TRACEFILE = Join-Path $fixture 'host-trace.txt'
$process = $null
$root = $null
$stderrTask = $null

function Start-BundleProcess {
    # Keep the native process handle and drain stderr; Windows PowerShell Start-Process
    # with redirected streams can return an object without a usable exit code.
    $script:process = [Diagnostics.Process]::new()
    $process.StartInfo = [Diagnostics.ProcessStartInfo]::new($exe)
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.WorkingDirectory = $working
    $process.StartInfo.RedirectStandardError = $true
    [void]$process.Start()
    $script:stderrTask = $process.StandardError.ReadToEndAsync()
}

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
    $diagnostic = $stderrTask.GetAwaiter().GetResult()
    throw "Timed out waiting for bundled $Description. Window: $title. Controls: $details. Diagnostic: $diagnostic. Modules: $modules. Extracted files: $extracted"
}
function Start-Bundle {
    Start-BundleProcess
    Wait-Bundle { $process.Refresh(); $process.MainWindowHandle -ne [IntPtr]::Zero } 'window handle'
    if ($process.MainWindowTitle -ne 'ClipboardSnapper') { throw 'Incorrect bundled window title.' }
    $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Wait-Bundle { $null -ne (Find-BundleControl 'FormatPicker') -and (Find-BundleControl 'FormatPicker').Current.IsEnabled } 'settings load'
    $process.Refresh() # Start-Process can cache module metadata before bundle extraction completes.
    foreach ($moduleName in @('System.Private.CoreLib.dll', 'Microsoft.UI.Xaml.dll')) {
        $module = $process.Modules | Where-Object ModuleName -eq $moduleName | Select-Object -First 1
        if ($null -eq $module -or -not $module.FileName.StartsWith($cache + '\', [StringComparison]::OrdinalIgnoreCase)) {
            $loaded = ($process.Modules | ForEach-Object { $_.FileName }) -join ' | '
            throw "Bundled native module was not loaded from the isolated extraction cache: $moduleName. Actual: $($module.FileName). Expected root: $cache. Loaded: $loaded"
        }
    }
    foreach ($moduleName in @('coreclr.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
        $module = $process.Modules | Where-Object ModuleName -eq $moduleName | Select-Object -First 1
        if ($null -ne $module -and -not $module.FileName.StartsWith($cache + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "An external runtime was loaded instead of a bundled component: $($module.FileName)"
        }
    }
    $runtimeRoot = Split-Path ($process.Modules | Where-Object ModuleName -eq 'System.Private.CoreLib.dll' | Select-Object -First 1).FileName
    foreach ($file in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
        if (-not (Test-Path (Join-Path $runtimeRoot $file) -PathType Leaf)) { throw "Bundled CRT payload missing: $file" }
    }
    $runtimeConfig = Get-Content (Join-Path $runtimeRoot 'ClipboardSnapper.runtimeconfig.json') -Raw | ConvertFrom-Json
    if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) { throw 'The bundle depends on a separately installed .NET framework.' }
}
function Close-Bundle {
    ([System.Windows.Automation.WindowPattern]$root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    $exited = $process.WaitForExit(15000)
    if (-not $exited -or $process.ExitCode -ne 0) {
        if (-not $exited) { Stop-Process -Id $process.Id; [void]$process.WaitForExit(15000) }
        $diagnostic = $stderrTask.GetAwaiter().GetResult()
        $trace = (Get-Content (Join-Path $fixture 'host-trace.txt') -ErrorAction SilentlyContinue | Select-Object -Last 15) -join ' | '
        throw "Bundled app failed to close normally. Exited: $exited. Code: $($process.ExitCode). Diagnostic: $diagnostic. Host: $trace"
    }
    $script:root = $null
}
try {
    # No language data is hidden in the EXE: an EXE-only installation fails with a useful dialog.
    Start-BundleProcess
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    do {
        $process.Refresh()
        if ($process.HasExited) { throw 'Missing external English did not show a startup error dialog.' }
        Start-Sleep -Milliseconds 100
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Missing external English did not report a startup error.' }
    $dialog = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $names = @($dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' '
    if ($names -notmatch 'lang' -or $names -notmatch 'en.json') { throw "Missing-English diagnostic is not actionable: $names" }
    ([System.Windows.Automation.WindowPattern]$dialog.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)).Close()
    if (-not $process.WaitForExit(15000) -or $process.ExitCode -eq 0) { throw 'Missing external English must exit with failure.' }
    if (Test-Path $config) { throw 'Failed language initialization wrote configuration.' }
    if (Test-Path (Join-Path $appFolder 'lang')) { throw 'The app unexpectedly generated translation files.' }
    Copy-Item $sourceLanguageFolder (Join-Path $appFolder 'lang') -Recurse

    # Start with the EXE and external lang folder, using a different working directory.
    Start-Bundle
    if ((Find-BundleControl 'MonitoringStatus').Current.Name -notin @('Ready', '준비됨')) { throw 'Bundled first launch started monitoring.' }
    if (@(Get-ChildItem $cache -Recurse -Directory -Filter lang).Count -ne 0) { throw 'Translation files were included in the runtime extraction cache.' }
    Close-Bundle
    Get-Content (Join-Path $fixture 'host-trace.txt') | Where-Object { $_ -match 'coreclr|CoreCLR|internal|runtime location' } | Select-Object -Last 15 | Write-Output
    $env:DOTNET_HOST_TRACE = $oldHostTrace
    $env:DOTNET_HOST_TRACEFILE = $oldHostTraceFile

    # Korean comes exclusively from the editable pack; settings remain beside the original EXE.
    $imageFolder = Join-Path $fixture 'saved images'
    New-Item -ItemType Directory -Path $imageFolder | Out-Null
    $profileId = '11111111111111111111111111111111'
    $formula = 'Portable_${start=7;padding=4}'
    $profileJson = [ordered]@{ Id = $profileId; Name = 'Portable profile'; Formula = $formula } | ConvertTo-Json -Compress
    $preserved = "; Keep settings across replacement`r`n[Appearance]`r`nLanguage=ko`r`n[Updates]`r`nEnabled=false`r`n[Storage]`r`nSaveFolder=$imageFolder`r`nImageFormat=PNG`r`nJpegQuality=90`r`n"
    $preserved += "[Naming]`r`nFormula=$formula`r`nSelectedPreset=$profileId`r`n[NamingPresets]`r`nPreset.$profileId=$profileJson`r`n"
    [IO.File]::WriteAllText($config, $preserved, [Text.UTF8Encoding]::new($false))
    Start-Bundle
    if ((Find-BundleControl 'StartButton').Current.Name -ne '시작') { throw 'External Korean did not load beside the original EXE.' }
    Close-Bundle
    if ([IO.File]::ReadAllText($config) -ne $preserved) {
        throw "Restart changed an existing preference fixture. Actual: $([IO.File]::ReadAllText($config)). Expected: $preserved"
    }

    # Editing, invalidating, removing and restoring Korean must never reveal a hidden built-in copy.
    $koreanFile = Join-Path $appFolder 'lang/ko.json'
    $originalKorean = [IO.File]::ReadAllText($koreanFile)
    $editedKorean = $originalKorean.Replace('"Start": "시작"', '"Start": "외부 시작"')
    [IO.File]::WriteAllText($koreanFile, $editedKorean, [Text.UTF8Encoding]::new($false))
    Start-Bundle
    if ((Find-BundleControl 'StartButton').Current.Name -ne '외부 시작') { throw 'Edited external Korean was ignored.' }
    Close-Bundle
    if ([IO.File]::ReadAllText($koreanFile) -ne $editedKorean) { throw 'The app overwrote the user translation file.' }
    [IO.File]::WriteAllText($koreanFile, 'not-json')
    Start-Bundle
    if ((Find-BundleControl 'StartButton').Current.Name -ne 'Start') { throw 'Invalid Korean used a hidden built-in pack.' }
    Close-Bundle
    Remove-Item $koreanFile
    Start-Bundle
    if ((Find-BundleControl 'StartButton').Current.Name -ne 'Start' -or [IO.File]::ReadAllText($config) -ne $preserved) {
        throw 'Removing Korean did not fall back to English while preserving the saved choice.'
    }
    Close-Bundle
    if (Test-Path $koreanFile) { throw 'The app regenerated a deleted language file.' }
    [IO.File]::WriteAllText($koreanFile, $originalKorean, [Text.UTF8Encoding]::new($false))

    # Delete only this test's stopped cache, replace the EXE, and repeat a cold launch.
    Remove-Item $cache -Recurse -Force
    Copy-Item $sourceExe $exe -Force
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
        Start-BundleProcess
        if (-not $process.WaitForExit(15000)) { throw 'Blocked extraction did not fail within the startup deadline.' }
        if ($process.ExitCode -eq 0 -or [string]::IsNullOrWhiteSpace($stderrTask.GetAwaiter().GetResult())) {
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
    if (@(Get-ChildItem $appFolder -Recurse -File -Force).Count -ne 3) { throw 'Feature tests left unexpected files beside the bundle and lang folder.' }
    Start-Bundle
    Close-Bundle
    $topLevel = @(Get-ChildItem $appFolder -Force | Select-Object -ExpandProperty Name | Sort-Object)
    if (($topLevel -join '|') -ne 'ClipboardSnapper.exe|config.ini|lang') { throw "Unexpected portable layout: $topLevel" }
    foreach ($code in @('en', 'ko')) {
        if ((Get-FileHash (Join-Path $appFolder "lang/$code.json")).Hash -ne (Get-FileHash (Join-Path $sourceLanguageFolder "$code.json")).Hash) {
            throw "The app changed the distributed $code translation file."
        }
    }
    Write-Output "::notice::Single-file verified: $((Get-Item $sourceExe).Length) bytes; EXE/config.ini/lang layout, no embedded language data, missing-English startup diagnostic, editable/invalid/removed/restored Korean, cold/repeated launches, native runtimes from isolated cache, spaces/non-ASCII/different working directory, cache recreation and replacement with portable settings, blocked-extraction diagnostic, and the complete feature/single-instance suite."
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id
        [void]$process.WaitForExit(15000)
    }
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $oldCache
    $env:DOTNET_HOST_TRACE = $oldHostTrace
    $env:DOTNET_HOST_TRACEFILE = $oldHostTraceFile
    Remove-Item $fixture -Recurse -Force
}
