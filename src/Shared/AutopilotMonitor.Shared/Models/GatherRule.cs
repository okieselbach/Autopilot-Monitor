using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Defines what data the agent should collect
    /// Gather rules are delivered to the agent via the config API
    /// and can be managed (enabled/disabled, created) through the portal
    /// </summary>
    public class GatherRule
    {
        /// <summary>
        /// Unique rule identifier (e.g., "GATHER-NET-001")
        /// </summary>
        public string RuleId { get; set; }

        /// <summary>
        /// Human-readable rule title (e.g., "Collect WinHTTP Proxy Settings")
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Detailed description of what this rule collects and why
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Rule category: network, identity, apps, device, esp, enrollment
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Semantic version of this rule (e.g., "1.0.0")
        /// </summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Author of this rule
        /// </summary>
        public string Author { get; set; } = "Autopilot Monitor";

        /// <summary>
        /// Whether this rule is currently enabled for the tenant
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether this is a built-in rule (shipped with the system)
        /// Built-in rules cannot be deleted, only disabled
        /// </summary>
        public bool IsBuiltIn { get; set; } = true;

        /// <summary>
        /// Whether this is a community-contributed rule (future use)
        /// </summary>
        public bool IsCommunity { get; set; } = false;

        // ===== WHAT TO COLLECT =====

        /// <summary>
        /// Type of data collection: "registry", "eventlog", "wmi", "file", "command", "logparser"
        /// </summary>
        public string CollectorType { get; set; }

        /// <summary>
        /// Target for collection:
        /// - registry: Registry path (e.g., "HKLM:\SOFTWARE\Microsoft\...")
        /// - eventlog: Event log name (e.g., "Microsoft-Windows-DeviceManagement...")
        /// - wmi: WMI query (e.g., "SELECT * FROM Win32_TPM")
        /// - file: File path (e.g., "C:\Windows\INF\setupapi.dev.log")
        /// - command: Command name from allowlist (e.g., "Get-TpmStatus")
        /// - logparser: Log file path with env vars (e.g., "%ProgramData%\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log")
        /// </summary>
        public string Target { get; set; }

        /// <summary>
        /// Additional parameters for the collector
        /// - registry: { "values": "ProxyServer,ProxyEnable" }
        /// - eventlog: { "eventIds": "407,12029", "maxEvents": "10" }
        /// - wmi: { "namespace": "root\\CIMV2\\Security\\MicrosoftTpm" }
        /// - file: { "maxLines": "100", "pattern": "error|fail" }
        /// - command: { "arguments": "-Detailed" }
        /// - logparser: { "pattern": "regex with (?&lt;named&gt;groups)", "logFormat": "cmtrace", "trackPosition": "true", "maxLines": "1000" }
        /// </summary>
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();

        // ===== WHEN TO COLLECT =====

        /// <summary>
        /// Trigger type: "startup", "phase_change", "interval", "on_event"
        /// </summary>
        public string Trigger { get; set; }

        /// <summary>
        /// Interval in seconds (only used when Trigger = "interval")
        /// </summary>
        public int? IntervalSeconds { get; set; }

        /// <summary>
        /// Phase to trigger on (only used when Trigger = "phase_change")
        /// e.g., "Identity", "MdmEnrollment", "AppInstallation"
        /// </summary>
        public string TriggerPhase { get; set; }

        /// <summary>
        /// Event type to trigger on (only used when Trigger = "on_event")
        /// e.g., "app_install_failed"
        /// </summary>
        public string TriggerEventType { get; set; }

        // ===== OUTPUT =====

        /// <summary>
        /// EventType for the emitted event (e.g., "gather_proxy_settings")
        /// </summary>
        public string OutputEventType { get; set; }

        /// <summary>
        /// Severity for the emitted event
        /// Default: "Info"
        /// </summary>
        public string OutputSeverity { get; set; } = "Info";

        // ===== METADATA =====

        /// <summary>
        /// Tags for filtering and categorization
        /// </summary>
        public string[] Tags { get; set; } = new string[0];

        /// <summary>
        /// When this rule was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this rule was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
