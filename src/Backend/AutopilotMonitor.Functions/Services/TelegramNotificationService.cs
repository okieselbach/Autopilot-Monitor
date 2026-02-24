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
                var response = await _http.PostAsync(webhookUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning(
                        "Telegram webhook returned {StatusCode} for tenant {TenantId}: {Body}",
                        (int)response.StatusCode, tenantId, body);
                }
                else
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
