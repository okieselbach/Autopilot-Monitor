using System.Collections.Generic;
using Newtonsoft.Json;

namespace AutopilotMonitor.SummaryDialog.Models
{
    public class FinalStatus
    {
        [JsonProperty("timestamp")]
        public string Timestamp { get; set; }

        [JsonProperty("outcome")]
        public string Outcome { get; set; }

        [JsonProperty("completionSource")]
        public string CompletionSource { get; set; }

        [JsonProperty("helloOutcome")]
        public string HelloOutcome { get; set; }

        [JsonProperty("enrollmentType")]
        public string EnrollmentType { get; set; }

        [JsonProperty("agentUptimeSeconds")]
        public double AgentUptimeSeconds { get; set; }

        [JsonProperty("signalsSeen")]
        public List<string> SignalsSeen { get; set; } = new List<string>();

        [JsonProperty("appSummary")]
        public AppSummary AppSummary { get; set; }

        [JsonProperty("packageStatesByPhase")]
        public Dictionary<string, List<PackageInfo>> PackageStatesByPhase { get; set; } = new Dictionary<string, List<PackageInfo>>();
    }

    public class AppSummary
    {
        [JsonProperty("totalApps")]
        public int TotalApps { get; set; }

        [JsonProperty("completedApps")]
        public int CompletedApps { get; set; }

        [JsonProperty("errorCount")]
        public int ErrorCount { get; set; }

        [JsonProperty("deviceErrors")]
        public int DeviceErrors { get; set; }

        [JsonProperty("userErrors")]
        public int UserErrors { get; set; }

        [JsonProperty("appsByPhase")]
        public Dictionary<string, int> AppsByPhase { get; set; } = new Dictionary<string, int>();
    }

    public class PackageInfo
    {
        [JsonProperty("appName")]
        public string AppName { get; set; }

        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("isError")]
        public bool IsError { get; set; }

        [JsonProperty("isCompleted")]
        public bool IsCompleted { get; set; }

        [JsonProperty("targeted")]
        public string Targeted { get; set; }
    }
}
