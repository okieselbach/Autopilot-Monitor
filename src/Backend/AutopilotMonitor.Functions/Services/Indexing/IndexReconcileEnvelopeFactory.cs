using System.Collections.Generic;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Indexing
{
    /// <summary>
    /// Pure factory that lifts committed primary rows into <see cref="IndexReconcileEnvelope"/>s
    /// for the <c>telemetry-index-reconcile</c> queue (Plan §2.8, §M5.d). State-less + zero-IO
    /// so callers can unit-test mapping behaviour against canned inputs without a live queue.
    /// <para>
    /// One envelope per primary row. The handler (M5.d.3) decides which of the 0–3 index
    /// tables to write, so the decision here is shape-only: copy discriminators + back-refs.
    /// </para>
    /// </summary>
    internal static class IndexReconcileEnvelopeFactory
    {
        public static IndexReconcileEnvelope FromSignal(SignalRecord record)
        {
            return new IndexReconcileEnvelope
            {
                EnvelopeVersion      = "1",
                SourceKind           = "Signal",
                TenantId             = record.TenantId,
                SessionId            = record.SessionId,
                OccurredAtUtc        = record.OccurredAtUtc,
                SessionSignalOrdinal = record.SessionSignalOrdinal,
                SignalKind           = record.Kind,
                SourceOrigin         = record.SourceOrigin,
            };
        }

        public static IndexReconcileEnvelope FromDecisionTransition(DecisionTransitionRecord record)
        {
            return new IndexReconcileEnvelope
            {
                EnvelopeVersion           = "1",
                SourceKind                = "DecisionTransition",
                TenantId                  = record.TenantId,
                SessionId                 = record.SessionId,
                OccurredAtUtc             = record.OccurredAtUtc,
                StepIndex                 = record.StepIndex,
                FromStage                 = record.FromStage,
                ToStage                   = record.ToStage,
                Taken                     = record.Taken,
                IsTerminal                = record.IsTerminal,
                DeadEndReason             = record.DeadEndReason,
                ClassifierVerdictId       = record.ClassifierVerdictId,
                ClassifierHypothesisLevel = record.ClassifierHypothesisLevel,
            };
        }

        /// <summary>
        /// Builds envelopes for both kinds in one pass, preserving input order. Convenience for
        /// <see cref="AutopilotMonitor.Functions.Functions.Ingest.IngestTelemetryFunction"/>,
        /// whose per-request batch mixes signals + transitions.
        /// </summary>
        public static List<IndexReconcileEnvelope> BuildBatch(
            IReadOnlyList<SignalRecord> signals,
            IReadOnlyList<DecisionTransitionRecord> transitions)
        {
            var result = new List<IndexReconcileEnvelope>(
                (signals?.Count ?? 0) + (transitions?.Count ?? 0));

            if (signals != null)
            {
                foreach (var s in signals) result.Add(FromSignal(s));
            }
            if (transitions != null)
            {
                foreach (var t in transitions) result.Add(FromDecisionTransition(t));
            }
            return result;
        }
    }
}
