using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Request to register a new session
    /// </summary>
    public class RegisterSessionRequest
    {
        public SessionRegistration Registration { get; set; }
    }

    /// <summary>
    /// Response from session registration
    /// </summary>
    public class RegisterSessionResponse
    {
        public string SessionId { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public DateTime RegisteredAt { get; set; }
    }

    /// <summary>
    /// Request to ingest events (batched)
    /// </summary>
    public class IngestEventsRequest
    {
        public string SessionId { get; set; }
        public string TenantId { get; set; }
        public List<EnrollmentEvent> Events { get; set; }
        public bool IsCompressed { get; set; }

        public IngestEventsRequest()
        {
            Events = new List<EnrollmentEvent>();
        }
    }

    /// <summary>
    /// Response from event ingestion
    /// </summary>
    public class IngestEventsResponse
    {
        public bool Success { get; set; }
        public int EventsReceived { get; set; }
        public int EventsProcessed { get; set; }
        public string Message { get; set; }
        public DateTime ProcessedAt { get; set; }

        /// <summary>
        /// Whether the request was rejected due to rate limiting
        /// </summary>
        public bool RateLimitExceeded { get; set; }

        /// <summary>
        /// Rate limit details (only populated if RateLimitExceeded is true)
        /// </summary>
        public RateLimitInfo RateLimitInfo { get; set; }

        /// <summary>
        /// Whether the device has been temporarily blocked by an admin
        /// </summary>
        public bool DeviceBlocked { get; set; }

        /// <summary>
        /// When the block expires (only populated if DeviceBlocked is true)
        /// </summary>
        public DateTime? UnblockAt { get; set; }

        /// <summary>
        /// Whether the device has been issued a remote kill signal (graceful self-destruct).
        /// The agent should execute its self-destruct routine and exit.
        /// </summary>
        public bool DeviceKillSignal { get; set; }
    }

    /// <summary>
    /// Rate limit information for UI display
    /// </summary>
    public class RateLimitInfo
    {
        /// <summary>
        /// Number of requests in current window
        /// </summary>
        public int RequestsInWindow { get; set; }

        /// <summary>
        /// Maximum allowed requests
        /// </summary>
        public int MaxRequests { get; set; }

        /// <summary>
        /// Window duration in seconds
        /// </summary>
        public int WindowDurationSeconds { get; set; }

        /// <summary>
        /// Seconds to wait before retrying
        /// </summary>
        public int RetryAfterSeconds { get; set; }
    }

    /// <summary>
    /// Request to get a short-lived SAS URL for diagnostics package upload.
    /// Called by the agent just before upload — the URL is never cached in config.
    /// </summary>
    public class GetDiagnosticsUploadUrlRequest
    {
        public string TenantId { get; set; }
        public string SessionId { get; set; }
        public string FileName { get; set; }
    }

    /// <summary>
    /// Response containing a short-lived SAS URL for diagnostics package upload.
    /// </summary>
    public class GetDiagnosticsUploadUrlResponse
    {
        public bool Success { get; set; }
        public string UploadUrl { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Session summary for UI display
    /// </summary>
    public class SessionSummary
    {
        public string SessionId { get; set; }
        public string TenantId { get; set; }
        public string SerialNumber { get; set; }
        public string DeviceName { get; set; }
        public string Manufacturer { get; set; }
        public string Model { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // Serialize as integer (0-7) not string for frontend compatibility
        public int CurrentPhase { get; set; }
        public string CurrentPhaseDetail { get; set; }
        public SessionStatus Status { get; set; }
        public string FailureReason { get; set; }
        public int EventCount { get; set; }
        public int? DurationSeconds { get; set; }

        /// <summary>
        /// Enrollment type: "v1" (Autopilot Classic/ESP) or "v2" (Windows Device Preparation).
        /// Defaults to "v1" for sessions that predate this field.
        /// </summary>
        public string EnrollmentType { get; set; } = "v1";

        /// <summary>
        /// Blob name of the uploaded diagnostics archive (null if not uploaded).
        /// Used to construct a download URL via the tenant's Blob Storage SAS URL.
        /// </summary>
        public string DiagnosticsBlobName { get; set; }

        /// <summary>
        /// Timestamp of the most recently received event for this session.
        /// Updated on every event batch ingestion. Used by maintenance to detect
        /// sessions that are still actively sending data beyond the configured window.
        /// Null for sessions that predate this field.
        /// </summary>
        public DateTime? LastEventAt { get; set; }

        /// <summary>
        /// Whether this session used WhiteGlove (Pre-Provisioning).
        /// Set when a whiteglove_complete event is processed.
        /// </summary>
        public bool IsPreProvisioned { get; set; }

        /// <summary>
        /// Timestamp when the WhiteGlove session resumed for user enrollment (Part 2).
        /// Set when the agent sends a whiteglove_resumed event or re-registers from Pending state.
        /// Used to compute the user enrollment duration (Duration 2) for Teams notifications.
        /// </summary>
        public DateTime? ResumedAt { get; set; }

        /// <summary>
        /// Whether the Autopilot profile indicates Hybrid Azure AD Join.
        /// Derived from CloudAssignedDomainJoinMethod == 1 in the Autopilot profile.
        /// </summary>
        public bool IsHybridJoin { get; set; }

        // Device detail fields — stored in the Sessions table but omitted from earlier versions
        public string OsName { get; set; }
        public string OsBuild { get; set; }
        public string OsDisplayVersion { get; set; }
        public string OsEdition { get; set; }
        public string OsLanguage { get; set; }
        public bool IsUserDriven { get; set; }
        public string AgentVersion { get; set; }

        // Geographic location fields — populated from device_location event geo data
        public string GeoCountry { get; set; } = string.Empty;
        public string GeoRegion { get; set; } = string.Empty;
        public string GeoCity { get; set; } = string.Empty;
        public string GeoLoc { get; set; } = string.Empty;
    }

    /// <summary>
    /// Paginated result of session listings.
    /// Used by GetSessionsAsync and GetAllSessionsAsync to support cursor-based "Load More".
    /// </summary>
    public class SessionPage
    {
        public List<SessionSummary> Sessions { get; set; } = new();
        public bool HasMore { get; set; }
        public string Cursor { get; set; }
    }

    /// <summary>
    /// Status of an enrollment session
    /// </summary>
    public enum SessionStatus
    {
        InProgress,
        Pending,      // WhiteGlove pre-provisioning complete, awaiting user enrollment
        Succeeded,
        Failed,
        Unknown
    }

    /// <summary>
    /// Request to submit a session report for analysis by the Autopilot Monitor team.
    /// Sent as JSON from the frontend; the backend creates the ZIP and uploads to central storage.
    /// </summary>
    public class SubmitSessionReportRequest
    {
        public string TenantId { get; set; }
        public string SessionId { get; set; }
        public string Comment { get; set; }
        public string Email { get; set; }

        /// <summary>Session row as CSV (single data row with header)</summary>
        public string SessionCsv { get; set; }

        /// <summary>Pre-generated UI timeline export (TXT)</summary>
        public string TimelineExportTxt { get; set; }

        /// <summary>Pre-generated raw events table export (CSV)</summary>
        public string EventsCsv { get; set; }

        /// <summary>Pre-generated analysis rule results export (CSV)</summary>
        public string RuleResultsCsv { get; set; }

        /// <summary>Base64-encoded screenshot image (optional)</summary>
        public string ScreenshotBase64 { get; set; }

        /// <summary>Original screenshot file name for extension detection</summary>
        public string ScreenshotFileName { get; set; }

        /// <summary>Base64-encoded agent log file (optional, max 5 MB)</summary>
        public string AgentLogBase64 { get; set; }

        /// <summary>Original agent log file name</summary>
        public string AgentLogFileName { get; set; }
    }

    /// <summary>
    /// Response from session report submission
    /// </summary>
    public class SubmitSessionReportResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string ReportId { get; set; }
    }

    /// <summary>
    /// Session report metadata for the admin-config reports table
    /// </summary>
    public class SessionReportMetadata
    {
        public string ReportId { get; set; }
        public string TenantId { get; set; }
        public string SessionId { get; set; }
        public string Comment { get; set; }
        public string Email { get; set; }
        public string BlobName { get; set; }
        public string SubmittedBy { get; set; }
        public DateTime SubmittedAt { get; set; }
        public string AdminNote { get; set; }
    }

    /// <summary>
    /// Response containing geographic performance metrics aggregated by location.
    /// </summary>
    public class GeographicMetricsResponse
    {
        public bool Success { get; set; }
        public List<LocationMetrics> Locations { get; set; } = new();
        public GlobalAverages GlobalAverages { get; set; } = new();
        public DateTime ComputedAt { get; set; }
        public int TotalSessions { get; set; }
        public int LocationsWithData { get; set; }
        /// <summary>Whether geo-location collection is enabled for this tenant</summary>
        public bool GeoLocationEnabled { get; set; } = true;
    }

    /// <summary>
    /// Performance metrics for a single geographic location.
    /// </summary>
    public class LocationMetrics
    {
        public string LocationKey { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Loc { get; set; } = string.Empty;

        public int SessionCount { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public double SuccessRate { get; set; }

        public double AvgDurationMinutes { get; set; }
        public double MedianDurationMinutes { get; set; }
        public double P95DurationMinutes { get; set; }

        /// <summary>Average number of apps installed per session at this location</summary>
        public double AvgAppCount { get; set; }
        /// <summary>Average minutes per app (AvgDurationMinutes / AvgAppCount)</summary>
        public double MinutesPerApp { get; set; }
        /// <summary>Normalized score: 100 = global median, lower is better</summary>
        public double AppLoadScore { get; set; }

        /// <summary>Average download throughput in bytes/sec at this location</summary>
        public double AvgThroughputBytesPerSec { get; set; }
        public long TotalDownloadBytes { get; set; }

        /// <summary>Percentage difference from global avg duration (negative = faster)</summary>
        public double DurationVsGlobalPct { get; set; }
        /// <summary>Percentage difference from global avg throughput (positive = faster)</summary>
        public double ThroughputVsGlobalPct { get; set; }

        public bool IsOutlier { get; set; }
        /// <summary>"fast", "slow", or null</summary>
        public string OutlierDirection { get; set; }
    }

    // -----------------------------------------------------------------------
    // Bootstrap session API models (OOBE pre-enrollment)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Request to create a new bootstrap session for OOBE agent deployment
    /// </summary>
    public class CreateBootstrapSessionRequest
    {
        public string TenantId { get; set; }

        /// <summary>
        /// How long the bootstrap URL should be valid (1–168 hours, default 8)
        /// </summary>
        public int ValidityHours { get; set; } = 8;

        /// <summary>
        /// Optional human-readable label (e.g. "Lab A", "Floor 3")
        /// </summary>
        public string Label { get; set; }
    }

    /// <summary>
    /// Response after creating a bootstrap session
    /// </summary>
    public class CreateBootstrapSessionResponse
    {
        public bool Success { get; set; }
        public string ShortCode { get; set; }
        public string BootstrapUrl { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// A single bootstrap session item for the list response
    /// </summary>
    public class BootstrapSessionListItem
    {
        public string ShortCode { get; set; }
        public string Label { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string CreatedByUpn { get; set; }
        public bool IsRevoked { get; set; }
        public bool IsExpired { get; set; }
        public int UsageCount { get; set; }
    }

    /// <summary>
    /// Response containing a list of bootstrap sessions for a tenant
    /// </summary>
    public class ListBootstrapSessionsResponse
    {
        public bool Success { get; set; }
        public List<BootstrapSessionListItem> Sessions { get; set; } = new List<BootstrapSessionListItem>();
    }

    /// <summary>
    /// Response from validating a bootstrap code (anonymous, called by the /go/{code} route)
    /// </summary>
    public class ValidateBootstrapCodeResponse
    {
        public bool Success { get; set; }
        public string TenantId { get; set; }
        public string Token { get; set; }
        public string AgentDownloadUrl { get; set; }
        public DateTime ExpiresAt { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Global average benchmarks for geographic comparison.
    /// </summary>
    public class GlobalAverages
    {
        public double AvgDurationMinutes { get; set; }
        public double MedianDurationMinutes { get; set; }
        public double AvgMinutesPerApp { get; set; }
        public double AvgThroughputBytesPerSec { get; set; }
        public double StdDevDurationMinutes { get; set; }
    }
}
