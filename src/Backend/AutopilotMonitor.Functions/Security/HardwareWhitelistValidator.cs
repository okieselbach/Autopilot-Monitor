using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates hardware manufacturer and model against whitelist
    /// Supports wildcard patterns (e.g., "Dell*", "HP EliteBook*")
    /// </summary>
    public static class HardwareWhitelistValidator
    {
        /// <summary>
        /// Validates hardware against whitelist
        /// </summary>
        /// <param name="manufacturer">Device manufacturer</param>
        /// <param name="model">Device model</param>
        /// <param name="manufacturerWhitelist">Allowed manufacturers (supports wildcards)</param>
        /// <param name="modelWhitelist">Allowed models (supports wildcards)</param>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <returns>Validation result</returns>
        public static HardwareValidationResult ValidateHardware(
            string? manufacturer,
            string? model,
            string[] manufacturerWhitelist,
            string[] modelWhitelist,
            ILogger? logger = null)
        {

            if (string.IsNullOrEmpty(manufacturer))
            {
                return new HardwareValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Manufacturer not provided"
                };
            }

            if (string.IsNullOrEmpty(model))
            {
                return new HardwareValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Model not provided"
                };
            }

            // Check manufacturer whitelist
            var manufacturerMatch = manufacturerWhitelist.Any(pattern => MatchesWildcard(manufacturer, pattern));
            if (!manufacturerMatch)
            {
                logger?.LogWarning($"Hardware rejected: Manufacturer '{manufacturer}' not in whitelist");
                return new HardwareValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Manufacturer '{manufacturer}' is not authorized",
                    Manufacturer = manufacturer,
                    Model = model
                };
            }

            // Check model whitelist
            var modelMatch = modelWhitelist.Any(pattern => MatchesWildcard(model, pattern));
            if (!modelMatch)
            {
                logger?.LogWarning($"Hardware rejected: Model '{model}' not in whitelist");
                return new HardwareValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Model '{model}' is not authorized",
                    Manufacturer = manufacturer,
                    Model = model
                };
            }

            logger?.LogDebug($"Hardware validated: Manufacturer={manufacturer}, Model={model}");
            return new HardwareValidationResult
            {
                IsValid = true,
                Manufacturer = manufacturer,
                Model = model
            };
        }

        /// <summary>
        /// Matches a string against a wildcard pattern (supports * and ?)
        /// </summary>
        private static bool MatchesWildcard(string text, string pattern)
        {
            if (pattern == "*")
                return true;

            // Convert wildcard pattern to regex
            // Escape regex special chars, then replace * and ?
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            try
            {
                // Use timeout to prevent ReDoS attacks with pathological input
                return Regex.IsMatch(text, regexPattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
            }
            catch (RegexMatchTimeoutException)
            {
                // Timeout indicates potential ReDoS attempt - treat as non-match
                return false;
            }
        }
    }

    /// <summary>
    /// Result of hardware validation
    /// </summary>
    public class HardwareValidationResult
    {
        /// <summary>
        /// Whether the hardware is authorized
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Device manufacturer
        /// </summary>
        public string? Manufacturer { get; set; }

        /// <summary>
        /// Device model
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// Error message if validation failed
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
