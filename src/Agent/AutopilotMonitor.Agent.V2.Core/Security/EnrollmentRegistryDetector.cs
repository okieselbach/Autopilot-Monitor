using System;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Read-only registry probes for enrollment metadata the agent has to pin down at session
    /// registration time. V1 parity — these mirror <c>EnrollmentTracker.DetectHybridJoinStatic</c>
    /// and <c>EnrollmentTracker.DetectEnrollmentTypeStatic</c> from the legacy agent.
    /// <para>
    /// Kept stateless and exception-swallowing so the registration pipeline can call them
    /// before any other component is up. A missing / inaccessible registry key degrades to
    /// the default value (v1 / non-hybrid) rather than throwing — mirrors V1 behaviour.
    /// </para>
    /// </summary>
    public static class EnrollmentRegistryDetector
    {
        private const string AutopilotPolicyCacheKey = @"SOFTWARE\Microsoft\Provisioning\AutopilotPolicyCache";
        private const string AutopilotSettingsKey = @"SOFTWARE\Microsoft\Provisioning\AutopilotSettings";

        /// <summary>
        /// Reads <c>CloudAssignedDomainJoinMethod</c> from the Autopilot policy cache. Returns
        /// <c>true</c> when the profile was deployed with Hybrid Azure AD Join
        /// (<c>CloudAssignedDomainJoinMethod == 1</c>), <c>false</c> otherwise or on any error.
        /// </summary>
        public static bool DetectHybridJoin()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(AutopilotPolicyCacheKey))
                {
                    if (key == null) return false;
                    var domainJoinMethod = key.GetValue("CloudAssignedDomainJoinMethod")?.ToString();
                    return domainJoinMethod == "1";
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Classifies the Autopilot flow based on the <c>AutopilotSettings</c> registry:
        /// <list type="bullet">
        ///   <item><c>CloudAssignedDeviceRegistration == 2</c> → <c>"v2"</c> (Windows Device Preparation).</item>
        ///   <item><c>CloudAssignedEspEnabled == 0</c> → <c>"v2"</c> (no ESP, WDP indicator).</item>
        ///   <item>Anything else → <c>"v1"</c> (Classic Autopilot / ESP).</item>
        /// </list>
        /// Defaults to <c>"v1"</c> on any error so a flaky registry does not misclassify a
        /// classic session as WDP.
        /// </summary>
        public static string DetectEnrollmentType()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(AutopilotSettingsKey))
                {
                    if (key == null) return "v1";

                    var deviceReg = key.GetValue("CloudAssignedDeviceRegistration")?.ToString();
                    if (deviceReg == "2") return "v2";

                    var espEnabled = key.GetValue("CloudAssignedEspEnabled")?.ToString();
                    if (espEnabled == "0") return "v2";
                }
            }
            catch
            {
                // Fall through to v1 default.
            }
            return "v1";
        }
    }
}
