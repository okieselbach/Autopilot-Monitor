#nullable enable
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Produktions-<see cref="IBackendTelemetryUploader"/>. Plan §2.7a / §4.x M4.4.2.
    /// <para>
    /// HTTP-Skelett gegen <c>POST /api/telemetry/batch</c>. Sendet einen Batch von
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

        public BackendTelemetryUploader(
            HttpClient httpClient,
            string baseUrl,
            string tenantId,
            string? manufacturer = null,
            string? model = null,
            string? serialNumber = null,
            string? bootstrapToken = null,
            string? agentVersion = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (string.IsNullOrEmpty(baseUrl)) throw new ArgumentException("BaseUrl is mandatory.", nameof(baseUrl));
            if (string.IsNullOrEmpty(tenantId)) throw new ArgumentException("TenantId is mandatory.", nameof(tenantId));

            _endpointUrl = baseUrl.TrimEnd('/') + Constants.ApiEndpoints.TelemetryBatch;
            _tenantId = tenantId;
            _manufacturer = manufacturer;
            _model = model;
            _serialNumber = serialNumber;
            _bootstrapToken = bootstrapToken;
            _agentVersion = agentVersion;
        }

        public async Task<UploadResult> UploadBatchAsync(
            IReadOnlyList<TelemetryItem> items,
            CancellationToken cancellationToken)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));

            // Empty batch: no-op success. Avoids burning a round-trip when the spool pulls an
            // empty page (possible in race conditions after a concurrent drain succeeded).
            if (items.Count == 0) return UploadResult.Ok();

            var body = SerializeBatch(items);
            using (var request = new HttpRequestMessage(HttpMethod.Post, _endpointUrl))
            {
                request.Content = new StringContent(body, Encoding.UTF8);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
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
                    // HttpClient timeout (not caller cancellation) surfaces as TaskCanceledException
                    // with cancellationToken.IsCancellationRequested == false.
                    return UploadResult.Transient($"timeout: {ex.Message}");
                }
                catch (HttpRequestException ex)
                {
                    // DNS failure, socket error, etc. — network-layer is retryable.
                    return UploadResult.Transient($"network: {ex.Message}");
                }

                using (response)
                {
                    return await MapResponseAsync(response).ConfigureAwait(false);
                }
            }
        }

        private static async Task<UploadResult> MapResponseAsync(HttpResponseMessage response)
        {
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode) return UploadResult.Ok();

            var reason = await TryReadReasonAsync(response).ConfigureAwait(false);
            var shortReason = $"http {statusCode}{(string.IsNullOrEmpty(reason) ? string.Empty : ": " + reason)}";

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:       // 401
                case HttpStatusCode.Forbidden:          // 403
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

        private static string SerializeBatch(IReadOnlyList<TelemetryItem> items)
        {
            // Newtonsoft PascalCase default + TelemetryItemKind carries [StringEnumConverter] →
            // Backend routes by string "Event"/"Signal"/"DecisionTransition". Wire-format
            // matches Plan §4.x M5 contract (PascalCase, StringEnum, ISO-8601 UTC).
            return JsonConvert.SerializeObject(items, Formatting.None);
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
