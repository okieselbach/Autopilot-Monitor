using System.Threading;
using System.Threading.Tasks;

namespace AutopilotMonitor.Functions.Services.Monitoring
{
    /// <summary>
    /// Thin abstraction over Azure Storage Queue properties so the health-check
    /// watcher can be unit-tested without a live Storage account. One call per
    /// queue read; implementations should be safe to invoke in parallel.
    /// </summary>
    public interface IPoisonQueueProbe
    {
        /// <summary>
        /// Returns the approximate message count of the queue named
        /// <paramref name="queueName"/>. A non-existent queue (404) is treated as
        /// zero — empty poison queues are not created until something fails for
        /// the first time, which is the healthy state we want to surface.
        /// Throws on any other transport/auth error so the caller can mark the
        /// check as unhealthy with a precise message.
        /// </summary>
        Task<long> GetApproximateMessageCountAsync(string queueName, CancellationToken ct);
    }
}
