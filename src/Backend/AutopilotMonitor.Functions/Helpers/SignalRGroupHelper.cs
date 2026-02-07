namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Helper class for parsing SignalR group names.
/// Group formats: "tenant-{tenantId}", "session-{tenantId}-{sessionId}", or "galactic-admins"
/// </summary>
public static class SignalRGroupHelper
{
    /// <summary>
    /// Extracts tenant ID from SignalR group name.
    /// </summary>
    public static string? ExtractTenantIdFromGroupName(string groupName)
    {
        if (groupName.StartsWith("tenant-"))
        {
            // Format: "tenant-{tenantId}"
            return groupName.Substring("tenant-".Length);
        }
        else if (groupName.StartsWith("session-"))
        {
            // Format: "session-{tenantId}-{sessionId}"
            // Extract everything between "session-" and the last 5 GUID segments
            var parts = groupName.Split('-');
            if (parts.Length >= 7) // "session" + 5 GUID parts (tenant) + 5 GUID parts (session)
            {
                // Reconstruct tenant GUID from parts 1-5
                return string.Join("-", parts.Skip(1).Take(5));
            }
        }
        return null;
    }

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
