using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Coverage for <see cref="DecisionAuditTrailBuilder"/> and the six DecisionEngine emission
    /// sites that previously published terminal / state-changing timeline events with empty
    /// or near-empty <c>EnrollmentEvent.Data</c>: <c>whiteglove_complete</c>,
    /// <c>whiteglove_resumed</c>, <c>whiteglove_part2_complete</c>, <c>enrollment_failed</c>
    /// (Part-2 safety / EspTerminalFailure / EffectInfrastructureFailure). Each test exercises
    /// the reducer's typed-payload contract end-to-end so a regression to "empty Data on the
    /// wire" is caught at the engine boundary instead of in a UI smoke test.
    /// </summary>
    public sealed class DecisionAuditTrailTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 4, 30, 8, 0, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null,
            string sourceOrigin = "test")
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: sourceOrigin,
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        private static DecisionEffect SingleTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.Single(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        // ============================================================ builder unit tests

        [Fact]
        public void Build_emits_mandatory_anchor_fields_for_minimal_state()
        {
            var state = DecisionState.CreateInitial("s", "t").ToBuilder()
                .WithStepIndex(7)
                .WithLastAppliedSignalOrdinal(42)
                .Build();

            var data = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.SessionStarted,
                trigger: "Test:Trigger");

            Assert.Equal("DecisionEngine", data["decisionSource"]);
            Assert.Equal("Test:Trigger", data["trigger"]);
            Assert.Equal(nameof(SessionStage.SessionStarted), data["sessionStage"]);
            Assert.Equal(7, data["stepIndex"]);
            Assert.Equal(42L, data["signalOrdinal"]);
            Assert.IsType<List<string>>(data["signalsSeen"]);
            Assert.IsType<Dictionary<string, object>>(data["signalEvidence"]);
            Assert.IsType<Dictionary<string, object>>(data["signalTimestamps"]);
            Assert.IsType<Dictionary<string, object>>(data["scenario"]);
            Assert.False(data.ContainsKey("classifier"));
            Assert.False(data.ContainsKey("classifierInputs"));
            Assert.False(data.ContainsKey("reason"));
        }

        [Fact]
        public void Build_omits_signalOrdinal_when_state_has_no_applied_signal()
        {
            // CreateInitial leaves LastAppliedSignalOrdinal at -1 — the builder should NOT
            // forge a "signalOrdinal=-1" entry that would mislead the Inspector.
            var state = DecisionState.CreateInitial("s", "t");

            var data = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.SessionStarted,
                trigger: "Test:NoSignal");

            Assert.False(data.ContainsKey("signalOrdinal"));
        }

        [Fact]
        public void Build_includes_failureReason_only_when_provided()
        {
            var state = DecisionState.CreateInitial("s", "t");

            var withReason = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.Failed,
                trigger: "Test:Failure",
                failureReason: "ime_pattern_failure");
            Assert.Equal("ime_pattern_failure", withReason["reason"]);

            var withoutReason = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.Failed,
                trigger: "Test:Failure");
            Assert.False(withoutReason.ContainsKey("reason"));
        }

        [Fact]
        public void Build_classifier_section_only_appears_when_verdict_supplied()
        {
            var state = DecisionState.CreateInitial("s", "t");
            var verdict = new ClassifierVerdictInfo("WhiteGloveSealing", "Confirmed", 80, "shellcore_alone", "abc123");

            var data = DecisionAuditTrailBuilder.Build(
                postState: state,
                decidedStage: SessionStage.WhiteGloveSealed,
                trigger: "Test:Classifier",
                classifier: verdict);

            var classifierBlock = Assert.IsType<Dictionary<string, object>>(data["classifier"]);
            Assert.Equal("WhiteGloveSealing", classifierBlock["id"]);
            Assert.Equal("Confirmed", classifierBlock["level"]);
            Assert.Equal(80, classifierBlock["score"]);
            Assert.Equal("shellcore_alone", classifierBlock["reason"]);
            Assert.Equal("abc123", classifierBlock["inputHash"]);
        }

        [Fact]
        public void Build_classifierInputs_flattens_snapshot_record_to_camelCased_dict()
        {
            var snapshot = new WhiteGloveSealingSnapshot(
                shellCoreWhiteGloveSuccessSeen: true,
                whiteGloveSealingPatternSeen: true,
                aadJoinedWithUser: false,
                desktopArrived: false,
                helloResolved: false,
                hasAccountSetupActivity: false,
                isDeviceOnlyDeploymentHypothesis: false,
                systemRebootUtc: null,
                currentEnrollmentPhase: null);

            var data = DecisionAuditTrailBuilder.Build(
                postState: DecisionState.CreateInitial("s", "t"),
                decidedStage: SessionStage.WhiteGloveSealed,
                trigger: "Test:Inputs",
                classifierInputs: snapshot);

            var inputs = Assert.IsType<Dictionary<string, object>>(data["classifierInputs"]);
            Assert.Equal(true, inputs["shellCoreWhiteGloveSuccessSeen"]);
            Assert.Equal(true, inputs["whiteGloveSealingPatternSeen"]);
            Assert.Equal(false, inputs["aadJoinedWithUser"]);
            // Null nullable properties are skipped, not emitted as null sentinels.
            Assert.False(inputs.ContainsKey("systemRebootUtc"));
            Assert.False(inputs.ContainsKey("currentEnrollmentPhase"));
        }

        [Fact]
        public void VerdictFromSignalPayload_returns_null_for_missing_keys()
        {
            Assert.Null(DecisionAuditTrailBuilder.VerdictFromSignalPayload(null));
            Assert.Null(DecisionAuditTrailBuilder.VerdictFromSignalPayload(new Dictionary<string, string>()));
            Assert.Null(DecisionAuditTrailBuilder.VerdictFromSignalPayload(
                new Dictionary<string, string> { ["classifier"] = "X" })); // no level
        }

        [Fact]
        public void VerdictFromSignalPayload_parses_full_payload()
        {
            var v = DecisionAuditTrailBuilder.VerdictFromSignalPayload(
                new Dictionary<string, string>
                {
                    ["classifier"] = "WhiteGloveSealing",
                    ["level"] = "Confirmed",
                    ["score"] = "80",
                    ["reason"] = "shellcore_alone",
                    ["inputHash"] = "abc123",
                });

            Assert.NotNull(v);
            Assert.Equal("WhiteGloveSealing", v!.Id);
            Assert.Equal("Confirmed", v.Level);
            Assert.Equal(80, v.Score);
            Assert.Equal("shellcore_alone", v.Reason);
            Assert.Equal("abc123", v.InputHash);
        }

        // ============================================================ whiteglove_complete

        [Fact]
        public void WhiteGloveComplete_carries_audit_trail_with_classifier_verdict_and_inputs()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-wg", "tenant-wg");
            // Drive through SessionStarted so the StepIndex / LastAppliedSignalOrdinal are real.
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            // Observe ShellCore success — the inline-WG fast path: a single high-confidence signal
            // is enough for the classifier to seal at 80/100.
            var shellSignal = MakeSignal(1, DecisionSignalKind.WhiteGloveShellCoreSuccess, T0.AddMinutes(5));
            state = engine.Reduce(state, shellSignal).NewState;

            // The reducer emitted a RunClassifier effect; the harness here synthesizes the
            // resulting verdict signal directly so the WG verdict-applier path runs.
            var verdictSignal = MakeSignal(2, DecisionSignalKind.ClassifierVerdictIssued, T0.AddMinutes(5),
                new Dictionary<string, string>
                {
                    ["classifier"] = WhiteGloveSealingClassifier.ClassifierId,
                    ["level"] = "Confirmed",
                    ["score"] = "80",
                    ["reason"] = "shellcore_alone",
                    ["inputHash"] = "deadbeef",
                });

            var step = engine.Reduce(state, verdictSignal);
            Assert.Equal(SessionStage.WhiteGloveSealed, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "whiteglove_complete");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            Assert.Equal("DecisionEngine", payload["decisionSource"]);
            Assert.Equal($"ClassifierVerdictIssued:{WhiteGloveSealingClassifier.ClassifierId}:Confirmed", payload["trigger"]);
            Assert.Equal(nameof(SessionStage.WhiteGloveSealed), payload["sessionStage"]);

            var signalsSeen = Assert.IsType<List<string>>(payload["signalsSeen"]);
            Assert.Contains("shellcore_whiteglove_success", signalsSeen);

            var classifier = Assert.IsType<Dictionary<string, object>>(payload["classifier"]);
            Assert.Equal(WhiteGloveSealingClassifier.ClassifierId, classifier["id"]);
            Assert.Equal("Confirmed", classifier["level"]);
            Assert.Equal(80, classifier["score"]);
            Assert.Equal("deadbeef", classifier["inputHash"]);

            var inputs = Assert.IsType<Dictionary<string, object>>(payload["classifierInputs"]);
            Assert.Equal(true, inputs["shellCoreWhiteGloveSuccessSeen"]);
            Assert.Equal(false, inputs["aadJoinedWithUser"]);

            var scenario = Assert.IsType<Dictionary<string, object>>(payload["scenario"]);
            Assert.Equal(EnrollmentMode.WhiteGlove.ToString(), scenario["mode"]);
        }

        // ============================================================ whiteglove_resumed

        [Fact]
        public void WhiteGloveResumed_attaches_audit_trail_alongside_legacy_scalar_parameters()
        {
            var engine = new DecisionEngine();
            // Sealed Part-1 state recovered after reboot.
            var sealedState = DecisionState.CreateInitial("sess-wgr", "tenant-wgr")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveSealed)
                .Build();
            var resumeUtc = new DateTime(2026, 4, 30, 9, 0, 0, DateTimeKind.Utc);
            var step = engine.Reduce(sealedState, MakeSignal(
                10, DecisionSignalKind.SessionRecovered, resumeUtc, sourceOrigin: "EnrollmentOrchestrator"));

            var effect = SingleTimelineEffect(step, "whiteglove_resumed");

            // Legacy scalar parameters preserved (existing assertions in ReducerTimelineEffectTests).
            Assert.Equal(resumeUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                effect.Parameters!["resumedAtUtc"]);
            Assert.Equal("EnrollmentOrchestrator", effect.Parameters["sourceOrigin"]);

            // Newly-attached audit trail.
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);
            Assert.Equal("SessionRecovered:WhiteGloveSealed->AwaitingUserSignIn", payload["trigger"]);
            Assert.Equal(nameof(SessionStage.WhiteGloveAwaitingUserSignIn), payload["sessionStage"]);
            // The bridge-specific scalars are present in the typed payload too.
            Assert.Equal(resumeUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture), payload["resumedAtUtc"]);
            Assert.Equal("EnrollmentOrchestrator", payload["sourceOrigin"]);
        }

        // ============================================================ whiteglove_part2_complete

        [Fact]
        public void WhiteGlovePart2Complete_carries_audit_trail_with_part2_verdict_and_facts()
        {
            var engine = new DecisionEngine();
            // Establish Part-2 awaiting state with all four Part-2 facts in place so the
            // verdict applier transitions to WhiteGloveCompletedPart2.
            var state = DecisionState.CreateInitial("sess-wg2", "tenant-wg2")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveAwaitingUserSignIn)
                .Build();
            state = engine.Reduce(state, MakeSignal(20, DecisionSignalKind.UserAadSignInComplete, T0.AddHours(2))).NewState;
            state = engine.Reduce(state, MakeSignal(21, DecisionSignalKind.HelloResolvedPart2, T0.AddHours(2).AddSeconds(10))).NewState;
            state = engine.Reduce(state, MakeSignal(22, DecisionSignalKind.DesktopArrivedPart2, T0.AddHours(2).AddSeconds(20))).NewState;
            state = engine.Reduce(state, MakeSignal(23, DecisionSignalKind.AccountSetupCompletedPart2, T0.AddHours(2).AddSeconds(30))).NewState;

            var verdictSignal = MakeSignal(24, DecisionSignalKind.ClassifierVerdictIssued, T0.AddHours(2).AddMinutes(1),
                new Dictionary<string, string>
                {
                    ["classifier"] = WhiteGlovePart2CompletionClassifier.ClassifierId,
                    ["level"] = "Confirmed",
                    ["score"] = "100",
                    ["reason"] = "all_part2_facts_present",
                    ["inputHash"] = "feedface",
                });

            var step = engine.Reduce(state, verdictSignal);
            Assert.Equal(SessionStage.WhiteGloveCompletedPart2, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "whiteglove_part2_complete");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            Assert.Equal($"ClassifierVerdictIssued:{WhiteGlovePart2CompletionClassifier.ClassifierId}:Confirmed", payload["trigger"]);
            Assert.Equal(nameof(SessionStage.WhiteGloveCompletedPart2), payload["sessionStage"]);

            var signalsSeen = Assert.IsType<List<string>>(payload["signalsSeen"]);
            Assert.Contains("user_aad_sign_in_complete", signalsSeen);
            Assert.Contains("hello_resolved_part2", signalsSeen);
            Assert.Contains("desktop_arrived_part2", signalsSeen);
            Assert.Contains("account_setup_completed_part2", signalsSeen);

            var classifier = Assert.IsType<Dictionary<string, object>>(payload["classifier"]);
            Assert.Equal(WhiteGlovePart2CompletionClassifier.ClassifierId, classifier["id"]);
            Assert.Equal("feedface", classifier["inputHash"]);
        }

        // ============================================================ enrollment_failed (Part-2 safety)

        [Fact]
        public void Part2SafetyDeadline_failure_event_carries_audit_trail_with_part2_user_absent_reason()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-p2s", "tenant-p2s")
                .ToBuilder()
                .WithStage(SessionStage.WhiteGloveAwaitingUserSignIn)
                .AddDeadline(new ActiveDeadline(
                    name: DeadlineNames.WhiteGlovePart2Safety,
                    dueAtUtc: T0.AddHours(24),
                    firesSignalKind: DecisionSignalKind.DeadlineFired,
                    firesPayload: new Dictionary<string, string>
                    {
                        [SignalPayloadKeys.Deadline] = DeadlineNames.WhiteGlovePart2Safety,
                    }))
                .Build();

            var step = engine.Reduce(state, MakeSignal(
                30, DecisionSignalKind.DeadlineFired, T0.AddHours(24),
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.WhiteGlovePart2Safety }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "enrollment_failed");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            Assert.Equal($"DeadlineFired:{DeadlineNames.WhiteGlovePart2Safety}", payload["trigger"]);
            Assert.Equal("part2_user_absent", payload["reason"]);
            Assert.Equal(nameof(SessionStage.Failed), payload["sessionStage"]);
        }

        // ============================================================ enrollment_failed (ESP terminal)

        [Fact]
        public void EspTerminalFailure_failure_event_carries_audit_trail_with_signal_reason()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-esp", "tenant-esp");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var step = engine.Reduce(state, MakeSignal(
                40, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(15),
                new Dictionary<string, string> { ["reason"] = "policy_apply_timeout" }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "enrollment_failed");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            Assert.Equal(nameof(DecisionSignalKind.EspTerminalFailure), payload["trigger"]);
            Assert.Equal("policy_apply_timeout", payload["reason"]);
        }

        // ============================================================ enrollment_failed (effect infra)

        [Fact]
        public void EffectInfrastructureFailure_failure_event_carries_audit_trail_with_signal_reason()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("sess-eff", "tenant-eff");
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;

            var step = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EffectInfrastructureFailure, T0.AddMinutes(2),
                new Dictionary<string, string> { ["reason"] = "deadline_scheduler_offline" }));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);

            var effect = SingleTimelineEffect(step, "enrollment_failed");
            var payload = Assert.IsType<Dictionary<string, object>>(effect.TypedPayload);

            Assert.Equal(nameof(DecisionSignalKind.EffectInfrastructureFailure), payload["trigger"]);
            Assert.Equal("deadline_scheduler_offline", payload["reason"]);
        }
    }
}
