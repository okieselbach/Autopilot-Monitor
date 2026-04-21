using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Resolves the Autopilot device's AAD TenantId from the MDM enrollment registry.
    /// Plan §4.x M4.5.b.
    /// <para>
    /// The MDM enrollment writes one sub-key per enrollment under
    /// <c>HKLM\SOFTWARE\Microsoft\Enrollments</c>. The entry with <c>EnrollmentType==6</c>
    /// (MDM Intune enrollment) carries <c>AADTenantID</c> — the only value that maps the
    /// device back to its Entra ID tenant without a backend round-trip.
    /// </para>
    /// </summary>
    public static class TenantIdResolver
    {
        private const string EnrollmentsKeyPath = @"SOFTWARE\Microsoft\Enrollments";
        private const int IntuneMdmEnrollmentType = 6;

        /// <summary>
        /// Walks the enrollments subtree and returns the first <c>AADTenantID</c> tagged with
        /// the Intune MDM enrollment type. Returns <c>null</c> when the device is not MDM-enrolled
        /// or the registry key is inaccessible. Never throws.
        /// </summary>
        public static string ResolveFromEnrollmentRegistry(AgentLogger logger = null)
        {
            try
            {
                using (var enrollmentsKey = Registry.LocalMachine.OpenSubKey(EnrollmentsKeyPath))
                {
                    if (enrollmentsKey == null)
                    {
                        logger?.Warning($"TenantIdResolver: {EnrollmentsKeyPath} not found — device likely not MDM-enrolled.");
                        return null;
                    }

                    foreach (var enrollmentGuid in enrollmentsKey.GetSubKeyNames())
                    {
                        using (var enrollmentKey = enrollmentsKey.OpenSubKey(enrollmentGuid))
                        {
                            if (enrollmentKey == null) continue;

                            var enrollmentType = enrollmentKey.GetValue("EnrollmentType");
                            if (enrollmentType == null) continue;

                            int typeValue;
                            try { typeValue = Convert.ToInt32(enrollmentType); }
                            catch { continue; }

                            if (typeValue != IntuneMdmEnrollmentType) continue;

                            var tenantId = enrollmentKey.GetValue("AADTenantID");
                            var resolved = tenantId?.ToString();
                            if (!string.IsNullOrEmpty(resolved))
                            {
                                logger?.Debug($"TenantIdResolver: resolved AADTenantID={resolved} from enrollment {enrollmentGuid}.");
                                return resolved;
                            }
                        }
                    }
                }

                logger?.Warning("TenantIdResolver: no Intune MDM enrollment (EnrollmentType=6) with AADTenantID found.");
                return null;
            }
            catch (Exception ex)
            {
                logger?.Error("TenantIdResolver: registry probe failed.", ex);
                return null;
            }
        }
    }
}
