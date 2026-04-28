using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Analyze
{
    /// <summary>
    /// Enqueues <see cref="AnalyzeOnEnrollmentEndEnvelope"/> messages onto the
    /// <c>analyze-on-enrollment-end</c> queue. Replaces the previous in-function
    /// fire-and-forget Task.Run that could be killed by Functions scale-in.
    /// <para>
    /// Implementations MUST NOT throw on transient send failures — the caller is on
    /// the agent's hot HTTP path and must always return 200. Failed enqueues mean the
    /// session falls back to the manual "Analyze Now" UI button.
    /// </para>
    /// </summary>
    public interface IAnalyzeOnEnrollmentEndProducer
    {
        Task EnqueueAsync(AnalyzeOnEnrollmentEndEnvelope envelope, CancellationToken cancellationToken = default);
    }
}
