using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure-function tests for <see cref="ReducerVerifier"/>. Covers the five detectors:
/// empty session, signal ordinal gaps, step index gaps, orphaned transitions, and
/// ReducerVersion drift — plus the happy-path (all contiguous, versions match).
/// </summary>
public class ReducerVerifierTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string CurrentReducerVersion = "1.4.2.0";

    private static SignalRecord Signal(long ordinal)
        => new SignalRecord
        {
            TenantId             = TenantId,
            SessionId            = SessionId,
            SessionSignalOrdinal = ordinal,
            SessionTraceOrdinal  = ordinal,
            Kind                 = "EspPhaseChanged",
            KindSchemaVersion    = 1,
            OccurredAtUtc        = DateTime.UtcNow,
            SourceOrigin         = "test",
            PayloadJson          = "{}",
        };

    private static DecisionTransitionRecord Transition(
        int stepIndex, long signalRef, string reducerVersion = CurrentReducerVersion)
        => new DecisionTransitionRecord
        {
            TenantId            = TenantId,
            SessionId           = SessionId,
            StepIndex           = stepIndex,
            SessionTraceOrdinal = signalRef,
            SignalOrdinalRef    = signalRef,
            OccurredAtUtc       = DateTime.UtcNow,
            Trigger             = "t",
            FromStage           = "SessionStarted",
            ToStage             = "EspInProgress",
            Taken               = true,
            ReducerVersion      = reducerVersion,
        };

    // ============================================================ Empty

    [Fact]
    public void Verify_empty_session_emits_info_issue_and_returns_zero_counts()
    {
        var report = ReducerVerifier.Verify(
            TenantId, SessionId,
            Array.Empty<SignalRecord>(),
            Array.Empty<DecisionTransitionRecord>(),
            CurrentReducerVersion);

        Assert.Equal(0, report.SignalCount);
        Assert.Equal(0, report.TransitionCount);
        Assert.Single(report.Issues);
        Assert.Equal("empty_session", report.Issues[0].Kind);
        Assert.Equal("Info", report.Issues[0].Severity);
    }

    // ============================================================ Happy path

    [Fact]
    public void Verify_happy_path_contiguous_signals_and_transitions_no_drift()
    {
        var signals = new[] { Signal(1), Signal(2), Signal(3) };
        var transitions = new[] { Transition(1, 1), Transition(2, 2), Transition(3, 3) };

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.Equal(3, report.SignalCount);
        Assert.Equal(3, report.TransitionCount);
        Assert.True(report.SignalOrdinalsContiguous);
        Assert.True(report.StepIndicesContiguous);
        Assert.False(report.ReducerVersionDrift);
        Assert.Equal(0, report.OrphanedTransitionCount);
        Assert.Empty(report.Issues);
    }

    // ============================================================ Signal ordinal gaps

    [Fact]
    public void Verify_detects_signal_ordinal_gap()
    {
        var signals = new[] { Signal(1), Signal(2), Signal(5) }; // gap: 3, 4 missing

        var report = ReducerVerifier.Verify(
            TenantId, SessionId, signals, Array.Empty<DecisionTransitionRecord>(), CurrentReducerVersion);

        Assert.False(report.SignalOrdinalsContiguous);
        Assert.Equal(1, report.SignalOrdinalFirst);
        Assert.Equal(5, report.SignalOrdinalLast);
        Assert.Contains(report.Issues, i => i.Kind == "signal_ordinal_gap" && i.Message.Contains("missing 2"));
    }

    [Fact]
    public void Verify_caps_ordinal_gap_issues_and_adds_overflow_message()
    {
        // Construct >20 gaps: alternating contiguous-then-jump pattern.
        var ordinals = new List<long>();
        for (var i = 0; i < 25; i++) ordinals.Add((long)(i * 10 + 1)); // 1,11,21,31,... → 24 gaps
        var signals = ordinals.Select(Signal).ToArray();

        var report = ReducerVerifier.Verify(
            TenantId, SessionId, signals, Array.Empty<DecisionTransitionRecord>(), CurrentReducerVersion);

        var gapIssues = report.Issues.Where(i => i.Kind == "signal_ordinal_gap").ToList();
        // 20 detailed + 1 overflow summary = 21
        Assert.Equal(21, gapIssues.Count);
        Assert.Contains(gapIssues, i => i.Message.StartsWith("... ") && i.Message.Contains("additional"));
    }

    // ============================================================ Step-index gaps

    [Fact]
    public void Verify_detects_step_index_gap()
    {
        var signals = new[] { Signal(1), Signal(2), Signal(3) };
        var transitions = new[] { Transition(1, 1), Transition(3, 3) }; // step 2 missing

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.False(report.StepIndicesContiguous);
        Assert.Contains(report.Issues, i => i.Kind == "step_index_gap");
    }

    // ============================================================ Orphaned transitions

    [Fact]
    public void Verify_flags_transition_referencing_missing_signal()
    {
        var signals = new[] { Signal(1), Signal(2) };
        // Transition step 2 references signal ordinal 99 which doesn't exist.
        var transitions = new[] { Transition(1, 1), Transition(2, 99) };

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.Equal(1, report.OrphanedTransitionCount);
        Assert.Contains(report.Issues, i => i.Kind == "orphaned_transition" && i.Message.Contains("99"));
    }

    // ============================================================ ReducerVersion drift

    [Fact]
    public void Verify_flags_ReducerVersion_drift_as_Info_level()
    {
        var signals = new[] { Signal(1) };
        var transitions = new[] { Transition(1, 1, reducerVersion: "1.2.0.0") };

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.True(report.ReducerVersionDrift);
        Assert.Equal("1.2.0.0", report.StoredReducerVersion);
        Assert.Equal(CurrentReducerVersion, report.CurrentReducerVersion);

        var drift = report.Issues.Single(i => i.Kind == "reducer_version_drift");
        // Drift is informational, not an error — it's a known condition when replays span
        // reducer versions and the report makes sense to show regardless.
        Assert.Equal("Info", drift.Severity);
    }

    [Fact]
    public void Verify_does_not_flag_drift_when_stored_version_matches_current()
    {
        var signals = new[] { Signal(1) };
        var transitions = new[] { Transition(1, 1, reducerVersion: CurrentReducerVersion) };

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.False(report.ReducerVersionDrift);
        Assert.DoesNotContain(report.Issues, i => i.Kind == "reducer_version_drift");
    }

    // ============================================================ Ordering

    [Fact]
    public void Verify_handles_out_of_order_input_because_the_verifier_sorts_before_checking()
    {
        var signals = new[] { Signal(3), Signal(1), Signal(2) };
        var transitions = new[] { Transition(3, 3), Transition(1, 1), Transition(2, 2) };

        var report = ReducerVerifier.Verify(TenantId, SessionId, signals, transitions, CurrentReducerVersion);

        Assert.True(report.SignalOrdinalsContiguous);
        Assert.True(report.StepIndicesContiguous);
        Assert.Equal(1, report.SignalOrdinalFirst);
        Assert.Equal(3, report.SignalOrdinalLast);
    }
}
