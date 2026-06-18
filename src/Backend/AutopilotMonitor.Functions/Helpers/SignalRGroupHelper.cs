namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Helper class for parsing SignalR group names.
/// Group formats:
///   - "tenant-{tenantId}"                       — tenant-wide live updates (sessions, etc.)
///   - "tenant-{tenantId}-notify-member"          — tenant notification bell (Member-tier visibility)
///   - "tenant-{tenantId}-notify-admin"           — tenant notification bell (Admin-tier visibility)
///   - "session-{tenantId}-{sessionId}"           — single-session live updates
///   - "global-admins"                            — global notification bell
/// </summary>
public static class SignalRGroupHelper
{
    public const string TenantNotifyMemberSuffix = "-notify-member";
    public const string TenantNotifyAdminSuffix = "-notify-admin";

    /// <summary>
    /// Extracts tenant ID from SignalR group name.
    /// </summary>
    public static string? ExtractTenantIdFromGroupName(string groupName)
    {
        if (groupName.StartsWith("session-"))
        {
            // Format: "session-{tenantId}-{sessionId}"
            // Extract everything between "session-" and the last 5 GUID segments
            var parts = groupName.Split('-');
            if (parts.Length >= 7) // "session" + 5 GUID parts (tenant) + 5 GUID parts (session)
            {
                // Reconstruct tenant GUID from parts 1-5
                return string.Join("-", parts.Skip(1).Take(5));
            }
            return null;
        }

        if (groupName.StartsWith("tenant-"))
        {
            // Strip the leading "tenant-" prefix, then strip the optional "-notify-{member,admin}" suffix.
            var withoutPrefix = groupName.Substring("tenant-".Length);
            if (withoutPrefix.EndsWith(TenantNotifyAdminSuffix))
                return withoutPrefix.Substring(0, withoutPrefix.Length - TenantNotifyAdminSuffix.Length);
            if (withoutPrefix.EndsWith(TenantNotifyMemberSuffix))
                return withoutPrefix.Substring(0, withoutPrefix.Length - TenantNotifyMemberSuffix.Length);
            return withoutPrefix;
        }

        return null;
    }

    /// <summary>
    /// True for the Admin-tier tenant notification group. Joining requires Tenant-Admin or Global-Admin.
    /// </summary>
    public static bool IsTenantNotifyAdminGroup(string groupName)
        => groupName.StartsWith("tenant-") && groupName.EndsWith(TenantNotifyAdminSuffix);

    /// <summary>
    /// True for the Member-tier tenant notification group. Joining requires tenant membership
    /// (any Admin/Operator/Viewer role) or Global-Admin — the live push carries the full
    /// notification payload, which is otherwise MemberRead-gated at the REST layer, so a roleless
    /// authenticated end user must not be allowed to join it.
    /// </summary>
    public static bool IsTenantNotifyMemberGroup(string groupName)
        => groupName.StartsWith("tenant-") && groupName.EndsWith(TenantNotifyMemberSuffix);

    public static string TenantNotifyMemberGroup(string tenantId) => $"tenant-{tenantId}{TenantNotifyMemberSuffix}";
    public static string TenantNotifyAdminGroup(string tenantId) => $"tenant-{tenantId}{TenantNotifyAdminSuffix}";
    public const string GlobalAdminsGroup = "global-admins";

    public static string ExtractLogPrefix(string groupName)
    {
        // Extract session ID from group name: "session-{tenantId}-{sessionId}"
        if (groupName.StartsWith("session-"))
        {
            var parts = groupName.Split('-');
            if (parts.Length > 2)
            {
                var sessionId = string.Join("-", parts.Skip(parts.Length - 5).Take(5)); // Last 5 parts form the GUID
                return $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
            }
        }
        // For tenant groups: "tenant-{tenantId}"
        return $"[Group: {groupName.Substring(0, Math.Min(20, groupName.Length))}]";
    }
}
