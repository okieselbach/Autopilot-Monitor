#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Surfaces the REAL Microsoft 365 Apps (Office Click-to-Run / C2R) background install lifecycle
    /// — even when the Intune "integrated" Office app reports done to IME within 1-2 minutes while C2R
    /// keeps streaming/laying down the product for many more minutes.
    /// <para>
    /// <b>Event-driven, no idle polling</b> (Rev 3). This class is the pure decision core; the
    /// <c>OfficeInstallDetectorHost</c> owns the OS watchers and drives it. The lifecycle anchor is no
    /// longer the late, transient <c>OfficeC2RClient.exe</c> worker — whichever Office signal arrives
    /// first opens the single install window (idempotent):
    /// <list type="bullet">
    ///   <item><see cref="OnOfficeDoSample"/> — aggregated Office-CDN Delivery-Optimization stats. The
    ///     download is visible here long before the worker process, so the first sample carrying jobs is
    ///     the EARLIEST start trigger; later samples fold a real download-% into progress.</item>
    ///   <item><see cref="OnRegistryChanged"/> — <c>RegNotifyChangeKeyValue</c> push on the ClickToRun
    ///     key. When idle and a <c>Scenario\INSTALL</c> key is present → start; when active → progress on
    ///     real movement only (no heartbeat) or an explicit error code.</item>
    ///   <item><see cref="OnWorkerStarted"/> — Office C2R worker started (WMI push / startup probe); an
    ///     idempotent start trigger (no-op if the lifecycle already began).</item>
    ///   <item><see cref="TryFinalizeCompletion"/> — called by the host after the download ended AND the
    ///     worker is gone, on a bounded probe schedule. Completion is proven by the core Office binaries
    ///     on disk under <c>{InstallationPath}\root\*</c> (the integrate phase lays them down after the
    ///     stream ends). An explicit error code → failed; no proof after the probe budget →
    ///     <see cref="AbandonSilently"/> (no false terminal).</item>
    /// </list>
    /// </para>
    /// <para>
    /// Registry reads use the forced Registry64 view (AnyCPU net48 may otherwise read the stale
    /// WOW6432Node mirror). One install lifecycle per detector (latches Terminal); a later C2R update
    /// in the same session is out of scope for v1.
    /// </para>
    /// </summary>
    public class OfficeInstallDetector
    {
        internal const string SourceName = "OfficeInstallDetector";
        internal const string AppName = "Microsoft 365 Apps";

        private const string ConfigurationSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
        private const string ScenarioSubKey = @"SOFTWARE\Microsoft\Office\ClickToRun\Scenario";

        // Value-name substrings scanned (case-insensitive) for a best-effort failure code in the
        // undocumented Scenario value set. Deliberately NARROW (no bare "result" — that would match a
        // benign "Result=Success"); a hit is only treated as a failure when the VALUE is a non-zero
        // numeric/hex code (see IsNonZeroNumericCode). TODO(office-followup): validate the real value
        // names against a field enrollment and widen only if needed.
        private static readonly string[] ErrorValueHints = { "errorcode", "hresult", "exitcode", "lasterror", "failurecode" };

        // Core Office app binaries used as the on-disk completion proof. ANY of these present under
        // {InstallationPath}\root\* means the C2R lay-down finished — "any of", because a deployment can
        // exclude products (e.g. no Outlook), so requiring all would miss legitimate completions.
        private static readonly string[] CoreBinaries = { "WINWORD.EXE", "EXCEL.EXE", "POWERPNT.EXE", "OUTLOOK.EXE" };

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly IClock _clock;
        private readonly object _lock = new object();

        // Test seam: when set, used instead of reading the live registry/process state. Production
        // never assigns it. Surfaced to the test assembly via InternalsVisibleTo.
        internal Func<OfficeC2RSnapshot>? SnapshotProvider { get; set; }

        // Test seam: when set, used instead of the real filesystem check for the core-binary completion
        // proof. Receives the InstallationPath; returns true when a core Office binary exists on disk.
        internal Func<string?, bool>? CoreBinariesProbe { get; set; }

        private enum DetectorState { Idle, Active, Terminal }

        /// <summary>Outcome of a completion attempt — drives the host's bounded post-download probe.</summary>
        internal enum CompletionOutcome { Completed, Failed, NotYet }

        private DetectorState _state = DetectorState.Idle;
        private DateTime? _startedAtUtc;
        private string? _lastProgressSignature;
        private string? _startedTrigger;
        private OfficeDoSample? _lastDo;

        public OfficeInstallDetector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            IClock clock)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        // -----------------------------------------------------------------------
        // Event entry points (driven by the host's process + registry watchers)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Office C2R worker (OfficeC2RClient.exe) started. In Rev 3 this is no longer the primary
        /// anchor — the worker is a late, transient launcher (it ran ~10 min AFTER the real download
        /// began in field sessions 8353e03b / 58d52632). It is one of three idempotent start triggers;
        /// whichever fires first opens the single install window.
        /// </summary>
        public void OnWorkerStarted()
        {
            lock (_lock)
            {
                if (_state != DetectorState.Idle) return;
                var snap = ReadSnapshotSafe();
                if (snap == null) return;
                BeginIfIdle("process", snap);
            }
        }

        /// <summary>
        /// ClickToRun registry changed. When idle and a Scenario subkey (INSTALL/…) is present, this is
        /// the earliest registry start signal; when already active it drives progress or surfaces an
        /// explicit error code.
        /// </summary>
        public void OnRegistryChanged()
        {
            lock (_lock)
            {
                if (_state == DetectorState.Terminal) return;
                var snap = ReadSnapshotSafe();
                if (snap == null) return;

                if (_state == DetectorState.Idle)
                {
                    if (snap.ActiveScenarioPresent) BeginIfIdle("registry", snap);
                    return;
                }
                EvaluateActive(snap);
            }
        }

        /// <summary>
        /// Latest aggregated Office DO stats. The first sample carrying jobs is the EARLIEST and most
        /// reliable start trigger (the Office-CDN download is visible long before the worker process);
        /// subsequent samples fold a real download-% into progress.
        /// </summary>
        public void OnOfficeDoSample(OfficeDoSample sample)
        {
            if (sample == null) return;
            lock (_lock)
            {
                _lastDo = sample;
                if (_state == DetectorState.Terminal) return;

                if (_state == DetectorState.Idle)
                {
                    if (sample.JobCount <= 0) return; // a zero-job sample is not a start signal
                    var snapStart = ReadSnapshotSafe();
                    if (snapStart == null) return;
                    BeginIfIdle("do", snapStart);
                    return;
                }

                var snap = ReadSnapshotSafe();
                if (snap == null) return;
                EvaluateActive(snap);
            }
        }

        /// <summary>
        /// Attempt to finalize the lifecycle after the install window has closed (download ended AND the
        /// worker is gone — orchestrated by the host with a settle/probe schedule). Completion is proven
        /// by the core Office binaries existing on disk under <c>{InstallationPath}\root\*</c> (C2R lays
        /// them down in the integrate phase, which can lag the download end), so the host calls this on a
        /// bounded retry schedule. An explicit error code finalizes as failed. While neither holds, the
        /// outcome is <see cref="CompletionOutcome.NotYet"/> and the lifecycle stays open.
        /// </summary>
        internal CompletionOutcome TryFinalizeCompletion()
        {
            lock (_lock)
            {
                if (_state != DetectorState.Active) return CompletionOutcome.NotYet;

                var snap = ReadSnapshotSafe();
                if (snap == null) return CompletionOutcome.NotYet;

                if (!string.IsNullOrEmpty(snap.ErrorCode))
                {
                    FinalizeFailed(snap);
                    return CompletionOutcome.Failed;
                }

                if (ProbeCoreBinaries(snap.InstallationPath))
                {
                    _state = DetectorState.Terminal;
                    EmitLifecycle(Constants.EventTypes.OfficeInstallCompleted, snap, EventSeverity.Info, "Completed", isTerminal: true, coreBinariesPresent: true);
                    return CompletionOutcome.Completed;
                }

                return CompletionOutcome.NotYet;
            }
        }

        /// <summary>
        /// Stop tracking without emitting a terminal event. Called by the host when the bounded
        /// completion-probe schedule is exhausted with no on-disk proof — conservative: we never emit a
        /// false completed/failed when we cannot positively verify the outcome.
        /// </summary>
        public void AbandonSilently()
        {
            lock (_lock)
            {
                if (_state == DetectorState.Active)
                {
                    _state = DetectorState.Terminal;
                    _logger.Info($"[{SourceName}] install window closed without on-disk completion proof — stopping silently (no terminal event)");
                }
            }
        }

        // -----------------------------------------------------------------------
        // State machine
        // -----------------------------------------------------------------------

        /// <summary>Opens the single install window from whichever signal arrives first. Caller holds the lock.</summary>
        private void BeginIfIdle(string trigger, OfficeC2RSnapshot snap)
        {
            if (_state != DetectorState.Idle) return;
            _state = DetectorState.Active;
            _startedAtUtc = _clock.UtcNow;
            _startedTrigger = trigger;
            _lastProgressSignature = BuildProgressSignature(snap, _lastDo);
            EmitLifecycle(Constants.EventTypes.OfficeInstallStarted, snap, EventSeverity.Info, PhaseOf(snap), isTerminal: false);
        }

        private void EvaluateActive(OfficeC2RSnapshot snap)
        {
            // An explicit numeric error code is the one in-flight terminal we trust.
            if (!string.IsNullOrEmpty(snap.ErrorCode))
            {
                FinalizeFailed(snap);
                return;
            }

            var sig = BuildProgressSignature(snap, _lastDo);
            if (!string.Equals(sig, _lastProgressSignature, StringComparison.Ordinal))
            {
                _lastProgressSignature = sig;
                EmitLifecycle(Constants.EventTypes.OfficeInstallProgress, snap, EventSeverity.Info, PhaseOf(snap), isTerminal: false);
            }
        }

        private void FinalizeFailed(OfficeC2RSnapshot snap)
        {
            if (_state == DetectorState.Terminal) return;
            _state = DetectorState.Terminal;
            EmitLifecycle(Constants.EventTypes.OfficeInstallFailed, snap, EventSeverity.Error, "Failed", isTerminal: true);
        }

        /// <summary>
        /// True when a core Office binary exists on disk under <c>{InstallationPath}\root\*</c> (the C2R
        /// version folder, e.g. <c>Office16</c>, is enumerated rather than hardcoded — a version literal
        /// would be fragile). Fail-soft: any error returns false (no positive proof). The
        /// <see cref="CoreBinariesProbe"/> seam overrides this in tests.
        /// </summary>
        private bool ProbeCoreBinaries(string? installationPath)
        {
            if (CoreBinariesProbe != null)
            {
                try { return CoreBinariesProbe(installationPath); }
                catch { return false; }
            }
            return CoreBinariesPresentOnDisk(installationPath, _logger);
        }

        internal static bool CoreBinariesPresentOnDisk(string? installationPath, AgentLogger logger)
        {
            if (string.IsNullOrWhiteSpace(installationPath)) return false;
            try
            {
                var root = System.IO.Path.Combine(installationPath!, "root");
                if (!System.IO.Directory.Exists(root)) return false;

                // Enumerate the version folder(s) under root (OfficeNN); avoids hardcoding the version.
                foreach (var versionDir in System.IO.Directory.GetDirectories(root))
                {
                    foreach (var binary in CoreBinaries)
                    {
                        try
                        {
                            if (System.IO.File.Exists(System.IO.Path.Combine(versionDir, binary))) return true;
                        }
                        catch { /* probe the next candidate */ }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug($"[{SourceName}] core-binary probe failed for '{installationPath}': {ex.Message}");
            }
            return false;
        }

        // StreamingFinished does not exist in the C2R registry, so the in-flight phase is reported as a
        // single "Installing"; the terminal completion uses "Completed".
        private static string PhaseOf(OfficeC2RSnapshot snap) => "Installing";

        /// <summary>
        /// Stable signature of the <b>observable</b> progress dimensions only — the values we actually
        /// surface in the payload and that represent real forward movement: the reported version, the
        /// active scenario name, and the DO download (bytes / percent). A change in any of these emits
        /// one progress event; an unchanged signature emits nothing — keeping the detector heartbeat-free.
        /// <para>
        /// The raw <c>Scenario</c> value dict is deliberately EXCLUDED. During an active C2R operation
        /// the worker rewrites those undocumented values many times per second, and the RegNotify watcher
        /// (subtree) fires on each write. Folding that churn into the signature produced bursts of
        /// progress events within the same second that all carried an identical, frozen download-% (the
        /// 3s DO sample had not advanced) and no surfaced cause — pure noise (field session 58d52632).
        /// Progress is therefore tied to observable movement, which naturally paces it to the DO cadence;
        /// the registry push still drives error-code discovery (and, later, TasksState phase detection).
        /// </para>
        /// </summary>
        private static string BuildProgressSignature(OfficeC2RSnapshot snap, OfficeDoSample? doSample)
        {
            var parts = new List<string>
            {
                "version=" + (snap.VersionToReport ?? string.Empty),
                "scenario=" + (snap.ActiveScenarioName ?? string.Empty),
            };
            if (doSample != null)
            {
                parts.Add("doBytes=" + doSample.TotalBytesDownloaded);
                parts.Add("doPct=" + doSample.DownloadPercent);
            }
            return string.Join("|", parts);
        }

        // -----------------------------------------------------------------------
        // Payload
        // -----------------------------------------------------------------------

        private void EmitLifecycle(string eventType, OfficeC2RSnapshot snap, EventSeverity severity, string phase, bool isTerminal, bool coreBinariesPresent = false)
        {
            var data = BuildPayload(snap, phase, isTerminal, _lastDo);
            if (!string.IsNullOrEmpty(_startedTrigger)) data["startedTrigger"] = _startedTrigger!;
            if (isTerminal && eventType == Constants.EventTypes.OfficeInstallCompleted)
                data["coreBinariesPresent"] = coreBinariesPresent;
            var message = BuildMessage(eventType, snap, phase, _lastDo);

            _post.Emit(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = eventType,
                Severity = severity,
                Source = SourceName,
                Phase = EnrollmentPhase.Unknown,
                Message = message,
                Data = data,
                ImmediateUpload = true,
            });
        }

        internal Dictionary<string, object> BuildPayload(OfficeC2RSnapshot snap, string phase, bool isTerminal, OfficeDoSample? doSample)
        {
            var elapsedSeconds = _startedAtUtc.HasValue
                ? Math.Max(0, (int)(_clock.UtcNow - _startedAtUtc.Value).TotalSeconds)
                : 0;

            var data = new Dictionary<string, object>
            {
                { "appName", AppName },
                { "products", snap.Products ?? new List<string>() },
                { "channel", snap.Channel ?? string.Empty },
                { "platform", snap.Platform ?? string.Empty },
                { "scenario", snap.ActiveScenarioName ?? string.Empty },
                { "phase", phase },
                { "versionReached", snap.VersionToReport ?? string.Empty },
                { "streamingFinished", snap.StreamingFinished },
                { "officeC2RClientRunning", snap.OfficeC2RClientRunning },
                { "errorCode", (object?)snap.ErrorCode ?? string.Empty },
                { "installationPath", snap.InstallationPath ?? string.Empty },
                { "scanErrors", (snap.Errors ?? new List<string>()).ToList() },
            };

            if (doSample != null)
            {
                // Delivery Optimization download telemetry for Office's CDN content (folded in to
                // avoid creating a phantom app in the backend AppInstallSummary — Office is not an IME app).
                data["doJobCount"] = doSample.JobCount;
                data["doFileSize"] = doSample.FileSize;
                data["doTotalBytesDownloaded"] = doSample.TotalBytesDownloaded;
                data["doBytesFromPeers"] = doSample.BytesFromPeers;
                data["doBytesFromHttp"] = doSample.BytesFromHttp;
                data["doBytesFromCacheServer"] = doSample.BytesFromCacheServer;
                data["doPercentPeerCaching"] = doSample.PercentPeerCaching;
                data["doDownloadMode"] = doSample.DownloadMode;
                if (doSample.DownloadPercent.HasValue)
                    data["downloadPercent"] = doSample.DownloadPercent.Value;
            }

            if (isTerminal)
                data["durationSeconds"] = elapsedSeconds;
            else
                data["elapsedSeconds"] = elapsedSeconds;

            return data;
        }

        private static string BuildMessage(string eventType, OfficeC2RSnapshot snap, string phase, OfficeDoSample? doSample)
        {
            var product = snap.Products != null && snap.Products.Count > 0 ? string.Join(",", snap.Products) : AppName;
            var pct = doSample?.DownloadPercent;
            if (eventType == Constants.EventTypes.OfficeInstallStarted)
                return $"{SourceName}: {product} install detected (phase={phase})";
            if (eventType == Constants.EventTypes.OfficeInstallProgress)
                return pct.HasValue
                    ? $"{SourceName}: {product} install progress (phase={phase}, download {pct.Value}%)"
                    : $"{SourceName}: {product} install progress (phase={phase})";
            if (eventType == Constants.EventTypes.OfficeInstallCompleted)
                return $"{SourceName}: {product} install completed{(string.IsNullOrEmpty(snap.VersionToReport) ? string.Empty : $" (v{snap.VersionToReport})")}";
            return $"{SourceName}: {product} install failed{(string.IsNullOrEmpty(snap.ErrorCode) ? string.Empty : $" (code {snap.ErrorCode})")}";
        }

        // -----------------------------------------------------------------------
        // Registry + process read. Fail-soft — errors captured, never thrown.
        // -----------------------------------------------------------------------

        private OfficeC2RSnapshot? ReadSnapshotSafe()
        {
            try { return SnapshotProvider != null ? SnapshotProvider() : ReadSnapshot(); }
            catch (Exception ex) { _logger.Warning($"[{SourceName}] snapshot read failed: {ex.Message}"); return null; }
        }

        internal virtual OfficeC2RSnapshot ReadSnapshot()
        {
            var snap = new OfficeC2RSnapshot();
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    ReadConfiguration(hklm, snap);
                    ReadScenarios(hklm, snap);
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"registry:{ex.GetType().Name}: {ex.Message}");
            }

            snap.OfficeC2RClientRunning = IsOfficeC2RClientRunning(snap);
            return snap;
        }

        private static void ReadConfiguration(RegistryKey hklm, OfficeC2RSnapshot snap)
        {
            try
            {
                using (var key = hklm.OpenSubKey(ConfigurationSubKey, writable: false))
                {
                    if (key == null) return;
                    snap.ConfigurationKeyPresent = true;

                    var products = ReadString(key, "ProductReleaseIds");
                    if (!string.IsNullOrEmpty(products))
                    {
                        snap.Products = products!
                            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(p => p.Trim())
                            .Where(p => p.Length > 0)
                            .ToList();
                    }

                    snap.Platform = ReadString(key, "Platform");
                    snap.Channel = ReadString(key, "UpdateChannel") ?? ReadString(key, "CDNBaseUrl");
                    snap.VersionToReport = ReadString(key, "VersionToReport") ?? ReadString(key, "ClientVersionToReport");
                    snap.StreamingFinished = IsTruthy(ReadString(key, "StreamingFinished"));
                    snap.InstallationPath = ReadString(key, "InstallationPath");
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"configuration:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void ReadScenarios(RegistryKey hklm, OfficeC2RSnapshot snap)
        {
            try
            {
                using (var scenarioRoot = hklm.OpenSubKey(ScenarioSubKey, writable: false))
                {
                    if (scenarioRoot == null) return;

                    var scenarioNames = scenarioRoot.GetSubKeyNames();
                    if (scenarioNames.Length == 0) return;

                    snap.ActiveScenarioPresent = true;
                    snap.ActiveScenarioName = scenarioNames[0]; // primary scenario (INSTALL / FIRSTRUN / UPDATE …)

                    foreach (var scenarioName in scenarioNames)
                    {
                        try
                        {
                            using (var scenarioKey = scenarioRoot.OpenSubKey(scenarioName, writable: false))
                            {
                                if (scenarioKey == null) continue;
                                foreach (var valueName in scenarioKey.GetValueNames())
                                {
                                    var value = ReadString(scenarioKey, valueName);
                                    if (value == null) continue;
                                    snap.ScenarioValues[$"{scenarioName}\\{valueName}"] = value;

                                    if (snap.ErrorCode == null
                                        && ErrorValueHints.Any(h => valueName.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                                        && IsNonZeroNumericCode(value))
                                    {
                                        snap.ErrorCode = value;
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            snap.Errors.Add($"scenario[{scenarioName}]:{ex.GetType().Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"scenario:{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool IsOfficeC2RClientRunning(OfficeC2RSnapshot snap)
        {
            Process[]? procs = null;
            try
            {
                procs = Process.GetProcessesByName("OfficeC2RClient");
                return procs.Length > 0;
            }
            catch (Exception ex)
            {
                snap.Errors.Add($"process:{ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                if (procs != null)
                    foreach (var p in procs) { try { p.Dispose(); } catch { } }
            }
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

        /// <summary>StreamingFinished is REG_SZ "True"/"False" or REG_DWORD 0/1 across versions.</summary>
        private static bool IsTruthy(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value!.Trim();
            return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True only when <paramref name="value"/> is a non-zero numeric or hex (0x…) code. Textual
        /// values such as "Success" / "Completed" / "InProgress" are NOT error codes and return false,
        /// so a benign "Result=Success" never masquerades as a failure.
        /// </summary>
        internal static bool IsNonZeroNumericCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return long.TryParse(v.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) && hex != 0;
            }
            // Decimal (HRESULTs can be large or negative); reject anything non-numeric.
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec != 0;
        }
    }

    // --------------------------------------------------------------------------------------
    // Raw scan model (surfaced to the test assembly via InternalsVisibleTo).
    // --------------------------------------------------------------------------------------

    /// <summary>One read's worth of Office C2R facts (registry + process).</summary>
    public sealed class OfficeC2RSnapshot
    {
        public bool ConfigurationKeyPresent { get; set; }
        public List<string> Products { get; set; } = new List<string>();
        public string? Channel { get; set; }
        public string? Platform { get; set; }
        public string? VersionToReport { get; set; }
        public string? InstallationPath { get; set; }
        public bool StreamingFinished { get; set; }
        public bool ActiveScenarioPresent { get; set; }
        public string? ActiveScenarioName { get; set; }
        public Dictionary<string, string> ScenarioValues { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string? ErrorCode { get; set; }
        public bool OfficeC2RClientRunning { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }

    /// <summary>
    /// Aggregated Delivery-Optimization stats across all Office CDN download jobs in one DO poll,
    /// produced by the extended DeliveryOptimizationCollector and folded into the office_install_* events.
    /// </summary>
    public sealed class OfficeDoSample
    {
        public int JobCount { get; set; }
        public long FileSize { get; set; }
        public long TotalBytesDownloaded { get; set; }
        public long BytesFromPeers { get; set; }
        public long BytesFromHttp { get; set; }
        public long BytesFromCacheServer { get; set; }
        public int PercentPeerCaching { get; set; }
        public int DownloadMode { get; set; }

        /// <summary>Whole-percent download progress, or null when total file size is unknown.</summary>
        public int? DownloadPercent => FileSize > 0 ? (int?)Math.Min(100, (int)((TotalBytesDownloaded * 100) / FileSize)) : null;
    }
}
