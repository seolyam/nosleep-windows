using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace nosleep_windows;

internal class CommandHandlers
{
    private const int MaxDurationSeconds = 365 * 24 * 3600;

    public static void HandleOn(string[] args)
    {
        var state = StateManager.LoadState();
        if (state != null && state.DaemonPid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(state.DaemonPid);
                if (!proc.HasExited && proc.ProcessName.Contains("nosleep", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("nosleep is already running. Please run 'nosleep off' first, or 'nosleep status'.");
                    return;
                }
            }
            catch
            {
                // Process doesn't exist, clean up stale state and proceed
                StateManager.ClearState();
            }
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

        var (originalAc, originalDc) = PowerManager.GetLidCloseActions();

        var newState = new NoSleepState
        {
            OriginalAcLidAction = originalAc,
            OriginalDcLidAction = originalDc,
            ExpirationTime = durationSeconds.HasValue ? DateTime.Now.AddSeconds(durationSeconds.Value) : null,
            BatteryThreshold = batteryThreshold,
            RequireAc = requireAc,
            KeepDisplayAlive = keepDisplayAlive,
            TargetProcesses = customTargets
        };

        // Save state early so daemon can find it
        StateManager.SaveState(newState);

        // Launch daemon
        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "nosleep-windows.exe";
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "_daemon",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var daemonProc = Process.Start(psi);
        if (daemonProc != null)
        {
            newState.DaemonPid = daemonProc.Id;
            StateManager.SaveState(newState);

            Console.WriteLine("==========================================================");
            Console.WriteLine("  nosleep-windows : ON");
            Console.WriteLine("==========================================================");
            Console.WriteLine("- PC Sleep & Lid Close : System stays awake when laptop lid is closed");
            Console.WriteLine("- Game Protection: ACTIVE ");
            Console.WriteLine("  * Windows Power Throttling (EcoQoS) disabled on protected processes");
            Console.WriteLine("  * Wi-Fi Power Saving Mode forced to Maximum Performance");
            Console.WriteLine("  * PCIe Link State (ASPM) power savings disabled");
            Console.WriteLine("  * System timer resolution set to high-precision 1.0 ms");
            Console.WriteLine("  * Active network keepalive to prevent Wi-Fi router disconnects");
            if (keepDisplayAlive)
            {
                Console.WriteLine("- Display Pipeline     : Kept active (safe for background rendering)");
            }
            else
            {
                Console.WriteLine("- Display Pipeline     : Screen power-off broadcast enabled (--screen-off)");
            }

            var currentPower = System.Windows.Forms.SystemInformation.PowerStatus;
            bool isAc = currentPower.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            if (isAc)
            {
                Console.WriteLine("- Power Supply         : Plugged in (AC Power - Maximum performance unlocked)");
            }
            else
            {
                Console.WriteLine("- Power Supply         : On Battery (" + (int)(currentPower.BatteryLifePercent * 100) + "%)");
            }

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
        else
        {
            Console.Error.WriteLine("Error: Failed to launch background daemon process.");
            StateManager.ClearState();
        }
    }

    public static void HandleOff()
    {
        Console.WriteLine("Restoring normal sleep behavior...");
        var state = StateManager.LoadState();
        if (state != null)
        {
            if (state.DaemonPid > 0)
            {
                try
                {
                    var proc = Process.GetProcessById(state.DaemonPid);
                    if (!proc.HasExited && proc.ProcessName.Contains("nosleep", StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill();
                    }
                }
                catch { }
            }

            Daemon.RestoreState(state);
            Console.WriteLine("nosleep is now OFF. All original power plan and sleep settings restored.");
        }
        else
        {
            Console.WriteLine("nosleep is not currently running.");
        }
    }

    public static void HandleStatus()
    {
        var state = StateManager.LoadState();
        bool isRunning = false;

        if (state != null && state.DaemonPid > 0)
        {
            try
            {
                var proc = Process.GetProcessById(state.DaemonPid);
                if (!proc.HasExited && proc.ProcessName.Contains("nosleep", StringComparison.OrdinalIgnoreCase))
                {
                    isRunning = true;
                }
            }
            catch { }
        }

        if (isRunning && state != null)
        {
            Console.WriteLine("==========================================================");
            Console.WriteLine("nosleep-windows : ON (PID: " + state.DaemonPid + ")");
            Console.WriteLine("==========================================================");
            Console.WriteLine("Lid close behavior  : PC will NOT sleep when lid is closed");
            Console.WriteLine("Game Mode     : ACTIVE");
            Console.WriteLine("  * Timer precision : 1.0 ms (timeBeginPeriod active)");
            Console.WriteLine("  * Wi-Fi Power     : Maximum Performance (Anti-disconnect active)");
            Console.WriteLine("  * PCIe Link State : ASPM Off (No bus downclocking)");
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

            var power = System.Windows.Forms.SystemInformation.PowerStatus;
            string lineStatus = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline 
                ? "On battery" 
                : "Plugged in (AC Adapter - Maximum Wattage Unlocked)";
            Console.WriteLine($"Power supply        : {lineStatus}");
            if (state.RequireAc)
            {
                Console.WriteLine("AC guard            : ACTIVE (Auto-sleeps immediately if charger is disconnected)");
            }

            if (state.BatteryThreshold.HasValue)
            {
                Console.WriteLine($"Battery guard       : Auto-off at <= {state.BatteryThreshold.Value}% (currently {(int)(power.BatteryLifePercent * 100)}%, {lineStatus})");
            }
            Console.WriteLine("==========================================================");
        }
        else
        {
            if (state != null)
            {
                // Stale state cleanup
                Daemon.RestoreState(state);
            }
            Console.WriteLine("nosleep-windows : OFF (normal Windows sleep behavior)");
        }
    }

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
