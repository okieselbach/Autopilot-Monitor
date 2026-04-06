using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.Core.Monitoring.Collectors
{
    /// <summary>
    /// Polls OS-level Delivery Optimization status via Get-DeliveryOptimizationStatus.
    /// Matches DO entries to tracked IME app downloads using the intunewin-bin FileId format
    /// (same ExtractAppIdFromDoFileId logic as the IME [DO TEL] path).
    /// Enriches AppPackageState with DO telemetry when IME no longer provides [DO TEL] entries.
    /// Always-on with dedup guard: skips apps where HasDoTelemetry is already true.
    /// </summary>
    public class DeliveryOptimizationCollector : CollectorBase
    {
        private const string LogFileName = "do-status.jsonl";
        private const int ProcessTimeoutMs = 30000;

        private static readonly string PsCommand =
            "Get-DeliveryOptimizationStatus | Select-Object FileId, FileSize, TotalBytesDownloaded, " +
            "PercentPeerCaching, BytesFromPeers, BytesFromHttp, Status, Priority, " +
            "BytesFromLanPeers, BytesFromGroupPeers, BytesFromInternetPeers, " +
            "DownloadDuration, DownloadMode, SourceURL, BytesFromCacheServer | ConvertTo-Json -Compress";

        private readonly Func<AppPackageStateList> _getPackageStates;
        private readonly Action<AppPackageState> _onDoTelemetryReceived;
        private readonly string _logFilePath;
        private bool _permanentlyDisabled;
        private int _consecutiveErrors;

        // Track which apps we already enriched (prevents duplicate callbacks across poll cycles)
        private readonly HashSet<string> _enrichedAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Change detection: only emit events and write log when DO state actually changed
        private long _lastSnapshotFingerprint;

        public DeliveryOptimizationCollector(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> emitEvent,
            AgentLogger logger,
            int intervalSeconds,
            Func<AppPackageStateList> getPackageStates,
            Action<AppPackageState> onDoTelemetryReceived,
            string logDirectory)
            : base(sessionId, tenantId, emitEvent, logger, intervalSeconds)
        {
            _getPackageStates = getPackageStates ?? throw new ArgumentNullException(nameof(getPackageStates));
            _onDoTelemetryReceived = onDoTelemetryReceived;
            _logFilePath = Path.Combine(logDirectory, LogFileName);
        }

        protected override TimeSpan GetInitialDelay() => TimeSpan.FromSeconds(10);

        protected override void Collect()
        {
            if (_permanentlyDisabled) return;

            // Guard: only poll when there are active downloads or recently completed apps without DO data
            var packageStates = _getPackageStates();
            if (packageStates == null || packageStates.Count == 0) return;

            var hasActiveDownloads = packageStates.Any(p =>
                p.InstallationState == AppInstallationState.Downloading ||
                p.InstallationState == AppInstallationState.Installing);

            var hasRecentlyCompletedWithoutDo = packageStates.Any(p =>
                !p.HasDoTelemetry &&
                !_enrichedAppIds.Contains(p.Id) &&
                p.DownloadingOrInstallingSeen &&
                (p.InstallationState == AppInstallationState.Installed ||
                 p.InstallationState == AppInstallationState.Error));

            if (!hasActiveDownloads && !hasRecentlyCompletedWithoutDo)
            {
                Logger.Debug("[DeliveryOptimizationCollector] No active downloads or pending DO enrichment, skipping poll");
                return;
            }

            // Execute PowerShell command
            var (output, error, exitCode) = RunPowerShellCommand();

            if (exitCode != 0 || output == null)
            {
                HandleError(error, exitCode);
                return;
            }

            _consecutiveErrors = 0;

            // Parse JSON output
            JArray entries;
            try
            {
                var token = JToken.Parse(output);
                // ConvertTo-Json returns a single object (not array) when there's only one result
                entries = token is JArray arr ? arr : new JArray(token);
            }
            catch (JsonReaderException ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Failed to parse DO JSON: {ex.Message}");
                return;
            }

            if (entries.Count == 0)
            {
                Logger.Debug("[DeliveryOptimizationCollector] No DO entries returned");
                return;
            }

            // Change detection: compute fingerprint from entry count + sum of TotalBytesDownloaded
            // Only write log / emit event when something actually changed
            var fingerprint = ComputeFingerprint(entries);
            var changed = fingerprint != _lastSnapshotFingerprint;
            _lastSnapshotFingerprint = fingerprint;

            // Match DO entries to tracked app downloads and enrich with telemetry
            // (always attempt — a new app may have appeared even if bytes didn't change)
            var matchCount = MatchAndEnrich(entries, packageStates);

            if (!changed && matchCount == 0)
            {
                Logger.Debug("[DeliveryOptimizationCollector] No DO changes since last poll, skipping event/log");
                return;
            }

            // Write raw snapshot to JSONL log file (only on change)
            WriteToLogFile(entries);

            // Log summary to agent log
            LogSummary(entries, packageStates, matchCount);

            // Emit lightweight status event
            EmitStatusEvent(entries, packageStates, matchCount);
        }

        /// <summary>
        /// Matches DO entries to tracked IME app downloads using the intunewin-bin FileId format.
        /// For matched apps where HasDoTelemetry is false, calls UpdateDoTelemetry and fires the callback.
        /// </summary>
        private int MatchAndEnrich(JArray entries, AppPackageStateList packageStates)
        {
            int matchCount = 0;

            foreach (var entry in entries)
            {
                var fileId = entry["FileId"]?.ToString();
                if (string.IsNullOrEmpty(fileId)) continue;

                // Use the same extraction logic as ImeLogTracker
                var appId = ImeLogTracker.ExtractAppIdFromDoFileId(fileId);
                if (string.IsNullOrEmpty(appId)) continue;

                var pkg = packageStates.GetPackage(appId);
                if (pkg == null) continue;

                // Dedup: skip if already enriched (by IME [DO TEL] path or previous poll cycle)
                if (pkg.HasDoTelemetry || _enrichedAppIds.Contains(appId))
                    continue;

                // Extract DO fields
                long fileSize = entry["FileSize"]?.Value<long>() ?? 0;
                long totalBytes = entry["TotalBytesDownloaded"]?.Value<long>() ?? 0;
                long bytesFromPeers = entry["BytesFromPeers"]?.Value<long>() ?? 0;
                int peerCachingPct = entry["PercentPeerCaching"]?.Value<int>() ?? 0;
                long bytesLanPeers = entry["BytesFromLanPeers"]?.Value<long>() ?? 0;
                long bytesGroupPeers = entry["BytesFromGroupPeers"]?.Value<long>() ?? 0;
                long bytesInternetPeers = entry["BytesFromInternetPeers"]?.Value<long>() ?? 0;
                int downloadMode = entry["DownloadMode"]?.Value<int>() ?? -1;
                long bytesFromHttp = entry["BytesFromHttp"]?.Value<long>() ?? 0;

                // DownloadDuration comes as a TimeSpan object from PowerShell (serialized with TotalSeconds etc.)
                string downloadDuration = ExtractDownloadDuration(entry["DownloadDuration"]);

                pkg.UpdateDoTelemetry(fileSize, totalBytes, bytesFromPeers, peerCachingPct,
                    bytesLanPeers, bytesGroupPeers, bytesInternetPeers,
                    downloadMode, downloadDuration, bytesFromHttp);

                _enrichedAppIds.Add(appId);
                matchCount++;

                Logger.Info($"[DeliveryOptimizationCollector] DO matched: {pkg.Name ?? appId} — " +
                            $"size={fileSize}, peers={bytesFromPeers} ({peerCachingPct}%), " +
                            $"http={bytesFromHttp}, mode={downloadMode}, duration={downloadDuration}");

                // Fire callback to trigger do_telemetry + download_progress events via EnrollmentTracker
                _onDoTelemetryReceived?.Invoke(pkg);
            }

            return matchCount;
        }

        /// <summary>
        /// Extracts a human-readable duration string from the PowerShell TimeSpan JSON object.
        /// PowerShell serializes TimeSpan as {"Ticks":..., "TotalSeconds":..., "Hours":..., ...}.
        /// Returns format "HH:mm:ss.fff" matching what IME [DO TEL] provides.
        /// </summary>
        private static string ExtractDownloadDuration(JToken durationToken)
        {
            if (durationToken == null || durationToken.Type == JTokenType.Null)
                return null;

            // If it's already a string (unlikely from PS but defensive), return as-is
            if (durationToken.Type == JTokenType.String)
                return durationToken.ToString();

            // PowerShell TimeSpan object: extract Ticks and reconstruct
            if (durationToken.Type == JTokenType.Object)
            {
                var ticks = durationToken["Ticks"]?.Value<long>() ?? 0;
                if (ticks > 0)
                {
                    var ts = TimeSpan.FromTicks(ticks);
                    return ts.ToString(@"hh\:mm\:ss\.fff");
                }

                // Fallback: use TotalSeconds if Ticks not available
                var totalSeconds = durationToken["TotalSeconds"]?.Value<double>() ?? 0;
                if (totalSeconds > 0)
                {
                    var ts = TimeSpan.FromSeconds(totalSeconds);
                    return ts.ToString(@"hh\:mm\:ss\.fff");
                }
            }

            return null;
        }

        /// <summary>
        /// Computes a lightweight fingerprint from DO entries to detect changes between polls.
        /// Uses entry count XOR'd with sum of TotalBytesDownloaded — changes when any download progresses,
        /// completes, or a new entry appears.
        /// </summary>
        private static long ComputeFingerprint(JArray entries)
        {
            long sum = entries.Count;
            foreach (var e in entries)
                sum += e["TotalBytesDownloaded"]?.Value<long>() ?? 0;
            return sum;
        }

        private (string output, string error, int exitCode) RunPowerShellCommand()
        {
            try
            {
                var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(PsCommand));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy RemoteSigned -EncodedCommand {encodedCommand}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(ProcessTimeoutMs))
                    {
                        try { process.Kill(); } catch { /* best effort */ }
                        Logger.Warning("[DeliveryOptimizationCollector] PowerShell process timed out after 30s, killed");
                        return (null, "timeout", -1);
                    }

                    return (
                        string.IsNullOrWhiteSpace(output) ? null : output.Trim(),
                        string.IsNullOrWhiteSpace(error) ? null : error.Trim(),
                        process.ExitCode
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] Failed to execute PowerShell: {ex.Message}");
                return (null, ex.Message, -1);
            }
        }

        private void HandleError(string error, int exitCode)
        {
            _consecutiveErrors++;

            // Detect cmdlet not available (older OS or Server SKU without DO module)
            if (error != null && (
                error.Contains("is not recognized as the name of a cmdlet") ||
                error.Contains("CommandNotFoundException")))
            {
                Logger.Warning("[DeliveryOptimizationCollector] Get-DeliveryOptimizationStatus not available on this OS, disabling collector permanently");
                _permanentlyDisabled = true;
                return;
            }

            if (_consecutiveErrors >= 5)
            {
                Logger.Warning($"[DeliveryOptimizationCollector] {_consecutiveErrors} consecutive errors, disabling collector permanently. Last error: {error}");
                _permanentlyDisabled = true;
                return;
            }

            Logger.Warning($"[DeliveryOptimizationCollector] PowerShell exited with code {exitCode}: {error ?? "(no error output)"}");
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

        private void LogSummary(JArray entries, AppPackageStateList packageStates, int matchCount)
        {
            var activeApps = packageStates.Where(p =>
                p.InstallationState == AppInstallationState.Downloading ||
                p.InstallationState == AppInstallationState.Installing).ToList();

            Logger.Info($"[DeliveryOptimizationCollector] DO snapshot: {entries.Count} entries, " +
                        $"{matchCount} matched to apps, {activeApps.Count} active downloads");
        }

        private void EmitStatusEvent(JArray entries, AppPackageStateList packageStates, int matchCount)
        {
            var activeApps = packageStates
                .Where(p => p.InstallationState == AppInstallationState.Downloading ||
                            p.InstallationState == AppInstallationState.Installing)
                .Select(p => new Dictionary<string, object>
                {
                    ["appId"] = p.Id,
                    ["name"] = p.Name,
                    ["bytesTotal"] = p.BytesTotal,
                    ["bytesDownloaded"] = p.BytesDownloaded,
                    ["hasDoTelemetry"] = p.HasDoTelemetry
                })
                .ToList();

            var data = new Dictionary<string, object>
            {
                ["doEntryCount"] = entries.Count,
                ["matchedCount"] = matchCount,
                ["totalEnriched"] = _enrichedAppIds.Count,
                ["activeDownloads"] = activeApps
            };

            EmitEvent(new EnrollmentEvent
            {
                SessionId = SessionId,
                TenantId = TenantId,
                EventType = "do_status_snapshot",
                Severity = EventSeverity.Debug,
                Source = "DeliveryOptimizationCollector",
                Phase = EnrollmentPhase.Unknown,
                Message = $"DO status: {entries.Count} entries, {matchCount} new matches ({_enrichedAppIds.Count} total enriched), {activeApps.Count} active downloads",
                Data = data
            });
        }

        private static bool IsIntuneCdnUrl(string url)
        {
            return url.IndexOf(".dl.delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("swda01.azureedge.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("swda02.azureedge.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("swdb01.azureedge.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("swdb02.azureedge.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("swdc01.azureedge.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("swdc02.azureedge.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("swdd01.azureedge.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("swdd02.azureedge.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("naprodimedatapri", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("naprodimedatasec", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("euprodimedatapri", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("euprodimedatasec", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
