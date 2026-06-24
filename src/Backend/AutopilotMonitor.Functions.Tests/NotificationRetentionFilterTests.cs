using System;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Locks the hybrid notification retention predicate: dismissed rows prune at the short cutoff,
/// any row prunes at the long cutoff, and unread rows in between survive. These are pure-string
/// assertions on the OData filter (the row-delete loop around it is trivial mechanical code and the
/// repos talk to a live TableClient with no test double in this codebase).
/// </summary>
public class NotificationRetentionFilterTests
{
    private static readonly DateTime DismissedCutoff = new(2026, 5, 25, 12, 0, 0, DateTimeKind.Utc); // now-30d
    private static readonly DateTime UnreadCutoff = new(2025, 12, 26, 12, 0, 0, DateTimeKind.Utc);   // now-180d

    [Fact]
    public void Predicate_GatesDismissedClauseOnDismissalAge()
    {
        var predicate = NotificationRetentionFilter.BuildPredicate(DismissedCutoff, UnreadCutoff);

        // Dismissed rows are eligible 30 days after DISMISSAL (DismissedAt), not creation — so an old
        // notification dismissed today still gets its full 30-day tail before pruning.
        Assert.Contains("Dismissed eq true and DismissedAt lt datetime'2026-05-25T12:00:00Z'", predicate);
        Assert.DoesNotContain("Dismissed eq true and CreatedAt", predicate);
    }

    [Fact]
    public void Predicate_HasUngatedCatchAllAtLongCutoff()
    {
        var predicate = NotificationRetentionFilter.BuildPredicate(DismissedCutoff, UnreadCutoff);

        // The long-cutoff clause is the catch-all: it is OR'd in WITHOUT a Dismissed gate, so any
        // row (read or unread) older than the long window is pruned and the table stays bounded.
        Assert.Contains("or CreatedAt lt datetime'2025-12-26T12:00:00Z'", predicate);
        Assert.StartsWith("((", predicate);
        Assert.EndsWith(")", predicate);
    }

    [Fact]
    public void Predicate_KeepsTheTwoCutoffsDistinct()
    {
        var predicate = NotificationRetentionFilter.BuildPredicate(DismissedCutoff, UnreadCutoff);

        // Regression guard: an unread notification between the two cutoffs must NOT be matched. That
        // is guaranteed structurally by the dismissed clause carrying the short cutoff and the
        // catch-all carrying the long cutoff — never the same literal in both positions.
        Assert.Contains("2026-05-25T12:00:00Z", predicate); // short → dismissed clause
        Assert.Contains("2025-12-26T12:00:00Z", predicate); // long  → catch-all
        Assert.NotEqual(
            NotificationRetentionFilter.FormatCutoff(DismissedCutoff),
            NotificationRetentionFilter.FormatCutoff(UnreadCutoff));
    }

    [Fact]
    public void FormatCutoff_NormalizesToUtcSecondPrecision()
    {
        // Local-kind input must be coerced to UTC so the OData literal is unambiguous.
        var local = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc).ToLocalTime();
        var formatted = NotificationRetentionFilter.FormatCutoff(local);

        Assert.Equal("datetime'2026-01-02T03:04:05Z'", formatted);
    }
}
