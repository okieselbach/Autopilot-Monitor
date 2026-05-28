using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Termination
{
    /// <summary>
    /// Session 080edee9 follow-up (2026-05-28) — coverage for
    /// <see cref="Constants.AppFailureTypes.ClassifyEspAppsFailure"/>. The classifier picks
    /// the right canonical failureType + message for an ESP Apps-subcategory failure based
    /// on the observed HRESULT, so the V2 EnrollmentTerminationHandler stops mis-labelling
    /// every ESP terminal failure as a timeout.
    /// </summary>
    public class AppFailureTypesClassifierTests
    {
        [Fact]
        public void Classify_0x87d1041c_returns_detection_failure()
        {
            var (failureType, message) = Constants.AppFailureTypes
                .ClassifyEspAppsFailure("0x87d1041c", espTimeoutMinutes: 180);

            Assert.Equal(Constants.AppFailureTypes.EspAppsDetectionFailure, failureType);
            Assert.Contains("detection", message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("0x87d1041c", message);
            Assert.DoesNotContain("timeout", message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Classify_uppercase_0x87D1041C_is_normalised_to_lowercase()
        {
            var (failureType, message) = Constants.AppFailureTypes
                .ClassifyEspAppsFailure("0x87D1041C", espTimeoutMinutes: null);

            Assert.Equal(Constants.AppFailureTypes.EspAppsDetectionFailure, failureType);
            Assert.Contains("0x87d1041c", message);
        }

        [Fact]
        public void Classify_other_hresult_returns_install_failure()
        {
            var (failureType, message) = Constants.AppFailureTypes
                .ClassifyEspAppsFailure("0x80070643", espTimeoutMinutes: 180);

            Assert.Equal(Constants.AppFailureTypes.EspAppsInstallFailure, failureType);
            Assert.Contains("0x80070643", message);
            Assert.DoesNotContain("timeout", message, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Classify_no_hresult_with_timeout_returns_timeout_with_configured_label()
        {
            // The 180 min is the *configured* SyncFailureTimeoutMinutes — the message MUST
            // make clear it's a ceiling, not the elapsed time (session 080edee9 root cause).
            var (failureType, message) = Constants.AppFailureTypes
                .ClassifyEspAppsFailure(errorCode: null, espTimeoutMinutes: 180);

            Assert.Equal(Constants.AppFailureTypes.EspAppsTimeout, failureType);
            Assert.Contains("ESP gave up", message);
            Assert.Contains("180 min", message);
            Assert.Contains("configured", message);
        }

        [Fact]
        public void Classify_no_hresult_no_timeout_returns_generic_timeout()
        {
            var (failureType, message) = Constants.AppFailureTypes
                .ClassifyEspAppsFailure(errorCode: null, espTimeoutMinutes: null);

            Assert.Equal(Constants.AppFailureTypes.EspAppsTimeout, failureType);
            Assert.Contains("ESP gave up", message);
            Assert.DoesNotContain("min", message);
        }

        [Fact]
        public void Classify_empty_hresult_treated_as_unknown()
        {
            var (failureType, _) = Constants.AppFailureTypes
                .ClassifyEspAppsFailure(errorCode: "", espTimeoutMinutes: 60);

            Assert.Equal(Constants.AppFailureTypes.EspAppsTimeout, failureType);
        }
    }
}
