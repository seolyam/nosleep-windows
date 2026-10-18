using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace nosleep_windows;

internal class Daemon
{
    public static void Run()
    {
        var state = StateManager.LoadState();
        if (state == null)
        {
            Console.WriteLine("No state found, daemon exiting.");
            return;
        }

        // Ensure daemon records its own PID
        state.DaemonPid = Environment.ProcessId;

        // 1. Enable 1ms high-resolution system timer
        PowerManager.TimeBeginPeriod(1);

        // 2. Prevent sleep & optionally keep display subsystem active
        var execFlags = PowerManager.ExecutionState.ES_CONTINUOUS | PowerManager.ExecutionState.ES_SYSTEM_REQUIRED;
        if (state.KeepDisplayAlive)
        {
            execFlags |= PowerManager.ExecutionState.ES_DISPLAY_REQUIRED;
        }
        PowerManager.SetThreadExecutionState(execFlags);

        // 3. Set Lid Close action to "Do Nothing" (0)
        PowerManager.SetLidCloseActions(PowerManager.ACTION_DO_NOTHING, PowerManager.ACTION_DO_NOTHING);

        // 4. Optimize Wi-Fi power management to prevent network drops/latency
        if (PowerManager.TryGetPowerSetting(PowerManager.GUID_SUBGROUP_WIRELESS, PowerManager.GUID_WIRELESS_POWER_SAVING_MODE, out uint acWifi, out uint dcWifi))
        {
            state.OriginalAcWifiMode ??= acWifi;
            state.OriginalDcWifiMode ??= dcWifi;
            PowerManager.TrySetPowerSetting(PowerManager.GUID_SUBGROUP_WIRELESS, PowerManager.GUID_WIRELESS_POWER_SAVING_MODE, PowerManager.WIFI_MAX_PERFORMANCE, PowerManager.WIFI_MAX_PERFORMANCE);
        }

        // 5. Optimize PCIe Link State Power Management (prevent PCIe bus throttle)
        if (PowerManager.TryGetPowerSetting(PowerManager.GUID_SUBGROUP_PCIEXPRESS, PowerManager.GUID_PCIEXPRESS_ASPM, out uint acPcie, out uint dcPcie))
        {
            state.OriginalAcPcieAspm ??= acPcie;
            state.OriginalDcPcieAspm ??= dcPcie;
            PowerManager.TrySetPowerSetting(PowerManager.GUID_SUBGROUP_PCIEXPRESS, PowerManager.GUID_PCIEXPRESS_ASPM, PowerManager.PCIE_ASPM_OFF, PowerManager.PCIE_ASPM_OFF);
        }

        // 6. Optimize Minimum CPU State (prevent CPU throttling to 5% on battery/lid close)
        if (PowerManager.TryGetPowerSetting(PowerManager.GUID_SUBGROUP_PROCESSOR, PowerManager.GUID_PROCESSOR_THROTTLE_MIN, out uint acCpu, out uint dcCpu))
        {
            state.OriginalAcCpuMin ??= acCpu;
            state.OriginalDcCpuMin ??= dcCpu;
            uint targetDcCpu = Math.Max(dcCpu, Math.Max(50u, acCpu));
            PowerManager.TrySetPowerSetting(PowerManager.GUID_SUBGROUP_PROCESSOR, PowerManager.GUID_PROCESSOR_THROTTLE_MIN, null, targetDcCpu);
        }

        // 7. Optimize Core Parking (unpark 100% of CPU cores for peak responsiveness on AC and DC)
        if (PowerManager.TryGetPowerSetting(PowerManager.GUID_SUBGROUP_PROCESSOR, PowerManager.GUID_PROCESSOR_CORE_PARKING_MIN, out uint acCp, out uint dcCp))
        {
            state.OriginalAcCoreParking ??= acCp;
            state.OriginalDcCoreParking ??= dcCp;
            PowerManager.TrySetPowerSetting(PowerManager.GUID_SUBGROUP_PROCESSOR, PowerManager.GUID_PROCESSOR_CORE_PARKING_MIN, PowerManager.CORE_PARKING_UNPARK_ALL, PowerManager.CORE_PARKING_UNPARK_ALL);
        }

        // Save updated backup values and true PID to state
        StateManager.SaveState(state);

        // 7. Start Performance and Guardrail background loop
        var cts = new CancellationTokenSource();
        var watchdogThread = new Thread(() => WatchdogLoop(state, cts.Token))
        {
            IsBackground = true,
            Name = "NoSleepWatchdog"
        };
        watchdogThread.Start();

        // 8. Register Lid Watcher Window
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var hiddenContext = new HiddenContext(state.KeepDisplayAlive);

        Application.Run(hiddenContext);
    }

    private static void WatchdogLoop(NoSleepState state, CancellationToken token)
    {
        int tickCounter = 0;

        // Default process names to protect (case-insensitive)
        var defaultTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "flashplayer",
            "flashplayerapp",
            "cefsharp.browsersubprocess"
        };

        if (state.TargetProcesses != null)
        {
            foreach (var t in state.TargetProcesses)
            {
                if (!string.IsNullOrWhiteSpace(t))
                    defaultTargets.Add(t.Trim());
            }
        }

        while (!token.IsCancellationRequested)
        {
            // A. Check Expiration Timer
            if (state.ExpirationTime.HasValue && DateTime.Now >= state.ExpirationTime.Value)
            {
                TriggerExit(state);
                break;
            }

            // B. Check Power Guardrails
            var status = SystemInformation.PowerStatus;
            
            // B1. Require AC Safeguard (Exit immediately if charger was unplugged)
            if (state.RequireAc && status.PowerLineStatus == PowerLineStatus.Offline)
            {
                TriggerExit(state);
                break;
            }

            // B2. Battery Threshold
            if (state.BatteryThreshold.HasValue && status.PowerLineStatus == PowerLineStatus.Offline &&
                status.BatteryLifePercent * 100 <= state.BatteryThreshold.Value)
            {
                TriggerExit(state);
                break;
            }

            // C. Anti-Throttle / Process Optimization Guard
            try
            {
                var processes = Process.GetProcesses();
                foreach (var proc in processes)
                {
                    try
                    {
                        if (defaultTargets.Contains(proc.ProcessName))
                        {
                            PowerManager.OptimizeProcess(proc);
                        }
                    }
                    catch { }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch { }

            // D. Network Keepalive Ping (Every ~30 seconds)
            // Keeps Wi-Fi adapter radio awake and maintains NAT mapping to prevent game disconnects
            tickCounter++;
            if (tickCounter >= 6)
            {
                tickCounter = 0;
                SendNetworkKeepalive();
            }

            try { Thread.Sleep(5000); } catch { }
        }
    }

    private static void SendNetworkKeepalive()
    {
        try
        {
            using var udp = new UdpClient();
            byte[] ping = new byte[] { 0 };
            // Send lightweight UDP probe to keep network interface active
            udp.Send(ping, 1, "1.1.1.1", 53);
        }
        catch { }
    }

    private static void TriggerExit(NoSleepState state)
    {
        try
        {
            RestoreState(state);
        }
        catch { }
        Environment.Exit(0);
    }

    public static void RestoreState(NoSleepState state)
    {
        // 1. End 1ms high resolution timer
        PowerManager.TimeEndPeriod(1);

        // 2. Restore Lid Close Actions
        PowerManager.SetLidCloseActions(state.OriginalAcLidAction, state.OriginalDcLidAction);

        // 3. Restore Wi-Fi power saving mode
        if (state.OriginalAcWifiMode.HasValue || state.OriginalDcWifiMode.HasValue)
        {
            PowerManager.TrySetPowerSetting(
                PowerManager.GUID_SUBGROUP_WIRELESS,
                PowerManager.GUID_WIRELESS_POWER_SAVING_MODE,
                state.OriginalAcWifiMode,
                state.OriginalDcWifiMode
            );
        }

        // 4. Restore PCIe ASPM
        if (state.OriginalAcPcieAspm.HasValue || state.OriginalDcPcieAspm.HasValue)
        {
            PowerManager.TrySetPowerSetting(
                PowerManager.GUID_SUBGROUP_PCIEXPRESS,
                PowerManager.GUID_PCIEXPRESS_ASPM,
                state.OriginalAcPcieAspm,
                state.OriginalDcPcieAspm
            );
        }

        // 5. Restore CPU Min
        if (state.OriginalAcCpuMin.HasValue || state.OriginalDcCpuMin.HasValue)
        {
            PowerManager.TrySetPowerSetting(
                PowerManager.GUID_SUBGROUP_PROCESSOR,
                PowerManager.GUID_PROCESSOR_THROTTLE_MIN,
                state.OriginalAcCpuMin,
                state.OriginalDcCpuMin
            );
        }

        // 6. Restore Core Parking
        if (state.OriginalAcCoreParking.HasValue || state.OriginalDcCoreParking.HasValue)
        {
            PowerManager.TrySetPowerSetting(
                PowerManager.GUID_SUBGROUP_PROCESSOR,
                PowerManager.GUID_PROCESSOR_CORE_PARKING_MIN,
                state.OriginalAcCoreParking,
                state.OriginalDcCoreParking
            );
        }

        // 7. Remove execution state requirement
        PowerManager.SetThreadExecutionState(PowerManager.ExecutionState.ES_CONTINUOUS);

        // 8. Clear state file
        StateManager.ClearState();
    }
}

internal class HiddenContext : ApplicationContext
{
    private HiddenForm hiddenForm;

    public HiddenContext(bool keepDisplayAlive)
    {
        hiddenForm = new HiddenForm(keepDisplayAlive);
        // Force window handle creation
        _ = hiddenForm.Handle;
    }

    private class HiddenForm : Form
    {
        private IntPtr hPowerSrc = IntPtr.Zero;
        private readonly bool keepDisplayAlive;

        public HiddenForm(bool keepDisplayAlive)
        {
            this.keepDisplayAlive = keepDisplayAlive;
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.Load += (s, e) => {
                this.Size = new System.Drawing.Size(0, 0);
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Guid guid = PowerManager.GUID_LIDSWITCH_STATE_CHANGE;
            hPowerSrc = PowerManager.RegisterPowerSettingNotification(this.Handle, ref guid, PowerManager.DEVICE_NOTIFY_WINDOW_HANDLE);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hPowerSrc != IntPtr.Zero)
            {
                PowerManager.UnregisterPowerSettingNotification(hPowerSrc);
                hPowerSrc = IntPtr.Zero;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_POWERBROADCAST = 0x0218;
            const int PBT_POWERSETTINGCHANGE = 0x8013;

            if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == PBT_POWERSETTINGCHANGE)
            {
                var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                if (setting.PowerSetting == PowerManager.GUID_LIDSWITCH_STATE_CHANGE)
                {
                    if (setting.DataLength == sizeof(int))
                    {
                        int lidState = Marshal.ReadInt32(m.LParam, Marshal.OffsetOf<POWERBROADCAST_SETTING>("Data").ToInt32());
                        
                        // Only broadcast SC_MONITORPOWER if keepDisplayAlive is FALSE.
                        // For games and long-running apps, broadcasting MONITOR_OFF pauses render timers,
                        // drops frame rate, and causes DirectX device lost crashes.
                        if (!keepDisplayAlive)
                        {
                            if (lidState == 0) // Lid closed
                            {
                                PowerManager.PostMessage(PowerManager.HWND_BROADCAST, PowerManager.WM_SYSCOMMAND, new IntPtr(PowerManager.SC_MONITORPOWER), new IntPtr(PowerManager.MONITOR_OFF));
                            }
                            else if (lidState == 1) // Lid opened
                            {
                                PowerManager.PostMessage(PowerManager.HWND_BROADCAST, PowerManager.WM_SYSCOMMAND, new IntPtr(PowerManager.SC_MONITORPOWER), new IntPtr(PowerManager.MONITOR_ON));
                            }
                        }
                    }
                }
            }
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }
}
