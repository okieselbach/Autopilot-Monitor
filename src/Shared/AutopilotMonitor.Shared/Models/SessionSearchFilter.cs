using System;

namespace AutopilotMonitor.Shared.Models
{
    public class SessionSearchFilter
    {
        // Basic (SessionSummary fields)
        public string Status { get; set; }
        public string SerialNumber { get; set; }
        public string AgentVersion { get; set; }
        public string ImeAgentVersion { get; set; }
        public string DeviceName { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public string OsBuild { get; set; }
        public string EnrollmentType { get; set; }
        public bool? IsPreProvisioned { get; set; }
        public bool? IsHybridJoin { get; set; }
        public string GeoCountry { get; set; }
        public DateTime? StartedAfter { get; set; }
        public DateTime? StartedBefore { get; set; }
        public int Limit { get; set; } = 50;

        // Extended (DeviceSnapshot fields)
        public string TpmSpecVersion { get; set; }
        public bool? TpmActivated { get; set; }
        public bool? SecureBootEnabled { get; set; }
        public bool? BitlockerEnabled { get; set; }
        public string AutopilotMode { get; set; }
        public string DomainJoinMethod { get; set; }
        public string ConnectionType { get; set; }
        public double? MinRamGB { get; set; }
        public bool? HasSSD { get; set; }

        public bool HasDeviceSnapshotFilters =>
            TpmSpecVersion != null || TpmActivated.HasValue || SecureBootEnabled.HasValue ||
            BitlockerEnabled.HasValue || AutopilotMode != null || DomainJoinMethod != null ||
            ConnectionType != null || MinRamGB.HasValue || HasSSD.HasValue;
    }
}
