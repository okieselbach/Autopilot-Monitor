using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Codex follow-up #4 — coverage for the AppInstall observation handlers. Until this
    /// PR both <see cref="DecisionSignalKind.AppInstallCompleted"/> and
    /// <see cref="DecisionSignalKind.AppInstallFailed"/> fell through to
    /// <c>HandleUnhandledSignal</c>, producing dead-end transitions and discarding the
    /// per-app outcome. These tests pin the new contract: bookkeeping advances, the
    /// <see cref="DecisionState.AppInstallFacts"/> aggregate updates correctly, and the
    /// transitions come out as taken (not dead-ends).
    /// </summary>
    public sealed class AppInstallSignalHandlerTests
    {
        private const string SessionId = "session-appinstall";
        private const string TenantId = "tenant-appinstall";

        private static DecisionSignal MakeCompleted(long ordinal, string appId, string newState)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.AppInstallCompleted,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc).AddSeconds(ordinal),
                sourceOrigin: "IME",
                evidence: new Evidence(EvidenceKind.Derived, $"app:{appId}", $"App {appId} → {newState}",
                    derivationInputs: new Dictionary<string, string>
                    {
                        ["appId"] = appId,
                        ["newState"] = newState,
                    }),
                payload: new Dictionary<string, string>
                {
                    ["appId"] = appId,
                    ["newState"] = newState,
                });
        }

        private static DecisionSignal MakeFailed(long ordinal, string appId)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: DecisionSignalKind.AppInstallFailed,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc).AddSeconds(ordinal),
                sourceOrigin: "IME",
                evidence: new Evidence(EvidenceKind.Derived, $"app:{appId}", $"App {appId} → Error",
                    derivationInputs: new Dictionary<string, string>
                    {
                        ["appId"] = appId,
                        ["newState"] = "Error",
                    }),
                payload: new Dictionary<string, string>
                {
                    ["appId"] = appId,
                    ["newState"] = "Error",
                });
        }

        // ---- Completed handler -----------------------------------------------------------

        [Theory]
        // newState payload → (Completed, Installed, Skipped, Postponed) — mapping is case-insensitive
        // (AppInstallFacts.WithCompleted uses OrdinalIgnoreCase).
        [InlineData("Installed", 1, 1, 0, 0)]
        [InlineData("installed", 1, 1, 0, 0)]   // case-insensitive (NEW coverage)
        [InlineData("Skipped",   1, 0, 1, 0)]
        [InlineData("SKIPPED",   1, 0, 1, 0)]   // case-insensitive (NEW coverage)
        [InlineData("Postponed", 1, 0, 0, 1)]
        // Forward-compat: any unknown label (future adapter vocabulary, empty payload) still
        // advances CompletedCount but does not hit any breakdown bucket.
        [InlineData("Replaced",  1, 0, 0, 0)]
        [InlineData("",          1, 0, 0, 0)]   // empty newState (NEW coverage)
        public void Completed_signal_updates_breakdown_by_newState_and_records_taken_transition(
            string newState,
            int expectedCompleted, int expectedInstalled, int expectedSkipped, int expectedPostponed)
        {
            var engine = new DecisionEngine();

            var step = engine.Reduce(
                DecisionState.CreateInitial(SessionId, TenantId),
                MakeCompleted(0, "app-1", newState));

            Assert.True(step.Transition.Taken);
            Assert.Null(step.Transition.DeadEndReason);
            Assert.Equal(nameof(DecisionSignalKind.AppInstallCompleted), step.Transition.Trigger);

            var facts = step.NewState.AppInstallFacts;
            Assert.Equal(expectedCompleted, facts.CompletedCount);
            Assert.Equal(expectedInstalled, facts.InstalledCount);
            Assert.Equal(expectedSkipped, facts.SkippedCount);
            Assert.Equal(expectedPostponed, facts.PostponedCount);
            Assert.Equal(0, facts.FailedCount);
        }

        [Fact]
        public void Completed_advances_step_bookkeeping_without_changing_stage_or_outcome()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial(SessionId, TenantId)
                .ToBuilder()
                .WithStage(SessionStage.AwaitingHello)
                .WithStepIndex(4)
                .WithLastAppliedSignalOrdinal(3)
                .Build();

            var step = engine.Reduce(state, MakeCompleted(4, "app-1", "Installed"));

            Assert.Equal(SessionStage.AwaitingHello, step.NewState.Stage);
            Assert.Null(step.NewState.Outcome);
            Assert.Equal(5, step.NewState.StepIndex);
            Assert.Equal(4, step.NewState.LastAppliedSignalOrdinal);
        }

        // ---- Failed handler --------------------------------------------------------------

        [Fact]
        public void Failed_increments_FailedCount_and_records_appId()
        {
            var engine = new DecisionEngine();
            var step = engine.Reduce(
                DecisionState.CreateInitial(SessionId, TenantId),
                MakeFailed(0, "app-broken-1"));

            Assert.True(step.Transition.Taken);
            Assert.Equal(nameof(DecisionSignalKind.AppInstallFailed), step.Transition.Trigger);

            var facts = step.NewState.AppInstallFacts;
            Assert.Equal(1, facts.FailedCount);
            Assert.Single(facts.FailedAppIds, "app-broken-1");
            Assert.Equal(0, facts.CompletedCount);
        }

        [Fact]
        public void Failed_without_appId_still_increments_count_but_does_not_pollute_list()
        {
            // Defensive: a malformed payload without "appId" should not crash or poison the
            // list with empty entries.
            var engine = new DecisionEngine();
            var signal = new DecisionSignal(
                sessionSignalOrdinal: 0,
                sessionTraceOrdinal: 0,
                kind: DecisionSignalKind.AppInstallFailed,
                kindSchemaVersion: 1,
                occurredAtUtc: new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc),
                sourceOrigin: "IME",
                evidence: new Evidence(EvidenceKind.Synthetic, "app:unknown", "test"),
                payload: new Dictionary<string, string>()); // no "appId"

            var step = engine.Reduce(DecisionState.CreateInitial(SessionId, TenantId), signal);

            var facts = step.NewState.AppInstallFacts;
            Assert.Equal(1, facts.FailedCount);
            Assert.Empty(facts.FailedAppIds);
        }

        [Fact]
        public void Failed_deduplicates_repeated_appId()
        {
            // The adapter posts terminal once per app, but a defensive dedup guards against
            // any replay-injection that might double-feed the same id.
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial(SessionId, TenantId);

            var step1 = engine.Reduce(state, MakeFailed(0, "app-x"));
            var step2 = engine.Reduce(step1.NewState, MakeFailed(1, "app-x"));

            var facts = step2.NewState.AppInstallFacts;
            Assert.Equal(2, facts.FailedCount);
            Assert.Single(facts.FailedAppIds); // still just one entry
            Assert.Equal("app-x", facts.FailedAppIds[0]);
        }

        [Fact]
        public void Failed_caps_FailedAppIds_at_MaxFailedAppIds_limit()
        {
            var engine = new DecisionEngine();
            DecisionState state = DecisionState.CreateInitial(SessionId, TenantId);

            // Post MaxFailedAppIds + 10 distinct failures; the tail must be dropped.
            var total = AppInstallFacts.MaxFailedAppIds + 10;
            for (var i = 0; i < total; i++)
            {
                state = engine.Reduce(state, MakeFailed(i, $"app-{i}")).NewState;
            }

            var facts = state.AppInstallFacts;
            Assert.Equal(total, facts.FailedCount);
            Assert.Equal(AppInstallFacts.MaxFailedAppIds, facts.FailedAppIds.Count);
            // First-observed wins — the cap drops later arrivals, not earlier ones.
            Assert.Equal("app-0", facts.FailedAppIds[0]);
            Assert.Equal($"app-{AppInstallFacts.MaxFailedAppIds - 1}",
                facts.FailedAppIds[AppInstallFacts.MaxFailedAppIds - 1]);
        }

        // ---- Integration -----------------------------------------------------------------

        [Fact]
        public void Mixed_stream_of_completed_and_failed_accumulates_facts_correctly()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial(SessionId, TenantId);

            state = engine.Reduce(state, MakeCompleted(0, "a1", "Installed")).NewState;
            state = engine.Reduce(state, MakeCompleted(1, "a2", "Skipped")).NewState;
            state = engine.Reduce(state, MakeFailed(2, "a3")).NewState;
            state = engine.Reduce(state, MakeCompleted(3, "a4", "Installed")).NewState;
            state = engine.Reduce(state, MakeFailed(4, "a5")).NewState;

            var facts = state.AppInstallFacts;
            Assert.Equal(3, facts.CompletedCount);
            Assert.Equal(2, facts.InstalledCount);
            Assert.Equal(1, facts.SkippedCount);
            Assert.Equal(0, facts.PostponedCount);
            Assert.Equal(2, facts.FailedCount);
            Assert.Equal(new[] { "a3", "a5" }, facts.FailedAppIds);
            Assert.Equal(5, state.StepIndex);
            Assert.Equal(4, state.LastAppliedSignalOrdinal);
        }
    }
}
