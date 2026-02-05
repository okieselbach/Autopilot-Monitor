/*
 * SERIAL NUMBER VALIDATION AGAINST INTUNE AUTOPILOT
 *
 * This validator is prepared but commented out because it requires:
 * 1. Multi-tenant Graph API integration
 * 2. App registration with proper permissions (DeviceManagementServiceConfig.Read.All)
 * 3. Tenant-specific Graph API credentials
 *
 * SECURITY BENEFITS:
 * When enabled, this provides the strongest authentication by validating that:
 * - The serial number exists in the tenant's Autopilot registration
 * - Combined with cert thumbprint, manufacturer, and model, this makes attacks nearly impossible
 *
 * An attacker would need to:
 * 1. Obtain a valid MDM client certificate from an enrolled device
 * 2. Know the exact manufacturer and model (whitelisted)
 * 3. Know a valid serial number registered in Autopilot for that tenant
 *
 * TO ENABLE:
 * 1. Uncomment this code
 * 2. Set up multi-tenant Graph API authentication
 * 3. Uncomment the serial number header in BackendApiClient.cs
 * 4. Uncomment the validation call in IngestEventsFunction.cs
 */

/*
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates device serial numbers against Intune Autopilot registration
    /// Requires Graph API integration with DeviceManagementServiceConfig.Read.All permission
    /// </summary>
    public class SerialNumberValidator
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;

        // TODO: Replace with proper multi-tenant token provider
        // private readonly IGraphTokenProvider _tokenProvider;

        public SerialNumberValidator(ILogger logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Validates that a serial number is registered in Autopilot for the given tenant
        /// </summary>
        /// <param name="tenantId">Tenant ID</param>
        /// <param name="serialNumber">Device serial number</param>
        /// <returns>Validation result</returns>
        public async Task<SerialNumberValidationResult> ValidateSerialNumberAsync(string tenantId, string serialNumber)
        {
            if (string.IsNullOrEmpty(serialNumber))
            {
                return new SerialNumberValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Serial number not provided"
                };
            }

            try
            {
                // TODO: Get Graph API access token for the tenant
                // var accessToken = await _tokenProvider.GetTokenForTenantAsync(tenantId);

                // Graph API endpoint to query Autopilot devices
                var graphUrl = $"https://graph.microsoft.com/v1.0/deviceManagement/windowsAutopilotDeviceIdentities" +
                              $"?$filter=serialNumber eq '{serialNumber}'";

                // TODO: Add authentication header
                // _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                var response = await _httpClient.GetAsync(graphUrl);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var data = JsonConvert.DeserializeObject<JObject>(responseJson);

                var devices = data["value"] as JArray;
                if (devices == null || devices.Count == 0)
                {
                    _logger.LogWarning($"Serial number {serialNumber} not found in Autopilot for tenant {tenantId}");
                    return new SerialNumberValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Serial number '{serialNumber}' is not registered in Autopilot",
                        SerialNumber = serialNumber
                    };
                }

                // Serial number found in Autopilot - device is authorized
                _logger.LogInformation($"Serial number {serialNumber} validated successfully in Autopilot");
                return new SerialNumberValidationResult
                {
                    IsValid = true,
                    SerialNumber = serialNumber,
                    AutopilotDeviceId = devices[0]["id"]?.ToString()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error validating serial number {serialNumber}");
                return new SerialNumberValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Error validating serial number: {ex.Message}",
                    SerialNumber = serialNumber
                };
            }
        }
    }

    /// <summary>
    /// Result of serial number validation
    /// </summary>
    public class SerialNumberValidationResult
    {
        /// <summary>
        /// Whether the serial number is registered in Autopilot
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Device serial number
        /// </summary>
        public string SerialNumber { get; set; }

        /// <summary>
        /// Autopilot device ID (if found)
        /// </summary>
        public string AutopilotDeviceId { get; set; }

        /// <summary>
        /// Error message if validation failed
        /// </summary>
        public string ErrorMessage { get; set; }
    }
}
*/

// USAGE EXAMPLE (to be added to IngestEventsFunction.cs when ready):
/*
// TODO: Uncomment when multi-tenant Graph API integration is ready
// Validate serial number against Intune Autopilot (strongest security check)
// var serialNumber = req.Headers.Contains("X-Device-SerialNumber")
//     ? req.Headers.GetValues("X-Device-SerialNumber").FirstOrDefault()
//     : null;
//
// var serialNumberValidation = await _serialNumberValidator.ValidateSerialNumberAsync(request.TenantId, serialNumber);
// if (!serialNumberValidation.IsValid)
// {
//     _logger.LogWarning($"Serial number validation failed: {serialNumberValidation.ErrorMessage}");
//     return await CreateErrorResponse(req, HttpStatusCode.Forbidden, "Device not registered in Autopilot");
// }
//
// _logger.LogInformation($"Serial number validated: {serialNumber} (Autopilot ID: {serialNumberValidation.AutopilotDeviceId})");
*/
