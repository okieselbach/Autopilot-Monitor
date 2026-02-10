using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Represents a single event during enrollment
    /// </summary>
    public class EnrollmentEvent
    {
        /// <summary>
        /// Unique identifier for this event
        /// </summary>
        public string EventId { get; set; }

        /// <summary>
        /// Session identifier this event belongs to
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Tenant identifier
        /// </summary>
        public string TenantId { get; set; }

        /// <summary>
        /// Timestamp when the event occurred (UTC)
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Type of event (e.g., "phase_transition", "app_install_start", "error")
        /// </summary>
        public string EventType { get; set; }

        /// <summary>
        /// Severity level of the event (internal property, not serialized)
        /// </summary>
        [JsonIgnore]
        public EventSeverity Severity { get; set; }

        /// <summary>
        /// Severity as string for JSON serialization
        /// </summary>
        [JsonPropertyName("severity")]
        public string SeverityString => Severity.ToString();

        /// <summary>
        /// Source of the event (e.g., "IME", "EventLog", "Agent")
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// Phase during which this event occurred (internal property, not serialized)
        /// Defaults to Unknown (chronologically sorted into active phase)
        /// </summary>
        [JsonIgnore]
        public EnrollmentPhase Phase { get; set; } = EnrollmentPhase.Unknown;

        /// <summary>
        /// Phase as number for JSON serialization (frontend expects number)
        /// </summary>
        [JsonPropertyName("phase")]
        public int PhaseNumber => (int)Phase;

        /// <summary>
        /// Phase name as string for JSON serialization
        /// </summary>
        [JsonPropertyName("phaseName")]
        public string PhaseName => GetPhaseName(Phase);

        private static string GetPhaseName(EnrollmentPhase phase)
        {
            return phase switch
            {
                EnrollmentPhase.Start => "Start",
                EnrollmentPhase.DevicePreparation => "Device Preparation",
                EnrollmentPhase.DeviceSetup => "Device Setup",
                EnrollmentPhase.AppsDevice => "Apps (Device)",
                EnrollmentPhase.AccountSetup => "Account Setup",
                EnrollmentPhase.AppsUser => "Apps (User)",
                EnrollmentPhase.Complete => "Complete",
                EnrollmentPhase.Failed => "Failed",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Human-readable message
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Additional structured data
        /// </summary>
        public Dictionary<string, object> Data { get; set; }

        /// <summary>
        /// Sequence number for ordering events with same timestamp
        /// </summary>
        public long Sequence { get; set; }

        public EnrollmentEvent()
        {
            EventId = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
            Data = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Severity levels for events
    /// </summary>
    public enum EventSeverity
    {
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Critical = 4
    }
}
