using System.Collections.Generic;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Wire-format DTO consumed by <c>AutopilotMonitor.SummaryDialog.exe</c>. Plan §4.x M4.6.β.
    /// <para>
    /// Mirrors <c>AutopilotMonitor.SummaryDialog.Models.FinalStatus</c> — the dialog deserializes
    /// this JSON on launch and renders the summary. We cannot reference the dialog project
    /// directly (WPF net48 WinExe), so the shape is duplicated here. Keep the JSON property names
    /// in lockstep with the dialog — changes here require a corresponding update in the dialog.
    /// </para>
    /// </summary>
    public sealed class FinalStatus
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
        public FinalStatusAppSummary AppSummary { get; set; } = new FinalStatusAppSummary();

        [JsonProperty("packageStatesByPhase")]
        public Dictionary<string, List<FinalStatusPackageInfo>> PackageStatesByPhase { get; set; } =
            new Dictionary<string, List<FinalStatusPackageInfo>>();
    }

    public sealed class FinalStatusAppSummary
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

    public sealed class FinalStatusPackageInfo
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

        // Plan §5 Fix 4a / 4c — per-app install-lifecycle timestamps. Omitted from the
        // JSON when not yet captured (e.g. agent started mid-install), so the dialog can
        // distinguish "not tracked" from "stamp=epoch".
        [JsonProperty("startedAt", NullValueHandling = NullValueHandling.Ignore)]
        public string StartedAt { get; set; }

        [JsonProperty("completedAt", NullValueHandling = NullValueHandling.Ignore)]
        public string CompletedAt { get; set; }

        [JsonProperty("durationSeconds", NullValueHandling = NullValueHandling.Ignore)]
        public double? DurationSeconds { get; set; }
    }
}
