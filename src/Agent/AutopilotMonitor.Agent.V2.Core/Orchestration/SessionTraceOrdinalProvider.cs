#nullable enable
using System.Threading;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// In-memory <see cref="ISessionTraceOrdinalProvider"/> via
    /// <see cref="Interlocked.Increment(ref long)"/>. Plan §2.2.
    /// <para>
    /// Recovery: beim Konstruktor <c>seedLastAssigned</c> aus dem höchsten
    /// persistierten Ordinal (max aus SignalLog + Journal + Spool) setzen;
    /// <see cref="Next"/> liefert dann <c>seed + 1, seed + 2, …</c>.
    /// </para>
    /// </summary>
    public sealed class SessionTraceOrdinalProvider : ISessionTraceOrdinalProvider
    {
        private long _lastAssigned;

        public SessionTraceOrdinalProvider(long seedLastAssigned = -1)
        {
            _lastAssigned = seedLastAssigned;
        }

        public long LastAssigned => Interlocked.Read(ref _lastAssigned);

        public long Next() => Interlocked.Increment(ref _lastAssigned);
    }
}
