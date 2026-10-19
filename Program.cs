using System;
using System.Runtime.InteropServices;

namespace nosleep_windows;

class Program
{
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            // Double-clicked (or run bare): show the window. If Windows created a console just for us,
            // we are its only process, so close it instead of leaving a black window behind. When
            // started from an existing terminal the console is shared and stays.
            if (GetConsoleProcessList(new uint[2], 2) <= 1)
                FreeConsole();
            ControlPanel.Run();
            return;
        }

        string command = args[0].ToLowerInvariant();

        switch (command)
        {
            case "on":
                CommandHandlers.HandleOn(args);
                break;
            case "off":
                CommandHandlers.HandleOff();
                break;
            case "status":
                CommandHandlers.HandleStatus();
                break;
            case "_daemon":
                Daemon.Run();
                break;
            case "-h":
            case "--help":
            case "help":
                PrintUsage();
                break;
            default:
                Console.Error.WriteLine($"nosleep: unknown option: '{command}'");
                PrintUsage();
                Environment.Exit(1);
                break;
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine(@"Usage: nosleep <on [DURATION] [OPTIONS] | off | status>
       (run with no arguments to open the control window)

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

Game Protection Features (Active by default):
  * Disables Windows Power Throttling (EcoQoS) on game processes.
  * Sets Windows multimedia system timer to high-precision 1.0 ms.
  * Forces Wi-Fi Power Saving Mode to Maximum Performance (prevents socket disconnects).
  * Disables PCIe Link State (ASPM) power savings to prevent bus latency.
  * Unparks 100% of CPU cores for zero micro-stutter.
  * Keeps minimum processor clock boosted on battery.
  * Sends periodic network keepalive to prevent Wi-Fi router / NAT idle disconnects.");
    }
}
