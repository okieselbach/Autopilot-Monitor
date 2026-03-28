using System.Text.RegularExpressions;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Normalizes request paths for usage grouping by replacing GUIDs with {id}.
/// E.g. "/api/sessions/abc-def-123/events" -> "sessions/{id}/events"
/// </summary>
public static class EndpointNormalizer
{
    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    public static string Normalize(string path)
    {
        // Strip /api/ prefix
        var normalized = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(5)
            : path.TrimStart('/');

        // Replace GUIDs with {id}
        normalized = GuidPattern.Replace(normalized, "{id}");

        return normalized.ToLowerInvariant();
    }
}
