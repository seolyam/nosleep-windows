using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace nosleep_windows;

internal class CommandHandlers
{
    private const int MaxDurationSeconds = 365 * 24 * 3600;

    public static void HandleOn(string[] args)
    {
        var state = StateManager.LoadState();
        if (state != null)
        {
            if (IsDaemonRunning(state))
            {
                Console.WriteLine("nosleep is already running. Please run 'nosleep off' first, or 'nosleep status'.");
                return;
            }

            // A previous session ended without cleaning up (crash, kill, reboot). Its state file holds the
            // user's real original settings, so restore them before capturing "originals" again; otherwise
            // we would record our own overrides (e.g. lid = Do Nothing) as the values to restore later.
            Daemon.RestoreState(state);
            Console.WriteLine("Restored settings left over from a previous nosleep session that did not exit cleanly.");
        }

        string? duration = null;
        int? batteryThreshold = 20;
        bool keepDisplayAlive = true;
        bool requireAc = false;
        var customTargets = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg.StartsWith("--battery=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg.Substring("--battery=".Length).TrimEnd('%');
                if (!int.TryParse(value, out int threshold) || threshold < 0 || threshold > 100)
                {
                    Console.Error.WriteLine($"Invalid battery threshold: '{value}'. Use a percentage between 0 and 100.");
                    Environment.Exit(1);
                }
                batteryThreshold = threshold;
            }
            else if (arg.Equals("--no-battery", StringComparison.OrdinalIgnoreCase))
            {
                batteryThreshold = null;
            }
            else if (arg.StartsWith("--target=", StringComparison.OrdinalIgnoreCase))
            {
                string targetVal = arg.Substring("--target=".Length).Trim();
                foreach (var item in targetVal.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    // Process.ProcessName has no extension, so accept "MyApp.exe" as well as "MyApp"
                    customTargets.Add(item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? item[..^4] : item);
                }
            }
            else if (arg.Equals("--screen-off", StringComparison.OrdinalIgnoreCase))
            {
                keepDisplayAlive = false;
            }
            else if (arg.Equals("--require-ac", StringComparison.OrdinalIgnoreCase) || arg.Equals("--ac-only", StringComparison.OrdinalIgnoreCase))
            {
                requireAc = true;
            }
            else if (arg.Equals("--game", StringComparison.OrdinalIgnoreCase))
            {
                // Enabled by default; flag explicitly acknowledged
                keepDisplayAlive = true;
            }
            else if (arg.StartsWith("-"))
            {
                Console.Error.WriteLine($"nosleep: unknown option: '{arg}'. Run 'nosleep help' for usage.");
                Environment.Exit(1);
            }
            else if (duration != null)
            {
                Console.Error.WriteLine($"nosleep: duration given twice ('{duration}' and '{arg}').");
                Environment.Exit(1);
            }
            else
            {
                duration = arg;
            }
        }

        int? durationSeconds = null;
        if (duration != null)
        {
            int parsed = ParseDuration(duration);
            if (parsed <= 0)
            {
                Console.Error.WriteLine($"Invalid duration: '{duration}'. Use 3h, 90m, 45s, 1h30m, or a bare number of hours (max 365 days).");
                Environment.Exit(1);
            }
            durationSeconds = parsed;
        }

        // The watchdog would exit within seconds under these conditions; tell the user instead of silently doing nothing.
        var power = SystemInformation.PowerStatus;
        bool onBattery = power.PowerLineStatus == PowerLineStatus.Offline;
        if (requireAc && onBattery)
        {
            Console.Error.WriteLine("Charger is not connected, but --require-ac was given. Plug in and try again.");
            Environment.Exit(1);
        }
        if (batteryThreshold.HasValue && onBattery && BatteryPercent(power) <= batteryThreshold.Value)
        {
            Console.Error.WriteLine($"Battery is at {BatteryPercent(power)}%, at or below the {batteryThreshold}% threshold. " +
                                    "Plug in, lower --battery=N, or use --no-battery.");
            Environment.Exit(1);
        }

        var newState = new NoSleepState
        {
            ExpirationTime = durationSeconds.HasValue ? DateTime.Now.AddSeconds(durationSeconds.Value) : null,
            BatteryThreshold = batteryThreshold,
            RequireAc = requireAc,
            KeepDisplayAlive = keepDisplayAlive,
            TargetProcesses = customTargets
        };

        // Apply the power plan overrides here (records originals into newState), then save before
        // launching so the daemon finds the state. After launch only the daemon writes the file.
        PowerTweaks.Apply(newState);
        StateManager.SaveState(newState);

        string exePath = Environment.ProcessPath ?? "nosleep-windows.exe";
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "_daemon",
            // ShellExecute (unlike CreateProcess with UseShellExecute=false) does not inherit our handles.
            // An inherited stdout pipe would make anything capturing our output (scripts, `nosleep on | ...`)
            // wait forever for the long-lived daemon to exit.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process? daemonProc = null;
        try { daemonProc = Process.Start(psi); } catch { }

        if (daemonProc == null || !WaitForDaemonStartup(daemonProc))
        {
            Console.Error.WriteLine("Error: Failed to launch background daemon process. Original settings restored.");
            try { if (daemonProc != null && !daemonProc.HasExited) daemonProc.Kill(); } catch { }
            Daemon.RestoreState(newState);
            Environment.Exit(1);
        }
        newState.DaemonPid = daemonProc.Id;

        Console.WriteLine("==========================================================");
        Console.WriteLine("  nosleep-windows : ON");
        Console.WriteLine("==========================================================");
        Console.WriteLine(newState.OriginalAcLidAction.HasValue
            ? "- PC Sleep & Lid Close : System stays awake when laptop lid is closed"
            : "- PC Sleep & Lid Close : System stays awake (WARNING: could not change lid close action)");
        Console.WriteLine("- Game Protection: ACTIVE ");
        Console.WriteLine("  * Windows Power Throttling (EcoQoS) disabled on protected processes");
        Console.WriteLine("  * Wi-Fi Power Saving Mode : " + Applied(newState.OriginalAcWifiMode, "Maximum Performance"));
        Console.WriteLine("  * PCIe Link State (ASPM)  : " + Applied(newState.OriginalAcPcieAspm, "Off"));
        Console.WriteLine("  * CPU core parking        : " + Applied(newState.OriginalAcCoreParking, "All cores unparked"));
        Console.WriteLine("  * System timer resolution set to high-precision 1.0 ms");
        Console.WriteLine("  * Active network keepalive to prevent Wi-Fi router disconnects");
        Console.WriteLine(keepDisplayAlive
            ? "- Display Pipeline     : Kept active (safe for background rendering)"
            : "- Display Pipeline     : Screen power-off broadcast enabled (--screen-off)");

        Console.WriteLine(onBattery
            ? $"- Power Supply         : On Battery ({BatteryPercent(power)}%)"
            : "- Power Supply         : Plugged in (AC Power - Maximum performance unlocked)");

        if (requireAc)
        {
            Console.WriteLine("- AC Guard             : ACTIVE (Auto-sleeps immediately if charger is unplugged)");
        }

        if (newState.ExpirationTime.HasValue)
        {
            Console.WriteLine($"- Auto-off             : In {FormatDuration(TimeSpan.FromSeconds(durationSeconds!.Value))} (at {newState.ExpirationTime.Value:hh:mm tt})");
        }
        else
        {
            Console.WriteLine("- Auto-off             : Indefinite (stays on until 'nosleep off')");
        }

        if (batteryThreshold.HasValue)
        {
            Console.WriteLine($"- Battery Guard        : Auto-off when battery drops <= {batteryThreshold}%");
        }

        Console.WriteLine("==========================================================");
    }

    public static void HandleOff()
    {
        var state = StateManager.LoadState();
        if (state == null)
        {
            Console.WriteLine("nosleep is not currently running.");
            return;
        }

        Console.WriteLine("Restoring normal sleep behavior...");
        if (TryGetDaemon(state, out var proc))
        {
            using (proc)
            {
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                }
                catch { }
            }
        }

        Daemon.RestoreState(state);
        Console.WriteLine("nosleep is now OFF. All original power plan and sleep settings restored.");
    }

    public static void HandleStatus()
    {
        var state = StateManager.LoadState();

        if (state == null || !IsDaemonRunning(state))
        {
            if (state != null)
            {
                // Stale state cleanup
                Daemon.RestoreState(state);
                Console.WriteLine("Restored settings left over from a previous nosleep session that did not exit cleanly.");
            }
            Console.WriteLine("nosleep-windows : OFF (normal Windows sleep behavior)");
            return;
        }

        Console.WriteLine("==========================================================");
        Console.WriteLine("nosleep-windows : ON (PID: " + state.DaemonPid + ")");
        Console.WriteLine("==========================================================");
        Console.WriteLine("Lid close behavior  : " + (state.OriginalAcLidAction.HasValue
            ? "PC will NOT sleep when lid is closed"
            : "Unchanged (could not modify lid action)"));
        Console.WriteLine("Game Mode     : ACTIVE");
        Console.WriteLine("  * Timer precision : 1.0 ms (timeBeginPeriod active)");
        Console.WriteLine("  * Wi-Fi Power     : " + Applied(state.OriginalAcWifiMode, "Maximum Performance (Anti-disconnect active)"));
        Console.WriteLine("  * PCIe Link State : " + Applied(state.OriginalAcPcieAspm, "ASPM Off (No bus downclocking)"));
        Console.WriteLine("  * Display context : " + (state.KeepDisplayAlive ? "Kept active (DirectX/Chromium won't freeze)" : "Screen-off mode"));

        // Check running target processes
        var searchTargets = Daemon.GetTargetSet(state);
        var detected = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (searchTargets.Contains(p.ProcessName))
                    {
                        detected.Add($"{p.ProcessName} (PID {p.Id}, Priority: {p.PriorityClass})");
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        if (detected.Count > 0)
        {
            Console.WriteLine("Protected processes : " + string.Join(", ", detected));
        }
        else
        {
            Console.WriteLine("Protected processes : Waiting for protected processes to launch...");
        }

        if (state.ExpirationTime.HasValue)
        {
            TimeSpan rem = state.ExpirationTime.Value - DateTime.Now;
            if (rem.TotalSeconds > 0)
            {
                Console.WriteLine($"Auto-off in         : {FormatDuration(rem)} (at {state.ExpirationTime.Value:hh:mm tt})");
            }
            else
            {
                Console.WriteLine("Auto-off in         : Expiring now");
            }
        }
        else
        {
            Console.WriteLine("Auto-off            : Disabled (runs indefinitely until 'nosleep off')");
        }

        var power = SystemInformation.PowerStatus;
        string lineStatus = power.PowerLineStatus == PowerLineStatus.Offline
            ? "On battery"
            : "Plugged in (AC Adapter - Maximum Wattage Unlocked)";
        Console.WriteLine($"Power supply        : {lineStatus}");
        if (state.RequireAc)
        {
            Console.WriteLine("AC guard            : ACTIVE (Auto-sleeps immediately if charger is disconnected)");
        }

        if (state.BatteryThreshold.HasValue)
        {
            string current = power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery)
                ? "no battery detected"
                : $"currently {BatteryPercent(power)}%";
            Console.WriteLine($"Battery guard       : Auto-off at <= {state.BatteryThreshold.Value}% ({current})");
        }
        Console.WriteLine("==========================================================");
    }

    private static bool IsDaemonRunning(NoSleepState state)
    {
        if (!TryGetDaemon(state, out var proc)) return false;
        proc.Dispose();
        return true;
    }

    // Matches on our own process name rather than a hard-coded "nosleep" so a renamed exe still works,
    // while a recycled PID belonging to an unrelated process is still rejected.
    private static bool TryGetDaemon(NoSleepState state, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Process? proc)
    {
        proc = null;
        if (state.DaemonPid <= 0 || state.DaemonPid == Environment.ProcessId) return false;
        try
        {
            var candidate = Process.GetProcessById(state.DaemonPid);
            using var self = Process.GetCurrentProcess();
            if (!candidate.HasExited && candidate.ProcessName.Equals(self.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                proc = candidate;
                return true;
            }
            candidate.Dispose();
        }
        catch { }
        return false;
    }

    // The daemon records its PID in the state file once it has started; wait for that handshake
    // so a daemon that crashes on startup is reported instead of printing "ON".
    private static bool WaitForDaemonStartup(Process daemonProc)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (daemonProc.HasExited) return false;
            var current = StateManager.LoadState();
            if (current != null && current.DaemonPid == daemonProc.Id) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    private static string Applied(uint? original, string description) =>
        original.HasValue ? description : "Unchanged (not supported on this power plan)";

    private static int BatteryPercent(PowerStatus power) =>
        (int)Math.Round(Math.Clamp(power.BatteryLifePercent, 0f, 1f) * 100);

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds:00}s";
        return $"{span.Seconds}s";
    }

    // Accepts "3h", "90m", "45s", combinations like "1h30m", or a bare number of hours.
    // Returns 0 for anything invalid or out of range.
    private static int ParseDuration(string duration)
    {
        var match = Regex.Match(duration.Trim(), @"^(?:(\d+)|(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?)$", RegexOptions.IgnoreCase);
        if (!match.Success || match.Length == 0) return 0;

        long Part(int group) => match.Groups[group].Success && long.TryParse(match.Groups[group].Value, out long v) ? v : 0;
        // Cap each part before multiplying so absurd inputs cannot overflow
        long Capped(int group) => Math.Min(Part(group), MaxDurationSeconds);

        long total = match.Groups[1].Success
            ? Capped(1) * 3600
            : Capped(2) * 3600 + Capped(3) * 60 + Capped(4);

        return total > 0 && total <= MaxDurationSeconds ? (int)total : 0;
    }
}
