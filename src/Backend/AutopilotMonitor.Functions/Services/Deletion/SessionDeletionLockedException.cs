using System;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Thrown by <c>SessionDeletionGuard</c> when a write to a session-scoped table is attempted
    /// while the Sessions row carries <c>DeletionState != None</c>. Callers translate this into
    /// the right HTTP status / queue-handler outcome per the plan §5 PR3 wiring table:
    /// telemetry ingest → 410 Gone, admin actions → 409 Conflict, async queue handlers → drop
    /// envelope + audit.
    /// </summary>
    public class SessionDeletionLockedException : InvalidOperationException
    {
        public string TenantId { get; }
        public string SessionId { get; }
        public string CallerContext { get; }
        public string CurrentState { get; }
        public string? ManifestId { get; }

        public SessionDeletionLockedException(
            string tenantId, string sessionId, string callerContext,
            string currentState, string? manifestId)
            : base(BuildMessage(tenantId, sessionId, callerContext, currentState, manifestId))
        {
            TenantId = tenantId;
            SessionId = sessionId;
            CallerContext = callerContext;
            CurrentState = currentState;
            ManifestId = manifestId;
        }

        private static string BuildMessage(string tenantId, string sessionId, string callerContext, string currentState, string? manifestId)
            => $"Session {tenantId}/{sessionId} is locked by an in-flight cascade-delete (state={currentState}"
               + (string.IsNullOrEmpty(manifestId) ? "" : $", manifestId={manifestId}")
               + $"); caller={callerContext} cannot proceed.";
    }
}
