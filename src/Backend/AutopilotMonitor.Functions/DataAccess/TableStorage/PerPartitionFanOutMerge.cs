using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Generic helpers driving cross-partition merge-pagination for global views
    /// where Azure-Tables' native (PK asc, RK asc) ordering would otherwise
    /// surface partitions one-at-a-time instead of globally newest-first.
    /// Used by ops-events (categories as partitions) and global audit logs
    /// (tenants as partitions).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per-partition continuation anchored on RowKey (not Azure's opaque token):
    /// <c>LastRowKey</c> = the oldest RowKey we have already returned for this
    /// partition. Next fetch filters <c>RowKey gt LastRowKey</c> — natural
    /// ordering within the partition (revtick → newest-first) means this skips
    /// exactly what we showed and re-considers everything older.
    /// </para>
    /// <para>
    /// Why not Azure's continuation token? Cross-partition merge can drop items
    /// that were fetched but didn't survive the top-pageSize cut. If we advanced
    /// an Azure cursor past those leftovers, we'd lose them forever. With a
    /// RowKey bound we simply don't advance the continuation for partitions whose
    /// fetched items lost the merge — they get re-fetched next call and compete
    /// again at a lower floor.
    /// </para>
    /// </remarks>
    internal static class PerPartitionFanOutMerge
    {
        internal sealed record PartitionContinuation(string? LastRowKey, bool Exhausted);

        internal sealed record PartitionFetchResult<T>(
            string Partition,
            IReadOnlyList<(string RowKey, T Item)> Items);

        /// <summary>
        /// Pure merge-and-advance step. Given pre-fetched per-partition items
        /// (already newest-first within each list) and the previous per-partition
        /// continuation map, returns the top-pageSize merged items plus the next
        /// continuation map.
        /// </summary>
        public static (List<T> Items, Dictionary<string, PartitionContinuation> NextContinuations)
            MergeAndAdvance<T>(
                IReadOnlyList<PartitionFetchResult<T>> fetched,
                IReadOnlyDictionary<string, PartitionContinuation> priorContinuations,
                int pageSize,
                Func<T, DateTime> timestampSelector)
        {
            var allFetched = fetched
                .SelectMany(r => r.Items.Select(t => (part: r.Partition, rk: t.RowKey, item: t.Item)))
                .OrderByDescending(t => timestampSelector(t.item))
                .ToList();

            var merged = allFetched.Take(pageSize).ToList();
            var returnedItems = merged.Select(t => t.item).ToList();

            var newContinuations = new Dictionary<string, PartitionContinuation>(StringComparer.Ordinal);
            foreach (var r in fetched)
            {
                var returnedFromPart = merged.Where(m => m.part == r.Partition).ToList();
                if (returnedFromPart.Count > 0)
                {
                    // Advance to the oldest RowKey we returned for this partition.
                    // Revtick scheme: larger RowKey string = older timestamp, so
                    // OrdinalCompare-max gives us the oldest of the returned set.
                    var oldestRk = returnedFromPart
                        .Select(m => m.rk)
                        .OrderBy(rk => rk, StringComparer.Ordinal)
                        .Last();
                    newContinuations[r.Partition] = new PartitionContinuation(oldestRk, Exhausted: false);
                }
                else if (r.Items.Count == 0)
                {
                    // No more items past current continuation → partition exhausted.
                    priorContinuations.TryGetValue(r.Partition, out var prior);
                    newContinuations[r.Partition] = new PartitionContinuation(prior?.LastRowKey, Exhausted: true);
                }
                else
                {
                    // Fetched some but they all lost the merge cut to other partitions.
                    // Keep continuation where it was — they'll be re-fetched and likely
                    // win on the next page when the floor drops. Wasted bandwidth, but
                    // correct (no item silently dropped).
                    priorContinuations.TryGetValue(r.Partition, out var prior);
                    newContinuations[r.Partition] = prior ?? new PartitionContinuation(null, Exhausted: false);
                }
            }

            // Carry over already-exhausted entries from the prior call so they
            // stay marked across pages (we only fetch from active partitions on
            // this round and don't see exhausted ones in `fetched`).
            foreach (var kv in priorContinuations)
            {
                if (kv.Value.Exhausted && !newContinuations.ContainsKey(kv.Key))
                    newContinuations[kv.Key] = kv.Value;
            }

            return (returnedItems, newContinuations);
        }

        public static string EncodeMultiContinuation(IReadOnlyDictionary<string, PartitionContinuation> continuations)
        {
            var doc = new Dictionary<string, object?>
            {
                ["v"] = 1,
                ["c"] = continuations.ToDictionary(
                    kv => kv.Key,
                    kv => (object?)new Dictionary<string, object?>
                    {
                        ["rk"] = kv.Value.LastRowKey,
                        ["x"]  = kv.Value.Exhausted,
                    }),
            };
            var json = JsonSerializer.Serialize(doc);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public static Dictionary<string, PartitionContinuation> DecodeMultiContinuation(string? raw)
        {
            var result = new Dictionary<string, PartitionContinuation>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(raw)) return result;

            try
            {
                var bytes = Convert.FromBase64String(raw);
                using var doc = JsonDocument.Parse(bytes);
                if (!doc.RootElement.TryGetProperty("c", out var entries)) return result;
                foreach (var prop in entries.EnumerateObject())
                {
                    string? rk = prop.Value.TryGetProperty("rk", out var rkEl) && rkEl.ValueKind == JsonValueKind.String
                        ? rkEl.GetString()
                        : null;
                    bool exhausted = prop.Value.TryGetProperty("x", out var xEl) && xEl.ValueKind == JsonValueKind.True;
                    result[prop.Name] = new PartitionContinuation(rk, exhausted);
                }
            }
            catch
            {
                // Malformed continuation → treat as empty (start over). The wire-
                // layer fingerprint already rejects tampered tokens before reaching here.
            }
            return result;
        }
    }
}
