using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for session and event data.
    /// Covers: Sessions, SessionsIndex, Events tables.
    /// </summary>
    public interface ISessionRepository
    {
        // --- Session CRUD ---
        Task<bool> StoreSessionAsync(SessionRegistration registration);
        Task<SessionSummary?> GetSessionAsync(string tenantId, string sessionId);
        Task<string?> FindSessionTenantIdAsync(string sessionId);
        Task<SessionPage> GetSessionsAsync(string tenantId, int maxResults = 100, string? cursor = null, int? days = null);
        Task<SessionPage> GetAllSessionsAsync(int maxResults = 100, string? cursor = null, int? days = null);
        Task<bool> DeleteSessionAsync(string tenantId, string sessionId);

        // --- Session Updates ---
        Task<bool> UpdateSessionStatusAsync(
            string tenantId, string sessionId, SessionStatus status,
            EnrollmentPhase? currentPhase = null, string? failureReason = null,
            DateTime? completedAt = null, DateTime? earliestEventTimestamp = null,
            DateTime? latestEventTimestamp = null, bool? isPreProvisioned = null,
            bool? isUserDriven = null, DateTime? resumedAt = null);
        Task IncrementSessionEventCountAsync(
            string tenantId, string sessionId, int increment,
            DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null,
            EnrollmentPhase? currentPhase = null);
        Task UpdateSessionDiagnosticsBlobAsync(string tenantId, string sessionId, string blobName);
        Task SetSessionPreProvisionedAsync(string tenantId, string sessionId, bool isPreProvisioned,
            SessionStatus? status = null, bool? isUserDriven = null);
        Task UpdateSessionGeoAsync(string tenantId, string sessionId,
            string? country, string? region, string? city, string? loc);
        Task UpdateSessionImeAgentVersionAsync(string tenantId, string sessionId, string version);

        // --- IME Version History ---
        Task<bool> RecordImeVersionAsync(string version, string tenantId, string sessionId);
        Task<List<ImeVersionHistoryEntry>> GetImeVersionHistoryAsync();

        // --- Events ---
        Task<bool> StoreEventAsync(EnrollmentEvent evt);
        Task<List<EnrollmentEvent>> StoreEventsBatchAsync(List<EnrollmentEvent> events);
        Task<List<EnrollmentEvent>> GetSessionEventsAsync(string tenantId, string sessionId, int maxResults = 1000);

        // --- Search ---
        Task<List<QuickSearchResult>> QuickSearchSessionsAsync(string? tenantId, string query, int limit = 10);
        Task<List<SessionSummary>> SearchSessionsAsync(string? tenantId, SessionSearchFilter filter);
        Task<List<SessionSummary>> SearchSessionsByEventAsync(
            string? tenantId, string eventType, string? source, string? severity,
            string? phase, int limit = 50);
        Task<List<SessionSummary>> SearchSessionsByCveAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk, int limit = 50);

        // --- Agent Indexes ---
        Task UpsertEventTypeIndexBatchAsync(string tenantId, string sessionId, IEnumerable<string> eventTypes);
        Task UpsertDeviceSnapshotAsync(string tenantId, string sessionId, IEnumerable<EnrollmentEvent> events);
        Task UpsertCveIndexEntriesAsync(string tenantId, string sessionId, List<Dictionary<string, object>> findings);
    }
}
