# OptiCore

**A Windows system platform that optimizes by measuring, and keeps every change reversible.**

OptiCore brings system analysis, cleanup, startup management, process management, game performance, Windows tweaks,
uninstalling, drivers, networking, security, storage, benchmarks and automatic maintenance together in one app.
It is built on three principles:

1. **Measure, don't guess.** Every finding is shown with the numbers behind it. The app explains how the health
   score is calculated. Before/after measurements and benchmarks show the real effect of a change.
2. **Everything is reversible.** Every change to the system is written to the *change journal* (with its old and
   new values) before it is applied. The Undo Center reverts a single change or everything, and checks against the
   live system whether recorded changes are still in effect.
3. **Be honest.** Every Windows tweak lists its evidence level (proven / situational / placebo / disputed), its
   risk, its trade-off and exactly what it changes in the registry. Operations with no measurable benefit or that
   do harm, such as registry "cleaning", deleting Prefetch or disabling Defender, are deliberately left out.

The interface is available in English and Turkish.

---

## Download

Get the latest version from the [Releases](https://github.com/mirsaidsahin/OptiCore/releases/latest) page:

- **OptiCore-Kurulum.exe** — installer ("Kurulum" is Turkish for "Setup"). Installs per user without admin rights
  and adds OptiCore to the Start menu and "Installed apps".
- **OptiCore.exe** — portable, runs without installing.

Windows 10/11 (64-bit). No .NET or other runtime needs to be installed.
The exe is not digitally signed, so Windows SmartScreen may show "Windows protected your PC" on first launch:
click "More info" → "Run anyway".

To try it safely first: `OptiCore.exe --dry-run` (scans everything, changes nothing).

---

## Modules

| Page | What it does |
|---|---|
| **Dashboard** | Health score (5 groups, weighted and explained), recommended actions, live CPU/RAM/GPU/disk/network charts, core map (P/E cores shown separately), top resource consumers, storage, battery and temperature. Customizable. |
| **Performance Intelligence** | Findings from all modules (evidence + expected impact + one-click fix), live **"Why is it slow right now?"** diagnosis (listens for 8 s; thermal throttling, memory pressure, single-core limit, disk latency… and the apps responsible), score and resource history, **before/after comparison**. |
| **System** | CPU (hybrid cores, cache), memory modules and upgrade path, graphics card (driver-free temperature/usage via D3DKMT), disks (SMART, with admin), network adapters, battery health and capacity history, thermals (throttling detection), motherboard/BIOS/TPM/Secure Boot. Report export (with serial numbers removed). |
| **Smart Cleanup** | 25+ categories (Windows, 6 browsers, Electron apps, game launchers, npm/pip/NuGet/Gradle/Cargo/Go caches) — measures first, shows you, then deletes. Duplicate files (SHA-256, hard links excluded), large files, empty folders, **project leftovers** (node_modules/bin/obj/.venv in old projects — verified against the project file). |
| **Startup** | Registry, Startup folder, Store apps, scheduled tasks, services. Boot impact is **measured** (Windows boot trace or OptiCore's post-boot measurement). Boot time history. **Startup guard**: notifies you when a new program adds itself to startup, ignoring version changes of the same item and signed updaters from well-known publishers. Suspicious entries (encoded PowerShell, running from Temp, double extensions…) are flagged. |
| **Windows Tweaks** | 51 tweaks (privacy, telemetry, performance, appearance, gaming, Explorer, updates), 4 presets, power plans. Notices when a Windows update reverts a tweak and offers to reapply it. |
| **Gaming** | Steam/Epic/GOG/Xbox/Riot libraries and Roblox, per-game profiles (power plan, priority, putting browsers into efficiency mode — restored when the game closes), automatic session detection, **FPS, frame time, 1%/0.1% lows and stutter count via ETW**, bottleneck analysis (GPU / CPU / single core / memory / thermals / FPS cap), background analysis, session history, in-game overlay. Hand-written notes and tips for **32 games** (ETS2, ATS, FS25, BeamNG, CS2, VALORANT, Fortnite, Roblox, GTA V, Cyberpunk 2077, Elden Ring, Forza Horizon, Flight Simulator…; the list lives in `GameKnowledge.cs`). Games are recognized by store ID or install folder name; profiles, FPS and bottleneck analysis also work for games not on the list. |
| **Processes** | Grouped / list / tree views, CPU/memory/IO/GPU, details (command line, signature, parent processes, connections), priority, **efficiency mode (EcoQoS)**, suspend, kill tree, process rules, per-app usage history, **memory leak** and sustained CPU usage warnings. |
| **Apps** | Desktop + Store apps, real size measurement, bulk and silent uninstall (MSI/Inno/Squirrel), leftover scan after uninstall (folders, registry, shortcuts — registry keys are backed up before deletion), bloatware detection, portable programs. |
| **Storage** | Treemap (drill-down), file types, largest files, disk health/SMART, free space trend and **"disk full" date forecast**, watched folders. |
| **Network** | Live traffic, Wi-Fi signal/standard/channel, latency, jitter and packet loss (router and internet measured separately), **DNS comparison** (raw UDP queries, uncached) and DNS switching, per-app connections, step-by-step connection diagnosis, repair tools. |
| **Drivers** | Inventory, problem devices (with error code explanations), outdated drivers, backup/restore with pnputil, official vendor pages. **Does not download drivers** (risk of the wrong driver). |
| **Security & Privacy** | Defender, firewall, UAC, SmartScreen, updates, Windows version support, Secure Boot, TPM, BitLocker, memory integrity, RDP, SMB1, accounts, proxy, hosts file. Network-exposed ports (with risk explanations), suspicious processes (signature, location) with SHA-256 → VirusTotal, **app permissions** (camera/microphone/location… last use and unused permissions), **browser extension audit**. Does not change security settings itself; it opens the relevant Windows page. |
| **Undo Center** | Change journal, live verification, revert a single entry / a whole operation / everything, restore points, export configuration as JSON. |
| **Diagnostics** | App crashes and hangs (faulting module and error code explanation), blue screens (bug check code → likely cause), unexpected shutdowns, event log analysis (known events explained), reliability index, Windows Update history, **app updates via winget**. |
| **Benchmark** | CPU (single/multi core), memory (bandwidth, latency), disk (uncached sequential/4K), graphics (WebGL), internet speed, **stability and thermal test** (stops itself at 98 °C). History and difference from the previous result. 1000 points = Core i5-1235U / DDR4-3200 / NVMe / Iris Xe. **"How good is this PC?"** places the CPU, GPU, memory and storage against well-known hardware on a 1–5 tier scale, with an overall tier, gaming expectations, the main bottleneck and links to online test results (built-in approximate table, about ±15%; your own measurements are used when available). |
| **Automatic Maintenance** | Scheduled tasks (analysis, cleanup, startup check, repairing reverted tweaks, restore point), run only when idle/plugged in, 3 ready-made profiles, alert rules, maintenance history, start at sign-in. |
| **Tools** | Context menu manager, locked file finder, secure delete (with SSD warning), file mover (reversible), **system snapshot and diff**, duplicate app/runtime detection. |
| **Overclocking** | Detects whether the CPU and GPU are unlocked, points to the right official tool (Intel XTU, AMD Ryzen Master, NVIDIA App, MSI Afterburner, Adrenalin), notices when memory runs below its XMP/EXPO speed, and adjusts Windows' processor boost policy reversibly. It never writes clock or voltage values itself. |

**One-click optimization:** The Dashboard button lists only safe steps (recommended cleanup, tweaks recommended for everyone, safe finding fixes), applies the ones you select, measures before/after and reverts all of them with one click.
**Alerts:** Notifications for overheating, memory pressure and low disk space, with adjustable thresholds.
**Updates:** The app checks GitHub releases at most once a day, downloads the new version, verifies it with SHA-256 and updates itself (can be turned off).
**Installer:** `OptiCore-Kurulum.exe` installs to `%LOCALAPPDATA%\Programs\OptiCore` without admin rights and adds OptiCore to the Start menu and "Installed apps". Silent install: `OptiCore-Kurulum.exe --quiet`, silent uninstall: `OptiCore.exe --uninstall --quiet`.

Also: English/Turkish interface, dark/light/system theme with 5 accent colors, **Ctrl+K command palette** (search
pages, tweaks and actions), notification center, system tray, single-instance, simulation mode.

---

## Architecture

```
src/
  Engine/        All system logic (.NET 10 library, independent of the UI)
    Core/        RPC router, jobs, data store, settings, notifications, bilingual text (LText)
    Journal/     Change journal: record → apply → verify → revert
    Platform/    Win32/WMI/PDH/registry/Task Scheduler/Authenticode helpers
    Native/      P/Invoke declarations
    Modules/     One module per feature; each adds its findings to Performance Intelligence
  App/           Desktop shell: WinForms + WebView2, tray, native dialogs, game overlay
    ui/          Interface (Preact + htm, no build step; all charts are hand-drawn SVG)
  Cli/           opticore-cli: call engine methods, and a development server for the browser
tests/           xUnit tests (the undo guarantee is tested against the real registry, in a sandboxed key)
```

The UI talks to the engine over a single message protocol: `{id, method, params, lang}` → `{id, ok, result|error}`.
On the desktop it travels over WebView2 messages; on the development server over local HTTP (127.0.0.1 only,
a per-run token, foreign Origin/Host rejected, the server refuses to run as administrator).
Long operations (scans, benchmarks…) start as jobs and the UI tracks their progress.

All state lives in readable JSON files under `%LOCALAPPDATA%\OptiCore`. No database, no telemetry;
the only internet traffic is the speed test, DNS test and update check, all started by the user or the settings.

## Building and running

Requirements: Windows 10/11 x64, [.NET 10 SDK](https://dot.net/download), WebView2 runtime (built into Windows 11).

```powershell
./build.ps1                    # build everything
./build.ps1 test               # run the tests
./build.ps1 run -DryRun        # start the desktop app in simulation mode
./build.ps1 run                # start the desktop app
./build.ps1 serve -DryRun      # develop the UI in a browser: http://127.0.0.1:5317
./build.ps1 publish            # single-file, self-contained build → ./release/OptiCore.exe
```

`release\OptiCore.exe` is a single file: the UI is embedded in the exe and extracted to the user's data folder on
first launch. It can be copied to a Windows 10/11 (x64) PC without .NET and run directly.

Development builds should be started through `build.ps1 run` so they can find .NET.

Calling any engine method from the command line:

```powershell
opticore-cli call system.hardware
opticore-cli call processes.top n=5 --warm 2000
opticore-cli call cleanup.scan --wait --dry-run
opticore-cli methods
```

**Simulation mode** (`--dry-run`): all scans and measurements are real, but nothing on the system changes;
actions are written to the change journal as "simulated". Recommended for a first try.

OptiCore starts as a standard user and only offers to restart as administrator when something needs it.

## Known limitations

These are known limits, either by design or because of what data Windows provides:

- **Temperatures:** OptiCore does not install a kernel driver, so it only reads sensors Windows exposes
  (ACPI thermal zones, the graphics card, disks in admin mode); per-core CPU temperature and, on most laptops,
  fan speed are not available this way. Throttling detection relies on the firmware-reported speed limit, not on
  temperature, and works on every system.
- **FPS measurement** requires admin rights (ETW) and covers DirectX 9–12 games. FPS is not measured for
  Vulkan/OpenGL games; the session is still recorded with CPU/GPU/temperature data. The in-game overlay shows in
  windowed and borderless modes, not in exclusive fullscreen.
- **SMART data, boot time history, restore points and the Windows boot trace** require admin rights.
- **The graphics benchmark** runs through WebGL; it is reliable for before/after comparisons on the same PC, but is
  not a one-to-one match for DirectX/Vulkan performance in games.
- **Hardware ranking** uses an approximate built-in table of well-known parts; unknown models fall back to your
  own benchmark results or are marked as unknown.
- **Per-app network traffic** cannot be measured in bytes (Windows doesn't expose it without admin); the number of
  connections and remote hosts is shown instead.
- Tested end to end on a real PC as a standard user and in simulation mode; the change journal's apply/revert chain
  is verified by tests against the real registry. Admin-only paths (FPS capture, SMART, restore points, driver
  backup) compile but have not yet been run with elevation.

## Third-party components

- [Preact](https://preactjs.com) 10.29.8 and [htm](https://github.com/developit/htm) 3.1.1 — `src/App/ui/vendor` (licenses included)
- [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2)
- [Microsoft.Diagnostics.Tracing.TraceEvent](https://www.nuget.org/packages/Microsoft.Diagnostics.Tracing.TraceEvent) — FPS capture
- [System.Management](https://www.nuget.org/packages/System.Management) — WMI
