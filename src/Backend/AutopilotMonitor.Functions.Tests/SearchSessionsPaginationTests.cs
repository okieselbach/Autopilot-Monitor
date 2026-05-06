using System.Collections.Generic;
using System.Collections.Specialized;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PR 5 (mcp-pagination-rollout) — pagination wiring on
/// <c>GET /api/search/sessions</c> + <c>GET /api/global/search/sessions</c>.
/// Replaces the legacy <c>?limit=</c> wire shape with the rollout plan's
/// <c>?pageSize=</c> + <c>?continuation=</c> + <c>nextLink</c> contract.
/// </summary>
public class SearchSessionsPaginationTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string TenantB = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static SessionSearchFilter SimpleFilter() => new() { Status = "Failed", Manufacturer = "Microsoft" };

    private static SessionSearchFilter HardwareFilter() => new()
    {
        Status = "Failed",
        DeviceProperties = new Dictionary<string, string>
        {
            ["tpm_status.specVersion"] = "2.0",
            ["hardware_spec.ramTotalGB"] = ">=8",
        },
    };

    // ────────── ParsePagination ─────────────────────────────────────────────

    [Fact]
    public void ParsePagination_with_no_params_returns_default_pageSize()
    {
        var parsed = SearchSessionsPagination.ParsePagination(new NameValueCollection());

        Assert.Null(parsed.Error);
        Assert.Equal(SearchSessionsPagination.DefaultPageSize, parsed.PageSize);
        Assert.Null(parsed.Continuation);
    }

    [Fact]
    public void ParsePagination_honours_pageSize_and_continuation()
    {
        var parsed = SearchSessionsPagination.ParsePagination(new NameValueCollection
        {
            { "pageSize", "200" },
            { "continuation", "ENCODED" },
        });

        Assert.Equal(200, parsed.PageSize);
        Assert.Equal("ENCODED", parsed.Continuation);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("banana")]
    public void ParsePagination_rejects_invalid_pageSize(string raw)
    {
        var parsed = SearchSessionsPagination.ParsePagination(
            new NameValueCollection { { "pageSize", raw } });
        Assert.NotNull(parsed.Error);
    }

    // ────────── Token round-trip + cross-binding rejections ─────────────────

    [Fact]
    public void TryAcceptContinuation_round_trips_for_matching_filter()
    {
        var filter = SimpleFilter();
        var fp = SearchSessionsPagination.Fingerprint("search:tenant", TenantA, filterTenantId: null, filter);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsPagination.TryAcceptContinuation(
            encoded, "search:tenant", TenantA, filterTenantId: null, filter,
            out var azure, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal("rawAzure", azure);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_for_different_caller()
    {
        var filter = SimpleFilter();
        var fp = SearchSessionsPagination.Fingerprint("search:tenant", TenantA, filterTenantId: null, filter);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var ok = SearchSessionsPagination.TryAcceptContinuation(
            encoded, "search:tenant", TenantB, filterTenantId: null, filter,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("cross_tenant", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_status_changes()
    {
        var filterFailed = new SessionSearchFilter { Status = "Failed" };
        var fp = SearchSessionsPagination.Fingerprint("search:tenant", TenantA, null, filterFailed);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var filterSucceeded = new SessionSearchFilter { Status = "Succeeded" };
        var ok = SearchSessionsPagination.TryAcceptContinuation(
            encoded, "search:tenant", TenantA, null, filterSucceeded,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_across_scan_and_snapshot_paths()
    {
        // Same other filters, but adding/removing deviceProperties switches the
        // backend code path (scan ↔ device-snapshot). The fingerprint encodes
        // "path" so the cursor can't be replayed across paths.
        var simple = SimpleFilter();
        var hardware = HardwareFilter();
        var fpScan = SearchSessionsPagination.Fingerprint("search:tenant", TenantA, null, simple);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpScan);

        var ok = SearchSessionsPagination.TryAcceptContinuation(
            encoded, "search:tenant", TenantA, null, hardware,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_device_property_changes()
    {
        var filterTpm20 = new SessionSearchFilter
        {
            DeviceProperties = new Dictionary<string, string> { ["tpm_status.specVersion"] = "2.0" },
        };
        var fp = SearchSessionsPagination.Fingerprint("search:tenant", TenantA, null, filterTpm20);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fp);

        var filterTpm12 = new SessionSearchFilter
        {
            DeviceProperties = new Dictionary<string, string> { ["tpm_status.specVersion"] = "1.2" },
        };
        var ok = SearchSessionsPagination.TryAcceptContinuation(
            encoded, "search:tenant", TenantA, null, filterTpm12,
            out _, out var reason);

        Assert.False(ok);
        Assert.Equal("filter_mismatch", reason);
    }

    [Fact]
    public void TryAcceptContinuation_rejects_token_when_scope_changes()
    {
        var filter = SimpleFilter();
        var fpTenant = SearchSessionsPagination.Fingerprint("search:tenant", TenantA, null, filter);
        var encoded = ContinuationToken.Encode("rawAzure", TenantA, fpTenant);

        var ok = SearchSessionsPagination.TryAcceptContinuation(
            encoded, "search:global", TenantA, null, filter,
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
            { "status", "Failed" },
            { "manufacturer", "Microsoft" },
            { "limit", "100" },                  // legacy — must be dropped
            { "pageSize", "200" },               // owned by new contract — overwritten
            { "continuation", "STALE" },         // owned by new contract — overwritten
            { "prop.tpm_status.specVersion", "2.0" },
        };

        var link = SearchSessionsPagination.BuildNextLink(
            "/api/search/sessions", 50, "FRESH+/=", query);

        Assert.StartsWith("/api/search/sessions?", link);
        Assert.Contains("pageSize=50", link);
        Assert.Contains("continuation=FRESH%2B%2F%3D", link);
        Assert.Contains("status=Failed", link);
        Assert.Contains("manufacturer=Microsoft", link);
        Assert.Contains("prop.tpm_status.specVersion=2.0", link);
        Assert.DoesNotContain("limit=", link);
        Assert.DoesNotContain("STALE", link);
    }

    [Fact]
    public void Fingerprint_is_stable_regardless_of_deviceProperties_iteration_order()
    {
        var f1 = new SessionSearchFilter
        {
            DeviceProperties = new Dictionary<string, string>
            {
                ["a"] = "1",
                ["b"] = "2",
            },
        };
        var f2 = new SessionSearchFilter
        {
            DeviceProperties = new Dictionary<string, string>
            {
                ["b"] = "2",
                ["a"] = "1",
            },
        };
        Assert.Equal(
            SearchSessionsPagination.Fingerprint("search:tenant", TenantA, null, f1),
            SearchSessionsPagination.Fingerprint("search:tenant", TenantA, null, f2));
    }
}
