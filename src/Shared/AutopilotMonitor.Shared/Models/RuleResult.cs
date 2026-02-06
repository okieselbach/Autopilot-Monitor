using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Result of an analyze rule evaluation against a session's events
    /// Stored in the RuleResults table and displayed in the session detail UI
    /// </summary>
    public class RuleResult
    {
        /// <summary>
        /// Unique identifier for this result
        /// </summary>
        public string ResultId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Session this result belongs to
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Tenant this result belongs to
        /// </summary>
        public string TenantId { get; set; }

        /// <summary>
        /// The rule that produced this result
        /// </summary>
        public string RuleId { get; set; }

        /// <summary>
        /// Human-readable title of the rule
        /// </summary>
        public string RuleTitle { get; set; }

        /// <summary>
        /// Severity level: "info", "warning", "high", "critical"
        /// </summary>
        public string Severity { get; set; }

        /// <summary>
        /// Rule category: network, identity, enrollment, apps, esp, device
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Confidence score (0-100)
        /// Higher = more confident this issue is the root cause
        /// </summary>
        public int ConfidenceScore { get; set; }

        /// <summary>
        /// Detailed explanation of the detected issue
        /// </summary>
        public string Explanation { get; set; }

        /// <summary>
        /// Remediation steps for the detected issue
        /// </summary>
        public List<RemediationStep> Remediation { get; set; } = new List<RemediationStep>();

        /// <summary>
        /// Links to relevant documentation
        /// </summary>
        public List<RelatedDoc> RelatedDocs { get; set; } = new List<RelatedDoc>();

        /// <summary>
        /// Evidence: which conditions matched and their values
        /// </summary>
        public Dictionary<string, object> MatchedConditions { get; set; } = new Dictionary<string, object>();

        /// <summary>
        /// When this issue was detected
        /// </summary>
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
    }
}
