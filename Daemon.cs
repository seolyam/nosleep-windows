using System;
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

        // 1. Prevent sleep
        PowerManager.SetThreadExecutionState(
            PowerManager.ExecutionState.ES_CONTINUOUS | 
            PowerManager.ExecutionState.ES_SYSTEM_REQUIRED);

        // 2. Set Lid Close action to "Do Nothing" (0)
        PowerManager.SetLidCloseActions(PowerManager.ACTION_DO_NOTHING, PowerManager.ACTION_DO_NOTHING);

        // 3. Start Guardrail thread
        var cts = new CancellationTokenSource();
        var guardrailThread = new Thread(() => GuardrailLoop(state, cts.Token))
        {
            IsBackground = true
        };
        guardrailThread.Start();

        // 4. Register Lid Watcher Window (Requires Application.Run for message loop)
        // Since we are using Application.Run, we need to ensure the project has <UseWindowsForms>true</UseWindowsForms>
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var hiddenContext = new HiddenContext();
        
        hiddenContext.ExitRequested += (s, e) =>
        {
            cts.Cancel();
            RestoreState(state);
            Application.Exit();
        };

        // We run the hidden context, which sets up the window and processes messages.
        // It will block here until Application.Exit() is called.
        Application.Run(hiddenContext);
    }

    private static void GuardrailLoop(NoSleepState state, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (state.ExpirationTime.HasValue && DateTime.Now >= state.ExpirationTime.Value)
            {
                TriggerExit();
                break;
            }

            if (state.BatteryThreshold.HasValue)
            {
                var status = SystemInformation.PowerStatus;
                if (status.PowerLineStatus == PowerLineStatus.Offline &&
                    status.BatteryLifePercent * 100 <= state.BatteryThreshold.Value)
                {
                    TriggerExit();
                    break;
                }
            }

            try { Thread.Sleep(10000); } catch { }
        }
    }

    private static void TriggerExit()
    {
        // Must marshal to UI thread to exit Application gracefully, or just use Environment.Exit
        Environment.Exit(0);
    }

    public static void RestoreState(NoSleepState state)
    {
        // Restore Lid Close Actions
        PowerManager.SetLidCloseActions(state.OriginalAcLidAction, state.OriginalDcLidAction);
        
        // Remove execution state requirement
        PowerManager.SetThreadExecutionState(PowerManager.ExecutionState.ES_CONTINUOUS);
        
        StateManager.ClearState();
    }
}

internal class HiddenContext : ApplicationContext
{
    private HiddenForm hiddenForm;
    public event EventHandler? ExitRequested;

    public HiddenContext()
    {
        hiddenForm = new HiddenForm();
    }

    private class HiddenForm : Form
    {
        private IntPtr hPowerSrc = IntPtr.Zero;

        public HiddenForm()
        {
            // Make completely invisible
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.Load += (s, e) => {
                this.Size = new System.Drawing.Size(0, 0);
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Register for power setting notifications
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
                        if (lidState == 0) // Lid closed
                        {
                            // Turn off monitor
                            PowerManager.SendMessage(PowerManager.HWND_BROADCAST, PowerManager.WM_SYSCOMMAND, new IntPtr(PowerManager.SC_MONITORPOWER), new IntPtr(PowerManager.MONITOR_OFF));
                        }
                        else if (lidState == 1) // Lid opened
                        {
                            // Turn on monitor
                            PowerManager.SendMessage(PowerManager.HWND_BROADCAST, PowerManager.WM_SYSCOMMAND, new IntPtr(PowerManager.SC_MONITORPOWER), new IntPtr(PowerManager.MONITOR_ON));
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
        public byte Data; // First byte of data
    }
}
