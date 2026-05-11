using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Identity;
using Azure.Storage.Queues;
using Microsoft.Extensions.Configuration;

namespace AutopilotMonitor.Functions.Services.Monitoring
{
    /// <summary>
    /// Azure Storage Queue implementation of <see cref="IPoisonQueueProbe"/>.
    /// Mirrors the connection-resolution pattern used by the existing queue workers
    /// (AnalyzeOnEnrollmentEndQueueWorker, IndexReconcileQueueWorker,
    /// VulnerabilityCorrelateQueueWorker): prefer Managed Identity via
    /// <c>AzureStorageAccountName</c>, fall back to <c>AzureTableStorageConnectionString</c>.
    /// QueueClient instances are cached per queue name — they are thread-safe and
    /// cheap to keep around for the lifetime of the Functions host.
    /// </summary>
    public sealed class AzurePoisonQueueProbe : IPoisonQueueProbe
    {
        private readonly ConcurrentDictionary<string, QueueClient> _clients = new();
        private readonly Func<string, QueueClient> _clientFactory;

        public AzurePoisonQueueProbe(IConfiguration configuration)
        {
            if (configuration is null) throw new ArgumentNullException(nameof(configuration));

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureTableStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var credential = new DefaultAzureCredential();
                _clientFactory = queueName =>
                {
                    var uri = new Uri(
                        $"https://{storageAccountName}.queue.core.windows.net/{queueName}");
                    return new QueueClient(uri, credential);
                };
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _clientFactory = queueName => new QueueClient(connectionString, queueName);
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        public async Task<long> GetApproximateMessageCountAsync(string queueName, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(queueName))
                throw new ArgumentException("Queue name must not be empty.", nameof(queueName));

            var client = _clients.GetOrAdd(queueName, _clientFactory);

            try
            {
                var props = await client.GetPropertiesAsync(ct).ConfigureAwait(false);
                return props.Value.ApproximateMessagesCount;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Poison queue is created lazily by the worker on first poison-move.
                // A 404 is the steady-state for queues that have never had a failure,
                // and is exactly the "healthy" signal we want to surface.
                return 0;
            }
        }
    }
}
