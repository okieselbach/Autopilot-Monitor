namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Publishes data change events for downstream consumers (Event Hub, Service Bus, etc.).
    /// Default implementation is NullDataEventPublisher (no-op).
    /// </summary>
    public interface IDataEventPublisher
    {
        /// <summary>
        /// Publishes a data change event.
        /// </summary>
        /// <param name="eventType">Domain event type (e.g. "session.created", "event.ingested", "rule.evaluated")</param>
        /// <param name="payload">The event payload (typically the domain object)</param>
        /// <param name="tenantId">Optional tenant context for routing</param>
        Task PublishAsync(string eventType, object payload, string? tenantId = null);
    }

    /// <summary>
    /// No-op implementation of IDataEventPublisher.
    /// Used when event streaming is not configured.
    /// </summary>
    public class NullDataEventPublisher : IDataEventPublisher
    {
        public Task PublishAsync(string eventType, object payload, string? tenantId = null)
            => Task.CompletedTask;
    }
}
