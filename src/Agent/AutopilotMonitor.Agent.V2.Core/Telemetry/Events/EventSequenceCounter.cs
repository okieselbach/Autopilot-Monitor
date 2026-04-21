#nullable enable
using System;
using System.Threading;

namespace AutopilotMonitor.Agent.V2.Core.Telemetry.Events
{
    /// <summary>
    /// Thread-safe, persistenter Counter für <c>EnrollmentEvent.Sequence</c>. Plan §2.2 / §4.x M4.4.1.
    /// <para>
    /// Sequences beginnen bei 1 (kompatibel mit Legacy-Wire-Format). Jeder <see cref="Next"/>-Call
    /// inkrementiert atomar + persistiert sofort über <see cref="EventSequencePersistence"/>.
    /// </para>
    /// <para>
    /// <b>Warum sofort-persist?</b> Event-Raten sind niedrig (&lt;1/s typical); die Cost für Disk-Flush
    /// pro Event ist irrelevant gegenüber der Crash-Safety-Garantie: nach einem Crash darf keine
    /// Sequence-Nummer doppelt vergeben werden (sonst Backend-RowKey-Kollision).
    /// </para>
    /// </summary>
    public sealed class EventSequenceCounter
    {
        private readonly EventSequencePersistence _persistence;
        private readonly object _lock = new object();
        private long _lastAssigned;

        public EventSequenceCounter(EventSequencePersistence persistence)
        {
            _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
            _lastAssigned = persistence.Load();
        }

        /// <summary>Zuletzt vergebener Wert. <c>0</c> bei leerem File.</summary>
        public long LastAssigned
        {
            get { lock (_lock) return _lastAssigned; }
        }

        /// <summary>Vergibt die nächste Sequence, persistiert vor Rückgabe.</summary>
        public long Next()
        {
            lock (_lock)
            {
                _lastAssigned++;
                _persistence.Save(_lastAssigned);
                return _lastAssigned;
            }
        }
    }
}
