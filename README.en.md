# Taskbar Music Controls

[中文](README.md) | **English**

A lightweight music controller and lyric display that lives on the Windows taskbar. It reads the current media session through Windows Global System Media Transport Controls (GSMTC) and draws playback controls, song info and lyrics directly into the taskbar — no desktop space used, no window switching.

## Features

**Media and playback**

- Shows the song title, artist and album artwork.
- Previous / play-pause / next, as far as the player's reported media session capabilities allow.
- Left-click the cover to bring up the player window, click again to send it back; middle-click the cover to copy `Title - Artist`.
- When several players are running, you can choose which one to follow.

**Lyrics**

- Line-by-line synced lyrics, with the original and the translation on two tracks.
- Configurable lyric sources: local files, NetEase Cloud Music, LRCLIB — looked up in a configurable order.
- A single song can be locked to one source, independent of the global setting.
- The lyric offset is stored per song, to correct timing errors between different lyric versions.

**Display**

- Three layouts: Full, Cover and controls, Cover only.
- Follows the taskbar's dark/light appearance automatically.
- Compresses into a more compact layout when the taskbar runs out of room, and expands again once there is space.

**Taskbar position**

- Can attach to the taskbar of any monitor.
- With centred Windows 11 icons the taskbar has two free gaps; pick one, then pick left- or right-aligned inside it.

**Tray and visibility**

- A tray icon in the notification area: left-click shows/hides the widget, right-click opens the full menu.
- Three ways to hide: manually, when nothing is playing, or after a pause timeout.

## Layouts

<img width="612" height="84" alt="image" src="https://github.com/user-attachments/assets/d86606ec-a80e-455d-9d88-4528991e1a54" />
<img width="612" height="84" alt="image" src="https://github.com/user-attachments/assets/c57f909d-c22b-4a53-a262-70e97c4e52c5" />
<img width="2560" height="84" alt="image" src="https://github.com/user-attachments/assets/39170e90-c827-454b-97fe-f72b75d06370" />
<img width="2560" height="84" alt="image" src="https://github.com/user-attachments/assets/68f5bed3-bc58-409a-b910-d75494a29516" />


| Layout | Content |
|---|---|
| **Full** (default) | Lyrics fill the whole area; on hover, playback controls, the progress bar, and the copy and translation buttons slide out from above. Shows the title and artist when there are no lyrics. |
| **Cover and controls** | Cover + a control column on the right, no lyrics. |
| **Cover only** | Just the album artwork; the play/pause button appears on hover. |


Lyrics are only shown in the **Full** layout: the other two have no lyric button and no room for a lyric panel. Switching layouts does not change the lyric toggle you already set, so switching back to Full brings the lyrics back.

## System requirements

- **Windows 11** (the platform it is mainly developed and tested on).
- Windows 10 is untested: the widget relies on the Windows 11 taskbar's UI Automation structure and may not be able to locate the taskbar on Windows 10.
- The player must provide a GSMTC media session to Windows (most do, including NetEase Cloud Music, Spotify and browsers); players without one cannot show media info or be controlled.
- Building from source needs the .NET 10 SDK; a release package needs nothing installed.

## Download and installation

Download a release package from the Releases page, extract it and run `TaskbarMusicControls.exe` — no installation needed:

| Package | Notes |
|---|---|
| `TaskbarMusicControls-v1.0.0-win-x64-standalone` | **Recommended.** The runtime is bundled, so the target machine needs nothing installed. |
| `TaskbarMusicControls-v1.0.0-win-x64-net10` | Smaller, but the target machine needs the .NET 10 Desktop Runtime preinstalled. |

- Only one instance may run at a time; a second launch is refused.
- To start automatically at login, right-click the widget (or the tray icon) → `Start with Windows`.
- The widget creates no desktop shortcut or Start menu entry; pin the exe to the taskbar / Start menu yourself if you want one.

## Usage

### Context menu

Right-click the widget or the tray icon to open it:

| Menu item | What it does |
|---|---|
| `Layout` | Switch between the three display layouts |
| `Lyric source` | `Lock to X` / `Locked to X`: lock the current song to a lyric source (click again to unlock). `Refresh lyrics (this song)`: look the current song up online again. `Local files first`: prefer local files. `NetEase → LRCLIB` / `LRCLIB → NetEase`: the lookup order of the two online sources |
| `Lyric timing (this song)` | Lyric offset for the current song: −1.0 s / −0.2 s / +0.2 s / +1.0 s, and reset |
| `Taskbar` | Choose which monitor's taskbar to attach to |
| `Block` | Choose which taskbar gap to use: left or right of the icons (only shown when the icons are centred) |
| `Alignment` | Left- or right-align inside the chosen gap |
| `Player` | Choose which player to follow when several exist (`Follow the current player` follows the system's current session) |
| `Visibility` | Hide manually, hide when nothing is playing, hide after a pause timeout; the current reason is shown while hidden |
| `Start with Windows` | Start automatically at login |
| `Exit Taskbar Music Widget` | Quit |

### Tray icon

- **Left-click**: show / hide the widget.
- **Right-click**: opens the same menu as the widget.
- The tooltip describes the state: `Taskbar Music Widget`, `… - by hand`, `… - no media`, `… - paused`, or `… - no room on the taskbar` when the taskbar has no space.

When the taskbar has no room for the widget, or the widget is hidden, the tray icon is the only way back to the settings.

### Mouse

| Action | Effect |
| ------------------------ | ------------------------------------------ |
| Left-click the cover | Bring up the player window; click again to send it back |
| Middle-click the cover | Copy `Title - Artist` |
| Right-click | Open the menu |
| Ctrl + wheel | Adjust the current song's lyric offset by ±0.2 s |
| Hover the text area in the Full layout | Slide out the control strip (progress bar, copy, translation, playback) |

### Hiding and showing

The three hide modes combine, highest priority first:

1. **Hide manually** (`Hide the widget`): stays hidden until you turn it back on.
2. **Hide when nothing is playing** (`Hide when nothing is playing`): hidden while no player provides a media session.
3. **Hide when paused** (`Hide when paused`): hidden after the chosen time paused (15 seconds / 30 seconds / 1 minute / 5 minutes).

Hiding only makes the widget transparent; it does not quit the app or stop following the player. Playback resuming or a left-click on the tray icon brings it back. Restoring a widget hidden "because nothing is playing" from the tray keeps it visible until playback starts again.

While hidden, the lyric sync and layout polling timers stop, to reduce idle overhead.

## Lyrics

### Local lyrics

Local lyrics live in:

```text
%LOCALAPPDATA%\TaskbarMusicControls\Lyrics
```

Both `.lrc` and `.txt` are supported, looked up under these names in order:

```text
Artist - Title.lrc
Title.lrc
Artist - Title.txt
Title.txt
```

Timed LRC files sync line by line; plain text can only be shown whole and does not follow the progress. Two consecutive lines with the same timestamp are shown as the original and the translation, one above the other:

```text
[00:12.00]Original lyric
[00:12.00]Translation
```

### Online lyrics

The online sources are NetEase Cloud Music and LRCLIB; their order follows the settings below:

- **Local files first (`Local files first` checked)**: local files first, then the **caches** of both online sources, and only when none of them has the lyrics are both APIs queried online.
- **Source priority (unchecked)**: each source is handled in order — its cache first, its API when the cache misses, and only then the next source.

A song locked with `Lock to X` uses only the locked source (cache, then API) and ignores the order above. `Refresh lyrics` skips the caches and queries again, but never deletes existing cache entries; if the lookup fails, the cached result is still used.

Online results are cached under a hash of "artist / title / album" in `%LOCALAPPDATA%\TaskbarMusicControls\Lyrics\Cache`, one file per source. The widget only touches the network when there are no local lyrics; no account or login is needed.

### Lyric offset

Different lyric files may run early or late as a whole, so each song can be adjusted individually:

- Right-click → `Lyric timing (this song)` to pick a step;
- or hold Ctrl and scroll the wheel over the widget, ±0.2 s per step.

The offset is stored per song, so each song keeps its own across track changes.

## Data and logs

All data lives in `%LOCALAPPDATA%\TaskbarMusicControls`:

| Path | Content |
|---|---|
| `settings.json` | All settings, see the table below |
| `Lyrics\` | Local lyric files; drop your own in here |
| `Lyrics\Cache\<hash>.<source>.json` | Online lyric cache, one file per source |
| `Lyrics\Cache\<hash>.meta.json` | Per-song state: lyric offset, locked source, last used source |
| `Logs\` | Diagnostic logs, see below |

`settings.json` fields:

| Field | Meaning |
|---|---|
| `DisplayMode` | Display layout: `Full` / `CoverAndControls` / `CoverOnly` |
| `TaskbarDeviceId` | The selected taskbar (monitor) |
| `Block` | Taskbar gap: `Left` / `Right` |
| `Alignment` | Alignment inside the gap: `Left` / `Right` |
| `TaskbarCentered` | Auto-detected taskbar icon alignment, read-only |
| `LyricsPreferLocal` | Whether local files come first |
| `LyricsOrder` | Online source order: `NeteaseFirst` / `LrclibFirst` |
| `LyricsMode` / `ShowTranslation` | Last lyric toggle and translation toggle |
| `HideWhenIdle` | Hide when nothing is playing |
| `ManualHidden` | Manual hide state |
| `PauseHideSeconds` | Seconds for the pause timeout, `0` disables it |
| `PlayerId` | The tracked player; empty follows the system's current session |
| `StartWithWindows` | Start at login |

This is the only settings file: reads and writes go only through it. A file without `Version` is ignored and the defaults are used.

Log files:

- `Logs\taskbar-layout.log`: taskbar layout detection and positioning.
- `Logs\playback-sync.log`: playback position corrections.
- `Logs\artwork.log`: cover reads and changes.

Debug builds record everything; release builds only record `ERROR` lines, so release logs are usually empty. Logs rotate automatically by size.

## Network and privacy

- The widget needs no account and uploads no data.
- Only when an online lyric source is enabled and neither local files nor the caches have a hit does it query a lyric service with the current title, artist, album and duration.
- Results are stored only in the local cache.

## Known limitations

- The widget merges into the taskbar by attaching its window to Windows Explorer's taskbar window; Windows has no general third-party taskbar widget API, so Windows updates or Explorer behaviour changes may affect compatibility.
- Attaching is a cross-process operation, and Windows blocks it between processes of different integrity levels: if Explorer runs as administrator, the widget must too, and vice versa. On a failed attach the widget shows a dialog instead of silently disappearing.
- The available taskbar area relies on a UI Automation scan; if Explorer does not expose the expected controls, the widget may fail to position or have to use a more compact layout.
- While the Start menu, notification centre or similar shell flyouts are open, Explorer temporarily stops exposing the taskbar as a top-level window. The widget stays visible and refreshes its layout once the flyout closes.
- When the monitor hosting the widget is turned off or Explorer restarts, the widget re-attaches to the taskbar that is still there, usually taking half a second to a second.
- Song info, playback position and control capabilities depend on what the player reports through GSMTC; support varies between players.
- Sync accuracy also depends on the lyric timestamps and the recording version.
- Release builds do not keep routine diagnostic logs; switch to a Debug build if you need to investigate a problem.

## Running from source

Open PowerShell in the repository root:

```powershell
dotnet run --project .\TaskbarMusicControls\TaskbarMusicControls.csproj
```

With Visual Studio, open `TaskbarMusicControls/TaskbarMusicControls.csproj`, pick the `Debug` or `Release` configuration and run.

## Build

```powershell
dotnet restore .\TaskbarMusicControls\TaskbarMusicControls.csproj
dotnet build .\TaskbarMusicControls\TaskbarMusicControls.csproj -c Release
```

## Publish

Create a self-contained publish folder for 64-bit Windows (the target machine needs no .NET Desktop Runtime):

```powershell
dotnet publish .\TaskbarMusicControls\TaskbarMusicControls.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\TaskbarMusicControls-v1.0.0-win-x64-standalone
```

Create a single-file publish folder that depends on an installed .NET Desktop Runtime:

```powershell
dotnet publish .\TaskbarMusicControls\TaskbarMusicControls.csproj -c Release -r win-x64 `
  --no-self-contained -p:PublishSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o .\TaskbarMusicControls-v1.0.0-win-x64-net10
```

Both publish folders are created in the project root:

- `TaskbarMusicControls-v1.0.0-win-x64-standalone`: **self-contained**, the runtime is bundled and the target machine needs nothing installed — use this one to distribute to users.
- `TaskbarMusicControls-v1.0.0-win-x64-net10`: **requires the .NET 10 Desktop Runtime**, smaller, but shows a "download a runtime" prompt on machines that do not have it.

## Credits and dependencies

The project pulls in no third-party NuGet packages. Lyrics and media data come from these services:

- NetEase Cloud Music: lyrics are fetched live through its unofficial web API (`music.163.com/api/search/get`, `music.163.com/api/song/lyric`); copyright belongs to NetEase Cloud Music and the related rights holders.
- LRCLIB (https://lrclib.net): a lyric source with a public lookup API.
- Windows GSMTC (Global System Media Transport Controls): used to read the current media session and playback state.

Thanks to the providers of these services and APIs.

## Disclaimer

1. This project is **for personal learning, technical research and exchange only**; any commercial use or large-scale data scraping is strictly prohibited.
2. The project distributes no audio files, lyrics or cover images to third parties; lyrics and covers are fetched live from the corresponding services, and only lyric text is cached locally for offline display. Their copyright belongs to NetEase Cloud Music, LRCLIB and the related rights holders.
3. Please delete the test data within 24 hours of downloading. Any legal dispute arising from misuse of this project is the responsibility of the user and has nothing to do with the author.
