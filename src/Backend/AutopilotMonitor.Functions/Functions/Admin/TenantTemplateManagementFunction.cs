using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin;

/// <summary>
/// GlobalAdmin-only management of <b>Tenant Templates</b> — app-internal named bundles of tenants for the
/// delegated-admin ("MSP mode") tier. An operator assigns a delegated UPN to a template instead of to each
/// tenant; the UPN then reads every tenant in the template (resolved by <see cref="DelegatedAdminService"/>).
/// Adding a tenant to the template grants it to all assignees at once.
///
/// Reads are GlobalReadOrAdmin (a read-only Global Reader may audit templates); all mutations are
/// GlobalAdminOnly (enforced by <c>PolicyEnforcementMiddleware</c> via the route catalog), so a delegated
/// caller can never manage templates. ALL mutations go through <see cref="DelegatedAdminService"/> (never the
/// repository directly) so the delegated-scope cache is invalidated in lockstep.
///
/// Audit: access-affecting mutations are logged under the <b>affected managed tenant(s)</b>' trail (so the
/// customer sees "who can read my tenant" / "access removed"). Template create/rename carry no tenant context
/// and are not customer-visible access changes — they are logged operationally only (no AuditLogs partition).
/// </summary>
public class TenantTemplateManagementFunction
{
    private readonly ILogger<TenantTemplateManagementFunction> _logger;
    private readonly DelegatedAdminService _delegatedAdminService;
    private readonly IMaintenanceRepository _maintenanceRepo;

    private const string AuditEntity = "DelegatedTemplateAccess";

    public TenantTemplateManagementFunction(
        ILogger<TenantTemplateManagementFunction> logger,
        DelegatedAdminService delegatedAdminService,
        IMaintenanceRepository maintenanceRepo)
    {
        _logger = logger;
        _delegatedAdminService = delegatedAdminService;
        _maintenanceRepo = maintenanceRepo;
    }

    /// <summary>GET /api/global/tenant-templates — list every template with tenants + assignee count. GlobalReadOrAdmin.</summary>
    [Function("GetTenantTemplates")]
    [Authorize]
    public async Task<HttpResponseData> GetTenantTemplates(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/tenant-templates")] HttpRequestData req)
    {
        var templates = await _delegatedAdminService.GetAllTemplatesAsync();
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { templates });
        return response;
    }

    /// <summary>POST /api/global/tenant-templates — create a template. GlobalAdminOnly. Body: { "name": "..." }.</summary>
    [Function("CreateTenantTemplate")]
    [Authorize]
    public async Task<HttpResponseData> CreateTenantTemplate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/tenant-templates")] HttpRequestData req,
        FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<CreateTenantTemplateRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Name))
            return await Bad(req, "name is required");

        var templateId = await _delegatedAdminService.CreateTemplateAsync(body.Name, currentUpn ?? "");

        _logger.LogInformation("Tenant template created: {TemplateId} '{Name}' by {By}", templateId, body.Name, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { templateId, name = body.Name.Trim() });
        return response;
    }

    /// <summary>PATCH /api/global/tenant-templates/{templateId} — rename. GlobalAdminOnly. Body: { "name": "..." }.</summary>
    [Function("RenameTenantTemplate")]
    [Authorize]
    public async Task<HttpResponseData> RenameTenantTemplate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/tenant-templates/{templateId}")] HttpRequestData req,
        string templateId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<RenameTenantTemplateRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Name))
            return await Bad(req, "name is required");

        var ok = await _delegatedAdminService.RenameTemplateAsync(templateId, body.Name);
        if (!ok)
            return await NotFound(req);

        _logger.LogInformation("Tenant template renamed: {TemplateId} -> '{Name}' by {By}", templateId, body.Name, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Template renamed" });
        return response;
    }

    /// <summary>DELETE /api/global/tenant-templates/{templateId} — delete template + all its assignments. GlobalAdminOnly.</summary>
    [Function("DeleteTenantTemplate")]
    [Authorize]
    public async Task<HttpResponseData> DeleteTenantTemplate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/tenant-templates/{templateId}")] HttpRequestData req,
        string templateId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        // Capture tenants + assignee count BEFORE the cascade delete removes them, so we can audit the
        // bulk access removal under each affected tenant.
        var template = await _delegatedAdminService.GetTemplateAsync(templateId);
        await _delegatedAdminService.DeleteTemplateAsync(templateId);

        if (template != null && template.AssigneeCount > 0)
        {
            await AuditPerTenantAsync(template.TenantIds, "DELETE", "*", currentUpn,
                new Dictionary<string, string>
                {
                    { "Template", template.Name },
                    { "TemplateId", templateId },
                    { "Reason", "template-deleted" },
                    { "AssigneeCount", template.AssigneeCount.ToString() },
                });
        }

        _logger.LogInformation("Tenant template deleted: {TemplateId} by {By}", templateId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Template deleted" });
        return response;
    }

    /// <summary>POST /api/global/tenant-templates/{templateId}/tenants — add a tenant. GlobalAdminOnly. Body: { "tenantId": "&lt;guid&gt;" }.</summary>
    [Function("AddTenantToTemplate")]
    [Authorize]
    public async Task<HttpResponseData> AddTenantToTemplate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/tenant-templates/{templateId}/tenants")] HttpRequestData req,
        string templateId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<AddTemplateTenantRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.TenantId) || !Guid.TryParse(body.TenantId, out _))
            return await Bad(req, "a valid tenantId (GUID) is required");

        var tenantId = body.TenantId.ToLowerInvariant();
        // The service refuses to add to a non-existent template (no ghost from a lone membership row).
        var added = await _delegatedAdminService.AddTenantToTemplateAsync(templateId, tenantId);
        if (!added)
            return await NotFound(req);

        // Every current assignee just gained access to this tenant — audit under that tenant per assignee.
        var assignees = await _delegatedAdminService.GetTemplateAssigneesAsync(templateId);
        foreach (var assignee in assignees)
        {
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId, "CREATE", AuditEntity, assignee.Upn, currentUpn ?? "",
                new Dictionary<string, string> { { "TemplateId", templateId }, { "Reason", "tenant-added-to-template" } });
        }

        _logger.LogInformation("Tenant {TenantId} added to template {TemplateId} by {By}", tenantId, templateId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant added to template" });
        return response;
    }

    /// <summary>DELETE /api/global/tenant-templates/{templateId}/tenants/{tenantId} — remove a tenant. GlobalAdminOnly.</summary>
    [Function("RemoveTenantFromTemplate")]
    [Authorize]
    public async Task<HttpResponseData> RemoveTenantFromTemplate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/tenant-templates/{templateId}/tenants/{tenantId}")] HttpRequestData req,
        string templateId, string tenantId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;
        var normalizedTenantId = tenantId.ToLowerInvariant();

        // Snapshot assignees BEFORE the removal so we can audit each one's lost access under this tenant.
        var assignees = await _delegatedAdminService.GetTemplateAssigneesAsync(templateId);
        // The service returns false when the template doesn't exist or the tenant isn't a member — so a typo
        // can't 200 and write false "access removed" audit rows. Only audit after a real removal.
        var removed = await _delegatedAdminService.RemoveTenantFromTemplateAsync(templateId, normalizedTenantId);
        if (!removed)
            return await NotFound(req);

        foreach (var assignee in assignees)
        {
            await _maintenanceRepo.LogAuditEntryAsync(
                normalizedTenantId, "DELETE", AuditEntity, assignee.Upn, currentUpn ?? "",
                new Dictionary<string, string> { { "TemplateId", templateId }, { "Reason", "tenant-removed-from-template" } });
        }

        _logger.LogInformation("Tenant {TenantId} removed from template {TemplateId} by {By}", normalizedTenantId, templateId, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Tenant removed from template" });
        return response;
    }

    /// <summary>
    /// POST /api/global/tenant-templates/{templateId}/assignees — assign a UPN to the template. GlobalAdminOnly.
    /// Body: { "upn": "user@domain.com", "role": "DelegatedReader" | "DelegatedAdmin" }.
    /// </summary>
    [Function("AssignTenantTemplate")]
    [Authorize]
    public async Task<HttpResponseData> AssignTenantTemplate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/tenant-templates/{templateId}/assignees")] HttpRequestData req,
        string templateId, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;

        var body = await req.ReadFromJsonAsync<AssignTemplateRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Upn))
            return await Bad(req, "upn is required");

        // Fail-closed role handling — mirror the delegated grant: default to least privilege, reject unknowns.
        var role = string.IsNullOrWhiteSpace(body.Role) ? Constants.DelegatedRoles.DelegatedReader : body.Role;
        if (role != Constants.DelegatedRoles.DelegatedReader && role != Constants.DelegatedRoles.DelegatedAdmin)
            return await Bad(req, $"role must be '{Constants.DelegatedRoles.DelegatedReader}' or '{Constants.DelegatedRoles.DelegatedAdmin}'");

        // Guard against assigning to a non-existent template (would create an orphan assignment row).
        var template = await _delegatedAdminService.GetTemplateAsync(templateId);
        if (template == null)
            return await NotFound(req);

        var upn = body.Upn.ToLowerInvariant();
        // The service re-checks existence (covers a delete racing this assign) — skip the audit if it no-ops.
        var assigned = await _delegatedAdminService.AssignTemplateAsync(upn, templateId, role, true, currentUpn ?? "");
        if (!assigned)
            return await NotFound(req);

        // The UPN just gained read access to every tenant in the template — audit under each.
        await AuditPerTenantAsync(template.TenantIds, "CREATE", upn, currentUpn,
            new Dictionary<string, string>
            {
                { "Template", template.Name },
                { "TemplateId", templateId },
                { "Role", role },
                { "Reason", "template-assigned" },
            });

        _logger.LogInformation("Template {TemplateId} assigned to {Upn} ({Role}) by {By}", templateId, upn, role, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { message = "Assigned to template" });
        return response;
    }

    /// <summary>DELETE /api/global/tenant-templates/{templateId}/assignees/{upn} — unassign a UPN. GlobalAdminOnly.</summary>
    [Function("UnassignTenantTemplate")]
    [Authorize]
    public async Task<HttpResponseData> UnassignTenantTemplate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "global/tenant-templates/{templateId}/assignees/{upn}")] HttpRequestData req,
        string templateId, string upn, FunctionContext context)
    {
        var currentUpn = context.GetRequestContext().UserPrincipalName;
        var normalizedUpn = upn.ToLowerInvariant();

        // Read the template's tenants (unchanged by unassign) so we can audit the UPN's lost access per tenant.
        var template = await _delegatedAdminService.GetTemplateAsync(templateId);
        // The service returns false when the UPN was not actually assigned — so a mistyped UPN can't 200 and
        // write false "access removed" audit rows. Only audit after a real unassign.
        var unassigned = await _delegatedAdminService.UnassignTemplateAsync(normalizedUpn, templateId);
        if (!unassigned)
            return await NotFound(req);

        if (template != null)
        {
            await AuditPerTenantAsync(template.TenantIds, "DELETE", normalizedUpn, currentUpn,
                new Dictionary<string, string>
                {
                    { "Template", template.Name },
                    { "TemplateId", templateId },
                    { "Reason", "template-unassigned" },
                });
        }

        _logger.LogInformation("Template {TemplateId} unassigned from {Upn} by {By}", templateId, normalizedUpn, currentUpn);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { message = "Unassigned from template" });
        return response;
    }

    /// <summary>Logs one audit entry per tenant (the customer-visible "who can read my tenant" trail).</summary>
    private async Task AuditPerTenantAsync(
        IEnumerable<string> tenantIds, string action, string entityId, string? performedBy, Dictionary<string, string> details)
    {
        foreach (var tenantId in tenantIds)
        {
            await _maintenanceRepo.LogAuditEntryAsync(
                tenantId.ToLowerInvariant(), action, AuditEntity, entityId, performedBy ?? "", details);
        }
    }

    private static async Task<HttpResponseData> Bad(HttpRequestData req, string error)
    {
        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
        await bad.WriteAsJsonAsync(new { error });
        return bad;
    }

    private static async Task<HttpResponseData> NotFound(HttpRequestData req)
    {
        var notFound = req.CreateResponse(HttpStatusCode.NotFound);
        await notFound.WriteAsJsonAsync(new { error = "Template not found" });
        return notFound;
    }
}

public class CreateTenantTemplateRequest
{
    public string Name { get; set; } = string.Empty;
}

public class RenameTenantTemplateRequest
{
    public string Name { get; set; } = string.Empty;
}

public class AddTemplateTenantRequest
{
    public string TenantId { get; set; } = string.Empty;
}

public class AssignTemplateRequest
{
    public string Upn { get; set; } = string.Empty;
    public string? Role { get; set; }
}
