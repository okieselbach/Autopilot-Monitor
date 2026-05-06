using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Pagination
{
    /// <summary>
    /// Repository-layer page envelope. Carries the underlying store's opaque
    /// continuation token (e.g. an Azure-Tables continuation) for the function
    /// layer to wrap with tenant + filter binding via
    /// <see cref="ContinuationToken"/>.
    /// </summary>
    public sealed class RawPage<T>
    {
        public IReadOnlyList<T> Items { get; }
        public string? NextRawToken { get; }

        public RawPage(IReadOnlyList<T> items, string? nextRawToken)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            NextRawToken = nextRawToken;
        }

        public static RawPage<T> Empty { get; } = new RawPage<T>(Array.Empty<T>(), null);
    }
}
