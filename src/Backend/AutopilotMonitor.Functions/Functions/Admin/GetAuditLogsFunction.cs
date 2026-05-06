using System.Net;
using System.Web;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Admin
{
    public class GetAuditLogsFunction
    {
        private readonly ILogger<GetAuditLogsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public GetAuditLogsFunction(ILogger<GetAuditLogsFunction> logger, IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("GetAuditLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "audit/logs")] HttpRequestData req)
        {
            _logger.LogInformation("GetAuditLogs function processing request");

            try
            {
                // Authentication + MemberRead authorization enforced by PolicyEnforcementMiddleware
                string tenantId = TenantHelper.GetTenantId(req);
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsed = DateWindowPagination.ParseQuery(query);
                if (parsed.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = parsed.Error });
                    return bad;
                }

                _logger.LogInformation(
                    "Fetching audit logs (tenant={TenantId}, dateFrom={DateFrom}, dateTo={DateTo}, pageSize={PageSize}, hasContinuation={HasContinuation}) for user {User}",
                    tenantId, parsed.DateFrom, parsed.DateTo,
                    parsed.PageSize?.ToString() ?? "all", parsed.Continuation != null,
                    userIdentifier);

                if (parsed.PageSize == null)
                {
                    var logs = await _maintenanceRepo.GetAuditLogsAsync(tenantId, parsed.DateFrom, parsed.DateTo);
                    return await req.OkAsync(new { success = true, count = logs.Count, logs });
                }

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!DateWindowPagination.TryAcceptContinuation(
                            parsed.Continuation,
                            scope: "audit:tenant",
                            callerTenantId: tenantId,
                            dateFrom: parsed.DateFrom,
                            dateTo: parsed.DateTo,
                            extras: null,
                            out azureToken,
                            out var rejectReason))
                    {
                        _logger.LogWarning("GetAuditLogs: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var page = await _maintenanceRepo.GetAuditLogsPageAsync(
                    tenantId, parsed.DateFrom, parsed.DateTo, parsed.PageSize.Value, azureToken);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = DateWindowPagination.Fingerprint(
                        scope: "audit:tenant",
                        callerTenantId: tenantId,
                        dateFrom: parsed.DateFrom,
                        dateTo: parsed.DateTo);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, tenantId, fp);
                    nextLink = DateWindowPagination.BuildNextLink(
                        basePath: "/api/audit/logs",
                        pageSize: parsed.PageSize.Value,
                        wireContinuation: wireToken,
                        dateFrom: parsed.DateFrom,
                        dateTo: parsed.DateTo);
                }

                return await req.OkAsync(new
                {
                    success = true,
                    count = page.Items.Count,
                    logs = page.Items,
                    nextLink,
                });
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Get audit logs");
            }
        }
    }
}
