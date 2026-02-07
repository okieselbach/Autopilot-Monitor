using System.Net;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions
{
    public class SignalRRemoveFromGroupFunction
    {
        private readonly ILogger<SignalRRemoveFromGroupFunction> _logger;
        private readonly GalacticAdminService _galacticAdminService;

        public SignalRRemoveFromGroupFunction(
            ILogger<SignalRRemoveFromGroupFunction> logger,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _galacticAdminService = galacticAdminService;
        }

        [Function("RemoveFromGroup")]
        public async Task<RemoveFromGroupOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/groups/leave")] HttpRequestData req)
        {
            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated RemoveFromGroup attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                    return new RemoveFromGroupOutput { HttpResponse = unauthorizedResponse };
                }

                // Parse request
                if (req.Body.Length > 1_048_576) // 1 MB limit
                {
                    var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await errorResponse.WriteAsJsonAsync(new { success = false, message = "Request body too large" });
                    return new RemoveFromGroupOutput { HttpResponse = errorResponse };
                }
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<RemoveFromGroupRequest>(requestBody);

                if (string.IsNullOrEmpty(request?.ConnectionId) || string.IsNullOrEmpty(request?.GroupName))
                {
                    var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await errorResponse.WriteAsJsonAsync(new { success = false, message = "ConnectionId and GroupName are required" });
                    return new RemoveFromGroupOutput { HttpResponse = errorResponse };
                }

                // Get user's tenant ID from JWT
                var userTenantId = TenantHelper.GetTenantId(req);
                var userEmail = TenantHelper.GetUserIdentifier(req);

                // Validate tenant access (same logic as AddToGroup)
                // Group names are in format: "tenant-{tenantId}", "session-{tenantId}-{sessionId}", or "galactic-admins"
                // Users can only leave groups for their own tenant (unless they are Galactic Admin)

                // Explicit validation for the galactic-admins group
                if (request.GroupName == "galactic-admins")
                {
                    var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);
                    if (!isGalacticAdmin)
                    {
                        _logger.LogWarning($"User {userEmail} (tenant {userTenantId}) attempted to leave galactic-admins group without being a Galactic Admin");
                        var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied: Only Galactic Admins can leave this group" });
                        return new RemoveFromGroupOutput { HttpResponse = forbiddenResponse };
                    }
                    _logger.LogInformation($"Galactic Admin {userEmail} leaving galactic-admins group");
                }
                else
                {
                    var requestedTenantId = SignalRGroupHelper.ExtractTenantIdFromGroupName(request.GroupName);
                    if (string.IsNullOrEmpty(requestedTenantId))
                    {
                        _logger.LogWarning($"User {userEmail} attempted to leave unrecognized group format: {request.GroupName}");
                        var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "Unrecognized group name format" });
                        return new RemoveFromGroupOutput { HttpResponse = badRequestResponse };
                    }

                    // Check if user is allowed to leave this tenant's group
                    if (requestedTenantId != userTenantId)
                    {
                        // Check if user is Galactic Admin (they can leave any tenant's group)
                        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);

                        if (!isGalacticAdmin)
                        {
                            _logger.LogWarning($"User {userEmail} (tenant {userTenantId}) attempted to leave group for tenant {requestedTenantId}");
                            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                            await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied: You can only leave groups for your own tenant" });
                            return new RemoveFromGroupOutput { HttpResponse = forbiddenResponse };
                        }
                        else
                        {
                            _logger.LogInformation($"Galactic Admin {userEmail} leaving cross-tenant group: {request.GroupName}");
                        }
                    }
                }

                // Extract session ID from group name if it's a session-specific group
                var logPrefix = SignalRGroupHelper.ExtractLogPrefix(request.GroupName);
                _logger.LogInformation($"{logPrefix} RemoveFromGroup: {request.GroupName} (User: {userEmail})");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Removed from group {request.GroupName}"
                });

                return new RemoveFromGroupOutput
                {
                    HttpResponse = response,
                    SignalRGroupAction = new SignalRGroupAction(SignalRGroupActionType.Remove)
                    {
                        GroupName = request.GroupName,
                        ConnectionId = request.ConnectionId
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing from group");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return new RemoveFromGroupOutput { HttpResponse = errorResponse };
            }
        }

    }

    public class RemoveFromGroupRequest
    {
        public string? ConnectionId { get; set; }
        public string? GroupName { get; set; }
    }

    public class RemoveFromGroupOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRGroupAction? SignalRGroupAction { get; set; }
    }
}
