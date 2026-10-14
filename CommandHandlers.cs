using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace nosleep_windows;

internal class CommandHandlers
{
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

        foreach (var arg in args)
        {
            if (arg == "on") continue;

            if (arg.StartsWith("--battery=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg.Substring("--battery=".Length), out int threshold))
                    batteryThreshold = threshold;
            }
            else if (arg.Equals("--no-battery", StringComparison.OrdinalIgnoreCase))
            {
                batteryThreshold = null;
            }
            else if (arg.StartsWith("--target=", StringComparison.OrdinalIgnoreCase))
            {
                string targetVal = arg.Substring("--target=".Length).Trim();
                foreach (var item in targetVal.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    customTargets.Add(item.Trim());
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
            else if (!arg.StartsWith("-"))
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
                Console.WriteLine($"Invalid duration: '{duration}'. Use 3h, 90m, 45s, or a bare number of hours.");
                return;
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
                Console.WriteLine($"- Auto-off             : In {duration} (at {newState.ExpirationTime.Value:hh:mm tt})");
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
            var searchTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "flashplayer"
            };
            if (state.TargetProcesses != null)
            {
                foreach (var t in state.TargetProcesses) searchTargets.Add(t);
            }

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
                    Console.WriteLine($"Auto-off in         : {Math.Floor(rem.TotalHours)}h {rem.Minutes:00}m (at {state.ExpirationTime.Value:hh:mm tt})");
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

    private static int ParseDuration(string duration)
    {
        var match = Regex.Match(duration, @"^([0-9]+)([hHmMsS]?)$");
        if (!match.Success) return 0;

        if (!int.TryParse(match.Groups[1].Value, out int val)) return 0;

        string unit = match.Groups[2].Value.ToLower();
        return unit switch
        {
            "h" => val * 3600,
            "m" => val * 60,
            "s" => val,
            "" => val * 3600, // bare number is hours
            _ => 0
        };
    }
}
