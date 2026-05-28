#nullable enable
using System;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Diagnostics
{
    /// <summary>
    /// Serializable record of a crash captured by <see cref="CrashDumpCapture"/>.
    /// One JSON file per crash sits next to the dump in <c>%ProgramData%\AutopilotMonitor\Crashes\</c>
    /// until <see cref="PendingCrashReporter"/> picks it up on the next agent start and emits the
    /// <c>previous_crash_detected</c> event.
    /// </summary>
    public sealed class CrashRecord
    {
        [JsonProperty("crashedAt")]
        public DateTime CrashedAt { get; set; }

        [JsonProperty("sessionId")]
        public string? SessionId { get; set; }

        [JsonProperty("tenantId")]
        public string? TenantId { get; set; }

        [JsonProperty("agentVersion")]
        public string? AgentVersion { get; set; }

        [JsonProperty("trigger")]
        public string Trigger { get; set; } = string.Empty;

        [JsonProperty("exceptionType")]
        public string? ExceptionType { get; set; }

        [JsonProperty("exceptionMessage")]
        public string? ExceptionMessage { get; set; }

        [JsonProperty("stackTrace")]
        public string? StackTrace { get; set; }

        [JsonProperty("dumpFilePath")]
        public string? DumpFilePath { get; set; }

        [JsonProperty("dumpFileSizeBytes")]
        public long? DumpFileSizeBytes { get; set; }

        [JsonProperty("dumpWriteSucceeded")]
        public bool DumpWriteSucceeded { get; set; }
    }
}
