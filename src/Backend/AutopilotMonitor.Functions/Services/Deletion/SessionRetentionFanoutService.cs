using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Per-tenant retention fanout for cascade-delete (plan §5 PR6 / §16 R14). Replaces the
    /// session-retention loop previously embedded in
    /// <c>MaintenanceService.CleanupOldDataAsync</c>: each tenant's
    /// <c>DataRetentionDays</c> is read; sessions older than the cutoff are dispatched per the
    /// per-tenant <see cref="TenantConfiguration.EnableCascadeDeleteV2"/> flag.
    /// <list type="bullet">
    ///   <item><b>Flag ON</b>: <see cref="ISessionDeletionEnqueuer.EnqueueAsync"/> with
    ///       <c>reason="retention_cutoff"</c>. The producer's own CAS-then-build-then-enqueue
    ///       handles the <c>DeletionState != None</c> case (returns <c>AlreadyInFlight</c>).</item>
    ///   <item><b>Flag OFF</b>: legacy direct-delete via the existing
    ///       <see cref="IMaintenanceRepository"/> + <see cref="ISessionRepository.DeleteSessionAsync"/>
    ///       helpers. PR5 finding 2 built the CAS guard into <c>DeleteSessionAsync</c> itself, so
    ///       a half-completed V2 cascade is never overrun even on the legacy path.</item>
    /// </list>
    /// <para>
    /// <b>Rate limit (plan §5 PR6):</b> at most <see cref="MaxEnqueuesPerTenantPerRun"/> dispatches
    /// per tenant per invocation, with <see cref="EnqueueThrottleDelay"/> between successive
    /// dispatches. Bounds the cost of a maintenance fan-out when a tenant has months of backlog
    /// behind a freshly-shortened retention setting.
    /// </para>
    /// </summary>
    public class SessionRetentionFanoutService
    {
        public const int MaxEnqueuesPerTenantPerRun = 100;
        public static readonly TimeSpan EnqueueThrottleDelay = TimeSpan.FromMilliseconds(50);

        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly TenantConfigurationService _tenantConfig;
        private readonly ISessionDeletionEnqueuer _enqueuer;
        private readonly AdminConfigurationService _adminConfig;
        private readonly ILogger<SessionRetentionFanoutService> _logger;
        private readonly Func<TimeSpan, CancellationToken, Task> _throttle;

        public SessionRetentionFanoutService(
            IMaintenanceRepository maintenanceRepo,
            ISessionRepository sessionRepo,
            TenantConfigurationService tenantConfig,
            ISessionDeletionEnqueuer enqueuer,
            AdminConfigurationService adminConfig,
            ILogger<SessionRetentionFanoutService> logger)
            : this(maintenanceRepo, sessionRepo, tenantConfig, enqueuer, adminConfig, logger, throttle: Task.Delay)
        {
        }

        /// <summary>
        /// Test seam — tests inject a no-op throttle to make the rate-limit loop exercise its
        /// counter without waiting 50ms × N in real time.
        /// </summary>
        internal SessionRetentionFanoutService(
            IMaintenanceRepository maintenanceRepo,
            ISessionRepository sessionRepo,
            TenantConfigurationService tenantConfig,
            ISessionDeletionEnqueuer enqueuer,
            AdminConfigurationService adminConfig,
            ILogger<SessionRetentionFanoutService> logger,
            Func<TimeSpan, CancellationToken, Task> throttle)
        {
            _maintenanceRepo = maintenanceRepo;
            _sessionRepo = sessionRepo;
            _tenantConfig = tenantConfig;
            _enqueuer = enqueuer;
            _adminConfig = adminConfig;
            _logger = logger;
            _throttle = throttle;
        }

        /// <summary>
        /// Aggregate result of a single fanout invocation; used for the per-tenant audit + the
        /// completion summary OpsEvent.
        /// </summary>
        public sealed class FanoutResult
        {
            public int TenantsProcessed { get; set; }
            public int SessionsEnqueued { get; set; }     // V2 path: enqueued for cascade
            public int SessionsLegacyDeleted { get; set; } // flag-OFF path: direct-delete succeeded
            public int SessionsSkipped { get; set; }       // already locked / poisoned / kill-switch / etc.
            public int RateLimitedTenants { get; set; }    // tenants that hit MaxEnqueuesPerTenantPerRun
            public bool AbortedByKillSwitch { get; set; }  // PR6 follow-up F2: kill-switch flipped mid-run
        }

        /// <summary>
        /// Runs the fanout for every tenant returned by <see cref="IMaintenanceRepository.GetAllTenantIdsAsync"/>.
        /// Each tenant is processed independently; an exception on tenant A is logged and the loop
        /// continues with tenant B (matches the existing maintenance behaviour: per-tenant
        /// failures must not cascade).
        /// </summary>
        public virtual async Task<FanoutResult> RunAsync(CancellationToken cancellationToken)
        {
            var result = new FanoutResult();
            var tenantIds = await _maintenanceRepo.GetAllTenantIdsAsync().ConfigureAwait(false);

            foreach (var tenantId in tenantIds)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // PR6 follow-up F2: per-tenant kill-switch check so a flip-ON mid-run halts the
                // remaining tenants. The maintenance function gates entry; this gates iteration.
                if (await _adminConfig.IsSessionDeletionKillSwitchActiveAsync().ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: kill-switch flipped on mid-run — halting before tenant {TenantId}",
                        tenantId);
                    result.AbortedByKillSwitch = true;
                    break;
                }

                try
                {
                    await RunForTenantAsync(tenantId, result, cancellationToken).ConfigureAwait(false);
                    result.TenantsProcessed++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SessionRetentionFanout failed for tenant {TenantId} — continuing with next tenant", tenantId);
                }
            }

            return result;
        }

        private async Task RunForTenantAsync(string tenantId, FanoutResult result, CancellationToken cancellationToken)
        {
            var config = await _tenantConfig.GetConfigurationAsync(tenantId).ConfigureAwait(false);
            var retentionDays = config?.DataRetentionDays ?? 90;

            if (retentionDays <= 0)
            {
                _logger.LogInformation("Tenant {TenantId}: DataRetentionDays=0 → skipping retention fanout", tenantId);
                return;
            }

            var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
            var oldSessions = await _maintenanceRepo.GetSessionsOlderThanAsync(tenantId, cutoffUtc).ConfigureAwait(false);

            if (oldSessions.Count == 0)
            {
                _logger.LogInformation("Tenant {TenantId}: no sessions older than {Days} days", tenantId, retentionDays);
                return;
            }

            var useV2Cascade = config?.EnableCascadeDeleteV2 == true;

            int processed = 0;
            int enqueued = 0;
            int legacyDeleted = 0;
            int skipped = 0;

            foreach (var session in oldSessions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (processed >= MaxEnqueuesPerTenantPerRun)
                {
                    _logger.LogInformation(
                        "Tenant {TenantId}: hit rate limit ({Limit}/run) — deferring remaining {Remaining} sessions to next run",
                        tenantId, MaxEnqueuesPerTenantPerRun, oldSessions.Count - processed);
                    result.RateLimitedTenants++;
                    break;
                }

                // PR6 follow-up F2: per-session kill-switch check so an emergency flip-ON halts
                // immediately instead of after the rest of this tenant's backlog. Uncached read
                // (PR5 F1) — uniform behaviour across scaled-out instances within seconds.
                if (await _adminConfig.IsSessionDeletionKillSwitchActiveAsync().ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "SessionRetentionFanout: kill-switch flipped on mid-tenant — halting at session {SessionId} of {TenantId}",
                        session.SessionId, tenantId);
                    result.AbortedByKillSwitch = true;
                    break;
                }

                if (useV2Cascade)
                {
                    var outcome = await EnqueueV2Async(session, cancellationToken).ConfigureAwait(false);
                    if (outcome == SessionDeletionEnqueueOutcome.Enqueued) enqueued++;
                    else skipped++;
                }
                else
                {
                    if (await LegacyDeleteAsync(session, cancellationToken).ConfigureAwait(false)) legacyDeleted++;
                    else skipped++;
                }

                processed++;

                // PR6 R14: rate-limit pacing — bounds the cost of a fanout when a tenant has months
                // of backlog (e.g. retention freshly shortened). Throttle is injected so unit tests
                // can exercise the loop without waiting real time.
                if (processed < oldSessions.Count && processed < MaxEnqueuesPerTenantPerRun)
                    await _throttle(EnqueueThrottleDelay, cancellationToken).ConfigureAwait(false);
            }

            result.SessionsEnqueued += enqueued;
            result.SessionsLegacyDeleted += legacyDeleted;
            result.SessionsSkipped += skipped;

            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId,
                "SessionDeletionMaintenanceFanout",
                "Session",
                $"{processed} sessions",
                "System.Maintenance",
                new Dictionary<string, string>
                {
                    { "Path", useV2Cascade ? "V2Cascade" : "LegacyDirectDelete" },
                    { "RetentionDays", retentionDays.ToString() },
                    { "CutoffUtc", cutoffUtc.ToString("o") },
                    { "Eligible", oldSessions.Count.ToString() },
                    { "Enqueued", enqueued.ToString() },
                    { "LegacyDeleted", legacyDeleted.ToString() },
                    { "Skipped", skipped.ToString() },
                    { "AbortedByKillSwitch", result.AbortedByKillSwitch.ToString() },
                }).ConfigureAwait(false);

            _logger.LogInformation(
                "Tenant {TenantId}: retention fanout — path={Path} cutoff={Cutoff:o} eligible={Eligible} enqueued={Enqueued} legacy={Legacy} skipped={Skipped}",
                tenantId, useV2Cascade ? "V2Cascade" : "LegacyDirectDelete", cutoffUtc, oldSessions.Count, enqueued, legacyDeleted, skipped);
        }

        /// <summary>
        /// V2 enqueue path. The producer handles CAS-Preparing, manifest build, blob upload, and
        /// queue send; we just translate the outcome into a counter bump (or skip) and log the
        /// reason. Maintenance-side audits (<c>deletion_started</c>) are written by the producer.
        /// </summary>
        private async Task<SessionDeletionEnqueueOutcome> EnqueueV2Async(SessionSummary session, CancellationToken cancellationToken)
        {
            var actor = new DeletionActor { Type = "maintenance", Actor = "System.Maintenance" };
            var result = await _enqueuer.EnqueueAsync(
                session.TenantId,
                session.SessionId,
                "retention_cutoff",
                actor,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            switch (result.Outcome)
            {
                case SessionDeletionEnqueueOutcome.Enqueued:
                    return SessionDeletionEnqueueOutcome.Enqueued;
                case SessionDeletionEnqueueOutcome.AlreadyInFlight:
                    _logger.LogInformation(
                        "Retention fanout: session {SessionId} already has a cascade in flight (state={State}, manifestId={ManifestId}) — skipping",
                        session.SessionId, result.ExistingState, result.ManifestId);
                    return result.Outcome;
                case SessionDeletionEnqueueOutcome.Poisoned:
                    _logger.LogWarning(
                        "Retention fanout: session {SessionId} is in DeletionState=Poisoned (manifestId={ManifestId}) — operator must run POST /restore first",
                        session.SessionId, result.ManifestId);
                    return result.Outcome;
                case SessionDeletionEnqueueOutcome.KillSwitchActive:
                    // Should not normally reach here because the caller (maintenance function) is
                    // expected to gate the whole fanout on the kill-switch. Belt-and-suspenders.
                    _logger.LogWarning("Retention fanout: kill-switch flipped on mid-fanout — aborting tenant {TenantId}", session.TenantId);
                    return result.Outcome;
                case SessionDeletionEnqueueOutcome.SessionNotFound:
                    _logger.LogInformation("Retention fanout: session {SessionId} no longer exists — skipping", session.SessionId);
                    return result.Outcome;
                case SessionDeletionEnqueueOutcome.CasExhausted:
                    _logger.LogWarning(
                        "Retention fanout: ETag-CAS exhausted for session {SessionId} — will retry next run",
                        session.SessionId);
                    return result.Outcome;
                default:
                    _logger.LogError(
                        "Retention fanout: unexpected enqueue outcome {Outcome} for session {SessionId}",
                        result.Outcome, session.SessionId);
                    return result.Outcome;
            }
        }

        /// <summary>
        /// Legacy direct-delete path — invoked when the tenant has not yet been migrated to the V2
        /// cascade (per-tenant flag OFF). Sequence:
        /// <list type="number">
        ///   <item>Pre-read the Sessions row (PR6 follow-up F1). If <c>DeletionState != None</c>
        ///       the V2 cascade owns the session — return false without touching ANY side table.
        ///       Without this gate, we would delete Events / RuleResults / AppInstallSummaries
        ///       and only discover the lock at the tombstone CAS, leaving the V2 cascade with a
        ///       half-eaten session.</item>
        ///   <item>Delete events / rule-results / app-summaries.</item>
        ///   <item>Delete the Sessions row via the CAS-guarded
        ///       <see cref="ISessionRepository.DeleteSessionAsync"/> (PR5 finding 2) — second-line
        ///       protection against the T1-T3 race where another instance CAS-claimed the row
        ///       after our pre-read but before our side-table mutations.</item>
        /// </list>
        /// </summary>
        private async Task<bool> LegacyDeleteAsync(SessionSummary session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tenantId = session.TenantId;
            var sessionId = session.SessionId;

            // F1: gate side-table mutations on DeletionState. The CAS guard inside DeleteSessionAsync
            // is the storage-atomic backstop (PR5 finding 2), but it fires AFTER the side-table
            // deletes. By the time it returns false we've already orphaned events from the V2
            // cascade's planned manifest. Reading the Sessions row up-front is cheap (one Get) and
            // skips ~3 side-table deletes when the lock is held.
            var refreshed = await _sessionRepo.GetSessionAsync(tenantId, sessionId).ConfigureAwait(false);
            if (refreshed == null)
            {
                // Row already gone — nothing to clean up. Treat as skip (no events were ours to delete either).
                return false;
            }
            if (SessionDeletionState.IsLocked(refreshed.DeletionState))
            {
                _logger.LogInformation(
                    "Retention fanout (legacy): session {SessionId} is locked by a V2 cascade (state={State}, manifestId={ManifestId}) — skipping ALL deletes, V2 worker owns the cascade",
                    sessionId, refreshed.DeletionState, refreshed.PendingDeletionManifestId);
                return false;
            }

            _ = await _maintenanceRepo.DeleteSessionEventsAsync(tenantId, sessionId).ConfigureAwait(false);
            _ = await _maintenanceRepo.DeleteSessionRuleResultsAsync(tenantId, sessionId).ConfigureAwait(false);
            _ = await _maintenanceRepo.DeleteSessionAppInstallSummariesAsync(tenantId, sessionId).ConfigureAwait(false);

            // CAS guard inside DeleteSessionAsync (PR5 finding 2) refuses if DeletionState is locked
            // — backstop for the T1-T3 race after our pre-read above. A `false` here means either
            // 404 (already gone) is treated as success internally, OR a concurrent V2 cascade
            // claimed the row in the brief window between our pre-read and the tombstone.
            var deleted = await _sessionRepo.DeleteSessionAsync(tenantId, sessionId).ConfigureAwait(false);
            if (!deleted)
            {
                _logger.LogWarning(
                    "Retention fanout (legacy): session {SessionId} could not be tombstoned — likely claimed by a concurrent V2 cascade. Side tables already deleted; cascade is idempotent so V2 will absorb the difference. Will retry next run",
                    sessionId);
            }
            return deleted;
        }
    }
}
