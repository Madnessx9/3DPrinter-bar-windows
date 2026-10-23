# Bambu Bar for Windows

**Your Bambu Lab printer, one click away in the notification area.**

This is the Windows port of [Bambu Bar](https://github.com/rolottr/bambu-bar) by rolottr, a macOS menu bar app. It shows your 3D printer's progress, temperatures and filament without opening Bambu Studio or Bambu Handy. Glance at the tray icon for progress, click it for everything else, or keep a widget on your desktop.

Built with C#, .NET 10 and WPF. No account, no cloud service, no analytics.

[Getting started](#getting-started) · [Features](#features) · [Widgets](#desktop-widgets) · [Differences from the Mac app](#differences-from-the-mac-app) · [Building](#building-from-source)

> Independent, community-developed software. Not affiliated with or endorsed by Bambu Lab. Printers that speak the Bambu LAN protocol (X1, P1, A1, H2 and P2S families) are supported, the same as the Mac app.

## Features

| | What you get |
| --- | --- |
| **Tray icon** | A printer glyph that fills as the print grows, with a pause or alert mark when the printer needs you. Choose to show the percentage or the time left in the icon instead; hover it for the job, progress, time left and finish time. |
| **Panel** | Click the icon: the job, its stage ("Heating hotend", "Paused: filament ran out"), percentage, layers, time left and finish time; nozzle, bed and chamber temperatures; AMS spools with what's left and the humidity; printer errors in plain English; pause, resume, stop, speed and chamber light. Frosted acrylic on Windows 11, like the system's own flyouts. |
| **Right-click menu** | Open the window, switch printers, refresh, pause, resume, stop, the chamber light, settings, check for updates, quit. |
| **Grams left** | Bambu Lab spools tell the AMS their net weight, so Bambu Bar shows filament left in grams (≈ 810 g), not just a percentage, and how much you have on hand in total. |
| **Named filament** | Bambu Lab spools show their colour by name (Grass Green, Charcoal). Other spools show the maker and product of the preset you set the slot to, such as SUNLU PLA Matte. |
| **3D printer models** | The same Blender models as the Mac app, as live 3D you can turn with the mouse (double-click to reset): the print grows layer by layer in the filament's colour, the bed drops (or the A1's gantry rises), the toolhead moves while printing, the AMS spools take their real colours and the chamber light follows the printer's. |
| **Knows your model** | From the printer profiles Bambu Lab publishes with Bambu Studio, so you only see what your printer can report: no chamber temperature on an A1 or P1S, no door or light on the A series, both nozzles on an H2D, heated-chamber targets on the H2 and X1E. |
| **Window** | Printer, Filament, Widgets, Settings and About pages, plus All Printers with more than one, on Windows 11's Mica backdrop. Follows Windows' light or dark mode. |
| **Many printers** | All of them watched at once. All Printers lists the ones that need you first (failed, paused), then by soonest finish. Choose which one the tray icon shows, or let it pick whichever is busy. |
| **Desktop widgets** | Small, medium and large, one per printer if you like, counting down between updates. |
| **Notifications** | Windows notifications when a print finishes, fails or is canceled, pauses (filament runout and the like), for new printer errors, and optionally when a print starts. Click one to see that printer. |
| **Easy setup** | Finds printers on your network and reads the access codes from Bambu Studio or OrcaSlicer on this PC, or from a settings file copied from another computer (Windows or Mac). When the slicer knows several printers, **Add All** sets them all up at once. |
| **Windows integration** | Open at sign-in (starts in the tray only), one copy at a time (opening it again shows the window), keyboard shortcuts, and it follows the printer when DHCP gives it a new address. |
| **Updates** | Checks GitHub once a day for a new release (turn it off in Settings) and offers the download. It never installs anything by itself. |

## Getting started

### Requirements

- **Windows 10 (version 1809) or Windows 11**, x64 or ARM64. Acrylic and Mica need Windows 11; Windows 10 gets solid backgrounds.
- A Bambu Lab printer on the same local network as the PC.

### Install and connect

1. Download `BambuBar-x64.exe` (or `BambuBar-arm64.exe`) from [Releases](../../releases) and put it somewhere permanent, such as a `Bambu Bar` folder in your user folder. It's one self-contained file; nothing to install. Or [build it yourself](#building-from-source).
2. Open it. The first time, **Windows Defender Firewall asks whether Bambu Bar may use the network: allow it on private networks.** Printers announce themselves over UDP, and without this the setup page can't find them. (You can still type the printer's IP address.)
3. The window lists the printers it finds; pick yours.
4. The **access code** usually fills itself in: if Bambu Studio or OrcaSlicer on this PC has used the printer, Bambu Bar reads the code from its settings in `%APPDATA%`. Slicer on another computer? Drop its `BambuStudio.conf` onto the setup page (on Windows it's in `%APPDATA%\BambuStudio`, on a Mac in `~/Library/Application Support/BambuStudio`). Otherwise read it off the printer: **Settings → Network** on its screen shows the 8-character **Access Code**.
5. Click **Connect**. The tray icon starts showing progress as soon as the printer reports in.

Windows hides new tray icons in the overflow (^). To keep Bambu Bar visible, drag its icon onto the taskbar, or turn it on in **Settings → Personalization → Taskbar → Other system tray icons**.

The access code is kept in **Windows Credential Manager** (as `Bambu Bar/<serial>`) and only ever sent to the printer. If it changes later and the slicer has the new one, Bambu Bar offers to use it.

Closing the window keeps Bambu Bar running in the tray. Started at sign-in, it stays in the tray; open it from the tray icon, or run BambuBar.exe again, to see the window. Pin it to Start or the taskbar for a quick way back.

### Unsigned downloads

The release builds are not code-signed, so Microsoft Defender SmartScreen may say "Windows protected your PC" the first time. Choose **More info → Run anyway**, or build it yourself.

### Controlling prints

Reading status works in any mode. Pause, resume, stop and speed changes are accepted only when the printer is in **LAN Only** mode with **Developer Mode** on (printer screen → Settings → Network). That's Bambu Lab's rule for third-party apps; without it the printer refuses the command and Bambu Bar says so. The chamber light works either way.

## Desktop widgets

- **Small:** a progress ring with the percentage, time left, the filament going in, and the finish time and layers. Paused, it says why; finished, how long ago and how long it took; idle, the temperatures and loaded spools.
- **Medium:** the printer with the part growing in the filament's colour, the job and its progress, finish time, layers and temperatures.
- **Large:** everything: temperature tiles, every AMS spool with the grams left, the newest error in plain words and how the last print went.

Windows has no widget API that apps like this can use, so Bambu Bar draws its widgets as small windows of its own. Add one from the **Widgets** page (or the tray's right-click menu → Widgets…), then drag it where you want it. It sits under your other windows like part of the desktop and comes back in the same place next time. **Right-click** a widget to choose its printer, size, or to keep it above other windows; click it to open that printer. The widgets need Bambu Bar running, which Open at sign-in takes care of.

## Differences from the Mac app

Everything the Mac app does, the Windows app does; a few things work the Windows way:

| macOS | Windows |
| --- | --- |
| Menu bar item with text beside the glyph | Tray icon. The **While printing, show** setting draws the percentage or time left in the icon itself (a tray icon is one small square); the tooltip always has the full line. |
| Menu bar panel | Flyout above the tray icon, acrylic on Windows 11. |
| App menu bar (Printer menu, Check for Updates…) | The tray icon's right-click menu. |
| WidgetKit widgets on the desktop and in Notification Center | Desktop widget windows (see above). |
| Keychain | Windows Credential Manager. |
| Login Items | The `Run` key in the registry. If you switch it off in **Settings → Apps → Startup** or Task Manager, Bambu Bar's settings say so and link there. |
| `~/Library/Application Support/Bambu Bar` | `%APPDATA%\Bambu Bar` (settings, print history, widget places). Set `BAMBUBAR_DATA_DIR` to use another folder. |
| `⌘` shortcuts | `Ctrl` shortcuts (below). |

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Ctrl+0` | All Printers (with more than one). |
| `Ctrl+1` – `Ctrl+5` | Printer, Filament, Widgets, Settings, About. |
| `Ctrl+R` | Ask the printer for a full report. |
| `Ctrl+L` | Turn the chamber light on or off. |
| `Ctrl+,` | Settings. |
| `Ctrl+W` | Close the window (Bambu Bar keeps running in the tray). |

In the tray panel: `Ctrl+O` opens the window, `Ctrl+Q` quits, `Esc` closes the panel.

## How it works

Bambu printers run an MQTT broker on port 8883. Bambu Bar connects over TLS 1.2, signs in as `bblp` with the access code, and accepts the printer only if its certificate chains to Bambu Lab's own certificate authorities, which ship with the app, *and* is issued to the printer's serial number, so no other device on the network can collect the code. It asks once for a full report and then merges the changes the printer sends about once a second. Printers announce themselves on UDP port 2021; that's how Bambu Bar lists them during setup and notices when one moves to a new address.

## Privacy and local storage

No account, no analytics, no cloud. Bambu Bar talks to your printers on your local network. Its only other request is the optional daily update check, which reads this project's latest release from the GitHub API and sends nothing about you or your printers.

- **Printers, preferences, window and widget places:** `%APPDATA%\Bambu Bar\settings.json`.
- **Access codes:** Windows Credential Manager.
- **Last prints** (only what the app saw while running): `%APPDATA%\Bambu Bar\history.json`.

## Building from source

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download). Windows to run it; the core library and its tests also build and run on Linux and macOS.

```powershell
git clone https://github.com/Precog29/bambu-bar-windows.git
cd bambu-bar-windows
dotnet build BambuBar.slnx                                  # debug build
dotnet run --project src\BambuBar                           # run it
dotnet publish src\BambuBar -c Release -r win-x64 -o publish # one self-contained BambuBar.exe
dotnet test tests\BambuBar.Core.Tests                       # unit tests, plus live runs against the emulator
```

The emulator tests need `mosquitto` and `openssl` on the PATH (on Linux, `apt install mosquitto mosquitto-clients`); without them they're skipped.

The [GitHub Actions workflow](.github/workflows/build.yml) runs the tests on Linux, builds on Windows, renders snapshots of every page in light and dark from the demo printer (download them from the run's artifacts), and publishes `BambuBar-x64.exe` and `BambuBar-arm64.exe`. Pushing a `v*` tag attaches them to a release.

### Layout

- `src/BambuBar.Core`: everything that isn't Windows UI: the MQTT client and TLS check, report parsing, discovery, slicer import, the printer store, history, settings, updates, the demo printer. Ported from the Mac app's Swift sources and tested on any OS.
- `src/BambuBar`: the WPF app: tray icon, flyout, window and pages, widgets, 3D models, notifications, Credential Manager, start at sign-in.
- `tests/BambuBar.Core.Tests`: xUnit tests, including TLS/MQTT runs against `scripts/emulator`.
- `tools/convert-models.py` converts the Mac app's USDZ printer models to the meshes the app loads; `tools/generate-icon.py` draws the app icon.

### Developing without a printer

- `--demo [printing|preparing|paused|finished|failed|idle|offline|unconfigured]` runs a simulated P2S; add `--fleet` for six printers. Nothing is saved and no network is used. `--dark` and `--light` force either look.
- `scripts/emulator/printer.py` runs a pretend P2S on `127.0.0.1:8883` (needs mosquitto): a TLS MQTT broker that replays a real P2S report, then prints, heats and obeys pause, resume, stop, speed and light. Connect with `BambuBar.exe --connect 127.0.0.1 22E00EMULATOR0 12345678 --insecure-tls`.
- `scripts\fleet\run.ps1` starts a mock fleet of 12 printers in Docker Desktop and opens Bambu Bar against it with `--emulator`. `-Import` starts on the Add Printer page to try **Add All**, `$env:SSDP=1` has the printers announce their names and models, `-Stop` stops it.
- `--probe <ip> <serial> <access-code|saved> [seconds] [--raw-filament]` connects to a printer from a terminal and prints what it reports (`saved` uses the code Bambu Bar saved). Pipe it to see the output wait for the end: `BambuBar.exe --probe 192.168.1.50 01P00A000000000 saved | Out-Host`.
- `--snapshot <dir>` (with `--demo`) writes PNGs of the panel, widgets, every page and the tray icon's states, then quits.

## Credits and license

[Bambu Bar](https://github.com/rolottr/bambu-bar) was created by [rolottr](https://github.com/rolottr); this Windows port follows its design and sources. Released under the [MIT License](LICENSE).

Protocol details, a real P2S report and the English HMS error texts come from [ha-bambulab](https://github.com/greghesp/ha-bambulab) (MIT), which also provided Bambu Lab's certificate authorities that ship with the app. [Bambuddy](https://github.com/maziggy/bambuddy) and [bambu-cli](https://github.com/tobiasbischoff/bambu-cli) helped map the newer printers, and the access-code import follows [Bambu Studio](https://github.com/bambulab/BambuStudio)'s settings format. Each model's capabilities follow the printer profiles in Bambu Studio.

The printer models were built for the Mac app in Blender from published outer dimensions; they are visual approximations, not manufacturer CAD, and carry no logos.

Bambu Lab, Bambu Studio, Bambu Handy, AMS and P2S are trademarks of Bambu Lab. This project is not affiliated with Bambu Lab.
