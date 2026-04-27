using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Indexing
{
    /// <summary>
    /// Background worker for the <c>telemetry-index-reconcile</c> queue (Plan §2.8, §M5.d.3).
    /// <para>
    /// Replaces the earlier <c>IndexReconcileFunction</c> QueueTrigger because the trigger
    /// binding required a Functions-host-specific connection app-setting
    /// (<c>&lt;Connection&gt;__queueServiceUri</c>) that the rest of the project's storage
    /// access does not need. This worker matches the
    /// <see cref="AzureQueueIndexReconcileProducer"/>, <see cref="TableStorageService"/> and
    /// <see cref="BlobStorageService"/> pattern: pure DI service driven by the existing
    /// <c>AzureStorageAccountName</c> (Managed Identity) or <c>AzureTableStorageConnectionString</c>
    /// fallback — no bespoke binding glue.
    /// </para>
    /// <para>
    /// <b>Plattform-Parität zum QueueTrigger:</b>
    /// <list type="bullet">
    ///   <item>Receive batch of up to <see cref="BatchSize"/> messages with
    ///     <see cref="VisibilityTimeout"/> visibility-timeout per message.</item>
    ///   <item>Handler success → <c>DeleteMessageAsync</c>.</item>
    ///   <item>Handler exception → no delete; message becomes visible again after the
    ///     timeout, <c>DequeueCount</c> increments. Identical to platform-retry semantics.</item>
    ///   <item>After <see cref="MaxDequeueCount"/> failed attempts the message is moved to
    ///     <c>telemetry-index-reconcile-poison</c> and deleted from the main queue. Same
    ///     behaviour as the QueueTrigger platform.</item>
    ///   <item>Empty receive → sleep for <see cref="PollInterval"/> and retry.</item>
    ///   <item>Unrecoverable poll-loop exception (e.g. transient Storage outage) → log + back
    ///     off for <see cref="ErrorBackoff"/> before resuming.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class IndexReconcileQueueWorker : BackgroundService
    {
        /// <summary>Max messages received per poll. Storage Queue caps batch-receive at 32.</summary>
        internal const int BatchSize = 32;

        /// <summary>
        /// Visibility timeout per received message — long enough to cover the slowest
        /// expected <see cref="IndexReconcileHandler.HandleAsync"/> path (5 index-table writes).
        /// </summary>
        internal static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

        /// <summary>Idle sleep between empty receives.</summary>
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        /// <summary>Cool-down after an unhandled poll-loop exception.</summary>
        internal static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Match the QueueTrigger platform default: 5 attempts, then move to poison.
        /// (Storage Queue's <c>DequeueCount</c> increments on each visibility-timeout reappear.)
        /// </summary>
        internal const int MaxDequeueCount = 5;

        private const string PoisonQueueSuffix = "-poison";

        private readonly QueueClient _mainQueue;
        private readonly QueueClient _poisonQueue;
        private readonly IndexReconcileHandler _handler;
        private readonly ILogger<IndexReconcileQueueWorker> _logger;

        public IndexReconcileQueueWorker(
            IConfiguration configuration,
            IndexReconcileHandler handler,
            ILogger<IndexReconcileQueueWorker> logger)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // Match the producer's binding base64 encoding so messages round-trip cleanly.
            var options = new QueueClientOptions
            {
                MessageEncoding = QueueMessageEncoding.Base64,
            };

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureTableStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var mainUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.TelemetryIndexReconcile}");
                var poisonUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.TelemetryIndexReconcile}{PoisonQueueSuffix}");
                var credential = new DefaultAzureCredential();
                _mainQueue = new QueueClient(mainUri, credential, options);
                _poisonQueue = new QueueClient(poisonUri, credential, options);
                _logger.LogInformation(
                    "IndexReconcileQueueWorker initialized with Managed Identity (account: {Account})",
                    storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _mainQueue = new QueueClient(
                    connectionString, Constants.QueueNames.TelemetryIndexReconcile, options);
                _poisonQueue = new QueueClient(
                    connectionString, Constants.QueueNames.TelemetryIndexReconcile + PoisonQueueSuffix, options);
                _logger.LogInformation("IndexReconcileQueueWorker initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Ensure both queues exist before the first receive — the producer creates the
            // main queue on first send too, but having the poison queue ready avoids losing
            // the first poison-move attempt.
            await TryCreateQueueAsync(_mainQueue, "main", stoppingToken).ConfigureAwait(false);
            await TryCreateQueueAsync(_poisonQueue, "poison", stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("IndexReconcileQueueWorker: poll loop started (queue {Name})",
                _mainQueue.Name);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var batch = await _mainQueue
                        .ReceiveMessagesAsync(BatchSize, VisibilityTimeout, stoppingToken)
                        .ConfigureAwait(false);

                    if (batch?.Value is null || batch.Value.Length == 0)
                    {
                        await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var msg in batch.Value)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        await ProcessOneAsync(msg, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "IndexReconcileQueueWorker: poll-loop error — backing off {Backoff}",
                        ErrorBackoff);
                    try { await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _logger.LogInformation("IndexReconcileQueueWorker: poll loop stopped");
        }

        private async Task ProcessOneAsync(QueueMessage msg, CancellationToken ct)
        {
            // Plattform-Parität: nach MaxDequeueCount Versuchen Move-to-poison + Delete-from-main.
            // DequeueCount > 5 bedeutet: 5 erfolglose Verarbeitungs-Versuche sind durch, der 6.
            // Receive ist jetzt — den schicken wir nicht mehr an den Handler.
            if (msg.DequeueCount > MaxDequeueCount)
            {
                await MoveToPoisonAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            IndexReconcileEnvelope? envelope;
            try
            {
                envelope = JsonConvert.DeserializeObject<IndexReconcileEnvelope>(msg.Body.ToString());
            }
            catch (JsonException ex)
            {
                // Malformed JSON is permanent; drop directly so it doesn't retry-loop and waste
                // dequeue attempts. Same call as the old QueueTrigger function.
                _logger.LogWarning(ex,
                    "IndexReconcileQueueWorker: malformed envelope JSON — dropping (msg {Id})",
                    msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (envelope is null)
            {
                _logger.LogWarning(
                    "IndexReconcileQueueWorker: null envelope after deserialization — dropping (msg {Id})",
                    msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                await _handler.HandleAsync(envelope, ct).ConfigureAwait(false);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown — let the message become visible again so the next worker run picks it up.
                throw;
            }
            catch (Exception ex)
            {
                // Don't delete: visibility-timeout will make the message visible again, DequeueCount
                // increments, eventual move-to-poison after MaxDequeueCount. Identical retry shape
                // to the QueueTrigger platform.
                _logger.LogWarning(ex,
                    "IndexReconcileQueueWorker: handler failed (msg {Id}, dequeue {N}) — visibility-timeout retry",
                    msg.MessageId, msg.DequeueCount);
            }
        }

        private async Task MoveToPoisonAsync(QueueMessage msg, CancellationToken ct)
        {
            try
            {
                await _poisonQueue.SendMessageAsync(msg.Body.ToString(), ct).ConfigureAwait(false);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "IndexReconcileQueueWorker: moved message {Id} to poison queue after {N} failed attempts",
                    msg.MessageId, msg.DequeueCount - 1);
            }
            catch (Exception ex)
            {
                // If poison-enqueue itself fails, leave the message — next visibility-timeout
                // will retry the move. Avoids losing data on transient outages.
                _logger.LogError(ex,
                    "IndexReconcileQueueWorker: poison move failed for message {Id} (will retry)",
                    msg.MessageId);
            }
        }

        private async Task SafeDeleteAsync(QueueMessage msg, CancellationToken ct)
        {
            try
            {
                await _mainQueue.DeleteMessageAsync(msg.MessageId, msg.PopReceipt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "IndexReconcileQueueWorker: delete failed for message {Id} (will reappear after visibility-timeout)",
                    msg.MessageId);
            }
        }

        private async Task TryCreateQueueAsync(QueueClient queue, string label, CancellationToken ct)
        {
            try
            {
                await queue.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "IndexReconcileQueueWorker: CreateIfNotExists failed for {Label} queue — will continue, send/receive will retry",
                    label);
            }
        }
    }
}
