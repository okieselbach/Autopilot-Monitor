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
    public class SignalRAddToGroupFunction
    {
        private readonly ILogger<SignalRAddToGroupFunction> _logger;
        private readonly GalacticAdminService _galacticAdminService;

        public SignalRAddToGroupFunction(
            ILogger<SignalRAddToGroupFunction> logger,
            GalacticAdminService galacticAdminService)
        {
            _logger = logger;
            _galacticAdminService = galacticAdminService;
        }

        [Function("AddToGroup")]
        public async Task<AddToGroupOutput> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "realtime/groups/join")] HttpRequestData req)
        {
            try
            {
                // Validate authentication
                if (!TenantHelper.IsAuthenticated(req))
                {
                    _logger.LogWarning("Unauthenticated AddToGroup attempt");
                    var unauthorizedResponse = req.CreateResponse(HttpStatusCode.Unauthorized);
                    await unauthorizedResponse.WriteAsJsonAsync(new { success = false, message = "Authentication required" });
                    return new AddToGroupOutput { HttpResponse = unauthorizedResponse };
                }

                // Parse request
                var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                var request = JsonConvert.DeserializeObject<AddToGroupRequest>(requestBody);

                if (string.IsNullOrEmpty(request?.ConnectionId) || string.IsNullOrEmpty(request?.GroupName))
                {
                    var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                    await errorResponse.WriteAsJsonAsync(new { success = false, message = "ConnectionId and GroupName are required" });
                    return new AddToGroupOutput { HttpResponse = errorResponse };
                }

                // Get user's tenant ID from JWT
                var userTenantId = TenantHelper.GetTenantId(req);
                var userEmail = TenantHelper.GetUserIdentifier(req);

                // Validate tenant access
                // Group names are in format: "tenant-{tenantId}", "session-{tenantId}-{sessionId}", or "galactic-admins"
                // Users can only join groups for their own tenant (unless they are Galactic Admin)

                // Explicit validation for the galactic-admins group
                if (request.GroupName == "galactic-admins")
                {
                    var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);
                    if (!isGalacticAdmin)
                    {
                        _logger.LogWarning($"User {userEmail} (tenant {userTenantId}) attempted to join galactic-admins group without being a Galactic Admin");
                        var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                        await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied: Only Galactic Admins can join this group" });
                        return new AddToGroupOutput { HttpResponse = forbiddenResponse };
                    }
                    _logger.LogInformation($"Galactic Admin {userEmail} joining galactic-admins group");
                }
                else
                {
                    var requestedTenantId = ExtractTenantIdFromGroupName(request.GroupName);
                    if (string.IsNullOrEmpty(requestedTenantId))
                    {
                        _logger.LogWarning($"User {userEmail} attempted to join unrecognized group format: {request.GroupName}");
                        var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                        await badRequestResponse.WriteAsJsonAsync(new { success = false, message = "Unrecognized group name format" });
                        return new AddToGroupOutput { HttpResponse = badRequestResponse };
                    }

                    // Check if user is allowed to join this tenant's group
                    if (requestedTenantId != userTenantId)
                    {
                        // Check if user is Galactic Admin (they can join any tenant's group)
                        var isGalacticAdmin = await _galacticAdminService.IsGalacticAdminAsync(userEmail);

                        if (!isGalacticAdmin)
                        {
                            _logger.LogWarning($"User {userEmail} (tenant {userTenantId}) attempted to join group for tenant {requestedTenantId}");
                            var forbiddenResponse = req.CreateResponse(HttpStatusCode.Forbidden);
                            await forbiddenResponse.WriteAsJsonAsync(new { success = false, message = "Access denied: You can only join groups for your own tenant" });
                            return new AddToGroupOutput { HttpResponse = forbiddenResponse };
                        }
                        else
                        {
                            _logger.LogInformation($"Galactic Admin {userEmail} joining cross-tenant group: {request.GroupName}");
                        }
                    }
                }

                // Extract session ID from group name if it's a session-specific group
                var logPrefix = ExtractLogPrefix(request.GroupName);
                _logger.LogInformation($"{logPrefix} AddToGroup: {request.GroupName} (User: {userEmail})");

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = $"Added to group {request.GroupName}"
                });

                return new AddToGroupOutput
                {
                    HttpResponse = response,
                    SignalRGroupAction = new SignalRGroupAction(SignalRGroupActionType.Add)
                    {
                        GroupName = request.GroupName,
                        ConnectionId = request.ConnectionId
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding to group");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { success = false, message = "Internal server error" });
                return new AddToGroupOutput { HttpResponse = errorResponse };
            }
        }

        /// <summary>
        /// Extracts tenant ID from SignalR group name
        /// Group formats: "tenant-{tenantId}" or "session-{tenantId}-{sessionId}"
        /// </summary>
        private string? ExtractTenantIdFromGroupName(string groupName)
        {
            if (groupName.StartsWith("tenant-"))
            {
                // Format: "tenant-{tenantId}"
                return groupName.Substring("tenant-".Length);
            }
            else if (groupName.StartsWith("session-"))
            {
                // Format: "session-{tenantId}-{sessionId}"
                // Extract everything between "session-" and the last 5 GUID segments
                var parts = groupName.Split('-');
                if (parts.Length >= 7) // "session" + 5 GUID parts (tenant) + 5 GUID parts (session)
                {
                    // Reconstruct tenant GUID from parts 1-5
                    return string.Join("-", parts.Skip(1).Take(5));
                }
            }
            return null;
        }

        private string ExtractLogPrefix(string groupName)
        {
            // Extract session ID from group name: "session-{tenantId}-{sessionId}"
            if (groupName.StartsWith("session-"))
            {
                var parts = groupName.Split('-');
                if (parts.Length > 2)
                {
                    var sessionId = string.Join("-", parts.Skip(parts.Length - 5).Take(5)); // Last 5 parts form the GUID
                    return $"[Session: {sessionId.Substring(0, Math.Min(8, sessionId.Length))}]";
                }
            }
            // For tenant groups: "tenant-{tenantId}"
            return $"[Group: {groupName.Substring(0, Math.Min(20, groupName.Length))}]";
        }
    }

    public class AddToGroupRequest
    {
        public string? ConnectionId { get; set; }
        public string? GroupName { get; set; }
    }

    public class AddToGroupOutput
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }

        [SignalROutput(HubName = "autopilotmonitor")]
        public SignalRGroupAction? SignalRGroupAction { get; set; }
    }
}
