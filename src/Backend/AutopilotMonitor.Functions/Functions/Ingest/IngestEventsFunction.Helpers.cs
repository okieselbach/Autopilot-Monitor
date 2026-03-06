using System.IO.Compression;
using System.Linq;
using System;
using System.Net;
using System.Text;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Partial: Request parsing, event normalization, app install aggregation helpers.
    /// </summary>
    public partial class IngestEventsFunction
    {
        private async Task<IngestEventsRequest> ParseNdjsonGzipRequest(Stream body, string? tenantId = null)
        {
            // Get configuration for payload size limit (use default if tenantId not available yet)
            var config = !string.IsNullOrEmpty(tenantId)
                ? await _configService.GetConfigurationAsync(tenantId)
                : null;

            var maxPayloadSizeBytes = (config?.MaxNdjsonPayloadSizeMB ?? 5) * 1024 * 1024;

            // Decompress gzip with size limit protection
            using var decompressed = new MemoryStream();
            using (var gzip = new GZipStream(body, CompressionMode.Decompress, leaveOpen: true))
            {
                // Copy with size limit to prevent memory exhaustion attacks
                var buffer = new byte[8192];
                int bytesRead;
                long totalBytesRead = 0;

                while ((bytesRead = await gzip.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    totalBytesRead += bytesRead;

                    // Check if we've exceeded the maximum payload size
                    if (totalBytesRead > maxPayloadSizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"NDJSON payload size exceeds maximum allowed size of {config?.MaxNdjsonPayloadSizeMB ?? 5} MB (decompressed). " +
                            $"Current size: {totalBytesRead / 1024.0 / 1024.0:F2} MB"
                        );
                    }

                    await decompressed.WriteAsync(buffer, 0, bytesRead);
                }

                _logger.LogDebug($"NDJSON payload decompressed: {totalBytesRead / 1024.0:F2} KB (limit: {maxPayloadSizeBytes / 1024.0 / 1024.0} MB)");
            }

            decompressed.Position = 0;
            var ndjson = await new StreamReader(decompressed, Encoding.UTF8).ReadToEndAsync();

            // Parse NDJSON (newline-delimited JSON)
            var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length < 1)
            {
                throw new InvalidOperationException("NDJSON must contain at least metadata line");
            }

            // First line: metadata
            var metadata = JsonConvert.DeserializeObject<NdjsonMetadata>(lines[0]);
            if (metadata == null)
            {
                throw new InvalidOperationException("Failed to parse NDJSON metadata");
            }

            // Subsequent lines: events
            var events = new List<EnrollmentEvent>();
            for (int i = 1; i < lines.Length; i++)
            {
                var evt = JsonConvert.DeserializeObject<EnrollmentEvent>(lines[i]);
                if (evt != null)
                {
                    NormalizeEventData(evt);
                    events.Add(evt);
                }
            }

            return new IngestEventsRequest
            {
                SessionId = metadata.SessionId,
                TenantId = metadata.TenantId,
                Events = events
            };
        }

        /// <summary>
        /// Normalizes event Data dictionary by converting Newtonsoft JToken objects to native .NET types.
        /// Required because Newtonsoft.Json deserializes nested objects as JObject/JArray, which
        /// System.Text.Json (used by SignalR) cannot serialize correctly - producing [[[]]] instead of real values.
        /// </summary>
        private static void NormalizeEventData(EnrollmentEvent evt)
        {
            if (evt.Data == null || evt.Data.Count == 0) return;
            var normalized = new Dictionary<string, object>();
            foreach (var kvp in evt.Data)
                normalized[kvp.Key] = ConvertJTokenToNative(kvp.Value);
            evt.Data = normalized;
        }

        private static object ConvertJTokenToNative(object value)
        {
            if (value is JArray jArray)
                return jArray.Select(item => ConvertJTokenToNative(item)).ToList<object>();
            if (value is JObject jObject)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObject.Properties())
                    dict[prop.Name] = ConvertJTokenToNative(prop.Value);
                return dict;
            }
            if (value is JValue jValue)
                return jValue.Value ?? string.Empty;
            return value;
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

            var appName = evt.Data?.ContainsKey("appName") == true ? evt.Data["appName"]?.ToString() : null;
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
                        summary.DurationSeconds = Math.Max(1, (int)(evt.Timestamp - summary.StartedAt).TotalSeconds);
                    break;

                case "app_install_failed":
                    summary.Status = "Failed";
                    summary.CompletedAt = evt.Timestamp;
                    if (summary.StartedAt != DateTime.MinValue)
                        summary.DurationSeconds = Math.Max(1, (int)(evt.Timestamp - summary.StartedAt).TotalSeconds);
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
                            summary.DownloadBytes = Math.Max(summary.DownloadBytes, doFs);
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

        private static void RecalculateAppDurations(AppInstallAggregationState state)
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
                summary.DownloadDurationSeconds = (int)(state.InstallStartedAt.Value - state.DownloadStartedAt.Value).TotalSeconds;
            }

            // Full duration: from effective start to completion/failure.
            if (summary.CompletedAt.HasValue && summary.StartedAt != DateTime.MinValue &&
                summary.CompletedAt.Value >= summary.StartedAt)
            {
                summary.DurationSeconds = (int)(summary.CompletedAt.Value - summary.StartedAt).TotalSeconds;
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
