using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace nosleep_windows;

internal class CommandHandlers
{
    public static void HandleOn(string[] args)
    {
        var state = StateManager.LoadState();
        if (state != null)
        {
            try
            {
                var proc = Process.GetProcessById(state.DaemonPid);
                if (!proc.HasExited)
                {
                    Console.WriteLine("nosleep is already running. Please run 'nosleep off' first.");
                    return;
                }
            }
            catch
            {
                // Process doesn't exist, clean up state and proceed
                StateManager.ClearState();
            }
        }

        string duration = null;
        int? batteryThreshold = 20;

        foreach (var arg in args)
        {
            if (arg == "on") continue;
            
            if (arg.StartsWith("--battery="))
            {
                if (int.TryParse(arg.Substring("--battery=".Length), out int threshold))
                    batteryThreshold = threshold;
            }
            else if (arg == "--no-battery")
            {
                batteryThreshold = null;
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
                Console.WriteLine($"Invalid duration: '{duration}'. Use 3h, 90m, 45s, or a bare number of hours.");
                return;
            }
            durationSeconds = parsed;
        }

        if (durationSeconds.HasValue)
            Console.WriteLine($"Disabling sleep for {duration}.");
        else
            Console.WriteLine("Disabling sleep indefinitely.");

        var (originalAc, originalDc) = PowerManager.GetLidCloseActions();

        var newState = new NoSleepState
        {
            OriginalAcLidAction = originalAc,
            OriginalDcLidAction = originalDc,
            ExpirationTime = durationSeconds.HasValue ? DateTime.Now.AddSeconds(durationSeconds.Value) : null,
            BatteryThreshold = batteryThreshold
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

            string offConditions = "";
            if (newState.ExpirationTime.HasValue)
            {
                offConditions = $"Auto-off at {newState.ExpirationTime.Value.ToString("hh:mm tt")}";
            }

            if (batteryThreshold.HasValue)
            {
                if (offConditions != "") offConditions += " or ";
                else offConditions = "Auto-off ";
                offConditions += $"when battery <= {batteryThreshold}%";
            }

            if (offConditions != "")
            {
                Console.WriteLine($"Sleep disabled. {offConditions} (or run 'nosleep off').");
            }
            else
            {
                Console.WriteLine("Sleep disabled. Will stay on until you run 'nosleep off'.");
            }
        }
    }

    public static void HandleOff()
    {
        Console.WriteLine("Restoring normal sleep behavior.");
        var state = StateManager.LoadState();
        if (state != null)
        {
            try
            {
                var proc = Process.GetProcessById(state.DaemonPid);
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch { }

            Daemon.RestoreState(state);
            Console.WriteLine("Sleep restored to normal.");
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
        
        if (state != null)
        {
            try
            {
                var proc = Process.GetProcessById(state.DaemonPid);
                if (!proc.HasExited) isRunning = true;
            }
            catch { }
        }

        if (isRunning && state != null)
        {
            Console.WriteLine("disablesleep : ON  (PC will not sleep, even with the lid closed)");
            if (state.ExpirationTime.HasValue)
            {
                TimeSpan rem = state.ExpirationTime.Value - DateTime.Now;
                if (rem.TotalSeconds > 0)
                {
                    Console.WriteLine($"auto-off in  : {Math.Floor(rem.TotalHours)}h {rem.Minutes:00}m (at {state.ExpirationTime.Value:hh:mm tt})");
                }
            }
            if (state.BatteryThreshold.HasValue)
            {
                var power = System.Windows.Forms.SystemInformation.PowerStatus;
                string lineStatus = power.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline ? "on battery" : "on AC";
                Console.WriteLine($"battery      : auto-off <= {state.BatteryThreshold.Value}% (currently {(int)(power.BatteryLifePercent * 100)}%, {lineStatus})");
            }
        }
        else
        {
            if (state != null)
            {
                // Stale state
                Daemon.RestoreState(state);
            }
            Console.WriteLine("disablesleep : off (normal sleep behavior)");
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
