using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.Core.Monitoring.Network
{
    /// <summary>
    /// Client for communicating with the backend API
    /// </summary>
    public class BackendApiClient : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly X509Certificate2 _clientCertificate;
        private readonly string _manufacturer;
        private readonly string _model;
        private readonly string _serialNumber;
        private readonly Logging.AgentLogger _logger;
        private readonly NetworkMetrics _networkMetrics = new NetworkMetrics();
        private readonly string _bootstrapToken;
        private readonly bool _useBootstrapTokenAuth;
        private readonly string _agentVersion;

        /// <summary>
        /// Exposes the network metrics counters for AgentSelfMetricsCollector to read.
        /// </summary>
        public NetworkMetrics NetworkMetrics => _networkMetrics;

        public BackendApiClient(string baseUrl, AgentConfiguration configuration = null, Logging.AgentLogger logger = null, string agentVersion = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _logger = logger;
            _bootstrapToken = configuration?.BootstrapToken;
            _useBootstrapTokenAuth = configuration?.UseBootstrapTokenAuth ?? false;
            _agentVersion = agentVersion;

            // Find MDM certificate for client authentication if enabled
            if (configuration?.UseClientCertAuth == true)
            {
                logger?.Debug("Client certificate authentication enabled - searching for certificate...");

                _clientCertificate = CertificateHelper.FindMdmCertificate(
                    configuration.ClientCertThumbprint,
                    logger
                );

                if (_clientCertificate != null)
                {
                    logger?.Info($"Client certificate loaded successfully");
                    logger?.Debug($"  Thumbprint: {_clientCertificate.Thumbprint}");
                    logger?.Debug($"  Subject: {_clientCertificate.Subject}");
                    logger?.Debug($"  Issuer: {_clientCertificate.Issuer}");
                    logger?.Debug($"  Valid: {_clientCertificate.NotBefore:yyyy-MM-dd} to {_clientCertificate.NotAfter:yyyy-MM-dd}");
                }
                else
                {
                    logger?.Warning("Client certificate authentication enabled but no certificate found");
                    logger?.Warning("  Request will be sent WITHOUT certificate (will likely fail security validation)");
                }
            }
            else
            {
                logger?.Debug("Client certificate authentication disabled");
            }

            // Get hardware information for whitelist validation and Autopilot device verification
            var hardwareInfo = HardwareInfo.GetHardwareInfo(logger);
            _manufacturer = hardwareInfo.Manufacturer;
            _model = hardwareInfo.Model;
            _serialNumber = hardwareInfo.SerialNumber;

            // Use HttpClientHandler with client certificate for TLS-level mTLS negotiation.
            // Azure App Service extracts the cert from the TLS handshake and forwards it
            // in the X-ARR-ClientCert header to the backend function.
            var handler = new HttpClientHandler();
            if (_clientCertificate != null)
            {
                handler.ClientCertificates.Add(_clientCertificate);
                logger?.Info("Client certificate attached to TLS handler for mTLS negotiation");
            }

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            // Set User-Agent for firewall allowlisting and server-side logging
            var ua = string.IsNullOrEmpty(_agentVersion)
                ? "AutopilotMonitor.Agent"
                : $"AutopilotMonitor.Agent/{_agentVersion}";
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        }

        /// <summary>
        /// Registers a new enrollment session
        /// </summary>
        public async Task<RegisterSessionResponse> RegisterSessionAsync(SessionRegistration registration)
        {
            var request = new RegisterSessionRequest { Registration = registration };
            var endpoint = _useBootstrapTokenAuth
                ? Constants.ApiEndpoints.BootstrapRegisterSession
                : Constants.ApiEndpoints.RegisterSession;
            var url = $"{_baseUrl}{endpoint}";

            var response = await PostAsync<RegisterSessionRequest, RegisterSessionResponse>(url, request);
            return response;
        }

        /// <summary>
        /// Ingests a batch of events using NDJSON + gzip compression for bandwidth optimization
        /// </summary>
        public async Task<IngestEventsResponse> IngestEventsAsync(IngestEventsRequest request)
        {
            var ingestEndpoint = _useBootstrapTokenAuth
                ? Constants.ApiEndpoints.BootstrapIngestEvents
                : Constants.ApiEndpoints.IngestEvents;
            var url = $"{_baseUrl}{ingestEndpoint}";

            // Use NDJSON (newline-delimited JSON) + gzip for efficient event upload
            // This reduces bandwidth significantly (70-80% compression typical for JSON)
            var ndjson = CreateNdjson(request);
            var ndjsonLength = System.Text.Encoding.UTF8.GetByteCount(ndjson);
            var compressedContent = CompressWithGzip(ndjson);

            _logger?.Debug($"IngestEventsAsync: POST {url} — {request.Events.Count} events, {compressedContent.Length} bytes compressed");
            _logger?.Verbose($"IngestEventsAsync: NDJSON {ndjsonLength} bytes → gzip {compressedContent.Length} bytes ({(ndjsonLength > 0 ? (100 - compressedContent.Length * 100 / ndjsonLength) : 0)}% reduction)");

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(compressedContent)
            };
            httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
            httpRequest.Content.Headers.Add("Content-Encoding", "gzip");

            // Add TenantId header so the backend can run security checks before parsing the body
            httpRequest.Headers.Add("X-Tenant-Id", request.TenantId);

            // Add additional security headers for device authorization (client cert is at TLS layer, but we can add hardware info in headers for whitelist validation and Autopilot device verification)
            AddSecurityHeaders(httpRequest);

            var sw = Stopwatch.StartNew();
            var failed = false;
            long bytesDown = 0;
            try
            {
                var httpResponse = await _httpClient.SendAsync(httpRequest);
                ThrowOnAuthFailure(httpResponse);
                httpResponse.EnsureSuccessStatusCode();

                var responseJson = await httpResponse.Content.ReadAsStringAsync();
                bytesDown = Encoding.UTF8.GetByteCount(responseJson);
                var response = JsonConvert.DeserializeObject<IngestEventsResponse>(responseJson);

                return response;
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                sw.Stop();
                _networkMetrics.RecordRequest(compressedContent.Length, bytesDown, sw.ElapsedMilliseconds, failed);
            }
        }

        /// <summary>
        /// Fetches the agent configuration (collector toggles + gather rules) from the backend
        /// </summary>
        public async Task<AgentConfigResponse> GetAgentConfigAsync(string tenantId)
        {
            var configEndpoint = _useBootstrapTokenAuth
                ? Constants.ApiEndpoints.BootstrapGetAgentConfig
                : Constants.ApiEndpoints.GetAgentConfig;
            var url = $"{_baseUrl}{configEndpoint}?tenantId={Uri.EscapeDataString(tenantId)}";

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            AddSecurityHeaders(httpRequest);

            _logger?.Debug($"GetAgentConfigAsync: GET {url}");

            var sw = Stopwatch.StartNew();
            var failed = false;
            long bytesDown = 0;
            try
            {
                var response = await _httpClient.SendAsync(httpRequest);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                bytesDown = Encoding.UTF8.GetByteCount(responseJson);
                var result = JsonConvert.DeserializeObject<AgentConfigResponse>(responseJson);
                return result;
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                sw.Stop();
                _networkMetrics.RecordRequest(0, bytesDown, sw.ElapsedMilliseconds, failed);
            }
        }

        /// <summary>
        /// Requests a short-lived SAS URL for diagnostics package upload.
        /// Called just before upload so the URL is never stored in config or on disk.
        /// Returns null if the request fails (non-fatal — diagnostics upload is best-effort).
        /// </summary>
        public async Task<GetDiagnosticsUploadUrlResponse> GetDiagnosticsUploadUrlAsync(
            string tenantId, string sessionId, string fileName)
        {
            var url = $"{_baseUrl}{Constants.ApiEndpoints.GetDiagnosticsUploadUrl}";
            var request = new GetDiagnosticsUploadUrlRequest
            {
                TenantId = tenantId,
                SessionId = sessionId,
                FileName = fileName
            };
            var response = await PostAsync<GetDiagnosticsUploadUrlRequest, GetDiagnosticsUploadUrlResponse>(url, request);
            return response;
        }

        /// <summary>
        /// Sends a critical error report to the emergency channel endpoint.
        /// Fire-and-forget: swallows all exceptions so a failure here never cascades.
        /// Uses a 5-second per-request timeout so it cannot block the upload loop.
        /// </summary>
        public async Task ReportAgentErrorAsync(AgentErrorReport report)
        {
            try
            {
                var errorEndpoint = _useBootstrapTokenAuth
                    ? Constants.ApiEndpoints.BootstrapReportError
                    : Constants.ApiEndpoints.ReportAgentError;
                var url = $"{_baseUrl}{errorEndpoint}";
                var json = JsonConvert.SerializeObject(report);

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                // Security validation on the backend requires tenant ID before parsing the body
                httpRequest.Headers.Add("X-Tenant-Id", report.TenantId);

                // Hardware headers are required for full security validation
                AddSecurityHeaders(httpRequest);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _httpClient.SendAsync(httpRequest, cts.Token);
                // Response status is deliberately ignored — endpoint always returns 200
            }
            catch (Exception ex)
            {
                // Swallow all exceptions — the emergency channel must never cascade failures
                _logger?.Debug($"ReportAgentErrorAsync: emergency channel failed: {ex.Message}");
            }
        }

        private async Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest data)
        {
            var json = JsonConvert.SerializeObject(data);
            var bytesUp = Encoding.UTF8.GetByteCount(json);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            // Add additional security headers for device authorization (client cert is at TLS layer, but we can add hardware info in headers for whitelist validation and Autopilot device verification)
            AddSecurityHeaders(httpRequest);

            var sw = Stopwatch.StartNew();
            var failed = false;
            long bytesDown = 0;
            try
            {
                var response = await _httpClient.SendAsync(httpRequest);
                ThrowOnAuthFailure(response);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                bytesDown = Encoding.UTF8.GetByteCount(responseJson);
                var result = JsonConvert.DeserializeObject<TResponse>(responseJson);

                return result;
            }
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                sw.Stop();
                _networkMetrics.RecordRequest(bytesUp, bytesDown, sw.ElapsedMilliseconds, failed);
            }
        }

        /// <summary>
        /// Throws BackendAuthException for 401/403 responses so callers can distinguish
        /// authentication failures from transient server errors.
        /// </summary>
        private static void ThrowOnAuthFailure(HttpResponseMessage response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new BackendAuthException(
                    $"Backend returned {(int)response.StatusCode} {response.StatusCode}. " +
                    "The device is not authorized. Check client certificate and Autopilot device validation.");
            }
        }

        /// <summary>
        /// Creates NDJSON (newline-delimited JSON) from IngestEventsRequest
        /// Format: metadata line + one event per line
        /// </summary>
        private string CreateNdjson(IngestEventsRequest request)
        {
            var lines = new System.Collections.Generic.List<string>();

            // First line: metadata (sessionId, tenantId)
            var metadata = new
            {
                sessionId = request.SessionId,
                tenantId = request.TenantId
            };
            lines.Add(JsonConvert.SerializeObject(metadata));

            // Subsequent lines: one event per line
            foreach (var evt in request.Events)
            {
                lines.Add(JsonConvert.SerializeObject(evt));
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// Compresses a string using gzip
        /// </summary>
        private byte[] CompressWithGzip(string text)
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

        /// <summary>
        /// Adds additional security headers for device authorization.
        /// Client certificate is sent at TLS layer via HttpClientHandler.ClientCertificates.
        /// </summary>
        private void AddSecurityHeaders(HttpRequestMessage request)
        {
            _logger?.Trace("Adding security headers to request...");

            // Bootstrap token auth (pre-MDM, OOBE bootstrapped agents)
            if (_useBootstrapTokenAuth && !string.IsNullOrEmpty(_bootstrapToken))
            {
                request.Headers.Add("X-Bootstrap-Token", _bootstrapToken);
                _logger?.Trace("  X-Bootstrap-Token: [set]");
            }

            // Client certificate is sent at TLS layer via HttpClientHandler.ClientCertificates (mTLS)

            // Add hardware information for whitelist validation
            if (!string.IsNullOrEmpty(_manufacturer))
            {
                request.Headers.Add("X-Device-Manufacturer", _manufacturer);
                _logger?.Trace($"  X-Device-Manufacturer: {_manufacturer}");
            }

            if (!string.IsNullOrEmpty(_model))
            {
                request.Headers.Add("X-Device-Model", _model);
                _logger?.Trace($"  X-Device-Model: {_model}");
            }

            // Add serial number for Autopilot device validation against Intune registration
            if (!string.IsNullOrEmpty(_serialNumber))
            {
                request.Headers.Add("X-Device-SerialNumber", _serialNumber);
                _logger?.Trace($"  X-Device-SerialNumber: {_serialNumber}");
            }

            // Add agent version for version-based block/kill management
            if (!string.IsNullOrEmpty(_agentVersion))
            {
                request.Headers.Add("X-Agent-Version", _agentVersion);
                _logger?.Trace($"  X-Agent-Version: {_agentVersion}");
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    /// <summary>
    /// Thrown when the backend returns 401 or 403, indicating the device is not authorized.
    /// </summary>
    public class BackendAuthException : Exception
    {
        public BackendAuthException(string message) : base(message) { }
    }
}
