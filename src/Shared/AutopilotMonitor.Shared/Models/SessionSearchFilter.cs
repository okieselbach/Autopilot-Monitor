using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    public class SessionSearchFilter
    {
        // Basic (SessionSummary fields)
        public string? Status { get; set; }
        public string? SerialNumber { get; set; }
        public string? AgentVersion { get; set; }
        public string? ImeAgentVersion { get; set; }
        public string? DeviceName { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? OsBuild { get; set; }
        public string? EnrollmentType { get; set; }
        public bool? IsPreProvisioned { get; set; }
        public bool? IsHybridJoin { get; set; }
        public string? GeoCountry { get; set; }
        public DateTime? StartedAfter { get; set; }
        public DateTime? StartedBefore { get; set; }
        public int Limit { get; set; } = 50;

        // Dynamic device property filters (key = "eventType.propertyName", value = filter expression)
        // Examples: "tpm_status.specVersion" = "2.0", "hardware_spec.ramTotalGB" = ">=8"
        public Dictionary<string, string>? DeviceProperties { get; set; }

        public bool HasDeviceSnapshotFilters =>
            DeviceProperties != null && DeviceProperties.Count > 0;
    }
}
