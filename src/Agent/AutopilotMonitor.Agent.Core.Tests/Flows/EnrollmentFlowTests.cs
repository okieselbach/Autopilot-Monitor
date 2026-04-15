using AutopilotMonitor.Shared.Models;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;

namespace AutopilotMonitor.Agent.Core.Tests.Flows
{
    /// <summary>
    /// Guards the v1/v2 enrollment-flow "Weiche": ensures flow handlers expose the correct
    /// policy bits and the factory picks the right handler for each EnrollmentType.
    /// Prevents regressions such as DevPrep flows accidentally applying the ESP gate,
    /// or Classic flows skipping ESP-phase tracking.
    /// </summary>
    public class EnrollmentFlowTests
    {
        // -- Enum <-> wire format roundtrip --

        [Theory]
        [InlineData("v1", EnrollmentType.Classic)]
        [InlineData("V1", EnrollmentType.Classic)]
        [InlineData("v2", EnrollmentType.DevicePreparation)]
        [InlineData("V2", EnrollmentType.DevicePreparation)]
        public void FromWireFormat_KnownValues_ReturnsMatchingEnum(string wire, EnrollmentType expected)
        {
            Assert.Equal(expected, EnrollmentTypeExtensions.FromWireFormat(wire));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("v3")]
        [InlineData("classic")]
        public void FromWireFormat_UnknownOrEmpty_ReturnsUnknown(string wire)
        {
            Assert.Equal(EnrollmentType.Unknown, EnrollmentTypeExtensions.FromWireFormat(wire));
        }

        [Theory]
        [InlineData(EnrollmentType.Classic, "v1")]
        [InlineData(EnrollmentType.DevicePreparation, "v2")]
        [InlineData(EnrollmentType.Unknown, "v1")] // legacy-safe default
        public void ToWireFormat_ProducesExpectedString(EnrollmentType type, string expected)
        {
            Assert.Equal(expected, type.ToWireFormat());
        }

        // -- Flow handler policy bits --

        [Fact]
        public void ClassicAutopilotFlow_TracksEspPhases_True()
        {
            var flow = new ClassicAutopilotFlow();
            Assert.Equal(EnrollmentType.Classic, flow.SupportedType);
            Assert.True(flow.TracksEspPhases);
            Assert.True(flow.AppliesEspGateOnDesktopArrival);
        }

        [Fact]
        public void DevicePreparationFlow_DoesNotTrackEspPhases()
        {
            var flow = new DevicePreparationFlow();
            Assert.Equal(EnrollmentType.DevicePreparation, flow.SupportedType);
            Assert.False(flow.TracksEspPhases);
            Assert.False(flow.AppliesEspGateOnDesktopArrival);
        }

        // -- Factory selection --

        [Fact]
        public void Factory_Create_Classic_ReturnsClassicFlow()
        {
            Assert.IsType<ClassicAutopilotFlow>(EnrollmentFlowFactory.Create(EnrollmentType.Classic));
        }

        [Fact]
        public void Factory_Create_DevicePreparation_ReturnsDevPrepFlow()
        {
            Assert.IsType<DevicePreparationFlow>(EnrollmentFlowFactory.Create(EnrollmentType.DevicePreparation));
        }

        [Fact]
        public void Factory_Create_Unknown_FallsBackToClassic()
        {
            // Unknown must not produce null — Classic is the safe legacy default.
            Assert.IsType<ClassicAutopilotFlow>(EnrollmentFlowFactory.Create(EnrollmentType.Unknown));
        }

        [Theory]
        [InlineData("v1", typeof(ClassicAutopilotFlow))]
        [InlineData("v2", typeof(DevicePreparationFlow))]
        [InlineData("", typeof(ClassicAutopilotFlow))]
        [InlineData("bogus", typeof(ClassicAutopilotFlow))]
        public void Factory_FromWireFormat_PicksCorrectFlow(string wire, System.Type expectedType)
        {
            var flow = EnrollmentFlowFactory.FromWireFormat(wire);
            Assert.IsType(expectedType, flow);
        }

        // -- RegisterSessionResponse back-compat guard --

        [Fact]
        public void RegisterSessionResponse_DefaultValidatedBy_IsUnknown()
        {
            // A response from an older backend that does not set ValidatedBy must
            // deserialize with ValidatedBy = Unknown so the agent falls back to
            // its own registry detection.
            var response = new RegisterSessionResponse();
            Assert.Equal(ValidatorType.Unknown, response.ValidatedBy);
        }

        [Fact]
        public void ValidatorType_Unknown_IsDefaultValue()
        {
            // Newtonsoft.Json / System.Text.Json default for a missing enum field is 0.
            // Unknown must be 0 so missing-field deserialization stays backward compatible.
            Assert.Equal(0, (int)ValidatorType.Unknown);
        }
    }
}
