using System;
using System.IO;
using System.Text.Json;

namespace nosleep_windows;

internal class NoSleepState
{
    public uint OriginalAcLidAction { get; set; }
    public uint OriginalDcLidAction { get; set; }
    public int DaemonPid { get; set; }
    public DateTime? ExpirationTime { get; set; }
    public int? BatteryThreshold { get; set; }
}

internal static class StateManager
{
    private static readonly string StateFilePath;

    static StateManager()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string stateDir = Path.Combine(localAppData, "nosleep-windows");
        Directory.CreateDirectory(stateDir);
        StateFilePath = Path.Combine(stateDir, "state.json");
    }

    public static void SaveState(NoSleepState state)
    {
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(StateFilePath, json);
    }

    public static NoSleepState? LoadState()
    {
        if (!File.Exists(StateFilePath)) return null;
        try
        {
            string json = File.ReadAllText(StateFilePath);
            return JsonSerializer.Deserialize<NoSleepState>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void ClearState()
    {
        if (File.Exists(StateFilePath))
        {
            File.Delete(StateFilePath);
        }
    }
}
