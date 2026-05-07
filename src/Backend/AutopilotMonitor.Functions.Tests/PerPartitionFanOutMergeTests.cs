using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.DataAccess;
using static AutopilotMonitor.Functions.DataAccess.TableStorage.PerPartitionFanOutMerge;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Cross-partition merge used by both the global all-category ops-events page
/// and the global cross-tenant audit-logs page. Without this fan-out, Azure
/// pages cross-partition queries by (PK asc, RK asc) — so page 1 would be
/// entirely the alphabetically-first partition, masking newer items from
/// later-alphabet partitions. The merge picks top-pageSize by timestamp across
/// all per-partition fetches.
/// </summary>
public class PerPartitionFanOutMergeTests
{
    private static OpsEventEntry Entry(string partition, DateTime ts, string id)
        => new() { Category = partition, Id = id, Timestamp = ts, EventType = "synthetic" };

    private static (string RowKey, OpsEventEntry Item) Row(DateTime ts, string id)
    {
        // Mimics the storage-layer revtick rowkey: smaller string = newer timestamp.
        var rk = $"{(DateTime.MaxValue.Ticks - ts.Ticks):D19}_{id}";
        return (rk, Entry(partition: "<set-by-caller>", ts, id));
    }

    private static PartitionFetchResult<OpsEventEntry> Fetched(
        string partition, params (string RowKey, OpsEventEntry Item)[] rows)
    {
        var aligned = rows
            .Select(r => (r.RowKey, Item: new OpsEventEntry
            {
                Id = r.Item.Id,
                Category = partition,
                EventType = r.Item.EventType,
                Timestamp = r.Item.Timestamp,
            }))
            .ToArray();
        return new PartitionFetchResult<OpsEventEntry>(partition, aligned);
    }

    // ────────── Merge picks newest-across-partitions, not within-partition ──

    [Fact]
    public void First_page_returns_newest_across_partitions_not_alphabetically_first()
    {
        // Without the merge, Azure's (PK asc, RK asc) ordering would surface
        // "Agent" partition items first (alphabetically), even though "Security"
        // has a newer event. The merge must pick by timestamp, not partition.
        var t0 = DateTime.UtcNow;
        var fetched = new[]
        {
            Fetched("Agent",    Row(t0.AddMinutes(-5), "agent-1")),
            Fetched("Security", Row(t0,                "sec-1")),
            Fetched("Tenant",   Row(t0.AddMinutes(-10), "tenant-1")),
        };

        var (items, _) = MergeAndAdvance(
            fetched,
            new Dictionary<string, PartitionContinuation>(),
            pageSize: 3,
            timestampSelector: e => e.Timestamp);

        Assert.Equal(3, items.Count);
        Assert.Equal("sec-1",    items[0].Id); // newest first
        Assert.Equal("agent-1",  items[1].Id);
        Assert.Equal("tenant-1", items[2].Id);
    }

    [Fact]
    public void Empty_fetch_drops_partition_from_continuation_map()
    {
        // Wire-format v2 invariant: exhausted partitions are absent from the
        // continuation, not encoded with `x:true`. Consumers treat "missing"
        // as exhausted on subsequent pages — that's what shrinks the audit
        // fan-out token from 30+ KB to a few hundred bytes.
        var t0 = DateTime.UtcNow;
        var fetched = new[]
        {
            Fetched("Agent"),                                 // empty fetch
            Fetched("Security", Row(t0, "sec-1")),
        };

        var (_, conts) = MergeAndAdvance(
            fetched,
            new Dictionary<string, PartitionContinuation>(),
            pageSize: 5,
            timestampSelector: e => e.Timestamp);

        Assert.False(conts.ContainsKey("Agent"));
        Assert.True(conts.ContainsKey("Security"));
    }

    // ────────── Continuation advance: avoid losing leftovers when a partition loses the cut ──

    [Fact]
    public void Continuation_keeps_position_for_partitions_whose_items_lost_the_merge_cut()
    {
        // Page 1: pageSize=3. Agent fetched 5 items, Security fetched 5 items, but
        // Security's are all newer. Merge takes 3 from Security; Agent's 5 items
        // all lost the cut. Agent's continuation MUST stay at its previous
        // position so the items get re-fetched and compete on page 2.
        var t0 = DateTime.UtcNow;
        var fetched = new[]
        {
            Fetched("Agent",
                Row(t0.AddMinutes(-100), "a1"),
                Row(t0.AddMinutes(-101), "a2"),
                Row(t0.AddMinutes(-102), "a3"),
                Row(t0.AddMinutes(-103), "a4"),
                Row(t0.AddMinutes(-104), "a5")),
            Fetched("Security",
                Row(t0,                  "s1"),
                Row(t0.AddMinutes(-1),   "s2"),
                Row(t0.AddMinutes(-2),   "s3"),
                Row(t0.AddMinutes(-3),   "s4"),
                Row(t0.AddMinutes(-4),   "s5")),
        };

        var (items, conts) = MergeAndAdvance(
            fetched,
            new Dictionary<string, PartitionContinuation>(),
            pageSize: 3,
            timestampSelector: e => e.Timestamp);

        // Security wins all three slots.
        Assert.Equal(new[] { "s1", "s2", "s3" }, items.Select(i => i.Id).ToArray());

        // Agent had no items returned and a non-empty fetch → keep prior continuation
        // (null = restart). The partition stays in the map (active with no rk),
        // so the next page will re-fetch its items and they'll be re-considered
        // at a lower floor.
        Assert.True(conts.ContainsKey("Agent"));
        Assert.Null(conts["Agent"].LastRowKey);

        Assert.True(conts.ContainsKey("Security"));
        Assert.NotNull(conts["Security"].LastRowKey);
    }

    [Fact]
    public void Continuation_advances_to_oldest_returned_rowkey_per_partition()
    {
        // When merge takes some-but-not-all from a partition, advance only past the
        // items we actually returned. The leftover (older) items in that fetch
        // will be re-fetched and re-considered on the next page.
        var t0 = DateTime.UtcNow;
        var (rkNew, _) = Row(t0,                "x-new");
        var (rkMid, _) = Row(t0.AddMinutes(-5), "x-mid");
        var (rkOld, _) = Row(t0.AddMinutes(-10), "x-old");
        var fetched = new[]
        {
            Fetched("Agent",
                (rkNew, Entry("Agent", t0,                "x-new")),
                (rkMid, Entry("Agent", t0.AddMinutes(-5), "x-mid")),
                (rkOld, Entry("Agent", t0.AddMinutes(-10), "x-old"))),
            // A second partition whose only event is between Agent's mid and old
            // items, ensuring the merge ends after Agent's mid item.
            Fetched("Security",
                Row(t0.AddMinutes(-7), "s-only")),
        };

        var (items, conts) = MergeAndAdvance(
            fetched,
            new Dictionary<string, PartitionContinuation>(),
            pageSize: 3,
            timestampSelector: e => e.Timestamp);

        // Top 3 by timestamp desc: x-new, x-mid, s-only. x-old is dropped.
        Assert.Equal(new[] { "x-new", "x-mid", "s-only" }, items.Select(i => i.Id).ToArray());

        // Agent's continuation advances to rkMid (the oldest Agent rowkey we returned).
        // x-old will be re-fetched and re-considered next page (filter: RowKey gt rkMid).
        Assert.Equal(rkMid, conts["Agent"].LastRowKey);
    }

    // ────────── Codec round-trip ────────────────────────────────────────────

    [Fact]
    public void Encode_then_decode_round_trips_active_partitions()
    {
        var original = new Dictionary<string, PartitionContinuation>
        {
            ["Agent"]   = new PartitionContinuation("0123456789012345678_aaa"),
            ["Consent"] = new PartitionContinuation("0987654321098765432_bbb"),
            ["Tenant"]  = new PartitionContinuation(null), // active, restart from top
        };

        var encoded = EncodeMultiContinuation(original);
        var decoded = DecodeMultiContinuation(encoded);

        Assert.Equal(3, decoded.Count);
        Assert.Equal("0123456789012345678_aaa", decoded["Agent"].LastRowKey);
        Assert.Equal("0987654321098765432_bbb", decoded["Consent"].LastRowKey);
        Assert.Null(decoded["Tenant"].LastRowKey);
    }

    [Fact]
    public void Decode_silently_drops_legacy_v1_exhausted_entries()
    {
        // Wire format v1 (pre-fix) explicitly encoded `x:true` for exhausted
        // partitions; v2 just omits them. Tokens that were already in flight
        // when the deploy went out must continue to page through cleanly —
        // those legacy `x:true` entries are dropped silently so the
        // consumer's "missing = exhausted" rule applies.
        var legacyJson = """{"v":1,"c":{"Agent":{"rk":"0123456789012345678_aaa","x":false},"Security":{"rk":null,"x":true},"Consent":{"rk":"0987654321098765432_bbb","x":false}}}""";
        var legacyToken = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(legacyJson));

        var decoded = DecodeMultiContinuation(legacyToken);

        Assert.Equal(2, decoded.Count);
        Assert.True(decoded.ContainsKey("Agent"));
        Assert.True(decoded.ContainsKey("Consent"));
        Assert.False(decoded.ContainsKey("Security")); // dropped: was x:true in v1 wire
    }

    [Fact]
    public void Encode_omits_partition_state_field_to_keep_token_compact()
    {
        // Sanity-check the wire compactness gain: 200 partitions of v1 with
        // `x:false` per entry produced ~30 KB after base64; v2 should be
        // dramatically smaller because we drop the redundant flag and only
        // encode active entries.
        var many = Enumerable.Range(0, 200)
            .ToDictionary(i => $"tenant-{i:D3}", i => new PartitionContinuation($"!1234567890123456789_{i:D8}"));

        var encoded = EncodeMultiContinuation(many);

        // 200 entries × (~30 chars name + ~30 chars rk + JSON overhead) → JSON
        // ~12 KB → base64 ~16 KB. The legacy `x:false` flag adds another
        // ~10 bytes per entry (~2 KB total) which we now save. The hard
        // assertion below is on absolute size: we must stay well below the
        // ~30 KB that broke audit Next clicks.
        Assert.True(encoded.Length < 20_000,
            $"Encoded continuation should stay under 20 KB even at 200 partitions; got {encoded.Length} bytes");
    }

    [Fact]
    public void Decode_returns_empty_for_malformed_or_empty_continuation()
    {
        Assert.Empty(DecodeMultiContinuation(null));
        Assert.Empty(DecodeMultiContinuation(""));
        Assert.Empty(DecodeMultiContinuation("not-base64-!@#"));
        Assert.Empty(DecodeMultiContinuation("dGhpcyBpcyBub3QganNvbg==")); // valid base64, not JSON
    }

    // ────────── End-to-end: drain across pages preserves all items, in order ──

    [Fact]
    public void Multi_page_drain_returns_all_items_globally_newest_first_no_duplicates()
    {
        var t0 = new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc);

        var allItems = new List<(string Part, OpsEventEntry Item, string Rk)>();
        var partitions = new[] { "Agent", "Security", "Tenant" };
        var minute = 0;
        foreach (var part in partitions)
        {
            for (int i = 0; i < 4; i++)
            {
                minute++;
                var ts = t0.AddMinutes(-minute);
                var (rk, _) = Row(ts, $"{part}-{i}");
                allItems.Add((part, new OpsEventEntry
                {
                    Category = part,
                    Id = $"{part}-{i}",
                    Timestamp = ts,
                    EventType = "synthetic",
                }, rk));
            }
        }

        IReadOnlyList<PartitionFetchResult<OpsEventEntry>> SimulateFetch(
            Dictionary<string, PartitionContinuation> continuations, int pageSize, bool isFirstPage)
        {
            var fetched = new List<PartitionFetchResult<OpsEventEntry>>();
            foreach (var part in partitions)
            {
                // Mirrors the consumer rule: on first page query everything; on
                // subsequent pages only partitions still in the continuation.
                if (!isFirstPage && !continuations.ContainsKey(part)) continue;
                continuations.TryGetValue(part, out var prior);
                var lastRk = prior?.LastRowKey;

                var rows = allItems
                    .Where(t => t.Part == part)
                    .Where(t => lastRk == null || string.Compare(t.Rk, lastRk, StringComparison.Ordinal) > 0)
                    .OrderBy(t => t.Rk, StringComparer.Ordinal)
                    .Take(pageSize)
                    .Select(t => (t.Rk, t.Item))
                    .ToList();
                fetched.Add(new PartitionFetchResult<OpsEventEntry>(part, rows));
            }
            return fetched;
        }

        const int pageSize = 5;
        var seen = new List<OpsEventEntry>();
        var conts = new Dictionary<string, PartitionContinuation>();
        var loopGuard = 0;
        bool isFirstPage = true;

        while (true)
        {
            var fetched = SimulateFetch(conts, pageSize, isFirstPage);
            var (page, nextConts) = MergeAndAdvance(fetched, conts, pageSize, e => e.Timestamp);
            seen.AddRange(page);
            conts = nextConts;
            isFirstPage = false;

            // Map only carries active partitions now — empty == drain complete.
            if (conts.Count == 0) break;
            if (page.Count == 0) break;
            if (++loopGuard > 50) Assert.Fail("Drain loop did not terminate");
        }

        Assert.Equal(12, seen.Count);
        Assert.Equal(12, seen.Select(e => e.Id).Distinct().Count());
        for (int i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i - 1].Timestamp >= seen[i].Timestamp,
                $"Out-of-order at index {i}: {seen[i - 1].Id}({seen[i - 1].Timestamp}) before {seen[i].Id}({seen[i].Timestamp})");
        }
    }
}
