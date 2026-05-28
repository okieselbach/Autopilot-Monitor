#nullable enable
using System;
using System.Runtime.InteropServices;

namespace AutopilotMonitor.Agent.V2.Core.Runtime
{
    /// <summary>
    /// Synchronous, fail-soft snapshot of the device's AC/battery state via the Win32
    /// <c>GetSystemPowerStatus</c> API. Used by <see cref="StartupEnvironmentProbes"/> at
    /// agent start to surface a power-state hint into the enrollment timeline — admins
    /// want to know whether a laptop entered Autopilot on battery with a low charge
    /// (a frequent driver behind power-management-related stalls).
    /// </summary>
    public static class PowerStateProbe
    {
        public static PowerStateResult Probe()
        {
            try
            {
                if (!GetSystemPowerStatus(out var status))
                {
                    var lastError = Marshal.GetLastWin32Error();
                    return new PowerStateResult
                    {
                        ProbeError = $"GetSystemPowerStatus returned false (Win32Error={lastError})",
                    };
                }

                var onAc = status.ACLineStatus == 1;
                var noBattery = (status.BatteryFlag & 0x80) != 0 || status.BatteryFlag == 0xFF;
                var isCharging = (status.BatteryFlag & 0x08) != 0;

                int? percent = status.BatteryLifePercent == 0xFF ? (int?)null : status.BatteryLifePercent;
                int? lifeMinutes = status.BatteryLifeTime < 0 ? (int?)null : status.BatteryLifeTime / 60;

                return new PowerStateResult
                {
                    OnAcPower = onAc,
                    HasBattery = !noBattery,
                    BatteryPercent = noBattery ? (int?)null : percent,
                    IsCharging = isCharging,
                    BatteryLifeMinutes = noBattery ? (int?)null : lifeMinutes,
                };
            }
            catch (Exception ex)
            {
                return new PowerStateResult { ProbeError = ex.Message };
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            // 0 = offline (battery), 1 = online (AC), 255 = unknown
            public byte ACLineStatus;

            // Bitmask: 1=High(>66%), 2=Low(<33%), 4=Critical(<5%), 8=Charging,
            //          128=NoSystemBattery, 255=Unknown
            public byte BatteryFlag;

            // 0..100, 255 = unknown
            public byte BatteryLifePercent;

            public byte SystemStatusFlag;

            // Seconds remaining, -1 = unknown/unlimited
            public int BatteryLifeTime;

            public int BatteryFullLifeTime;
        }
    }

    public sealed class PowerStateResult
    {
        public bool OnAcPower { get; set; }
        public bool HasBattery { get; set; }
        public int? BatteryPercent { get; set; }
        public bool IsCharging { get; set; }
        public int? BatteryLifeMinutes { get; set; }
        public string? ProbeError { get; set; }
    }
}
