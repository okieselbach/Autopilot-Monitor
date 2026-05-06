using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Pagination
{
    /// <summary>
    /// Internal page envelope used by paginated endpoints. The wire shape
    /// (collection key + count + nextLink) is produced by the endpoint when it
    /// projects this onto the response — we keep <c>events</c>/<c>sessions</c>/
    /// <c>logs</c>/<c>reports</c> etc. as the legacy collection key per the
    /// pagination rollout plan, so this generic container deliberately does not
    /// dictate the JSON property name.
    /// </summary>
    public sealed class PaginatedResult<T>
    {
        public IReadOnlyList<T> Items { get; }
        public int Count { get; }
        public string? NextLink { get; }

        public PaginatedResult(IReadOnlyList<T> items, string? nextLink)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            Count = items.Count;
            NextLink = nextLink;
        }

        public static PaginatedResult<T> Empty { get; } = new PaginatedResult<T>(Array.Empty<T>(), null);
    }
}
