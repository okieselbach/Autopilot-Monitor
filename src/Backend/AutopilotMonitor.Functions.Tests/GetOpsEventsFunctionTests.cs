using System.Linq;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Shared.DataAccess;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the optional ?tenantId= DRILL on the global ops-events endpoint. ops-events is GA/Reader-only
/// (catalog TenantScoping.None — a delegated caller cannot reach it; see
/// PolicyEnforcementMiddlewareTests.Delegated_OpsEvents_WithManagedTenantId_IsForbidden), so this is DRILL
/// CORRECTNESS, not a security boundary: a GA/Reader that names ?tenantId= must get only that tenant's rows.
/// OpsEvents is partitioned by category (not tenant), so <see cref="GetOpsEventsFunction.FilterByTenant"/>
/// is the only thing that scopes the drill — these tests trip a red build if a refactor broadens the
/// predicate, drops the tenant-less exclusion, or breaks case folding.
/// </summary>
public class GetOpsEventsFunctionTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private static OpsEventEntry Event(string? tenantId, string id = "e") =>
        new() { Id = id, TenantId = tenantId, Category = OpsEventCategory.Security };

    [Fact]
    public void FilterByTenant_NullOrEmptyFilter_PassesEverything()
    {
        // GA/Reader cross-tenant view: no tenantId named → every tenant's row is visible (unbounded).
        var source = new[] { Event(TenantA), Event(TenantB), Event(tenantId: null) };

        Assert.Equal(3, GetOpsEventsFunction.FilterByTenant(source, null).Count());
        Assert.Equal(3, GetOpsEventsFunction.FilterByTenant(source, "").Count());
    }

    [Fact]
    public void FilterByTenant_ConcreteFilter_ReturnsOnlyThatTenant_ZeroForeign()
    {
        var source = new[]
        {
            Event(TenantA, "a1"), Event(TenantB, "b1"), Event(TenantA, "a2"),
            Event(tenantId: null, "n1"),
        };

        var filtered = GetOpsEventsFunction.FilterByTenant(source, TenantA).ToList();

        Assert.Equal(2, filtered.Count);
        // The drill must return ONLY the named tenant's rows — no foreign or tenant-less row survives.
        Assert.All(filtered, e => Assert.Equal(TenantA, e.TenantId));
        Assert.DoesNotContain(filtered, e => e.TenantId != TenantA);
    }

    [Fact]
    public void FilterByTenant_IsCaseInsensitive()
    {
        // AllowedTenantIds / route tenantIds are lowercased while stored TenantId casing is not guaranteed.
        var source = new[] { Event(TenantA.ToUpperInvariant()) };

        var filtered = GetOpsEventsFunction.FilterByTenant(source, TenantA.ToLowerInvariant()).ToList();

        Assert.Single(filtered);
    }
}
