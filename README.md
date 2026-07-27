# nosleep-windows

A lightweight Windows command-line utility to temporarily disable system sleep and monitor power-off, even when you close the laptop lid! It allows you to run long background tasks without having to change your power plan settings.

## Features

- **Prevent Sleep**: Stops your PC from sleeping or turning off the screen.
- **Lid Close Support**: Keeps the system awake even if you close your laptop lid.
- **Auto-Off Timer**: Automatically restores normal sleep behavior after a given duration (defaults to 3 hours).
- **Battery Protection**: Automatically restores normal sleep behavior if your battery level drops below a configurable threshold (defaults to 20%).

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (or runtime to run it)
- Windows OS

## Build

To build the project locally, open a terminal in the project directory and run:

```powershell
dotnet build -c Release
```

The executable will be located at `bin/Release/net10.0-windows/nosleep-windows.exe`.

## Usage

This is a command-line application (CLI), which means it should be executed from a **Command Prompt** or **PowerShell** window.

```text
Usage: nosleep-windows <on [DURATION] [--battery=N|--no-battery] | off | status>

  on [DURATION]   Disable sleep (survives lid close), auto-off after DURATION.
                  DURATION defaults to 3h. Accepts 3h, 90m, 45s, or bare hours.
                  Also auto-offs at 20% charge while on battery power.
     --battery=N  Move that threshold (N is 1-99)
     --no-battery Turn it off — auto-off on DURATION alone
  off             Restore normal sleep behavior now
  status          Show current state and time remaining

  -h, --help      Show this help
```

### Examples

**Turn on "no sleep" mode for 3 hours (default)**
```powershell
.\nosleep-windows.exe on
```

**Turn on for 90 minutes and auto-off if battery reaches 15%**
```powershell
.\nosleep-windows.exe on 90m --battery=15
```

**Check the background daemon status**
```powershell
.\nosleep-windows.exe status
```

**Turn it off early (restore normal behavior)**
```powershell
.\nosleep-windows.exe off
```

## How it works

When you run `nosleep-windows on`, a tiny background process (daemon) is spawned. This daemon uses Windows power management APIs (`SetThreadExecutionState` and `PowerRegisterSuspendResumeNotification` / Lid Actions) to enforce wakefulness. The background daemon gracefully exits and restores all original system behavior once the timer expires, the battery threshold is hit, or you explicitly run `nosleep-windows off`.
