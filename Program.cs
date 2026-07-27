using System;

namespace nosleep_windows;

class Program
{
    static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
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
        Console.WriteLine(@"Usage: nosleep <on [DURATION] [--battery=N|--no-battery] | off | status>

  on [DURATION]   Disable sleep (survives lid close), auto-off after DURATION.
                  DURATION defaults to 3h. Accepts 3h, 90m, 45s, or bare hours.
                  Also auto-offs at 20% charge while on battery power.
     --battery=N  Move that threshold (N is 1-99)
     --no-battery Turn it off — auto-off on DURATION alone
  off             Restore normal sleep behavior now
  status          Show current state and time remaining

  -h, --help      Show this help");
    }
}
