using System;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// M2 smoke tests — verify the M1 scaffolding is wired up and the core invariants
    /// (immutability, validation, stub behaviour) hold. Real reducer + classifier tests
    /// arrive in M3.
    /// </summary>
    public sealed class SmokeTests
    {
        [Fact]
        public void DecisionState_CreateInitial_producesValidNonTerminalState()
        {
            var state = DecisionState.CreateInitial("session-1", "tenant-1");

            Assert.Equal("session-1", state.SessionId);
            Assert.Equal("tenant-1", state.TenantId);
            Assert.Equal(SessionStage.SessionStarted, state.Stage);
            Assert.Null(state.Outcome);
            Assert.Equal(HypothesisLevel.Unknown, state.EnrollmentType.Level);
            Assert.Equal(HypothesisLevel.Unknown, state.WhiteGloveSealing.Level);
            Assert.Equal(HypothesisLevel.Unknown, state.WhiteGlovePart2Completion.Level);
            Assert.Equal(HypothesisLevel.Unknown, state.DeviceOnlyDeployment.Level);
            Assert.Empty(state.Deadlines);
            Assert.Equal(-1, state.LastAppliedSignalOrdinal);
            Assert.Equal(0, state.StepIndex);
            Assert.Equal(DecisionState.CurrentSchemaVersion, state.SchemaVersion);
        }

        [Fact]
        public void Evidence_Derived_withoutDerivationInputs_throws()
        {
            Assert.Throws<ArgumentException>(() => new Evidence(
                kind: EvidenceKind.Derived,
                identifier: "detector-v1",
                summary: "test",
                derivationInputs: null));
        }

        [Fact]
        public void Evidence_Raw_withEmptyIdentifier_throws()
        {
            Assert.Throws<ArgumentException>(() => new Evidence(
                kind: EvidenceKind.Raw,
                identifier: "",
                summary: "test"));
        }

        [Fact]
        public void DecisionSignal_invalidSchemaVersion_throws()
        {
            var evidence = new Evidence(EvidenceKind.Raw, "rec-1", "summary");
            Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionSignal(
                sessionSignalOrdinal: 0,
                sessionTraceOrdinal: 0,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 0,
                occurredAtUtc: DateTime.UtcNow,
                sourceOrigin: "test",
                evidence: evidence));
        }

        [Fact]
        public void DecisionEngine_Reduce_M1Stub_throwsNotImplemented()
        {
            var engine = new DecisionEngine();
            var state = DecisionState.CreateInitial("s", "t");
            var signal = new DecisionSignal(
                sessionSignalOrdinal: 0,
                sessionTraceOrdinal: 0,
                kind: DecisionSignalKind.SessionStarted,
                kindSchemaVersion: 1,
                occurredAtUtc: DateTime.UtcNow,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, "session:started", "test"));

            Assert.Throws<NotImplementedException>(() => engine.Reduce(state, signal));
        }

        [Fact]
        public void DecisionEngine_ReducerVersion_matchesAssemblyVersion()
        {
            var engine = new DecisionEngine();
            // AssemblyVersion in the DecisionCore csproj is 2.0.0.0.
            Assert.Equal("2.0.0.0", engine.ReducerVersion);
        }

        [Fact]
        public void Hypothesis_With_returnsNewInstance_originalUnchanged()
        {
            var original = Hypothesis.UnknownInstance;
            var updated = original.With(level: HypothesisLevel.Strong, score: 80);

            Assert.Equal(HypothesisLevel.Unknown, original.Level);
            Assert.Equal(0, original.Score);
            Assert.Equal(HypothesisLevel.Strong, updated.Level);
            Assert.Equal(80, updated.Score);
            Assert.NotSame(original, updated);
        }

        [Fact]
        public void EnrollmentPhase_referenceFromSharedWorks()
        {
            // Plan §2.3: EnrollmentPhase comes from Shared, not duplicated in DecisionCore.
            var phase = EnrollmentPhase.AccountSetup;
            var fact = new SignalFact<EnrollmentPhase>(phase, 42);
            Assert.Equal(EnrollmentPhase.AccountSetup, fact.Value);
            Assert.Equal(42, fact.SourceSignalOrdinal);
        }
    }
}
