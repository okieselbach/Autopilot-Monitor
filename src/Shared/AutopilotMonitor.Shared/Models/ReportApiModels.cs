using System;

namespace AutopilotMonitor.Shared.Models
{
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
