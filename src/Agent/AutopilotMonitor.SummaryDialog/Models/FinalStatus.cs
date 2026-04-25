using System.Collections.Generic;
using Newtonsoft.Json;

namespace AutopilotMonitor.SummaryDialog.Models
{
    public class FinalStatus
    {
        /// <summary>
        /// Wire-format version. <c>0</c> (default — field absent) means a V1 agent wrote
        /// this file; the dialog uses the V1 backward-compat render path. <c>2</c> means
        /// V2 agent with richer outcome/error fields — the dialog uses the modern renderer.
        /// </summary>
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; }

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

        /// <summary>
        /// Schema 2 — human-readable explanation rendered as a banner under the outcome
        /// header for non-success outcomes. Null/empty for success cases.
        /// </summary>
        [JsonProperty("failureReason", NullValueHandling = NullValueHandling.Ignore)]
        public string FailureReason { get; set; }

        /// <summary>
        /// Schema 2 — milestone signal timestamps (ISO-8601). Diagnostic-only, not rendered
        /// by the dialog UI but preserved here for field engineers reading the JSON post-mortem.
        /// </summary>
        [JsonProperty("signalTimestamps", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> SignalTimestamps { get; set; }

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

        // Schema 2 fields — present only when emitted by a V2 agent. Older V1 JSON
        // simply lacks them (Newtonsoft leaves them null, which the renderer treats
        // as "no extra info available").
        [JsonProperty("durationSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public double? DurationSeconds { get; set; }

        [JsonProperty("errorPatternId", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorPatternId { get; set; }

        [JsonProperty("errorDetail", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorDetail { get; set; }

        [JsonProperty("errorCode", NullValueHandling = NullValueHandling.Ignore)]
        public string ErrorCode { get; set; }
    }
}
