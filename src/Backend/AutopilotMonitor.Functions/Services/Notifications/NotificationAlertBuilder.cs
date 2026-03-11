using System;
using System.Collections.Generic;
using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Builds channel-agnostic NotificationAlert instances for enrollment events.
    /// </summary>
    public static class NotificationAlertBuilder
    {
        public static NotificationAlert BuildEnrollmentAlert(
            string? deviceName,
            string? serialNumber,
            string? manufacturer,
            string? model,
            bool success,
            string? failureReason,
            TimeSpan? duration,
            string? sessionUrl = null)
        {
            var title = success ? "\u2705 Enrollment Succeeded" : "\u274c Enrollment Failed";
            var themeColor = success ? "00B050" : "FF0000";
            var severity = success ? NotificationSeverity.Success : NotificationSeverity.Error;
            var summary = success
                ? $"Enrollment Succeeded: {deviceName ?? "Unknown Device"}"
                : $"Enrollment Failed: {deviceName ?? "Unknown Device"}";

            var durationText = duration.HasValue
                ? $"{(int)duration.Value.TotalMinutes}m {duration.Value.Seconds}s"
                : "\u2013";

            var hardwareText = BuildHardwareText(manufacturer, model);

            var facts = new List<NotificationFact>
            {
                new() { Name = "Device", Value = deviceName ?? "\u2013" },
                new() { Name = "Serial", Value = serialNumber ?? "\u2013" },
                new() { Name = "Hardware", Value = hardwareText },
                new() { Name = "Duration", Value = durationText },
            };

            if (!success && !string.IsNullOrEmpty(failureReason))
                facts.Add(new NotificationFact { Name = "Failure Reason", Value = failureReason });

            var alert = new NotificationAlert
            {
                Title = title,
                Summary = summary,
                Severity = severity,
                ThemeColor = themeColor,
                Facts = facts,
            };

            if (!string.IsNullOrEmpty(sessionUrl))
                alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "Open session", Url = sessionUrl });

            return alert;
        }

        public static NotificationAlert BuildWhiteGloveAlert(
            string? deviceName,
            string? serialNumber,
            string? manufacturer,
            string? model,
            bool success,
            TimeSpan? duration,
            string? sessionUrl = null)
        {
            var title = success ? "\ud83d\udfe2 Pre-Provisioning Completed" : "\u274c Pre-Provisioning Failed";
            var themeColor = success ? "0078D4" : "FF0000";
            var severity = success ? NotificationSeverity.Success : NotificationSeverity.Error;
            var summary = success
                ? $"Pre-Provisioning Completed: {deviceName ?? "Unknown Device"}"
                : $"Pre-Provisioning Failed: {deviceName ?? "Unknown Device"}";

            var durationText = duration.HasValue
                ? $"{(int)duration.Value.TotalMinutes}m {duration.Value.Seconds}s"
                : "\u2013";

            var hardwareText = BuildHardwareText(manufacturer, model);

            var alert = new NotificationAlert
            {
                Title = title,
                Summary = summary,
                Severity = severity,
                ThemeColor = themeColor,
                Facts = new List<NotificationFact>
                {
                    new() { Name = "Device", Value = deviceName ?? "\u2013" },
                    new() { Name = "Serial", Value = serialNumber ?? "\u2013" },
                    new() { Name = "Hardware", Value = hardwareText },
                    new() { Name = "Duration", Value = durationText },
                },
            };

            if (!string.IsNullOrEmpty(sessionUrl))
                alert.Actions.Add(new NotificationAction { Type = "openUrl", Title = "Open session", Url = sessionUrl });

            return alert;
        }

        public static NotificationAlert BuildTestAlert()
        {
            return new NotificationAlert
            {
                Title = "\ud83d\udd14 Test Notification",
                Summary = "This is a test notification from Autopilot Monitor.",
                Severity = NotificationSeverity.Info,
                ThemeColor = "0078D4",
                Facts = new List<NotificationFact>
                {
                    new() { Name = "Device", Value = "TEST-DEVICE-001" },
                    new() { Name = "Serial", Value = "SN-TEST-12345" },
                    new() { Name = "Hardware", Value = "Test Manufacturer TestModel" },
                    new() { Name = "Duration", Value = "5m 30s" },
                },
                Actions = new List<NotificationAction>
                {
                    new() { Type = "openUrl", Title = "Open Autopilot Monitor", Url = "https://www.autopilotmonitor.com" },
                },
            };
        }

        private static string BuildHardwareText(string? manufacturer, string? model)
        {
            var parts = new[]
            {
                string.IsNullOrEmpty(manufacturer) ? null : manufacturer.Trim(),
                string.IsNullOrEmpty(model) ? null : model.Trim()
            };

            var result = string.Join(" ", Array.FindAll(parts, p => p != null));
            return string.IsNullOrEmpty(result) ? "\u2013" : result;
        }
    }
}
