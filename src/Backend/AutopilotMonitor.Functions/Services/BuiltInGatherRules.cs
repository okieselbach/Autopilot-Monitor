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
            // No built-in rules - all data collection is now handled by:
            // - EnrollmentTracker: device info (network, DNS, proxy, autopilot profile, SecureBoot, BitLocker, AAD join)
            // - ImeLogTracker: IME log parsing with patterns from BuiltInImeLogPatterns
            // - PerformanceCollector: CPU/memory/disk (always on)
            // - CertValidationCollector: certificate validation (optional toggle)
            // - EventLogWatcher, RegistryMonitor, PhaseDetector, HelloDetector: core collectors
            //
            // GatherRules remain as a mechanism for user-defined ad-hoc collection only.
            return new List<GatherRule>();
        }
    }
}
