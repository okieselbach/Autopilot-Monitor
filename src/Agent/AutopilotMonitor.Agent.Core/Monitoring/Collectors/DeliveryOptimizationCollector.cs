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
    /// Phase 1: Captures raw DO data to a local JSONL log file for analysis.
    /// Only polls when active app downloads are detected.
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
        private readonly string _logFilePath;
        private bool _permanentlyDisabled;
        private int _consecutiveErrors;

        public DeliveryOptimizationCollector(
            string sessionId,
            string tenantId,
            Action<EnrollmentEvent> emitEvent,
            AgentLogger logger,
            int intervalSeconds,
            Func<AppPackageStateList> getPackageStates,
            string logDirectory)
            : base(sessionId, tenantId, emitEvent, logger, intervalSeconds)
        {
            _getPackageStates = getPackageStates ?? throw new ArgumentNullException(nameof(getPackageStates));
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

            // Write raw snapshot to JSONL log file
            WriteToLogFile(entries);

            // Log summary to agent log
            LogSummary(entries, packageStates);

            // Emit lightweight status event
            EmitStatusEvent(entries, packageStates);
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

        private void LogSummary(JArray entries, AppPackageStateList packageStates)
        {
            // Count entries with Intune-like CDN URLs
            var intuneEntries = entries.Count(e =>
            {
                var url = e["SourceURL"]?.ToString();
                return !string.IsNullOrEmpty(url) && IsIntuneCdnUrl(url);
            });

            var activeApps = packageStates.Where(p =>
                p.InstallationState == AppInstallationState.Downloading ||
                p.InstallationState == AppInstallationState.Installing).ToList();

            Logger.Info($"[DeliveryOptimizationCollector] DO snapshot: {entries.Count} total entries, " +
                        $"{intuneEntries} with Intune CDN URLs, {activeApps.Count} active app downloads");

            // Log Intune-related entries at Info level for visibility
            foreach (var entry in entries)
            {
                var url = entry["SourceURL"]?.ToString();
                if (!string.IsNullOrEmpty(url) && IsIntuneCdnUrl(url))
                {
                    var fileId = entry["FileId"]?.ToString() ?? "?";
                    var fileSize = entry["FileSize"]?.Value<long>() ?? 0;
                    var status = entry["Status"]?.ToString() ?? "?";
                    var peerPct = entry["PercentPeerCaching"]?.Value<int>() ?? 0;
                    var mode = entry["DownloadMode"]?.ToString() ?? "?";

                    Logger.Info($"[DeliveryOptimizationCollector] Intune DO entry: " +
                                $"FileId={fileId}, Size={fileSize}, Status={status}, " +
                                $"PeerCaching={peerPct}%, Mode={mode}");
                }
            }
        }

        private void EmitStatusEvent(JArray entries, AppPackageStateList packageStates)
        {
            var intuneEntryCount = entries.Count(e =>
            {
                var url = e["SourceURL"]?.ToString();
                return !string.IsNullOrEmpty(url) && IsIntuneCdnUrl(url);
            });

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
                ["intuneEntryCount"] = intuneEntryCount,
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
                Message = $"DO status: {entries.Count} entries ({intuneEntryCount} Intune), {activeApps.Count} active downloads",
                Data = data
            });
        }

        private static bool IsIntuneCdnUrl(string url)
        {
            // Intune content delivery CDN patterns
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
