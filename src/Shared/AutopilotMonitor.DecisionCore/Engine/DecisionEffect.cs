using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Action kind an <see cref="DecisionEffect"/> requests the EffectRunner to execute. Plan §2.5 / §2.7b.
    /// </summary>
    public enum DecisionEffectKind
    {
        ScheduleDeadline,
        CancelDeadline,
        RunClassifier,
        EmitEventTimelineEntry,
        PersistSnapshot,
    }

    /// <summary>
    /// Immutable effect record. Plan §2.5.
    /// <para>
    /// Effects are the <b>only</b> side-channel through which the reducer talks to the outside world.
    /// The reducer itself is pure (<c>(old, signal) → (new, transition, effects)</c>); the EffectRunner
    /// performs I/O (Deadlines, Classifier, Event-Emit, Persist) per plan §2.7b error strategy:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>Transient</b> (retry, 100/400/1600 ms): <see cref="DecisionEffectKind.EmitEventTimelineEntry"/>, <see cref="DecisionEffectKind.PersistSnapshot"/></item>
    ///   <item><b>Critical</b> (session abort on register failure): <see cref="DecisionEffectKind.ScheduleDeadline"/>, <see cref="DecisionEffectKind.CancelDeadline"/></item>
    ///   <item><b>Optional</b> (best-effort, no abort): <see cref="DecisionEffectKind.RunClassifier"/></item>
    /// </list>
    /// </summary>
    public sealed class DecisionEffect
    {
        public DecisionEffect(
            DecisionEffectKind kind,
            IReadOnlyDictionary<string, string>? parameters = null,
            ActiveDeadline? deadline = null,
            string? cancelDeadlineName = null,
            string? classifierId = null,
            object? classifierSnapshot = null)
        {
            Kind = kind;
            Parameters = parameters;
            Deadline = deadline;
            CancelDeadlineName = cancelDeadlineName;
            ClassifierId = classifierId;
            ClassifierSnapshot = classifierSnapshot;
        }

        public DecisionEffectKind Kind { get; }

        public IReadOnlyDictionary<string, string>? Parameters { get; }

        /// <summary>Set for <see cref="DecisionEffectKind.ScheduleDeadline"/>.</summary>
        public ActiveDeadline? Deadline { get; }

        /// <summary>Set for <see cref="DecisionEffectKind.CancelDeadline"/>.</summary>
        public string? CancelDeadlineName { get; }

        /// <summary>Set for <see cref="DecisionEffectKind.RunClassifier"/>.</summary>
        public string? ClassifierId { get; }

        /// <summary>Set for <see cref="DecisionEffectKind.RunClassifier"/> — classifier-specific snapshot object.</summary>
        public object? ClassifierSnapshot { get; }
    }
}
