#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Abstraction über den Backend-Aufruf zur Batch-Telemetry-Übertragung. Plan §2.7a.
    /// <para>
    /// Produktionsimplementierung in <c>BackendApiClient</c> (ruft <c>POST /api/telemetry/batch</c>).
    /// Tests verwenden einen Fake-Uploader — der Orchestrator ist HTTP-agnostisch.
    /// </para>
    /// </summary>
    public interface IBackendTelemetryUploader
    {
        /// <summary>
        /// Sendet <paramref name="items"/> als einen Batch an das Backend. Backend-Ingest
        /// ist idempotent via (<c>PartitionKey</c>, <c>RowKey</c>) — Doppel-Upload nach Retry ist no-op.
        /// </summary>
        Task<UploadResult> UploadBatchAsync(IReadOnlyList<TelemetryItem> items, CancellationToken cancellationToken);
    }
}
