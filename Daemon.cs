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
    // Default process names to protect (case-insensitive)
    public static readonly string[] DefaultTargets =
    {
        "flashplayer",
        "flashplayerapp",
        "cefsharp.browsersubprocess"
    };

    public static HashSet<string> GetTargetSet(NoSleepState state)
    {
        var targets = new HashSet<string>(DefaultTargets, StringComparer.OrdinalIgnoreCase);
        if (state.TargetProcesses != null)
        {
            foreach (var t in state.TargetProcesses)
            {
                if (!string.IsNullOrWhiteSpace(t))
                    targets.Add(t.Trim());
            }
        }
        return targets;
    }

    public static void Run()
    {
        var state = StateManager.LoadState();
        // No console to report to (stdio is not connected); 'nosleep on' detects the early exit.
        if (state == null) return;

        // Power plan overrides were already applied by 'nosleep on'. Recording our PID tells it we started.
        state.DaemonPid = Environment.ProcessId;
        StateManager.SaveState(state);

        // Never leave the lid action stuck on "Do Nothing" because the daemon crashed.
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { RestoreState(state); } catch { }
        };

        // 1. Enable 1ms high-resolution system timer (released automatically when this process exits)
        PowerManager.TimeBeginPeriod(1);

        // 2. Prevent sleep & optionally keep display subsystem active
        var execFlags = PowerManager.ExecutionState.ES_CONTINUOUS | PowerManager.ExecutionState.ES_SYSTEM_REQUIRED;
        if (state.KeepDisplayAlive)
        {
            execFlags |= PowerManager.ExecutionState.ES_DISPLAY_REQUIRED;
        }
        PowerManager.SetThreadExecutionState(execFlags);

        // 3. Start Performance and Guardrail background loop
        var cts = new CancellationTokenSource();
        var watchdogThread = new Thread(() => WatchdogLoop(state, cts.Token))
        {
            IsBackground = true,
            Name = "NoSleepWatchdog"
        };
        watchdogThread.Start();

        // 4. Register Lid Watcher Window (also restores settings if Windows logs off / shuts down)
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var hiddenContext = new HiddenContext(state);

        Application.Run(hiddenContext);
    }

    private static void WatchdogLoop(NoSleepState state, CancellationToken token)
    {
        int tickCounter = 0;
        var targets = GetTargetSet(state);

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
                        if (targets.Contains(proc.ProcessName))
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

            token.WaitHandle.WaitOne(5000);
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

    // Restores the persistent power plan changes. Safe to call from any process ('off', 'status',
    // the daemon itself); per-process state (timer resolution, execution state) dies with the daemon.
    public static void RestoreState(NoSleepState state)
    {
        PowerTweaks.Restore(state);
        StateManager.ClearState();
    }
}

internal class HiddenContext : ApplicationContext
{
    private HiddenForm hiddenForm;

    public HiddenContext(NoSleepState state)
    {
        hiddenForm = new HiddenForm(state);
        // Force window handle creation
        _ = hiddenForm.Handle;
    }

    private class HiddenForm : Form
    {
        private IntPtr hPowerSrc = IntPtr.Zero;
        private readonly NoSleepState state;
        private readonly bool keepDisplayAlive;

        public HiddenForm(NoSleepState state)
        {
            this.state = state;
            this.keepDisplayAlive = state.KeepDisplayAlive;
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
            const int WM_ENDSESSION = 0x0016;

            // The daemon does not survive logoff/reboot, so put the power plan back before Windows kills us;
            // otherwise the lid action would stay "Do Nothing" indefinitely.
            if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero)
            {
                try { Daemon.RestoreState(state); } catch { }
            }

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
