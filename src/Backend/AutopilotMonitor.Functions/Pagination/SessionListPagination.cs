using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Pagination
{
    /// <summary>
    /// Pure helpers for the session-list pagination surface
    /// (<c>/api/sessions</c> + <c>/api/global/sessions</c>). Replaces the
    /// legacy <c>?limit=</c> + <c>?cursor=</c> wire shape with the rollout
    /// plan's <c>?pageSize=</c> + <c>?continuation=</c> + <c>nextLink</c>
    /// contract.
    /// </summary>
    public static class SessionListPagination
    {
        public const int DefaultPageSize = 100;
        public const int MaxPageSize = 1000;

        /// <summary>
        /// Fingerprint binding the token to <c>(scope, callerTenantId, days, filterTenantId)</c>.
        /// <paramref name="filterTenantId"/> is for the global endpoint only.
        /// </summary>
        public static string Fingerprint(string scope, string callerTenantId, int? days, string? filterTenantId = null) =>
            ContinuationToken.ComputeFingerprint(new[]
            {
                new KeyValuePair<string, string?>("scope", scope),
                new KeyValuePair<string, string?>("tenantId", callerTenantId),
                new KeyValuePair<string, string?>("days", days?.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string?>("filterTenantId", filterTenantId),
            });

        public sealed class Parsed
        {
            public int PageSize { get; init; }
            public string? Continuation { get; init; }
            public int? Days { get; init; }
            public string? FilterTenantId { get; init; }
            public string? Error { get; init; }
        }

        public static Parsed ParseQuery(NameValueCollection? query, bool acceptFilterTenantId)
        {
            var pageSizeRaw = query?["pageSize"];
            var continuationRaw = query?["continuation"];
            var daysRaw = query?["days"];
            var filterTenantIdRaw = acceptFilterTenantId ? query?["tenantId"] : null;

            int pageSize = DefaultPageSize;
            if (!string.IsNullOrEmpty(pageSizeRaw))
            {
                if (!int.TryParse(pageSizeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    return new Parsed { PageSize = DefaultPageSize, Error = "pageSize must be an integer" };
                if (n < 1 || n > MaxPageSize)
                    return new Parsed { PageSize = DefaultPageSize, Error = $"pageSize must be between 1 and {MaxPageSize}" };
                pageSize = n;
            }

            int? days = null;
            if (!string.IsNullOrEmpty(daysRaw))
            {
                if (!int.TryParse(daysRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) || d < 1)
                    return new Parsed { PageSize = pageSize, Error = "days must be a positive integer" };
                days = d;
            }

            return new Parsed
            {
                PageSize = pageSize,
                Continuation = string.IsNullOrEmpty(continuationRaw) ? null : continuationRaw,
                Days = days,
                FilterTenantId = string.IsNullOrEmpty(filterTenantIdRaw) ? null : filterTenantIdRaw,
            };
        }

        public static bool TryAcceptContinuation(
            string raw,
            string scope,
            string callerTenantId,
            int? days,
            string? filterTenantId,
            out string azureToken,
            out string? rejectReason)
        {
            var fp = Fingerprint(scope, callerTenantId, days, filterTenantId);
            return ContinuationToken.TryDecode(raw, callerTenantId, fp, out azureToken, out rejectReason);
        }

        public static string BuildNextLink(
            string basePath,
            int pageSize,
            string wireContinuation,
            int? days,
            string? filterTenantId)
        {
            var sb = new StringBuilder(basePath);
            sb.Append('?');
            sb.Append("pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture));
            sb.Append("&continuation=").Append(System.Uri.EscapeDataString(wireContinuation));
            if (days.HasValue)
            {
                sb.Append("&days=").Append(days.Value.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(filterTenantId))
            {
                sb.Append("&tenantId=").Append(System.Uri.EscapeDataString(filterTenantId!));
            }
            return sb.ToString();
        }
    }
}
