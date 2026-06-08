using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic
{
    /// <summary>
    /// Polls OS-level Delivery Optimization status via a persistent PowerShell Runspace.
    /// Matches DO entries to tracked IME app downloads using the intunewin-bin FileId format.
    /// Emits download_progress events on every poll (for live UI) and do_telemetry once per app
    /// when the download completes.
    ///
    /// Lifecycle: starts dormant, wakes up when ImeLogTracker detects a download,
    /// goes dormant again when all downloads are enriched.
    /// </summary>
    public class DeliveryOptimizationCollector : CollectorBase
    {
        private const string LogFileName = "do-status.jsonl";
        private const int InvokeTimeoutMs = 5000;

        private readonly Func<AppPackageStateList> _getPackageStates;
        private readonly Action<AppPackageState> _onDoTelemetryReceived;
        private readonly string _logFilePath;

        // Office C2R DO support (Rev 3): the Office-CDN download is visible in DO long before the
        // OfficeC2RClient.exe worker appears, so we classify the non-IME Office-CDN jobs UNCONDITIONALLY
        // (no longer gated on the worker) and hand aggregated stats to the OfficeInstallDetector — which
        // folds them into the office_install_* events and treats the first sample as the start trigger.
        // Office is not an IME app, so we deliberately do NOT emit download_progress/do_telemetry for it
        // (that would create a phantom app in the backend AppInstallSummary).
        private readonly Action<OfficeDoSample> _onOfficeDoSample;
        private readonly Action _onOfficeDownloadEnded;

        // Keep-awake sources for an Office install (any keeps us polling): the worker process is up
        // (_officeActive), an Office-CDN job was seen in the last poll (_officeJobsSeenLastPoll), or the
        // registry hinted an install is imminent (_officeExpectedPolls — a bounded probe window so a
        // Scenario\INSTALL that never produces a download does not keep us awake forever).
        private volatile bool _officeActive;
        private bool _officeJobsSeenLastPoll;
        private bool _officeEndedNotified;
        private int _officeExpectedPolls;

        // Bounded probe budget after a registry "Office expected" hint, in polls (~ this × interval).
        private const int OfficeExpectedProbePolls = 20;

        private Runspace _runspace;
        private bool _permanentlyDisabled;
        private int _consecutiveErrors;
        private volatile bool _dormant = true;
        private int _collecting; // concurrency guard (0 = idle, 1 = collecting)

        // Track which apps we already sent final do_telemetry for (prevents duplicate callbacks)
        private readonly HashSet<string> _enrichedAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Change detection per app: only emit download_progress when bytes actually changed
        private readonly Dictionary<string, long> _lastBytesPerApp = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Change detection for JSONL log: only write when overall state changed
        private long _lastSnapshotFingerprint;

        public DeliveryOptimizationCollector(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            int intervalSeconds,
            Func<AppPackageStateList> getPackageStates,
            Action<AppPackageState> onDoTelemetryReceived,
            string logDirectory,
            Action<OfficeDoSample> onOfficeDoSample = null,
            Action onOfficeDownloadEnded = null)
            : base(sessionId, tenantId, post, logger, intervalSeconds)
        {
            _getPackageStates = getPackageStates ?? throw new ArgumentNullException(nameof(getPackageStates));
            _onDoTelemetryReceived = onDoTelemetryReceived;
            _logFilePath = Path.Combine(logDirectory, LogFileName);
            _onOfficeDoSample = onOfficeDoSample;
            _onOfficeDownloadEnded = onOfficeDownloadEnded;
        }

        /// <summary>Start dormant — timer does not fire until WakeUp() is called.</summary>
        protected override TimeSpan GetInitialDelay() => Timeout.InfiniteTimeSpan;

        protected override void OnBeforeStart()
        {
            EnsureRunspace();
        }

        protected override void OnAfterStop()
        {
            DisposeRunspace();
        }

        /// <summary>
        /// Wakes the collector from dormant state. Called by MonitoringService when
        /// ImeLogTracker detects an app entering the Downloading state.
        /// Safe to call multiple times / from any thread.
        /// </summary>
        public void WakeUp()
        {
            if (_permanentlyDisabled || !_dormant) return;
            _dormant = false;
            Logger.Info("[DeliveryOptimizationCollector] Waking up — download activity detected");
            ResumeTimer();
        }

        /// <summary>
        /// Office C2R wake source: the OfficeProcessWatcher reports an Office worker started/stopped.
        /// While active, the collector keeps polling (even with no IME downloads) so it can capture
        /// Office's DO jobs. Called from any thread.
        /// </summary>
        public void NotifyOfficeActive(bool active)
        {
            _officeActive = active;
            if (active)
            {
                Logger.Info("[DeliveryOptimizationCollector] Office install active — sampling DO for Office CDN jobs");
                WakeUp();
            }
        }

        /// <summary>
        /// Office C2R early wake source (Rev 3): the RegistryChangeWatcher observed a
        /// <c>…\ClickToRun\Scenario\INSTALL</c> key — an Office install is imminent, possibly before any
        /// IME download or worker process. Wakes the collector for a bounded probe window so it can
        /// catch the Office-CDN DO job at the very start of the download. Called from any thread.
        /// </summary>
        public void NotifyOfficeExpected()
        {
            _officeExpectedPolls = OfficeExpectedProbePolls;
            Logger.Info("[DeliveryOptimizationCollector] Office install expected (registry Scenario\\INSTALL) — probing DO for Office CDN jobs");
            WakeUp();
        }

        protected override void Collect()
        {
            if (_permanentlyDisabled || _dormant) return;

            // Concurrency guard: Runspace is not thread-safe. Skip if previous poll is still running.
            if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0)
            {
                Logger.Debug("[DeliveryOptimizationCollector] Skipping poll — previous invocation still running");
                return;
            }
            try
            {
                CollectCore();
            }
            finally
            {
                Interlocked.Exchange(ref _collecting, 0);
            }
        }

        private void CollectCore()
        {

            // Guard: check if there's still work to do. Office C2R has no IME packages, so an active
            // Office install (from the OfficeProcessWatcher) keeps the collector polling on its own.
            var packageStates = _getPackageStates();

            var hasActiveDownloads = false;
            var hasPendingEnrichment = false;
            if (packageStates != null)
            {
                for (int i = 0; i < packageStates.Count; i++)
                {
                    var p = packageStates[i];
                    if (p.InstallationState == AppInstallationState.Downloading ||
                        p.InstallationState == AppInstallationState.Installing)
                        hasActiveDownloads = true;

                    if (!p.HasDoTelemetry && !_enrichedAppIds.Contains(p.Id) &&
                        p.DownloadingOrInstallingSeen &&
                        (p.InstallationState == AppInstallationState.Installed ||
                         p.InstallationState == AppInstallationState.Error))
                        hasPendingEnrichment = true;
                }
            }

            // Office keep-awake: worker up, an Office-CDN job seen last poll, or a bounded registry-hint
            // probe window still open. This lets us detect Office's DO download independent of the
            // (late, transient) OfficeC2RClient.exe worker.
            var officeKeepAwake = _officeActive || _officeJobsSeenLastPoll || _officeExpectedPolls > 0;

            if (!hasActiveDownloads && !hasPendingEnrichment && !officeKeepAwake)
            {
                Logger.Info("[DeliveryOptimizationCollector] Going dormant — no active downloads, pending enrichment or Office install");
                _dormant = true;
                PauseTimer();
                return;
            }

            // Invoke PowerShell via persistent Runspace
            var results = InvokeDoStatus();
            if (results == null) return;

            _consecutiveErrors = 0;

            // Process results: emit progress events and match completed downloads
            ProcessResults(results, packageStates);
        }

        // -----------------------------------------------------------------------
        // PowerShell Runspace management
        // -----------------------------------------------------------------------

        private void EnsureRunspace()
        {
            if (_runspace != null && _runspace.RunspaceStateInfo.State == RunspaceState.Opened)
                return;

            DisposeRunspace();

            try
            {
                _runspace = RunspaceFactory.CreateRunspace();
                _runspace.Open();
                Logger.Info("[DeliveryOptimizationCollector] PowerShell Runspace opened");
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Failed to open Runspace: {ex.Message}");
                _runspace = null;
            }
        }

        private void DisposeRunspace()
        {
            if (_runspace == null) return;
            try
            {
                _runspace.Close();
                _runspace.Dispose();
            }
            catch { /* best effort */ }
            _runspace = null;
        }

        /// <summary>
        /// Invokes Get-DeliveryOptimizationStatus in the persistent Runspace.
        /// Returns a list of PSObject results, or null on error.
        /// Self-heals: recreates the Runspace on failure.
        /// </summary>
        private List<PSObject> InvokeDoStatus()
        {
            EnsureRunspace();
            if (_runspace == null)
            {
                HandleError("Runspace not available");
                return null;
            }

            try
            {
                using (var ps = PowerShell.Create())
                {
                    ps.Runspace = _runspace;
                    ps.AddCommand("Get-DeliveryOptimizationStatus");

                    // Invoke with timeout protection
                    var asyncResult = ps.BeginInvoke();
                    if (!asyncResult.AsyncWaitHandle.WaitOne(InvokeTimeoutMs))
                    {
                        ps.Stop();
                        Logger.Warning("[DeliveryOptimizationCollector] PS invoke timed out after 5s, stopping");
                        HandleError("timeout");
                        return null;
                    }

                    var results = new List<PSObject>(ps.EndInvoke(asyncResult));

                    // Check for errors in the PS error stream
                    if (ps.HadErrors && ps.Streams.Error.Count > 0)
                    {
                        var firstError = ps.Streams.Error[0].ToString();
                        if (firstError.Contains("is not recognized as the name of a cmdlet") ||
                            firstError.Contains("CommandNotFoundException"))
                        {
                            Logger.Warning("[DeliveryOptimizationCollector] Get-DeliveryOptimizationStatus not available, disabling permanently");
                            _permanentlyDisabled = true;
                            return null;
                        }
                        Logger.Debug($"[DeliveryOptimizationCollector] PS error stream: {firstError}");
                    }

                    return results;
                }
            }
            catch (PSInvalidOperationException ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Runspace error, self-healing: {ex.Message}");
                DisposeRunspace();
                HandleError(ex.Message);
                return null;
            }
            catch (RuntimeException ex)
            {
                // Catches command-not-found and other PS runtime errors
                if (ex.ErrorRecord?.FullyQualifiedErrorId?.Contains("CommandNotFoundException") == true)
                {
                    Logger.Warning("[DeliveryOptimizationCollector] Get-DeliveryOptimizationStatus not available, disabling permanently");
                    _permanentlyDisabled = true;
                    return null;
                }
                Logger.Warning($"[DeliveryOptimizationCollector] PS runtime error: {ex.Message}");
                HandleError(ex.Message);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Invoke failed: {ex.Message}");
                DisposeRunspace();
                HandleError(ex.Message);
                return null;
            }
        }

        // -----------------------------------------------------------------------
        // Result processing: progress events + final DO telemetry
        // -----------------------------------------------------------------------

        private void ProcessResults(List<PSObject> results, AppPackageStateList packageStates)
        {
            if (results.Count == 0)
            {
                Logger.Verbose("[DeliveryOptimizationCollector] No DO entries returned");
                return;
            }

            int progressCount = 0;
            int matchCount = 0;

            // Office C2R DO aggregation across all Office-CDN jobs in this poll.
            int officeJobCount = 0;
            long officeFileSize = 0, officeTotalBytes = 0, officeBytesFromPeers = 0,
                 officeBytesFromHttp = 0, officeBytesFromCacheServer = 0;
            int officeDownloadMode = -1;

            // Build JSONL snapshot for log (only if fingerprint changes)
            var logEntries = new JArray();

            foreach (var result in results)
            {
                var fileId = GetPropString(result, "FileId");
                if (string.IsNullOrEmpty(fileId)) continue;

                long fileSize = GetPropLong(result, "FileSize");
                long totalBytes = GetPropLong(result, "TotalBytesDownloaded");
                long bytesFromPeers = GetPropLong(result, "BytesFromPeers");
                int peerCachingPct = GetPropInt(result, "PercentPeerCaching");
                long bytesLanPeers = GetPropLong(result, "BytesFromLanPeers");
                long bytesGroupPeers = GetPropLong(result, "BytesFromGroupPeers");
                long bytesInternetPeers = GetPropLong(result, "BytesFromInternetPeers");
                long bytesLinkLocalPeers = GetPropLong(result, "BytesFromLinkLocalPeers");
                int downloadMode = GetPropInt(result, "DownloadMode", -1);
                long bytesFromHttp = GetPropLong(result, "BytesFromHttp");
                long bytesFromCacheServer = GetPropLong(result, "BytesFromCacheServer");
                var downloadDuration = GetPropTimeSpan(result, "DownloadDuration");
                var sourceUrl = GetPropString(result, "SourceURL");
                var cacheHost = GetPropUriString(result, "CacheHost");

                // Build log entry for JSONL
                logEntries.Add(new JObject
                {
                    ["FileId"] = fileId,
                    ["FileSize"] = fileSize,
                    ["TotalBytesDownloaded"] = totalBytes,
                    ["PercentPeerCaching"] = peerCachingPct,
                    ["BytesFromPeers"] = bytesFromPeers,
                    ["BytesFromHttp"] = bytesFromHttp,
                    ["DownloadMode"] = downloadMode,
                    ["SourceURL"] = sourceUrl,
                    ["BytesFromCacheServer"] = bytesFromCacheServer
                });

                // Try to match to an IME-tracked app
                var appId = ImeLogTracker.ExtractAppIdFromDoFileId(fileId);
                var pkg = (!string.IsNullOrEmpty(appId) && packageStates != null)
                    ? packageStates.GetPackage(appId)
                    : null;
                if (pkg == null)
                {
                    // Not an IME app. Accumulate Office-CDN jobs UNCONDITIONALLY (Rev 3) for the
                    // OfficeInstallDetector (folded into office_install_*; NOT emitted as download_progress
                    // to avoid a phantom app in the backend AppInstallSummary). No longer gated on the
                    // worker process — the download is visible here long before OfficeC2RClient.exe runs.
                    if (IsOfficeCdnJob(sourceUrl))
                    {
                        officeJobCount++;
                        officeFileSize += fileSize;
                        officeTotalBytes += totalBytes;
                        officeBytesFromPeers += bytesFromPeers;
                        officeBytesFromHttp += bytesFromHttp;
                        officeBytesFromCacheServer += bytesFromCacheServer;
                        if (downloadMode >= 0) officeDownloadMode = downloadMode;
                    }
                    continue;
                }

                // --- Live download_progress (every poll where bytes changed) ---
                long lastBytes;
                _lastBytesPerApp.TryGetValue(appId, out lastBytes);

                if (totalBytes != lastBytes)
                {
                    _lastBytesPerApp[appId] = totalBytes;
                    progressCount++;

                    int percentComplete = fileSize > 0 ? (int)((totalBytes * 100) / fileSize) : 0;

                    Post.Emit(new EnrollmentEvent
                    {
                        SessionId = SessionId,
                        TenantId = TenantId,
                        EventType = Constants.EventTypes.DownloadProgress,
                        Severity = EventSeverity.Debug,
                        Source = "DeliveryOptimizationCollector",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"{pkg.Name ?? appId}: {percentComplete}% ({totalBytes}/{fileSize}) peers={peerCachingPct}% mode={downloadMode}",
                        Data = BuildDoEventData(
                            appId, pkg.Name, totalBytes, fileSize, percentComplete,
                            bytesFromPeers, bytesFromHttp, peerCachingPct, downloadMode,
                            bytesLanPeers, bytesGroupPeers, bytesInternetPeers, bytesLinkLocalPeers,
                            bytesFromCacheServer, cacheHost),
                        ImmediateUpload = true
                    });
                }

                // --- Final DO telemetry (once per app when download is complete) ---
                if (!pkg.HasDoTelemetry && !_enrichedAppIds.Contains(appId) &&
                    totalBytes >= fileSize && fileSize > 0)
                {
                    var durationStr = downloadDuration?.ToString(@"hh\:mm\:ss\.fff");

                    pkg.UpdateDoTelemetry(fileSize, totalBytes, bytesFromPeers, peerCachingPct,
                        bytesLanPeers, bytesGroupPeers, bytesInternetPeers,
                        downloadMode, durationStr, bytesFromHttp,
                        bytesFromLinkLocalPeers: bytesLinkLocalPeers,
                        bytesFromCacheServer: bytesFromCacheServer,
                        cacheHost: cacheHost);

                    _enrichedAppIds.Add(appId);
                    matchCount++;

                    Logger.Info($"[DeliveryOptimizationCollector] DO matched: {pkg.Name ?? appId} — " +
                                $"size={fileSize}, peers={bytesFromPeers} ({peerCachingPct}%), " +
                                $"http={bytesFromHttp}, mode={downloadMode}, duration={durationStr}");

                    // Fire callback → EnrollmentTracker emits do_telemetry + download_progress events
                    _onDoTelemetryReceived?.Invoke(pkg);
                }
            }

            // Hand aggregated Office DO stats to the OfficeInstallDetector (folded into office_install_*).
            // Unconditional now (Rev 3): the first sample with jobs is the detector's start trigger; a
            // subsequent disappearance / 100% is the download-ended signal that arms the host's close.
            var officeJobsThisPoll = officeJobCount > 0;
            var officeComplete = officeFileSize > 0 && officeTotalBytes >= officeFileSize;

            if (officeJobsThisPoll && _onOfficeDoSample != null)
            {
                int officePeerPct = officeTotalBytes > 0 ? (int)((officeBytesFromPeers * 100) / officeTotalBytes) : 0;
                try
                {
                    _onOfficeDoSample(new OfficeDoSample
                    {
                        JobCount = officeJobCount,
                        FileSize = officeFileSize,
                        TotalBytesDownloaded = officeTotalBytes,
                        BytesFromPeers = officeBytesFromPeers,
                        BytesFromHttp = officeBytesFromHttp,
                        BytesFromCacheServer = officeBytesFromCacheServer,
                        PercentPeerCaching = officePeerPct,
                        DownloadMode = officeDownloadMode,
                    });
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[DeliveryOptimizationCollector] onOfficeDoSample threw: {ex.Message}");
                }
            }

            // Download-ended signal (once): Office-CDN jobs were present and have now disappeared, or a
            // job reached 100%. Arms the OfficeInstallDetectorHost's close/completion-probe schedule.
            var officeDownloadEnded = !_officeEndedNotified &&
                ((officeJobsThisPoll && officeComplete) || (!officeJobsThisPoll && _officeJobsSeenLastPoll));
            if (officeDownloadEnded)
            {
                _officeEndedNotified = true;
                try { _onOfficeDownloadEnded?.Invoke(); }
                catch (Exception ex) { Logger.Warning($"[DeliveryOptimizationCollector] onOfficeDownloadEnded threw: {ex.Message}"); }
            }

            _officeJobsSeenLastPoll = officeJobsThisPoll;
            // Decrement the bounded registry-hint probe window only while no Office job is being seen.
            if (!officeJobsThisPoll && _officeExpectedPolls > 0) _officeExpectedPolls--;

            // Write JSONL log only when overall state changed
            var fingerprint = ComputeFingerprint(logEntries);
            if (fingerprint != _lastSnapshotFingerprint)
            {
                _lastSnapshotFingerprint = fingerprint;
                WriteToLogFile(logEntries);
            }

            Logger.Verbose($"[DeliveryOptimizationCollector] Poll: {results.Count} entries, " +
                           $"{progressCount} progress updates, {matchCount} new matches ({_enrichedAppIds.Count} total enriched)");
        }

        // Office C2R content CDN hosts — version-independent (a FileId build marker like "_16_0_" would
        // break on older/newer Office versions). Field-validated against session 8353e03b: C2R uses BOTH
        // the primary CDN officecdn.microsoft.com (registry OriginalCDNDomain) and the content CDN
        // f.c2r.ts.cdn.office.net (registry FailoverDomain / PreferredCDNPrefix).
        private static readonly string[] OfficeCdnHosts = { "cdn.office.net", "officecdn.microsoft.com" };

        /// <summary>
        /// True when a DO job's SourceURL points at an Office C2R content CDN (not an IME Win32 app).
        /// Matched purely by the stable CDN host so it survives Office version changes.
        /// </summary>
        private static bool IsOfficeCdnJob(string sourceUrl)
        {
            if (string.IsNullOrEmpty(sourceUrl)) return false;
            foreach (var host in OfficeCdnHosts)
                if (sourceUrl.IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // -----------------------------------------------------------------------
        // PSObject property helpers (safe extraction, no exceptions on missing props)
        // -----------------------------------------------------------------------

        private static string GetPropString(PSObject obj, string name)
        {
            return obj.Properties[name]?.Value?.ToString();
        }

        private static long GetPropLong(PSObject obj, string name, long defaultValue = 0)
        {
            var val = obj.Properties[name]?.Value;
            if (val == null) return defaultValue;
            try { return Convert.ToInt64(val); }
            catch { return defaultValue; }
        }

        private static int GetPropInt(PSObject obj, string name, int defaultValue = 0)
        {
            var val = obj.Properties[name]?.Value;
            if (val == null) return defaultValue;
            try { return Convert.ToInt32(val); }
            catch { return defaultValue; }
        }

        private static TimeSpan? GetPropTimeSpan(PSObject obj, string name)
        {
            var val = obj.Properties[name]?.Value;
            if (val is TimeSpan ts) return ts;
            return null;
        }

        // CacheHost is exposed by the cmdlet as System.Uri (or null when no MCC was used).
        // .ToString() returns the absolute URI form (e.g. "http://72.144.231.24/").
        // Returning null on absent or empty hosts so the event omits the field instead of "/".
        private static string GetPropUriString(PSObject obj, string name)
        {
            var val = obj.Properties[name]?.Value;
            if (val == null) return null;
            if (val is Uri uri) return uri.ToString();
            var s = val.ToString();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        // Builds the Data dictionary for download_progress events. Centralised so the
        // download_progress (in-flight) and (potentially future) periodic-snapshot emissions
        // stay symmetric on the breakdown / cache fields the UI now consumes.
        private static Dictionary<string, object> BuildDoEventData(
            string appId, string appName, long totalBytes, long fileSize, int percentComplete,
            long bytesFromPeers, long bytesFromHttp, int peerCachingPct, int downloadMode,
            long bytesLanPeers, long bytesGroupPeers, long bytesInternetPeers, long bytesLinkLocalPeers,
            long bytesFromCacheServer, string cacheHost)
        {
            var data = new Dictionary<string, object>
            {
                ["appId"] = appId,
                ["appName"] = appName,
                // UI-compatible fields (DownloadProgress.tsx reads these)
                ["bytesDownloaded"] = totalBytes,
                ["bytesTotal"] = fileSize,
                ["progressPercent"] = percentComplete,
                // DO-specific fields
                ["doFileSize"] = fileSize,
                ["doTotalBytesDownloaded"] = totalBytes,
                ["doPercentComplete"] = percentComplete,
                ["doBytesFromPeers"] = bytesFromPeers,
                ["doBytesFromHttp"] = bytesFromHttp,
                ["doPercentPeerCaching"] = peerCachingPct,
                ["doDownloadMode"] = downloadMode,
                // Per-source breakdown — the customer-visible bug was these being absent on
                // download_progress while present on do_telemetry, leaving the UI at 0/0/0 mid-flight.
                ["doBytesFromLanPeers"] = bytesLanPeers,
                ["doBytesFromGroupPeers"] = bytesGroupPeers,
                ["doBytesFromInternetPeers"] = bytesInternetPeers,
                ["doBytesFromLinkLocalPeers"] = bytesLinkLocalPeers,
                ["doBytesFromCacheServer"] = bytesFromCacheServer,
                ["doSource"] = "os_cmdlet"
            };
            if (!string.IsNullOrEmpty(cacheHost))
                data["doCacheHost"] = cacheHost;
            return data;
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private void HandleError(string error)
        {
            _consecutiveErrors++;

            if (_consecutiveErrors >= 5)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] {_consecutiveErrors} consecutive errors, disabling permanently. Last: {error}");
                _permanentlyDisabled = true;
                return;
            }

            Logger.Warning($"[DeliveryOptimizationCollector] Error ({_consecutiveErrors}/5): {error}");
        }

        private static long ComputeFingerprint(JArray entries)
        {
            long sum = entries.Count;
            foreach (var e in entries)
            {
                sum += e["TotalBytesDownloaded"]?.Value<long>() ?? 0;
                // Include FileId hash to detect entry additions/removals
                var fileId = e["FileId"]?.ToString();
                if (fileId != null)
                    sum += fileId.GetHashCode();
            }
            return sum;
        }

        private void WriteToLogFile(JArray entries)
        {
            try
            {
                var snapshot = new JObject
                {
                    ["timestamp"] = DateTime.UtcNow.ToString("o"),
                    ["entryCount"] = entries.Count,
                    ["entries"] = entries
                };

                var line = snapshot.ToString(Formatting.None) + Environment.NewLine;
                File.AppendAllText(_logFilePath, line, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Failed to write DO log: {ex.Message}");
            }
        }
    }
}
