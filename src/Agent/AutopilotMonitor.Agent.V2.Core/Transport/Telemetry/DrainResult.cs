#nullable enable
namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Aggregat-Ergebnis eines <see cref="ITelemetryTransport.DrainAllAsync"/>-Laufs. Plan §2.7a.
    /// </summary>
    public sealed class DrainResult
    {
        public DrainResult(int uploadedItems, int failedBatches, string? lastErrorReason)
        {
            UploadedItems = uploadedItems;
            FailedBatches = failedBatches;
            LastErrorReason = lastErrorReason;
        }

        public int UploadedItems { get; }
        public int FailedBatches { get; }
        public string? LastErrorReason { get; }

        public bool Success => FailedBatches == 0;

        public static DrainResult Empty() => new DrainResult(0, 0, null);
    }
}
