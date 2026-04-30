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
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\TenantInfo\{TenantId}</c>
    /// — the sub-key name itself is the TenantId GUID. Written by AAD join, which in
    /// Autopilot/OOBE happens before MDM enrollment.
    /// </description></item>
    /// <item><description>
    /// <c>HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo\{thumbprint}</c>
    /// value <c>TenantId</c>. Same AAD-join phase as TenantInfo, used as a secondary
    /// reading because some flows populate one but not the other.
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
        public const string SourceCloudDomainJoinTenantInfo = "cloud_domain_join_tenant_info";
        public const string SourceCloudDomainJoinJoinInfo = "cloud_domain_join_join_info";

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

            LogResolutionMiss(logger, diagnostics);
            return null;
        }

        private static RegistryKey OpenLocalMachine64() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        private static string TryEnrollmentsRegistry(ResolverDiagnostics diag)
        {
            try
            {
                using (var hklm64 = OpenLocalMachine64())
                using (var enrollmentsKey = hklm64.OpenSubKey(EnrollmentsKeyPath))
                {
                    if (enrollmentsKey == null)
                    {
                        diag.EnrollmentsRootMissing = true;
                        return null;
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

                            if (typeValue == IntuneMdmEnrollmentType)
                            {
                                var enrollmentState = enrollmentKey.GetValue("EnrollmentState")?.ToString();
                                diag.IntuneMdmEnrollments.Add(new IntuneMdmSnapshot
                                {
                                    Guid = subKeyName,
                                    EnrollmentState = enrollmentState,
                                    HasAadTenantId = !string.IsNullOrEmpty(aadTenantId),
                                });

                                if (!string.IsNullOrEmpty(aadTenantId))
                                    return aadTenantId;
                            }
                            else
                            {
                                diag.OtherEnrollmentTypeCount++;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                diag.EnrollmentsProbeError = ex.GetType().Name + ": " + ex.Message;
            }

            return null;
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
                            $"AADTenantID={(e.HasAadTenantId ? "<present>" : "<missing>")}");
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
        }

        private sealed class ResolverDiagnostics
        {
            public bool EnrollmentsRootMissing;
            public string EnrollmentsProbeError;
            public readonly List<IntuneMdmSnapshot> IntuneMdmEnrollments = new List<IntuneMdmSnapshot>();
            public int OtherEnrollmentTypeCount;
            public int EnrollmentsStructuralCount;

            public bool TenantInfoRootMissing;
            public string TenantInfoProbeError;
            public readonly List<string> TenantInfoSubKeys = new List<string>();

            public bool JoinInfoRootMissing;
            public string JoinInfoProbeError;
            public readonly List<JoinInfoSnapshot> JoinInfoEntries = new List<JoinInfoSnapshot>();
        }

        private struct IntuneMdmSnapshot
        {
            public string Guid;
            public string EnrollmentState;
            public bool HasAadTenantId;
        }

        private struct JoinInfoSnapshot
        {
            public string Thumbprint;
            public bool HasTenantId;
        }
    }
}
