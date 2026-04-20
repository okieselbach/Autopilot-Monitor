using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Harness
{
    /// <summary>
    /// Builder helpers for <see cref="DecisionSignal"/> in tests — keeps call sites compact.
    /// </summary>
    internal static class TestSignals
    {
        public static DecisionSignal Raw(
            long ordinal,
            DecisionSignalKind kind = DecisionSignalKind.EspPhaseChanged,
            DateTime? occurredAtUtc = null,
            long? traceOrdinal = null,
            string origin = "UnitTest",
            IReadOnlyDictionary<string, string>? payload = null)
        {
            var at = occurredAtUtc ?? new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: traceOrdinal ?? ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: at,
                sourceOrigin: origin,
                evidence: new Evidence(
                    kind: EvidenceKind.Raw,
                    identifier: $"raw-{ordinal}",
                    summary: $"Raw signal {ordinal}",
                    rawPointer: null,
                    derivationInputs: null),
                payload: payload);
        }
    }
}
