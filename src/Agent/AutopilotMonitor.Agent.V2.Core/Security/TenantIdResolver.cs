using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Resolves the device's AAD TenantId from one of several Windows registry signals.
    /// <para>
    /// All registry reads go through the explicit <see cref="RegistryView.Registry64"/>
    /// view, never <see cref="RegistryView.Default"/>. Reason: SDK-style net48 EXEs
    /// may resolve AnyCPU to 32-bit at runtime depending on MSBuild defaults, in which
    /// case <c>HKLM\SOFTWARE\Microsoft\Enrollments</c> silently redirects to the
    /// <c>WOW6432Node</c> mirror, a different (and on most devices stale/empty) hive
    /// that never carries the active MDM enrollment. Forcing Registry64 makes the
    /// resolver bitness-independent regardless of how AnyCPU resolves at runtime.
    /// </para>
    /// <para>
    /// Probe order, first non-empty hit wins:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <c>HKLM\SOFTWARE\Microsoft\Enrollments\{guid}</c> with <c>EnrollmentType=6</c>
    /// (Intune MDM) and <c>AADTenantID</c>. Authoritative once MDM enrollment finished.
    /// </description></item>
    /// <item><description>
    /// Same root, any other <c>EnrollmentType</c> with a non-empty <c>AADTenantID</c>
    /// (e.g. Type=26 AAD-Join entry stamped during Autopilot pre-provisioning with the
    /// <c>fooUser@…onmicrosoft.com</c> placeholder). Picks up the TenantID before the
    /// Type=6 sub-key has been fully populated — observed in hybrid/pre-provisioning
    /// flows where Type=6 sits at <c>EnrollmentState=1</c> (Discovered) with
    /// <c>AADTenantID</c> still missing.
    /// </description></item>
    /// <item><description>
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\TenantInfo\{TenantId}</c>
    /// — the sub-key name itself is the TenantId GUID. Written by AAD join, which in
    /// Autopilot/OOBE happens before MDM enrollment.
    /// </description></item>
    /// <item><description>
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo\{thumbprint}</c>
    /// value <c>TenantId</c>. Same AAD-join phase as TenantInfo, used as a secondary
    /// reading because some flows populate one but not the other.
    /// </description></item>
    /// <item><description>
    /// <c>MS-Organization-Access</c> device cert in <c>LocalMachine\My</c>, OID
    /// <c>1.2.840.113556.1.5.284.5</c>. Last-resort synchronous probe — the cert can
    /// land before the CloudDomainJoin registry writes complete, so this lets the agent
    /// boot without falling through to the registry-watcher awaiter and burning the
    /// 600s wait window. See <see cref="EntraDeviceCertHelper"/>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Returns <c>null</c> when no source carries a TenantId. Never throws. On miss the
    /// resolver dumps a compact diagnostic summary so the agent log carries enough
    /// evidence to classify "device truly not enrolled" vs. "agent fired before
    /// enrollment finished writing".
    /// </para>
    /// </summary>
    public static class TenantIdResolver
    {
        private const string EnrollmentsKeyPath = @"SOFTWARE\Microsoft\Enrollments";
        private const string CloudDomainJoinTenantInfoPath = @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\TenantInfo";
        private const string CloudDomainJoinJoinInfoPath = @"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo";
        private const int IntuneMdmEnrollmentType = 6;

        public const string SourceEnrollmentsRegistry = "enrollments_registry";
        public const string SourceEnrollmentsRegistryFallback = "enrollments_registry_fallback";
        public const string SourceCloudDomainJoinTenantInfo = "cloud_domain_join_tenant_info";
        public const string SourceCloudDomainJoinJoinInfo = "cloud_domain_join_join_info";
        public const string SourceMsOrganizationAccessCert = "ms_organization_access_cert";

        /// <summary>
        /// Tries each probe in order and returns the first non-empty TenantId.
        /// Logs the winning source at Info level. On full miss, logs a diagnostic
        /// summary at Warning level. Returns <c>null</c> when nothing resolves.
        /// </summary>
        public static string Resolve(AgentLogger logger = null)
        {
            var diagnostics = new ResolverDiagnostics();

            var fromEnrollments = TryEnrollmentsRegistry(diagnostics);
            if (!string.IsNullOrEmpty(fromEnrollments))
            {
                logger?.Info($"TenantIdResolver: resolved TenantId={fromEnrollments} from {SourceEnrollmentsRegistry}.");
                return fromEnrollments;
            }

            var fromEnrollmentsFallback = TryEnrollmentsRegistryFallback(diagnostics, out var fallbackType, out var fallbackUpn);
            if (!string.IsNullOrEmpty(fromEnrollmentsFallback))
            {
                logger?.Info($"TenantIdResolver: resolved TenantId={fromEnrollmentsFallback} from {SourceEnrollmentsRegistryFallback} (Type={fallbackType}; UPN={FormatUpnForLog(fallbackUpn)}; no Type=6 sub-key carried AADTenantID yet — typical Autopilot pre-provisioning / hybrid window).");
                return fromEnrollmentsFallback;
            }

            var fromTenantInfo = TryCloudDomainJoinTenantInfo(diagnostics);
            if (!string.IsNullOrEmpty(fromTenantInfo))
            {
                logger?.Info($"TenantIdResolver: resolved TenantId={fromTenantInfo} from {SourceCloudDomainJoinTenantInfo} (Enrollments registry had no Intune MDM hit).");
                return fromTenantInfo;
            }

            var fromJoinInfo = TryCloudDomainJoinJoinInfo(diagnostics);
            if (!string.IsNullOrEmpty(fromJoinInfo))
            {
                logger?.Info($"TenantIdResolver: resolved TenantId={fromJoinInfo} from {SourceCloudDomainJoinJoinInfo} (Enrollments registry + CloudDomainJoin\\TenantInfo had no hit).");
                return fromJoinInfo;
            }

            // Last synchronous probe: parse the MS-Organization-Access device cert.
            // EntraDeviceCertHelper logs its own miss-reason warnings, so just record the attempt
            // here and let a non-null result short-circuit the awaiter wait window.
            diagnostics.MsOrgAccessCertProbed = true;
            var fromCert = EntraDeviceCertHelper.TryGetTenantIdFromCert(logger);
            if (fromCert != null)
            {
                var tenantIdString = fromCert.Value.ToString("D");
                logger?.Info($"TenantIdResolver: resolved TenantId={tenantIdString} from {SourceMsOrganizationAccessCert} (no registry source carried it yet).");
                return tenantIdString;
            }
            diagnostics.MsOrgAccessCertMissed = true;

            LogResolutionMiss(logger, diagnostics);
            return null;
        }

        private static RegistryKey OpenLocalMachine64() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        private static string TryEnrollmentsRegistry(ResolverDiagnostics diag)
        {
            CollectEnrollmentSnapshots(diag);

            foreach (var e in diag.IntuneMdmEnrollments)
            {
                if (!string.IsNullOrEmpty(e.AadTenantId))
                    return e.AadTenantId;
            }

            return null;
        }

        /// <summary>
        /// Pass 2: walks the same Enrollments snapshot collected by pass 1 and returns the
        /// first non-Type=6 sub-key carrying a non-empty <c>AADTenantID</c>. Targets the
        /// Autopilot pre-provisioning / hybrid window where Type=6 sits at
        /// <c>EnrollmentState=1</c> with <c>AADTenantID</c> still missing while a Type=26
        /// AAD-Join entry already carries the TenantID.
        /// </summary>
        private static string TryEnrollmentsRegistryFallback(ResolverDiagnostics diag, out int? winningType, out string winningUpn)
        {
            winningType = null;
            winningUpn = null;

            // CollectEnrollmentSnapshots already ran in pass 1 and populated diag.OtherEnrollmentsWithTenantId.
            // No second registry walk needed.
            foreach (var e in diag.OtherEnrollmentsWithTenantId)
            {
                if (!string.IsNullOrEmpty(e.AadTenantId))
                {
                    winningType = e.EnrollmentType;
                    winningUpn = e.Upn;
                    return e.AadTenantId;
                }
            }

            return null;
        }

        private static void CollectEnrollmentSnapshots(ResolverDiagnostics diag)
        {
            if (diag.EnrollmentsCollected) return;
            diag.EnrollmentsCollected = true;

            try
            {
                using (var hklm64 = OpenLocalMachine64())
                using (var enrollmentsKey = hklm64.OpenSubKey(EnrollmentsKeyPath))
                {
                    if (enrollmentsKey == null)
                    {
                        diag.EnrollmentsRootMissing = true;
                        return;
                    }

                    foreach (var subKeyName in enrollmentsKey.GetSubKeyNames())
                    {
                        using (var enrollmentKey = enrollmentsKey.OpenSubKey(subKeyName))
                        {
                            if (enrollmentKey == null) continue;

                            var enrollmentType = enrollmentKey.GetValue("EnrollmentType");
                            int? typeValue = null;
                            if (enrollmentType != null)
                            {
                                try { typeValue = Convert.ToInt32(enrollmentType); }
                                catch { /* unparseable — record as null */ }
                            }

                            // Sub-keys without EnrollmentType are well-known structural folders
                            // (Context, Ownership, Status, ValidNodePaths). Skip them silently —
                            // they never carry AADTenantID and only add log noise on miss.
                            if (typeValue == null)
                            {
                                diag.EnrollmentsStructuralCount++;
                                continue;
                            }

                            var aadTenantId = enrollmentKey.GetValue("AADTenantID")?.ToString();
                            var upn = enrollmentKey.GetValue("UPN")?.ToString();

                            if (typeValue == IntuneMdmEnrollmentType)
                            {
                                var enrollmentState = enrollmentKey.GetValue("EnrollmentState")?.ToString();
                                diag.IntuneMdmEnrollments.Add(new IntuneMdmSnapshot
                                {
                                    Guid = subKeyName,
                                    EnrollmentState = enrollmentState,
                                    AadTenantId = aadTenantId,
                                    Upn = upn,
                                });
                            }
                            else
                            {
                                diag.OtherEnrollmentTypeCount++;
                                if (!string.IsNullOrEmpty(aadTenantId))
                                {
                                    diag.OtherEnrollmentsWithTenantId.Add(new OtherEnrollmentSnapshot
                                    {
                                        Guid = subKeyName,
                                        EnrollmentType = typeValue.Value,
                                        AadTenantId = aadTenantId,
                                        Upn = upn,
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diag.EnrollmentsProbeError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string TryCloudDomainJoinTenantInfo(ResolverDiagnostics diag)
        {
            try
            {
                using (var hklm64 = OpenLocalMachine64())
                using (var tenantInfoKey = hklm64.OpenSubKey(CloudDomainJoinTenantInfoPath))
                {
                    if (tenantInfoKey == null)
                    {
                        diag.TenantInfoRootMissing = true;
                        return null;
                    }

                    var subKeys = tenantInfoKey.GetSubKeyNames();
                    diag.TenantInfoSubKeys.AddRange(subKeys);

                    foreach (var subKey in subKeys)
                    {
                        if (Guid.TryParse(subKey, out _))
                            return subKey;
                    }
                }
            }
            catch (Exception ex)
            {
                diag.TenantInfoProbeError = ex.GetType().Name + ": " + ex.Message;
            }

            return null;
        }

        private static string TryCloudDomainJoinJoinInfo(ResolverDiagnostics diag)
        {
            try
            {
                using (var hklm64 = OpenLocalMachine64())
                using (var joinInfoKey = hklm64.OpenSubKey(CloudDomainJoinJoinInfoPath))
                {
                    if (joinInfoKey == null)
                    {
                        diag.JoinInfoRootMissing = true;
                        return null;
                    }

                    foreach (var thumbprint in joinInfoKey.GetSubKeyNames())
                    {
                        using (var entry = joinInfoKey.OpenSubKey(thumbprint))
                        {
                            if (entry == null) continue;

                            var tenantId = entry.GetValue("TenantId")?.ToString();
                            diag.JoinInfoEntries.Add(new JoinInfoSnapshot
                            {
                                Thumbprint = thumbprint,
                                HasTenantId = !string.IsNullOrEmpty(tenantId),
                            });

                            if (!string.IsNullOrEmpty(tenantId))
                                return tenantId;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diag.JoinInfoProbeError = ex.GetType().Name + ": " + ex.Message;
            }

            return null;
        }

        private static void LogResolutionMiss(AgentLogger logger, ResolverDiagnostics diag)
        {
            if (logger == null) return;

            logger.Warning("TenantIdResolver: no TenantId resolvable from registry — device likely not yet AAD-joined / MDM-enrolled, or enrollment write hasn't completed.");

            // Enrollments root.
            if (diag.EnrollmentsProbeError != null)
            {
                logger.Warning($"TenantIdResolver: Enrollments probe failed: {diag.EnrollmentsProbeError}");
            }
            else if (diag.EnrollmentsRootMissing)
            {
                logger.Warning($"TenantIdResolver: HKLM\\{EnrollmentsKeyPath} not found (Registry64 view).");
            }
            else
            {
                logger.Warning(
                    $"TenantIdResolver: HKLM\\{EnrollmentsKeyPath} (Registry64) — " +
                    $"Intune-MDM (Type=6): {diag.IntuneMdmEnrollments.Count}, " +
                    $"other enrollment types: {diag.OtherEnrollmentTypeCount}, " +
                    $"structural sub-keys: {diag.EnrollmentsStructuralCount}.");

                if (diag.IntuneMdmEnrollments.Count == 0)
                {
                    logger.Warning("TenantIdResolver: no Type=6 enrollment present yet — MDM enrollment hasn't reached registry write phase.");
                }
                else
                {
                    foreach (var e in diag.IntuneMdmEnrollments)
                    {
                        logger.Warning(
                            $"  - Type=6 {e.Guid}: EnrollmentState={e.EnrollmentState ?? "<missing>"}, " +
                            $"AADTenantID={(string.IsNullOrEmpty(e.AadTenantId) ? "<missing>" : "<present>")}, " +
                            $"UPN={FormatUpnForLog(e.Upn)}");
                    }
                }

                if (diag.OtherEnrollmentsWithTenantId.Count > 0)
                {
                    logger.Warning(
                        $"TenantIdResolver: {diag.OtherEnrollmentsWithTenantId.Count} non-Type-6 enrollment(s) carried a non-empty AADTenantID but pass-2 still returned null (should not happen — investigate):");
                    foreach (var o in diag.OtherEnrollmentsWithTenantId)
                    {
                        logger.Warning(
                            $"  - Type={o.EnrollmentType} {o.Guid}: AADTenantID=<present>, UPN={FormatUpnForLog(o.Upn)}");
                    }
                }
            }

            // CloudDomainJoin\TenantInfo.
            if (diag.TenantInfoProbeError != null)
                logger.Warning($"TenantIdResolver: TenantInfo probe failed: {diag.TenantInfoProbeError}");
            else if (diag.TenantInfoRootMissing)
                logger.Warning($"TenantIdResolver: HKLM\\{CloudDomainJoinTenantInfoPath} not found (device not AAD-joined yet).");
            else if (diag.TenantInfoSubKeys.Count == 0)
                logger.Warning($"TenantIdResolver: HKLM\\{CloudDomainJoinTenantInfoPath} present but no sub-keys.");
            else
                logger.Warning($"TenantIdResolver: HKLM\\{CloudDomainJoinTenantInfoPath} sub-keys: [{string.Join(", ", diag.TenantInfoSubKeys)}] — none parsed as a GUID.");

            // CloudDomainJoin\JoinInfo.
            if (diag.JoinInfoProbeError != null)
                logger.Warning($"TenantIdResolver: JoinInfo probe failed: {diag.JoinInfoProbeError}");
            else if (diag.JoinInfoRootMissing)
                logger.Warning($"TenantIdResolver: HKLM\\{CloudDomainJoinJoinInfoPath} not found.");
            else if (diag.JoinInfoEntries.Count == 0)
                logger.Warning($"TenantIdResolver: HKLM\\{CloudDomainJoinJoinInfoPath} present but no thumbprint sub-keys.");
            else
            {
                logger.Warning($"TenantIdResolver: HKLM\\{CloudDomainJoinJoinInfoPath} carries {diag.JoinInfoEntries.Count} entry/entries but none had a non-empty TenantId value:");
                foreach (var j in diag.JoinInfoEntries)
                    logger.Warning($"  - thumbprint={j.Thumbprint}, TenantId={(j.HasTenantId ? "<present>" : "<missing>")}");
            }

            // MS-Organization-Access cert probe (EntraDeviceCertHelper logs its own miss reason — this
            // is just a breadcrumb that the resolver did try the cert path before giving up).
            if (diag.MsOrgAccessCertProbed && diag.MsOrgAccessCertMissed)
                logger.Warning($"TenantIdResolver: {SourceMsOrganizationAccessCert} probe also missed (see EntraDeviceCertHelper warning above for the specific reason).");
        }

        /// <summary>
        /// The two well-known placeholder accounts written into enrollment registry sub-keys
        /// during Autopilot pre-provisioning. Both are generated by Windows from the
        /// <c>MakeFakeUserEmail</c> code path and are NOT real users — surfacing them in the
        /// log is safe and useful for diagnostics. Real user UPNs are PII and must NOT be
        /// logged.
        /// </summary>
        public enum PlaceholderUpnKind
        {
            /// <summary>Not a known placeholder — could be a real user UPN.</summary>
            None = 0,
            /// <summary><c>fooUser@&lt;tenant&gt;.onmicrosoft.com</c> — used for the Intune-side
            /// (MDM) enrollment leg during Autopilot pre-provisioning.</summary>
            FooUser = 1,
            /// <summary><c>autopilot@&lt;tenant&gt;.onmicrosoft.com</c> — used for the Entra
            /// (AAD-Join) leg during Autopilot pre-provisioning.</summary>
            Autopilot = 2,
        }

        /// <summary>
        /// Classifies a UPN read from an enrollment registry sub-key as either a known
        /// Autopilot pre-provisioning placeholder or "could be a real user". Match is
        /// prefix-based and case-insensitive (Microsoft documents both <c>fooUser@</c> and
        /// <c>foouser@</c> casings).
        /// </summary>
        public static PlaceholderUpnKind ClassifyPlaceholderUpn(string upn)
        {
            if (string.IsNullOrEmpty(upn)) return PlaceholderUpnKind.None;
            if (upn.StartsWith("fooUser@", StringComparison.OrdinalIgnoreCase)) return PlaceholderUpnKind.FooUser;
            if (upn.StartsWith("autopilot@", StringComparison.OrdinalIgnoreCase)) return PlaceholderUpnKind.Autopilot;
            return PlaceholderUpnKind.None;
        }

        /// <summary>
        /// Renders a UPN for the agent log. Known placeholders are logged in full plus an
        /// annotation. Anything else is treated as a real user UPN and redacted to
        /// <c>&lt;redacted_real_user_upn&gt;</c> — even partial UPN exposure is PII.
        /// </summary>
        private static string FormatUpnForLog(string upn)
        {
            if (string.IsNullOrEmpty(upn)) return "<missing>";
            switch (ClassifyPlaceholderUpn(upn))
            {
                case PlaceholderUpnKind.FooUser:
                    return upn + " <autopilot_pre_provisioning_placeholder:fooUser_intune_mdm_leg>";
                case PlaceholderUpnKind.Autopilot:
                    return upn + " <autopilot_pre_provisioning_placeholder:autopilot_entra_join_leg>";
                default:
                    return "<redacted_real_user_upn>";
            }
        }

        private sealed class ResolverDiagnostics
        {
            public bool EnrollmentsCollected;
            public bool EnrollmentsRootMissing;
            public string EnrollmentsProbeError;
            public readonly List<IntuneMdmSnapshot> IntuneMdmEnrollments = new List<IntuneMdmSnapshot>();
            public readonly List<OtherEnrollmentSnapshot> OtherEnrollmentsWithTenantId = new List<OtherEnrollmentSnapshot>();
            public int OtherEnrollmentTypeCount;
            public int EnrollmentsStructuralCount;

            public bool TenantInfoRootMissing;
            public string TenantInfoProbeError;
            public readonly List<string> TenantInfoSubKeys = new List<string>();

            public bool JoinInfoRootMissing;
            public string JoinInfoProbeError;
            public readonly List<JoinInfoSnapshot> JoinInfoEntries = new List<JoinInfoSnapshot>();

            public bool MsOrgAccessCertProbed;
            public bool MsOrgAccessCertMissed;
        }

        private struct IntuneMdmSnapshot
        {
            public string Guid;
            public string EnrollmentState;
            public string AadTenantId;
            public string Upn;
        }

        private struct OtherEnrollmentSnapshot
        {
            public string Guid;
            public int EnrollmentType;
            public string AadTenantId;
            public string Upn;
        }

        private struct JoinInfoSnapshot
        {
            public string Thumbprint;
            public bool HasTenantId;
        }
    }
}
