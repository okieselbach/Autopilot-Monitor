using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Queue-message envelope for the <c>analyze-on-enrollment-end</c> queue. Replaces the
    /// previous in-function fire-and-forget Task.Run that ran the rule engine after
    /// session-terminal events — the Task.Run could be killed mid-flight by Functions
    /// scale-in, leaving sessions without rule results until the user clicked "Analyze Now".
    /// <para>
    /// The handler ignores envelope contents beyond <see cref="TenantId"/> and
    /// <see cref="SessionId"/>; <see cref="Reason"/> and <see cref="EnqueuedAt"/> exist for
    /// diagnostics only. <see cref="Reason"/> values: <c>enrollment_complete</c>,
    /// <c>enrollment_failed</c>, <c>vulnerability_correlated</c>.
    /// </para>
    /// </summary>
    public sealed class AnalyzeOnEnrollmentEndEnvelope
    {
        /// <summary>Schema version — bump on breaking envelope changes so consumers can reject or migrate.</summary>
        public string EnvelopeVersion { get; set; } = "1";

        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;

        /// <summary>What caused the enqueue. Diagnostics only — handler does not branch on this.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>UTC time the producer enqueued the message; useful for measuring queue lag.</summary>
        public DateTime EnqueuedAt { get; set; }
    }
}
