using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Resolves and manages <b>delegated admin</b> assignments — the "scoped global" tier between a
/// single-tenant member and a platform GlobalAdmin. A delegated admin may read (and later write) a
/// SUBSET of tenants: exactly the tenants for which it holds an Active, enabled DelegatedAdmins row.
/// Externally this is surfaced as "MSP mode".
///
/// The scope is keyed on UPN and is <b>tid-agnostic</b> — identical to <see cref="GlobalAdminService"/>:
/// the caller signs into their own home tenant, and their cross-tenant reach is resolved from this table
/// regardless of the JWT's tid. Resolution is cached for 5 minutes; every mutation invalidates the cache.
/// </summary>
public class DelegatedAdminService
{
    private readonly IAdminRepository _adminRepo;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DelegatedAdminService> _logger;
    // Per-process cache: on scaled-out Flex Consumption, the scope invalidation in UpsertAsync/revoke
    // only clears the mutating instance, so other instances serve a stale delegated (MSP) scope until
    // expiry. A short TTL caps that cross-instance window so a granted/revoked delegated assignment
    // self-heals in seconds. The lookup is a small Table Storage query. Do NOT raise this back to
    // minutes "for performance" — it reintroduces the role flip-flop (see TenantAdminsService).
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(30);

    public DelegatedAdminService(
        IAdminRepository adminRepo,
        IMemoryCache cache,
        ILogger<DelegatedAdminService> logger)
    {
        _adminRepo = adminRepo;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the caller's effective delegated scope: the set of tenants it may access and the role per
    /// tenant. Only <see cref="Constants.DelegatedStatus.Active"/> + enabled rows with a recognized role
    /// contribute; pending/revoked/disabled/unknown-role rows are ignored (fail-closed). Cached briefly (see _cacheDuration).
    /// Returns an empty (never null) scope for a UPN with no effective assignments.
    /// </summary>
    public virtual async Task<DelegatedScope> GetScopeAsync(string? upn)
    {
        if (string.IsNullOrWhiteSpace(upn))
            return DelegatedScope.Empty;

        upn = upn.ToLowerInvariant();
        var cacheKey = $"delegated-scope:{upn}";
        if (_cache.TryGetValue<DelegatedScope>(cacheKey, out var cached) && cached != null)
            return cached;

        var rows = await _adminRepo.GetDelegatedTenantsAsync(upn);
        var tenantRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!row.IsEnabled || row.Status != Constants.DelegatedStatus.Active)
                continue;

            var role = NormalizeRole(row.Role, upn, row.TenantId);
            if (role == null)
                continue;

            // A duplicate (admin, tenant) RowKey is impossible in Table Storage, but if two rows ever
            // collide on tenantId casing, the stronger role wins (DelegatedAdmin > DelegatedReader).
            if (tenantRoles.TryGetValue(row.TenantId, out var existing) && IsStronger(existing, role))
                continue;
            tenantRoles[row.TenantId] = role;
        }

        // Template-derived tenants: a UPN assigned to a Tenant Template inherits read scope to every
        // tenant in that template. This is the MSP convenience — assign the UPN once, manage the tenant
        // set on the template. Same fail-closed + stronger-role-wins merge as direct grants; a tenant
        // present both directly and via a template keeps the stronger role. Membership changes converge
        // within the cache TTL (no separate per-template cache by design).
        var templateAssignments = await _adminRepo.GetTemplateAssignmentsForUpnAsync(upn);
        foreach (var assignment in templateAssignments)
        {
            if (!assignment.IsEnabled)
                continue;

            var role = NormalizeRole(assignment.Role, upn, $"template:{assignment.TemplateId}");
            if (role == null)
                continue;

            var templateTenants = await _adminRepo.GetTemplateTenantsAsync(assignment.TemplateId);
            foreach (var tenantId in templateTenants)
            {
                if (string.IsNullOrWhiteSpace(tenantId))
                    continue;
                if (tenantRoles.TryGetValue(tenantId, out var existing) && IsStronger(existing, role))
                    continue;
                tenantRoles[tenantId] = role;
            }
        }

        var scope = new DelegatedScope(tenantRoles);
        _cache.Set(cacheKey, scope, _cacheDuration);
        return scope;
    }

    /// <summary>Creates or replaces an assignment, then invalidates the UPN's cached scope.</summary>
    public async Task<DelegatedAdminEntry> UpsertAsync(
        string upn, string tenantId, string role, string status, string source, string grantedBy)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        grantedBy = grantedBy.ToLowerInvariant();

        await _adminRepo.UpsertDelegatedAdminAsync(upn, tenantId, role, status, source, grantedBy);
        Invalidate(upn);

        return new DelegatedAdminEntry
        {
            Upn = upn,
            TenantId = tenantId,
            Role = role,
            IsEnabled = true,
            Status = status,
            Source = source,
            GrantedAt = DateTime.UtcNow,
            GrantedBy = grantedBy
        };
    }

    /// <summary>Transitions an assignment's status (e.g. PendingApproval → Active on approval, → Revoked).</summary>
    public async Task<bool> SetStatusAsync(string upn, string tenantId, string status)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        var ok = await _adminRepo.SetDelegatedAdminStatusAsync(upn, tenantId, status);
        Invalidate(upn);
        return ok;
    }

    public async Task<bool> SetEnabledAsync(string upn, string tenantId, bool isEnabled)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        var ok = await _adminRepo.SetDelegatedAdminEnabledAsync(upn, tenantId, isEnabled);
        Invalidate(upn);
        return ok;
    }

    public async Task<bool> RemoveAsync(string upn, string tenantId)
    {
        upn = upn.ToLowerInvariant();
        tenantId = tenantId.ToLowerInvariant();
        var ok = await _adminRepo.RemoveDelegatedAdminAsync(upn, tenantId);
        Invalidate(upn);
        return ok;
    }

    /// <summary>Every assignment row across all delegated admins — for the operator/GA management UI.</summary>
    public Task<List<DelegatedAdminEntry>> GetAllAsync()
        => _adminRepo.GetAllDelegatedAdminsAsync();

    /// <summary>All assignment rows for a UPN (any status) — for the operator/admin management UI.</summary>
    public Task<List<DelegatedAdminEntry>> GetAssignmentsForUpnAsync(string upn)
        => _adminRepo.GetDelegatedTenantsAsync(upn.ToLowerInvariant());

    /// <summary>All assignment rows targeting a tenant (any status) — for the customer "who manages me?" UI.</summary>
    public Task<List<DelegatedAdminEntry>> GetAssigneesForTenantAsync(string tenantId)
        => _adminRepo.GetDelegatedAssigneesAsync(tenantId.ToLowerInvariant());

    // --- Tenant Templates (app-internal tenant bundles) ---
    //
    // ALL template mutations go through the service (never the repo directly) so the delegated-scope
    // cache is invalidated in lockstep — otherwise a removed tenant / unassigned UPN keeps stale auth
    // scope until TTL expiry. As with direct grants, Invalidate clears only THIS instance's cache; other
    // scaled-out instances self-heal within the short TTL (see _cacheDuration). Mutations that change the
    // tenant SET of a template (add/remove tenant, delete) invalidate EVERY current assignee.
    //
    // templateId is normalized to lowercase at this boundary so the opaque key is case-insensitive for any
    // (e.g. hand-crafted external) caller. Generated IDs are already lowercase hex GUIDs, so this is lossless.

    /// <summary>Read-through: all templates with tenant members + assignee counts (management UI).</summary>
    public Task<List<TenantTemplate>> GetAllTemplatesAsync() => _adminRepo.GetAllTenantTemplatesAsync();

    /// <summary>Read-through: one template with its tenant members + assignee count.</summary>
    public Task<TenantTemplate?> GetTemplateAsync(string templateId) => _adminRepo.GetTenantTemplateAsync(NormalizeTemplateId(templateId));

    /// <summary>Read-through: all UPNs assigned to a template (management UI).</summary>
    public Task<List<TenantTemplateAssignment>> GetTemplateAssigneesAsync(string templateId)
        => _adminRepo.GetTemplateAssigneesAsync(NormalizeTemplateId(templateId));

    /// <summary>Creates a template (no scope effect — a fresh template has no assignees).</summary>
    public Task<string> CreateTemplateAsync(string name, string createdBy)
        => _adminRepo.CreateTenantTemplateAsync(name, createdBy);

    /// <summary>Renames a template (name only — no scope effect, no invalidation needed).</summary>
    public Task<bool> RenameTemplateAsync(string templateId, string name)
        => _adminRepo.RenameTenantTemplateAsync(NormalizeTemplateId(templateId), name);

    /// <summary>Deletes a template and all its assignments; invalidates every (former) assignee's scope.</summary>
    public async Task<bool> DeleteTemplateAsync(string templateId)
    {
        templateId = NormalizeTemplateId(templateId);
        // Capture assignees BEFORE the cascade delete removes their assignment rows.
        var assignees = await _adminRepo.GetTemplateAssigneesAsync(templateId);
        var ok = await _adminRepo.DeleteTenantTemplateAsync(templateId);
        InvalidateAll(assignees);
        return ok;
    }

    /// <summary>
    /// Adds a tenant to a template; every assignee gains it — invalidate them so it takes effect now.
    /// Returns false (no-op) if the template does not exist (meta-backed) — prevents a "ghost" template
    /// being conjured from a lone membership row.
    /// </summary>
    public async Task<bool> AddTenantToTemplateAsync(string templateId, string tenantId)
    {
        templateId = NormalizeTemplateId(templateId);
        if (await _adminRepo.GetTenantTemplateAsync(templateId) == null)
            return false;

        var ok = await _adminRepo.AddTenantToTemplateAsync(templateId, tenantId);
        await InvalidateTemplateAssigneesAsync(templateId);
        return ok;
    }

    /// <summary>
    /// Removes a tenant from a template (REVOKE flow); invalidate every assignee immediately. Returns false
    /// (no-op, no invalidation) if the template does not exist or the tenant is not currently a member — so a
    /// typo cannot return success and write false "access removed" audit rows.
    /// </summary>
    public async Task<bool> RemoveTenantFromTemplateAsync(string templateId, string tenantId)
    {
        templateId = NormalizeTemplateId(templateId);
        tenantId = tenantId.ToLowerInvariant();
        var template = await _adminRepo.GetTenantTemplateAsync(templateId);
        if (template == null || !template.TenantIds.Contains(tenantId))
            return false;

        var ok = await _adminRepo.RemoveTenantFromTemplateAsync(templateId, tenantId);
        await InvalidateTemplateAssigneesAsync(templateId);
        return ok;
    }

    /// <summary>
    /// Assigns a UPN to a template; invalidates that UPN's scope. Returns false (no-op) if the template does
    /// not exist (meta-backed) — so the service fully owns the existence invariant, independent of any caller
    /// pre-check, and a delete racing an assign can't leave a dangling assignment row.
    /// </summary>
    public async Task<bool> AssignTemplateAsync(string upn, string templateId, string role, bool isEnabled, string assignedBy)
    {
        templateId = NormalizeTemplateId(templateId);
        if (await _adminRepo.GetTenantTemplateAsync(templateId) == null)
            return false;

        upn = upn.ToLowerInvariant();
        var ok = await _adminRepo.AssignTemplateAsync(upn, templateId, role, isEnabled, assignedBy);
        Invalidate(upn);
        return ok;
    }

    /// <summary>
    /// Unassigns a UPN from a template (REVOKE flow); invalidates that UPN's scope immediately. Returns false
    /// (no-op, no invalidation) if the UPN is not currently assigned to the template — so a mistyped UPN can't
    /// return success and write false "access removed" audit rows.
    /// </summary>
    public async Task<bool> UnassignTemplateAsync(string upn, string templateId)
    {
        upn = upn.ToLowerInvariant();
        templateId = NormalizeTemplateId(templateId);

        var assignments = await _adminRepo.GetTemplateAssignmentsForUpnAsync(upn);
        var isAssigned = false;
        foreach (var assignment in assignments)
        {
            if (string.Equals(assignment.TemplateId, templateId, StringComparison.Ordinal))
            {
                isAssigned = true;
                break;
            }
        }
        if (!isAssigned)
            return false;

        var ok = await _adminRepo.UnassignTemplateAsync(upn, templateId);
        Invalidate(upn);
        return ok;
    }

    /// <summary>Invalidates the cached scope of every current assignee of a template.</summary>
    private async Task InvalidateTemplateAssigneesAsync(string templateId)
        => InvalidateAll(await _adminRepo.GetTemplateAssigneesAsync(templateId));

    private void InvalidateAll(IEnumerable<TenantTemplateAssignment> assignees)
    {
        foreach (var assignee in assignees)
            Invalidate(assignee.Upn);
    }

    private void Invalidate(string upn) => _cache.Remove($"delegated-scope:{upn.ToLowerInvariant()}");

    /// <summary>Normalizes the opaque templateId to lowercase so the storage key is case-insensitive to callers.</summary>
    private static string NormalizeTemplateId(string templateId) => (templateId ?? string.Empty).ToLowerInvariant();

    /// <summary>Empty/missing role defaults to the least-privileged DelegatedReader; an unrecognized
    /// role is dropped (fail-closed) rather than silently granting access.</summary>
    private string? NormalizeRole(string role, string upn, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(role))
            return Constants.DelegatedRoles.DelegatedReader;
        if (role == Constants.DelegatedRoles.DelegatedReader || role == Constants.DelegatedRoles.DelegatedAdmin)
            return role;

        _logger.LogWarning("Unrecognized delegated Role '{Role}' for {Upn} on tenant {TenantId} — ignoring row",
            role, upn, tenantId);
        return null;
    }

    private static bool IsStronger(string existing, string candidate)
        => existing == Constants.DelegatedRoles.DelegatedAdmin
           && candidate == Constants.DelegatedRoles.DelegatedReader;
}

/// <summary>
/// Immutable resolved delegated scope: which tenants a UPN may access and at what role. Empty when the
/// caller is not a delegated admin. Consumed by the auth middleware to gate cross-tenant access against
/// a subset (vs. the all-or-nothing GlobalAdmin scope).
/// </summary>
public sealed class DelegatedScope
{
    public static readonly DelegatedScope Empty = new(new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _tenantRoles;

    public DelegatedScope(IReadOnlyDictionary<string, string> tenantRoles)
        => _tenantRoles = tenantRoles;

    /// <summary>Tenant IDs (lowercase) this scope grants access to.</summary>
    public IReadOnlyCollection<string> TenantIds => (IReadOnlyCollection<string>)_tenantRoles.Keys;

    public bool IsEmpty => _tenantRoles.Count == 0;

    /// <summary>True if the scope grants access to the given tenant (any delegated role).</summary>
    public bool Covers(string? tenantId)
        => !string.IsNullOrEmpty(tenantId) && _tenantRoles.ContainsKey(tenantId);

    /// <summary>The delegated role for a tenant, or null if not covered.</summary>
    public string? RoleFor(string? tenantId)
        => tenantId != null && _tenantRoles.TryGetValue(tenantId, out var r) ? r : null;

    /// <summary>True if the scope grants write (DelegatedAdmin) on the given tenant.</summary>
    public bool CanWrite(string? tenantId)
        => RoleFor(tenantId) == Constants.DelegatedRoles.DelegatedAdmin;
}

/// <summary>
/// Entity representing a delegated admin assignment in Table Storage.
/// PartitionKey = delegated-admin UPN (lowercase); RowKey = TenantId (lowercase).
/// </summary>
public class DelegatedAdminEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // UPN (lowercase)
    public string RowKey { get; set; } = string.Empty;       // TenantId (lowercase)
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    /// <summary>The delegated admin's UPN (lowercase) — denormalized copy of PartitionKey.</summary>
    public string Upn { get; set; } = string.Empty;

    /// <summary>The managed tenant ID (lowercase) — denormalized copy of RowKey.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary><see cref="Constants.DelegatedRoles"/>: DelegatedReader (default) or DelegatedAdmin.</summary>
    public string Role { get; set; } = Constants.DelegatedRoles.DelegatedReader;

    /// <summary>Whether this assignment is currently enabled (soft toggle, independent of Status).</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary><see cref="Constants.DelegatedStatus"/>: Active / PendingApproval / Revoked.</summary>
    public string Status { get; set; } = Constants.DelegatedStatus.Active;

    /// <summary><see cref="Constants.DelegatedSource"/>: OperatorGranted / CustomerDelegated.</summary>
    public string Source { get; set; } = Constants.DelegatedSource.OperatorGranted;

    /// <summary>When this assignment was created.</summary>
    public DateTime GrantedDate { get; set; }

    /// <summary>UPN of the principal who created this assignment (operator GA, or customer tenant admin).</summary>
    public string GrantedBy { get; set; } = string.Empty;
}

/// <summary>
/// A row in the <see cref="Constants.TableNames.TenantTemplates"/> table. Two row layouts share the
/// class, discriminated by <see cref="RowKey"/>:
/// <list type="bullet">
/// <item>PK = templateId, RK = <see cref="MetaRowKey"/> — template metadata (<see cref="Name"/>, creator).</item>
/// <item>PK = templateId, RK = tenantId (lowercase) — one membership row per tenant in the template.</item>
/// </list>
/// A template is an app-internal named bundle of tenants; a delegated admin assigned to it (see
/// <see cref="TenantTemplateAssignmentEntity"/>) gains read scope to every tenant member.
/// </summary>
public class TenantTemplateEntity : ITableEntity
{
    /// <summary>The reserved RowKey of the per-template metadata row. Tenant IDs are GUIDs and never collide with it.</summary>
    public const string MetaRowKey = "meta";

    public string PartitionKey { get; set; } = string.Empty; // templateId
    public string RowKey { get; set; } = string.Empty;       // "meta" or tenantId (lowercase)
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    /// <summary>Display name (meta row only; empty on membership rows).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>UPN of the GlobalAdmin who created the template (meta row only).</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>When the template was created (meta row only).</summary>
    public DateTime CreatedDate { get; set; }

    /// <summary>The managed tenant ID (lowercase) — denormalized copy of RowKey on membership rows; empty on meta.</summary>
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// A row in the <see cref="Constants.TableNames.TenantTemplateAssignments"/> table: delegated-admin UPN X
/// is assigned to template T at role <see cref="Role"/>. PK = UPN (lowercase), RK = templateId. Resolving
/// a UPN's scope point-scans this by PK, then expands each template into its tenant members. Operator-managed
/// only (no PendingApproval flow); fail-closed — only enabled + recognized-role assignments confer scope.
/// </summary>
public class TenantTemplateAssignmentEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // UPN (lowercase)
    public string RowKey { get; set; } = string.Empty;       // templateId
    public DateTimeOffset? Timestamp { get; set; }
    public Azure.ETag ETag { get; set; }

    /// <summary>The delegated admin's UPN (lowercase) — denormalized copy of PartitionKey.</summary>
    public string Upn { get; set; } = string.Empty;

    /// <summary>The template ID — denormalized copy of RowKey.</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary><see cref="Constants.DelegatedRoles"/>: DelegatedReader (default) or DelegatedAdmin.</summary>
    public string Role { get; set; } = Constants.DelegatedRoles.DelegatedReader;

    /// <summary>Whether this assignment is currently enabled (soft toggle).</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>UPN of the GlobalAdmin who created this assignment.</summary>
    public string AssignedBy { get; set; } = string.Empty;

    /// <summary>When this assignment was created.</summary>
    public DateTime AssignedDate { get; set; }
}
