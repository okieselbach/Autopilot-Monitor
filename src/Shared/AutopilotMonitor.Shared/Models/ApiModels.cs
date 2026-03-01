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

        // Device detail fields — stored in the Sessions table but omitted from earlier versions
        public string OsBuild { get; set; }
        public string OsEdition { get; set; }
        public string OsLanguage { get; set; }
        public bool IsUserDriven { get; set; }
        public string AgentVersion { get; set; }
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
}
