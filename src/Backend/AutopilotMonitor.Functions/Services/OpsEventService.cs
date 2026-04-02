using System;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for recording operational events into the OpsEvents table.
    /// Provides typed helper methods for each event category so callers
    /// don't need to construct OpsEventEntry manually.
    /// All writes are fire-and-forget safe — failures are logged but never thrown.
    /// </summary>
    public class OpsEventService
    {
        private readonly IOpsEventRepository _repository;
        private readonly ILogger<OpsEventService> _logger;

        public OpsEventService(IOpsEventRepository repository, ILogger<OpsEventService> logger)
        {
            _repository = repository;
            _logger = logger;
        }

        // ── Consent ────────────────────────────────────────────────────────────

        public Task RecordConsentFlowStartedAsync(string tenantId, string userId, string redirectUri)
            => WriteAsync(OpsEventCategory.Consent, "ConsentFlowStarted", OpsEventSeverity.Info,
                $"Admin consent flow started by {userId}",
                tenantId, userId, new { redirectUri });

        public Task RecordConsentFlowFailedAsync(string tenantId, string userId, string error, string errorDescription)
            => WriteAsync(OpsEventCategory.Consent, "ConsentFlowFailed", OpsEventSeverity.Error,
                $"Admin consent failed: {error}",
                tenantId, userId, new { error, errorDescription });

        public Task RecordConsentRedirectUriMismatchAsync(string tenantId, string userId, string redirectUri, string redirectPath)
            => WriteAsync(OpsEventCategory.Consent, "ConsentRedirectUriMismatch", OpsEventSeverity.Critical,
                $"Redirect URI path '{redirectPath}' not in registered paths — consent will fail with AADSTS50011",
                tenantId, userId, new { redirectUri, redirectPath });

        // ── Maintenance ────────────────────────────────────────────────────────

        public Task RecordMaintenanceCompletedAsync(int durationMs, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "MaintenanceCompleted", OpsEventSeverity.Info,
                $"Maintenance completed in {durationMs}ms (triggered by {triggeredBy})",
                null, triggeredBy, new { durationMs });

        public Task RecordMaintenanceFailedAsync(string error, string triggeredBy)
            => WriteAsync(OpsEventCategory.Maintenance, "MaintenanceFailed", OpsEventSeverity.Error,
                $"Maintenance failed: {error}",
                null, triggeredBy, new { error });

        public Task RecordOpsEventCleanupAsync(int deletedCount, int retentionDays)
            => WriteAsync(OpsEventCategory.Maintenance, "OpsEventCleanup", OpsEventSeverity.Info,
                $"Cleaned up {deletedCount} ops events older than {retentionDays} days",
                null, "System.Maintenance", new { deletedCount, retentionDays });

        // ── Security ───────────────────────────────────────────────────────────

        public Task RecordDeviceBlockedAsync(string tenantId, string serialNumber, string reason, string blockedBy)
            => WriteAsync(OpsEventCategory.Security, "DeviceBlocked", OpsEventSeverity.Warning,
                $"Device {serialNumber} blocked: {reason}",
                tenantId, blockedBy, new { serialNumber, reason });

        public Task RecordExcessiveDataBlockedAsync(string tenantId, int devicesBlocked, int windowHours)
            => WriteAsync(OpsEventCategory.Security, "ExcessiveDataBlocked", OpsEventSeverity.Warning,
                $"{devicesBlocked} device(s) auto-blocked for excessive data (>{windowHours}h window)",
                tenantId, "System.Maintenance", new { devicesBlocked, windowHours });

        public Task RecordVersionBlockedAsync(string pattern, string blockedBy)
            => WriteAsync(OpsEventCategory.Security, "VersionBlocked", OpsEventSeverity.Warning,
                $"Agent version pattern '{pattern}' blocked",
                null, blockedBy, new { pattern });

        // ── Tenant ─────────────────────────────────────────────────────────────

        public Task RecordTenantOffboardedAsync(string tenantId, string performedBy, Dictionary<string, int> deletedCounts)
            => WriteAsync(OpsEventCategory.Tenant, "TenantOffboarded", OpsEventSeverity.Warning,
                $"Tenant {tenantId} offboarded — all data deleted",
                tenantId, performedBy, deletedCounts);

        // ── Agent ──────────────────────────────────────────────────────────────

        public Task RecordSessionTimeoutsAsync(string tenantId, int sessionCount, int timeoutHours)
            => WriteAsync(OpsEventCategory.Agent, "SessionTimeouts", OpsEventSeverity.Info,
                $"{sessionCount} session(s) timed out after {timeoutHours}h",
                tenantId, "System.Maintenance", new { sessionCount, timeoutHours });

        // ── Core write method ──────────────────────────────────────────────────

        private async Task WriteAsync(string category, string eventType, string severity,
            string message, string? tenantId, string? userId, object? details)
        {
            try
            {
                var entry = new OpsEventEntry
                {
                    Category  = category,
                    EventType = eventType,
                    Severity  = severity,
                    TenantId  = tenantId,
                    UserId    = userId,
                    Message   = message,
                    Details   = details != null ? JsonSerializer.Serialize(details) : null,
                    Timestamp = DateTime.UtcNow,
                };

                await _repository.SaveOpsEventAsync(entry);
            }
            catch (Exception ex)
            {
                // Never throw from ops event recording — it must not break the calling flow
                _logger.LogWarning(ex, "Failed to record ops event {Category}/{EventType}", category, eventType);
            }
        }
    }
}
