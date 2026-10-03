# ClipboardSnapper

A minimal C# / WinUI 3 desktop app. It opens a window titled **ClipboardSnapper**
and displays the app name. No capture, clipboard monitoring, tray integration,
hotkeys, settings, or automatic updates are implemented.

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
5. Open the extracted folder and run **ClipboardSnapper.exe**. A basic window
   titled **ClipboardSnapper** should appear with the app name in its center.

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
uploaded only after these checks pass.

No Release publishing, Store registration, signing, or automatic deployment is
configured.

## References

- [Official WinUI app template](https://github.com/microsoft/WindowsAppSDK/tree/main/dev/Templates/Source/ProjectTemplates/Desktop/CSharp/SingleProjectPackagedApp)
- [Windows App SDK self-contained deployment](https://learn.microsoft.com/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [.NET supported operating systems](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Windows App SDK supported Windows releases](https://learn.microsoft.com/windows/apps/windows-app-sdk/support)
