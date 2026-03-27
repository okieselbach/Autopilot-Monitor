using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Request to submit a session report for analysis by the Autopilot Monitor team.
    /// Sent as JSON from the frontend; the backend creates the ZIP and uploads to central storage.
    /// </summary>
    public class SubmitSessionReportRequest
    {
        public string TenantId { get; set; } = default!;
        public string SessionId { get; set; } = default!;
        public string Comment { get; set; } = default!;
        public string Email { get; set; } = default!;

        /// <summary>Session row as CSV (single data row with header)</summary>
        public string SessionCsv { get; set; } = default!;

        /// <summary>Pre-generated UI timeline export (TXT)</summary>
        public string TimelineExportTxt { get; set; } = default!;

        /// <summary>Pre-generated raw events table export (CSV)</summary>
        public string EventsCsv { get; set; } = default!;

        /// <summary>Pre-generated analysis rule results export (CSV)</summary>
        public string RuleResultsCsv { get; set; } = default!;

        /// <summary>Base64-encoded screenshot image (optional)</summary>
        public string ScreenshotBase64 { get; set; } = default!;

        /// <summary>Original screenshot file name for extension detection</summary>
        public string ScreenshotFileName { get; set; } = default!;

        /// <summary>Base64-encoded agent log file (optional, max 5 MB)</summary>
        public string AgentLogBase64 { get; set; } = default!;

        /// <summary>Original agent log file name</summary>
        public string AgentLogFileName { get; set; } = default!;
    }

    /// <summary>
    /// Response from session report submission
    /// </summary>
    public class SubmitSessionReportResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
        public string ReportId { get; set; } = default!;
    }

    /// <summary>
    /// Session report metadata for the admin-config reports table
    /// </summary>
    public class SessionReportMetadata
    {
        public string ReportId { get; set; } = default!;
        public string TenantId { get; set; } = default!;
        public string SessionId { get; set; } = default!;
        public string Comment { get; set; } = default!;
        public string Email { get; set; } = default!;
        public string BlobName { get; set; } = default!;
        public string SubmittedBy { get; set; } = default!;
        public DateTime SubmittedAt { get; set; }
        public string AdminNote { get; set; } = default!;
    }
}
