using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Codex follow-up #6 — semantic replay coverage for <see cref="ReducerVerifier"/>.
/// Structural-only cases live in <see cref="ReducerVerifierTests"/>; this file pins the
/// contract that when preconditions hold (contiguous ordinals / matching ReducerVersion)
/// the verifier folds the real signal stream through a live <see cref="DecisionEngine"/>
/// and reports divergence.
/// </summary>
public class ReducerVerifierSemanticReplayTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    /// <summary>The live backend reducer version — matches what Verify uses for drift checks.</summary>
    private static string CurrentReducerVersion { get; } = new DecisionEngine().ReducerVersion;

    // ---- Seed helpers ---------------------------------------------------------------

    /// <summary>
    /// Build a canonical 3-signal stream + a matching journal by folding the live reducer
    /// over them. The helper is the "truth" data for agreement tests — anything that differs
    /// from this output in a test fixture is by definition divergence.
    /// </summary>
    private static (SignalRecord[] signals, DecisionTransitionRecord[] transitions, DecisionState finalState)
        BuildAgreedStream()
    {
        var sigs = new[]
        {
            MakeSignal(0, DecisionSignalKind.SessionStarted),
            MakeSignal(1, DecisionSignalKind.AppInstallCompleted),
            MakeSignal(2, DecisionSignalKind.AppInstallCompleted),
        };

        var engine = new DecisionEngine();
        var state = DecisionState.CreateInitial(TenantId, SessionId);
        var transitionRecords = new DecisionTransitionRecord[sigs.Length];

        for (var i = 0; i < sigs.Length; i++)
        {
            var step = engine.Reduce(state, sigs[i]);
            state = step.NewState;

            transitionRecords[i] = new DecisionTransitionRecord
            {
                TenantId = TenantId,
                SessionId = SessionId,
                StepIndex = step.Transition.StepIndex,
                SessionTraceOrdinal = step.Transition.SessionTraceOrdinal,
                SignalOrdinalRef = step.Transition.SignalOrdinalRef,
                OccurredAtUtc = step.Transition.OccurredAtUtc,
                Trigger = step.Transition.Trigger,
                FromStage = step.Transition.FromStage.ToString(),
                ToStage = step.Transition.ToStage.ToString(),
                Taken = step.Transition.Taken,
                DeadEndReason = step.Transition.DeadEndReason,
                ReducerVersion = step.Transition.ReducerVersion,
                PayloadJson = TransitionSerializer.Serialize(step.Transition),
            };
        }

        var signalRecords = new SignalRecord[sigs.Length];
        for (var i = 0; i < sigs.Length; i++)
        {
            signalRecords[i] = new SignalRecord
            {
                TenantId = TenantId,
                SessionId = SessionId,
                SessionSignalOrdinal = sigs[i].SessionSignalOrdinal,
                SessionTraceOrdinal = sigs[i].SessionTraceOrdinal,
                Kind = sigs[i].Kind.ToString(),
                KindSchemaVersion = sigs[i].KindSchemaVersion,
                OccurredAtUtc = sigs[i].OccurredAtUtc,
                SourceOrigin = sigs[i].SourceOrigin,
                PayloadJson = SignalSerializer.Serialize(sigs[i]),
            };
        }

        return (signalRecords, transitionRecords, state);
    }

    private static DecisionSignal MakeSignal(long ordinal, DecisionSignalKind kind) =>
        new DecisionSignal(
            sessionSignalOrdinal: ordinal,
            sessionTraceOrdinal: ordinal,
            kind: kind,
            kindSchemaVersion: 1,
            occurredAtUtc: new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc).AddSeconds(ordinal),
            sourceOrigin: "replay-test",
            evidence: new Evidence(EvidenceKind.Synthetic, $"replay:ord-{ordinal}", "test"));

    // ---- Skip conditions ------------------------------------------------------------

    [Fact]
    public void Replay_skipped_when_stored_reducer_version_drifts()
    {
        var (signals, transitions, _) = BuildAgreedStream();
        // Override ReducerVersion on the first transition to simulate drift.
        transitions[0] = CloneWithReducerVersion(transitions[0], "0.9.0.0");

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.False(report.SemanticReplayPerformed);
        Assert.Equal("reducer_version_drift", report.SemanticReplaySkipReason);
        Assert.Contains(report.Issues, i => i.Kind == "replay_skipped");
    }

    [Fact]
    public void Replay_skipped_when_signal_stream_has_ordinal_gap()
    {
        var (signals, transitions, _) = BuildAgreedStream();
        // Drop the middle signal → ordinal gap.
        var gapped = new[] { signals[0], signals[2] };

        var report = ReducerVerifier.Verify(TenantId, SessionId, gapped, transitions, CurrentReducerVersion);

        Assert.False(report.SemanticReplayPerformed);
        Assert.Equal("non_contiguous_signal_ordinals", report.SemanticReplaySkipReason);
    }

    [Fact]
    public void Replay_skipped_when_transition_journal_has_step_index_gap()
    {
        var (signals, transitions, _) = BuildAgreedStream();
        var gapped = new[] { transitions[0], transitions[2] }; // StepIndex gap

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, gapped, CurrentReducerVersion);

        Assert.False(report.SemanticReplayPerformed);
        Assert.Equal("non_contiguous_step_indices", report.SemanticReplaySkipReason);
    }

    // ---- Agreement + divergence ----------------------------------------------------

    [Fact]
    public void Replay_perfect_agreement_marks_final_stage_matches_and_zero_divergence()
    {
        var (signals, transitions, finalState) = BuildAgreedStream();

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.True(report.SemanticReplayPerformed);
        Assert.Null(report.SemanticReplaySkipReason);
        Assert.True(report.SemanticReplayFinalStageMatches);
        Assert.Equal(finalState.Stage.ToString(), report.ReplayedFinalStage);
        Assert.Equal(0, report.TransitionDivergenceCount);
        Assert.DoesNotContain(report.Issues, i => i.Kind == "replay_divergence");
        Assert.DoesNotContain(report.Issues, i => i.Kind == "replay_final_stage_mismatch");
    }

    [Fact]
    public void Replay_divergence_reports_issue_when_stored_trigger_was_tampered()
    {
        var (signals, transitions, _) = BuildAgreedStream();
        // Tamper with a stored transition: swap Trigger. Replay will produce the
        // authentic trigger and divergence is reported.
        transitions[1] = CloneWithTrigger(transitions[1], "TamperedTrigger");

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.True(report.SemanticReplayPerformed);
        Assert.Equal(1, report.TransitionDivergenceCount);
        var issue = Assert.Single(report.Issues, i => i.Kind == "replay_divergence");
        Assert.Contains("TamperedTrigger", issue.Message);
    }

    [Fact]
    public void Replay_final_stage_mismatch_raises_error_issue_when_last_stored_ToStage_differs()
    {
        var (signals, transitions, _) = BuildAgreedStream();
        transitions[^1] = CloneWithToStage(transitions[^1], "Completed"); // pretend the last stored landed in Completed

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.True(report.SemanticReplayPerformed);
        Assert.False(report.SemanticReplayFinalStageMatches);
        Assert.Contains(report.Issues,
            i => i.Kind == "replay_final_stage_mismatch" && i.Severity == "Error");
    }

    [Fact]
    public void Replay_handles_mid_stream_signal_deserialization_failure_without_aborting()
    {
        var (signals, transitions, _) = BuildAgreedStream();
        // Corrupt the second signal's PayloadJson so deserialization fails there.
        signals[1].PayloadJson = "this is not json";

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        // Replay ran on the valid prefix (signal 0); deserialization error reported for the
        // truncated tail rather than aborting the whole verification.
        Assert.True(report.SemanticReplayPerformed);
        Assert.Contains(report.Issues, i => i.Kind == "replay_deserialization_error");
    }

    // ---- Clone helpers (records are POCO, so tweaks need full copies) --------------

    private static DecisionTransitionRecord CloneWithReducerVersion(
        DecisionTransitionRecord source, string reducerVersion) =>
        new DecisionTransitionRecord
        {
            TenantId = source.TenantId,
            SessionId = source.SessionId,
            StepIndex = source.StepIndex,
            SessionTraceOrdinal = source.SessionTraceOrdinal,
            SignalOrdinalRef = source.SignalOrdinalRef,
            OccurredAtUtc = source.OccurredAtUtc,
            Trigger = source.Trigger,
            FromStage = source.FromStage,
            ToStage = source.ToStage,
            Taken = source.Taken,
            DeadEndReason = source.DeadEndReason,
            ReducerVersion = reducerVersion,
            PayloadJson = source.PayloadJson,
        };

    private static DecisionTransitionRecord CloneWithTrigger(
        DecisionTransitionRecord source, string trigger)
    {
        // We rewrite PayloadJson too so TransitionSerializer sees the tampered trigger
        // and the verifier's comparison picks it up.
        var transition = TransitionSerializer.Deserialize(source.PayloadJson);
        var tampered = new DecisionTransition(
            stepIndex: transition.StepIndex,
            sessionTraceOrdinal: transition.SessionTraceOrdinal,
            signalOrdinalRef: transition.SignalOrdinalRef,
            occurredAtUtc: transition.OccurredAtUtc,
            trigger: trigger,
            fromStage: transition.FromStage,
            toStage: transition.ToStage,
            taken: transition.Taken,
            deadEndReason: transition.DeadEndReason,
            reducerVersion: transition.ReducerVersion);
        return new DecisionTransitionRecord
        {
            TenantId = source.TenantId,
            SessionId = source.SessionId,
            StepIndex = source.StepIndex,
            SessionTraceOrdinal = source.SessionTraceOrdinal,
            SignalOrdinalRef = source.SignalOrdinalRef,
            OccurredAtUtc = source.OccurredAtUtc,
            Trigger = trigger,
            FromStage = source.FromStage,
            ToStage = source.ToStage,
            Taken = source.Taken,
            DeadEndReason = source.DeadEndReason,
            ReducerVersion = source.ReducerVersion,
            PayloadJson = TransitionSerializer.Serialize(tampered),
        };
    }

    private static DecisionTransitionRecord CloneWithToStage(
        DecisionTransitionRecord source, string toStage)
    {
        var transition = TransitionSerializer.Deserialize(source.PayloadJson);
        var tampered = new DecisionTransition(
            stepIndex: transition.StepIndex,
            sessionTraceOrdinal: transition.SessionTraceOrdinal,
            signalOrdinalRef: transition.SignalOrdinalRef,
            occurredAtUtc: transition.OccurredAtUtc,
            trigger: transition.Trigger,
            fromStage: transition.FromStage,
            toStage: (SessionStage)Enum.Parse(typeof(SessionStage), toStage),
            taken: transition.Taken,
            deadEndReason: transition.DeadEndReason,
            reducerVersion: transition.ReducerVersion);
        return new DecisionTransitionRecord
        {
            TenantId = source.TenantId,
            SessionId = source.SessionId,
            StepIndex = source.StepIndex,
            SessionTraceOrdinal = source.SessionTraceOrdinal,
            SignalOrdinalRef = source.SignalOrdinalRef,
            OccurredAtUtc = source.OccurredAtUtc,
            Trigger = source.Trigger,
            FromStage = source.FromStage,
            ToStage = toStage,
            Taken = source.Taken,
            DeadEndReason = source.DeadEndReason,
            ReducerVersion = source.ReducerVersion,
            PayloadJson = TransitionSerializer.Serialize(tampered),
        };
    }
}
