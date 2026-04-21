using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Indexing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Queue-triggered consumer for the <c>telemetry-index-reconcile</c> queue (Plan §2.8, §M5.d.3).
    /// Thin wrapper around <see cref="IndexReconcileHandler"/> — deserializes the JSON message
    /// and hands it off; no business logic lives here.
    /// <para>
    /// <b>Connection:</b> <c>AzureTableStorageConnectionString</c> (same storage account the
    /// producer writes to — queue + tables colocate for atomic dev/test setup).
    /// </para>
    /// <para>
    /// <b>Retry / poison:</b> platform default (5 retries, then
    /// <c>telemetry-index-reconcile-poison</c>). Malformed JSON is logged + dropped here so it
    /// doesn't retry-loop forever; transient repo failures propagate so the platform retries.
    /// </para>
    /// </summary>
    public sealed class IndexReconcileFunction
    {
        private readonly IndexReconcileHandler _handler;
        private readonly ILogger<IndexReconcileFunction> _logger;

        public IndexReconcileFunction(
            IndexReconcileHandler handler,
            ILogger<IndexReconcileFunction> logger)
        {
            _handler = handler;
            _logger = logger;
        }

        [Function("IndexReconcile")]
        public async Task Run(
            [QueueTrigger(Constants.QueueNames.TelemetryIndexReconcile, Connection = "AzureTableStorageConnectionString")] string message,
            CancellationToken cancellationToken)
        {
            IndexReconcileEnvelope? envelope;
            try
            {
                envelope = JsonConvert.DeserializeObject<IndexReconcileEnvelope>(message);
            }
            catch (JsonException ex)
            {
                // Malformed JSON is permanent; don't let it retry-loop the queue.
                _logger.LogWarning(ex, "IndexReconcile: malformed envelope JSON — dropping");
                return;
            }

            if (envelope is null)
            {
                _logger.LogWarning("IndexReconcile: null envelope after deserialization — dropping");
                return;
            }

            await _handler.HandleAsync(envelope, cancellationToken);
        }
    }
}
