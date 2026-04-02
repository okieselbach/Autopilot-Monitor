using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Evaluates ops alert rules and dispatches notifications to all enabled providers.
    /// Called fire-and-forget from OpsEventService — failures are logged but never thrown.
    /// </summary>
    public class OpsAlertDispatchService
    {
        private readonly AdminConfigurationService _adminConfigService;
        private readonly TelegramNotificationService _telegram;
        private readonly WebhookNotificationService _webhook;
        private readonly ILogger<OpsAlertDispatchService> _logger;

        public OpsAlertDispatchService(
            AdminConfigurationService adminConfigService,
            TelegramNotificationService telegram,
            WebhookNotificationService webhook,
            ILogger<OpsAlertDispatchService> logger)
        {
            _adminConfigService = adminConfigService;
            _telegram = telegram;
            _webhook = webhook;
            _logger = logger;
        }

        /// <summary>
        /// Evaluates alert rules for the given ops event and dispatches to all enabled providers.
        /// Safe to call fire-and-forget — never throws.
        /// </summary>
        public async Task DispatchAsync(string category, string eventType, string severity,
            string message, string? tenantId)
        {
            try
            {
                var config = await _adminConfigService.GetConfigurationAsync();
                if (config == null) return;

                var rules = config.GetOpsAlertRules();
                var matchingRule = rules.FirstOrDefault(r =>
                    r.Enabled &&
                    r.EventType == eventType &&
                    SeverityRank(severity) >= SeverityRank(r.MinSeverity));

                if (matchingRule == null) return;

                var alert = BuildAlert(category, eventType, severity, message, tenantId);

                var tasks = new List<Task>();

                if (config.OpsAlertTelegramEnabled && !string.IsNullOrWhiteSpace(config.OpsAlertTelegramChatId))
                    tasks.Add(_telegram.SendOpsAlertAsync(config.OpsAlertTelegramChatId, alert));

                if (config.OpsAlertTeamsEnabled && !string.IsNullOrWhiteSpace(config.OpsAlertTeamsWebhookUrl))
                    tasks.Add(_webhook.SendNotificationAsync(config.OpsAlertTeamsWebhookUrl, WebhookProviderType.TeamsWorkflowWebhook, alert));

                if (config.OpsAlertSlackEnabled && !string.IsNullOrWhiteSpace(config.OpsAlertSlackWebhookUrl))
                    tasks.Add(_webhook.SendNotificationAsync(config.OpsAlertSlackWebhookUrl, WebhookProviderType.Slack, alert));

                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                    _logger.LogInformation("Ops alert dispatched for {Category}/{EventType} to {ProviderCount} provider(s)",
                        category, eventType, tasks.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to dispatch ops alert for {Category}/{EventType}", category, eventType);
            }
        }

        private static NotificationAlert BuildAlert(string category, string eventType,
            string severity, string message, string? tenantId)
        {
            var notifSeverity = severity switch
            {
                OpsEventSeverity.Critical => NotificationSeverity.Error,
                OpsEventSeverity.Error => NotificationSeverity.Error,
                OpsEventSeverity.Warning => NotificationSeverity.Warning,
                _ => NotificationSeverity.Info
            };

            var themeColor = severity switch
            {
                OpsEventSeverity.Critical => "8B0000",
                OpsEventSeverity.Error => "FF4500",
                OpsEventSeverity.Warning => "FFA500",
                _ => "4682B4"
            };

            var facts = new List<NotificationFact>
            {
                new() { Name = "Category", Value = category },
                new() { Name = "Event", Value = eventType },
                new() { Name = "Severity", Value = severity },
            };

            if (!string.IsNullOrWhiteSpace(tenantId))
                facts.Add(new NotificationFact { Name = "Tenant", Value = tenantId });

            return new NotificationAlert
            {
                Title = $"Ops Alert: {category}/{eventType}",
                Summary = message,
                Severity = notifSeverity,
                ThemeColor = themeColor,
                Facts = facts
            };
        }

        private static int SeverityRank(string severity) => severity switch
        {
            OpsEventSeverity.Info => 0,
            OpsEventSeverity.Warning => 1,
            OpsEventSeverity.Error => 2,
            OpsEventSeverity.Critical => 3,
            _ => -1
        };
    }
}
