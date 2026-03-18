using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Sends Telegram notifications for Private Preview signup events.
    /// Webhook URL is read from the PreviewConfig table (PartitionKey="TelegramBot", RowKey="config").
    /// Temporary — remove after GA.
    /// </summary>
    public class TelegramNotificationService
    {
        private readonly HttpClient _http;
        private readonly TableClient _tableClient;
        private readonly ILogger<TelegramNotificationService> _logger;

        public TelegramNotificationService(
            HttpClient http,
            IConfiguration configuration,
            ILogger<TelegramNotificationService> logger)
        {
            _http = http;
            _logger = logger;

            var connectionString = configuration["AzureTableStorageConnectionString"];
            var serviceClient = new TableServiceClient(connectionString);
            _tableClient = serviceClient.GetTableClient(Constants.TableNames.PreviewConfig);
            // Table is initialized centrally by TableInitializerService at startup
        }

        /// <summary>
        /// Sends a Telegram message to the configured channel when a new tenant signs up for Private Preview.
        /// No-op if the webhook URL is not configured in the PreviewConfig table.
        /// </summary>
        public async Task SendNewTenantSignupAsync(string tenantId, string upn)
        {
            try
            {
                var webhookUrl = await GetWebhookUrlAsync();
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogDebug("Telegram webhook URL not configured — skipping Private Preview signup notification");
                    return;
                }

                var payload = new
                {
                    chat_id = "-1003632442830", // Telegram channel ID (as string)
                    text = $"New Private Preview signup!\nTenantID: {tenantId}\nUPN: {upn}"
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var sent = await PostWithRetryAsync(webhookUrl, content, $"signup:{tenantId}");

                if (sent)
                {
                    _logger.LogInformation(
                        "Telegram Private Preview signup notification sent for tenant {TenantId}, UPN {Upn}",
                        tenantId, upn);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to send Telegram Private Preview signup notification for tenant {TenantId}",
                    tenantId);
            }
        }

        /// <summary>
        /// Sends a Telegram notification when a Tenant Admin submits a session report.
        /// Best-effort — silently no-ops if the webhook URL is not configured.
        /// </summary>
        public async Task SendSessionReportAsync(string tenantId, string submittedBy, string sessionId, string reportId, string comment)
        {
            try
            {
                var webhookUrl = await GetWebhookUrlAsync();
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogDebug("Telegram webhook URL not configured — skipping session report notification");
                    return;
                }

                var commentLine = string.IsNullOrWhiteSpace(comment) ? "" : $"\nComment: {comment}";
                var payload = new
                {
                    chat_id = "-1003632442830",
                    text = $"New Session Report submitted!\nTenantID: {tenantId}\nBy: {submittedBy}\nSessionID: {sessionId}\nReportID: {reportId}{commentLine}"
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var sent = await PostWithRetryAsync(webhookUrl, content, $"report:{reportId}");

                if (sent)
                {
                    _logger.LogInformation(
                        "Telegram session report notification sent for report {ReportId}", reportId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram session report notification for {ReportId}", reportId);
            }
        }

        /// <summary>
        /// Posts content to a webhook URL with a single retry on transient failures (429, 5xx, network errors).
        /// </summary>
        private async Task<bool> PostWithRetryAsync(string webhookUrl, StringContent content, string context)
        {
            const int maxAttempts = 2;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var response = await _http.PostAsync(webhookUrl, content);

                    if (response.IsSuccessStatusCode)
                        return true;

                    var statusCode = (int)response.StatusCode;
                    var isTransient = statusCode == 429 || statusCode >= 500 && statusCode <= 599;

                    if (!isTransient || attempt >= maxAttempts)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning("Telegram webhook returned {StatusCode} for {Context}: {Body}", statusCode, context, body);
                        return false;
                    }

                    _logger.LogWarning(
                        "Telegram webhook returned {StatusCode} for {Context} (attempt {Attempt}/{MaxAttempts}). Retrying in 2s",
                        statusCode, context, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex) when (attempt < maxAttempts && (ex is HttpRequestException or TaskCanceledException))
                {
                    _logger.LogWarning(ex,
                        "Telegram webhook network error for {Context} (attempt {Attempt}/{MaxAttempts}). Retrying in 2s",
                        context, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
            return false;
        }

        /// <summary>
        /// Sends a Telegram notification when a user submits feedback via the in-app feedback bubble.
        /// Best-effort — silently no-ops if the webhook URL is not configured.
        /// </summary>
        public async Task SendFeedbackAsync(string tenantId, string upn, string displayName, int rating, string? comment)
        {
            try
            {
                var webhookUrl = await GetWebhookUrlAsync();
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    _logger.LogDebug("Telegram webhook URL not configured — skipping feedback notification");
                    return;
                }

                var stars = new string('\u2605', rating) + new string('\u2606', 5 - rating);
                var commentLine = string.IsNullOrWhiteSpace(comment) ? "" : $"\nComment: {comment}";
                var payload = new
                {
                    chat_id = "-1003632442830",
                    text = $"New Feedback!\nRating: {stars} ({rating}/5)\nUser: {displayName} ({upn})\nTenant: {tenantId}{commentLine}"
                };

                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var sent = await PostWithRetryAsync(webhookUrl, content, $"feedback:{upn}");

                if (sent)
                {
                    _logger.LogInformation("Telegram feedback notification sent for user {Upn}", upn);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send Telegram feedback notification for {Upn}", upn);
            }
        }

        private async Task<string?> GetWebhookUrlAsync()
        {
            try
            {
                var entity = await _tableClient.GetEntityAsync<TableEntity>("TelegramBot", "config");
                entity.Value.TryGetValue("WebhookUrl", out var value);
                return value?.ToString();
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read Telegram webhook URL from PreviewConfig table");
                return null;
            }
        }
    }
}
