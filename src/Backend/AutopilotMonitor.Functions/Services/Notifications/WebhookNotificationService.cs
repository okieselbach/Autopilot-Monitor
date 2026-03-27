using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Dispatches NotificationAlert payloads to webhook URLs using provider-specific renderers.
    /// Replaces TeamsNotificationService with a channel-agnostic approach.
    /// </summary>
    public class WebhookNotificationService
    {
        private readonly HttpClient _http;
        private readonly ILogger<WebhookNotificationService> _logger;
        private readonly Dictionary<WebhookProviderType, INotificationRenderer> _renderers;

        public WebhookNotificationService(HttpClient http, ILogger<WebhookNotificationService> logger)
        {
            _http = http;
            _logger = logger;
            _renderers = new Dictionary<WebhookProviderType, INotificationRenderer>
            {
                [WebhookProviderType.TeamsLegacyConnector] = new LegacyTeamsConnectorRenderer(),
                [WebhookProviderType.TeamsWorkflowWebhook] = new TeamsWorkflowAdaptiveCardRenderer(),
                [WebhookProviderType.Slack] = new SlackRenderer(),
            };
        }

        /// <summary>
        /// Sends a notification (fire-and-forget, non-fatal). Exceptions are logged as warnings.
        /// </summary>
        public async Task SendNotificationAsync(string webhookUrl, WebhookProviderType providerType, NotificationAlert alert)
        {
            if (string.IsNullOrEmpty(webhookUrl) || providerType == WebhookProviderType.None)
                return;

            try
            {
                if (!_renderers.TryGetValue(providerType, out var renderer))
                {
                    _logger.LogWarning("No renderer registered for webhook provider type {ProviderType}", providerType);
                    return;
                }

                var json = renderer.RenderToJson(alert);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(webhookUrl, content);

                if (response.IsSuccessStatusCode)
                    _logger.LogInformation("Webhook notification sent: {Summary} (provider={ProviderType})", alert.Summary, providerType);
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("Webhook returned {StatusCode} for {Summary}: {Body}", (int)response.StatusCode, alert.Summary, body);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send webhook notification: {Summary}", alert.Summary);
            }
        }

        /// <summary>
        /// Sends a notification and returns the result (for test endpoint). Not fire-and-forget.
        /// </summary>
        public async Task<WebhookTestResult> SendNotificationWithResultAsync(string webhookUrl, WebhookProviderType providerType, NotificationAlert alert)
        {
            if (string.IsNullOrEmpty(webhookUrl))
                return new WebhookTestResult { Success = false, Message = "Webhook URL is not configured." };

            if (providerType == WebhookProviderType.None)
                return new WebhookTestResult { Success = false, Message = "No webhook provider selected." };

            if (!_renderers.TryGetValue(providerType, out var renderer))
                return new WebhookTestResult { Success = false, Message = $"Unknown provider type: {providerType}" };

            try
            {
                var json = renderer.RenderToJson(alert);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(webhookUrl, content);
                var statusCode = (int)response.StatusCode;

                if (response.IsSuccessStatusCode)
                {
                    return new WebhookTestResult { Success = true, StatusCode = statusCode, Message = "Test notification sent successfully." };
                }

                var body = await response.Content.ReadAsStringAsync();
                return new WebhookTestResult
                {
                    Success = false,
                    StatusCode = statusCode,
                    Message = $"Webhook returned HTTP {statusCode}: {(body.Length > 200 ? body[..200] : body)}"
                };
            }
            catch (Exception ex)
            {
                return new WebhookTestResult { Success = false, Message = $"Connection error: {ex.Message}" };
            }
        }

    }

    public class WebhookTestResult
    {
        public bool Success { get; set; }
        public int? StatusCode { get; set; }
        public string Message { get; set; } = "";
    }
}
