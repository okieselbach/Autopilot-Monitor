using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class QueryRawSessionsFunction
    {
        private readonly ILogger<QueryRawSessionsFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public QueryRawSessionsFunction(ILogger<QueryRawSessionsFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        /// <summary>
        /// GET /api/raw/sessions — Tenant-scoped raw session query
        /// </summary>
        [Function("QueryRawSessions")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "raw/sessions")] HttpRequestData req)
        {
            try
            {
                var tenantId = TenantHelper.GetTenantId(req);
                return await QuerySessions(req, tenantId);
            }
            catch (UnauthorizedAccessException)
            {
                var err = req.CreateResponse(HttpStatusCode.Unauthorized);
                await err.WriteAsJsonAsync(new { error = "Unauthorized" });
                return err;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query raw sessions");
            }
        }

        /// <summary>
        /// GET /api/global/raw/sessions — Cross-tenant raw session query (GlobalAdminOnly)
        /// </summary>
        [Function("QueryRawSessionsGlobal")]
        public async Task<HttpResponseData> RunGlobal(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/sessions")] HttpRequestData req)
        {
            try
            {
                var tenantId = req.Query["tenantId"];
                return await QuerySessions(req, tenantId);
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query global raw sessions");
            }
        }

        private async Task<HttpResponseData> QuerySessions(HttpRequestData req, string? tenantId)
        {
            var status = req.Query["status"];
            var startedAfter = req.Query["startedAfter"];
            var startedBefore = req.Query["startedBefore"];
            var serialNumber = req.Query["serialNumber"];
            var fieldsParam = req.Query["fields"];
            var limitStr = req.Query["limit"];
            var limit = int.TryParse(limitStr, out var l) ? Math.Clamp(l, 1, 200) : 50;

            // Build search filter for the underlying query
            var filter = new SessionSearchFilter();
            if (!string.IsNullOrEmpty(status))
                filter.Status = status;
            if (!string.IsNullOrEmpty(startedAfter) && DateTime.TryParse(startedAfter, out var after))
                filter.StartedAfter = after;
            if (!string.IsNullOrEmpty(startedBefore) && DateTime.TryParse(startedBefore, out var before))
                filter.StartedBefore = before;
            if (!string.IsNullOrEmpty(serialNumber))
                filter.SerialNumber = serialNumber;
            filter.Limit = limit;

            var sessions = await _sessionRepo.SearchSessionsAsync(tenantId, filter);

            // Apply field projection if requested
            object result;
            if (!string.IsNullOrEmpty(fieldsParam))
            {
                var fieldSet = new HashSet<string>(
                    fieldsParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);

                var projected = sessions.Select(session => ProjectFields(session, fieldSet)).ToList();
                result = new { tenantId, count = projected.Count, sessions = projected };
            }
            else
            {
                result = new { tenantId, count = sessions.Count, sessions };
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }

        private static Dictionary<string, object?> ProjectFields(SessionSummary session, HashSet<string> fields)
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            // Map requested field names to session properties
            if (fields.Contains("sessionId")) dict["sessionId"] = session.SessionId;
            if (fields.Contains("tenantId")) dict["tenantId"] = session.TenantId;
            if (fields.Contains("status")) dict["status"] = session.Status.ToString();
            if (fields.Contains("serialNumber")) dict["serialNumber"] = session.SerialNumber;
            if (fields.Contains("manufacturer")) dict["manufacturer"] = session.Manufacturer;
            if (fields.Contains("model")) dict["model"] = session.Model;
            if (fields.Contains("deviceName")) dict["deviceName"] = session.DeviceName;
            if (fields.Contains("osBuild")) dict["osBuild"] = session.OsBuild;
            if (fields.Contains("osName")) dict["osName"] = session.OsName;
            if (fields.Contains("startedAt")) dict["startedAt"] = session.StartedAt;
            if (fields.Contains("completedAt")) dict["completedAt"] = session.CompletedAt;
            if (fields.Contains("durationSeconds")) dict["durationSeconds"] = session.DurationSeconds;
            if (fields.Contains("currentPhase")) dict["currentPhase"] = session.CurrentPhase;
            if (fields.Contains("failureReason")) dict["failureReason"] = session.FailureReason;
            if (fields.Contains("eventCount")) dict["eventCount"] = session.EventCount;
            if (fields.Contains("enrollmentType")) dict["enrollmentType"] = session.EnrollmentType;
            if (fields.Contains("isPreProvisioned")) dict["isPreProvisioned"] = session.IsPreProvisioned;
            if (fields.Contains("isUserDriven")) dict["isUserDriven"] = session.IsUserDriven;
            if (fields.Contains("isHybridJoin")) dict["isHybridJoin"] = session.IsHybridJoin;
            if (fields.Contains("agentVersion")) dict["agentVersion"] = session.AgentVersion;
            if (fields.Contains("geoCountry")) dict["geoCountry"] = session.GeoCountry;

            // If no fields matched, return all
            if (dict.Count == 0)
            {
                dict["sessionId"] = session.SessionId;
                dict["tenantId"] = session.TenantId;
                dict["status"] = session.Status.ToString();
                dict["serialNumber"] = session.SerialNumber;
                dict["startedAt"] = session.StartedAt;
                dict["completedAt"] = session.CompletedAt;
            }

            return dict;
        }
    }
}
