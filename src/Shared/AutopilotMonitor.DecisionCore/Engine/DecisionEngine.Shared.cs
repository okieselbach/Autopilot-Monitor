using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.DecisionCore.Engine
{
    // Lifecycle + cross-scenario shared handlers. Plan §2.5 partial-class layout.
    public sealed partial class DecisionEngine
    {
        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SessionStarted"/>. Plan §2.7 / §4.x M4.4.4.
        /// <para>
        /// For a fresh session this is the first signal and the state already equals
        /// <see cref="DecisionState.CreateInitial(string, string)"/>. The handler still runs
        /// through the pipeline so the start is recorded as a journal transition (step 0) —
        /// this anchors the Inspector timeline.
        /// </para>
        /// <para>
        /// Also arms the <see cref="DeadlineNames.ClassifierTick"/> deadline up-front
        /// (Plan §4.x M4.4 re-trigger-lücke fix): the legacy reactive arming in
        /// <c>AttachWhiteGloveClassifierEffects</c> only fired on the first WG-relevant
        /// signal, which meant non-WG or late-WG sessions never re-evaluated the classifier.
        /// Arming from SessionStarted guarantees a periodic classifier pass; the existing
        /// <c>hasTick</c> dedup in <c>AttachWhiteGloveClassifierEffects</c> makes the reactive
        /// arm a no-op when a tick is already present.
        /// </para>
        /// <para>
        /// If the engine sees <c>SessionStarted</c> on a state whose stage is already
        /// something other than <see cref="SessionStage.SessionStarted"/>, we treat it as a
        /// defensive no-op (dead-end) rather than silently reinitializing — replay of a
        /// truncated log should fail visibly, not reset hard-won hypotheses.
        /// </para>
        /// </summary>
        private DecisionStep HandleSessionStartedV1(DecisionState state, DecisionSignal signal)
        {
            if (state.Stage != SessionStage.SessionStarted && state.StepIndex != 0)
            {
                var bookkeptDead = BumpStepBookkeeping(state, signal);
                return new DecisionStep(
                    bookkeptDead,
                    BuildDeadEndTransition(
                        state: state,
                        signal: signal,
                        nextStepIndex: bookkeptDead.StepIndex,
                        trigger: nameof(DecisionSignalKind.SessionStarted),
                        deadEndReason: $"session_started_in_active_state:{state.Stage}"),
                    Array.Empty<DecisionEffect>());
            }

            // Arm ClassifierTick up-front so the White-Glove classifier re-evaluates
            // periodically from the very start of the session — Plan §4.x M4.4.4.
            var classifierTick = BuildClassifierTickDeadline(signal.OccurredAtUtc);

            // Stage stays SessionStarted; this transition is the "we saw the start" anchor.
            var newState = state.ToBuilder()
                .WithStage(SessionStage.SessionStarted)
                .WithStepIndex(state.StepIndex + 1)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .AddDeadline(classifierTick)
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.SessionStarted,
                nextStepIndex: newState.StepIndex,
                trigger: nameof(DecisionSignalKind.SessionStarted));

            var effects = new DecisionEffect[]
            {
                new DecisionEffect(DecisionEffectKind.ScheduleDeadline, deadline: classifierTick),
            };

            return new DecisionStep(newState, transition, effects);
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SessionRecovered"/>. Plan §2.7 sonder-case 1.
        /// <para>
        /// In M3.0 scope this is a generic bookkeeping handler. The White-Glove Part-1 →
        /// Part-2 post-reboot transition is implemented in <c>DecisionEngine.WhiteGlovePart2.cs</c>
        /// (M3.4) and takes precedence there when the prior stage was
        /// <see cref="SessionStage.WhiteGloveSealed"/>.
        /// </para>
        /// </summary>
        private DecisionStep HandleSessionRecoveredV1(DecisionState state, DecisionSignal signal)
        {
            // Plan §2.7 sonder-case 1: WhiteGlove Part 1 -> Reboot -> Part 2.
            // If the recovered session was sealed, transition into the Part 2 awaiting-user
            // stage and arm the 24h safety deadline. See DecisionEngine.WhiteGlovePart2.cs.
            if (state.Stage == SessionStage.WhiteGloveSealed)
            {
                return HandleWhiteGlovePart1To2Bridge(state, signal);
            }

            // Otherwise the recovered state is already mid-flight elsewhere; the handler is
            // a neutral "observed a restart" step — stage unchanged, bookkeeping advanced.
            var newState = BumpStepBookkeeping(state, signal);
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: newState.StepIndex,
                trigger: nameof(DecisionSignalKind.SessionRecovered));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.SessionAborted"/>.
        /// <para>
        /// Emitted by the orchestrator, never by a collector. Stage transitions to
        /// <see cref="SessionStage.Failed"/> with <see cref="SessionOutcome.Aborted"/>.
        /// This is a terminal event; the orchestrator uses it to record admin-kill /
        /// override actions cleanly without going through the regular completion paths
        /// (plan §2.7 admin-action audit).
        /// </para>
        /// </summary>
        private DecisionStep HandleSessionAbortedV1(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStage(SessionStage.Failed)
                .WithOutcome(SessionOutcome.Aborted)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .ClearDeadlines()
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: SessionStage.Failed,
                nextStepIndex: nextStep,
                trigger: nameof(DecisionSignalKind.SessionAborted));

            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.AdminPreemptionDetected"/>. Plan §2.7 admin-
        /// action audit; V2 parity PR-B3.
        /// <para>
        /// Emitted by <c>Program.RunAgent</c> when the register-session response carries an
        /// <c>AdminAction</c> value (operator marked the session terminal via the portal before
        /// the agent even started). The signal payload carries
        /// <c>adminOutcome=Succeeded|Failed</c>.
        /// </para>
        /// <para>
        /// Stage transitions to <see cref="SessionStage.Completed"/> (Succeeded) or
        /// <see cref="SessionStage.Failed"/> (anything else); <see cref="SessionOutcome.AdminPreempted"/>
        /// captures the non-enrollment nature of the transition so dashboards + KQL can tell
        /// an admin-override apart from a genuine enrollment_complete/_failed.
        /// </para>
        /// </summary>
        private DecisionStep HandleAdminPreemptionDetectedV1(DecisionState state, DecisionSignal signal)
        {
            var adminOutcome = signal.Payload != null && signal.Payload.TryGetValue("adminOutcome", out var v)
                ? v
                : "Failed"; // defensive default: preemption without outcome is treated as failure.

            var succeeded = string.Equals(adminOutcome, "Succeeded", StringComparison.OrdinalIgnoreCase);
            var toStage = succeeded ? SessionStage.Completed : SessionStage.Failed;
            var eventType = succeeded ? "enrollment_complete" : "enrollment_failed";

            var nextStep = state.StepIndex + 1;
            var newState = state.ToBuilder()
                .WithStage(toStage)
                .WithOutcome(SessionOutcome.AdminPreempted)
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .ClearDeadlines()
                .Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: toStage,
                nextStepIndex: nextStep,
                trigger: $"AdminPreemption:{adminOutcome}");

            var effects = new[]
            {
                new DecisionEffect(
                    DecisionEffectKind.EmitEventTimelineEntry,
                    parameters: new Dictionary<string, string>
                    {
                        ["eventType"] = eventType,
                        ["adminAction"] = adminOutcome,
                        ["source"] = signal.SourceOrigin ?? "register_session_response",
                        ["reason"] = $"Session {adminOutcome.ToLowerInvariant()} by administrator (detected on register-session).",
                    }),
            };

            return new DecisionStep(newState, transition, effects);
        }

        // ============================================================== shared helpers
        // Partial-class shared helpers used by Classic / SelfDeploying / WhiteGlove handlers
        // as they come online in M3.1+ live below. M3.0 establishes the skeleton; the bodies
        // grow with each sub-milestone.

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DeadlineFired"/>. Plan §2.6.
        /// <para>
        /// The payload carries <see cref="SignalPayloadKeys.Deadline"/> = the deadline name
        /// (from <see cref="DeadlineNames"/>). The handler removes the corresponding
        /// <see cref="ActiveDeadline"/> from state and dispatches to a deadline-specific body.
        /// Deadlines for stages that don't yet exist in this sub-milestone (e.g. Part-2
        /// safety) land in the Unknown-Deadline path — they complete bookkeeping without
        /// changing state, which lets M3.0 replay logs that contain future deadline names.
        /// </para>
        /// </summary>
        private DecisionStep HandleDeadlineFiredV1(DecisionState state, DecisionSignal signal)
        {
            var deadlineName = signal.Payload != null && signal.Payload.TryGetValue(SignalPayloadKeys.Deadline, out var n)
                ? n
                : null;

            if (string.IsNullOrEmpty(deadlineName))
            {
                var bookkeptDead = BumpStepBookkeeping(state, signal);
                return new DecisionStep(
                    bookkeptDead,
                    BuildDeadEndTransition(
                        state: state,
                        signal: signal,
                        nextStepIndex: bookkeptDead.StepIndex,
                        trigger: nameof(DecisionSignalKind.DeadlineFired),
                        deadEndReason: "deadline_fired_without_name"),
                    Array.Empty<DecisionEffect>());
            }

            switch (deadlineName)
            {
                case DeadlineNames.HelloSafety:
                    return HandleHelloSafetyDeadlineFired(state, signal);
                case DeadlineNames.DeviceOnlyEspDetection:
                    return HandleDeviceOnlyEspDetectionDeadlineFired(state, signal);
                case DeadlineNames.ClassifierTick:
                    return HandleClassifierTickDeadlineFired(state, signal);
                case DeadlineNames.WhiteGlovePart2Safety:
                    return HandleWhiteGlovePart2SafetyDeadlineFired(state, signal);
                default:
                    // Deadline name not recognized in this sub-milestone. Cancel it from state
                    // and record a neutral taken transition — M3.3+ adds handlers for
                    // ClassifierTick, M3.4 for WhiteGlovePart2Safety, etc.
                    var nextStepIgnored = state.StepIndex + 1;
                    var cancelled = state.ToBuilder()
                        .WithStepIndex(nextStepIgnored)
                        .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                        .CancelDeadline(deadlineName!)
                        .Build();
                    var transitionIgnored = BuildTakenTransition(
                        before: state,
                        signal: signal,
                        toStage: state.Stage,
                        nextStepIndex: nextStepIgnored,
                        trigger: $"DeadlineFired:{deadlineName}");
                    return new DecisionStep(cancelled, transitionIgnored, Array.Empty<DecisionEffect>());
            }
        }

        /// <summary>
        /// Hello-safety deadline fired: the post-ESP grace window expired without a
        /// <see cref="DecisionSignalKind.HelloResolved"/>. Treat as a Hello timeout — the
        /// session completes with <see cref="DecisionState.HelloOutcome"/>=<c>Timeout</c>
        /// if Desktop has also arrived; otherwise we stay in <see cref="SessionStage.AwaitingDesktop"/>
        /// and the downstream <c>DesktopArrived</c> handler completes the session.
        /// </summary>
        private DecisionStep HandleHelloSafetyDeadlineFired(DecisionState state, DecisionSignal signal)
        {
            var nextStep = state.StepIndex + 1;
            var builder = state.ToBuilder()
                .WithStepIndex(nextStep)
                .WithLastAppliedSignalOrdinal(signal.SessionSignalOrdinal)
                .CancelDeadline(DeadlineNames.HelloSafety);

            // If Hello already resolved before the deadline fired (race), the fact is already
            // set; don't overwrite it. Otherwise record the synthetic timeout.
            if (state.HelloResolvedUtc == null)
            {
                builder.HelloResolvedUtc = new SignalFact<DateTime>(signal.OccurredAtUtc, signal.SessionSignalOrdinal);
                builder.HelloOutcome = new SignalFact<string>("Timeout", signal.SessionSignalOrdinal);
            }

            var desktopAlreadyArrived = state.DesktopArrivedUtc != null;
            var toStage = desktopAlreadyArrived ? SessionStage.Completed : SessionStage.AwaitingDesktop;
            builder.WithStage(toStage);

            if (desktopAlreadyArrived)
            {
                builder.WithOutcome(SessionOutcome.EnrollmentComplete);
                builder.ClearDeadlines();
            }

            var newState = builder.Build();

            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: toStage,
                nextStepIndex: nextStep,
                trigger: $"DeadlineFired:{DeadlineNames.HelloSafety}");

            DecisionEffect[] effects = desktopAlreadyArrived
                ? new[] { BuildEnrollmentCompleteEffect() }
                : Array.Empty<DecisionEffect>();

            return new DecisionStep(newState, transition, effects);
        }

        // ============================================================== diagnostic signals
        // Plan §4.x M4.4.3 — close the reducer-handler gap for signals that carry useful
        // telemetry but do NOT influence the state machine. Previously these fell through to
        // HandleUnhandledSignal, which wrote a DeadEnd transition every time — noise in the
        // journal. Now they record as neutral taken transitions; full payload/evidence
        // remains in the SignalLog for Inspector analysis.

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.DeviceInfoCollected"/>. Diagnostic-only —
        /// carries hardware inventory in payload, does not drive stage or hypothesis.
        /// </summary>
        private DecisionStep HandleDeviceInfoCollectedV1(DecisionState state, DecisionSignal signal) =>
            RecordDiagnosticObservation(state, signal, nameof(DecisionSignalKind.DeviceInfoCollected));

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.AutopilotProfileRead"/>. Diagnostic-only —
        /// carries Autopilot profile registry contents, does not drive stage or hypothesis.
        /// </summary>
        private DecisionStep HandleAutopilotProfileReadV1(DecisionState state, DecisionSignal signal) =>
            RecordDiagnosticObservation(state, signal, nameof(DecisionSignalKind.AutopilotProfileRead));

        private DecisionStep RecordDiagnosticObservation(DecisionState state, DecisionSignal signal, string trigger)
        {
            var newState = BumpStepBookkeeping(state, signal);
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: newState.StepIndex,
                trigger: trigger);
            return new DecisionStep(newState, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Handle <see cref="DecisionSignalKind.InformationalEvent"/>. Pure pass-through for
        /// the single-rail refactor (plan §1.3): the signal payload is copied 1:1 into an
        /// <see cref="DecisionEffectKind.EmitEventTimelineEntry"/> effect and the
        /// <see cref="Telemetry.Events.EventTimelineEmitter"/> reconstructs the
        /// <c>EnrollmentEvent</c> from the <see cref="SignalPayloadKeys"/>. DecisionState is
        /// unchanged apart from the standard bookkeeping (<c>StepIndex</c>,
        /// <c>LastAppliedSignalOrdinal</c>) — this handler is deliberately not a decision point.
        /// <para>
        /// <b>Validation</b>: <see cref="SignalPayloadKeys.EventType"/> and
        /// <see cref="SignalPayloadKeys.Source"/> are mandatory. A missing / empty key produces
        /// a <c>DeadEnd</c> transition with reason
        /// <c>informational_event_missing_{key}</c> so the malformed signal is visible in the
        /// transitions table instead of silently reaching the emitter with a throw (kernel
        /// fail-safe would also catch it, but with a less descriptive reason).
        /// </para>
        /// <para>
        /// <b>Promotion path</b>: if a sender later needs a specific pass-through to influence
        /// a decision, swap the signal kind for a dedicated one (e.g.
        /// <c>PlatformScriptCompleted</c>) and add a state-mutating reducer case. The emission
        /// contract and UI shape stay identical because the emitter still receives the same
        /// parameter keys.
        /// </para>
        /// </summary>
        private DecisionStep HandleInformationalEventV1(DecisionState state, DecisionSignal signal)
        {
            var payload = signal.Payload;

            if (payload == null
                || !payload.TryGetValue(SignalPayloadKeys.EventType, out var eventType)
                || string.IsNullOrEmpty(eventType))
            {
                return BuildInformationalEventDeadEnd(state, signal, SignalPayloadKeys.EventType);
            }
            if (!payload.TryGetValue(SignalPayloadKeys.Source, out var source)
                || string.IsNullOrEmpty(source))
            {
                return BuildInformationalEventDeadEnd(state, signal, SignalPayloadKeys.Source);
            }

            var newState = BumpStepBookkeeping(state, signal);
            var transition = BuildTakenTransition(
                before: state,
                signal: signal,
                toStage: state.Stage,
                nextStepIndex: newState.StepIndex,
                trigger: nameof(DecisionSignalKind.InformationalEvent));

            // Effect parameters are the signal payload verbatim. EventTimelineEmitter extracts
            // the reserved top-level keys (eventType, source, severity, message, phase,
            // immediateUpload) and keeps the rest as Data entries.
            var effect = new DecisionEffect(
                kind: DecisionEffectKind.EmitEventTimelineEntry,
                parameters: payload);

            return new DecisionStep(newState, transition, new[] { effect });
        }

        private DecisionStep BuildInformationalEventDeadEnd(DecisionState state, DecisionSignal signal, string missingKey)
        {
            var bookkept = BumpStepBookkeeping(state, signal);
            var transition = BuildDeadEndTransition(
                state: state,
                signal: signal,
                nextStepIndex: bookkept.StepIndex,
                trigger: nameof(DecisionSignalKind.InformationalEvent),
                deadEndReason: $"informational_event_missing_{missingKey}");
            return new DecisionStep(bookkept, transition, Array.Empty<DecisionEffect>());
        }

        /// <summary>
        /// Determine the user-visible enrollment phase implied by an ESP phase-change signal.
        /// Plan §2.3 phase-fact mapping. Populated in M3.1 as Classic handlers come online.
        /// </summary>
        internal static EnrollmentPhase MapEspPhaseToEnrollmentPhase(string rawPhase)
        {
            if (string.IsNullOrEmpty(rawPhase)) return EnrollmentPhase.Unknown;
            return rawPhase switch
            {
                "DeviceSetup" => EnrollmentPhase.DeviceSetup,
                "AccountSetup" => EnrollmentPhase.AccountSetup,
                "FinalizingSetup" => EnrollmentPhase.FinalizingSetup,
                "Finalizing" => EnrollmentPhase.FinalizingSetup,
                "Complete" => EnrollmentPhase.Complete,
                _ => EnrollmentPhase.Unknown,
            };
        }
    }
}
