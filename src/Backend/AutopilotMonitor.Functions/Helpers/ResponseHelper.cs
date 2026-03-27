using System.Net;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Extension methods on HttpRequestData for creating consistent HTTP responses.
/// Eliminates repeated CreateResponse + WriteAsJsonAsync boilerplate across function handlers.
/// </summary>
public static class ResponseHelper
{
    /// <summary>200 OK with a JSON body.</summary>
    public static async Task<HttpResponseData> OkAsync(this HttpRequestData req, object data)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(data);
        return response;
    }

    /// <summary>201 Created with a JSON body.</summary>
    public static async Task<HttpResponseData> CreatedAsync(this HttpRequestData req, object data)
    {
        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(data);
        return response;
    }

    /// <summary>400 Bad Request with <c>{ "error": message }</c>.</summary>
    public static async Task<HttpResponseData> BadRequestAsync(this HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.BadRequest);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>401 Unauthorized with <c>{ "error": message }</c>.</summary>
    public static async Task<HttpResponseData> UnauthorizedAsync(this HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.Unauthorized);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>403 Forbidden with <c>{ "error": message }</c>.</summary>
    public static async Task<HttpResponseData> ForbiddenAsync(this HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.Forbidden);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>404 Not Found with <c>{ "error": message }</c>.</summary>
    public static async Task<HttpResponseData> NotFoundAsync(this HttpRequestData req, string message)
    {
        var response = req.CreateResponse(HttpStatusCode.NotFound);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>
    /// 500 Internal Server Error. Logs the exception and returns <c>{ "error": "Internal server error" }</c>.
    /// </summary>
    public static async Task<HttpResponseData> InternalServerErrorAsync(
        this HttpRequestData req,
        ILogger logger,
        Exception ex,
        string logMessage = "Unhandled error")
    {
        logger.LogError(ex, logMessage);
        var response = req.CreateResponse(HttpStatusCode.InternalServerError);
        await response.WriteAsJsonAsync(new { error = "Internal server error" });
        return response;
    }
}
