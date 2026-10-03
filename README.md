# ClipboardSnapper

A C# / WinUI 3 desktop app that saves new clipboard images while monitoring is
enabled. The English screen shows monitoring controls, save settings, the latest
saved image, and recent successes and failures.

## Use the app

1. Choose a **Save folder** by entering an absolute path or selecting **Browse**.
   The default is `Pictures\ClipboardSnapper`. **Open Folder** creates and opens
   the selected directory.
2. Choose **PNG**, **JPEG**, or **BMP**, then press **Start**. The status changes
   from **Ready** to **Monitoring**. Existing clipboard content is not saved;
   copy a new image after starting. Text and file-list clipboard content are ignored.
3. A completed save shows **Image saved**, updates the preview, and adds a **Saved**
   row with its local save time. **Save failed** and **Failed** rows expose the
   reason; select **Details** for the full path, time, and exception message.
4. Press **Stop** to stop accepting clipboard changes. Images already being read
   or saved finish in the background. Stop before changing folder or format.

Settings and the latest 100 history rows are session-only. Files remain on disk.
Closing the window waits for accepted reads and writes to finish. JPEG images
are composited onto white because JPEG cannot store transparency. Failed writes
use temporary files and do not expose a partially written final image.

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

The workflow runs on `windows-2022` for pushes to `main` and manual dispatches.
It restores locked dependencies, builds Release, publishes, bundles the CRT,
checks required output files and self-contained .NET configuration, and launches
the published executable. The smoke test checks that the process remains alive,
creates a main window handle, reports the expected title, and loads the .NET CLR
and WinUI native modules from the publish folder. It does not inspect
pixels or verify that a user can see the window. The complete publish folder is
also tested through Windows UI Automation: PNG/JPEG/BMP pixel round trips,
Start/Stop, preview/history, failure details, initial physical window bounds,
maximizing, and narrow layout reflow. This is a UI Automation check rather than a
screenshot or a multi-monitor/high-DPI visual review. The artifact is uploaded
only after these checks pass. CI then downloads that artifact and compares
every file's relative path and SHA-256 hash with the publish output, including a
file-count check.

No Release publishing, Store registration, signing, or automatic deployment is
configured.

## References

- [Official WinUI app template](https://github.com/microsoft/WindowsAppSDK/tree/main/dev/Templates/Source/ProjectTemplates/Desktop/CSharp/SingleProjectPackagedApp)
- [Windows App SDK self-contained deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [.NET supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Windows App SDK supported Windows releases](https://learn.microsoft.com/windows/apps/windows-app-sdk/support)
