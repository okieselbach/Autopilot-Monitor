#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Provisioning
{
    /// <summary>
    /// Scans the device for provisioning-package (PPKG) traces and emits a single
    /// <see cref="Constants.EventTypes.ProvisioningPackageScan"/> event with the raw facts.
    /// <para>
    /// PPKGs are containers that inject settings, apps, accounts, WLAN/VPN, certificates and
    /// scripts before or during enrollment. They can come from legitimate bulk enrollment OR be
    /// a manipulation vector. This collector reports facts only — file/registry presence plus a
    /// best-effort, package-scoped content classification. Whether a PPKG is *unexpected* for a
    /// given tenant is deliberately NOT decided here; that is a backend analyze-rule's job.
    /// </para>
    /// <para>
    /// <b>Trigger</b>: invoked once when the ESP DeviceSetup phase starts (see
    /// <c>ProvisioningPackageHost</c>), NOT at agent start — the agent may run a long time via
    /// bootstrap before any PPKG is applied, so an at-start scan would inspect an empty machine.
    /// </para>
    /// <para>
    /// <b>Reliability</b>: file presence + <c>Provisioning\Packages\&lt;id&gt;</c> metadata are
    /// hard facts. Content indicators are best-effort and derived ONLY from the per-package
    /// registry subtree — never from <c>Provisioning\Diagnostics</c>, which records every CSP the
    /// device ever applied (including normal MDM policy) and would yield misleading positives.
    /// Binary <c>.ppkg</c> decoding (CAB/SyncML) is intentionally out of scope.
    /// </para>
    /// </summary>
    public sealed class ProvisioningPackageCollector
    {
        internal const string SourceName = "ProvisioningPackageCollector";

        private const string ProvisioningRootSubKey = @"SOFTWARE\Microsoft\Provisioning";
        private const string PackagesSubKey = @"SOFTWARE\Microsoft\Provisioning\Packages";
        private const string OmadmAccountsSubKey = @"SOFTWARE\Microsoft\Provisioning\OMADM\Accounts";
        private const string DiagnosticsSubKey = @"SOFTWARE\Microsoft\Provisioning\Diagnostics";

        // Bound registry recursion so a pathological hive cannot hang the scan.
        private const int MaxRegistryNodesPerPackage = 512;
        // Cap collection lists so a flooded directory/hive cannot blow up the event payload.
        private const int MaxPpkgFiles = 50;
        private const int MaxPackages = 50;
        private const int MaxRecoveryFiles = 50;

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly IClock? _clock;

        public ProvisioningPackageCollector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            IClock? clock = null)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock;
        }

        /// <summary>
        /// Runs the full scan and emits exactly one <c>provisioning_package_scan</c> event.
        /// Fail-soft end to end: any probe error is captured into <c>scanErrors</c> and the event
        /// is still emitted (so the backend can distinguish "scanned clean" from "never scanned").
        /// </summary>
        public void Scan()
        {
            _logger.Info("ProvisioningPackageCollector: scanning for provisioning-package traces (DeviceSetup trigger).");

            ProvisioningScanFindings findings;
            try
            {
                findings = Collect();
            }
            catch (Exception ex)
            {
                // Should never happen — Collect() is itself fail-soft — but never let a scan
                // crash take down the host's Task.Run.
                findings = new ProvisioningScanFindings();
                findings.Errors.Add($"collect:{ex.GetType().Name}: {ex.Message}");
                _logger.Warning($"ProvisioningPackageCollector: Collect threw: {ex.Message}");
            }

            var payload = BuildPayload(findings);
            var anyFound = payload.TryGetValue("anyPpkgFound", out var f) && f is bool b && b;
            var message = anyFound
                ? $"Provisioning-package traces detected (files={findings.Files.Count}, packages={findings.Packages.Count}, recoveryResidue={findings.RecoveryCustomizationsFiles.Count})"
                : "No provisioning-package traces detected";

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.ProvisioningPackageScan,
                Severity = EventSeverity.Info,
                Source = SourceName,
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = payload,
                ImmediateUpload = true,
            });

            // One scalar event per detected artifact so analyze rules can match per package
            // (incl. the allow-list template's not_regex on `identity`). The aggregate event
            // above keeps the "scanned clean" / counts baseline. None emitted on a clean device.
            EmitPerPackageEvents(findings);

            _logger.Info($"ProvisioningPackageCollector: scan complete (anyPpkgFound={anyFound}, packages={findings.Packages.Count}, files={findings.Files.Count}, errors={findings.Errors.Count}).");
        }

        private void EmitPerPackageEvents(ProvisioningScanFindings findings)
        {
            foreach (var data in BuildDetectedEvents(findings))
            {
                EmitDetected(data);
            }
        }

        /// <summary>
        /// The full set of per-artifact <c>provisioning_package_detected</c> payloads: one per
        /// registry package, one per .ppkg file, and one per Recovery\Customizations residue file
        /// that is not already covered by a .ppkg file event. <c>BuildPayload</c> derives
        /// <c>anyPpkgFound</c> from this same set, so the scan verdict and the events the analyze
        /// rules match on stay consistent by construction.
        /// <para>
        /// Residue dedup is by actual captured path, NOT by ".ppkg" extension: a recovery .ppkg
        /// whose recursive enumeration failed or was truncated is absent from <c>findings.Files</c>,
        /// so it is NOT silently dropped here — it still gets a (recovery_residue) detected event.
        /// </para>
        /// </summary>
        internal static List<Dictionary<string, object>> BuildDetectedEvents(ProvisioningScanFindings findings)
        {
            var list = new List<Dictionary<string, object>>();

            foreach (var pkg in findings.Packages)
                list.Add(BuildPackageEventData(pkg));

            foreach (var file in findings.Files)
                list.Add(BuildFileEventData(file));

            // Paths already emitted as .ppkg file events — used to dedup recovery residue.
            var capturedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in findings.Files)
            {
                if (!string.IsNullOrEmpty(file.FullPath)) capturedPaths.Add(file.FullPath);
            }

            foreach (var name in findings.RecoveryCustomizationsFiles)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var fullPath = string.IsNullOrEmpty(findings.RecoveryCustomizationsDir)
                    ? name
                    : Path.Combine(findings.RecoveryCustomizationsDir, name);
                // Skip only when this exact file is already emitted as a .ppkg file event.
                if (capturedPaths.Contains(fullPath)) continue;
                list.Add(BuildRecoveryResidueEventData(name, findings.RecoveryCustomizationsDir));
            }

            return list;
        }

        private void EmitDetected(Dictionary<string, object> data)
        {
            var identity = data.TryGetValue("identity", out var id) ? id?.ToString() : null;
            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = Constants.EventTypes.ProvisioningPackageDetected,
                Severity = EventSeverity.Info,
                Source = SourceName,
                Phase = EnrollmentPhase.Unknown,
                Message = string.IsNullOrEmpty(identity)
                    ? "Provisioning package artifact detected"
                    : $"Provisioning package artifact detected: {identity}",
                Data = data,
                ImmediateUpload = true,
            });
        }

        // ----------------------------------------------------------------------------------
        // IO probes (fail-soft) — each catch appends to findings.Errors and continues.
        // ----------------------------------------------------------------------------------

        internal ProvisioningScanFindings Collect()
        {
            var findings = new ProvisioningScanFindings();
            CollectFiles(findings);
            CollectRegistry(findings);
            return findings;
        }

        private void CollectFiles(ProvisioningScanFindings findings)
        {
            string windows = SafeFolder(() => Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"C:\Windows");
            string programData = SafeFolder(() => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"C:\ProgramData");
            string systemDrive = SafeFolder(() => Path.GetPathRoot(windows) ?? @"C:\", @"C:\");

            var ppkgDirs = new[]
            {
                Path.Combine(programData, "Microsoft", "Provisioning"),
                Path.Combine(windows, "Provisioning"),
                Path.Combine(systemDrive, "Recovery", "Customizations"),
                Path.Combine(windows, "System32", "Provisioning"),
            };

            foreach (var dir in ppkgDirs)
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;

                    foreach (var file in Directory.EnumerateFiles(dir, "*.ppkg", SearchOption.AllDirectories))
                    {
                        if (findings.Files.Count >= MaxPpkgFiles) { findings.FilesTruncated = true; break; }
                        try
                        {
                            var info = new FileInfo(file);
                            findings.Files.Add(new PpkgFileFact
                            {
                                Directory = dir,
                                Name = info.Name,
                                FullPath = file,
                                SizeBytes = info.Exists ? info.Length : 0,
                                LastWriteUtc = info.Exists ? info.LastWriteTimeUtc.ToString("o") : null,
                            });
                        }
                        catch (Exception ex)
                        {
                            findings.Errors.Add($"file:{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    findings.Errors.Add($"dir:{Trunc(dir)}:{ex.GetType().Name}: {ex.Message}");
                }
            }

            // Recovery\Customizations residue — ANY leftover file counts, not just .ppkg.
            var recoveryDir = Path.Combine(systemDrive, "Recovery", "Customizations");
            findings.RecoveryCustomizationsDir = recoveryDir;
            try
            {
                if (Directory.Exists(recoveryDir))
                {
                    foreach (var file in Directory.EnumerateFiles(recoveryDir, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (findings.RecoveryCustomizationsFiles.Count >= MaxRecoveryFiles)
                        {
                            findings.RecoveryFilesTruncated = true;
                            break;
                        }
                        findings.RecoveryCustomizationsFiles.Add(Path.GetFileName(file));
                    }
                }
            }
            catch (Exception ex)
            {
                findings.Errors.Add($"recovery:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CollectRegistry(ProvisioningScanFindings findings)
        {
            // Forced Registry64 view — AnyCPU net48 may resolve to 32-bit and silently read the
            // stale WOW6432Node mirror (same rationale as TenantIdResolver).
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    findings.ProvisioningRootPresent = TryKeyExists(hklm, ProvisioningRootSubKey);
                    CollectPackages(hklm, findings);
                    findings.OmadmAccountsPresent = TryHasSubKeys(hklm, OmadmAccountsSubKey);
                    findings.DiagnosticsPresent = TryKeyExists(hklm, DiagnosticsSubKey);
                }
            }
            catch (Exception ex)
            {
                findings.Errors.Add($"registry:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void CollectPackages(RegistryKey hklm, ProvisioningScanFindings findings)
        {
            try
            {
                using (var packagesKey = hklm.OpenSubKey(PackagesSubKey))
                {
                    if (packagesKey == null) return;

                    foreach (var packageId in packagesKey.GetSubKeyNames())
                    {
                        if (findings.Packages.Count >= MaxPackages) { findings.PackagesTruncated = true; break; }
                        try
                        {
                            using (var pkgKey = packagesKey.OpenSubKey(packageId))
                            {
                                if (pkgKey == null) continue;

                                var pkg = new PpkgPackageFact
                                {
                                    PackageId = packageId,
                                    Name = ReadString(pkgKey, "Name") ?? ReadString(pkgKey, "PackageName"),
                                    OwnerType = ReadString(pkgKey, "OwnerType"),
                                    Rank = ReadString(pkgKey, "Rank"),
                                    InstallTime = ReadString(pkgKey, "InstallTime") ?? ReadString(pkgKey, "InstalledTime"),
                                };

                                // Package-scoped subtree key-names → feeds the best-effort content
                                // indicators. Scoped to THIS package only (never the global
                                // Diagnostics tree) to avoid MDM-policy false positives.
                                var budget = MaxRegistryNodesPerPackage;
                                CollectSubKeyNames(pkgKey, pkg.SubKeyNames, ref budget);

                                findings.Packages.Add(pkg);
                            }
                        }
                        catch (Exception ex)
                        {
                            findings.Errors.Add($"package:{Trunc(packageId)}:{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                findings.Errors.Add($"packages:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void CollectSubKeyNames(RegistryKey key, List<string> sink, ref int budget)
        {
            if (budget <= 0) return;
            string[] names;
            try { names = key.GetSubKeyNames(); }
            catch { return; }

            foreach (var name in names)
            {
                if (budget <= 0) return;
                budget--;
                sink.Add(name);
                try
                {
                    using (var child = key.OpenSubKey(name))
                    {
                        if (child != null) CollectSubKeyNames(child, sink, ref budget);
                    }
                }
                catch { /* fail-soft: skip unreadable subkey */ }
            }
        }

        // ----------------------------------------------------------------------------------
        // Pure aggregation — no IO, unit-tested directly.
        // ----------------------------------------------------------------------------------

        internal static Dictionary<string, object> BuildPayload(ProvisioningScanFindings findings)
        {
            // Derive anyPpkgFound from the SAME set that produces per-package
            // `provisioning_package_detected` events, so the aggregate "found" verdict and the
            // events the analyze rules listen on can never diverge (Recovery-residue-only gap fix).
            var anyPpkgFound = BuildDetectedEvents(findings).Count > 0;

            var ppkgFiles = findings.Files
                .Select(file => new Dictionary<string, object>
                {
                    ["dir"] = file.Directory,
                    ["name"] = file.Name,
                    ["path"] = file.FullPath,
                    ["sizeBytes"] = file.SizeBytes,
                    ["lastWriteUtc"] = (object?)file.LastWriteUtc ?? string.Empty,
                })
                .ToList();

            var packages = findings.Packages
                .Select(pkg =>
                {
                    var d = new Dictionary<string, object> { ["packageId"] = pkg.PackageId };
                    if (!string.IsNullOrEmpty(pkg.Name)) d["name"] = pkg.Name!;
                    if (!string.IsNullOrEmpty(pkg.OwnerType)) d["ownerType"] = pkg.OwnerType!;
                    if (!string.IsNullOrEmpty(pkg.Rank)) d["rank"] = pkg.Rank!;
                    if (!string.IsNullOrEmpty(pkg.InstallTime)) d["installTime"] = pkg.InstallTime!;
                    if (pkg.SubKeyNames.Count > 0) d["registrySubKeys"] = pkg.SubKeyNames.ToList();
                    return d;
                })
                .ToList();

            return new Dictionary<string, object>
            {
                ["anyPpkgFound"] = anyPpkgFound,
                ["ppkgFileCount"] = findings.Files.Count,
                ["ppkgFiles"] = ppkgFiles,
                ["ppkgFilesTruncated"] = findings.FilesTruncated,
                ["packageCount"] = findings.Packages.Count,
                ["packages"] = packages,
                ["packagesTruncated"] = findings.PackagesTruncated,
                ["recoveryCustomizationsResidue"] = findings.RecoveryCustomizationsFiles.Count > 0,
                ["recoveryCustomizationsFiles"] = findings.RecoveryCustomizationsFiles.ToList(),
                ["recoveryFilesTruncated"] = findings.RecoveryFilesTruncated,
                // Context only — present on virtually every MDM-enrolled device, NOT a PPKG signal.
                ["omadmAccountsPresent"] = findings.OmadmAccountsPresent,
                ["provisioningDiagnosticsPresent"] = findings.DiagnosticsPresent,
                ["contentIndicators"] = BuildContentIndicators(findings),
                ["scanErrors"] = findings.Errors.ToList(),
            };
        }

        /// <summary>
        /// Best-effort content classification derived ONLY from per-package registry subkey names.
        /// Absence of an indicator does NOT prove the category is absent (a full answer needs
        /// binary <c>.ppkg</c> decoding, which is out of scope). Always carries a <c>note</c>.
        /// </summary>
        internal static Dictionary<string, object> BuildContentIndicators(ProvisioningScanFindings findings)
        {
            bool localAccounts = false, certificates = false, wifi = false,
                 vpn = false, appManagement = false, scripts = false;

            foreach (var pkg in findings.Packages)
            {
                foreach (var raw in pkg.SubKeyNames)
                {
                    var n = raw.ToLowerInvariant();
                    if (n.Contains("account")) localAccounts = true;
                    if (n.Contains("certificate") || n.Contains("rootcatrusted") || n.Contains("clientcertificate")) certificates = true;
                    if (n.Contains("wifi") || n.Contains("wlan")) wifi = true;
                    if (n.Contains("vpn")) vpn = true;
                    if (n.Contains("enterprisedesktopappmanagement") || n.Contains("enterprisemodernappmanagement") || n.Contains("msi")) appManagement = true;
                    if (n.Contains("provisioningcommand") || n.Contains("script")) scripts = true;
                }
            }

            return new Dictionary<string, object>
            {
                ["localAccounts"] = localAccounts,
                ["certificates"] = certificates,
                ["wifiProfiles"] = wifi,
                ["vpnProfiles"] = vpn,
                ["appManagement"] = appManagement,
                ["scripts"] = scripts,
                ["note"] = "best-effort; derived from per-package registry subkey names only — absence does not prove a category is absent (no binary .ppkg decoding)",
            };
        }

        // ----------------------------------------------------------------------------------
        // Per-package event projection — scalar fields only (rule-engine matchable).
        // ----------------------------------------------------------------------------------

        /// <summary>
        /// Combined allow-list match key: name | fileName | ownerType | packageId (non-empty,
        /// distinct). A single allow-list regex (`not_regex` on this field) can match a known
        /// vendor PPKG by any of its identifiers.
        /// </summary>
        internal static string BuildIdentity(string? name, string? fileName, string? ownerType, string? packageId)
        {
            var parts = new[] { name, fileName, ownerType, packageId }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Trim());

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ordered = new List<string>();
            foreach (var p in parts)
            {
                if (seen.Add(p)) ordered.Add(p);
            }
            return string.Join(" | ", ordered);
        }

        internal static Dictionary<string, object> BuildPackageEventData(PpkgPackageFact pkg)
        {
            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["source"] = "registry",
                ["packageId"] = pkg.PackageId,
                ["packageName"] = pkg.Name ?? string.Empty,
                ["ownerType"] = pkg.OwnerType ?? string.Empty,
                ["rank"] = pkg.Rank ?? string.Empty,
                ["installTime"] = pkg.InstallTime ?? string.Empty,
                ["identity"] = BuildIdentity(pkg.Name, fileName: null, ownerType: pkg.OwnerType, packageId: pkg.PackageId),
            };
            return data;
        }

        internal static Dictionary<string, object> BuildFileEventData(PpkgFileFact file)
        {
            var data = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["source"] = "file",
                ["fileName"] = file.Name,
                ["dir"] = file.Directory,
                ["path"] = file.FullPath,
                ["sizeBytes"] = file.SizeBytes,
                ["identity"] = BuildIdentity(name: null, fileName: file.Name, ownerType: null, packageId: null),
            };
            if (!string.IsNullOrEmpty(file.LastWriteUtc)) data["lastWriteUtc"] = file.LastWriteUtc!;
            return data;
        }

        internal static Dictionary<string, object> BuildRecoveryResidueEventData(string fileName, string? dir)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["source"] = "recovery_residue",
                ["fileName"] = fileName,
                ["dir"] = dir ?? string.Empty,
                ["identity"] = BuildIdentity(name: null, fileName: fileName, ownerType: null, packageId: null),
            };
        }

        // ----------------------------------------------------------------------------------
        // Small fail-soft helpers.
        // ----------------------------------------------------------------------------------

        private static string SafeFolder(Func<string> read, string fallback)
        {
            try
            {
                var v = read();
                return string.IsNullOrEmpty(v) ? fallback : v;
            }
            catch { return fallback; }
        }

        private static bool TryKeyExists(RegistryKey hklm, string subKey)
        {
            try { using (var k = hklm.OpenSubKey(subKey)) return k != null; }
            catch { return false; }
        }

        private static bool TryHasSubKeys(RegistryKey hklm, string subKey)
        {
            try
            {
                using (var k = hklm.OpenSubKey(subKey))
                    return k != null && k.GetSubKeyNames().Length > 0;
            }
            catch { return false; }
        }

        private static string? ReadString(RegistryKey key, string valueName)
        {
            try
            {
                var v = key.GetValue(valueName);
                if (v == null) return null;
                var s = v.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch { return null; }
        }

        private static string Trunc(string s) => s.Length <= 80 ? s : s.Substring(0, 80);
    }

    // --------------------------------------------------------------------------------------
    // Raw scan model (internal; surfaced to the test assembly via InternalsVisibleTo).
    // --------------------------------------------------------------------------------------

    internal sealed class ProvisioningScanFindings
    {
        public List<PpkgFileFact> Files { get; } = new List<PpkgFileFact>();
        public bool FilesTruncated { get; set; }

        public List<PpkgPackageFact> Packages { get; } = new List<PpkgPackageFact>();
        public bool PackagesTruncated { get; set; }

        public List<string> RecoveryCustomizationsFiles { get; } = new List<string>();
        public bool RecoveryFilesTruncated { get; set; }
        public string? RecoveryCustomizationsDir { get; set; }

        public bool ProvisioningRootPresent { get; set; }
        public bool OmadmAccountsPresent { get; set; }
        public bool DiagnosticsPresent { get; set; }

        public List<string> Errors { get; } = new List<string>();
    }

    internal sealed class PpkgFileFact
    {
        public string Directory { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string? LastWriteUtc { get; set; }
    }

    internal sealed class PpkgPackageFact
    {
        public string PackageId { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? OwnerType { get; set; }
        public string? Rank { get; set; }
        public string? InstallTime { get; set; }
        public List<string> SubKeyNames { get; } = new List<string>();
    }
}
