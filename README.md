# BearMinVLC

A sleek, minimal video player for Windows, built on VLC's playback engine (libVLC).
Dark, quiet, and out of the way of the video — VLC's format support without VLC's interface.

## Install

Download **[BearMinVLC-win-Setup.exe](https://github.com/bearminimumdev/BearMinVLC/releases/latest/download/BearMinVLC-win-Setup.exe)**
from the latest release and run it. It installs for your user account in a few seconds (no admin prompt),
adds Start menu and desktop shortcuts, and registers BearMinVLC as a video player — pick it in
**Settings → Apps → Default apps** to open videos with it. Nothing else is needed: .NET and VLC's engine are
bundled.

**Updates are automatic.** On launch the app checks this repo's releases, downloads a newer version in the
background, and installs it when you close the app (or right away with "Restart now").

Uninstall from **Settings → Apps → Installed apps**. Your recent list and settings in `%AppData%\BearMinVLC` are kept.

## Build it

```
dotnet build src/BearMinVLC -c Release
dotnet publish src/BearMinVLC -c Release -o publish
```

## Release a new version

Bump nothing by hand — the script stamps the version:

```
./scripts/release.ps1 -Version 0.3.0 -Notes "What changed"
```

It builds a self-contained copy, packs it with [Velopack](https://velopack.io) (installer, portable zip, full and
delta update packages), and publishes the GitHub release that installed copies update from. It needs the GitHub CLI
signed in (`gh auth login`) and refuses to run with uncommitted changes.

## Using it

- **Open:** drag a file onto the window, `Ctrl O`, `Ctrl U` for a stream URL, or pick from Recent.
- **Mouse:** click the video to pause · double-click for fullscreen · scroll for volume · drag to move the
  window · right-click for more (always on top, aspect ratio, screenshot, close video).
- **Resume:** files pick up where you left off (between 30 s in and 95% through), with the subtitle and audio
  track you chose. A "Start over" button appears for 5 seconds.
- **Settings** (`Ctrl ,`, the right-click menu, or the link at the bottom of the start screen): resume on/off,
  skip length, hardware decoding, deinterlacing, subtitle size and position, preferred subtitle/audio language,
  and audio output device. Subtitle, decoding and deinterlacing changes restart the current video at the same spot.
- Press `?` in the app for every keyboard shortcut.

| Key | Action | Key | Action |
|---|---|---|---|
| Space | Play / pause | ↑ ↓ | Volume |
| ← → | Skip 10 s (Shift: 3 s, Ctrl: 1 min) | M | Mute |
| 0–9 | Jump to 0–90% | V / B | Next subtitle / audio track |
| . | Next frame | G H / J K | Subtitle / audio delay ±50 ms |
| [ ] | Slower / faster | Backspace | Normal speed |
| F | Fullscreen | Esc | Close menu / exit fullscreen |
| T | Always on top | S | Screenshot (saved to Pictures\BearMinVLC) |
| Ctrl W | Close video | Ctrl O / Ctrl U | Open file / URL |
| Ctrl , | Settings | ? | Shortcut sheet |

Recent files, thumbnails and settings live in `%AppData%\BearMinVLC`. The installed app itself lives in
`%LocalAppData%\BearMinVLC`.

## How it's put together

- **Avalonia 12.1.4** for the UI, **LibVLCSharp 3.10.1** + **libVLC 3.0.24** for playback. Versions are pinned:
  LibVLCSharp.Avalonia's overlay breaks across Avalonia upgrades, so upgrade them together and test.
- libVLC draws video into a native window; all controls live in a transparent overlay window that LibVLCSharp
  keeps on top of it (`PlayerView`). `MainWindow` only hosts the video surface.
- `src/BearMinVLC/Views/PlayerView.axaml(.cs)` — every screen and behaviour.
- `src/BearMinVLC/Services/` — the libVLC wrapper, recent/resume storage, settings, Win32 helpers, the updater
  (`Updater.cs`) and Windows "Open with" registration done by the installer (`ShellIntegration.cs`).
- `installer/` — the splash image the installer shows. `scripts/release.ps1` — the release pipeline.
- `design/` — the approved mockups and logo files.

### Checking the UI without a person

Debug builds include a screenshot harness that generates a test clip, walks every screen, clicks and drags with
the real mouse, and writes PNGs plus a log:

```
dotnet build src/BearMinVLC
src\BearMinVLC\bin\Debug\net10.0-windows\win-x64\BearMinVLC.exe --shots artifacts\shots --gen
```

It uses a throwaway data folder, so it never touches your real Recent list. It moves the mouse, so don't use
the PC while it runs (about 30 seconds).

## Known limitations (v1)

- Windows 11 Snap Layouts don't pop up when hovering the maximize button (the button lives in the overlay
  window). Win+Z and dragging the title bar to a screen edge still work.
- Subtitles sit behind the control bar while the controls are showing, unless Settings → Subtitles → Position is
  set to "Above controls".
- Skip length is set in Settings (left/right arrows and the skip buttons). Shift and Ctrl skips stay at 3 s / 1 min.
- 64-bit Windows only. No playlist or media library yet.

## Licences

libVLC and LibVLCSharp are LGPL-2.1; the app links them dynamically. Geist fonts are SIL OFL 1.1
(`licenses\OFL-Geist.txt` in the build output). Avalonia is MIT.
