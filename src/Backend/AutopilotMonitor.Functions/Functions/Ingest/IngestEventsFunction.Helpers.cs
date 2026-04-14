using System.Linq;
using System;
using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Partial: Request parsing, event normalization, app install aggregation helpers.
    /// </summary>
    public partial class IngestEventsFunction
    {
        private async Task<IngestEventsRequest> ParseNdjsonRequest(Stream body, string? tenantId = null)
        {
            var config = !string.IsNullOrEmpty(tenantId)
                ? await _configService.GetConfigurationAsync(tenantId)
                : null;

            var maxPayloadSizeBytes = (config?.MaxNdjsonPayloadSizeMB ?? 5) * 1024 * 1024;

            // body is already gzip-decompressed by the UseRequestDecompression middleware if the
            // client sent Content-Encoding: gzip — parser reads plain NDJSON.
            var (sessionId, metaTenantId, events) = await NdjsonParser.ParseNdjsonStreamAsync(body, maxPayloadSizeBytes);
            _logger.LogDebug("NDJSON payload parsed: {EventCount} events (limit: {LimitMB} MB)",
                events.Count, config?.MaxNdjsonPayloadSizeMB ?? 5);

            return new IngestEventsRequest
            {
                SessionId = sessionId,
                TenantId = metaTenantId,
                Events = events
            };
        }

        /// <summary>
        /// Aggregates app install events into AppInstallSummary records
        /// </summary>
        private void AggregateAppInstallEvent(EnrollmentEvent evt, string tenantId, string sessionId, Dictionary<string, AppInstallAggregationState> summaries)
        {
            // Agent sends: app_install_started, app_install_completed, app_install_failed, app_download_started, download_progress
            // Support both legacy (app_install_start/complete) and current agent event types
            bool isRelevant =
                evt.EventType == "app_install_started" || evt.EventType == "app_install_start" ||
                evt.EventType == "app_install_completed" || evt.EventType == "app_install_complete" ||
                evt.EventType == "app_install_failed" ||
                evt.EventType == "app_download_started" ||
                evt.EventType == "app_install_skipped" ||
                evt.EventType == "download_progress" ||
                evt.EventType == "do_telemetry";

            if (!isRelevant) return;

            var appName = evt.Data?.ContainsKey("appName") == true ? evt.Data["appName"]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(appName)) return;

            if (!summaries.TryGetValue(appName, out var state))
            {
                state = new AppInstallAggregationState
                {
                    Summary = new AppInstallSummary
                    {
                        AppName = appName,
                        SessionId = sessionId,
                        TenantId = tenantId,
                        StartedAt = evt.Timestamp
                    }
                };
                summaries[appName] = state;
            }

            var summary = state.Summary;

            // App metadata fields (may appear on any app_install_* event).
            // Only overwrite if the incoming value is non-empty so earlier values
            // from app_install_started aren't wiped by later _completed/_failed events.
            if (evt.Data != null)
            {
                if (evt.Data.TryGetValue("appVersion", out var appVersionObj))
                {
                    var appVersion = appVersionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(appVersion))
                        summary.AppVersion = appVersion.Trim();
                }
                if (evt.Data.TryGetValue("appType", out var appTypeObj))
                {
                    var appType = appTypeObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(appType))
                        summary.AppType = appType.Trim();
                }
                if (evt.Data.TryGetValue("attemptNumber", out var attemptObj) &&
                    int.TryParse(attemptObj?.ToString(), out var attempt) && attempt > 0)
                {
                    // Keep highest observed attempt (monotonic within a session)
                    summary.AttemptNumber = Math.Max(summary.AttemptNumber, attempt);
                }
                if (evt.Data.TryGetValue("installerPhase", out var phaseObj))
                {
                    var phase = phaseObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(phase))
                        summary.InstallerPhase = phase.Trim();
                }
                if (evt.Data.TryGetValue("exitCode", out var exitCodeObj) &&
                    int.TryParse(exitCodeObj?.ToString(), out var exitCode))
                {
                    summary.ExitCode = exitCode;
                }
                if (evt.Data.TryGetValue("detectionResult", out var detectionObj))
                {
                    var detection = detectionObj?.ToString();
                    if (!string.IsNullOrWhiteSpace(detection))
                        summary.DetectionResult = detection.Trim();
                }
            }

            switch (evt.EventType)
            {
                case "app_install_started":
                case "app_install_start":
                    if (!state.InstallStartedAt.HasValue || evt.Timestamp < state.InstallStartedAt.Value)
                        state.InstallStartedAt = evt.Timestamp;
                    if (summary.Status == "InProgress" || summary.Status == string.Empty)
                        summary.Status = "InProgress";
                    break;

                case "app_download_started":
                    if (!state.DownloadStartedAt.HasValue || evt.Timestamp < state.DownloadStartedAt.Value)
                        state.DownloadStartedAt = evt.Timestamp;
                    if (summary.Status == "InProgress" || summary.Status == string.Empty)
                        summary.Status = "InProgress";
                    break;

                case "app_install_completed":
                case "app_install_complete":
                    summary.Status = "Succeeded";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                        summary.DurationSeconds = Math.Max(1, EventTimestampValidator.SafeDurationSeconds(summary.StartedAt, evt.Timestamp));
                    break;

                case "app_install_failed":
                    summary.Status = "Failed";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                        summary.DurationSeconds = Math.Max(1, EventTimestampValidator.SafeDurationSeconds(summary.StartedAt, evt.Timestamp));
                    // Agent does not send errorCode/errorMessage in Data — use the event message
                    summary.FailureCode = evt.Data?.ContainsKey("errorCode") == true
                        ? evt.Data["errorCode"]?.ToString() ?? string.Empty : string.Empty;
                    summary.FailureMessage = evt.Data?.ContainsKey("errorMessage") == true
                        ? evt.Data["errorMessage"]?.ToString() ?? string.Empty : evt.Message ?? string.Empty;
                    break;

                case "app_install_skipped":
                    // Mark as succeeded (skipped = already installed / not applicable) with 0 duration
                    if (summary.Status == "InProgress")
                        summary.Status = "Succeeded";
                    break;

                case "download_progress":
                    // Agent sends "bytesDownloaded" (not "bytes_downloaded")
                    var bytesKey = evt.Data?.ContainsKey("bytesDownloaded") == true ? "bytesDownloaded"
                        : evt.Data?.ContainsKey("bytes_downloaded") == true ? "bytes_downloaded" : null;
                    if (bytesKey != null && long.TryParse(evt.Data![bytesKey]?.ToString(), out var bytes))
                        summary.DownloadBytes = Math.Max(summary.DownloadBytes, bytes);
                    break;

                case "do_telemetry":
                    if (evt.Data != null)
                    {
                        if (evt.Data.ContainsKey("doFileSize") && long.TryParse(evt.Data["doFileSize"]?.ToString(), out var doFs))
                        {
                            summary.DownloadBytes = Math.Max(summary.DownloadBytes, doFs);
                            summary.DoFileSize = doFs;
                        }
                        if (evt.Data.ContainsKey("doTotalBytesDownloaded") && long.TryParse(evt.Data["doTotalBytesDownloaded"]?.ToString(), out var doTotalDl))
                            summary.DoTotalBytesDownloaded = doTotalDl;
                        if (evt.Data.ContainsKey("doBytesFromPeers") && long.TryParse(evt.Data["doBytesFromPeers"]?.ToString(), out var doPeers))
                            summary.DoBytesFromPeers = doPeers;
                        if (evt.Data.ContainsKey("doBytesFromHttp") && long.TryParse(evt.Data["doBytesFromHttp"]?.ToString(), out var doHttp))
                            summary.DoBytesFromHttp = doHttp;
                        if (evt.Data.ContainsKey("doPercentPeerCaching") && int.TryParse(evt.Data["doPercentPeerCaching"]?.ToString(), out var doPct))
                            summary.DoPercentPeerCaching = doPct;
                        if (evt.Data.ContainsKey("doDownloadMode") && int.TryParse(evt.Data["doDownloadMode"]?.ToString(), out var doMode))
                            summary.DoDownloadMode = doMode;
                        if (evt.Data.ContainsKey("doDownloadDuration"))
                        {
                            var doDurStr = evt.Data["doDownloadDuration"]?.ToString() ?? string.Empty;
                            summary.DoDownloadDuration = doDurStr;
                            // Use DO's measured duration for throughput calculation (more accurate than timestamp diff)
                            if (TimeSpan.TryParse(doDurStr, out var doDurTs) && doDurTs.TotalSeconds >= 1)
                                summary.DownloadDurationSeconds = Math.Max(summary.DownloadDurationSeconds, (int)doDurTs.TotalSeconds);
                        }
                        if (evt.Data.ContainsKey("doBytesFromLanPeers") && long.TryParse(evt.Data["doBytesFromLanPeers"]?.ToString(), out var doLan))
                            summary.DoBytesFromLanPeers = doLan;
                        if (evt.Data.ContainsKey("doBytesFromGroupPeers") && long.TryParse(evt.Data["doBytesFromGroupPeers"]?.ToString(), out var doGroup))
                            summary.DoBytesFromGroupPeers = doGroup;
                        if (evt.Data.ContainsKey("doBytesFromInternetPeers") && long.TryParse(evt.Data["doBytesFromInternetPeers"]?.ToString(), out var doInet))
                            summary.DoBytesFromInternetPeers = doInet;
                    }
                    break;
            }

            RecalculateAppDurations(state);
        }

        internal static void RecalculateAppDurations(AppInstallAggregationState state)
        {
            var summary = state.Summary;

            // Effective start for full app duration: earliest known install/download start.
            var effectiveStart = summary.StartedAt;
            if (state.DownloadStartedAt.HasValue &&
                (effectiveStart == DateTime.MinValue || state.DownloadStartedAt.Value < effectiveStart))
            {
                effectiveStart = state.DownloadStartedAt.Value;
            }

            if (state.InstallStartedAt.HasValue &&
                (effectiveStart == DateTime.MinValue || state.InstallStartedAt.Value < effectiveStart))
            {
                effectiveStart = state.InstallStartedAt.Value;
            }

            if (effectiveStart != DateTime.MinValue)
            {
                summary.StartedAt = effectiveStart;
            }

            // Download duration: from first download start to first install start.
            if (state.DownloadStartedAt.HasValue && state.InstallStartedAt.HasValue &&
                state.InstallStartedAt.Value >= state.DownloadStartedAt.Value)
            {
                summary.DownloadDurationSeconds = EventTimestampValidator.SafeDurationSeconds(
                    state.DownloadStartedAt.Value, state.InstallStartedAt.Value);
            }

            // Full duration: from effective start to completion/failure.
            if (summary.CompletedAt.HasValue && summary.StartedAt != DateTime.MinValue &&
                summary.CompletedAt.Value >= summary.StartedAt)
            {
                summary.DurationSeconds = EventTimestampValidator.SafeDurationSeconds(
                    summary.StartedAt, summary.CompletedAt.Value);
            }
        }

        private async Task<IngestEventsOutput> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
        {
            var response = req.CreateResponse(statusCode);
            var errorResponse = new IngestEventsResponse
            {
                Success = false,
                EventsReceived = 0,
                EventsProcessed = 0,
                Message = message,
                ProcessedAt = DateTime.UtcNow
            };
            await response.WriteAsJsonAsync(errorResponse);
            return new IngestEventsOutput { HttpResponse = response, SignalRMessages = Array.Empty<SignalRMessageAction>() };
        }
    }
}
