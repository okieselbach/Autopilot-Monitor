using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PR 4 (mcp-pagination-rollout) — pagination wiring on
/// <c>GET /api/global/session-reports</c>. The endpoint stays GA-only at the
/// policy layer; <c>tenantId</c> here is a server-side <em>filter</em>, not an
/// authorization scope. The continuation token still binds to the caller's
/// identity + the active filter so a token issued for one filter view can't be
/// silently retargeted to another tenant's reports.
/// </summary>
public class SessionReportsPaginationTests
{
    private const string CallerTenant = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string CallerTenantOther = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string FilterTenantA = "00000000-1111-2222-3333-444444444444";
    private const string FilterTenantB = "55555555-6666-7777-8888-999999999999";

    // ────────── ParseQuery ──────────────────────────────────────────────────

    [Fact]
    public void ParseQuery_with_no_params_returns_unfiltered_unpaginated()
    {
        var parsed = SessionReportsPagination.ParseQuery(new NameValueCollection());

        Assert.Null(parsed.Error);
        Assert.Null(parsed.FilterTenantId);
        Assert.Null(parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Fact]
    public void ParseQuery_keeps_tenantId_filter_independent_of_pagination()
    {
        var parsed = SessionReportsPagination.ParseQuery(new NameValueCollection
        {
            { "tenantId", FilterTenantA },
        });

        Assert.Null(parsed.Error);
        Assert.Equal(FilterTenantA, parsed.FilterTenantId);
        Assert.Null(parsed.PageSize);
    }

    [Fact]
    public void ParseQuery_with_pageSize_activates_pagination()
    {
        var parsed = SessionReportsPagination.ParseQuery(new NameValueCollection
        {
            { "pageSize", "50" },
            { "continuation", "ENCODED" },
            { "tenantId", FilterTenantA },
        });

        Assert.Equal(50, parsed.PageSize);
        Assert.Equal("ENCODED", parsed.Continuation);
        Assert.Equal(FilterTenantA, parsed.FilterTenantId);
    }

    [Fact]
    public void ParseQuery_drops_continuation_when_pageSize_absent()
    {
        var parsed = SessionReportsPagination.ParseQuery(new NameValueCollection
        {
            { "continuation", "STALE" },
        });

        Assert.Null(parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1001")]
    [InlineData("banana")]
    public void ParseQuery_rejects_invalid_pageSize(string raw)
    {
        var parsed = SessionReportsPagination.ParseQuery(new NameValueCollection { { "pageSize", raw } });
        Assert.NotNull(parsed.Error);
    }

    // ────────── Token round-trip + cross-binding rejections ─────────────────

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_caller_and_filter()
    {
        var fp = SessionReportsPagination.Fingerprint(CallerTenant, FilterTenantA);
        var encoded = ContinuationToken.Encode("rawAzure", CallerTenant, fp);

        var ok = SessionReportsPagination.TryAcceptContinuation(
            encoded, CallerTenant, FilterTenantA, out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_caller_when_filter_absent()
    {
        // GA list-all-tenants view — filterTenantId null both times → fingerprint matches.
        var fp = SessionReportsPagination.Fingerprint(CallerTenant, filterTenantId: null);
        var encoded = ContinuationToken.Encode("rawAzure", CallerTenant, fp);

        var ok = SessionReportsPagination.TryAcceptContinuation(
            encoded, CallerTenant, filterTenantId: null, out var azure, out _);

        Assert.True(ok);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_replayed_by_a_different_caller()
    {
        // Anti-replay: a token issued for caller A can't be reused by caller B,
        // even though both could query session-reports — protects deep paginated
        // bookmark links from being replayed by a third party.
        var fp = SessionReportsPagination.Fingerprint(CallerTenant, FilterTenantA);
        var encoded = ContinuationToken.Encode("rawAzure", CallerTenant, fp);

        var ok = SessionReportsPagination.TryAcceptContinuation(
            encoded, CallerTenantOther, FilterTenantA, out _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_filter_changes_between_calls()
    {
        // Caller paginates with tenantId=A then rewrites to tenantId=B — token
        // must be rejected so the cursor can't bleed across filters.
        var fpA = SessionReportsPagination.Fingerprint(CallerTenant, FilterTenantA);
        var encoded = ContinuationToken.Encode("rawAzure", CallerTenant, fpA);

        var ok = SessionReportsPagination.TryAcceptContinuation(
            encoded, CallerTenant, FilterTenantB, out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_filter_drops_to_null()
    {
        // Caller had tenantId=A applied then cleared the filter — different
        // filter scope, token must restart.
        var fpA = SessionReportsPagination.Fingerprint(CallerTenant, FilterTenantA);
        var encoded = ContinuationToken.Encode("rawAzure", CallerTenant, fpA);

        var ok = SessionReportsPagination.TryAcceptContinuation(
            encoded, CallerTenant, filterTenantId: null, out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    // ────────── BuildNextLink ───────────────────────────────────────────────

    [Fact]
    public void BuildNextLink_includes_pageSize_continuation_and_tenantId_when_filtering()
    {
        var link = SessionReportsPagination.BuildNextLink(50, "TOKEN+/=", FilterTenantA);

        Assert.StartsWith("/api/global/session-reports?", link);
        Assert.Contains("pageSize=50", link);
        Assert.Contains("continuation=TOKEN%2B%2F%3D", link);
        Assert.Contains($"tenantId={FilterTenantA}", link);
    }

    [Fact]
    public void BuildNextLink_omits_tenantId_when_no_filter_active()
    {
        var link = SessionReportsPagination.BuildNextLink(50, "abc", filterTenantId: null);

        Assert.StartsWith("/api/global/session-reports?", link);
        Assert.Contains("pageSize=50", link);
        Assert.Contains("continuation=abc", link);
        Assert.DoesNotContain("tenantId=", link);
    }
}
