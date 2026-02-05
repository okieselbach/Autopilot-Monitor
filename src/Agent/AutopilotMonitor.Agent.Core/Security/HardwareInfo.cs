using System;
using System.Management;
using AutopilotMonitor.Agent.Core.Logging;

namespace AutopilotMonitor.Agent.Core.Security
{
    /// <summary>
    /// Helper for retrieving hardware information
    /// </summary>
    public static class HardwareInfo
    {
        /// <summary>
        /// Gets the device manufacturer and model from WMI
        /// </summary>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <returns>Tuple of (Manufacturer, Model, SerialNumber)</returns>
        public static (string Manufacturer, string Model, string SerialNumber) GetHardwareInfo(AgentLogger logger = null)
        {
            string manufacturer = "Unknown";
            string model = "Unknown";
            string serialNumber = "Unknown";

            try
            {
                // Get Manufacturer and Model from Win32_ComputerSystem
                using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                using (var collection = searcher.Get())
                {
                    foreach (var obj in collection)
                    {
                        using (obj)
                        {
                            manufacturer = obj["Manufacturer"]?.ToString() ?? "Unknown";
                            model = obj["Model"]?.ToString() ?? "Unknown";
                        }
                    }
                }

                // Get Serial Number from Win32_BIOS
                using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS"))
                using (var collection = searcher.Get())
                {
                    foreach (var obj in collection)
                    {
                        using (obj)
                        {
                            serialNumber = obj["SerialNumber"]?.ToString() ?? "Unknown";
                        }
                    }
                }

                logger?.Debug($"Hardware detected: Manufacturer={manufacturer}, Model={model}, SerialNumber={serialNumber}");
                return (manufacturer, model, serialNumber);
            }
            catch (Exception ex)
            {
                logger?.Error("Error retrieving hardware information", ex);
                return ("Unknown", "Unknown", "Unknown");
            }
        }
    }
}
