using System;

namespace nosleep_windows;

// Persistent power plan overrides. These live in the power scheme (not in the daemon process),
// so they are applied once by 'nosleep on' and must be explicitly restored on every exit path.
internal static class PowerTweaks
{
    public static void Apply(NoSleepState state)
    {
        Guid scheme = PowerManager.GetActivePowerScheme();
        if (scheme == Guid.Empty) return;
        state.PowerScheme = scheme;

        // 1. Lid close action -> "Do Nothing"
        if (Override(scheme, PowerManager.GUID_SUBGROUP_BUTTONS, PowerManager.GUID_LIDACTION,
                (_, _) => PowerManager.ACTION_DO_NOTHING, (_, _) => PowerManager.ACTION_DO_NOTHING, out uint ac, out uint dc))
        {
            state.OriginalAcLidAction = ac;
            state.OriginalDcLidAction = dc;
        }

        // 2. Wi-Fi power saving -> Maximum Performance (prevents network drops/latency)
        if (Override(scheme, PowerManager.GUID_SUBGROUP_WIRELESS, PowerManager.GUID_WIRELESS_POWER_SAVING_MODE,
                (_, _) => PowerManager.WIFI_MAX_PERFORMANCE, (_, _) => PowerManager.WIFI_MAX_PERFORMANCE, out ac, out dc))
        {
            state.OriginalAcWifiMode = ac;
            state.OriginalDcWifiMode = dc;
        }

        // 3. PCIe Link State Power Management -> Off (prevents PCIe bus throttle)
        if (Override(scheme, PowerManager.GUID_SUBGROUP_PCIEXPRESS, PowerManager.GUID_PCIEXPRESS_ASPM,
                (_, _) => PowerManager.PCIE_ASPM_OFF, (_, _) => PowerManager.PCIE_ASPM_OFF, out ac, out dc))
        {
            state.OriginalAcPcieAspm = ac;
            state.OriginalDcPcieAspm = dc;
        }

        // 4. Minimum CPU state on battery -> at least 50% (or the AC value if higher)
        if (Override(scheme, PowerManager.GUID_SUBGROUP_PROCESSOR, PowerManager.GUID_PROCESSOR_THROTTLE_MIN,
                null, (acCpu, dcCpu) => Math.Max(dcCpu, Math.Max(50u, acCpu)), out ac, out dc))
        {
            state.OriginalAcCpuMin = ac;
            state.OriginalDcCpuMin = dc;
        }

        // 5. Core parking -> unpark 100% of cores on AC and DC
        if (Override(scheme, PowerManager.GUID_SUBGROUP_PROCESSOR, PowerManager.GUID_PROCESSOR_CORE_PARKING_MIN,
                (_, _) => PowerManager.CORE_PARKING_UNPARK_ALL, (_, _) => PowerManager.CORE_PARKING_UNPARK_ALL, out ac, out dc))
        {
            state.OriginalAcCoreParking = ac;
            state.OriginalDcCoreParking = dc;
        }

        PowerManager.ReapplySchemeIfActive(scheme);
    }

    public static void Restore(NoSleepState state)
    {
        // State files from older versions have no recorded scheme; fall back to the active one.
        Guid scheme = state.PowerScheme ?? PowerManager.GetActivePowerScheme();

        Write(scheme, PowerManager.GUID_SUBGROUP_BUTTONS, PowerManager.GUID_LIDACTION, state.OriginalAcLidAction, state.OriginalDcLidAction);
        Write(scheme, PowerManager.GUID_SUBGROUP_WIRELESS, PowerManager.GUID_WIRELESS_POWER_SAVING_MODE, state.OriginalAcWifiMode, state.OriginalDcWifiMode);
        Write(scheme, PowerManager.GUID_SUBGROUP_PCIEXPRESS, PowerManager.GUID_PCIEXPRESS_ASPM, state.OriginalAcPcieAspm, state.OriginalDcPcieAspm);
        Write(scheme, PowerManager.GUID_SUBGROUP_PROCESSOR, PowerManager.GUID_PROCESSOR_THROTTLE_MIN, state.OriginalAcCpuMin, state.OriginalDcCpuMin);
        Write(scheme, PowerManager.GUID_SUBGROUP_PROCESSOR, PowerManager.GUID_PROCESSOR_CORE_PARKING_MIN, state.OriginalAcCoreParking, state.OriginalDcCoreParking);

        PowerManager.ReapplySchemeIfActive(scheme);
    }

    // Reads the current values, writes the targets (null target = leave that side alone), and only
    // reports success if both the read and the write worked, so we never "restore" a value we never saved.
    // Target functions receive the current (ac, dc) values.
    private static bool Override(Guid scheme, Guid subGroup, Guid setting,
        Func<uint, uint, uint>? acTarget, Func<uint, uint, uint>? dcTarget, out uint ac, out uint dc)
    {
        if (!PowerManager.TryGetPowerSetting(scheme, subGroup, setting, out ac, out dc)) return false;
        return PowerManager.TrySetPowerSetting(scheme, subGroup, setting, acTarget?.Invoke(ac, dc), dcTarget?.Invoke(ac, dc));
    }

    private static void Write(Guid scheme, Guid subGroup, Guid setting, uint? ac, uint? dc)
    {
        if (ac.HasValue || dc.HasValue)
            PowerManager.TrySetPowerSetting(scheme, subGroup, setting, ac, dc);
    }
}
