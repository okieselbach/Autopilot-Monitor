using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PR 3 (mcp-pagination-rollout) — date-windowed forensics endpoints
/// (audit logs + ops events). Verifies the shared
/// <see cref="DateWindowPagination"/> helper and its cross-tenant /
/// cross-window guarantees.
/// </summary>
public class DateWindowPaginationTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string TenantB = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // ────────── ParseQuery ──────────────────────────────────────────────────

    [Fact]
    public void ParseQuery_with_no_window_defaults_to_30d_ending_at_now()
    {
        var anchor = new DateTimeOffset(2026, 5, 5, 14, 0, 0, TimeSpan.Zero);
        var parsed = DateWindowPagination.ParseQuery(new NameValueCollection(), anchor);

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.DateFrom);
        Assert.NotNull(parsed.DateTo);
        Assert.Equal(anchor.UtcDateTime, parsed.DateTo);
        Assert.Equal(anchor.UtcDateTime - TimeSpan.FromDays(30), parsed.DateFrom);
    }

    [Fact]
    public void ParseQuery_honours_explicit_dateFrom_and_dateTo_exactly()
    {
        var qs = new NameValueCollection
        {
            { "dateFrom", "2026-04-01T00:00:00Z" },
            { "dateTo",   "2026-05-01T00:00:00Z" },
        };
        var parsed = DateWindowPagination.ParseQuery(qs);

        Assert.Null(parsed.Error);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), parsed.DateFrom);
        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), parsed.DateTo);
    }

    [Fact]
    public void ParseQuery_with_only_dateFrom_leaves_dateTo_open()
    {
        var qs = new NameValueCollection { { "dateFrom", "2026-04-01T00:00:00Z" } };
        var parsed = DateWindowPagination.ParseQuery(qs);

        Assert.Null(parsed.Error);
        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), parsed.DateFrom);
        Assert.Null(parsed.DateTo);
    }

    [Fact]
    public void ParseQuery_rejects_dateFrom_after_dateTo()
    {
        var qs = new NameValueCollection
        {
            { "dateFrom", "2026-06-01T00:00:00Z" },
            { "dateTo",   "2026-04-01T00:00:00Z" },
        };
        var parsed = DateWindowPagination.ParseQuery(qs);

        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void ParseQuery_rejects_garbage_dateFrom()
    {
        var qs = new NameValueCollection { { "dateFrom", "not-a-date" } };
        var parsed = DateWindowPagination.ParseQuery(qs);
        Assert.NotNull(parsed.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("banana")]
    public void ParseQuery_rejects_invalid_pageSize(string raw)
    {
        var qs = new NameValueCollection { { "pageSize", raw } };
        var parsed = DateWindowPagination.ParseQuery(qs);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void ParseQuery_drops_continuation_when_pageSize_absent()
    {
        var qs = new NameValueCollection { { "continuation", "STALE" } };
        var parsed = DateWindowPagination.ParseQuery(qs);

        Assert.Null(parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Fact]
    public void ParseQuery_with_pageSize_activates_pagination()
    {
        var qs = new NameValueCollection
        {
            { "pageSize", "50" },
            { "continuation", "ENCODED" },
        };
        var parsed = DateWindowPagination.ParseQuery(qs);

        Assert.Equal(50, parsed.PageSize);
        Assert.Equal("ENCODED", parsed.Continuation);
    }

    // ────────── Fingerprint + token round trip ──────────────────────────────

    [Fact]
    public void TryAcceptContinuation_round_trips_when_window_and_tenant_match()
    {
        var dateFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var fp = DateWindowPagination.Fingerprint("audit:tenant", TenantA, dateFrom, dateTo);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = DateWindowPagination.TryAcceptContinuation(
            encoded, "audit:tenant", TenantA, dateFrom, dateTo, extras: null,
            out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_for_a_different_tenant()
    {
        var dateFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var fp = DateWindowPagination.Fingerprint("audit:tenant", TenantA, dateFrom, dateTo);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = DateWindowPagination.TryAcceptContinuation(
            encoded, "audit:tenant", TenantB, dateFrom, dateTo, extras: null,
            out var _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_window_changes()
    {
        var fpA = DateWindowPagination.Fingerprint(
            "audit:tenant", TenantA,
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpA);

        var ok = DateWindowPagination.TryAcceptContinuation(
            encoded, "audit:tenant", TenantA,
            // Caller now sends a different window — token must be rejected,
            // otherwise the underlying Pk/Rk cursor would seek into rows that
            // no longer match the active filter.
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc),
            extras: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_scope_changes()
    {
        // A token issued for the tenant-scoped audit endpoint must not be
        // accepted by the global-scoped endpoint (or vice versa) — the scope
        // is part of the fingerprint precisely so the two are non-fungible.
        var dateFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var fpTenant = DateWindowPagination.Fingerprint("audit:tenant", TenantA, dateFrom, dateTo);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpTenant);

        var ok = DateWindowPagination.TryAcceptContinuation(
            encoded, "audit:global", TenantA, dateFrom, dateTo, extras: null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_for_ops_events_includes_category_in_fingerprint()
    {
        var dateFrom = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var dateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var withConsent = new[] { new KeyValuePair<string, string?>("category", "Consent") };
        var fp = DateWindowPagination.Fingerprint("ops-events", TenantA, dateFrom, dateTo, withConsent);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        // Same scope/tenant/window but different category → must reject.
        var withSecurity = new[] { new KeyValuePair<string, string?>("category", "Security") };
        var ok = DateWindowPagination.TryAcceptContinuation(
            encoded, "ops-events", TenantA, dateFrom, dateTo, withSecurity,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    // ────────── BuildNextLink ───────────────────────────────────────────────

    [Fact]
    public void BuildNextLink_includes_pageSize_continuation_dateFrom_dateTo_and_extras()
    {
        var dateFrom = new DateTime(2026, 4, 1, 12, 30, 0, DateTimeKind.Utc);
        var dateTo = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var extras = new[] { new KeyValuePair<string, string?>("category", "Consent") };

        var link = DateWindowPagination.BuildNextLink(
            "/api/global/ops-events", 50, "TOKEN+/=", dateFrom, dateTo, extras);

        Assert.StartsWith("/api/global/ops-events?", link);
        Assert.Contains("pageSize=50", link);
        Assert.Contains("continuation=TOKEN%2B%2F%3D", link);
        Assert.Contains("dateFrom=2026-04-01T12%3A30%3A00", link);
        Assert.Contains("dateTo=2026-05-01T00%3A00%3A00", link);
        Assert.Contains("category=Consent", link);
    }

    [Fact]
    public void BuildNextLink_omits_optional_bounds_and_empty_extras()
    {
        var link = DateWindowPagination.BuildNextLink(
            "/api/audit/logs", 50, "abc", dateFrom: null, dateTo: null,
            extras: new[] { new KeyValuePair<string, string?>("category", null) });

        Assert.StartsWith("/api/audit/logs?", link);
        Assert.Contains("pageSize=50", link);
        Assert.Contains("continuation=abc", link);
        Assert.DoesNotContain("dateFrom=", link);
        Assert.DoesNotContain("dateTo=", link);
        Assert.DoesNotContain("category=", link);
    }
}
