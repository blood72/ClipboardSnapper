# ClipboardSnapper

A C# / WinUI 3 desktop app that saves new clipboard images while monitoring is
enabled. English and Korean screens show monitoring controls, save settings, the
latest saved image, and recent successes and failures. The instructions below use
the English control names.

## Use the app

1. Choose a **Save folder** by entering an absolute path or selecting **Browse**.
   The default is `Pictures\ClipboardSnapper`. **Open Folder** creates and opens
   the selected directory.
2. Choose **PNG**, **JPEG**, or **BMP**. JPEG shows a **Quality (1–100)** slider,
   defaulting to **90**; PNG and BMP hide it. Then press **Start**. The status changes
   from **Ready** to **Monitoring**. Existing clipboard content is not saved;
   copy a new image after starting. Text and file-list clipboard content are ignored.
3. A completed save shows **Image saved**, updates the preview, and adds a **Saved**
   row with its local save time. **Save failed** and **Failed** rows expose the
   reason; select **Details** for the full path, time, and exception message.
4. Press **Stop** to stop accepting clipboard changes. Images already being read
   or saved finish in the background. The image format and JPEG quality controls
   stay disabled while monitoring starts, runs or stops. After Stop completes,
   edit them for the next run; already accepted images keep their original options.
   Stop before changing the folder or filename rules as well.
5. **Clear History**, next to **Recent files**, clears the current session's list,
   preview, queue rows/completed totals, image captions and success/failure messages. It works while monitoring
   or stopped and returns the screen to empty guidance. **Saved files are kept**;
   the Windows clipboard and its history are unchanged. Monitoring and pending
   saves continue. Previously accepted images finish saving without returning to
   the cleared screen; images copied after Clear appear normally. This is a screen
   cleanup action, not secure deletion. Folder settings, filename presets and
   numbering state are kept.

The UI language, save folder, image format, JPEG quality, filename formula, active
preset and saved user presets are remembered in `config.ini` beside the executable.
The latest 100 history rows are session-only. Files remain on disk.
Closing the window waits for accepted reads and writes to finish. JPEG images
are composited onto white because JPEG cannot store transparency. Failed writes
use temporary files and do not expose a partially written final image.
JPEG quality is passed to `BitmapEncoder` as the `ImageQuality` option, dividing
the integer slider value by 100 to produce a single-precision value from 0.01 to
1.0. It is not applied to PNG or BMP.

## Save queue and processing status

**Save queue** lists each accepted capture with its acceptance time, frozen format,
and Reading → Waiting → Saving → Saved/failure state. Completed rows keep the
filename or failure explanation; **Details** exposes the full path and original
diagnostics. A capture number identifies a job before its filename is available.
The view retains up to 100 completed jobs plus all active jobs.

It also shows **Reading** (clipboard acquisition/checks), **Waiting**
(images queued for the writer), and **Saving** (encoding and writing). An
indeterminate indicator appears while any work is active; it is not a percentage
or an estimate of remaining time. When monitoring is idle it says **Waiting for
new images**. After **Stop**, **Finishing accepted work** remains visible until
all accepted reads and saves finish, then changes to **All accepted work finished**.
If a native clipboard provider is blocking acquisition, **Stopping monitoring**
shows that Stop was requested but is waiting for the capture thread to respond;
it does not claim monitoring has already stopped.

**Saved / Failed** totals belong to the current history generation: they start at
zero on launch, remain across Start/Stop, and reset on **Clear History**. Failure
counts distinguish read errors, captures rejected by the existing busy limits,
and save errors. Text and other non-image clipboard content do not count as
successes or failures. Failure reasons remain in **Recent files → Details**.

Clearing leaves Reading/Waiting/Saving unchanged and lets those jobs finish.
The cleared queue rows are hidden, including older active captures. Their older
results neither repopulate queue/history/preview nor increase the new totals; captures accepted after Clear count normally. Stored images and Windows
clipboard contents are unchanged. The recent list is limited to 100 rows, so its
size need not equal the cumulative totals. Language changes translate the queue
without resetting work or totals. Queue statistics are session-only.

Capture and writer threads update only bounded job metadata and counters under a brief lock; they
never wait for UI rendering or invoke UI callbacks. The existing 250 ms display
timer samples a consistent snapshot and skips unchanged values. Short-lived
stages may finish between display updates. The display does not change existing
read concurrency, queue capacity, rejection or accepted-save policies. Queue
cancellation/reordering/retry controls are outside this feature; final visual
placement remains part of the later UI review (#2).

## Language and editable translations

On first launch, the app follows the Windows UI language. It tries an available
exact language tag and then its parent tags: `ko-KR` uses `ko`; an unavailable
language uses English. **Language** selects a language immediately, including
while monitoring. The choice is remembered in executable-adjacent `config.ini`:

```ini
[Appearance]
Language=ko
```

An unavailable saved language falls back to English with a warning. A read-only
or otherwise unwritable configuration keeps the choice for this session only and
shows a warning. Changing language preserves monitoring, queued saves, existing
history, preview, paths and profile names. Filename formulas, date variables,
numeric collision suffixes and fixed `yyyy-MM-dd HH:mm:ss` timestamps keep their
existing formatting. Windows-owned dialogs and raw system/library diagnostics
retain their Windows-provided language; file Details also keeps the original
exception information. Repository documentation remains English.

Translations are editable UTF-8 files in **`lang` beside `ClipboardSnapper.exe`**.
The publish artifact includes `lang/en.json` and `lang/ko.json`. Add another
language-tag JSON file, such as `lang/ja.json`, then restart the app to select it;
no app rebuild is required. Translation files are read at startup. Added, edited,
removed or invalidated files take effect on the next launch; an unavailable saved
language falls back to English with a warning. Missing translation keys use the
current English file, then the embedded English baseline. Malformed JSON, duplicate
keys, invalid language tags and incompatible placeholders are rejected per file;
other language files remain usable. Embedded English handles absent external files.

**Reload settings** reads configuration and language packs together while stopped;
see the reload procedure below. There is no separate language-only reload command.

See [Translation file guide](docs/translations.md) for the format and examples.
Until formal 1.0.0, migrations between snapshot configuration formats are not
required. Overall UI cleanup and comprehensive visual/DPI review remain a later task.

## Remembered save folder

**Browse** saves the selected folder immediately; a manually entered path is saved
when the path field loses focus. Neither Start nor a captured/saved image is
required. Cancelling Browse keeps the previous selection. The next launch restores
the folder and remains in **Ready**, with monitoring off.

The configuration is portable: `config.ini` lives in the executable's directory,
regardless of the working directory used to launch the app. Keep it alongside the
app when moving or updating the application. The app creates it when a
preference is saved; it is not a required runtime/deployment file.

```ini
[Storage]
SaveFolder=C:\Users\YourName\Pictures\ClipboardSnapper
```

Folder usability is checked during restoration, selection/focus loss and Start.
Missing folders are created if possible. A short-lived probe checks write access
and is removed automatically. Invalid paths, disconnected drives and inaccessible
folders fall back to the current Windows user's `Pictures\ClipboardSnapper`;
the field and saved preference both change to this default, with a warning. If the
default is also unusable, choose another folder before starting. A destination that
becomes unavailable during monitoring still reports save failures; already accepted
images and queued saves keep their original options and are never redirected.

The file uses INI sections and `key=value` entries (UTF-8 on writes); `Storage` and
`SaveFolder` are case-insensitive. Unrelated entries and comments are retained for
future preferences, including updater settings. No updater is implemented here.
Updates use a temporary file in the same directory followed by file replacement.
Brief replacement locks are retried with a bounded delay off the UI thread;
read-only files and persistent permission errors still produce a warning.
An absent configuration uses the default. An unreadable or malformed configuration
uses the default and shows a warning without overwriting the original file.
Malformed section headers/entries and duplicate `SaveFolder` keys are rejected.

Use a writable application folder to remember changes. If `config.ini` cannot be
written, the current usable selection still works for this session and the screen
warns that it was not remembered. The app does not request elevation or silently
move configuration elsewhere. Saved images and clipboard contents are unaffected.
Settings I/O and validation run off the UI thread, separately from clipboard reads
and the image-saving worker; rapid preference updates are serialized.

## Remembered image format and JPEG quality

Changing **Image format** saves immediately. **JPEG Quality** changes are saved
200 ms after the last slider edit, coalescing continuous adjustments off the UI
thread. Start and normal window close await the last pending write. No capture,
Start or separate Save button is required to remember either choice.

```ini
[Storage]
ImageFormat=JPEG
JpegQuality=90
```

These keys share the existing portable `config.ini` with the save folder, profiles
and language. Formats are `PNG`, `JPEG` or `BMP` (case-insensitive); quality is an
integer from 1 to 100. Missing values default independently to PNG and 90. The
quality control is visible only for JPEG; switching to PNG/BMP and back preserves
its remembered value, and non-JPEG encoders ignore it. The monitoring-time locks
and frozen capture options described above still apply.

An unsupported format falls back to PNG; invalid quality falls back to 90, while
the other valid value is retained. These fallbacks show a warning and do not
rewrite the saved values merely by loading them. An unreadable/malformed file or
duplicate preference key uses PNG/90 with a warning and preserves the original
file. An explicit option edit can save corrected values to an otherwise valid
file. Failed writes show a warning and retain the selected values for the current
session; restarting restores the last successfully saved values. Clear History
suppresses late warnings from edits made before the clear without cancelling the
settings write. Unrelated INI entries and comments are preserved.

## Reload settings

Use **Reload settings** under **Save settings** after pressing Stop, or before
starting monitoring. The command is unavailable during monitoring, Start/Stop
transitions, a folder picker, profile operations, or another reload. It never stops
monitoring automatically. It waits for already accepted reads and saves to finish
with their original captured options, then remains stopped.

Reload reads one `config.ini` snapshot for the folder, filename profiles/formula,
selected profile, language and image format/quality, and discovers `lang/*.json`
again. Added, edited, removed or invalid packs take effect without restarting.
The startup and reload paths share the same loader, validation, defaults and
warnings. Missing entries use fresh defaults; invalid individual values use their
normal fallback without keeping stale values. Invalid/unreadable INI uses defaults
with visible diagnostics and preserves the file. The existing unusable-folder
policy still writes its default fallback if possible. Unrelated INI data is kept.

Already submitted configuration writes complete before the read boundary. Avoid
editing files concurrently with outstanding app writes: those writes can still
persist their previously submitted values. During reload, configuration editing
is disabled and late write callbacks cannot restore old controls or notices.
A quality edit still waiting for its debounce, a folder field still being edited,
unsaved profile name/formula, or a failed persistence change requires confirmation:
**Discard and reload** applies file contents without saving those edits; **Cancel**
keeps the current edits/settings. Cancelled slider edits resume their ordinary
background persistence. Focusing the reload command does not first save a pending
folder edit; tabbing away without invoking restores ordinary folder persistence.

Session history, queue history, the preview and completed totals are retained.
The screen can show normal completions while remaining saves drain. Reload does
not clear history, reset filename counters by starting monitoring, delete images
or modify the Windows clipboard. Separately pressing Clear History during reload
keeps its existing generation rules, including suppression of old completion
results and late reload notices. A normal window close waits for an active reload;
closing during reload does not save its discarded/unconfirmed editor contents.
Future preferences must join this common loader when implemented; automatic-update
settings are not implemented in this snapshot.

## Filename formulas and user presets

Open **Filename rules and presets** under **Save settings** while stopped. Enter
a filename-stem **Formula** and inspect its **Example**. The default is
`Clipboard_$YYYY$MM$DD_$hh$mm$ss_$fff`, for example
`Clipboard_20261006_090305_123.png`. The chosen image format adds `.png`, `.jpg` or
`.bmp`; omit the extension from the formula.

The formula notation follows [PowerRename](https://learn.microsoft.com/windows/powertoys/powerrename):

| Variables | Meaning |
| --- | --- |
| `$YYYY`, `$YY`, `$Y` | Full year, last two digits, last digit |
| `$MMMM`, `$MMM`, `$MM`, `$M` | Month name, abbreviated name, padded/unpadded number |
| `$DDDD`, `$DDD`, `$DD`, `$D` | Weekday name, abbreviated name, padded/unpadded day |
| `$hh`, `$h`, `$mm`, `$m`, `$ss`, `$s` | Padded/unpadded hours, minutes and seconds |
| `$fff`, `$ff`, `$f` | Three, first two or first one millisecond digits |
| `${}` | Zero-based counter |
| `${start=10;padding=4;increment=2}` | Counter producing `0010`, `0012`, `0014`, etc. |
| `${rstringalnum=8}`, `${rstringalpha=8}`, `${rstringdigit=8}` | Random alphanumeric, alphabetic or digit strings |
| `${ruuidv4}` | Random version-4 UUID |

Multiple counters use the same accepted-capture index, with independent start,
padding and increment options. Start and increment accept signed 64-bit integers;
padding is 0–255, and random-string lengths are 1–255. These bounds do not override
the final Windows filename-length check. Use `$$` to insert a literal dollar sign.
Unknown expressions, path separators, control characters, reserved Windows
device names and trailing dots/spaces are rejected with an explanation. Components
including the extension must fit within 255 characters. A later suffix that would
exceed the limit is reported as a save failure. Other OS/path errors are also
reported instead of silently changing a rule.

Date variables use **local capture-acceptance time**, fixed before asynchronous
clipboard reads, rather than the later save time or an existing file's creation
date. Month/weekday names use invariant English and the Gregorian calendar.
Numbering starts afresh with each **Start**; Clear History does not reset it.
Rejected or failed captures can leave number gaps. Accepted images retain their
formula, timestamp and index through Stop, preset changes and later monitoring
runs. Viewing examples never advances actual counters or creates/reserves files.
Search/replace, regex renaming and EXIF/XMP variables are not supported.

Use **New Profile** to create a profile from the default filename formula, or
**Duplicate Profile** to copy the selected profile's saved formula into a new
profile with its own ID. Select a profile to restore its saved formula. Edit
**Profile name** and the formula, then choose **Save Preset**: it always updates
that selected profile, including a renamed profile, without an update prompt.
Changing the name never creates a profile. Names must be unique without case
sensitivity; a conflicting name shows an error and never replaces another profile.
New and duplicate profiles receive an available name automatically.

On a fresh start, **Default** is an ordinary editable, deletable profile; there
is no protected built-in entry. **Delete Profile** asks for confirmation and
selects another remaining profile. Deleting the last profile leaves an empty
collection, which stays empty after restart. Choose **New Profile** to continue;
Start, editing, saving, duplication and deletion require a selected profile.
Deleting a profile never deletes images or changes the clipboard or pending saves.
Controls are disabled while monitoring; Stop to edit them.

Profiles are saved immediately when created, duplicated, updated or deleted.
Selecting a profile remembers the selection and formula. The current valid formula
is also remembered on Start and normal close, even if its edits have not been
saved into the selected profile. **Save Preset** is the explicit update action;
Duplicate copies the saved formula, not unsaved editor changes. `config.ini`
stores `[Naming]` entries `Formula` and `SelectedPreset`, and `[NamingPresets]`
entries `Preset.<id>` containing JSON-encoded ID/name/formula records so Unicode,
quotes and INI punctuation round-trip safely. A persisted empty selection with no
profiles stays empty; a fresh configuration starts with an ordinary Default profile.
The file is still optional; no user configuration is bundled in the artifact.
Migration from earlier snapshot settings is not part of this change. Malformed
profile data is preserved with a warning and a usable default. Failed writes leave
changes available
only for the current session,
with a warning; they are not reported as durable.
Folder and preset writes share serialized atomic updates, retaining unrelated INI
entries/comments and the existing save-folder setting.

Existing files and directories are never replaced. If `name.png` is occupied,
the app tries `name (2).png`, `name (3).png`, and so on, choosing the lowest
available name. A fully encoded, uniquely named temporary image is moved into
place without replacement. Concurrent claims retry the next candidate without
re-encoding or exposing a partial final image. The new preset controls have a
practical initial position; final layout and visual polish are a later UI task.

## Window and processing behavior

The initial outer window targets **1300 × 860 physical pixels** and is centered
on the work area of the monitor containing the pointer. The work area excludes
the taskbar; the requested size is clamped to it. AppWindow and DisplayArea APIs
already use physical pixels, so the target is not multiplied by the DPI scale.
The manifest enables PerMonitorV2 awareness. XAML uses logical units: at 150%
scale, 1300 physical pixels correspond to roughly 867 logical units before window
chrome is accounted for. Settings stack below 850 logical units. Scrolling keeps
content accessible in small windows; resizing and maximizing remain enabled.

Clipboard access runs on a dedicated dispatcher thread. A separate background
worker encodes and saves images, then publishes results without awaiting the UI.
UI updates are batched every 250 ms, the virtualized list is bounded, and preview
decoding is limited to about 1024 × 768 pixels. Only the latest completed save is
previewed; preview errors do not change the save result.
Each clipboard change receives a session generation before any asynchronous read.
Clear advances that generation and resets presentation metadata without touching
the save queue. Results are checked when published and again when the UI drains
them. Preview success/error completions also check generation and latest-image
identity; an in-progress preview is cancelled on Clear. Old reads, saves and
preview loads cannot repopulate the screen or overwrite a newer preview.

Internal buffers are bounded. If images arrive faster than they can be read or
saved, the app reports a failure rather than silently discarding a capture.
Individual clipboard streams are limited to 128 MB and decoded images to 64
megapixels. Clipboard providers can invalidate data before it is read; a read
failure is reported with instructions to copy again. There is no app-specific
filter, queue management screen, tray integration, or automatic updater.

## Requirements

- An x64 PC running Windows 11 24H2 (build 26100) or 25H2 (build 26200).
  Windows 11 24H2 is the minimum supported desktop release for this boilerplate.
  Keep Windows updated and use an edition still in Microsoft support.
  Windows 10 is not included in this app's supported deployment targets.
- An interactive desktop session. ARM64 and x86 builds are not included.
- Keep every file and subfolder from the downloaded artifact together.

The app is **unpackaged**: no MSIX installation, signing certificate, or Developer
Mode is required. The publish folder includes the .NET runtime, Windows App SDK
runtime, and app-local x64 Visual C++ runtime DLLs. Visual Studio and separate
.NET, Windows App SDK, or Visual C++ runtime installations are not intended to be
required. Windows-provided system components remain OS prerequisites.

CI checks startup on a Windows hosted runner, which already has developer tools
installed. That check does not prove execution on a clean PC or actual on-screen
visibility. Final desktop verification should be performed on the target PC.

## Download and run

1. Open the repository's [Actions tab](https://github.com/blood72/ClipboardSnapper/actions).
2. Select **Build Windows x64** and a successful run for the desired commit.
   To build manually, choose **Run workflow** on the `main` branch.
3. Download the **ClipboardSnapper-win-x64** artifact from that run's summary.
   GitHub requires sign-in to download Actions artifacts; artifacts expire after
   14 days.
4. Extract the **entire ZIP** into a folder. Do not run the app inside the ZIP or
   copy only the executable.
5. Open the extracted folder and run **ClipboardSnapper.exe**. The monitoring
   screen opens in the **Ready** state; follow the usage steps above.

## Build from source

Pinned stable versions:

| Component | Version |
| --- | --- |
| .NET SDK (LTS) | 10.0.401 (`global.json`) |
| Target framework | `net10.0-windows10.0.26100.0` |
| Windows App SDK / WinUI 3 | 1.8.260921001 |
| Windows SDK BuildTools | 10.0.26100.4654 |
| Architecture | `win-x64` |

On Windows, install the pinned .NET SDK. The Windows XAML/resource build tools
come from NuGet. Visual Studio 2022 with C++ x64 build tools supplies the app-local
Visual C++ redistributable DLLs used by CI; editing the source does not require
Visual Studio. Run these commands from the repository root:

```powershell
dotnet restore ClipboardSnapper/ClipboardSnapper.csproj --locked-mode -p:Platform=x64
dotnet build ClipboardSnapper/ClipboardSnapper.csproj --configuration Release --no-restore -p:Platform=x64
dotnet publish ClipboardSnapper/ClipboardSnapper.csproj --configuration Release --no-restore -p:Platform=x64 --output artifacts/publish
```

To reproduce the complete downloadable folder, also perform the **Bundle Visual
C++ runtime** step in [the workflow](.github/workflows/build.yml). A plain
`dotnet publish` alone does not perform that extra app-local CRT copy. CI records
the bundled CRT file versions; they come from the hosted runner's installed
Visual Studio redistributable directory.

Both `SelfContained` and `WindowsAppSDKSelfContained` are enabled. Single-file
publishing, trimming, and ReadyToRun are disabled. NuGet dependencies are locked
in `ClipboardSnapper/packages.lock.json`; Actions are pinned to commit SHAs.
Dependency version pins do not freeze the hosted runner image.

`EnableMsixTooling` enables the template's XAML/PRI resource build targets;
`WindowsPackageType=None` keeps deployment unpackaged. Publish runs the incremental
build targets so the app's resource index is included in the output folder.

## CI validation

The workflow runs on `windows-2022` for pull requests, pushes to `main` and manual dispatches.
It restores locked dependencies, builds Release, publishes, bundles the CRT,
checks required output files and self-contained .NET configuration, and launches
the published executable. The smoke test checks that the process remains alive,
creates a main window handle, reports the expected title, and loads the .NET CLR
and WinUI native modules from the publish folder. It does not inspect
pixels or verify that a user can see the window. The complete publish folder is
also tested through Windows UI Automation: PNG/JPEG/BMP pixel round trips,
Start/Stop, preview/history, failure details, initial physical window bounds,
maximizing, and narrow layout reflow. This is a UI Automation check rather than a
screenshot or a multi-monitor/high-DPI visual review. Deterministic contract
tests also cover delayed clipboard-read/save
results, queued results, preview success/error races, concurrent/repeated clears,
post-clear captures, file preservation and frozen quality options. Windows tests
exercise the real `BitmapEncoder` at quality 1/90/100 and compare file sizes and
JPEG quantization tables, default 90 and white transparency. UI Automation checks
Clear while monitoring/stopped, fresh successes/failures, unchanged file hashes
and clipboard sequence, and JPEG quality visibility/editing restrictions.
The focused option-lock smoke test observes asynchronous Start with a temporary
configuration-file sharing lock, attempts disabled format/quality automation input
while monitoring and during a real blocked Stop, and checks editing recovery and
the next run's format. A gated real-encoder test checks that an accepted JPEG keeps
its original quantization tables after next-run format/quality edits.
Preference contracts cover persisted defaults, invalid/unavailable and missing
folders, Unicode paths, unrelated INI entries, malformed/read-only/write-failed
configuration and existing-file retention. UI Automation checks manual focus-loss
persistence and Browse selection/cancellation without Start or captures, restores
the path across process restarts from a different working directory, checks the
read-only configuration warning, and verifies persistent fallback to the default.
Image preference contracts cover all formats and quality limits, isolated invalid
value fallback, malformed/duplicate/read-only storage, and concurrent reads/writes
without torn format/quality pairs or lost folder/profile/language settings. Windows
UI Automation checks all format/quality-limit pairs across real process restarts
without captures, PNG/BMP quality retention, rapid edits, Start/Close flushing,
read-only session choices, generation-safe Clear, and external/missing/invalid
startup values without automatic rewriting.
Naming contracts verify all date/time variables, signed/multiple counters, random
variables, preview isolation, invalid Windows names, frozen capture metadata,
suffix gaps and occupied directories, concurrent non-overwriting moves, persistent
preset changes, and concurrent folder/preset configuration writes. Real Windows
encoding checks repeated names in all formats, complete output images and temporary
cleanup. UI Automation exercises explicit New/Duplicate, stable-ID updates and renaming
without prompts, conflicting-name rejection, deletion/cancellation, empty collections,
restart restoration, formula validation, locked monitoring controls,
counter behavior across Clear/Start and preserved files/settings.
Localization contracts check complete Korean keys, OS/explicit/fallback policy,
structured errors with unchanged diagnostics, live notifications, concurrent
language/folder/profile INI writes, malformed/read-only settings, custom JSON packs,
key fallback and malformed-file isolation. Windows UI Automation exercises Korean
controls, help/accessibility, validation and dialogs; PNG/JPEG/BMP saving; language
switches while monitoring with existing history; Clear History, file/clipboard
retention; translated failures with original diagnostics; restart/session-only
language choices; new/edited/removed JSON languages; and Korean narrow-window reflow.
Reload contracts exercise complete INI updates concurrent with repeated loads,
independent invalid/default recovery, folder fallback and live catalog replacement.
Published-app UI Automation checks monitoring/Stop restrictions, focused unsaved
folder/profile confirmation and cancellation, external settings, repeated/read-only
loads without rewriting, retained history/preview/totals/files/clipboard, accepted
save options, pending-write/Clear coordination, language pack changes and Korean
confirmation, and malformed/removed settings recovery.
Both JSON files are required publish contents and included in artifact verification.
The artifact is uploaded
only after these checks pass. CI then downloads that artifact and compares
every file's relative path and SHA-256 hash with the publish output, including a
file-count check.

Run deterministic session tests on any OS, or include real encoding on Windows:

```powershell
dotnet restore tests/Contracts/Contracts.csproj --locked-mode -p:EnableWindowsTargeting=true
dotnet run --project tests/Contracts/Contracts.csproj --framework net10.0 --configuration Release --no-restore
# Windows only:
dotnet run --project tests/Contracts/Contracts.csproj --framework net10.0-windows10.0.26100.0 --configuration Release --no-restore
```

These contract tests link the production session/queue/encoding source; they do not
require an additional test framework. Delayed operations use explicit completion
gates rather than timing assumptions. The UI Automation test drives the published
application, including a real delayed clipboard owner for Stop/draining and
Clear during acquisition; delayed preview completion is covered by the contract tests rather
than a claim of visual observation during a decode.

No Release publishing, Store registration, signing, or automatic deployment is
configured.

## References

- [Official WinUI app template](https://github.com/microsoft/WindowsAppSDK/tree/main/dev/Templates/Source/ProjectTemplates/Desktop/CSharp/SingleProjectPackagedApp)
- [Windows App SDK self-contained deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [.NET supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Windows App SDK supported Windows releases](https://learn.microsoft.com/windows/apps/windows-app-sdk/support)
