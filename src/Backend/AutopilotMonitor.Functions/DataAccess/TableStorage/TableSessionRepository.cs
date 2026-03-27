using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of ISessionRepository.
    /// Delegates to existing TableStorageService for backwards compatibility.
    /// </summary>
    public class TableSessionRepository : ISessionRepository
    {
        private readonly TableStorageService _storage;
        private readonly IDataEventPublisher _publisher;

        public TableSessionRepository(TableStorageService storage, IDataEventPublisher publisher)
        {
            _storage = storage;
            _publisher = publisher;
        }

        public async Task<bool> StoreSessionAsync(SessionRegistration registration)
        {
            var result = await _storage.StoreSessionAsync(registration);
            if (result)
                await _publisher.PublishAsync("session.created", registration, registration.TenantId);
            return result;
        }

        public Task<SessionSummary?> GetSessionAsync(string tenantId, string sessionId)
            => _storage.GetSessionAsync(tenantId, sessionId);

        public Task<SessionPage> GetSessionsAsync(string tenantId, int maxResults = 100, string? cursor = null)
            => _storage.GetSessionsAsync(tenantId, maxResults, cursor);

        public Task<SessionPage> GetAllSessionsAsync(int maxResults = 100, string? cursor = null)
            => _storage.GetAllSessionsAsync(maxResults, cursor);

        public Task<bool> DeleteSessionAsync(string tenantId, string sessionId)
            => _storage.DeleteSessionAsync(tenantId, sessionId);

        public Task<bool> UpdateSessionStatusAsync(
            string tenantId, string sessionId, SessionStatus status,
            EnrollmentPhase? currentPhase = null, string? failureReason = null,
            DateTime? completedAt = null, DateTime? earliestEventTimestamp = null,
            DateTime? latestEventTimestamp = null, bool? isPreProvisioned = null,
            bool? isUserDriven = null, DateTime? resumedAt = null)
            => _storage.UpdateSessionStatusAsync(tenantId, sessionId, status,
                currentPhase, failureReason, completedAt, earliestEventTimestamp,
                latestEventTimestamp, isPreProvisioned, isUserDriven, resumedAt);

        public Task IncrementSessionEventCountAsync(
            string tenantId, string sessionId, int increment,
            DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null,
            EnrollmentPhase? currentPhase = null)
            => _storage.IncrementSessionEventCountAsync(tenantId, sessionId, increment,
                earliestEventTimestamp, latestEventTimestamp, currentPhase);

        public Task UpdateSessionDiagnosticsBlobAsync(string tenantId, string sessionId, string blobName)
            => _storage.UpdateSessionDiagnosticsBlobAsync(tenantId, sessionId, blobName);

        public Task SetSessionPreProvisionedAsync(string tenantId, string sessionId, bool isPreProvisioned,
            SessionStatus? status = null, bool? isUserDriven = null)
            => _storage.SetSessionPreProvisionedAsync(tenantId, sessionId, isPreProvisioned, status, isUserDriven);

        public Task UpdateSessionGeoAsync(string tenantId, string sessionId,
            string? country, string? region, string? city, string? loc)
            => _storage.UpdateSessionGeoAsync(tenantId, sessionId, country, region, city, loc);

        public Task UpdateSessionImeAgentVersionAsync(string tenantId, string sessionId, string version)
            => _storage.UpdateSessionImeAgentVersionAsync(tenantId, sessionId, version);

        public async Task<bool> StoreEventAsync(EnrollmentEvent evt)
        {
            var result = await _storage.StoreEventAsync(evt);
            if (result)
                await _publisher.PublishAsync("event.ingested", evt, evt.TenantId);
            return result;
        }

        public async Task<List<EnrollmentEvent>> StoreEventsBatchAsync(List<EnrollmentEvent> events)
        {
            var result = await _storage.StoreEventsBatchAsync(events);
            if (result.Count > 0 && events.Count > 0)
                await _publisher.PublishAsync("events.ingested", new { count = result.Count }, events[0].TenantId);
            return result;
        }

        public Task<List<EnrollmentEvent>> GetSessionEventsAsync(string tenantId, string sessionId, int maxResults = 1000)
            => _storage.GetSessionEventsAsync(tenantId, sessionId, maxResults);

        public Task<List<SessionSummary>> SearchSessionsAsync(string? tenantId, SessionSearchFilter filter)
            => _storage.SearchSessionsAsync(tenantId, filter);

        public Task<List<SessionSummary>> SearchSessionsByEventAsync(
            string? tenantId, string eventType, string? source, string? severity,
            string? phase, int limit = 50)
            => _storage.SearchSessionsByEventAsync(tenantId, eventType, source, severity, phase, limit);

        public Task<List<SessionSummary>> SearchSessionsByCveAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk, int limit = 50)
            => _storage.SearchSessionsByCveAsync(tenantId, cveId, minCvssScore, overallRisk, limit);

        public Task UpsertEventTypeIndexBatchAsync(string tenantId, string sessionId, IEnumerable<string> eventTypes)
            => _storage.UpsertEventTypeIndexBatchAsync(tenantId, sessionId, eventTypes);

        public Task UpsertDeviceSnapshotAsync(string tenantId, string sessionId, IEnumerable<EnrollmentEvent> events)
            => _storage.UpsertDeviceSnapshotAsync(tenantId, sessionId, events);

        public Task UpsertCveIndexEntriesAsync(string tenantId, string sessionId, List<Dictionary<string, object>> findings)
            => _storage.UpsertCveIndexEntriesAsync(tenantId, sessionId, findings);
    }
}
