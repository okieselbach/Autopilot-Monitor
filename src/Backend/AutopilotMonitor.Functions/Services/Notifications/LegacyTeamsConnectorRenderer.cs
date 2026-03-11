using System.Linq;
using AutopilotMonitor.Shared.Models.Notifications;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Renders a NotificationAlert as a Microsoft Teams MessageCard (legacy Office 365 Connector format).
    /// </summary>
    public class LegacyTeamsConnectorRenderer : INotificationRenderer
    {
        public WebhookProviderType ProviderType => WebhookProviderType.TeamsLegacyConnector;

        public string RenderToJson(NotificationAlert alert)
        {
            var facts = alert.Facts.Select(f => new { name = f.Name, value = f.Value }).ToArray();

            var potentialAction = alert.Actions.Count > 0
                ? alert.Actions.Select(a => new
                {
                    type = "OpenUri",
                    name = a.Title,
                    targets = new[] { new { os = "default", uri = a.Url } }
                }).ToArray()
                : null;

            object card;
            if (potentialAction != null)
            {
                card = new
                {
                    type = "MessageCard",
                    context = "http://schema.org/extensions",
                    themeColor = alert.ThemeColor ?? "0078D4",
                    summary = alert.Summary,
                    sections = new[]
                    {
                        new { activityTitle = alert.Title, facts }
                    },
                    potentialAction
                };
            }
            else
            {
                card = new
                {
                    type = "MessageCard",
                    context = "http://schema.org/extensions",
                    themeColor = alert.ThemeColor ?? "0078D4",
                    summary = alert.Summary,
                    sections = new[]
                    {
                        new { activityTitle = alert.Title, facts }
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

            // Teams MessageCard requires @type and @context prefixes
            json = json.Replace("\"type\":", "\"@type\":")
                       .Replace("\"context\":", "\"@context\":");

            return json;
        }
    }
}
