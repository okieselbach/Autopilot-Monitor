using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models.Notifications;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Renders a NotificationAlert as a Slack Block Kit payload for Incoming Webhooks.
    /// </summary>
    public class SlackRenderer : INotificationRenderer
    {
        public WebhookProviderType ProviderType => WebhookProviderType.Slack;

        public string RenderToJson(NotificationAlert alert)
        {
            var blocks = new List<object>();

            // Header with severity emoji
            var emoji = GetSeverityEmoji(alert.Severity);
            blocks.Add(new
            {
                type = "header",
                text = new { type = "plain_text", text = $"{emoji} {alert.Title}", emoji = true }
            });

            // Summary
            if (!string.IsNullOrEmpty(alert.Summary))
            {
                blocks.Add(new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = alert.Summary }
                });
            }

            // Facts as fields (Slack allows max 10 fields per section)
            if (alert.Facts.Count > 0)
            {
                var fields = alert.Facts
                    .Select(f => new { type = "mrkdwn", text = $"*{f.Name}:*\n{f.Value}" })
                    .ToArray();

                // Split into chunks of 10
                for (int i = 0; i < fields.Length; i += 10)
                {
                    var chunk = fields.Skip(i).Take(10).ToArray();
                    blocks.Add(new
                    {
                        type = "section",
                        fields = chunk
                    });
                }
            }

            // Additional sections
            foreach (var section in alert.Sections)
            {
                var text = "";
                if (!string.IsNullOrEmpty(section.Title))
                    text += $"*{section.Title}*\n";
                if (!string.IsNullOrEmpty(section.Text))
                    text += section.Text;

                if (!string.IsNullOrEmpty(text))
                {
                    blocks.Add(new
                    {
                        type = "section",
                        text = new { type = "mrkdwn", text }
                    });
                }
            }

            // Action buttons
            var actionElements = alert.Actions
                .Where(a => a.Type == "openUrl" && !string.IsNullOrEmpty(a.Url))
                .Select(a => new
                {
                    type = "button",
                    text = new { type = "plain_text", text = a.Title, emoji = true },
                    url = a.Url
                })
                .ToArray();

            if (actionElements.Length > 0)
            {
                blocks.Add(new
                {
                    type = "actions",
                    elements = actionElements
                });
            }

            var payload = new { blocks };

            return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                {
                    NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy()
                }
            });
        }

        private static string GetSeverityEmoji(NotificationSeverity severity)
        {
            return severity switch
            {
                NotificationSeverity.Success => "\u2705",
                NotificationSeverity.Error => "\u274c",
                NotificationSeverity.Warning => "\u26a0\ufe0f",
                NotificationSeverity.Info => "\u2139\ufe0f",
                _ => "\u2139\ufe0f"
            };
        }
    }
}
