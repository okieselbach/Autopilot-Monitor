using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Live presence record for a single web user. One row per user in the
    /// <c>UserPresence</c> table (PartitionKey = tenantId, RowKey = SHA-256(lowercase UPN) hex),
    /// overwritten (upsert) on each authenticated web request. "Active now" is derived at read
    /// time by filtering on <see cref="LastSeen"/> within a sliding window — the row itself is
    /// never deleted by the read path, so the table is self-maintaining (size = distinct users).
    /// </summary>
    public class UserPresenceEntry
    {
        /// <summary>The user's Azure AD tenant ID (PartitionKey).</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>The user's UPN / email.</summary>
        public string Upn { get; set; } = string.Empty;

        /// <summary>Resolved role at last request (e.g. "GlobalAdmin", "Admin", "Operator", "Viewer", "Authenticated").</summary>
        public string UserRole { get; set; } = string.Empty;

        /// <summary>UTC timestamp of the user's most recent authenticated request.</summary>
        public DateTime LastSeen { get; set; }
    }
}
