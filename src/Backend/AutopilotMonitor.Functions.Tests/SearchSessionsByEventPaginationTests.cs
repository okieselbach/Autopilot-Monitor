using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pagination wiring on <c>GET /api/search/sessions-by-event</c> +
/// <c>GET /api/global/search/sessions-by-event</c> — closes the gap left after
/// PR-6 where the repo's paged variant existed but the endpoint kept its
/// legacy <c>?limit=</c> max-100 contract.
/// </summary>
public class SearchSessionsByEventPaginationTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string TenantB = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string FilterA = "00000000-1111-2222-3333-444444444444";

    // ────────── ParsePagination ─────────────────────────────────────────────

    [Fact]
    public void ParsePagination_with_no_params_returns_default_pageSize()
    {
        var parsed = SearchSessionsByEventPagination.ParsePagination(new NameValueCollection());

        Assert.Null(parsed.Error);
        Assert.Equal(SearchSessionsByEventPagination.DefaultPageSize, parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Fact]
    public void ParsePagination_honours_pageSize_and_continuation()
    {
        var parsed = SearchSessionsByEventPagination.ParsePagination(new NameValueCollection
        {
            { "pageSize", "200" },
            { "continuation", "ENCODED" },
        });

        Assert.Equal(200, parsed.PageSize);
        Assert.Equal("ENCODED", parsed.Continuation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1001")]
    [InlineData("banana")]
    public void ParsePagination_rejects_invalid_pageSize(string raw)
    {
        var parsed = SearchSessionsByEventPagination.ParsePagination(new NameValueCollection { { "pageSize", raw } });
        Assert.NotNull(parsed.Error);
    }

    // ────────── Token round-trip + cross-binding rejections ─────────────────

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_eventType()
    {
        var fp = SearchSessionsByEventPagination.Fingerprint(
            "search-by-event:tenant", TenantA, filterTenantId: null, eventType: "app_install_failed");
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsByEventPagination.TryAcceptContinuation(
            encoded, "search-by-event:tenant", TenantA, filterTenantId: null,
            eventType: "app_install_failed", out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_for_different_caller()
    {
        var fp = SearchSessionsByEventPagination.Fingerprint(
            "search-by-event:tenant", TenantA, null, "app_install_failed");
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsByEventPagination.TryAcceptContinuation(
            encoded, "search-by-event:tenant", TenantB, null, "app_install_failed",
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_eventType_changes()
    {
        var fp = SearchSessionsByEventPagination.Fingerprint(
            "search-by-event:tenant", TenantA, null, "app_install_failed");
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsByEventPagination.TryAcceptContinuation(
            encoded, "search-by-event:tenant", TenantA, null, "error_detected",
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_scope_changes()
    {
        // Tenant-scoped token replayed into the global endpoint — tokens must
        // be non-fungible across the two endpoints even with the same caller +
        // eventType (the underlying queries differ).
        var fpTenant = SearchSessionsByEventPagination.Fingerprint(
            "search-by-event:tenant", TenantA, null, "app_install_failed");
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpTenant);

        var ok = SearchSessionsByEventPagination.TryAcceptContinuation(
            encoded, "search-by-event:global", TenantA, null, "app_install_failed",
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_global_filterTenantId_changes()
    {
        var fpA = SearchSessionsByEventPagination.Fingerprint(
            "search-by-event:global", TenantA, FilterA, "app_install_failed");
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpA);

        var ok = SearchSessionsByEventPagination.TryAcceptContinuation(
            encoded, "search-by-event:global", TenantA,
            filterTenantId: "99999999-9999-9999-9999-999999999999",
            eventType: "app_install_failed",
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_across_search_by_event_and_raw_events_scopes()
    {
        // A token from the cousin endpoint /api/raw/events (raw-events:tenant)
        // must not be accepted here, even with identical eventType + caller.
        var fpRaw = QueryRawEventsPagination.Fingerprint(
            scope: "raw-events:tenant", callerTenantId: TenantA,
            filterTenantId: null, sessionId: null,
            eventType: "app_install_failed", source: null, severity: null,
            startedAfter: null, startedBefore: null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpRaw);

        var ok = SearchSessionsByEventPagination.TryAcceptContinuation(
            encoded, "search-by-event:tenant", TenantA, null, "app_install_failed",
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    // ────────── BuildNextLink ───────────────────────────────────────────────

    [Fact]
    public void BuildNextLink_echoes_filter_params_and_drops_legacy_keys()
    {
        var query = new NameValueCollection
        {
            { "eventType", "app_install_failed" },
            { "tenantId", FilterA },
            { "limit", "100" },           // legacy → must be dropped
            { "pageSize", "999" },        // overwritten by the new contract
            { "continuation", "STALE" },  // overwritten by the new contract
        };

        var link = SearchSessionsByEventPagination.BuildNextLink(
            "/api/global/search/sessions-by-event", 50, "FRESH+/=", query);

        Assert.StartsWith("/api/global/search/sessions-by-event?", link);
        Assert.Contains("pageSize=50", link);
        Assert.Contains("continuation=FRESH%2B%2F%3D", link);
        Assert.Contains("eventType=app_install_failed", link);
        Assert.Contains($"tenantId={FilterA}", link);
        Assert.DoesNotContain("limit=", link);
        Assert.DoesNotContain("STALE", link);
    }
}
