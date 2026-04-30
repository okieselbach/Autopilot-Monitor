#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Produktions-<see cref="IBackendTelemetryUploader"/>. Plan §2.7a / §4.x M4.4.2.
    /// <para>
    /// HTTP-Skelett gegen <c>POST /api/agent/telemetry</c>. Sendet einen Batch von
    /// <see cref="TelemetryItem"/>s als JSON-Array; Backend routet pro Item nach
    /// <see cref="TelemetryItemKind"/> in die Ziel-Tabelle (Events / Signals /
    /// DecisionTransitions) und dedupliziert via (<c>PartitionKey</c>, <c>RowKey</c>).
    /// </para>
    /// <para>
    /// <b>Retry/Back-off</b> übernimmt <see cref="TelemetryUploadOrchestrator"/> (M4.1) — dieser
    /// Uploader macht genau einen HTTP-Call und mapped die Response auf
    /// <see cref="UploadResult"/>.
    /// </para>
    /// <para>
    /// <b>mTLS</b>: Client-Zertifikat wird über den vom Caller eingerichteten
    /// <see cref="HttpClient"/> (<see cref="HttpClientHandler.ClientCertificates"/>) gesendet —
    /// dieser Uploader ist cert-agnostisch. Tests injizieren einen <see cref="HttpClient"/>
    /// mit Fake-<see cref="HttpMessageHandler"/>.
    /// </para>
    /// </summary>
    public sealed class BackendTelemetryUploader : IBackendTelemetryUploader
    {
        private readonly HttpClient _httpClient;
        private readonly string _endpointUrl;
        private readonly string _tenantId;
        private readonly string? _manufacturer;
        private readonly string? _model;
        private readonly string? _serialNumber;
        private readonly string? _bootstrapToken;
        private readonly string? _agentVersion;
        private readonly AuthFailureTracker? _authFailureTracker;
        private readonly Logging.AgentLogger? _logger;

        public BackendTelemetryUploader(
            HttpClient httpClient,
            string baseUrl,
            string tenantId,
            string? manufacturer = null,
            string? model = null,
            string? serialNumber = null,
            string? bootstrapToken = null,
            string? agentVersion = null,
            AuthFailureTracker? authFailureTracker = null,
            Logging.AgentLogger? logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (string.IsNullOrEmpty(baseUrl)) throw new ArgumentException("BaseUrl is mandatory.", nameof(baseUrl));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));

            _endpointUrl = baseUrl.TrimEnd('/') + Constants.ApiEndpoints.IngestTelemetry;
            _tenantId = tenantId;
            _manufacturer = manufacturer;
            _model = model;
            _serialNumber = serialNumber;
            _bootstrapToken = bootstrapToken;
            _agentVersion = agentVersion;
            _authFailureTracker = authFailureTracker;
            _logger = logger;
        }

        public async Task<UploadResult> UploadBatchAsync(
            IReadOnlyList<TelemetryItem> items,
            CancellationToken cancellationToken)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            // Empty batch: no-op success. Avoids burning a round-trip when the spool pulls an
            // empty page (possible in race conditions after a concurrent drain succeeded).
            if (items.Count == 0) return UploadResult.Ok();

            // Bandwidth: the agent ships on low-bandwidth links (tethered 4G, remote OOBE),
            // so the body is gzip-compressed on the wire. ~70 % reduction for JSON-heavy
            // telemetry batches (matches legacy IngestEventsAsync). Response-side gzip is
            // handled transparently by HttpClientHandler.AutomaticDecompression in
            // MtlsHttpClientFactory — we only need to compress outbound here.
            var body = SerializeBatch(items);
            var compressedBody = CompressWithGzip(body);

            // Upload-cadence is pipeline mechanics — keep on Debug so a default Info log
            // is dominated by lifecycle events. Failures (TIMEOUT/NETWORK/TRANSIENT/PERMANENT)
            // below stay on Warning/Error so "why didn't my event arrive" surfaces at Info.
            _logger?.Debug(
                $"BackendTelemetryUploader: flushing {items.Count} item(s) (bytes={compressedBody.Length}, firstId={items[0].TelemetryItemId}, lastId={items[items.Count - 1].TelemetryItemId}).");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            using (var request = new HttpRequestMessage(HttpMethod.Post, _endpointUrl))
            {
                request.Content = new ByteArrayContent(compressedBody);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Content.Headers.ContentEncoding.Add("gzip");
                AddSecurityHeaders(request);

                HttpResponseMessage response;
                try
                {
                    response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller-initiated cancellation — propagate; the orchestrator's drain
                    // loop will stop entirely rather than schedule a retry.
                    throw;
                }
                catch (TaskCanceledException ex)
                {
                    sw.Stop();
                    _logger?.Warning($"BackendTelemetryUploader: ingest TIMEOUT after {sw.ElapsedMilliseconds}ms (items={items.Count}): {ex.Message}");
                    // HttpClient timeout (not caller cancellation) surfaces as TaskCanceledException
                    // with cancellationToken.IsCancellationRequested == false.
                    return UploadResult.Transient($"timeout: {ex.Message}");
                }
                catch (HttpRequestException ex)
                {
                    sw.Stop();
                    _logger?.Warning($"BackendTelemetryUploader: ingest NETWORK fail after {sw.ElapsedMilliseconds}ms (items={items.Count}): {ex.Message}");
                    // DNS failure, socket error, etc. — network-layer is retryable.
                    return UploadResult.Transient($"network: {ex.Message}");
                }

                using (response)
                {
                    var result = await MapResponseAsync(response).ConfigureAwait(false);
                    sw.Stop();

                    if (result.Success)
                    {
                        _logger?.Debug($"BackendTelemetryUploader: ingest OK (items={items.Count}, durationMs={sw.ElapsedMilliseconds}, status={(int)response.StatusCode}).");
                    }
                    else if (result.IsTransient)
                    {
                        _logger?.Warning($"BackendTelemetryUploader: ingest TRANSIENT (items={items.Count}, durationMs={sw.ElapsedMilliseconds}, status={(int)response.StatusCode}): {result.ErrorReason}");
                    }
                    else
                    {
                        _logger?.Error($"BackendTelemetryUploader: ingest PERMANENT fail (items={items.Count}, durationMs={sw.ElapsedMilliseconds}, status={(int)response.StatusCode}): {result.ErrorReason}");
                    }

                    return result;
                }
            }
        }

        private async Task<UploadResult> MapResponseAsync(HttpResponseMessage response)
        {
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                // Auth was accepted — reset the consecutive-failure counter so a transient
                // backend hiccup does not eventually shut the agent down.
                _authFailureTracker?.RecordSuccess();

                // M4.6.ε — parse backend-to-agent control signals from the 2xx response body.
                // Body is best-effort JSON; anything unparseable degrades cleanly to plain Ok().
                return await TryReadControlSignalsAsync(response).ConfigureAwait(false);
            }

            var reason = await TryReadReasonAsync(response).ConfigureAwait(false);
            var shortReason = $"http {statusCode}{(string.IsNullOrEmpty(reason) ? string.Empty : ": " + reason)}";

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:       // 401
                case HttpStatusCode.Forbidden:          // 403
                    // Feed the central tracker so the agent can shut down after MaxAuthFailures
                    // instead of retrying a permanently-revoked certificate forever.
                    _authFailureTracker?.RecordFailure(statusCode, "agent/telemetry");
                    return UploadResult.Permanent($"unauthorized: {shortReason}");

                case HttpStatusCode.RequestTimeout:     // 408
                case (HttpStatusCode)429:               // TooManyRequests — enum available net48 SP1+, cast for safety
                    return UploadResult.Transient(shortReason);
            }

            if (statusCode >= 500 && statusCode <= 599) return UploadResult.Transient(shortReason);
            if (statusCode >= 400 && statusCode <= 499) return UploadResult.Permanent(shortReason);

            // Unexpected range (1xx, 3xx). Treat as transient — orchestrator retries and logs.
            return UploadResult.Transient(shortReason);
        }

        private static async Task<string> TryReadReasonAsync(HttpResponseMessage response)
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(body)) return string.Empty;
                return body.Length <= 200 ? body : body.Substring(0, 200);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// M4.6.ε — parse backend-to-agent control signals from a 2xx batch-response body. Fields
        /// are <b>optional</b>: an empty body or a body without any of these fields is treated as
        /// a plain <see cref="UploadResult.Ok"/>. Parse failures are logged into the response
        /// surface as a plain <c>Ok</c> — we never fail an upload over a malformed response body.
        /// <para>
        /// Recognised field names (case-insensitive, PascalCase and camelCase both accepted to
        /// match Legacy <c>IngestEventsResponse</c> and a future M5 batch response):
        /// <c>deviceBlocked</c> (bool), <c>unblockAt</c> (ISO-8601 UTC), <c>deviceKillSignal</c>
        /// (bool), <c>adminAction</c> (string), <c>actions</c> (<see cref="ServerAction"/>[]).
        /// </para>
        /// </summary>
        private static async Task<UploadResult> TryReadControlSignalsAsync(HttpResponseMessage response)
        {
            string body;
            try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { return UploadResult.Ok(); }

            if (string.IsNullOrWhiteSpace(body)) return UploadResult.Ok();

            JObject? root;
            try { root = JObject.Parse(body); }
            catch { return UploadResult.Ok(); }
            if (root == null) return UploadResult.Ok();

            var deviceBlocked = TryBool(root, "deviceBlocked", "DeviceBlocked") ?? false;
            var unblockAt = TryDateTime(root, "unblockAt", "UnblockAt");
            var deviceKillSignal = TryBool(root, "deviceKillSignal", "DeviceKillSignal") ?? false;
            var adminAction = TryString(root, "adminAction", "AdminAction");
            var actions = TryActions(root);

            var carriesSignal = deviceBlocked
                || unblockAt.HasValue
                || deviceKillSignal
                || !string.IsNullOrEmpty(adminAction)
                || (actions != null && actions.Count > 0);

            return carriesSignal
                ? UploadResult.OkWithSignals(
                    deviceBlocked: deviceBlocked,
                    unblockAt: unblockAt,
                    deviceKillSignal: deviceKillSignal,
                    adminAction: string.IsNullOrEmpty(adminAction) ? null : adminAction,
                    actions: actions)
                : UploadResult.Ok();
        }

        private static bool? TryBool(JObject root, params string[] names)
        {
            foreach (var n in names)
            {
                var tok = root[n];
                if (tok == null) continue;
                if (tok.Type == JTokenType.Boolean) return tok.Value<bool>();
                if (tok.Type == JTokenType.String && bool.TryParse(tok.Value<string>(), out var parsed)) return parsed;
            }
            return null;
        }

        private static string? TryString(JObject root, params string[] names)
        {
            foreach (var n in names)
            {
                var tok = root[n];
                if (tok == null) continue;
                if (tok.Type == JTokenType.String) return tok.Value<string>();
            }
            return null;
        }

        private static DateTime? TryDateTime(JObject root, params string[] names)
        {
            foreach (var n in names)
            {
                var tok = root[n];
                if (tok == null) continue;
                if (tok.Type == JTokenType.Date)
                {
                    var v = tok.Value<DateTime>();
                    return v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime();
                }
                if (tok.Type == JTokenType.String)
                {
                    var raw = tok.Value<string>();
                    if (DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                }
            }
            return null;
        }

        private static System.Collections.Generic.List<ServerAction>? TryActions(JObject root)
        {
            var tok = root["actions"] ?? root["Actions"];
            if (tok == null || tok.Type != JTokenType.Array) return null;

            try
            {
                var list = tok.ToObject<System.Collections.Generic.List<ServerAction>>();
                return list == null || list.Count == 0 ? null : list;
            }
            catch
            {
                return null;
            }
        }

        private static string SerializeBatch(IReadOnlyList<TelemetryItem> items)
        {
            // Newtonsoft PascalCase default + TelemetryItemKind carries [StringEnumConverter] →
            // Backend routes by string "Event"/"Signal"/"DecisionTransition". Wire-format
            // matches Plan §4.x M5 contract (PascalCase, StringEnum, ISO-8601 UTC).
            return JsonConvert.SerializeObject(items, Formatting.None);
        }

        private static byte[] CompressWithGzip(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionMode.Compress))
                {
                    gzip.Write(bytes, 0, bytes.Length);
                }
                return output.ToArray();
            }
        }

        private void AddSecurityHeaders(HttpRequestMessage request)
        {
            // X-Tenant-Id lets the backend run auth checks before parsing the body.
            request.Headers.Add("X-Tenant-Id", _tenantId);

            // Hardware headers for whitelist/Autopilot validation.
            if (!string.IsNullOrEmpty(_manufacturer)) request.Headers.Add("X-Device-Manufacturer", _manufacturer);
            if (!string.IsNullOrEmpty(_model)) request.Headers.Add("X-Device-Model", _model);
            if (!string.IsNullOrEmpty(_serialNumber)) request.Headers.Add("X-Device-SerialNumber", _serialNumber);

            // Bootstrap token auth for pre-MDM agents (cert-less OOBE phase).
            if (!string.IsNullOrEmpty(_bootstrapToken)) request.Headers.Add("X-Bootstrap-Token", _bootstrapToken);

            // Agent version for version-based kill-switch + server-side diagnostics.
            if (!string.IsNullOrEmpty(_agentVersion)) request.Headers.Add("X-Agent-Version", _agentVersion);
        }
    }
}
