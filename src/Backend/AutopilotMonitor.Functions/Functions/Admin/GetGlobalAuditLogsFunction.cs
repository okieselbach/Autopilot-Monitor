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
    public class GetGlobalAuditLogsFunction
    {
        private readonly ILogger<GetGlobalAuditLogsFunction> _logger;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public GetGlobalAuditLogsFunction(
            ILogger<GetGlobalAuditLogsFunction> logger,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _maintenanceRepo = maintenanceRepo;
        }

        [Function("GetGlobalAuditLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/audit/logs")] HttpRequestData req)
        {
            _logger.LogInformation("GetGlobalAuditLogs function processing request (Global Admin Mode)");

            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                var userEmail = TenantHelper.GetUserIdentifier(req);
                var callerTenantId = TenantHelper.GetTenantId(req);

                var query = HttpUtility.ParseQueryString(req.Url.Query ?? string.Empty);
                var parsed = DateWindowPagination.ParseQuery(query);
                if (parsed.Error != null)
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { success = false, message = parsed.Error });
                    return bad;
                }

                _logger.LogInformation(
                    "Fetching global audit logs (dateFrom={DateFrom}, dateTo={DateTo}, pageSize={PageSize}) for {User}",
                    parsed.DateFrom, parsed.DateTo, parsed.PageSize?.ToString() ?? "all", userEmail);

                if (parsed.PageSize == null)
                {
                    var logs = await _maintenanceRepo.GetAllAuditLogsAsync(parsed.DateFrom, parsed.DateTo);
                    return await req.OkAsync(new { success = true, count = logs.Count, logs });
                }

                string? azureToken = null;
                if (parsed.Continuation != null)
                {
                    if (!DateWindowPagination.TryAcceptContinuation(
                            parsed.Continuation,
                            scope: "audit:global",
                            callerTenantId: callerTenantId,
                            dateFrom: parsed.DateFrom,
                            dateTo: parsed.DateTo,
                            extras: null,
                            out azureToken,
                            out var rejectReason))
                    {
                        _logger.LogWarning("GetGlobalAuditLogs: continuation rejected ({Reason})", rejectReason);
                        var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                        await bad.WriteAsJsonAsync(new
                        {
                            success = false,
                            message = $"Invalid continuation token ({rejectReason}). Restart pagination from the first page.",
                        });
                        return bad;
                    }
                }

                var page = await _maintenanceRepo.GetAllAuditLogsPageAsync(
                    parsed.DateFrom, parsed.DateTo, parsed.PageSize.Value, azureToken);

                string? nextLink = null;
                if (!string.IsNullOrEmpty(page.NextRawToken))
                {
                    var fp = DateWindowPagination.Fingerprint(
                        scope: "audit:global",
                        callerTenantId: callerTenantId,
                        dateFrom: parsed.DateFrom,
                        dateTo: parsed.DateTo);
                    var wireToken = ContinuationToken.Encode(page.NextRawToken!, callerTenantId, fp);
                    nextLink = DateWindowPagination.BuildNextLink(
                        basePath: "/api/global/audit/logs",
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
                return await req.InternalServerErrorAsync(_logger, ex, "Get global audit logs");
            }
        }
    }
}
