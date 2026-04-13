using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Config;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Evaluates SLA compliance for all tenants and dispatches breach notifications.
    /// Two entry points:
    /// - EvaluateAllTenantsAsync() — called by timer trigger (every 2 hours)
    /// - EvaluateSessionCompletionAsync() — called inline from IngestEventsFunction (fire-and-forget)
    /// </summary>
    public class SlaBreachEvaluationService
    {
        private readonly TenantConfigurationService _configService;
        private readonly IConfigRepository _configRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly WebhookNotificationService _webhookService;
        private readonly GlobalNotificationService _globalNotificationService;
        private readonly SlaNotificationThrottleService _throttle;
        private readonly OpsEventService _opsEventService;
        private readonly TelemetryClient _telemetryClient;
        private readonly ILogger<SlaBreachEvaluationService> _logger;

        public SlaBreachEvaluationService(
            TenantConfigurationService configService,
            IConfigRepository configRepo,
            IMaintenanceRepository maintenanceRepo,
            ISessionRepository sessionRepo,
            WebhookNotificationService webhookService,
            GlobalNotificationService globalNotificationService,
            SlaNotificationThrottleService throttle,
            OpsEventService opsEventService,
            TelemetryClient telemetryClient,
            ILogger<SlaBreachEvaluationService> logger)
        {
            _configService = configService;
            _configRepo = configRepo;
            _maintenanceRepo = maintenanceRepo;
            _sessionRepo = sessionRepo;
            _webhookService = webhookService;
            _globalNotificationService = globalNotificationService;
            _throttle = throttle;
            _opsEventService = opsEventService;
            _telemetryClient = telemetryClient;
            _logger = logger;
        }

        // ── Timer trigger entry point ─────────────────────────────────────────

        /// <summary>
        /// Evaluates SLA compliance for all tenants with at least one SlaNotifyOn* toggle enabled.
        /// Called every 2 hours by the timer trigger.
        /// </summary>
        public async Task EvaluateAllTenantsAsync()
        {
            var sw = Stopwatch.StartNew();
            int tenantsEvaluated = 0;
            int breachesDetected = 0;
            int notificationsSent = 0;

            try
            {
                var allConfigs = await _configRepo.GetAllTenantConfigurationsAsync();

                // Filter to tenants with at least one SLA notification toggle enabled
                var qualifying = allConfigs.Where(c =>
                    c.SlaNotifyOnSuccessRateBreach ||
                    c.SlaNotifyOnDurationBreach ||
                    c.SlaNotifyOnAppInstallBreach).ToList();

                _logger.LogInformation("SLA evaluation: {Total} tenants, {Qualifying} with SLA notifications enabled",
                    allConfigs.Count, qualifying.Count);

                foreach (var config in qualifying)
                {
                    try
                    {
                        var result = await EvaluateTenantAsync(config);
                        tenantsEvaluated++;
                        breachesDetected += result.BreachesDetected;
                        notificationsSent += result.NotificationsSent;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SLA evaluation failed for tenant {TenantId}", config.TenantId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SLA evaluation failed");
            }

            sw.Stop();

            // Application Insights telemetry
            _telemetryClient.TrackEvent("SlaEvaluationCompleted", new Dictionary<string, string>
            {
                { "TenantsEvaluated", tenantsEvaluated.ToString() },
                { "BreachesFound", breachesDetected.ToString() },
                { "NotificationsSent", notificationsSent.ToString() },
                { "DurationMs", sw.ElapsedMilliseconds.ToString() },
            });

            // Ops event
            _ = _opsEventService.RecordSlaEvaluationCompletedAsync(
                tenantsEvaluated, breachesDetected, notificationsSent, (int)sw.ElapsedMilliseconds);

            _logger.LogInformation("SLA evaluation completed: {Tenants} tenants, {Breaches} breaches, {Notifications} notifications in {Ms}ms",
                tenantsEvaluated, breachesDetected, notificationsSent, sw.ElapsedMilliseconds);
        }

        private async Task<(int BreachesDetected, int NotificationsSent)> EvaluateTenantAsync(TenantConfiguration config)
        {
            int breaches = 0;
            int notifications = 0;

            var now = DateTime.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1);
            var sessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(monthStart, now.AddDays(1), config.TenantId);

            var terminal = sessions
                .Where(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed)
                .ToList();

            if (terminal.Count == 0) return (0, 0);

            // Check success rate breach
            if (config.SlaNotifyOnSuccessRateBreach && config.SlaTargetSuccessRate.HasValue)
            {
                var succeeded = terminal.Count(s => s.Status == SessionStatus.Succeeded);
                var successRate = (succeeded / (double)terminal.Count) * 100;
                var threshold = config.SlaSuccessRateNotifyThreshold.HasValue
                    ? (double)config.SlaSuccessRateNotifyThreshold.Value
                    : (double)config.SlaTargetSuccessRate.Value;

                if (successRate < threshold)
                {
                    breaches++;
                    if (_throttle.ShouldNotify(config.TenantId, "sla_success_rate"))
                    {
                        var failed = terminal.Count(s => s.Status == SessionStatus.Failed);
                        await SendBreachNotificationAsync(config, "SuccessRate",
                            successRate, threshold, terminal.Count, failed);
                        notifications++;
                    }
                }
            }

            // Check duration breach
            if (config.SlaNotifyOnDurationBreach && config.SlaTargetMaxDurationMinutes.HasValue)
            {
                var completed = terminal
                    .Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0)
                    .Select(s => s.DurationSeconds!.Value / 60.0)
                    .OrderBy(d => d)
                    .ToList();

                if (completed.Count > 0)
                {
                    var p95 = CalculatePercentile(completed, 95);
                    if (p95 > config.SlaTargetMaxDurationMinutes.Value)
                    {
                        breaches++;
                        if (_throttle.ShouldNotify(config.TenantId, "sla_duration"))
                        {
                            await SendBreachNotificationAsync(config, "Duration",
                                p95, config.SlaTargetMaxDurationMinutes.Value, terminal.Count, 0);
                            notifications++;
                        }
                    }
                }
            }

            return (breaches, notifications);
        }

        private async Task SendBreachNotificationAsync(TenantConfiguration config, string breachType,
            double currentValue, double targetValue, int totalSessions, int failedSessions)
        {
            var alert = NotificationAlertBuilder.BuildSlaBreachAlert(
                config.TenantId, currentValue, targetValue,
                totalSessions, failedSessions, breachType,
                $"https://www.autopilotmonitor.com/sla");

            // Per-tenant webhook (Teams/Slack)
            var (webhookUrl, providerType) = config.GetEffectiveWebhookConfig();
            if (!string.IsNullOrEmpty(webhookUrl) && providerType != 0)
            {
                await _webhookService.SendNotificationAsync(webhookUrl, (Shared.Models.Notifications.WebhookProviderType)providerType, alert);
            }

            // In-app global notification
            _ = _globalNotificationService.CreateNotificationAsync(
                "sla_breach",
                alert.Title,
                alert.Summary ?? "",
                "/sla");

            // Application Insights
            _telemetryClient.TrackEvent("SlaBreachDetected", new Dictionary<string, string>
            {
                { "TenantId", config.TenantId },
                { "BreachType", breachType },
                { "CurrentValue", currentValue.ToString("F1") },
                { "TargetValue", targetValue.ToString("F1") },
                { "TotalSessions", totalSessions.ToString() },
                { "FailedSessions", failedSessions.ToString() },
                { "Period", "CurrentMonth" },
            });

            _telemetryClient.TrackEvent("SlaNotificationSent", new Dictionary<string, string>
            {
                { "TenantId", config.TenantId },
                { "Channel", !string.IsNullOrEmpty(webhookUrl) ? "Webhook+InApp" : "InApp" },
                { "NotificationType", "sla_breach" },
            });

            // Ops event
            _ = _opsEventService.RecordSlaBreachNotificationAsync(
                config.TenantId, breachType, currentValue, targetValue, totalSessions, failedSessions);
        }

        // ── Inline entry point (from IngestEventsFunction) ────────────────────

        /// <summary>
        /// Checks for consecutive enrollment failures after a session completes with failure.
        /// Designed to be called fire-and-forget — never throws.
        /// </summary>
        public async Task EvaluateSessionCompletionAsync(string tenantId, SessionSummary failedSession)
        {
            try
            {
                var config = await _configService.GetConfigurationAsync(tenantId);
                if (config == null || !config.SlaNotifyOnConsecutiveFailures)
                    return;

                var threshold = config.SlaConsecutiveFailureThreshold;
                if (threshold < 2) threshold = 5;

                // Query the most recent N sessions for this tenant
                var page = await _sessionRepo.GetSessionsAsync(tenantId, threshold);
                if (page?.Sessions == null || page.Sessions.Count < threshold)
                    return;

                // Check if all N are failed
                var allFailed = page.Sessions
                    .Take(threshold)
                    .All(s => s.Status == SessionStatus.Failed);

                if (!allFailed) return;

                if (!_throttle.ShouldNotify(tenantId, "consecutive_failures"))
                    return;

                var lastDevice = failedSession.DeviceName;
                var lastReason = failedSession.FailureReason;

                var alert = NotificationAlertBuilder.BuildConsecutiveFailuresAlert(
                    tenantId, threshold, lastDevice, lastReason,
                    $"https://www.autopilotmonitor.com/sla");

                // Per-tenant webhook
                var (webhookUrl, providerType) = config.GetEffectiveWebhookConfig();
                if (!string.IsNullOrEmpty(webhookUrl) && providerType != 0)
                {
                    await _webhookService.SendNotificationAsync(webhookUrl, (Shared.Models.Notifications.WebhookProviderType)providerType, alert);
                }

                // In-app global notification
                _ = _globalNotificationService.CreateNotificationAsync(
                    "sla_consecutive_failures",
                    alert.Title,
                    alert.Summary ?? "",
                    "/sla");

                // Application Insights
                _telemetryClient.TrackEvent("SlaConsecutiveFailures", new Dictionary<string, string>
                {
                    { "TenantId", tenantId },
                    { "ConsecutiveCount", threshold.ToString() },
                    { "LastDevice", lastDevice ?? "" },
                    { "LastFailureReason", lastReason ?? "" },
                });

                _telemetryClient.TrackEvent("SlaNotificationSent", new Dictionary<string, string>
                {
                    { "TenantId", tenantId },
                    { "Channel", !string.IsNullOrEmpty(webhookUrl) ? "Webhook+InApp" : "InApp" },
                    { "NotificationType", "consecutive_failures" },
                });

                // Ops event
                _ = _opsEventService.RecordSlaConsecutiveFailuresAsync(tenantId, threshold, lastDevice, lastReason);

                _logger.LogWarning("Consecutive failure notification sent for tenant {TenantId}: {Count} failures",
                    tenantId, threshold);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate consecutive failures for tenant {TenantId}", tenantId);
            }
        }

        private static double CalculatePercentile(List<double> sortedValues, int percentile)
        {
            if (sortedValues.Count == 0) return 0;
            var index = (int)Math.Ceiling((percentile / 100.0) * sortedValues.Count) - 1;
            index = Math.Max(0, Math.Min(index, sortedValues.Count - 1));
            return Math.Round(sortedValues[index], 1);
        }
    }
}
