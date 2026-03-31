using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.ApplicationInsights;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Pre-auth distress channel: receives error signals from agents that CANNOT use the
    /// authenticated emergency channel (cert missing, hardware blocked, device not registered, etc.).
    ///
    /// Security: NO authentication. Protected by:
    ///   1. Tenant existence check (cheap, cached)
    ///   2. Three-layer rate limiting (per-IP, per-tenant, global circuit breaker)
    ///   3. Strict payload validation (1 KB max, fixed enum, field length limits)
    ///   4. Always returns 200 OK (zero information leakage)
    ///
    /// Storage: Application Insights custom event + Azure Table Storage (14-day retention).
    /// </summary>
    public class ReportDistressFunction
    {
        private readonly ILogger<ReportDistressFunction> _logger;
        private readonly TenantConfigurationService _tenantConfigService;
        private readonly DistressRateLimitService _rateLimitService;
        private readonly IDistressReportRepository _repository;
        private readonly TelemetryClient _telemetryClient;

        // Strict limits for the unauthenticated endpoint
        private const int MaxContentLength = 1024;
        private const int MaxStringField64 = 64;
        private const int MaxStringField32 = 32;
        private const int MaxMessageLength = 256;
        private static readonly TimeSpan MaxTimestampAge = TimeSpan.FromHours(24);

        // Strip control characters (except common whitespace)
        private static readonly Regex ControlChars = new Regex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

        // Simple GUID format check (avoids injection)
        private static readonly Regex GuidPattern = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        public ReportDistressFunction(
            ILogger<ReportDistressFunction> logger,
            TenantConfigurationService tenantConfigService,
            DistressRateLimitService rateLimitService,
            IDistressReportRepository repository,
            TelemetryClient telemetryClient)
        {
            _logger = logger;
            _tenantConfigService = tenantConfigService;
            _rateLimitService = rateLimitService;
            _repository = repository;
            _telemetryClient = telemetryClient;
        }

        [Function("ReportDistress")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "agent/distress")] HttpRequestData req)
        {
            // All validation failures return 200 OK — zero information leakage.
            try
            {
                // Gate 1: Content-Length
                if (req.Headers.TryGetValues("Content-Length", out var clValues)
                    && long.TryParse(clValues.FirstOrDefault(), out var contentLength)
                    && contentLength > MaxContentLength)
                {
                    return req.CreateResponse(HttpStatusCode.OK);
                }

                // Gate 2: Extract TenantId from header
                var tenantId = req.Headers.Contains("X-Tenant-Id")
                    ? req.Headers.GetValues("X-Tenant-Id").FirstOrDefault()
                    : null;

                if (string.IsNullOrEmpty(tenantId))
                    return req.CreateResponse(HttpStatusCode.OK);

                // Gate 3: GUID format validation (prevents injection)
                if (!GuidPattern.IsMatch(tenantId))
                    return req.CreateResponse(HttpStatusCode.OK);

                // Gate 4: Rate limiting (IP + tenant + global circuit breaker)
                var clientIp = ExtractClientIp(req);
                var rateLimitResult = _rateLimitService.Check(clientIp, tenantId);
                if (!rateLimitResult.IsAllowed)
                    return req.CreateResponse(HttpStatusCode.OK);

                // Gate 5: Tenant existence check (cached — cheap O(1) lookup)
                var (_, exists) = await _tenantConfigService.TryGetConfigurationAsync(tenantId);
                if (!exists)
                    return req.CreateResponse(HttpStatusCode.OK);

                // Gate 6: Parse and validate body
                DistressReport? report;
                try
                {
                    report = await JsonSerializer.DeserializeAsync<DistressReport>(
                        req.Body,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch
                {
                    return req.CreateResponse(HttpStatusCode.OK);
                }

                if (report == null)
                    return req.CreateResponse(HttpStatusCode.OK);

                // Validate enum (reject unknown values)
                if (!Enum.IsDefined(typeof(DistressErrorType), report.ErrorType))
                    return req.CreateResponse(HttpStatusCode.OK);

                // Validate timestamp (reject stale/future)
                var age = DateTime.UtcNow - report.Timestamp;
                if (age < TimeSpan.FromMinutes(-5) || age > MaxTimestampAge)
                    return req.CreateResponse(HttpStatusCode.OK);

                // Sanitize strings
                var manufacturer = Sanitize(report.Manufacturer, MaxStringField64);
                var model = Sanitize(report.Model, MaxStringField64);
                var serialNumber = Sanitize(report.SerialNumber, MaxStringField64);
                var agentVersion = Sanitize(report.AgentVersion, MaxStringField32);
                var message = Sanitize(report.Message, MaxMessageLength);

                // Persist to Table Storage
                var entry = new DistressReportEntry
                {
                    TenantId       = tenantId,
                    ErrorType      = report.ErrorType.ToString(),
                    Manufacturer   = manufacturer,
                    Model          = model,
                    SerialNumber   = serialNumber,
                    AgentVersion   = agentVersion,
                    HttpStatusCode = report.HttpStatusCode,
                    Message        = message,
                    AgentTimestamp = report.Timestamp,
                    IngestedAt     = DateTime.UtcNow,
                    SourceIp       = clientIp,
                };

                await _repository.SaveDistressReportAsync(tenantId, entry);

                // Structured log (Warning, not Critical — data is unverified)
                _logger.LogWarning(
                    "AgentDistress [{ErrorType}] tenant={TenantId} mfr={Manufacturer} model={Model} sn={SerialNumber} http={HttpStatusCode} ver={AgentVersion}: {Message}",
                    report.ErrorType, tenantId, manufacturer, model, serialNumber,
                    report.HttpStatusCode, agentVersion, message);

                // Custom event for KQL queries:
                //   customEvents | where name == "AgentDistressReport" | order by timestamp desc
                _telemetryClient.TrackEvent("AgentDistressReport", new Dictionary<string, string>
                {
                    ["TenantId"]       = tenantId,
                    ["ErrorType"]      = report.ErrorType.ToString(),
                    ["Manufacturer"]   = manufacturer ?? string.Empty,
                    ["Model"]          = model ?? string.Empty,
                    ["SerialNumber"]   = serialNumber ?? string.Empty,
                    ["AgentVersion"]   = agentVersion ?? string.Empty,
                    ["HttpStatusCode"] = report.HttpStatusCode?.ToString() ?? string.Empty,
                    ["Message"]        = message ?? string.Empty,
                    ["AgentTimestamp"]  = report.Timestamp.ToString("O"),
                    ["SourceIp"]       = clientIp ?? string.Empty,
                });

                return req.CreateResponse(HttpStatusCode.OK);
            }
            catch (Exception ex)
            {
                // Swallow — distress channel must never leak errors
                _logger.LogError(ex, "ReportDistress: Unexpected error");
                return req.CreateResponse(HttpStatusCode.OK);
            }
        }

        private static string ExtractClientIp(HttpRequestData req)
        {
            if (req.Headers.TryGetValues("X-Forwarded-For", out var fwdValues))
            {
                var fwd = fwdValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(fwd))
                {
                    // X-Forwarded-For can contain "client, proxy1, proxy2" — take the first
                    var ip = fwd.Split(',')[0].Trim();

                    // Handle bracketed IPv6 with port: [::1]:12345
                    if (ip.StartsWith('['))
                    {
                        var closeBracket = ip.IndexOf(']');
                        if (closeBracket > 0)
                            ip = ip.Substring(1, closeBracket - 1);
                        return ip;
                    }

                    // Bare IPv6 (contains multiple colons): return as-is
                    if (ip.IndexOf(':') != ip.LastIndexOf(':'))
                        return ip;

                    // IPv4 with optional port: strip port (e.g., "1.2.3.4:12345")
                    var colonIdx = ip.LastIndexOf(':');
                    if (colonIdx > 0)
                        ip = ip.Substring(0, colonIdx);

                    return ip;
                }
            }
            return "unknown";
        }

        private static string? Sanitize(string? value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return null;
            // Strip control characters
            var clean = ControlChars.Replace(value, string.Empty).Trim();
            // Truncate
            return clean.Length <= maxLength ? clean : clean.Substring(0, maxLength);
        }
    }
}
