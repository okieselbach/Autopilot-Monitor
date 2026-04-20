using System;

namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Immutable value-with-provenance record. Plan §2.3.
    /// <see cref="SourceSignalOrdinal"/> points back at the <c>DecisionSignal</c>
    /// that set the fact, enabling evidence trace in the Inspector.
    /// </summary>
    public sealed class SignalFact<T>
    {
        public SignalFact(T value, long sourceSignalOrdinal)
        {
            if (sourceSignalOrdinal < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sourceSignalOrdinal),
                    "SourceSignalOrdinal must be non-negative.");
            }

            Value = value;
            SourceSignalOrdinal = sourceSignalOrdinal;
        }

        public T Value { get; }

        public long SourceSignalOrdinal { get; }

        public SignalFact<T> With(T value, long sourceSignalOrdinal) =>
            new SignalFact<T>(value, sourceSignalOrdinal);
    }
}
