using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Defines how to analyze collected events to detect issues
    /// Analyze rules run server-side during event ingestion
    /// </summary>
    public class AnalyzeRule
    {
        /// <summary>
        /// Unique rule identifier (e.g., "ANALYZE-NET-001")
        /// </summary>
        public string RuleId { get; set; }

        /// <summary>
        /// Human-readable rule title (e.g., "Proxy Authentication Required")
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Detailed description of what this rule detects
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Severity level: "info", "warning", "high", "critical"
        /// </summary>
        public string Severity { get; set; }

        /// <summary>
        /// Rule category: network, identity, enrollment, apps, esp, device
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Semantic version of this rule (e.g., "1.0.0")
        /// </summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Author of this rule
        /// </summary>
        public string Author { get; set; } = "Autopilot Monitor";

        /// <summary>
        /// Whether this rule is currently enabled for the tenant
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Whether this is a built-in rule (shipped with the system)
        /// </summary>
        public bool IsBuiltIn { get; set; } = true;

        /// <summary>
        /// Whether this is a community-contributed rule
        /// Community rules behave like built-in rules (read-only, state stored separately)
        /// but are displayed with a distinct "Community" badge in the portal
        /// </summary>
        public bool IsCommunity { get; set; } = false;

        /// <summary>
        /// Rule trigger type: "single" (matches individual events) or "correlation" (combines multiple event types)
        /// Both types run at the same time during analysis - this field is organizational/descriptive
        /// </summary>
        public string Trigger { get; set; } = "single";

        // ===== MATCHING CONDITIONS =====

        /// <summary>
        /// Conditions that must be evaluated against the event stream
        /// All required conditions must match for the rule to fire
        /// </summary>
        public List<RuleCondition> Conditions { get; set; } = new List<RuleCondition>();

        // ===== CONFIDENCE SCORING =====

        /// <summary>
        /// Base confidence score (0-100) when the rule's required conditions match
        /// Additional confidence is added from ConfidenceFactors
        /// </summary>
        public int BaseConfidence { get; set; } = 50;

        /// <summary>
        /// Additional factors that increase confidence when matched
        /// </summary>
        public List<ConfidenceFactor> ConfidenceFactors { get; set; } = new List<ConfidenceFactor>();

        /// <summary>
        /// Minimum confidence score (0-100) to create a RuleResult
        /// Default: 40
        /// </summary>
        public int ConfidenceThreshold { get; set; } = 40;

        // ===== RESULTS =====

        /// <summary>
        /// Detailed explanation of the detected issue
        /// Supports markdown formatting
        /// </summary>
        public string Explanation { get; set; }

        /// <summary>
        /// Steps to remediate the detected issue
        /// </summary>
        public List<RemediationStep> Remediation { get; set; } = new List<RemediationStep>();

        /// <summary>
        /// Links to relevant documentation
        /// </summary>
        public List<RelatedDoc> RelatedDocs { get; set; } = new List<RelatedDoc>();

        // ===== METADATA =====

        /// <summary>
        /// Tags for filtering and categorization
        /// </summary>
        public string[] Tags { get; set; } = new string[0];

        /// <summary>
        /// When this rule was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When this rule was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// A condition that is evaluated against the event stream
    /// </summary>
    public class RuleCondition
    {
        /// <summary>
        /// Descriptive name for this signal (e.g., "proxy_407_error")
        /// </summary>
        public string Signal { get; set; }

        /// <summary>
        /// Source of the signal: "event_type", "event_data", "phase_duration", "event_count", "app_install_duration", "event_correlation"
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Event type to match on.
        /// For "event_type"/"event_data": the event type to match.
        /// For "event_correlation": the FIRST event type (Event A).
        /// </summary>
        public string EventType { get; set; }

        /// <summary>
        /// Data field to match on.
        /// For "event_data": field to check with Operator/Value.
        /// For "event_correlation": optional filter field on Event B (the second event).
        /// Uses dot notation for nested fields (e.g., "data.errorCode").
        /// </summary>
        public string DataField { get; set; }

        /// <summary>
        /// Comparison operator: "equals", "contains", "regex", "gt", "lt", "gte", "lte", "exists", "count_gte"
        /// For "event_correlation": operator for the Event B filter (applied to DataField).
        /// </summary>
        public string Operator { get; set; }

        /// <summary>
        /// Value to compare against.
        /// For "event_correlation": value for the Event B filter.
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// Whether this condition must match for the rule to fire
        /// If false, it only contributes to confidence scoring
        /// </summary>
        public bool Required { get; set; } = false;

        // ===== Event Correlation Properties =====
        // Used only when Source = "event_correlation"

        /// <summary>
        /// The second event type to correlate with (Event B).
        /// Example: "app_install_failed"
        /// </summary>
        public string CorrelateEventType { get; set; }

        /// <summary>
        /// The data field to join on — must have the same value in both Event A and Event B.
        /// Example: "appId" means both events must share the same appId value.
        /// </summary>
        public string JoinField { get; set; }

        /// <summary>
        /// Maximum time in seconds between Event A and Event B. Null or 0 means no time limit.
        /// </summary>
        public int? TimeWindowSeconds { get; set; }

        /// <summary>
        /// Optional filter field on Event A (the first event).
        /// Combined with EventAFilterOperator and EventAFilterValue.
        /// </summary>
        public string EventAFilterField { get; set; }

        /// <summary>
        /// Operator for the Event A filter. Uses same operators as the main Operator field.
        /// </summary>
        public string EventAFilterOperator { get; set; }

        /// <summary>
        /// Value for the Event A filter.
        /// </summary>
        public string EventAFilterValue { get; set; }
    }

    /// <summary>
    /// A factor that increases confidence when matched
    /// </summary>
    public class ConfidenceFactor
    {
        /// <summary>
        /// Descriptive name for this factor
        /// </summary>
        public string Signal { get; set; }

        /// <summary>
        /// Condition expression (e.g., "count >= 5", "exists", "duration > 300")
        /// </summary>
        public string Condition { get; set; }

        /// <summary>
        /// Confidence weight to add when this factor matches (0-100)
        /// Total confidence = BaseConfidence + sum of matched factor weights, capped at 100
        /// </summary>
        public int Weight { get; set; }
    }

    /// <summary>
    /// A remediation step with title and sub-steps
    /// </summary>
    public class RemediationStep
    {
        /// <summary>
        /// Title of the remediation approach
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// Ordered steps to execute
        /// </summary>
        public List<string> Steps { get; set; } = new List<string>();
    }

    /// <summary>
    /// A link to related documentation
    /// </summary>
    public class RelatedDoc
    {
        /// <summary>
        /// Display title for the link
        /// </summary>
        public string Title { get; set; }

        /// <summary>
        /// URL to the documentation
        /// </summary>
        public string Url { get; set; }
    }
}
