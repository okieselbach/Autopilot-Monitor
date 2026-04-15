using System;

namespace AutopilotMonitor.Agent.Core.Monitoring.Telemetry.DeviceInfo
{
    /// <summary>
    /// Provides device hardware and OS information via WMI and registry queries.
    /// </summary>
    public static class DeviceInfoProvider
    {
        public static string GetSerialNumber()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["SerialNumber"]?.ToString() ?? "Unknown";
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        public static string GetManufacturer()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Manufacturer FROM Win32_ComputerSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["Manufacturer"]?.ToString() ?? "Unknown";
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        public static string GetModel()
        {
            try
            {
                var manufacturer = GetManufacturer();
                if (manufacturer.IndexOf("lenovo", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Lenovo stores the friendly model name (e.g. "ThinkPad X1 Carbon Gen 9")
                    // in Win32_ComputerSystemProduct.Version instead of Win32_ComputerSystem.Model
                    // which only contains the internal type number (e.g. "20Y3S0FP00")
                    using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Version FROM Win32_ComputerSystemProduct"))
                    {
                        foreach (var obj in searcher.Get())
                        {
                            return obj["Version"]?.ToString() ?? "Unknown";
                        }
                    }
                }

                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["Model"]?.ToString() ?? "Unknown";
                    }
                }
            }
            catch { }
            return "Unknown";
        }

        public static string GetOsEdition()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    return key?.GetValue("EditionID")?.ToString() ?? "Unknown";
                }
            }
            catch { }
            return "Unknown";
        }

        /// <summary>
        /// Returns the OS product name, e.g. "Microsoft Windows 11 Pro".
        /// Uses WMI Win32_OperatingSystem.Caption which is the authoritative source.
        /// </summary>
        public static string GetOsName()
        {
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        return obj["Caption"]?.ToString() ?? string.Empty;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// Returns the real OS build string, e.g. "26220.7934" (CurrentBuild.UBR).
        /// Falls back to CurrentBuild alone if UBR is unavailable.
        /// </summary>
        public static string GetOsBuild()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var currentBuild = key?.GetValue("CurrentBuild")?.ToString();
                    if (string.IsNullOrEmpty(currentBuild))
                        return string.Empty;

                    var ubr = key?.GetValue("UBR");
                    return ubr != null ? $"{currentBuild}.{ubr}" : currentBuild;
                }
            }
            catch { }
            return string.Empty;
        }

        /// <summary>
        /// Returns the OS display version, e.g. "25H2", "24H2".
        /// </summary>
        public static string GetOsDisplayVersion()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    return key?.GetValue("DisplayVersion")?.ToString() ?? string.Empty;
                }
            }
            catch { }
            return string.Empty;
        }
    }
}
