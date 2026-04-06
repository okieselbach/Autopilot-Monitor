using System.Net;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Returns all known IME agent versions with first/last seen dates and session counts.
    /// Permanent archive that survives data retention — tracks Microsoft IME releases over time.
    /// Access: MemberRead (any tenant member can see global IME version history).
    /// </summary>
    public class GetImeVersionHistoryFunction
    {
        private readonly ILogger<GetImeVersionHistoryFunction> _logger;
        private readonly ISessionRepository _sessionRepo;

        public GetImeVersionHistoryFunction(ILogger<GetImeVersionHistoryFunction> logger, ISessionRepository sessionRepo)
        {
            _logger = logger;
            _sessionRepo = sessionRepo;
        }

        [Function("GetImeVersionHistory")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "metrics/ime-versions")] HttpRequestData req)
        {
            try
            {
                var versions = await _sessionRepo.GetImeVersionHistoryAsync();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(versions);
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching IME version history");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Failed to retrieve IME version history" });
                return errorResponse;
            }
        }
    }
}
