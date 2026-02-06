using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Built-in gather rules shipped with the system
    /// These define what data the agent collects beyond the core collectors
    /// </summary>
    public static class BuiltInGatherRules
    {
        public static List<GatherRule> GetAll()
        {
            return new List<GatherRule>
            {
                // ===== NETWORK RULES =====
                new GatherRule
                {
                    RuleId = "GATHER-NET-001",
                    Title = "Collect Proxy Settings",
                    Description = "Reads WinHTTP and Internet Explorer proxy configuration from the registry. Helps diagnose proxy-related enrollment failures.",
                    Category = "network",
                    CollectorType = "registry",
                    Target = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings",
                    Parameters = new Dictionary<string, string>
                    {
                        { "values", "ProxyServer,ProxyEnable,ProxyOverride,AutoConfigURL" }
                    },
                    Trigger = "startup",
                    OutputEventType = "gather_proxy_settings",
                    Tags = new[] { "network", "proxy", "common" },
                    Author = "Autopilot Monitor"
                },
                new GatherRule
                {
                    RuleId = "GATHER-NET-002",
                    Title = "Collect DNS Configuration",
                    Description = "Queries DNS client server addresses to verify DNS resolution capability.",
                    Category = "network",
                    CollectorType = "command",
                    Target = "Get-DnsClientServerAddress | Select-Object InterfaceAlias, ServerAddresses",
                    Trigger = "startup",
                    OutputEventType = "gather_dns_config",
                    Tags = new[] { "network", "dns" },
                    Author = "Autopilot Monitor"
                },
                new GatherRule
                {
                    RuleId = "GATHER-NET-003",
                    Title = "Collect Network Adapters",
                    Description = "Lists all network adapters and their status via WMI.",
                    Category = "network",
                    CollectorType = "wmi",
                    Target = "SELECT Name, NetConnectionStatus, Speed, MACAddress FROM Win32_NetworkAdapter WHERE NetConnectionStatus IS NOT NULL",
                    Trigger = "startup",
                    OutputEventType = "gather_network_adapters",
                    Tags = new[] { "network", "adapters" },
                    Author = "Autopilot Monitor"
                },

                // ===== IDENTITY RULES =====
                new GatherRule
                {
                    RuleId = "GATHER-ID-001",
                    Title = "Collect AAD Join Status",
                    Description = "Reads Azure AD join state from the registry to verify device identity.",
                    Category = "identity",
                    CollectorType = "registry",
                    Target = @"HKLM:\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo",
                    Parameters = new Dictionary<string, string>
                    {
                        { "recurse", "true" }
                    },
                    Trigger = "phase_change",
                    TriggerPhase = "Identity",
                    OutputEventType = "gather_aad_join_status",
                    Tags = new[] { "identity", "aad", "entra" },
                    Author = "Autopilot Monitor"
                },
                new GatherRule
                {
                    RuleId = "GATHER-ID-002",
                    Title = "Collect Device Certificates",
                    Description = "Lists certificates in the machine personal store to verify MDM and device certificates.",
                    Category = "identity",
                    CollectorType = "command",
                    Target = "certutil -store My",
                    Trigger = "phase_change",
                    TriggerPhase = "Identity",
                    OutputEventType = "gather_device_certificates",
                    Tags = new[] { "identity", "certificates" },
                    Author = "Autopilot Monitor"
                },

                // ===== MDM ENROLLMENT RULES =====
                new GatherRule
                {
                    RuleId = "GATHER-MDM-001",
                    Title = "Collect MDM Enrollment Status",
                    Description = "Reads MDM enrollment registry keys to verify Intune enrollment state.",
                    Category = "enrollment",
                    CollectorType = "registry",
                    Target = @"HKLM:\SOFTWARE\Microsoft\Enrollments",
                    Parameters = new Dictionary<string, string>
                    {
                        { "recurse", "true" },
                        { "maxDepth", "2" }
                    },
                    Trigger = "phase_change",
                    TriggerPhase = "MdmEnrollment",
                    OutputEventType = "gather_mdm_enrollment_status",
                    Tags = new[] { "enrollment", "mdm", "intune" },
                    Author = "Autopilot Monitor"
                },

                // ===== APP INSTALLATION RULES =====
                new GatherRule
                {
                    RuleId = "GATHER-APP-001",
                    Title = "Collect IME App Inventory",
                    Description = "Reads the Intune Management Extension app inventory from registry.",
                    Category = "apps",
                    CollectorType = "registry",
                    Target = @"HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps",
                    Parameters = new Dictionary<string, string>
                    {
                        { "recurse", "true" },
                        { "maxDepth", "2" }
                    },
                    Trigger = "phase_change",
                    TriggerPhase = "AppInstallation",
                    OutputEventType = "gather_ime_app_inventory",
                    Tags = new[] { "apps", "ime", "win32" },
                    Author = "Autopilot Monitor"
                },
                new GatherRule
                {
                    RuleId = "GATHER-APP-002",
                    Title = "Collect Win32App Detection Results",
                    Description = "Periodically checks Win32 app detection script results during app installation phase.",
                    Category = "apps",
                    CollectorType = "registry",
                    Target = @"HKLM:\SOFTWARE\Microsoft\IntuneManagementExtension\Win32Apps",
                    Parameters = new Dictionary<string, string>
                    {
                        { "recurse", "true" },
                        { "filter", "DetectionState" }
                    },
                    Trigger = "interval",
                    IntervalSeconds = 60,
                    OutputEventType = "gather_win32app_detection",
                    Tags = new[] { "apps", "detection", "win32" },
                    Author = "Autopilot Monitor"
                },

                // ===== ESP RULES =====
                new GatherRule
                {
                    RuleId = "GATHER-ESP-001",
                    Title = "Collect ESP Tracking Policies",
                    Description = "Reads ESP tracking policy list from registry to identify blocking apps and policies.",
                    Category = "esp",
                    CollectorType = "registry",
                    Target = @"HKLM:\SOFTWARE\Microsoft\Enrollments",
                    Parameters = new Dictionary<string, string>
                    {
                        { "recurse", "true" },
                        { "filter", "FirstSync" }
                    },
                    Trigger = "phase_change",
                    TriggerPhase = "EspDeviceSetup",
                    OutputEventType = "gather_esp_tracking_policies",
                    Tags = new[] { "esp", "tracking", "blocking" },
                    Author = "Autopilot Monitor"
                },

                // ===== DEVICE RULES =====
                new GatherRule
                {
                    RuleId = "GATHER-DEV-001",
                    Title = "Collect TPM Status",
                    Description = "Queries TPM status via WMI to verify TPM readiness for Autopilot.",
                    Category = "device",
                    CollectorType = "wmi",
                    Target = "SELECT * FROM Win32_Tpm",
                    Parameters = new Dictionary<string, string>
                    {
                        { "namespace", @"root\CIMV2\Security\MicrosoftTpm" }
                    },
                    Trigger = "startup",
                    OutputEventType = "gather_tpm_status",
                    Tags = new[] { "device", "tpm", "security" },
                    Author = "Autopilot Monitor"
                },
                new GatherRule
                {
                    RuleId = "GATHER-DEV-002",
                    Title = "Collect BitLocker Status",
                    Description = "Queries BitLocker volume encryption status.",
                    Category = "device",
                    CollectorType = "wmi",
                    Target = "SELECT * FROM Win32_EncryptableVolume",
                    Parameters = new Dictionary<string, string>
                    {
                        { "namespace", @"root\CIMV2\Security\MicrosoftVolumeEncryption" }
                    },
                    Trigger = "phase_change",
                    TriggerPhase = "EspDeviceSetup",
                    OutputEventType = "gather_bitlocker_status",
                    Tags = new[] { "device", "bitlocker", "encryption" },
                    Author = "Autopilot Monitor"
                },
                new GatherRule
                {
                    RuleId = "GATHER-DEV-003",
                    Title = "Collect SecureBoot Status",
                    Description = "Reads Secure Boot state from the registry.",
                    Category = "device",
                    CollectorType = "registry",
                    Target = @"HKLM:\SYSTEM\CurrentControlSet\Control\SecureBoot\State",
                    Parameters = new Dictionary<string, string>
                    {
                        { "values", "UEFISecureBootEnabled" }
                    },
                    Trigger = "startup",
                    OutputEventType = "gather_secureboot_status",
                    Tags = new[] { "device", "secureboot", "security" },
                    Author = "Autopilot Monitor"
                }
            };
        }
    }
}
