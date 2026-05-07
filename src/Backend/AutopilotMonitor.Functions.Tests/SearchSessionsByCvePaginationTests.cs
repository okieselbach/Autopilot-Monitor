using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pagination wiring on <c>GET /api/search/sessions-by-cve</c> +
/// <c>GET /api/global/search/sessions-by-cve</c> — the legacy unpaged variant
/// silently capped at <c>limit*2</c> candidates, making "how many devices are
/// affected by CVE-X" un-answerable on large tenants. The new paged variant
/// drains the full set across calls.
/// </summary>
public class SearchSessionsByCvePaginationTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string TenantB = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string CveA = "CVE-2024-21447";
    private const string CveB = "CVE-2025-12345";

    [Fact]
    public void ParsePagination_with_no_params_returns_default_pageSize()
    {
        var parsed = SearchSessionsByCvePagination.ParsePagination(new NameValueCollection());

        Assert.Null(parsed.Error);
        Assert.Equal(SearchSessionsByCvePagination.DefaultPageSize, parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("1001")]
    [InlineData("banana")]
    public void ParsePagination_rejects_invalid_pageSize(string raw)
    {
        var parsed = SearchSessionsByCvePagination.ParsePagination(new NameValueCollection { { "pageSize", raw } });
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_caller_and_filters()
    {
        var fp = SearchSessionsByCvePagination.Fingerprint(
            "search-by-cve:tenant", TenantA, filterTenantId: null,
            cveId: CveA, minCvssScore: 7.0, overallRisk: "high");
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsByCvePagination.TryAcceptContinuation(
            encoded, "search-by-cve:tenant", TenantA, filterTenantId: null,
            cveId: CveA, minCvssScore: 7.0, overallRisk: "high",
            out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_cveId_changes()
    {
        // The whole point of the fingerprint binding cveId: a token issued for
        // CVE-A must never seek into CVE-B's index partition, even with the same
        // caller/filter combination.
        var fp = SearchSessionsByCvePagination.Fingerprint(
            "search-by-cve:tenant", TenantA, null, CveA, null, null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsByCvePagination.TryAcceptContinuation(
            encoded, "search-by-cve:tenant", TenantA, null, CveB, null, null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_minCvssScore_changes()
    {
        var fp = SearchSessionsByCvePagination.Fingerprint(
            "search-by-cve:tenant", TenantA, null, CveA, 7.0, null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsByCvePagination.TryAcceptContinuation(
            encoded, "search-by-cve:tenant", TenantA, null, CveA, 9.0, null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_for_a_different_caller()
    {
        var fp = SearchSessionsByCvePagination.Fingerprint(
            "search-by-cve:tenant", TenantA, null, CveA, null, null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsByCvePagination.TryAcceptContinuation(
            encoded, "search-by-cve:tenant", TenantB, null, CveA, null, null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_scope_changes_tenant_to_global()
    {
        // Tenant-scoped token replayed at the global endpoint — non-fungible
        // even with same caller + cveId, because the underlying CveIndex query
        // shape differs (PK exact-match vs PK range scan).
        var fpTenant = SearchSessionsByCvePagination.Fingerprint(
            "search-by-cve:tenant", TenantA, null, CveA, null, null);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpTenant);

        var ok = SearchSessionsByCvePagination.TryAcceptContinuation(
            encoded, "search-by-cve:global", TenantA, null, CveA, null, null,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void BuildNextLink_echoes_filter_params_and_drops_legacy_keys()
    {
        var query = new NameValueCollection
        {
            { "cveId", CveA },
            { "minCvssScore", "7.0" },
            { "overallRisk", "high" },
            { "limit", "100" },           // legacy → must be dropped
            { "pageSize", "999" },        // overwritten by the new contract
            { "continuation", "STALE" },  // overwritten by the new contract
        };

        var link = SearchSessionsByCvePagination.BuildNextLink(
            "/api/search/sessions-by-cve", 50, "FRESH+/=", query);

        Assert.StartsWith("/api/search/sessions-by-cve?", link);
        Assert.Contains("pageSize=50", link);
        Assert.Contains("continuation=FRESH%2B%2F%3D", link);
        Assert.Contains($"cveId={CveA}", link);
        Assert.Contains("minCvssScore=7.0", link);
        Assert.Contains("overallRisk=high", link);
        Assert.DoesNotContain("limit=", link);
        Assert.DoesNotContain("STALE", link);
    }
}
