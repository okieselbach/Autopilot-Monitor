using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Immutable journal record for a single reducer step. Plan §2.8.
    /// <para>
    /// <b>Taken=true</b>: a state-changing transition. <b>Taken=false</b>: a dead-end
    /// (trigger arrived, at least one guard failed — recorded so the Inspector can show the
    /// blocked path in the decision graph, §2.9).
    /// </para>
    /// <para>
    /// Cross-type correlation: <see cref="SignalOrdinalRef"/> links to the triggering signal,
    /// <see cref="EmittedEventSequences"/> lists Event.Sequence values of any timeline events
    /// this step emitted, and <see cref="SessionTraceOrdinal"/> is the session-wide monotonic
    /// counter shared across Event / Signal / Transition (plan §2.2 correlation IDs).
    /// </para>
    /// </summary>
    public sealed class DecisionTransition
    {
        public DecisionTransition(
            int stepIndex,
            long sessionTraceOrdinal,
            long signalOrdinalRef,
            DateTime occurredAtUtc,
            string trigger,
            SessionStage fromStage,
            SessionStage toStage,
            bool taken,
            string? deadEndReason,
            string reducerVersion,
            IReadOnlyList<GuardReport>? guards = null,
            IReadOnlyList<long>? emittedEventSequences = null,
            ClassifierVerdict? classifierVerdict = null,
            string? errorMessage = null,
            string? stackTraceHash = null)
        {
            if (stepIndex < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stepIndex),
                    "StepIndex must be non-negative.");
            }

            if (string.IsNullOrEmpty(trigger))
            {
                throw new ArgumentException("Trigger is mandatory.", nameof(trigger));
            }

            if (string.IsNullOrEmpty(reducerVersion))
            {
                throw new ArgumentException("ReducerVersion is mandatory.", nameof(reducerVersion));
            }

            StepIndex = stepIndex;
            SessionTraceOrdinal = sessionTraceOrdinal;
            SignalOrdinalRef = signalOrdinalRef;
            OccurredAtUtc = occurredAtUtc;
            Trigger = trigger;
            FromStage = fromStage;
            ToStage = toStage;
            Taken = taken;
            DeadEndReason = deadEndReason;
            ReducerVersion = reducerVersion;
            Guards = guards ?? Array.Empty<GuardReport>();
            EmittedEventSequences = emittedEventSequences ?? Array.Empty<long>();
            ClassifierVerdict = classifierVerdict;
            ErrorMessage = errorMessage;
            StackTraceHash = stackTraceHash;
        }

        public int StepIndex { get; }

        public long SessionTraceOrdinal { get; }

        /// <summary>Plan §2.2 cross-reference — which signal caused this transition.</summary>
        public long SignalOrdinalRef { get; }

        public DateTime OccurredAtUtc { get; }

        public string Trigger { get; }

        public SessionStage FromStage { get; }

        public SessionStage ToStage { get; }

        public bool Taken { get; }

        /// <summary>
        /// Non-null when <see cref="Taken"/> is false. Plan §2.5 also uses well-known reasons:
        /// <c>"reducer_exception"</c>, <c>"effect_failure"</c>, <c>"hybrid_reboot_gate_blocking"</c>, …
        /// </summary>
        public string? DeadEndReason { get; }

        public string ReducerVersion { get; }

        public IReadOnlyList<GuardReport> Guards { get; }

        /// <summary>
        /// Plan §2.2 cross-reference — Event.Sequence values this transition emitted as
        /// effects. <b>Currently always empty</b> (Codex follow-up #3): the journal record
        /// is appended before effects run, so the Event.Sequence values are not yet known
        /// at construction time. The forward link is instead carried by
        /// <c>EnrollmentEvent.CausedByTransitionStepIndex</c> — query the Events table by
        /// StepIndex for the "events this step emitted" lookup. The property stays on the
        /// type for journal backwards-compatibility and for a potential future sidecar fill.
        /// </summary>
        public IReadOnlyList<long> EmittedEventSequences { get; }

        public ClassifierVerdict? ClassifierVerdict { get; }

        /// <summary>Set when <see cref="DeadEndReason"/> == "reducer_exception" (plan §2.5 fail-safe).</summary>
        public string? ErrorMessage { get; }

        /// <summary>SHA256-short hash of the original stack trace. Prevents unbounded PII in the journal.</summary>
        public string? StackTraceHash { get; }
    }
}
