using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Centralized security validation for all API requests
    /// Performs certificate validation, rate limiting, and hardware whitelisting
    /// </summary>
    public class SecurityValidator
    {
        private static readonly Regex GuidRegex = new Regex(
            @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            RegexOptions.Compiled);

        /// <summary>
        /// Validates that a string is a valid GUID format.
        /// Use this to prevent OData filter injection in Table Storage queries.
        /// </summary>
        public static bool IsValidGuid(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return Guid.TryParse(value, out _) && GuidRegex.IsMatch(value);
        }

        /// <summary>
        /// Validates that a value is a valid GUID and throws if not.
        /// </summary>
        public static void EnsureValidGuid(string? value, string parameterName)
        {
            if (!IsValidGuid(value))
                throw new ArgumentException($"Invalid {parameterName} format. Expected a valid GUID.", parameterName);
        }

        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly SerialNumberValidator _serialNumberValidator;
        private readonly ILogger _logger;

        public SecurityValidator(
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            SerialNumberValidator serialNumberValidator,
            ILogger logger)
        {
            _configService = configService;
            _rateLimitService = rateLimitService;
            _serialNumberValidator = serialNumberValidator;
            _logger = logger;
        }

        /// <summary>
        /// Validates request security (certificate, rate limit, hardware whitelist)
        /// </summary>
        /// <param name="req">HTTP request</param>
        /// <param name="tenantId">Tenant ID for configuration lookup</param>
        /// <returns>Security validation result</returns>
        public async Task<SecurityValidationResult> ValidateRequestAsync(HttpRequestData req, string tenantId, string? sessionId = null)
        {
            // Load tenant configuration
            var config = await _configService.GetConfigurationAsync(tenantId);

            // Security validation is always enforced (no longer configurable per tenant)
            // Hard gate: tenant must enable serial validation before agent traffic is accepted.
            // Galactic Admins can set AllowInsecureAgentRequests=true in the config row for test tenants.
            if (!config.ValidateSerialNumber && !config.AllowInsecureAgentRequests)
            {
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Serial number validation is required",
                    Details = "Enable 'Validate Serial Number' in Configuration before using the agent ingestion endpoints."
                };
            }

            // 1. Validate client certificate
            var certHeader = req.Headers.Contains("X-Client-Certificate")
                ? req.Headers.GetValues("X-Client-Certificate").FirstOrDefault()
                : null;

            var certValidation = CertificateValidator.ValidateCertificate(certHeader, _logger);
            if (!certValidation.IsValid)
            {
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Unauthorized,
                    ErrorMessage = "Invalid or missing client certificate",
                    Details = certValidation.ErrorMessage
                };
            }

            // 2. Check rate limit (DoS protection)
            // Use custom tenant rate limit if set, otherwise use the synced global rate limit
            var rateLimitValue = config.CustomRateLimitRequestsPerMinute ?? config.RateLimitRequestsPerMinute;

            var rateLimitResult = _rateLimitService.CheckRateLimit(
                certValidation.Thumbprint!,
                rateLimitValue
            );

            if (!rateLimitResult.IsAllowed)
            {
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.TooManyRequests,
                    ErrorMessage = "Rate limit exceeded",
                    RateLimitResult = rateLimitResult
                };
            }

            // 3. Validate hardware whitelist
            var manufacturer = req.Headers.Contains("X-Device-Manufacturer")
                ? req.Headers.GetValues("X-Device-Manufacturer").FirstOrDefault()
                : null;

            var model = req.Headers.Contains("X-Device-Model")
                ? req.Headers.GetValues("X-Device-Model").FirstOrDefault()
                : null;

            var hardwareValidation = HardwareWhitelistValidator.ValidateHardware(
                manufacturer,
                model,
                config.GetManufacturerWhitelist(),
                config.GetModelWhitelist(),
                _logger
            );

            if (!hardwareValidation.IsValid)
            {
                return new SecurityValidationResult
                {
                    IsValid = false,
                    StatusCode = HttpStatusCode.Forbidden,
                    ErrorMessage = "Hardware not authorized",
                    Details = hardwareValidation.ErrorMessage
                };
            }

            // 4. Validate serial number against Intune Autopilot (optional, tenant setting)
            if (config.ValidateSerialNumber)
            {
                var serialNumber = req.Headers.Contains("X-Device-SerialNumber")
                    ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
                    : null;

                var serialValidation = await _serialNumberValidator.ValidateSerialNumberAsync(tenantId, serialNumber, sessionId);
                if (!serialValidation.IsValid)
                {
                    return new SecurityValidationResult
                    {
                        IsValid = false,
                        StatusCode = HttpStatusCode.Forbidden,
                        ErrorMessage = "Device not registered in Autopilot",
                        Details = serialValidation.ErrorMessage
                    };
                }
            }

            // All checks passed
            return new SecurityValidationResult
            {
                IsValid = true,
                CertificateThumbprint = certValidation.Thumbprint,
                Manufacturer = manufacturer,
                Model = model,
                RateLimitResult = rateLimitResult
            };
        }
    }

    /// <summary>
    /// Result of security validation
    /// </summary>
    public class SecurityValidationResult
    {
        /// <summary>
        /// Whether the request passed all security checks
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// HTTP status code if validation failed
        /// </summary>
        public HttpStatusCode StatusCode { get; set; }

        /// <summary>
        /// Error message if validation failed
        /// </summary>
        public string? ErrorMessage { get; set; }

        /// <summary>
        /// Detailed error information
        /// </summary>
        public string? Details { get; set; }

        /// <summary>
        /// Certificate thumbprint (if validation succeeded)
        /// </summary>
        public string? CertificateThumbprint { get; set; }

        /// <summary>
        /// Device manufacturer (if validation succeeded)
        /// </summary>
        public string? Manufacturer { get; set; }

        /// <summary>
        /// Device model (if validation succeeded)
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// Rate limit result (if validation succeeded)
        /// </summary>
        public RateLimitResult? RateLimitResult { get; set; }
    }
}
