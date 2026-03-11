namespace AutopilotMonitor.Shared.Models.Notifications
{
    /// <summary>
    /// Determines which renderer formats the notification payload for a webhook.
    /// </summary>
    public enum WebhookProviderType
    {
        /// <summary>No webhook configured.</summary>
        None = 0,

        /// <summary>Microsoft Teams legacy Office 365 Connector (MessageCard format). Deprecated by Microsoft.</summary>
        TeamsLegacyConnector = 1,

        /// <summary>Microsoft Teams Workflow webhook (Adaptive Card format). Recommended replacement.</summary>
        TeamsWorkflowWebhook = 2,

        /// <summary>Slack Incoming Webhook (Block Kit format).</summary>
        Slack = 10,
    }
}
