using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests.State
{
    /// <summary>
    /// Typ-Invarianten für <see cref="EnrollmentScenarioProfile"/> — Codex follow-up #5.
    /// </summary>
    public sealed class EnrollmentScenarioProfileTests
    {
        [Fact]
        public void Empty_hasUnknownDefaults()
        {
            var p = EnrollmentScenarioProfile.Empty;

            Assert.Equal(EnrollmentMode.Unknown, p.Mode);
            Assert.Equal(EnrollmentJoinMode.Unknown, p.JoinMode);
            Assert.Equal(EspConfig.Unknown, p.EspConfig);
            Assert.Equal(PreProvisioningSide.None, p.PreProvisioningSide);
            Assert.Equal(ProfileConfidence.Low, p.Confidence);
            Assert.Equal(-1, p.EvidenceOrdinal);
            Assert.Null(p.Reason);
        }

        [Fact]
        public void With_omittedParam_preservesCurrentValue()
        {
            var seed = EnrollmentScenarioProfile.Empty.With(
                mode: EnrollmentMode.Classic,
                confidence: ProfileConfidence.Medium,
                reason: "account_setup_observed",
                evidenceOrdinal: 5);

            var patched = seed.With(confidence: ProfileConfidence.High, evidenceOrdinal: 9);

            Assert.Equal(EnrollmentMode.Classic, patched.Mode);
            Assert.Equal(ProfileConfidence.High, patched.Confidence);
            Assert.Equal("account_setup_observed", patched.Reason);
            Assert.Equal(9, patched.EvidenceOrdinal);
            Assert.Equal(EnrollmentJoinMode.Unknown, patched.JoinMode);
        }

        [Fact]
        public void With_returnsNewInstance_originalUnchanged()
        {
            var seed = EnrollmentScenarioProfile.Empty;
            var mutated = seed.With(mode: EnrollmentMode.WhiteGlove, confidence: ProfileConfidence.High);

            Assert.Equal(EnrollmentMode.Unknown, seed.Mode);
            Assert.Equal(EnrollmentMode.WhiteGlove, mutated.Mode);
            Assert.NotSame(seed, mutated);
        }

        [Fact]
        public void With_reasonNull_doesNotClear()
        {
            // Intentional: Reason is historical evidence; With(reason:null) is interpreted as
            // "no change" (same semantic as Hypothesis.With).
            var seed = EnrollmentScenarioProfile.Empty.With(reason: "initial");
            var patched = seed.With(reason: null);

            Assert.Equal("initial", patched.Reason);
        }

        [Theory]
        [InlineData(ProfileConfidence.Low, ProfileConfidence.Medium, ProfileConfidence.Medium)]
        [InlineData(ProfileConfidence.Low, ProfileConfidence.Low, ProfileConfidence.Low)]
        [InlineData(ProfileConfidence.Medium, ProfileConfidence.Low, ProfileConfidence.Medium)]
        [InlineData(ProfileConfidence.High, ProfileConfidence.Medium, ProfileConfidence.High)]
        public void Confidence_monotonicHelper_picksHigher(ProfileConfidence a, ProfileConfidence b, ProfileConfidence expected)
        {
            Assert.Equal(expected, EnrollmentScenarioProfileUpdater.Max(a, b));
        }
    }
}
