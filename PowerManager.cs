using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace nosleep_windows;

internal static class PowerManager
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [Flags]
    public enum ExecutionState : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    public static extern uint TimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    public static extern uint TimeEndPeriod(uint uMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessInformation(
        IntPtr hProcess,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        uint processInformationSize
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessPriorityBoost(IntPtr hProcess, bool bDisablePriorityBoost);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    public const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    [DllImport("powrprof.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern uint PowerReadACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint AcValueIndex);

    [DllImport("powrprof.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern uint PowerReadDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, out uint DcValueIndex);

    [DllImport("powrprof.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);

    [DllImport("powrprof.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);

    [DllImport("powrprof.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid PowerSettingGuid, uint Flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterPowerSettingNotification(IntPtr Handle);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_MONITORPOWER = 0xF170;
    public const int MONITOR_OFF = 2;
    public const int MONITOR_ON = -1;
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    public static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
    
    // Subgroup: SUB_BUTTONS
    public static readonly Guid GUID_SUBGROUP_BUTTONS = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
    // Setting: Lid Close Action
    public static readonly Guid GUID_LIDACTION = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");

    // Subgroup: Wireless Adapter Settings
    public static readonly Guid GUID_SUBGROUP_WIRELESS = new Guid("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    // Setting: Wireless Power Saving Mode (0 = Maximum Performance)
    public static readonly Guid GUID_WIRELESS_POWER_SAVING_MODE = new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a");

    // Subgroup: PCI Express
    public static readonly Guid GUID_SUBGROUP_PCIEXPRESS = new Guid("501a4d13-42af-4429-9fd1-a8218c268e20");
    // Setting: Link State Power Management (0 = Off)
    public static readonly Guid GUID_PCIEXPRESS_ASPM = new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5");

    // Subgroup: Processor power management
    public static readonly Guid GUID_SUBGROUP_PROCESSOR = new Guid("54533251-82be-4824-96c1-47b60b740d00");
    // Setting: Minimum processor state
    public static readonly Guid GUID_PROCESSOR_THROTTLE_MIN = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c");
    // Setting: Processor performance core parking min cores (100% = unpark all cores)
    public static readonly Guid GUID_PROCESSOR_CORE_PARKING_MIN = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");

    public const uint ACTION_DO_NOTHING = 0;
    public const uint WIFI_MAX_PERFORMANCE = 0;
    public const uint PCIE_ASPM_OFF = 0;
    public const uint CORE_PARKING_UNPARK_ALL = 100;

    public static Guid GetActivePowerScheme()
    {
        uint res = PowerGetActiveScheme(IntPtr.Zero, out IntPtr activePolicyGuidPtr);
        if (res == 0 && activePolicyGuidPtr != IntPtr.Zero)
        {
            Guid activePolicyGuid = Marshal.PtrToStructure<Guid>(activePolicyGuidPtr);
            LocalFree(activePolicyGuidPtr);
            return activePolicyGuid;
        }
        return Guid.Empty;
    }

    public static bool TryGetPowerSetting(Guid subGroup, Guid setting, out uint ac, out uint dc)
    {
        ac = 0;
        dc = 0;
        Guid activeScheme = GetActivePowerScheme();
        if (activeScheme == Guid.Empty) return false;

        uint resAc = PowerReadACValueIndex(IntPtr.Zero, ref activeScheme, ref subGroup, ref setting, out ac);
        uint resDc = PowerReadDCValueIndex(IntPtr.Zero, ref activeScheme, ref subGroup, ref setting, out dc);
        return resAc == 0 && resDc == 0;
    }

    public static bool TrySetPowerSetting(Guid subGroup, Guid setting, uint? ac, uint? dc)
    {
        Guid activeScheme = GetActivePowerScheme();
        if (activeScheme == Guid.Empty) return false;

        bool changed = false;
        if (ac.HasValue)
        {
            if (PowerWriteACValueIndex(IntPtr.Zero, ref activeScheme, ref subGroup, ref setting, ac.Value) == 0)
                changed = true;
        }
        if (dc.HasValue)
        {
            if (PowerWriteDCValueIndex(IntPtr.Zero, ref activeScheme, ref subGroup, ref setting, dc.Value) == 0)
                changed = true;
        }
        if (changed)
        {
            PowerSetActiveScheme(IntPtr.Zero, ref activeScheme);
        }
        return changed;
    }

    public static (uint ac, uint dc) GetLidCloseActions()
    {
        if (TryGetPowerSetting(GUID_SUBGROUP_BUTTONS, GUID_LIDACTION, out uint ac, out uint dc))
            return (ac, dc);
        return (0, 0);
    }

    public static void SetLidCloseActions(uint acAction, uint dcAction)
    {
        TrySetPowerSetting(GUID_SUBGROUP_BUTTONS, GUID_LIDACTION, acAction, dcAction);
    }

    public static bool OptimizeProcess(Process proc)
    {
        try
        {
            if (proc.HasExited) return false;

            // 1. Disable EcoQoS / Power Throttling
            var throttlingState = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0 // Disable throttling completely
            };

            SetProcessInformation(
                proc.Handle,
                ProcessPowerThrottling,
                ref throttlingState,
                (uint)Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE))
            );

            // 2. Set priority class to AboveNormal to guarantee CPU scheduling
            try
            {
                if (proc.PriorityClass < ProcessPriorityClass.AboveNormal)
                {
                    proc.PriorityClass = ProcessPriorityClass.AboveNormal;
                }
            }
            catch { }

            // 3. Ensure priority boost is enabled (false = do not disable boost)
            SetProcessPriorityBoost(proc.Handle, false);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
