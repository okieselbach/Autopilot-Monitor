using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Sends enrollment notifications to a configured Microsoft Teams Incoming Webhook.
    /// Uses the MessageCard format which is reliably supported by all Teams Incoming Webhooks.
    /// </summary>
    public class TeamsNotificationService
    {
        private readonly HttpClient _http;
        private readonly ILogger<TeamsNotificationService> _logger;

        public TeamsNotificationService(HttpClient http, ILogger<TeamsNotificationService> logger)
        {
            _http = http;
            _logger = logger;
        }

        /// <summary>
        /// Sends an enrollment completion notification to the given Teams Incoming Webhook URL.
        /// This method is non-fatal: exceptions are caught and logged as warnings.
        /// </summary>
        public async Task SendEnrollmentNotificationAsync(
            string webhookUrl,
            string? deviceName,
            string? serialNumber,
            string? manufacturer,
            string? model,
            bool success,
            string? failureReason,
            TimeSpan? duration)
        {
            if (string.IsNullOrEmpty(webhookUrl))
                return;

            try
            {
                var title = success ? "✅ Enrollment Succeeded" : "❌ Enrollment Failed";
                var themeColor = success ? "00B050" : "FF0000";
                var summary = success
                    ? $"Enrollment Succeeded: {deviceName ?? "Unknown Device"}"
                    : $"Enrollment Failed: {deviceName ?? "Unknown Device"}";

                var durationText = duration.HasValue
                    ? $"{(int)duration.Value.TotalMinutes}m {duration.Value.Seconds}s"
                    : "–";

                var hardwareText = BuildHardwareText(manufacturer, model);

                var facts = new[]
                {
                    new { name = "Device", value = deviceName ?? "–" },
                    new { name = "Serial", value = serialNumber ?? "–" },
                    new { name = "Hardware", value = hardwareText },
                    new { name = "Duration", value = durationText }
                };

                object card;

                if (!success && !string.IsNullOrEmpty(failureReason))
                {
                    // Add failure reason fact
                    var factsWithReason = new[]
                    {
                        new { name = "Device", value = deviceName ?? "–" },
                        new { name = "Serial", value = serialNumber ?? "–" },
                        new { name = "Hardware", value = hardwareText },
                        new { name = "Duration", value = durationText },
                        new { name = "Failure Reason", value = failureReason }
                    };

                    card = new
                    {
                        type = "MessageCard",
                        context = "http://schema.org/extensions",
                        themeColor,
                        summary,
                        sections = new[]
                        {
                            new { activityTitle = title, facts = factsWithReason }
                        }
                    };
                }
                else
                {
                    card = new
                    {
                        type = "MessageCard",
                        context = "http://schema.org/extensions",
                        themeColor,
                        summary,
                        sections = new[]
                        {
                            new { activityTitle = title, facts }
                        }
                    };
                }

                var json = JsonConvert.SerializeObject(card, new JsonSerializerSettings
                {
                    ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                    {
                        NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy()
                    }
                });

                // Teams MessageCard requires @type and @context — serialize with @ prefix manually
                json = json.Replace("\"type\":", "\"@type\":")
                           .Replace("\"context\":", "\"@context\":");

                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(webhookUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning($"Teams webhook returned {(int)response.StatusCode}: {body}");
                }
                else
                {
                    _logger.LogInformation($"Teams enrollment notification sent for device '{deviceName}' (success={success})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to send Teams enrollment notification for device '{deviceName}'");
            }
        }

        private static string BuildHardwareText(string manufacturer, string model)
        {
            var parts = new[]
            {
                string.IsNullOrEmpty(manufacturer) ? null : manufacturer.Trim(),
                string.IsNullOrEmpty(model) ? null : model.Trim()
            };

            var result = string.Join(" ", Array.FindAll(parts, p => p != null));
            return string.IsNullOrEmpty(result) ? "–" : result;
        }
    }
}
