using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.Json;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Collects static and semi-static device information at enrollment startup and end.
    /// Emits device info events (os_info, boot_time, network_adapters, dns_configuration,
    /// proxy_configuration, autopilot_profile, enrollment_type_detected, secureboot_status,
    /// bitlocker_status, aad_join_status) via the provided emitEvent action.
    /// </summary>
    public class DeviceInfoCollector
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly AgentLogger _logger;

        private const string RegKeyWindowsCurrentVersion = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        public DeviceInfoCollector(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> emitEvent,
            AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId  = tenantId  ?? throw new ArgumentNullException(nameof(tenantId));
            _emitEvent = emitEvent ?? throw new ArgumentNullException(nameof(emitEvent));
            _logger    = logger    ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Runs all device info collectors at agent startup.
        /// Returns the detected enrollment type ("v1" or "v2") so the caller can track it.
        /// </summary>
        public string CollectAll()
        {
            _logger.Info("EnrollmentTracker: collecting device info (at start)");

            CollectOSInfoAndBootTime();
            CollectNetworkAndDnsConfiguration();
            CollectProxyConfiguration();
            var enrollmentType = CollectAutopilotProfile();
            CollectSecureBootStatus();
            CollectBitLockerStatus();
            CollectAadJoinStatus();

            return enrollmentType;
        }

        /// <summary>
        /// Re-collects device info that may change during enrollment (e.g. BitLocker enabled via policy).
        /// Called at enrollment complete / FinalizingSetup transition to capture final state.
        /// </summary>
        public void CollectAtEnd()
        {
            _logger.Info("EnrollmentTracker: collecting device info (at end)");

            // BitLocker can be enabled during enrollment via policy
            CollectBitLockerStatus();

            // Add more collectors here as needed
        }

        /// <summary>
        /// Collects OS info and boot time in a single WMI query against Win32_OperatingSystem.
        /// Previously these were three separate queries (Caption, LastBootUpTime via two searchers);
        /// consolidated to a single round-trip.
        /// Emits two events (os_info, boot_time) for backward compatibility.
        /// </summary>
        private void CollectOSInfoAndBootTime()
        {
            string osName = string.Empty;
            var bootData = new Dictionary<string, object>();

            // Single WMI query for both Caption (OS name) and LastBootUpTime
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Caption, LastBootUpTime FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        osName = obj["Caption"]?.ToString() ?? string.Empty;

                        var lastBootUpTimeStr = obj["LastBootUpTime"]?.ToString();
                        if (!string.IsNullOrEmpty(lastBootUpTimeStr))
                        {
                            var bootTimeLocal = ManagementDateTimeConverter.ToDateTime(lastBootUpTimeStr);
                            var bootTimeUtc = DateTime.SpecifyKind(bootTimeLocal.ToUniversalTime(), DateTimeKind.Utc);

                            bootData["bootTimeUtc"] = bootTimeUtc.ToString("o");
                            bootData["bootTime"] = bootTimeUtc.ToString("o");

                            var uptime = DateTime.UtcNow - bootTimeUtc;
                            bootData["uptimeMinutes"] = (int)uptime.TotalMinutes;
                            bootData["uptimeHours"] = uptime.TotalHours;

                            _logger.Debug($"Boot time (UTC): {bootTimeUtc:o}, Uptime: {uptime.TotalMinutes:F1} minutes");
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to query Win32_OperatingSystem: {ex.Message}");
            }

            // Emit os_info event
            try
            {
                var data = new Dictionary<string, object>
                {
                    { "version", Environment.OSVersion.Version.ToString() },
                    { "osVersion", osName },
                    { "edition", (Registry.GetValue(RegKeyWindowsCurrentVersion, "EditionID", string.Empty) ?? string.Empty).ToString() },
                    { "compositionEdition", (Registry.GetValue(RegKeyWindowsCurrentVersion, "CompositionEditionID", string.Empty) ?? string.Empty).ToString() },
                    { "currentBuild", (Registry.GetValue(RegKeyWindowsCurrentVersion, "CurrentBuild", string.Empty) ?? string.Empty).ToString() },
                    { "buildBranch", (Registry.GetValue(RegKeyWindowsCurrentVersion, "BuildBranch", string.Empty) ?? string.Empty).ToString() },
                    { "displayVersion", (Registry.GetValue(RegKeyWindowsCurrentVersion, "DisplayVersion", string.Empty) ?? string.Empty).ToString() },
                    { "buildRevision", (Registry.GetValue(RegKeyWindowsCurrentVersion, "UBR", string.Empty) ?? string.Empty).ToString() }
                };

                object osVersionValue;
                object displayVersionValue;
                object currentBuildValue;
                object buildRevisionValue;

                var osVersion = data.TryGetValue("osVersion", out osVersionValue) ? osVersionValue?.ToString() ?? string.Empty : string.Empty;
                var displayVersion = data.TryGetValue("displayVersion", out displayVersionValue) ? displayVersionValue?.ToString() ?? string.Empty : string.Empty;
                var currentBuild = data.TryGetValue("currentBuild", out currentBuildValue) ? currentBuildValue?.ToString() ?? string.Empty : string.Empty;
                var buildRevision = data.TryGetValue("buildRevision", out buildRevisionValue) ? buildRevisionValue?.ToString() ?? string.Empty : string.Empty;

                var message = string.IsNullOrWhiteSpace(osVersion) ? "OS information collected" : osVersion;
                if (!string.IsNullOrWhiteSpace(displayVersion))
                {
                    message += $" {displayVersion}";
                }

                if (!string.IsNullOrWhiteSpace(currentBuild))
                {
                    message += string.IsNullOrWhiteSpace(buildRevision)
                        ? $" (Build {currentBuild})"
                        : $" (Build {currentBuild}.{buildRevision})";
                }

                EmitDeviceInfoEvent("os_info", message, data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect OS info: {ex.Message}");
            }

            // Emit boot_time event
            try
            {
                if (!bootData.ContainsKey("bootTime"))
                {
                    bootData["note"] = "Boot time could not be determined";
                }

                EmitDeviceInfoEvent("boot_time",
                    bootData.ContainsKey("bootTime")
                        ? $"Last boot: {bootData["bootTime"]}"
                        : "Boot time unavailable",
                    bootData);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to emit boot time event: {ex.Message}");
            }
        }

        /// <summary>
        /// Collects network adapter and DNS configuration in a single WMI query.
        /// Previously these were two separate queries against Win32_NetworkAdapterConfiguration;
        /// consolidated to halve the WMI round-trips (each creates a new COM connection).
        /// Emits two events (network_adapters, dns_configuration) for backward compatibility.
        /// </summary>
        private void CollectNetworkAndDnsConfiguration()
        {
            try
            {
                var adapters = new List<Dictionary<string, object>>();
                var dnsServers = new List<Dictionary<string, object>>();

                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Description, IPAddress, IPSubnet, DefaultIPGateway, MACAddress, DHCPEnabled, DHCPServer, DNSServerSearchOrder " +
                    "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        // Network adapter info
                        var adapter = new Dictionary<string, object>
                        {
                            { "description", obj["Description"]?.ToString() },
                            { "macAddress", obj["MACAddress"]?.ToString() },
                            { "dhcpEnabled", obj["DHCPEnabled"]?.ToString() },
                            { "dhcpServer", obj["DHCPServer"]?.ToString() }
                        };

                        var ipAddresses = obj["IPAddress"] as string[];
                        if (ipAddresses != null)
                            adapter["ipAddresses"] = string.Join(", ", ipAddresses);

                        var subnets = obj["IPSubnet"] as string[];
                        if (subnets != null)
                            adapter["subnets"] = string.Join(", ", subnets);

                        var gateways = obj["DefaultIPGateway"] as string[];
                        if (gateways != null)
                            adapter["gateways"] = string.Join(", ", gateways);

                        adapters.Add(adapter);

                        // DNS info (extracted from the same result set)
                        var servers = obj["DNSServerSearchOrder"] as string[];
                        if (servers != null && servers.Length > 0)
                        {
                            dnsServers.Add(new Dictionary<string, object>
                            {
                                { "adapter", obj["Description"]?.ToString() },
                                { "servers", string.Join(", ", servers) }
                            });
                        }
                    }
                }

                EmitDeviceInfoEvent("network_adapters", "Network adapters configuration",
                    new Dictionary<string, object>
                    {
                        { "adapterCount", adapters.Count },
                        { "adapters", adapters }
                    });

                EmitDeviceInfoEvent("dns_configuration", "DNS server configuration",
                    new Dictionary<string, object>
                    {
                        { "dnsEntries", dnsServers }
                    });
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect network/DNS config: {ex.Message}");
            }
        }

        private void CollectProxyConfiguration()
        {
            try
            {
                var data = new Dictionary<string, object>();

                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (key != null)
                    {
                        data["proxyEnabled"] = key.GetValue("ProxyEnable")?.ToString() == "1";
                        data["proxyServer"] = key.GetValue("ProxyServer")?.ToString();
                        data["proxyOverride"] = key.GetValue("ProxyOverride")?.ToString();
                        data["autoConfigUrl"] = key.GetValue("AutoConfigURL")?.ToString();
                    }
                }

                // Also check WinHTTP proxy
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings\Connections"))
                {
                    data["winHttpProxyConfigured"] = key?.GetValue("WinHttpSettings") != null;
                }

                var proxyType = "Direct";
                if (data.ContainsKey("proxyEnabled") && (bool)data["proxyEnabled"])
                    proxyType = "Proxy";
                else if (data.ContainsKey("autoConfigUrl") && data["autoConfigUrl"] != null)
                    proxyType = "PAC";

                data["proxyType"] = proxyType;

                EmitDeviceInfoEvent("proxy_configuration", $"Proxy configuration: {proxyType}", data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect proxy config: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the Autopilot profile from registry and detects enrollment type.
        /// Returns "v1" (Classic ESP) or "v2" (Windows Device Preparation).
        /// </summary>
        private string CollectAutopilotProfile()
        {
            var detectedType = "v1";

            try
            {
                var data = new Dictionary<string, object>();

                // Read JSON from AutopilotPolicyCache registry key (contains all Autopilot profile info)
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache"))
                {
                    if (key != null)
                    {
                        // The registry key contains individual values that together form the Autopilot profile
                        // Read all available values and add them to data dictionary
                        foreach (var valueName in key.GetValueNames())
                        {
                            var value = key.GetValue(valueName);
                            if (value != null)
                            {
                                // Store with original casing from registry
                                var valueAsString = value.ToString();
                                if (string.Equals(valueName, "PolicyJsonCache", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(valueName, "CloudAssignedAadServerData", StringComparison.OrdinalIgnoreCase))
                                {
                                    valueAsString = TryFormatJson(valueAsString);
                                }

                                data[valueName] = valueAsString;
                            }
                        }

                        // Extract key values for enrollment type detection and UI display
                        var deviceReg = data.ContainsKey("CloudAssignedDeviceRegistration")
                            ? data["CloudAssignedDeviceRegistration"]?.ToString()
                            : null;
                        var espEnabled = data.ContainsKey("CloudAssignedEspEnabled")
                            ? data["CloudAssignedEspEnabled"]?.ToString()
                            : null;

                        // Determine enrollment type: WDP if DeviceRegistration=2 or ESP explicitly disabled
                        if (deviceReg == "2" || espEnabled == "0")
                            detectedType = "v2";
                        else
                            detectedType = "v1";

                        _logger.Info($"EnrollmentTracker: Read {data.Count} values from AutopilotPolicyCache");
                    }
                    else
                    {
                        _logger.Warning("EnrollmentTracker: AutopilotPolicyCache registry key not found");
                    }
                }

                // Include enrollment type in autopilot_profile event data
                data["enrollmentType"] = detectedType;

                EmitDeviceInfoEvent("autopilot_profile", "Autopilot profile configuration", data);

                // Emit dedicated enrollment_type_detected event for easy filtering
                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "enrollment_type_detected",
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTracker",
                    Phase = EnrollmentPhase.Start,
                    Message = detectedType == "v2"
                        ? "Enrollment type: Autopilot v2 (Windows Device Preparation)"
                        : "Enrollment type: Autopilot v1 (Classic ESP)",
                    Data = new Dictionary<string, object> { { "enrollmentType", detectedType } }
                });

                _logger.Info($"EnrollmentTracker: enrollment type detected: {detectedType}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect autopilot profile: {ex.Message}");
            }

            return detectedType;
        }

        private void CollectSecureBootStatus()
        {
            try
            {
                var data = new Dictionary<string, object>();

                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                {
                    if (key != null)
                    {
                        var value = key.GetValue("UEFISecureBootEnabled");
                        data["uefiSecureBootEnabled"] = value != null && Convert.ToInt32(value) == 1;
                    }
                    else
                    {
                        data["uefiSecureBootEnabled"] = false;
                        data["note"] = "SecureBoot registry key not found";
                    }
                }

                EmitDeviceInfoEvent("secureboot_status", $"SecureBoot: {data["uefiSecureBootEnabled"]}", data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect SecureBoot status: {ex.Message}");
            }
        }

        private void CollectBitLockerStatus()
        {
            try
            {
                var data = new Dictionary<string, object>();
                var volumes = new List<Dictionary<string, object>>();

                using (var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption"),
                    new ObjectQuery("SELECT DriveLetter, ProtectionStatus, ConversionStatus, EncryptionMethod FROM Win32_EncryptableVolume")))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        volumes.Add(new Dictionary<string, object>
                        {
                            { "driveLetter", obj["DriveLetter"]?.ToString() },
                            { "protectionStatus", obj["ProtectionStatus"]?.ToString() },
                            { "conversionStatus", obj["ConversionStatus"]?.ToString() },
                            { "encryptionMethod", obj["EncryptionMethod"]?.ToString() }
                        });
                    }
                }

                data["volumes"] = volumes;
                data["systemDriveProtected"] = volumes.Any(v =>
                    v["driveLetter"]?.ToString() == "C:" && v["protectionStatus"]?.ToString() == "1");

                EmitDeviceInfoEvent("bitlocker_status", "BitLocker encryption status", data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect BitLocker status: {ex.Message}");
            }
        }

        private void CollectAadJoinStatus()
        {
            try
            {
                var data = new Dictionary<string, object>();

                using (var joinInfoKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo"))
                {
                    if (joinInfoKey != null)
                    {
                        var subKeyNames = joinInfoKey.GetSubKeyNames();
                        if (subKeyNames.Length > 0)
                        {
                            using (var subKey = joinInfoKey.OpenSubKey(subKeyNames[0]))
                            {
                                if (subKey != null)
                                {
                                    data["tenantId"] = subKey.GetValue("TenantId")?.ToString();
                                    data["userEmail"] = subKey.GetValue("UserEmail")?.ToString();
                                    data["joinType"] = "Azure AD Joined";
                                    data["thumbprint"] = subKeyNames[0]; // Certificate thumbprint
                                }
                            }
                        }
                        else
                        {
                            data["joinType"] = "Not Joined";
                        }
                    }
                    else
                    {
                        data["joinType"] = "Not Joined";
                    }
                }

                object joinTypeValue;
                var joinType = data.TryGetValue("joinType", out joinTypeValue) ? joinTypeValue?.ToString() ?? "Unknown" : "Unknown";
                EmitDeviceInfoEvent("aad_join_status", $"AAD join: {joinType}", data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect AAD join status: {ex.Message}");
            }
        }

        private void EmitDeviceInfoEvent(string eventType, string message, Dictionary<string, object> data)
        {
            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data
            });
        }

        private static string TryFormatJson(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input ?? string.Empty;

            try
            {
                using (var doc = JsonDocument.Parse(input))
                {
                    return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                    {
                        WriteIndented = true
                    });
                }
            }
            catch
            {
                return input;
            }
        }
    }
}
