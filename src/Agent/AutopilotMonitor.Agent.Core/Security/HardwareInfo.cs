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
                // Lenovo reports the marketing model name in Win32_ComputerSystemProduct.Version
                // instead of Win32_ComputerSystem.Model (which contains a generic platform string)
                using (var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
                using (var collection = searcher.Get())
                {
                    foreach (var obj in collection)
                    {
                        using (obj)
                        {
                            manufacturer = obj["Manufacturer"]?.ToString() ?? "Unknown";
                            if (manufacturer.IndexOf("lenovo", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                using (var lenovoSearcher = new ManagementObjectSearcher("SELECT Version FROM Win32_ComputerSystemProduct"))
                                using (var lenovoCollection = lenovoSearcher.Get())
                                {
                                    foreach (var lenovoObj in lenovoCollection)
                                    {
                                        using (lenovoObj)
                                        {
                                            model = lenovoObj["Version"]?.ToString() ?? "Unknown";
                                        }
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                model = obj["Model"]?.ToString() ?? "Unknown";
                            }
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
