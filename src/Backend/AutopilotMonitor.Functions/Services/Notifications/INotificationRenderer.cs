using AutopilotMonitor.Shared.Models.Notifications;

namespace AutopilotMonitor.Functions.Services.Notifications
{
    /// <summary>
    /// Transforms a channel-agnostic NotificationAlert into a provider-specific JSON payload.
    /// </summary>
    public interface INotificationRenderer
    {
        WebhookProviderType ProviderType { get; }
        string RenderToJson(NotificationAlert alert);
    }
}
