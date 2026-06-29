using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="DelegatedAdminService"/> scope resolution — the "scoped global" (MSP) tier.
/// Locks in the fail-closed rules: only Active + enabled + recognized-role rows confer scope; empty role
/// defaults to the least-privileged DelegatedReader; unknown roles are dropped. Also covers the 5-minute
/// cache and its invalidation on mutation.
/// </summary>
public class DelegatedAdminServiceTests
{
    private const string Upn = "msp-admin@partner.example";
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private const string TemplateId1 = "tpl-aaaaaaaa";
    private const string TemplateId2 = "tpl-bbbbbbbb";
    private const string TenantC = "33333333-3333-3333-3333-333333333333";

    private static (DelegatedAdminService Svc, Mock<IAdminRepository> Repo) Build()
    {
        var repo = new Mock<IAdminRepository>();
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<DelegatedAdminEntry>());
        // Default: no template assignments. Template-specific tests override per-case.
        repo.Setup(r => r.GetTemplateAssignmentsForUpnAsync(It.IsAny<string>()))
            .ReturnsAsync(new List<TenantTemplateAssignment>());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var svc = new DelegatedAdminService(repo.Object, cache, NullLogger<DelegatedAdminService>.Instance);
        return (svc, repo);
    }

    private static TenantTemplateAssignment Assignment(
        string templateId,
        string role = Constants.DelegatedRoles.DelegatedReader,
        bool enabled = true) => new()
        {
            Upn = Upn,
            TemplateId = templateId,
            Role = role,
            IsEnabled = enabled,
            AssignedBy = "ga@vendor.example",
        };

    private static void ReturnsTemplates(Mock<IAdminRepository> repo, params TenantTemplateAssignment[] assignments) =>
        repo.Setup(r => r.GetTemplateAssignmentsForUpnAsync(It.IsAny<string>())).ReturnsAsync(assignments.ToList());

    private static void TemplateTenants(Mock<IAdminRepository> repo, string templateId, params string[] tenantIds) =>
        repo.Setup(r => r.GetTemplateTenantsAsync(templateId)).ReturnsAsync(tenantIds.ToList());

    private static DelegatedAdminEntry Row(
        string tenantId,
        string role = Constants.DelegatedRoles.DelegatedReader,
        bool enabled = true,
        string status = Constants.DelegatedStatus.Active) => new()
        {
            Upn = Upn,
            TenantId = tenantId,
            Role = role,
            IsEnabled = enabled,
            Status = status,
            Source = Constants.DelegatedSource.OperatorGranted,
        };

    private static void Returns(Mock<IAdminRepository> repo, params DelegatedAdminEntry[] rows) =>
        repo.Setup(r => r.GetDelegatedTenantsAsync(It.IsAny<string>())).ReturnsAsync(rows.ToList());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetScope_BlankUpn_ReturnsEmpty(string? upn)
    {
        var (svc, repo) = Build();
        var scope = await svc.GetScopeAsync(upn);
        Assert.True(scope.IsEmpty);
        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetScope_NoRows_ReturnsEmpty()
    {
        var (svc, _) = Build();
        var scope = await svc.GetScopeAsync(Upn);
        Assert.True(scope.IsEmpty);
        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_ActiveEnabledRow_IsCovered()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedReader));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.Covers(TenantA));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
        Assert.False(scope.CanWrite(TenantA));
        Assert.Single(scope.TenantIds);
    }

    [Fact]
    public async Task GetScope_DelegatedAdminRole_CanWrite()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedAdmin));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.CanWrite(TenantA));
    }

    [Theory]
    [InlineData(Constants.DelegatedStatus.PendingApproval)]
    [InlineData(Constants.DelegatedStatus.Revoked)]
    public async Task GetScope_NonActiveStatus_NotCovered(string status)
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, status: status));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.False(scope.Covers(TenantA));
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_DisabledRow_NotCovered()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, enabled: false));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_EmptyRole_DefaultsToReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, role: ""));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
    }

    [Fact]
    public async Task GetScope_UnknownRole_IsDropped()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, role: "SuperRoot"));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.False(scope.Covers(TenantA));
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_MultipleTenants_AllResolved()
    {
        var (svc, repo) = Build();
        Returns(repo,
            Row(TenantA, Constants.DelegatedRoles.DelegatedReader),
            Row(TenantB, Constants.DelegatedRoles.DelegatedAdmin));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(2, scope.TenantIds.Count);
        Assert.False(scope.CanWrite(TenantA));
        Assert.True(scope.CanWrite(TenantB));
    }

    [Fact]
    public async Task GetScope_TenantIdLookup_IsCaseInsensitive()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA.ToLowerInvariant()));

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.Covers(TenantA.ToUpperInvariant()));
    }

    [Fact]
    public async Task GetScope_SecondCall_IsCached()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));

        await svc.GetScopeAsync(Upn);
        await svc.GetScopeAsync(Upn);

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Upsert_InvalidatesCache()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        repo.Setup(r => r.UpsertDelegatedAdminAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await svc.GetScopeAsync(Upn); // primes cache
        await svc.UpsertAsync(Upn, TenantB, Constants.DelegatedRoles.DelegatedReader,
            Constants.DelegatedStatus.Active, Constants.DelegatedSource.OperatorGranted, "ga@vendor.example");
        await svc.GetScopeAsync(Upn); // must re-query after invalidation

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Revoke_InvalidatesCache()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        repo.Setup(r => r.SetDelegatedAdminStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await svc.GetScopeAsync(Upn);
        await svc.SetStatusAsync(Upn, TenantA, Constants.DelegatedStatus.Revoked);
        await svc.GetScopeAsync(Upn);

        repo.Verify(r => r.GetDelegatedTenantsAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    // --- Tenant Templates: scope is the union of direct grants + every tenant in every assigned template ---

    [Fact]
    public async Task GetScope_TemplateAssignment_ExpandsToTemplateTenants()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1, Constants.DelegatedRoles.DelegatedReader));
        TemplateTenants(repo, TemplateId1, TenantA, TenantB);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(2, scope.TenantIds.Count);
        Assert.True(scope.Covers(TenantA));
        Assert.True(scope.Covers(TenantB));
        Assert.Equal(Constants.DelegatedRoles.DelegatedReader, scope.RoleFor(TenantA));
    }

    [Fact]
    public async Task GetScope_DirectAndTemplate_AreUnioned()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA));
        ReturnsTemplates(repo, Assignment(TemplateId1));
        TemplateTenants(repo, TemplateId1, TenantB, TenantC);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(3, scope.TenantIds.Count);
        Assert.True(scope.Covers(TenantA));
        Assert.True(scope.Covers(TenantB));
        Assert.True(scope.Covers(TenantC));
    }

    [Fact]
    public async Task GetScope_DisabledTemplateAssignment_Ignored()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1, enabled: false));
        TemplateTenants(repo, TemplateId1, TenantA);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.IsEmpty);
        Assert.False(scope.Covers(TenantA));
    }

    [Fact]
    public async Task GetScope_UnknownTemplateRole_IsDropped()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1, role: "SuperRoot"));
        TemplateTenants(repo, TemplateId1, TenantA);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_EmptyTemplate_ContributesNothing()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1));
        TemplateTenants(repo, TemplateId1); // no tenants

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetScope_TenantInTwoTemplates_ResolvedOnce()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1), Assignment(TemplateId2));
        TemplateTenants(repo, TemplateId1, TenantA);
        TemplateTenants(repo, TemplateId2, TenantA, TenantB);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.Equal(2, scope.TenantIds.Count);
        Assert.True(scope.Covers(TenantA));
        Assert.True(scope.Covers(TenantB));
    }

    [Fact]
    public async Task GetScope_TemplateAdminRole_BeatsDirectReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedReader));
        ReturnsTemplates(repo, Assignment(TemplateId1, Constants.DelegatedRoles.DelegatedAdmin));
        TemplateTenants(repo, TemplateId1, TenantA);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.CanWrite(TenantA)); // stronger DelegatedAdmin wins across sources
    }

    [Fact]
    public async Task GetScope_DirectAdmin_BeatsTemplateReader()
    {
        var (svc, repo) = Build();
        Returns(repo, Row(TenantA, Constants.DelegatedRoles.DelegatedAdmin));
        ReturnsTemplates(repo, Assignment(TemplateId1, Constants.DelegatedRoles.DelegatedReader));
        TemplateTenants(repo, TemplateId1, TenantA);

        var scope = await svc.GetScopeAsync(Upn);

        Assert.True(scope.CanWrite(TenantA)); // direct DelegatedAdmin not downgraded by template reader
    }

    [Fact]
    public async Task GetScope_WithTemplates_IsCached()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1));
        TemplateTenants(repo, TemplateId1, TenantA);

        await svc.GetScopeAsync(Upn);
        await svc.GetScopeAsync(Upn);

        repo.Verify(r => r.GetTemplateAssignmentsForUpnAsync(It.IsAny<string>()), Times.Once);
        repo.Verify(r => r.GetTemplateTenantsAsync(TemplateId1), Times.Once);
    }

    // --- Template mutations go through the service and invalidate cached scope (no stale auth) ---

    [Fact]
    public async Task AssignTemplate_InvalidatesUpnCache()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1));
        TemplateTenants(repo, TemplateId1, TenantA);
        TemplateExists(repo, TemplateId2); // target template must exist for the assign to take effect
        repo.Setup(r => r.AssignTemplateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await svc.GetScopeAsync(Upn); // primes cache
        await svc.AssignTemplateAsync(Upn, TemplateId2, Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example");
        await svc.GetScopeAsync(Upn); // must re-resolve after invalidation

        repo.Verify(r => r.GetTemplateAssignmentsForUpnAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AssignTemplate_NonexistentTemplate_IsNoOp()
    {
        var (svc, repo) = Build();
        // GetTenantTemplateAsync null by default → no assignment row, no cache invalidation.
        repo.Setup(r => r.AssignTemplateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var assigned = await svc.AssignTemplateAsync(
            Upn, TemplateId1, Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example");

        Assert.False(assigned);
        repo.Verify(r => r.AssignTemplateAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UnassignTemplate_InvalidatesUpnCache()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1)); // UPN IS assigned to TemplateId1
        TemplateTenants(repo, TemplateId1, TenantA);
        repo.Setup(r => r.UnassignTemplateAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await svc.GetScopeAsync(Upn);
        var unassigned = await svc.UnassignTemplateAsync(Upn, TemplateId1);
        await svc.GetScopeAsync(Upn);

        Assert.True(unassigned);
        // Verify re-resolution via the tenant expansion (the unassign now also reads assignments itself,
        // so asserting on GetTemplateTenantsAsync isolates the two scope resolutions cleanly).
        repo.Verify(r => r.GetTemplateTenantsAsync(TemplateId1), Times.Exactly(2));
    }

    [Fact]
    public async Task UnassignTemplate_NotAssigned_IsNoOp()
    {
        var (svc, repo) = Build();
        // GetTemplateAssignmentsForUpnAsync returns empty by default → the UPN is not assigned → no delete,
        // no invalidation, so the endpoint won't write false revoke audits.
        repo.Setup(r => r.UnassignTemplateAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var unassigned = await svc.UnassignTemplateAsync(Upn, TemplateId1);

        Assert.False(unassigned);
        repo.Verify(r => r.UnassignTemplateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private static void TemplateExists(Mock<IAdminRepository> repo, string templateId, params string[] tenantIds) =>
        repo.Setup(r => r.GetTenantTemplateAsync(templateId)).ReturnsAsync(new TenantTemplate
        {
            TemplateId = templateId,
            Name = "Managed Service Tenants",
            TenantIds = tenantIds.ToList(),
        });

    [Fact]
    public async Task RemoveTenantFromTemplate_InvalidatesAllAssignees()
    {
        const string upnB = "msp-admin-2@partner.example";
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1));
        TemplateTenants(repo, TemplateId1, TenantA);
        TemplateExists(repo, TemplateId1, TenantA); // tenant IS a member → real removal
        repo.Setup(r => r.RemoveTenantFromTemplateAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        repo.Setup(r => r.GetTemplateAssigneesAsync(TemplateId1)).ReturnsAsync(new List<TenantTemplateAssignment>
        {
            new() { Upn = Upn, TemplateId = TemplateId1, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true },
            new() { Upn = upnB, TemplateId = TemplateId1, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true },
        });

        await svc.GetScopeAsync(Upn);   // primes A
        await svc.GetScopeAsync(upnB);  // primes B
        var removed = await svc.RemoveTenantFromTemplateAsync(TemplateId1, TenantA);
        await svc.GetScopeAsync(Upn);   // re-resolves A
        await svc.GetScopeAsync(upnB);  // re-resolves B

        Assert.True(removed);
        repo.Verify(r => r.GetTemplateAssignmentsForUpnAsync(Upn), Times.Exactly(2));
        repo.Verify(r => r.GetTemplateAssignmentsForUpnAsync(upnB), Times.Exactly(2));
    }

    [Fact]
    public async Task AddTenantToTemplate_NonexistentTemplate_IsNoOp()
    {
        var (svc, repo) = Build();
        // GetTenantTemplateAsync defaults to null (template does not exist) → must NOT upsert a ghost row.
        repo.Setup(r => r.AddTenantToTemplateAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var added = await svc.AddTenantToTemplateAsync(TemplateId1, TenantA);

        Assert.False(added);
        repo.Verify(r => r.AddTenantToTemplateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddTenantToTemplate_ExistingTemplate_Adds()
    {
        var (svc, repo) = Build();
        TemplateExists(repo, TemplateId1); // meta-backed, no tenants yet
        repo.Setup(r => r.AddTenantToTemplateAsync(TemplateId1, TenantA)).ReturnsAsync(true);
        repo.Setup(r => r.GetTemplateAssigneesAsync(TemplateId1)).ReturnsAsync(new List<TenantTemplateAssignment>());

        var added = await svc.AddTenantToTemplateAsync(TemplateId1, TenantA);

        Assert.True(added);
        repo.Verify(r => r.AddTenantToTemplateAsync(TemplateId1, TenantA), Times.Once);
    }

    [Fact]
    public async Task RemoveTenantFromTemplate_NonexistentTemplate_IsNoOp()
    {
        var (svc, repo) = Build();
        // GetTenantTemplateAsync null by default → no removal, no false audit signal.
        repo.Setup(r => r.RemoveTenantFromTemplateAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var removed = await svc.RemoveTenantFromTemplateAsync(TemplateId1, TenantA);

        Assert.False(removed);
        repo.Verify(r => r.RemoveTenantFromTemplateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RemoveTenantFromTemplate_TenantNotMember_IsNoOp()
    {
        var (svc, repo) = Build();
        TemplateExists(repo, TemplateId1, TenantB); // template exists but does NOT contain TenantA
        repo.Setup(r => r.RemoveTenantFromTemplateAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var removed = await svc.RemoveTenantFromTemplateAsync(TemplateId1, TenantA);

        Assert.False(removed);
        repo.Verify(r => r.RemoveTenantFromTemplateAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTemplate_InvalidatesCapturedAssignees()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1));
        TemplateTenants(repo, TemplateId1, TenantA);
        repo.Setup(r => r.DeleteTenantTemplateAsync(It.IsAny<string>())).ReturnsAsync(true);
        // Assignees are captured BEFORE the cascade delete removes them.
        repo.Setup(r => r.GetTemplateAssigneesAsync(TemplateId1)).ReturnsAsync(new List<TenantTemplateAssignment>
        {
            new() { Upn = Upn, TemplateId = TemplateId1, Role = Constants.DelegatedRoles.DelegatedReader, IsEnabled = true },
        });

        await svc.GetScopeAsync(Upn);
        await svc.DeleteTemplateAsync(TemplateId1);
        await svc.GetScopeAsync(Upn);

        repo.Verify(r => r.GetTemplateAssignmentsForUpnAsync(Upn), Times.Exactly(2));
    }

    [Fact]
    public async Task AssignTemplate_NormalizesTemplateIdCase()
    {
        // The opaque templateId is case-insensitive at the service boundary: an upper-cased id resolves the
        // same lowercase-stored template, so external/hand-crafted callers don't silently miss.
        var (svc, repo) = Build();
        TemplateExists(repo, "tpl-lower"); // stored/generated id is lowercase
        repo.Setup(r => r.AssignTemplateAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var assigned = await svc.AssignTemplateAsync(
            Upn, "TPL-LOWER", Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example");

        Assert.True(assigned);
        // Repo was looked up + written with the normalized (lowercase) id.
        repo.Verify(r => r.GetTenantTemplateAsync("tpl-lower"), Times.Once);
        repo.Verify(r => r.AssignTemplateAsync(Upn, "tpl-lower",
            Constants.DelegatedRoles.DelegatedReader, true, "ga@vendor.example"), Times.Once);
    }

    [Fact]
    public async Task RenameTemplate_DoesNotInvalidateCache()
    {
        var (svc, repo) = Build();
        ReturnsTemplates(repo, Assignment(TemplateId1));
        TemplateTenants(repo, TemplateId1, TenantA);
        repo.Setup(r => r.RenameTenantTemplateAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await svc.GetScopeAsync(Upn);
        await svc.RenameTemplateAsync(TemplateId1, "Renamed");
        await svc.GetScopeAsync(Upn); // name-only change → scope stays cached

        repo.Verify(r => r.GetTemplateAssignmentsForUpnAsync(It.IsAny<string>()), Times.Once);
    }
}
