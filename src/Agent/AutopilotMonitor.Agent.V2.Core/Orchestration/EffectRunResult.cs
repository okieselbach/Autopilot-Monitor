#nullable enable
using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Ergebnis eines <see cref="IEffectRunner.RunAsync"/>-Laufs. Plan §2.7b.
    /// <para>
    /// Der Orchestrator (M4.4) liest:
    /// <list type="bullet">
    ///   <item><see cref="SessionMustAbort"/> → ist rein Observability. Codex follow-up #2:
    ///         beim Setzen dieses Flags hat der EffectRunner bereits ein synthetisches
    ///         <c>EffectInfrastructureFailure</c>-Signal an den Ingress gestellt; der Reducer
    ///         terminiert die Session auf dem nächsten Step (Stage=Failed, Outcome=EnrollmentFailed,
    ///         <c>enrollment_failed</c>-Timeline-Event). Der Caller muss nur loggen.</item>
    ///   <item><see cref="Failures"/> → schreibt pro Fehler eine <c>effect_failure</c>-Transition ins Journal</item>
    ///   <item><see cref="ClassifierInvocations"/> / <see cref="ClassifierSkippedByAntiLoop"/> → Observability</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class EffectRunResult
    {
        public EffectRunResult(
            bool sessionMustAbort,
            string? abortReason,
            IReadOnlyList<EffectFailure> failures,
            int classifierInvocations,
            int classifierSkippedByAntiLoop)
        {
            SessionMustAbort = sessionMustAbort;
            AbortReason = abortReason;
            Failures = failures ?? Array.Empty<EffectFailure>();
            ClassifierInvocations = classifierInvocations;
            ClassifierSkippedByAntiLoop = classifierSkippedByAntiLoop;
        }

        public bool SessionMustAbort { get; }
        public string? AbortReason { get; }
        public IReadOnlyList<EffectFailure> Failures { get; }
        public int ClassifierInvocations { get; }
        public int ClassifierSkippedByAntiLoop { get; }

        public bool Success => !SessionMustAbort && Failures.Count == 0;

        public static EffectRunResult Empty() =>
            new EffectRunResult(false, null, Array.Empty<EffectFailure>(), 0, 0);
    }
}
