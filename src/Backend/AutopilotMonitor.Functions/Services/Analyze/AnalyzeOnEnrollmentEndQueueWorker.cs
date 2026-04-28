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

namespace AutopilotMonitor.Functions.Services.Analyze
{
    /// <summary>
    /// Background worker for the <c>analyze-on-enrollment-end</c> queue. Replaces the
    /// previous in-function fire-and-forget Task.Run that ran the rule engine after
    /// session-terminal events — that pattern could be killed mid-flight by Azure Functions
    /// scale-in, leaving rule results un-persisted.
    /// <para>
    /// Mirrors <c>IndexReconcileQueueWorker</c> instead of using a Functions <c>QueueTrigger</c>
    /// because the trigger binding requires a Functions-host-specific connection app-setting
    /// (<c>&lt;Connection&gt;__queueServiceUri</c>) that the rest of the project's storage
    /// access does not need. Pure DI BackgroundService driven by the existing
    /// <c>AzureStorageAccountName</c> (Managed Identity) or <c>AzureTableStorageConnectionString</c>
    /// fallback — no bespoke binding glue.
    /// </para>
    /// <para>
    /// <b>Platform parity to QueueTrigger:</b>
    /// <list type="bullet">
    ///   <item>Receive batch of up to <see cref="BatchSize"/> messages with
    ///     <see cref="VisibilityTimeout"/> visibility-timeout per message.</item>
    ///   <item>Handler success → <c>DeleteMessageAsync</c>.</item>
    ///   <item>Handler exception → no delete; message becomes visible again after the timeout,
    ///     <c>DequeueCount</c> increments. Identical to platform-retry semantics.</item>
    ///   <item>After <see cref="MaxDequeueCount"/> failed attempts the message is moved to
    ///     <c>analyze-on-enrollment-end-poison</c> and deleted from the main queue.</item>
    ///   <item>Empty receive → sleep for <see cref="PollInterval"/> and retry.</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class AnalyzeOnEnrollmentEndQueueWorker : BackgroundService
    {
        /// <summary>Max messages received per poll. Storage Queue caps batch-receive at 32.</summary>
        internal const int BatchSize = 32;

        /// <summary>
        /// Visibility timeout per received message. Rule evaluation reads all session events
        /// + N rules, then writes 0..N rule-result rows + per-rule stat rows; large sessions
        /// can take several seconds. 5 minutes leaves plenty of headroom and matches the
        /// IndexReconcile worker.
        /// </summary>
        internal static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

        /// <summary>Idle sleep between empty receives.</summary>
        internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

        /// <summary>Cool-down after an unhandled poll-loop exception.</summary>
        internal static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

        /// <summary>Match the QueueTrigger platform default: 5 attempts, then move to poison.</summary>
        internal const int MaxDequeueCount = 5;

        private const string PoisonQueueSuffix = "-poison";

        private readonly QueueClient _mainQueue;
        private readonly QueueClient _poisonQueue;
        private readonly AnalyzeOnEnrollmentEndHandler _handler;
        private readonly ILogger<AnalyzeOnEnrollmentEndQueueWorker> _logger;

        public AnalyzeOnEnrollmentEndQueueWorker(
            IConfiguration configuration,
            AnalyzeOnEnrollmentEndHandler handler,
            ILogger<AnalyzeOnEnrollmentEndQueueWorker> logger)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var options = new QueueClientOptions
            {
                MessageEncoding = QueueMessageEncoding.Base64,
            };

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureTableStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var mainUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.AnalyzeOnEnrollmentEnd}");
                var poisonUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.AnalyzeOnEnrollmentEnd}{PoisonQueueSuffix}");
                var credential = new DefaultAzureCredential();
                _mainQueue = new QueueClient(mainUri, credential, options);
                _poisonQueue = new QueueClient(poisonUri, credential, options);
                _logger.LogInformation(
                    "AnalyzeOnEnrollmentEndQueueWorker initialized with Managed Identity (account: {Account})",
                    storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _mainQueue = new QueueClient(
                    connectionString, Constants.QueueNames.AnalyzeOnEnrollmentEnd, options);
                _poisonQueue = new QueueClient(
                    connectionString, Constants.QueueNames.AnalyzeOnEnrollmentEnd + PoisonQueueSuffix, options);
                _logger.LogInformation("AnalyzeOnEnrollmentEndQueueWorker initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await TryCreateQueueAsync(_mainQueue, "main", stoppingToken).ConfigureAwait(false);
            await TryCreateQueueAsync(_poisonQueue, "poison", stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("AnalyzeOnEnrollmentEndQueueWorker: poll loop started (queue {Name})",
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
                        "AnalyzeOnEnrollmentEndQueueWorker: poll-loop error — backing off {Backoff}",
                        ErrorBackoff);
                    try { await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _logger.LogInformation("AnalyzeOnEnrollmentEndQueueWorker: poll loop stopped");
        }

        private async Task ProcessOneAsync(QueueMessage msg, CancellationToken ct)
        {
            if (msg.DequeueCount > MaxDequeueCount)
            {
                await MoveToPoisonAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            AnalyzeOnEnrollmentEndEnvelope? envelope;
            try
            {
                envelope = JsonConvert.DeserializeObject<AnalyzeOnEnrollmentEndEnvelope>(msg.Body.ToString());
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "AnalyzeOnEnrollmentEndQueueWorker: malformed envelope JSON — dropping (msg {Id})",
                    msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (envelope is null)
            {
                _logger.LogWarning(
                    "AnalyzeOnEnrollmentEndQueueWorker: null envelope after deserialization — dropping (msg {Id})",
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
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AnalyzeOnEnrollmentEndQueueWorker: handler failed (msg {Id}, dequeue {N}) — visibility-timeout retry",
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
                    "AnalyzeOnEnrollmentEndQueueWorker: moved message {Id} to poison queue after {N} failed attempts",
                    msg.MessageId, msg.DequeueCount - 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AnalyzeOnEnrollmentEndQueueWorker: poison move failed for message {Id} (will retry)",
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
                    "AnalyzeOnEnrollmentEndQueueWorker: delete failed for message {Id} (will reappear after visibility-timeout)",
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
                    "AnalyzeOnEnrollmentEndQueueWorker: CreateIfNotExists failed for {Label} queue — will continue, send/receive will retry",
                    label);
            }
        }
    }
}
