using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Regression guard for the CveIndex query filter behind
/// <c>GET /api/global/search/sessions-by-cve</c>.
///
/// The CveIndex PartitionKey is <c>{tenantId}_{cveId}</c>. The original
/// cross-tenant branch expressed a PartitionKey range on the cveId
/// (<c>PartitionKey ge '{cve}' and PartitionKey lt '{cve}~'</c>) — but no row's
/// PK begins with the cveId (they all begin with the tenant GUID), so the GA
/// cross-tenant search silently returned 0 results while
/// <c>get_vulnerability_summary</c> (a full CveId-property scan) reported the
/// real, non-zero exposure. These tests pin the corrected filter shape.
/// </summary>
public class SearchSessionsByCveFilterTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string CveA = "CVE-2024-21447";

    [Fact]
    public void CrossTenant_filters_on_CveId_property_not_a_PartitionKey_range()
    {
        var filter = TableStorageService.BuildCveIndexSearchFilter(tenantId: null, cveId: CveA);

        // Must match the CveId column so it sweeps every tenant's partitions.
        Assert.Equal($"CveId eq '{CveA}'", filter);
        // The old, broken form anchored a range on the PK (which starts with the
        // tenant GUID, not the cveId) — must never come back.
        Assert.DoesNotContain("PartitionKey ge", filter);
        Assert.DoesNotContain("PartitionKey lt", filter);
    }

    [Fact]
    public void CrossTenant_treats_empty_tenantId_same_as_null()
    {
        Assert.Equal(
            TableStorageService.BuildCveIndexSearchFilter(tenantId: null, cveId: CveA),
            TableStorageService.BuildCveIndexSearchFilter(tenantId: "", cveId: CveA));
    }

    [Fact]
    public void TenantScoped_uses_exact_partition_key_match()
    {
        var filter = TableStorageService.BuildCveIndexSearchFilter(TenantA, CveA);
        Assert.Equal($"PartitionKey eq '{TenantA}_{CveA}'", filter);
    }

    [Fact]
    public void Escapes_single_quotes_in_cveId_to_neutralize_injection()
    {
        var filter = TableStorageService.BuildCveIndexSearchFilter(
            tenantId: null, cveId: "CVE-2024' or CveId ne '");
        // Quotes are doubled — the payload stays inside the quoted literal.
        Assert.Equal("CveId eq 'CVE-2024'' or CveId ne '''", filter);
    }

    [Fact]
    public void Escapes_single_quotes_in_tenantId()
    {
        var filter = TableStorageService.BuildCveIndexSearchFilter(
            tenantId: "t' or PartitionKey ne '", cveId: CveA);
        Assert.StartsWith("PartitionKey eq 't'' or PartitionKey ne ''_", filter);
    }
}
