using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Built-in gather rules shipped with the system.
    ///
    /// NOTE: All network, identity, device, ESP, and IME log parsing rules have been moved
    /// to the EnrollmentTracker (direct WMI/Registry collection) and ImeLogTracker (IME log
    /// patterns via BuiltInImeLogPatterns). GatherRules now serve only as a mechanism for
    /// user-defined ad-hoc collection rules.
    /// </summary>
    public static class BuiltInGatherRules
    {
        public static List<GatherRule> GetAll()
        {
            return new List<GatherRule>
            {
                // dsregcmd /status — collect Azure AD / Entra join status during Device Preparation
                new GatherRule
                {
                    RuleId = "GATHER-DEVICE-001",
                    Title = "Collect dsregcmd join status",
                    Description = "Runs dsregcmd /status to capture the Azure AD / Entra join state when the device enters the Device Preparation phase.",
                    Category = "device",
                    Version = "1.0.0",
                    Author = "Autopilot Monitor",
                    Enabled = false,
                    IsBuiltIn = true,
                    CollectorType = "command_allowlisted",
                    Target = "dsregcmd /status",
                    Parameters = new Dictionary<string, string>(),
                    Trigger = "phase_change",
                    TriggerPhase = "DevicePreparation",
                    OutputEventType = "gather_dsregcmd_status",
                    OutputSeverity = "Info",
                    Tags = new[] { "device", "entra", "join" }
                },

                // Last system startup event from the System event log
                new GatherRule
                {
                    RuleId = "GATHER-DEVICE-002",
                    Title = "Collect last system startup event",
                    Description = "Collects the most recent Event ID 12 (Kernel-General) from the System log which indicates the last OS startup time.",
                    Category = "device",
                    Version = "1.0.0",
                    Author = "Autopilot Monitor",
                    Enabled = false,
                    IsBuiltIn = true,
                    CollectorType = "eventlog",
                    Target = "System",
                    Parameters = new Dictionary<string, string>
                    {
                        { "eventId", "12" },
                        { "source", "Microsoft-Windows-Kernel-General" },
                        { "maxEntries", "1" }
                    },
                    Trigger = "startup",
                    OutputEventType = "gather_last_startup_event",
                    OutputSeverity = "Info",
                    Tags = new[] { "device", "startup", "boot" }
                }
            };
        }
    }
}
