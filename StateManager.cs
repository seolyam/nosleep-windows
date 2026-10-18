using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Text.Json;

namespace nosleep_windows;

internal class NoSleepState
{
    public uint OriginalAcLidAction { get; set; }
    public uint OriginalDcLidAction { get; set; }

    public uint? OriginalAcWifiMode { get; set; }
    public uint? OriginalDcWifiMode { get; set; }

    public uint? OriginalAcPcieAspm { get; set; }
    public uint? OriginalDcPcieAspm { get; set; }

    public uint? OriginalAcCpuMin { get; set; }
    public uint? OriginalDcCpuMin { get; set; }

    public uint? OriginalAcCoreParking { get; set; }
    public uint? OriginalDcCoreParking { get; set; }

    public int DaemonPid { get; set; }
    public DateTime? ExpirationTime { get; set; }
    public int? BatteryThreshold { get; set; }

    public bool RequireAc { get; set; } = false;
    public bool KeepDisplayAlive { get; set; } = true;
    public List<string> TargetProcesses { get; set; } = new();
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

        // Write-then-rename so a concurrent reader never sees a half-written file. The rename can
        // briefly fail while another nosleep process has the file open, so retry a few times.
        string tempPath = StateFilePath + ".tmp";
        File.WriteAllText(tempPath, json);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tempPath, StateFilePath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(50);
            }
        }
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
            try
            {
                File.Delete(StateFilePath);
            }
            catch { }
        }
    }
}
