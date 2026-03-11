using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models.Notifications;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Renders a NotificationAlert as a Teams Workflow webhook payload with an Adaptive Card attachment.
    /// </summary>
    public class TeamsWorkflowAdaptiveCardRenderer : INotificationRenderer
    {
        public WebhookProviderType ProviderType => WebhookProviderType.TeamsWorkflowWebhook;

        public string RenderToJson(NotificationAlert alert)
        {
            var body = new List<object>();

            // Title
            body.Add(new
            {
                type = "TextBlock",
                text = alert.Title,
                weight = "Bolder",
                size = "Medium",
                wrap = true
            });

            // Summary
            if (!string.IsNullOrEmpty(alert.Summary))
            {
                body.Add(new
                {
                    type = "TextBlock",
                    text = alert.Summary,
                    wrap = true,
                    isSubtle = true
                });
            }

            // Facts
            if (alert.Facts.Count > 0)
            {
                body.Add(new
                {
                    type = "FactSet",
                    facts = alert.Facts.Select(f => new { title = f.Name, value = f.Value }).ToArray()
                });
            }

            // Additional sections
            foreach (var section in alert.Sections)
            {
                if (!string.IsNullOrEmpty(section.Title))
                {
                    body.Add(new
                    {
                        type = "TextBlock",
                        text = section.Title,
                        weight = "Bolder",
                        wrap = true
                    });
                }

                if (!string.IsNullOrEmpty(section.Text))
                {
                    body.Add(new
                    {
                        type = "TextBlock",
                        text = section.Text,
                        wrap = true
                    });
                }
            }

            // Actions
            var actions = alert.Actions
                .Where(a => a.Type == "openUrl" && !string.IsNullOrEmpty(a.Url))
                .Select(a => new
                {
                    type = "Action.OpenUrl",
                    title = a.Title,
                    url = a.Url
                })
                .ToArray();

            // Build Adaptive Card
            var adaptiveCard = new Dictionary<string, object>
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                ["type"] = "AdaptiveCard",
                ["version"] = "1.4",
                ["body"] = body,
            };

            if (actions.Length > 0)
                adaptiveCard["actions"] = actions;

            // Add color accent via msteams width property based on severity
            adaptiveCard["msteams"] = new { width = "Full" };

            // Wrap in Workflow webhook envelope
            var payload = new
            {
                type = "message",
                attachments = new[]
                {
                    new
                    {
                        contentType = "application/vnd.microsoft.card.adaptive",
                        content = adaptiveCard
                    }
                }
            };

            return JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver
                {
                    NamingStrategy = new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy()
                }
            });
        }
    }
}
