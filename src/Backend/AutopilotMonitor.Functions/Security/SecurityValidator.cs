using System;
using System.Linq;
using System.Net;
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
        private readonly TenantConfigurationService _configService;
        private readonly RateLimitService _rateLimitService;
        private readonly ILogger _logger;

        public SecurityValidator(
            TenantConfigurationService configService,
            RateLimitService rateLimitService,
            ILogger logger)
        {
            _configService = configService;
            _rateLimitService = rateLimitService;
            _logger = logger;
        }

        /// <summary>
        /// Validates request security (certificate, rate limit, hardware whitelist)
        /// </summary>
        /// <param name="req">HTTP request</param>
        /// <param name="tenantId">Tenant ID for configuration lookup</param>
        /// <returns>Security validation result</returns>
        public async Task<SecurityValidationResult> ValidateRequestAsync(HttpRequestData req, string tenantId)
        {
            // Load tenant configuration
            var config = await _configService.GetConfigurationAsync(tenantId);

            // Security validation is always enforced (no longer configurable per tenant)

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

            // TODO: Serial number validation when Graph API integration is ready
            // if (config.ValidateSerialNumber)
            // {
            //     var serialNumber = req.Headers.Contains("X-Device-SerialNumber")
            //         ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
            //         : null;
            //
            //     var serialValidation = await _serialNumberValidator.ValidateSerialNumberAsync(tenantId, serialNumber);
            //     if (!serialValidation.IsValid)
            //     {
            //         return new SecurityValidationResult
            //         {
            //             IsValid = false,
            //             StatusCode = HttpStatusCode.Forbidden,
            //             ErrorMessage = "Device not registered in Autopilot",
            //             Details = serialValidation.ErrorMessage
            //         };
            //     }
            // }

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
