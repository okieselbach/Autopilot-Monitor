using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Central enrollment tracking orchestrator.
    /// Collects consolidated device info events at startup and manages ImeLogTracker
    /// for smart app installation tracking with strategic event emission.
    /// </summary>
    public class EnrollmentTracker : IDisposable
    {
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly AgentLogger _logger;
        private readonly string _imeLogFolder;
        private readonly List<ImeLogPattern> _imeLogPatterns;
        private readonly Collectors.HelloDetector _helloDetector;

        private ImeLogTracker _imeLogTracker;
        private Timer _summaryTimer;
        private bool _summaryTimerActive;
        private string _lastEspPhase; // Track last ESP phase to prevent duplicate events
        private bool _hasAutoSwitchedToAppsPhase; // Track if we've already auto-switched to apps phase for current ESP phase
        private string _enrollmentType = "v1"; // "v1" = Autopilot Classic/ESP, "v2" = Windows Device Preparation
        private bool _isWaitingForHello = false; // Track if we're waiting for Hello to complete before sending enrollment_complete
        private bool _finalDeviceInfoCollected = false; // Ensure final device info is emitted only once

        // Default IME log folder
        private const string DefaultImeLogFolder = @"%ProgramData%\Microsoft\IntuneManagementExtension\Logs";
        private const string RegKeyWindowsCurrentVersion = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        /// <summary>
        /// Detects whether this is an Autopilot v1 (Classic ESP) or v2 (Windows Device Preparation) enrollment
        /// by reading the Autopilot registry keys. Safe to call before EnrollmentTracker is instantiated.
        /// Returns "v2" if WDP indicators are present, "v1" otherwise.
        /// </summary>
        public static string DetectEnrollmentTypeStatic()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Provisioning\AutopilotSettings"))
                {
                    if (key != null)
                    {
                        // CloudAssignedDeviceRegistration=2 signals WDP provisioning flow
                        var deviceReg = key.GetValue("CloudAssignedDeviceRegistration")?.ToString();
                        if (deviceReg == "2")
                            return "v2";

                        // CloudAssignedEspEnabled=0 means no ESP, characteristic of WDP
                        var espEnabled = key.GetValue("CloudAssignedEspEnabled")?.ToString();
                        if (espEnabled == "0")
                            return "v2";
                    }
                }
            }
            catch { }

            return "v1";
        }

        public EnrollmentTracker(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> emitEvent,
            AgentLogger logger,
            List<ImeLogPattern> imeLogPatterns,
            string imeLogFolderOverride = null,
            bool simulationMode = false,
            double speedFactor = 50,
            string imeMatchLogPath = null,
            Collectors.HelloDetector helloDetector = null)
        {
            _sessionId = sessionId;
            _tenantId = tenantId;
            _emitEvent = emitEvent;
            _logger = logger;
            _imeLogPatterns = imeLogPatterns ?? new List<ImeLogPattern>();
            _imeLogFolder = imeLogFolderOverride ?? DefaultImeLogFolder;
            _helloDetector = helloDetector;

            // Create ImeLogTracker with state persistence directory
            var stateDirectory = @"%ProgramData%\AutopilotMonitor\State";
            _imeLogTracker = new ImeLogTracker(_imeLogFolder, _imeLogPatterns, _logger, matchLogPath: imeMatchLogPath, stateDirectory: stateDirectory);
            _imeLogTracker.SimulationMode = simulationMode;
            _imeLogTracker.SpeedFactor = speedFactor;

            // Wire up callbacks
            _imeLogTracker.OnEspPhaseChanged = HandleEspPhaseChanged;
            _imeLogTracker.OnImeAgentVersion = HandleImeAgentVersion;
            _imeLogTracker.OnImeStarted = HandleImeStarted;
            _imeLogTracker.OnAppStateChanged = HandleAppStateChanged;
            _imeLogTracker.OnPoliciesDiscovered = HandlePoliciesDiscovered;
            _imeLogTracker.OnAllAppsCompleted = HandleAllAppsCompleted;
            _imeLogTracker.OnUserSessionCompleted = HandleUserSessionCompleted;

            // Subscribe to HelloDetector completion event if available
            if (_helloDetector != null)
            {
                _helloDetector.HelloCompleted += OnHelloCompleted;
                _helloDetector.FinalizingSetupPhaseTriggered += OnFinalizingSetupPhaseTriggered;
            }
        }

        /// <summary>
        /// Starts the enrollment tracker: collects device info and starts IME log tracking.
        /// </summary>
        public void Start()
        {
            _logger.Info("EnrollmentTracker: starting");

            // Collect and emit consolidated device info events
            CollectDeviceInfo();

            // Start IME log tracking
            _imeLogTracker.Start();

            // Start periodic summary timer (30s, starts when app tracking begins)
            _summaryTimer = new Timer(SummaryTimerCallback, null, Timeout.Infinite, Timeout.Infinite);

            _logger.Info("EnrollmentTracker: started");
        }

        /// <summary>
        /// Stops the enrollment tracker
        /// </summary>
        public void Stop()
        {
            _logger.Info("EnrollmentTracker: stopping");
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _imeLogTracker?.Stop();
            _logger.Info("EnrollmentTracker: stopped");
        }

        /// <summary>
        /// Updates IME log patterns (hot-reload from config change)
        /// </summary>
        public void UpdatePatterns(List<ImeLogPattern> newPatterns)
        {
            if (newPatterns != null)
            {
                _logger.Info("EnrollmentTracker: updating IME log patterns (hot-reload)");
                _imeLogTracker?.CompilePatterns(newPatterns);
            }
        }

        /// <summary>
        /// Access to the ImeLogTracker (for simulator to reference package states)
        /// </summary>
        public ImeLogTracker ImeTracker => _imeLogTracker;

        // ===== Device Info Collection =====

        private void CollectDeviceInfo()
        {
            _logger.Info("EnrollmentTracker: collecting device info (at start)");

            CollectOSInfo();
            CollectBootTime();
            CollectNetworkAdapters();
            CollectDnsConfiguration();
            CollectProxyConfiguration();
            CollectAutopilotProfile();
            CollectSecureBootStatus();
            CollectBitLockerStatus();
            CollectAadJoinStatus();
        }

        /// <summary>
        /// Collects device info that may change during enrollment (e.g., BitLocker enabled via policy).
        /// Called at enrollment complete to capture final state.
        /// </summary>
        private void CollectDeviceInfoAtEnd()
        {
            _logger.Info("EnrollmentTracker: collecting device info (at end)");

            // BitLocker can be enabled during enrollment via policy
            CollectBitLockerStatus();

            // Add more collectors here as needed
        }

        private void CollectBootTime()
        {
            try
            {
                var data = new Dictionary<string, object>();

                // Query WMI for OS boot time
                using (var searcher = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        var lastBootUpTimeStr = obj["LastBootUpTime"]?.ToString();
                        if (!string.IsNullOrEmpty(lastBootUpTimeStr))
                        {
                            // WMI time format: yyyyMMddHHmmss.ffffff+/-UUU
                            var bootTime = ManagementDateTimeConverter.ToDateTime(lastBootUpTimeStr);
                            data["bootTime"] = bootTime.ToString("o"); // ISO 8601 format
                            data["bootTimeUtc"] = bootTime.ToUniversalTime().ToString("o");

                            // Calculate uptime
                            var uptime = DateTime.Now - bootTime;
                            data["uptimeMinutes"] = (int)uptime.TotalMinutes;
                            data["uptimeHours"] = uptime.TotalHours;

                            _logger.Debug($"Boot time: {bootTime:o}, Uptime: {uptime.TotalMinutes:F1} minutes");
                            break;
                        }
                    }
                }

                if (!data.ContainsKey("bootTime"))
                {
                    data["note"] = "Boot time could not be determined";
                }

                EmitDeviceInfoEvent("boot_time",
                    data.ContainsKey("bootTime")
                        ? $"Last boot: {data["bootTime"]}"
                        : "Boot time unavailable",
                    data);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect boot time: {ex.Message}");
            }
        }

        private void CollectOSInfo()
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    { "version", Environment.OSVersion.Version.ToString() },
                    { "osVersion", GetOsName() },
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
        }

        private string GetOsName()
        {
            var osName = string.Empty;

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Caption FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        osName = obj["Caption"]?.ToString() ?? string.Empty;
                        break;
                    }
                }
            }
            catch
            {
                // prevent OS info collection from failing the tracker
            }

            return osName;
        }

        private void CollectNetworkAdapters()
        {
            try
            {
                var adapters = new List<Dictionary<string, object>>();
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Description, IPAddress, IPSubnet, DefaultIPGateway, MACAddress, DHCPEnabled, DHCPServer, DNSServerSearchOrder " +
                    "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
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
                    }
                }

                EmitDeviceInfoEvent("network_adapters", "Network adapters configuration",
                    new Dictionary<string, object>
                    {
                        { "adapterCount", adapters.Count },
                        { "adapters", adapters }
                    });
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect network adapters: {ex.Message}");
            }
        }

        private void CollectDnsConfiguration()
        {
            try
            {
                var dnsServers = new List<Dictionary<string, object>>();
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Description, DNSServerSearchOrder FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
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

                EmitDeviceInfoEvent("dns_configuration", "DNS server configuration",
                    new Dictionary<string, object>
                    {
                        { "dnsEntries", dnsServers }
                    });
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect DNS config: {ex.Message}");
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

        private void CollectAutopilotProfile()
        {
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
                            _enrollmentType = "v2";
                        else
                            _enrollmentType = "v1";

                        _logger.Info($"EnrollmentTracker: Read {data.Count} values from AutopilotPolicyCache");
                    }
                    else
                    {
                        _logger.Warning("EnrollmentTracker: AutopilotPolicyCache registry key not found");
                    }
                }

                // Include enrollment type in autopilot_profile event data
                data["enrollmentType"] = _enrollmentType;

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
                    Message = _enrollmentType == "v2"
                        ? "Enrollment type: Autopilot v2 (Windows Device Preparation)"
                        : "Enrollment type: Autopilot v1 (Classic ESP)",
                    Data = new Dictionary<string, object> { { "enrollmentType", _enrollmentType } }
                });

                _logger.Info($"EnrollmentTracker: enrollment type detected: {_enrollmentType}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTracker: failed to collect autopilot profile: {ex.Message}");
            }
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

        // ===== ImeLogTracker Callbacks -> Strategic Events =====

        private void HandleEspPhaseChanged(string phase)
        {
            // WDP (v2) has no ESP - skip ESP phase handling entirely
            if (_enrollmentType == "v2")
            {
                _logger.Debug($"EnrollmentTracker: skipping ESP phase event in WDP enrollment (phase: {phase})");
                return;
            }

            // Only emit event if the phase has actually changed
            if (string.Equals(phase, _lastEspPhase, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug($"EnrollmentTracker: ESP phase unchanged ({phase}), skipping event");
                return;
            }

            _logger.Info($"EnrollmentTracker: ESP phase changed from '{_lastEspPhase ?? "null"}' to '{phase}'");
            _lastEspPhase = phase;
            _hasAutoSwitchedToAppsPhase = false; // Reset when ESP phase changes

            // Map ESP phase to EnrollmentPhase (phase change events)
            var enrollmentPhase = EnrollmentPhase.DeviceSetup;
            if (string.Equals(phase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                enrollmentPhase = EnrollmentPhase.AccountSetup;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "esp_phase_changed",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = enrollmentPhase,
                Message = $"ESP phase: {phase}",
                Data = new Dictionary<string, object> { { "espPhase", phase } }
            });

            // Start summary timer when we detect ESP phase
            if (!_summaryTimerActive)
            {
                _summaryTimerActive = true;
                _summaryTimer?.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            }
        }

        private void HandleImeAgentVersion(string version)
        {
            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "ime_agent_version",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"IME Agent version: {version}",
                Data = new Dictionary<string, object> { { "agentVersion", version } }
            });
        }

        private void HandleImeStarted()
        {
            _logger.Info("EnrollmentTracker: IME started event");
        }

        private void HandleAppStateChanged(AppPackageState app, AppInstallationState oldState, AppInstallationState newState)
        {
            // Auto-switch to app installation phase when first app activity detected
            // If we're in DeviceSetup and an app starts downloading/installing, switch to AppsDevice
            // If we're in AccountSetup and an app starts downloading/installing, switch to AppsUser
            if (!_hasAutoSwitchedToAppsPhase &&
                (newState == AppInstallationState.Downloading || newState == AppInstallationState.Installing) &&
                oldState < AppInstallationState.Downloading)
            {
                if (_lastEspPhase != null)
                {
                    if (string.Equals(_lastEspPhase, "DeviceSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Switch from DeviceSetup to AppsDevice
                        _logger.Info($"EnrollmentTracker: First app activity detected during DeviceSetup, switching to AppsDevice phase");
                        _hasAutoSwitchedToAppsPhase = true;
                        _emitEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsDevice,
                            Message = "ESP phase: AppsDevice (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsDevice" }, { "autoDetected", true } }
                        });
                    }
                    else if (string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                    {
                        // Switch from AccountSetup to AppsUser
                        _logger.Info($"EnrollmentTracker: First app activity detected during AccountSetup, switching to AppsUser phase");
                        _hasAutoSwitchedToAppsPhase = true;
                        _emitEvent(new EnrollmentEvent
                        {
                            SessionId = _sessionId,
                            TenantId = _tenantId,
                            EventType = "esp_phase_changed",
                            Severity = EventSeverity.Info,
                            Source = "EnrollmentTracker",
                            Phase = EnrollmentPhase.AppsUser,
                            Message = "ESP phase: AppsUser (auto-detected from app activity)",
                            Data = new Dictionary<string, object> { { "espPhase", "AppsUser" }, { "autoDetected", true } }
                        });
                    }
                }
            }

            // Only emit strategic events for significant state transitions
            string eventType;
            var severity = EventSeverity.Info;
            var phase = EnrollmentPhase.Unknown; // Apps set to Unknown, will be sorted chronologically into active phase

            switch (newState)
            {
                case AppInstallationState.Downloading:
                    // Emit strategic event once when download starts
                    if (oldState < AppInstallationState.Downloading)
                    {
                        eventType = "app_download_started";
                    }
                    else
                    {
                        // Emit debug event for download progress updates
                        // Skip if no real download data (bytesTotal too small or zero)
                        if (app.BytesTotal > 1024) // At least 1 KB to be a real download
                        {
                            _emitEvent(new EnrollmentEvent
                            {
                                SessionId = _sessionId,
                                TenantId = _tenantId,
                                EventType = "download_progress",
                                Severity = EventSeverity.Debug,
                                Source = "ImeLogTracker",
                                Phase = phase,
                                Message = $"{app.Name ?? app.Id}: {app.ProgressPercent}%",
                                Data = app.ToEventData()
                            });
                        }
                        return; // Skip main event emission below
                    }
                    break;

                case AppInstallationState.Installing:
                    eventType = "app_install_started";
                    break;

                case AppInstallationState.Installed:
                    eventType = "app_install_completed";
                    // Emit download_progress event for download manager (shows as completed)
                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "download_progress",
                        Severity = EventSeverity.Debug,
                        Source = "ImeLogTracker",
                        Phase = phase,
                        Message = $"{app.Name ?? app.Id}: completed",
                        Data = new Dictionary<string, object>(app.ToEventData())
                        {
                            ["status"] = "completed"
                        }
                    });
                    break;

                case AppInstallationState.Skipped:
                    eventType = "app_install_skipped";
                    break;

                case AppInstallationState.Error:
                    eventType = "app_install_failed";
                    severity = EventSeverity.Error;
                    // Emit download_progress event for download manager (shows as failed)
                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "download_progress",
                        Severity = EventSeverity.Debug,
                        Source = "ImeLogTracker",
                        Phase = phase,
                        Message = $"{app.Name ?? app.Id}: failed",
                        Data = new Dictionary<string, object>(app.ToEventData())
                        {
                            ["status"] = "failed"
                        }
                    });
                    break;

                case AppInstallationState.Postponed:
                    eventType = "app_install_failed";
                    severity = EventSeverity.Warning;
                    break;

                default:
                    return; // Don't emit for Unknown, NotInstalled, InProgress
            }

            // Build a descriptive message: include error detail if available
            var message = $"{app.Name ?? app.Id}: {newState}";
            if (newState == AppInstallationState.Error && !string.IsNullOrEmpty(app.ErrorDetail))
                message = $"{app.Name ?? app.Id}: {app.ErrorDetail}";

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = "ImeLogTracker",
                Phase = phase,
                Message = message,
                Data = app.ToEventData()
            });
        }

        private void HandlePoliciesDiscovered(string policiesJson)
        {
            _logger.Info($"EnrollmentTracker: policies discovered, tracking {_imeLogTracker.PackageStates.Count} apps");
        }

        private void HandleAllAppsCompleted()
        {
            _logger.Info("EnrollmentTracker: all apps completed");

            // Stop summary timer
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;

            // Emit final summary
            EmitAppTrackingSummary();

            // Note: Phase transition to FinalizingSetup is now handled by Shell-Core events
            // (ESP exit or Hello wizard start) for more robust detection.
            // We no longer automatically transition here when apps complete.
            if (_lastEspPhase != null)
            {
                _logger.Info($"EnrollmentTracker: All apps completed while in phase '{_lastEspPhase}'");
                _logger.Info("EnrollmentTracker: Waiting for ESP exit or Hello wizard events to transition to FinalizingSetup");
            }
        }

        private void HandleUserSessionCompleted()
        {
            _logger.Info("EnrollmentTracker: User session completed (detected from IME log)");

            // Stop summary timer if running
            _summaryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _summaryTimerActive = false;

            // Check if Windows Hello is configured but not yet completed
            if (_helloDetector != null)
            {
                bool helloPolicyConfigured = _helloDetector.IsPolicyConfigured;
                bool helloCompleted = _helloDetector.IsHelloCompleted;

                if (helloPolicyConfigured && !helloCompleted)
                {
                    // Hello is configured but not finished yet - DO NOT mark enrollment as complete
                    _logger.Info("EnrollmentTracker: Windows Hello policy is configured but provisioning has not completed yet.");
                    _logger.Info("EnrollmentTracker: Waiting for Hello provisioning to finish before marking enrollment as complete.");

                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "waiting_for_hello",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTracker",
                        Phase = EnrollmentPhase.FinalizingSetup,
                        Message = "User apps completed - waiting for Windows Hello provisioning to finish"
                    });

                    // Set flag so we know we're waiting
                    _isWaitingForHello = true;

                    // Note: enrollment_complete will be triggered when Hello events arrive
                    // or when the agent is stopped/times out
                    return;
                }

                if (helloPolicyConfigured && helloCompleted)
                {
                    _logger.Info("EnrollmentTracker: Windows Hello provisioning has completed. Enrollment can complete.");
                }
                else
                {
                    _logger.Info("EnrollmentTracker: No Windows Hello policy detected. Enrollment can complete.");
                }
            }

            // Emit enrollment completed event
            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "enrollment_complete",
                Severity = EventSeverity.Info,
                Source = "ImeLogTracker",
                Phase = EnrollmentPhase.Complete,
                Message = "Autopilot enrollment completed successfully (user session completed)"
            });

            // Write enrollment complete marker for cleanup retry detection
            WriteEnrollmentCompleteMarker();

            // Clean up persisted tracker state so next enrollment starts fresh
            _imeLogTracker?.DeleteState();
        }

        /// <summary>
        /// Called when Windows Hello provisioning completes (via HelloDetector event)
        /// If we were waiting for Hello, now we can mark enrollment as complete
        /// </summary>
        private void OnHelloCompleted(object sender, EventArgs e)
        {
            _logger.Info("EnrollmentTracker: Received HelloCompleted event from HelloDetector");

            if (_isWaitingForHello)
            {
                _logger.Info("EnrollmentTracker: Hello provisioning completed while waiting - marking enrollment as complete now");
                _isWaitingForHello = false;

                // Emit enrollment completed event
                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "enrollment_complete",
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTracker",
                    Phase = EnrollmentPhase.Complete,
                    Message = "Autopilot enrollment completed successfully (Hello provisioning completed)"
                });

                // Write enrollment complete marker for cleanup retry detection
                WriteEnrollmentCompleteMarker();

                // Clean up persisted tracker state so next enrollment starts fresh
                _imeLogTracker?.DeleteState();
            }
            else
            {
                _logger.Debug("EnrollmentTracker: HelloCompleted event received but not waiting - ignoring");
            }
        }

        /// <summary>
        /// Called when ESP exit or Hello wizard start is detected (via HelloDetector Shell-Core events)
        /// Triggers transition to FinalizingSetup phase
        /// </summary>
        private void OnFinalizingSetupPhaseTriggered(object sender, string reason)
        {
            _logger.Info($"EnrollmentTracker: FinalizingSetup phase trigger received - reason: {reason}");

            // If ESP exiting, check which phase we're in
            if (reason == "esp_exiting")
            {
                if (string.Equals(_lastEspPhase, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                {
                    // AccountSetup phase exiting - this is the final ESP exit, start Hello wait timer
                    _logger.Info("EnrollmentTracker: AccountSetup phase exiting - enrollment nearing completion, starting Hello wait timer");

                    // Emit phase change event to FinalizingSetup
                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        EventType = "esp_phase_changed",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTracker",
                        Phase = EnrollmentPhase.FinalizingSetup,
                        Message = "ESP phase: FinalizingSetup (AccountSetup completed, waiting for final steps)",
                        Data = new Dictionary<string, object>
                        {
                            { "espPhase", "FinalizingSetup" },
                            { "autoDetected", true },
                            { "triggeredBy", reason },
                            { "previousPhase", "AccountSetup" }
                        }
                    });

                    CollectDeviceInfoAtFinalizingSetup(reason);

                    // Start Hello wait timer (waits for Hello wizard to start or timeout)
                    _helloDetector?.StartHelloWaitTimer();
                }
                else
                {
                    // DeviceSetup phase exiting - this is just transition to AccountSetup, not final
                    _logger.Info($"EnrollmentTracker: ESP phase exiting from '{_lastEspPhase ?? "unknown"}' - intermediate transition, not starting Hello wait timer");

                    // Don't emit FinalizingSetup event yet, this is not the final exit
                    // The regular esp_phase_changed event will be emitted when we detect AccountSetup phase
                }
            }
            else if (reason == "hello_wizard_started")
            {
                // Hello wizard started - transition to FinalizingSetup regardless of previous phase
                _logger.Info("EnrollmentTracker: Hello wizard started - transitioning to FinalizingSetup phase");

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = "esp_phase_changed",
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTracker",
                    Phase = EnrollmentPhase.FinalizingSetup,
                    Message = "ESP phase: FinalizingSetup (Hello wizard started)",
                    Data = new Dictionary<string, object>
                    {
                        { "espPhase", "FinalizingSetup" },
                        { "autoDetected", true },
                        { "triggeredBy", reason }
                    }
                });

                CollectDeviceInfoAtFinalizingSetup(reason);
            }
        }

        private void CollectDeviceInfoAtFinalizingSetup(string triggerReason)
        {
            if (_finalDeviceInfoCollected)
            {
                _logger.Debug($"EnrollmentTracker: final device info already collected, skipping (trigger: {triggerReason})");
                return;
            }

            _finalDeviceInfoCollected = true;
            _logger.Info($"EnrollmentTracker: triggering final device info collection at FinalizingSetup (trigger: {triggerReason})");
            CollectDeviceInfoAtEnd();
        }

        private void SummaryTimerCallback(object state)
        {
            if (_imeLogTracker?.PackageStates?.CountAll > 0)
            {
                EmitAppTrackingSummary();
            }
        }

        private void EmitAppTrackingSummary()
        {
            var states = _imeLogTracker?.PackageStates;
            if (states == null || states.CountAll == 0) return;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "app_tracking_summary",
                Severity = states.HasError ? EventSeverity.Warning : EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"App tracking: {states.CountCompleted}/{states.CountAll} completed" +
                          (states.HasError ? $" ({states.ErrorCount} errors)" : ""),
                Data = states.GetSummaryData()
            });
        }

        public void Dispose()
        {
            Stop();

            // Unsubscribe from HelloDetector events
            if (_helloDetector != null)
            {
                _helloDetector.HelloCompleted -= OnHelloCompleted;
                _helloDetector.FinalizingSetupPhaseTriggered -= OnFinalizingSetupPhaseTriggered;
            }

            _summaryTimer?.Dispose();
            _imeLogTracker?.Dispose();
        }

        /// <summary>
        /// Writes an enrollment complete marker to the state directory.
        /// This marker is checked on agent restart to handle cleanup retry if scheduled task fails.
        /// </summary>
        private void WriteEnrollmentCompleteMarker()
        {
            try
            {
                var stateDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\State");
                Directory.CreateDirectory(stateDirectory);

                var markerPath = Path.Combine(stateDirectory, "enrollment-complete.marker");
                File.WriteAllText(markerPath, $"Enrollment completed at {DateTime.UtcNow:O}");

                _logger.Info($"Enrollment complete marker written: {markerPath}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to write enrollment complete marker: {ex.Message}");
            }
        }
    }
}
