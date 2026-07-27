using System;
using System.Runtime.InteropServices;

namespace nosleep_windows;

internal static class PowerManager
{
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    [FlagsAttribute]
    public enum ExecutionState : uint
    {
        ES_AWAYMODE_REQUIRED = 0x00000040,
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001
    }

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

    public const uint ACTION_DO_NOTHING = 0;

    public static Guid GetActivePowerScheme()
    {
        uint res = PowerGetActiveScheme(IntPtr.Zero, out IntPtr activePolicyGuidPtr);
        if (res == 0 && activePolicyGuidPtr != IntPtr.Zero)
        {
            Guid activePolicyGuid = Marshal.PtrToStructure<Guid>(activePolicyGuidPtr);
            return activePolicyGuid;
        }
        return Guid.Empty; // fallback
    }

    public static (uint ac, uint dc) GetLidCloseActions()
    {
        Guid activeScheme = GetActivePowerScheme();
        Guid subGroup = GUID_SUBGROUP_BUTTONS;
        Guid lidAction = GUID_LIDACTION;
        uint ac = 0, dc = 0;
        PowerReadACValueIndex(IntPtr.Zero, ref activeScheme, ref subGroup, ref lidAction, out ac);
        PowerReadDCValueIndex(IntPtr.Zero, ref activeScheme, ref subGroup, ref lidAction, out dc);
        return (ac, dc);
    }

    public static void SetLidCloseActions(uint acAction, uint dcAction)
    {
        Guid activeScheme = GetActivePowerScheme();
        Guid subGroup = GUID_SUBGROUP_BUTTONS;
        Guid lidAction = GUID_LIDACTION;
        PowerWriteACValueIndex(IntPtr.Zero, ref activeScheme, ref subGroup, ref lidAction, acAction);
        PowerWriteDCValueIndex(IntPtr.Zero, ref activeScheme, ref subGroup, ref lidAction, dcAction);
        PowerSetActiveScheme(IntPtr.Zero, ref activeScheme);
    }
}
