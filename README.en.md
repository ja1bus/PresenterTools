# PresenterTools

English | [日本語](README.md)

PresenterTools is a Windows 11 tool that stays in the notification area and makes mouse actions, key presses and points of interest easy for your audience to follow in Teams screen sharing, on meeting-room displays and in IT training. Its overlays are transparent and never take your clicks, so you keep working in Excel, a browser or a terminal as usual.

This repository contains the source code of the publicly released PresenterTools.

## Features

- Cursor Highlight: a ring (or filled circle) around the cursor
- Click Effect: ripples where you left- or right-click (optionally with a button label and a mark while a button is held)
- Laser: draws the cursor trail like a laser pointer while a shortcut is held (or toggled on each press); the trail fades from the end
- Magnifier: a small window showing the area around the cursor at 200% / 300% / 400%, placed so it does not hide the cursor
- Key Display: shows typed text and shortcuts (such as `Ctrl+C`) in large letters on the screen (always off at startup)
- Each feature can be turned on and off on its own, and Master hides them all at once; control them from the control panel, the tray, global shortcuts or (optionally) mouse side buttons
- Colors, sizes, timings and shortcuts can be changed in Settings, with a live preview of the appearance
- English and Japanese UI (follows the Windows display language, or can be fixed in Settings)

**While Key Display is on, everything you type, including passwords, is shown on the screen.** Turn it off before typing anything you do not want to show. PresenterTools never sends what you type or your settings over the internet (see the privacy policy below).

## Supported OS and runtime

- Windows 11 (64-bit, x64)
- The Microsoft Store version includes .NET, so nothing else needs to be installed.
- To build and run from the source in this repository, the [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) is required.

## Installation

The official distribution channel is the Microsoft Store. Please get PresenterTools there (free).

- Microsoft Store: https://apps.microsoft.com/detail/9PP65NZK3TZ7

This GitHub repository does not distribute executables (binaries).

## Basic usage

1. When started, PresenterTools shows its control panel and an icon in the notification area.
2. Turn each feature on and off with the buttons on the control panel.
3. The default shortcuts are `Ctrl+Shift+F9` for Master on/off and `Ctrl+Shift+F10` to show the control panel. Other actions (such as drawing with the laser) are unassigned by default; assign them from "Settings" on the control panel.
4. When sharing in Teams, share your "Screen" (the whole monitor). If you share a single window, the PresenterTools effects are not visible to others.
5. The × on the control panel only hides it. To quit, choose "Exit" in the tray menu or on the control panel.

See the user guide for details.

- User guide: https://www.iwanaga.net/presentertools/en/manual/

## Building

### Requirements

- Windows 11 x64
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0) (verified: 8.0.425)
- Only the `dotnet` CLI is needed (Visual Studio is not required).
- The app itself has no NuGet package dependencies. Only the tests use the NuGet packages `Microsoft.NET.Test.Sdk`, `xunit` and `xunit.runner.visualstudio` (restored from nuget.org on the first build).

### Commands

Run from the repository root.

```powershell
dotnet build PresenterTools.csproj
dotnet test Tests/PresenterTools.Tests.csproj
dotnet run --project PresenterTools.csproj
```

`dotnet run` shows the control panel and keeps PresenterTools in the notification area.

To create the distributable folder (framework-dependent, win-x64), run the following. The output goes to `publish\FrameworkDependent\` and requires the .NET 8 Desktop Runtime (x64) to run.

```powershell
dotnet publish PresenterTools.csproj -c Release -p:PublishProfile=FrameworkDependent
```

The output also includes `LICENSE` and `THIRD-PARTY-NOTICES.md` (v1.4.0 and later; shown from "MIT License" and "Third-party notices" in the app's Settings window).

### Layout

| Path | Contents |
|---|---|
| `PresenterTools.csproj` | The app (WPF, `net8.0-windows`). Only the tray icon uses WinForms `NotifyIcon` |
| `AppHost.cs` | Wires input, state and display together; saves and applies settings |
| `Core/` | Logic independent of UI and Win32 (settings, shortcuts, state, key display, layout calculations), and UI strings (English `Strings.resx`, Japanese `Strings.ja.resx`) |
| `Input/` | Global shortcuts (`RegisterHotKey`) and low-level mouse and keyboard hooks |
| `Overlay/` | Click-through overlay windows (ring, ripples, laser, magnifier, key display) |
| `UI/`, `Services/`, `Interop/` | Control panel and Settings windows; monitor, tray and log services; Win32 API declarations |
| `Tests/` | Unit tests for `Core` (xUnit) |
| `tools/icon-source.png`, `Resources/icon.ico` | App icon (`pwsh -File tools/make-icon.ps1` creates `icon.ico` from `icon-source.png`) |

The tooling that builds the Microsoft Store package (MSIX) is not included in this repository. The `PresenterToolsMsix` condition in `PresenterTools.csproj` is for the Store version.

Settings are saved to `%LocalAppData%\PresenterTools\settings.json`, and errors are logged to `error.log` in the same folder.

Source comments are mostly written in Japanese. References such as "SPEC §N" (including item numbers like "D-N") in comments point to sections of the internal development specification, which is not included in this repository.

## License

Released under the [MIT License](LICENSE).

Copyright (c) 2026 ja1bus (© ja1bus)

For third-party software, see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## No warranty

This software is provided "as is", without warranty of any kind, express or implied. In no event shall the author be liable for any damages arising from the use of this software (see [LICENSE](LICENSE) for details).

## Links

- Web page: https://www.iwanaga.net/presentertools/en/
- User guide: https://www.iwanaga.net/presentertools/en/manual/
- Changelog: https://www.iwanaga.net/presentertools/en/changelog/
- Privacy policy: https://www.iwanaga.net/presentertools/en/privacy/
- Microsoft Store: https://apps.microsoft.com/detail/9PP65NZK3TZ7
- Contact: support@iwanaga.net
