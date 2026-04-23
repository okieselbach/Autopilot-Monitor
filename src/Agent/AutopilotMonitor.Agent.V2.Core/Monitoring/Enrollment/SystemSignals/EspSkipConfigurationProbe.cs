using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Plan §6 Fix 7/9 — single source of truth for the Autopilot-enrollment ESP skip flags
    /// (<c>SkipUserStatusPage</c> / <c>SkipDeviceStatusPage</c>) that the MDM CSP writes under
    /// <c>HKLM\SOFTWARE\Microsoft\Enrollments\{guid}\FirstSync</c> during FirstSync.
    /// <para>
    /// Two consumers:
    /// <list type="bullet">
    ///   <item><see cref="Telemetry.DeviceInfo.DeviceInfoCollector"/> — emits the
    ///     <c>esp_config_detected</c> event + the <c>EspConfigDetected</c> decision signal
    ///     at agent start and at first DeviceSetup-phase detection.</item>
    ///   <item><see cref="EspAndHelloTracker"/> — guards the coordinator's
    ///     synthetic <c>EspPhaseChanged(FinalizingSetup)</c> forward: a Classic V1 enrollment
    ///     with <c>SkipUser=false</c> sees TWO <c>esp_exiting</c> events (Device-ESP exit and
    ///     Account-ESP exit) and only the second is a true final exit.</item>
    /// </list>
    /// Reading from the same authoritative registry location in both places keeps the reducer's
    /// <see cref="AutopilotMonitor.DecisionCore.State.DecisionState.SkipUserEsp"/> fact and the
    /// tracker's guard in lockstep — the alternative (passing the value around) has too many
    /// subtle lifecycle order issues to be safe.
    /// </para>
    /// <para>
    /// CSP values: <c>0xFFFFFFFF</c> = skip (ESP page not shown), <c>0</c> = show, key missing
    /// = unknown. Defensive interpretation: non-null AND non-zero = skip.
    /// </para>
    /// </summary>
    internal static class EspSkipConfigurationProbe
    {
        internal const string EnrollmentsKeyPath = @"SOFTWARE\Microsoft\Enrollments";
        internal const int MdmEnrollmentType = 6;

        /// <summary>
        /// Reads the current device's <c>SkipUserStatusPage</c> / <c>SkipDeviceStatusPage</c>
        /// flags. Returns <c>(null, null)</c> when the enrollment key is missing or unreadable —
        /// callers must treat <c>null</c> as "unknown", not "false".
        /// </summary>
        /// <param name="logger">Optional logger — Debug-level trace only; no warn/error.</param>
        public static (bool? skipUser, bool? skipDevice) Read(AgentLogger logger = null)
        {
            bool? skipUser = null;
            bool? skipDevice = null;

            try
            {
                using (var enrollmentsKey = Registry.LocalMachine.OpenSubKey(EnrollmentsKeyPath))
                {
                    if (enrollmentsKey == null)
                    {
                        logger?.Debug("EspSkipConfigurationProbe: Enrollments registry key not found");
                        return (null, null);
                    }

                    foreach (var guid in enrollmentsKey.GetSubKeyNames())
                    {
                        using (var enrollmentKey = enrollmentsKey.OpenSubKey(guid))
                        {
                            if (enrollmentKey == null) continue;

                            var enrollmentType = enrollmentKey.GetValue("EnrollmentType");
                            if (enrollmentType == null || Convert.ToInt32(enrollmentType) != MdmEnrollmentType)
                                continue;

                            using (var firstSyncKey = enrollmentKey.OpenSubKey("FirstSync"))
                            {
                                if (firstSyncKey == null)
                                {
                                    logger?.Debug($"EspSkipConfigurationProbe: FirstSync subkey not found for enrollment {guid}");
                                    return (null, null);
                                }

                                var rawSkipUser = firstSyncKey.GetValue("SkipUserStatusPage");
                                if (rawSkipUser != null)
                                    skipUser = Convert.ToInt32(rawSkipUser) != 0;

                                var rawSkipDevice = firstSyncKey.GetValue("SkipDeviceStatusPage");
                                if (rawSkipDevice != null)
                                    skipDevice = Convert.ToInt32(rawSkipDevice) != 0;
                            }

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"EspSkipConfigurationProbe: read threw: {ex.Message}");
            }

            return (skipUser, skipDevice);
        }
    }
}
