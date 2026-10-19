# nosleep-windows

A high-performance Windows command-line utility to temporarily disable system sleep, prevent internet drops, eliminate background throttling, and keep games and long-running apps running at full speed with the laptop lid closed!

---

## Why this exists (Running with the Lid Closed)

When running games or long-running apps on a laptop and closing the lid, Windows normally causes several critical issues:
1. **Screen Off Pausing / Freezing**: Windows broadcasts monitor-off messages, causing Chromium / WPF render loops and JavaScript timers to slow down to 1 FPS or freeze completely.
2. **Game Disconnects**: Wi-Fi adapters switch to low-power sleep modes (802.11 PS-Poll), causing ping spikes and packet loss that trigger socket timeouts and dropped connections.
3. **Power Throttling (EcoQoS)**: Windows 10/11 flags background windows and shoves app threads onto low-frequency Efficiency cores (E-cores) while relaxing timer resolution from 1ms to 15.6ms+.
4. **DirectX / Render Crashes**: Direct3D surfaces often encounter device loss when the display pipeline is disconnected, causing apps to crash.

**nosleep-windows solves all of these issues out of the box.**

---

## Features

- **Survives Lid Close**: Automatically sets Windows lid action to "Do Nothing" while active, and cleanly restores original settings when finished.
- **Active Game Protection**:
  - **No Display Pipeline Disconnect**: Keeps DirectX and Chromium animation/timer loops running at full speed without crashing or pausing. (The physical laptop backlight turns off naturally via hardware).
  - **Anti-Throttling (EcoQoS Disabled)**: Strips Windows 11/10 Power Throttling from protected processes and boosts their scheduling priority to `AboveNormal`.
  - **Automatic Process Detection**: Automatically identifies and protects common game processes. Custom processes can also be specified via `--target=NAME`.
  - **High-Precision 1.0 ms Timer**: Engages `timeBeginPeriod(1)` to prevent script loop delays and timer quantization jitter.
  - **Wi-Fi Maximum Performance**: Forces Wi-Fi power saving mode to `0` (Maximum Performance) on both AC and Battery (DC) to prevent socket timeouts and connection loss.
  - **PCIe ASPM Disabled**: Disables Link State Power Management on PCIe bus to eliminate latency spikes to the Wi-Fi card and GPU.
  - **Active Network Keepalive**: Sends periodic background network probes every 30 seconds to prevent Wi-Fi router AP sleep and NAT session timeouts.
  - **CPU Frequency Protection**: Boosts DC minimum processor state so the CPU does not downclock to 5% on battery.
- **Auto-Off Timer**: Specify a custom duration (e.g. `3h`, `90m`, `45s`, `1h30m`) or let it run indefinitely.
- **Battery Guard**: Automatically restores normal sleep behavior if battery falls below a safe threshold (defaults to 20%).
- **Clean Restoral**: Restores all original power plan settings (Lid action, Wi-Fi mode, PCIe ASPM, CPU states) upon exit or when running `nosleep off`.
  - Settings are also restored on logoff/shutdown and if the daemon crashes. If the daemon is killed outright, the next `nosleep on` or `nosleep status` detects the leftover session and restores the originals.
  - Settings that cannot be changed on your power plan are left untouched and reported as "Unchanged".

---

## Quick start (no terminal needed)

Double-click `nosleep-windows.exe`. A small window opens with a **Turn ON / Turn OFF** button, a duration picker, the battery guard, and an optional list of extra apps to protect. The window shows live status; you can close it and nosleep keeps running in the background. Open it again any time to turn it off.

To get the exe, build it once (below) and copy `dist
osleep-windows.exe` wherever you like (Desktop, a pinned taskbar shortcut, etc.). It is a single self-contained file and does not need .NET installed.

---

## Build

To build the project locally, open a terminal in the project directory and run:

```powershell
.\.dotnet\dotnet.exe build -c Release
```

The executable will be located at `bin/Release/net10.0-windows/nosleep-windows.exe`.

To produce a single standalone exe (no .NET install required) in `dist\`:

```powershell
.\.dotnet\dotnet.exe publish -c Release -r win-x64 -o dist
```

---

## Usage

Run `nosleep-windows.exe` from PowerShell or Command Prompt:

```text
Usage: nosleep <on [DURATION] [OPTIONS] | off | status>

Commands:
  on [DURATION]     Disable sleep (survives lid close) and activate game anti-slowdown mode.
                    DURATION accepts 3h, 90m, 45s, 1h30m, or bare hours. Defaults to indefinite.
  off               Restore normal sleep behavior and original power plan settings.
  status            Show current status, timer remaining, and protected game processes.

Options for 'on':
  --require-ac      Auto-sleep immediately if AC power is disconnected (charger unplugged).
  --battery=N       Auto-off when battery <= N% (default: 20%).
  --no-battery      Disable battery threshold (stay awake on battery).
  --target=NAME     Add custom process names to protect (e.g. --target=MyApp,Other.exe).
                    Common game processes are protected by default.
  --screen-off      Broadcast monitor-off message on lid close (not recommended for games).
                    By default, the display pipeline is kept active to prevent game lag/crashes.
```

---

## Examples

### 1. Start NoSleep (Indefinite)
```powershell
.\nosleep-windows.exe on
```
Keeps your laptop running with the lid closed, keeps protected apps unthrottled, forces Wi-Fi to max performance, and auto-offs only if battery hits 20%.

### 2. Run for 2 Hours with Custom Battery Threshold
```powershell
.\nosleep-windows.exe on 2h --battery=15
```

### 3. Check Live Status & Protected Processes
```powershell
.\nosleep-windows.exe status
```
Output:
```text
==========================================================
nosleep-windows : ON (PID: 8844)
==========================================================
Lid close behavior  : PC will NOT sleep when lid is closed
Game Mode     : ACTIVE
  * Timer precision : 1.0 ms (timeBeginPeriod active)
  * Wi-Fi Power     : Maximum Performance (Anti-disconnect active)
  * PCIe Link State : ASPM Off (No bus downclocking)
  * Display context : Kept active (DirectX/Chromium won't freeze)
Protected processes : MyApp (PID 14320, Priority: AboveNormal)
Auto-off in         : 1h 45m (at 04:30 PM)
Battery guard       : Auto-off at <= 15% (currently 85%, on AC adapter)
==========================================================
```

### 4. Stop NoSleep (Restore Normal Windows Sleep)
```powershell
.\nosleep-windows.exe off
```
Restores original lid actions, Wi-Fi power saving mode, and default Windows sleep settings immediately.
