using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Background poll-loop for the <c>session-deletion</c> queue (plan §5 PR4). Modeled on
    /// <see cref="AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndQueueWorker"/>
    /// with four explicit deviations:
    /// <list type="bullet">
    ///   <item><see cref="BatchSize"/> = <b>1</b> (not 32) — a single cascade can be tens of MB
    ///       and minutes of wall-time; one-at-a-time bounds memory and poison blast radius.</item>
    ///   <item><b>Kill-switch on entry</b>: every receive checks
    ///       <c>AdminConfiguration.SessionDeletionKillSwitch</c>; when active the worker idles
    ///       without dequeuing (envelope stays on the queue, no visibility timeout pressure).</item>
    ///   <item><b>Heartbeat</b>: while <see cref="SessionDeletionHandler"/> runs, a sidecar task
    ///       calls <c>UpdateMessageAsync</c> every <see cref="_heartbeatInterval"/> to extend the
    ///       message visibility by <see cref="HeartbeatExtendBy"/>. Prevents queue re-delivery
    ///       from spawning a parallel worker on the same manifest while a step is in flight.</item>
    ///   <item><b>Poison audit</b>: max-dequeue exhaustion writes a <c>deletion_poisoned</c>
    ///       audit entry alongside the poison-queue move so the operator can correlate the
    ///       poisoned cascade with its manifest. The Sessions row's <c>DeletionState</c> is NOT
    ///       auto-cleared — operator action via restore-from-poisoned (§13, PR4b) is required.</item>
    /// </list>
    /// <para>
    /// <b>The worker is NOT registered in <c>Program.cs</c> in PR4</b> — that wires up in PR5
    /// alongside the flag-gated <c>DeleteSessionFunction</c> changes. Without the producer
    /// emitting envelopes (gated by <c>TenantConfiguration.EnableCascadeDeleteV2</c>, default
    /// false), having the worker idle on an empty queue is harmless; we hold off so the worker's
    /// poll-loop doesn't appear in production telemetry until the cutover.
    /// </para>
    /// </summary>
    public sealed class SessionDeletionWorker : BackgroundService
    {
        /// <summary>
        /// Explicit deviation from sibling workers — one cascade per receive bounds the worker's
        /// memory footprint (a 35MB snapshot blob × 32 messages = 1.1GB) and limits poison-queue
        /// blast radius on a corruption signal. Plan §5 PR4.
        /// </summary>
        internal const int BatchSize = 1;

        /// <summary>Initial message-visibility window — extended in-flight by the heartbeat task.</summary>
        internal static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);

        /// <summary>How often the heartbeat task extends the in-flight message's visibility.</summary>
        internal static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(60);

        /// <summary>How much the heartbeat task adds to visibility on each tick.</summary>
        internal static readonly TimeSpan HeartbeatExtendBy = TimeSpan.FromMinutes(5);

        /// <summary>Idle sleep between empty receives or kill-switch-on cycles.</summary>
        internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);

        /// <summary>Instance-level overrides — production uses the defaults above, tests inject shorter values via the internal ctor.</summary>
        private readonly TimeSpan _heartbeatInterval;
        private readonly TimeSpan _pollInterval;

        /// <summary>Cool-down after an unhandled poll-loop exception.</summary>
        internal static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

        /// <summary>Match the QueueTrigger platform default + sibling workers: 5 attempts → poison.</summary>
        internal const int MaxDequeueCount = 5;

        private const string PoisonQueueSuffix = "-poison";

        private readonly QueueClient _mainQueue;
        private readonly QueueClient _poisonQueue;
        private readonly SessionDeletionHandler _handler;
        private readonly TableStorageService _storage;
        private readonly AdminConfigurationService _adminConfig;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ILogger<SessionDeletionWorker> _logger;

        public SessionDeletionWorker(
            IConfiguration configuration,
            SessionDeletionHandler handler,
            TableStorageService storage,
            AdminConfigurationService adminConfig,
            IMaintenanceRepository maintenanceRepo,
            ILogger<SessionDeletionWorker> logger)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _adminConfig = adminConfig ?? throw new ArgumentNullException(nameof(adminConfig));
            _maintenanceRepo = maintenanceRepo ?? throw new ArgumentNullException(nameof(maintenanceRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _heartbeatInterval = DefaultHeartbeatInterval;
            _pollInterval = DefaultPollInterval;

            var options = new QueueClientOptions
            {
                MessageEncoding = QueueMessageEncoding.Base64,
            };

            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureTableStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var mainUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.SessionDeletion}");
                var poisonUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.SessionDeletion}{PoisonQueueSuffix}");
                var credential = new DefaultAzureCredential();
                _mainQueue = new QueueClient(mainUri, credential, options);
                _poisonQueue = new QueueClient(poisonUri, credential, options);
                _logger.LogInformation(
                    "SessionDeletionWorker initialized with Managed Identity (account: {Account})",
                    storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _mainQueue = new QueueClient(
                    connectionString, Constants.QueueNames.SessionDeletion, options);
                _poisonQueue = new QueueClient(
                    connectionString, Constants.QueueNames.SessionDeletion + PoisonQueueSuffix, options);
                _logger.LogInformation("SessionDeletionWorker initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        /// <summary>
        /// Test seam: construct directly with mock <see cref="QueueClient"/> instances so the
        /// poll loop can be exercised against an in-memory queue without spinning up Azurite.
        /// Mirrors <see cref="SessionDeletionProducer"/>'s internal test ctor.
        /// </summary>
        internal SessionDeletionWorker(
            QueueClient mainQueue,
            QueueClient poisonQueue,
            SessionDeletionHandler handler,
            TableStorageService storage,
            AdminConfigurationService adminConfig,
            IMaintenanceRepository maintenanceRepo,
            ILogger<SessionDeletionWorker> logger,
            TimeSpan? heartbeatInterval = null,
            TimeSpan? pollInterval = null)
        {
            _mainQueue = mainQueue ?? throw new ArgumentNullException(nameof(mainQueue));
            _poisonQueue = poisonQueue ?? throw new ArgumentNullException(nameof(poisonQueue));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _adminConfig = adminConfig ?? throw new ArgumentNullException(nameof(adminConfig));
            _maintenanceRepo = maintenanceRepo ?? throw new ArgumentNullException(nameof(maintenanceRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
            _pollInterval = pollInterval ?? DefaultPollInterval;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await TryCreateQueueAsync(_mainQueue, "main", stoppingToken).ConfigureAwait(false);
            await TryCreateQueueAsync(_poisonQueue, "poison", stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("SessionDeletionWorker: poll loop started (queue {Name})", _mainQueue.Name);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Kill-switch entry guard (plan §1 P8 / §9). When active, the worker does not
                    // dequeue — the message stays visible on the main queue until the operator
                    // flips the switch back. Envelope is preserved without losing dequeue budget.
                    var admin = await _adminConfig.GetConfigurationAsync().ConfigureAwait(false);
                    if (admin.SessionDeletionKillSwitch)
                    {
                        _logger.LogDebug("SessionDeletionWorker: kill-switch active; idling for {Interval}", _pollInterval);
                        await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    var batch = await _mainQueue
                        .ReceiveMessagesAsync(BatchSize, VisibilityTimeout, stoppingToken)
                        .ConfigureAwait(false);

                    if (batch?.Value is null || batch.Value.Length == 0)
                    {
                        await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
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
                        "SessionDeletionWorker: poll-loop error — backing off {Backoff}",
                        ErrorBackoff);
                    try { await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _logger.LogInformation("SessionDeletionWorker: poll loop stopped");
        }

        private async Task ProcessOneAsync(QueueMessage msg, CancellationToken ct)
        {
            if (msg.DequeueCount > MaxDequeueCount)
            {
                await MoveToPoisonAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            SessionDeletionEnvelope? envelope;
            try
            {
                envelope = JsonConvert.DeserializeObject<SessionDeletionEnvelope>(msg.Body.ToString());
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "SessionDeletionWorker: malformed envelope JSON — dropping (msg {Id})",
                    msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (envelope is null
                || string.IsNullOrEmpty(envelope.TenantId)
                || string.IsNullOrEmpty(envelope.SessionId)
                || string.IsNullOrEmpty(envelope.ManifestId))
            {
                _logger.LogWarning(
                    "SessionDeletionWorker: envelope missing required fields — dropping (msg {Id})",
                    msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            // Heartbeat: extend visibility every _heartbeatInterval while the handler runs. We
            // capture the latest PopReceipt locally because each UpdateMessageAsync returns a
            // fresh receipt; the SafeDelete at the end uses whichever receipt was issued last.
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeatState = new HeartbeatState(msg.MessageId, msg.PopReceipt);
            var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(heartbeatState, heartbeatCts.Token), heartbeatCts.Token);

            try
            {
                await _handler.HandleAsync(envelope, ct).ConfigureAwait(false);

                // Handler succeeded — stop heartbeat and delete using the latest pop-receipt.
                heartbeatCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

                await SafeDeleteAsync(msg.MessageId, heartbeatState.PopReceipt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                heartbeatCts.Cancel();
                throw;
            }
            catch (Exception ex)
            {
                heartbeatCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

                _logger.LogWarning(ex,
                    "SessionDeletionWorker: handler failed (tenant={Tenant} session={Session} manifestId={ManifestId} dequeue={N}) — visibility-timeout retry",
                    envelope.TenantId, envelope.SessionId, envelope.ManifestId, msg.DequeueCount);
            }
        }

        private async Task HeartbeatLoopAsync(HeartbeatState state, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(_heartbeatInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    var updated = await _mainQueue.UpdateMessageAsync(
                        state.MessageId, state.PopReceipt,
                        visibilityTimeout: HeartbeatExtendBy,
                        cancellationToken: ct).ConfigureAwait(false);
                    state.PopReceipt = updated.Value.PopReceipt;
                    _logger.LogDebug(
                        "SessionDeletionWorker: heartbeat extended visibility for message {Id} by {Extend}",
                        state.MessageId, HeartbeatExtendBy);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // Heartbeat failure (e.g. PopReceipt expired because the visibility window
                    // was missed) is logged and the loop exits. The handler keeps running; on
                    // its eventual return, SafeDelete will hit a 404 and the message will
                    // reappear after its current visibility expires — but the handler's own
                    // ETag-CAS on the progress blob makes a parallel pickup idempotent.
                    _logger.LogWarning(ex,
                        "SessionDeletionWorker: heartbeat UpdateMessageAsync failed for message {Id} — heartbeat exiting",
                        state.MessageId);
                    return;
                }
            }
        }

        private async Task MoveToPoisonAsync(QueueMessage msg, CancellationToken ct)
        {
            // Best-effort: attempt to parse envelope so we can audit the manifest id alongside
            // the poison move. If the body is malformed we still move it to the poison queue
            // (the audit just lacks the manifestId field).
            SessionDeletionEnvelope? envelope = null;
            try { envelope = JsonConvert.DeserializeObject<SessionDeletionEnvelope>(msg.Body.ToString()); }
            catch (JsonException) { /* swallow — non-parseable envelopes still get poisoned */ }

            // PR4b E1: transition Sessions.DeletionState → Poisoned BEFORE the poison-queue send.
            // Without this, the row stays at Running (or Queued if poisoned before pickup) and the
            // restore endpoint cannot dispatch into partial-restore mode. Best-effort: if CAS fails
            // (concurrent writer, row gone, state mismatch) we still continue with the poison-queue
            // move so the original poison flow completes and an audit trail exists.
            if (envelope != null && !string.IsNullOrEmpty(envelope.TenantId) && !string.IsNullOrEmpty(envelope.SessionId))
            {
                await TryTransitionToPoisonedStateAsync(envelope.TenantId, envelope.SessionId, envelope.ManifestId, ct).ConfigureAwait(false);
            }

            try
            {
                await _poisonQueue.SendMessageAsync(msg.Body.ToString(), ct).ConfigureAwait(false);
                await SafeDeleteAsync(msg.MessageId, msg.PopReceipt, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "SessionDeletionWorker: moved message {Id} to poison queue after {N} failed attempts",
                    msg.MessageId, msg.DequeueCount - 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SessionDeletionWorker: poison move failed for message {Id} (will retry)",
                    msg.MessageId);
                return;
            }

            if (envelope != null && !string.IsNullOrEmpty(envelope.TenantId) && !string.IsNullOrEmpty(envelope.SessionId))
            {
                try
                {
                    await _maintenanceRepo.LogAuditEntryAsync(
                        envelope.TenantId,
                        action: "deletion_poisoned",
                        entityType: "Session",
                        entityId: envelope.SessionId,
                        performedBy: "system",
                        details: new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["manifestId"] = envelope.ManifestId ?? string.Empty,
                            ["reason"] = envelope.Reason ?? string.Empty,
                            ["messageId"] = msg.MessageId,
                            ["dequeueCount"] = (msg.DequeueCount - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        }).ConfigureAwait(false);
                }
                catch (Exception auditEx)
                {
                    _logger.LogError(auditEx,
                        "SessionDeletionWorker: deletion_poisoned audit log write failed for tenant={Tenant} session={Session}",
                        envelope.TenantId, envelope.SessionId);
                }
            }
        }

        private async Task SafeDeleteAsync(QueueMessage msg, CancellationToken ct)
            => await SafeDeleteAsync(msg.MessageId, msg.PopReceipt, ct).ConfigureAwait(false);

        private async Task SafeDeleteAsync(string messageId, string popReceipt, CancellationToken ct)
        {
            try
            {
                await _mainQueue.DeleteMessageAsync(messageId, popReceipt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SessionDeletionWorker: delete failed for message {Id} (will reappear after visibility-timeout)",
                    messageId);
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
                    "SessionDeletionWorker: CreateIfNotExists failed for {Label} queue — will continue, send/receive will retry",
                    label);
            }
        }

        /// <summary>
        /// PR4b E1 + PR4c F5: CAS Sessions.DeletionState to Poisoned with manifestId pre-check.
        /// Tries <c>Running → Poisoned</c> first (the common case — Worker successfully CAS'd
        /// Queued→Running on first pickup and the handler then failed N times). Falls back to
        /// <c>Queued → Poisoned</c> for the rare case where the cascade poisoned before the
        /// Worker ever successfully transitioned to Running (handler always threw at the
        /// state-acquire step). Logs warning on persistent CAS failure but does NOT throw — the
        /// poison-queue move + audit must still complete so the operator has an observable trail
        /// even if the row state is somehow inconsistent.
        /// <para>
        /// <b>PR4c F5</b>: before issuing any CAS, read the Sessions row's
        /// <c>PendingDeletionManifestId</c> and compare to the envelope's <c>manifestId</c>. If
        /// they don't match, the envelope is stale (from a prior cascade attempt) — do NOT
        /// touch the state of a possibly-fresh active cascade. The poison-queue + audit still
        /// fire in the caller so the stale message is removed from the main queue.
        /// </para>
        /// </summary>
        private async Task TryTransitionToPoisonedStateAsync(string tenantId, string sessionId, string? manifestId, CancellationToken ct)
        {
            // PR4c F5: pre-check that the envelope's manifestId matches the row's
            // PendingDeletionManifestId. A stale envelope from a prior cascade must not flip a
            // fresh active cascade to Poisoned.
            var row = await _storage.GetSessionRowAsync(tenantId, sessionId, ct).ConfigureAwait(false);
            if (row == null)
            {
                _logger.LogInformation(
                    "SessionDeletionWorker: Sessions row gone for tenant={Tenant} session={Session} (cascade already tombstoned by another worker?) — skipping state transition",
                    tenantId, sessionId);
                return;
            }

            var currentPending = row.GetString("PendingDeletionManifestId");
            if (!string.Equals(currentPending, manifestId, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "SessionDeletionWorker: stale poison envelope for tenant={Tenant} session={Session} — envelope.ManifestId={Envelope} but row.PendingDeletionManifestId={Pending}; skipping state transition (active cascade is a different manifest)",
                    tenantId, sessionId, manifestId, currentPending);
                return;
            }

            // First attempt: Running → Poisoned (the expected case after a successful pickup).
            var cas = await _storage.CasSetSessionDeletionStateAsync(
                tenantId, sessionId,
                fromState: SessionDeletionState.Running,
                toState: SessionDeletionState.Poisoned,
                newManifestId: null,
                ct).ConfigureAwait(false);
            if (cas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.Updated)
            {
                _logger.LogInformation(
                    "SessionDeletionWorker: CAS Running→Poisoned succeeded for tenant={Tenant} session={Session} manifestId={ManifestId}",
                    tenantId, sessionId, manifestId);
                return;
            }

            // Fallback: Queued → Poisoned (poisoned before pickup ever succeeded).
            if (cas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.WrongState
                && cas.CurrentState == SessionDeletionState.Queued)
            {
                cas = await _storage.CasSetSessionDeletionStateAsync(
                    tenantId, sessionId,
                    fromState: SessionDeletionState.Queued,
                    toState: SessionDeletionState.Poisoned,
                    newManifestId: null,
                    ct).ConfigureAwait(false);
                if (cas.Outcome == TableStorageService.SessionDeletionStateCasOutcome.Updated)
                {
                    _logger.LogInformation(
                        "SessionDeletionWorker: CAS Queued→Poisoned succeeded for tenant={Tenant} session={Session} manifestId={ManifestId}",
                        tenantId, sessionId, manifestId);
                    return;
                }
            }

            // Persistent CAS failure — log and continue so the poison-queue move + audit still fire.
            _logger.LogWarning(
                "SessionDeletionWorker: could not transition Sessions.DeletionState to Poisoned for tenant={Tenant} session={Session} manifestId={ManifestId} outcome={Outcome} currentState={State}; restore endpoint may reject with 409 \"active_cascade\" until operator manually intervenes.",
                tenantId, sessionId, manifestId, cas.Outcome, cas.CurrentState);
        }

        private sealed class HeartbeatState
        {
            public string MessageId { get; }
            public string PopReceipt { get; set; }
            public HeartbeatState(string messageId, string popReceipt) { MessageId = messageId; PopReceipt = popReceipt; }
        }
    }
}
