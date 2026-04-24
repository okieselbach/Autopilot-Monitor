using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.State
{
    /// <summary>
    /// Set-once + ordinal semantics for <see cref="EnrollmentScenarioObservations"/>.
    /// Codex follow-up #5.
    /// </summary>
    public sealed class EnrollmentScenarioObservationsTests
    {
        [Fact]
        public void Empty_hasAllNullFacts()
        {
            var obs = EnrollmentScenarioObservations.Empty;

            Assert.Null(obs.ShellCoreWhiteGloveSuccessSeen);
            Assert.Null(obs.WhiteGloveSealingPatternSeen);
            Assert.Null(obs.AadUserJoinWithUserObserved);
            Assert.Null(obs.SkipUserEsp);
            Assert.Null(obs.SkipDeviceEsp);
        }

        [Fact]
        public void WithShellCoreWhiteGloveSuccessSeen_setsFact_preservesOrdinal()
        {
            var obs = EnrollmentScenarioObservations.Empty
                .WithShellCoreWhiteGloveSuccessSeen(sourceSignalOrdinal: 17);

            Assert.NotNull(obs.ShellCoreWhiteGloveSuccessSeen);
            Assert.True(obs.ShellCoreWhiteGloveSuccessSeen!.Value);
            Assert.Equal(17, obs.ShellCoreWhiteGloveSuccessSeen!.SourceSignalOrdinal);
        }

        [Fact]
        public void WithShellCoreWhiteGloveSuccessSeen_alreadySet_isNoOp()
        {
            var seeded = EnrollmentScenarioObservations.Empty
                .WithShellCoreWhiteGloveSuccessSeen(sourceSignalOrdinal: 3);

            var repeat = seeded.WithShellCoreWhiteGloveSuccessSeen(sourceSignalOrdinal: 99);

            // Set-once: first ordinal wins; second call is a no-op returning the same instance.
            Assert.Same(seeded, repeat);
            Assert.Equal(3, repeat.ShellCoreWhiteGloveSuccessSeen!.SourceSignalOrdinal);
        }

        [Fact]
        public void WithAadUserJoinWithUserObserved_setsValueAndOrdinal()
        {
            var obs = EnrollmentScenarioObservations.Empty
                .WithAadUserJoinWithUserObserved(value: true, sourceSignalOrdinal: 42);

            Assert.NotNull(obs.AadUserJoinWithUserObserved);
            Assert.True(obs.AadUserJoinWithUserObserved!.Value);
            Assert.Equal(42, obs.AadUserJoinWithUserObserved!.SourceSignalOrdinal);
        }

        [Fact]
        public void WithAadUserJoinWithUserObserved_recordsFalseValue()
        {
            var obs = EnrollmentScenarioObservations.Empty
                .WithAadUserJoinWithUserObserved(value: false, sourceSignalOrdinal: 5);

            Assert.False(obs.AadUserJoinWithUserObserved!.Value);
        }

        [Fact]
        public void WithSkipUserEsp_thenWithSkipDeviceEsp_bothSet_independentOrdinals()
        {
            var obs = EnrollmentScenarioObservations.Empty
                .WithSkipUserEsp(true, sourceSignalOrdinal: 1)
                .WithSkipDeviceEsp(false, sourceSignalOrdinal: 2);

            Assert.True(obs.SkipUserEsp!.Value);
            Assert.Equal(1, obs.SkipUserEsp!.SourceSignalOrdinal);
            Assert.False(obs.SkipDeviceEsp!.Value);
            Assert.Equal(2, obs.SkipDeviceEsp!.SourceSignalOrdinal);
        }

        [Fact]
        public void WithSkipUserEsp_alreadySet_isNoOpEvenIfValueDiffers()
        {
            var seeded = EnrollmentScenarioObservations.Empty
                .WithSkipUserEsp(value: false, sourceSignalOrdinal: 1);

            var repeat = seeded.WithSkipUserEsp(value: true, sourceSignalOrdinal: 99);

            Assert.Same(seeded, repeat);
            Assert.False(repeat.SkipUserEsp!.Value);
            Assert.Equal(1, repeat.SkipUserEsp!.SourceSignalOrdinal);
        }

        [Fact]
        public void Mutations_returnNewInstances()
        {
            var empty = EnrollmentScenarioObservations.Empty;
            var mutated = empty.WithWhiteGloveSealingPatternSeen(7);

            Assert.NotSame(empty, mutated);
            Assert.Null(empty.WhiteGloveSealingPatternSeen);
            Assert.NotNull(mutated.WhiteGloveSealingPatternSeen);
        }
    }
}
