using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

        public BackendApiClient(string baseUrl, AgentConfiguration configuration = null, Logging.AgentLogger logger = null)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _logger = logger;

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

            // Get hardware information for whitelist validation and serial number verification
            var hardwareInfo = HardwareInfo.GetHardwareInfo(logger);
            _manufacturer = hardwareInfo.Manufacturer;
            _model = hardwareInfo.Model;
            _serialNumber = hardwareInfo.SerialNumber;

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        /// <summary>
        /// Registers a new enrollment session
        /// </summary>
        public async Task<RegisterSessionResponse> RegisterSessionAsync(SessionRegistration registration)
        {
            var request = new RegisterSessionRequest { Registration = registration };
            var url = $"{_baseUrl}{Constants.ApiEndpoints.RegisterSession}";

            var response = await PostAsync<RegisterSessionRequest, RegisterSessionResponse>(url, request);
            return response;
        }

        /// <summary>
        /// Ingests a batch of events using NDJSON + gzip compression for bandwidth optimization
        /// </summary>
        public async Task<IngestEventsResponse> IngestEventsAsync(IngestEventsRequest request)
        {
            var url = $"{_baseUrl}{Constants.ApiEndpoints.IngestEvents}";

            // Use NDJSON (newline-delimited JSON) + gzip for efficient event upload
            // This reduces bandwidth significantly (70-80% compression typical for JSON)
            var ndjson = CreateNdjson(request);
            var compressedContent = CompressWithGzip(ndjson);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(compressedContent)
            };
            httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-ndjson");
            httpRequest.Content.Headers.Add("Content-Encoding", "gzip");

            // Add client certificate for device authentication (if available)
            AddClientCertificateHeader(httpRequest);

            var httpResponse = await _httpClient.SendAsync(httpRequest);
            httpResponse.EnsureSuccessStatusCode();

            var responseJson = await httpResponse.Content.ReadAsStringAsync();
            var response = JsonConvert.DeserializeObject<IngestEventsResponse>(responseJson);

            return response;
        }

        /// <summary>
        /// Fetches the agent configuration (collector toggles + gather rules) from the backend
        /// </summary>
        public async Task<AgentConfigResponse> GetAgentConfigAsync(string tenantId)
        {
            var url = $"{_baseUrl}{Constants.ApiEndpoints.GetAgentConfig}?tenantId={Uri.EscapeDataString(tenantId)}";

            var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            AddClientCertificateHeader(httpRequest);

            var response = await _httpClient.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<AgentConfigResponse>(responseJson);
            return result;
        }

        /// <summary>
        /// Gets a SAS URL for uploading a troubleshooting bundle
        /// </summary>
        public async Task<UploadBundleResponse> GetBundleUploadUrlAsync(UploadBundleRequest request)
        {
            var url = $"{_baseUrl}{Constants.ApiEndpoints.UploadBundle}";
            var response = await PostAsync<UploadBundleRequest, UploadBundleResponse>(url, request);
            return response;
        }

        private async Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest data)
        {
            var json = JsonConvert.SerializeObject(data);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            // Add device identification headers for all API calls
            AddClientCertificateHeader(httpRequest);

            var response = await _httpClient.SendAsync(httpRequest);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<TResponse>(responseJson);

            return result;
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
        /// Adds device identification headers for authentication and authorization
        /// Includes: Client certificate, manufacturer, model, serial number
        /// </summary>
        private void AddClientCertificateHeader(HttpRequestMessage request)
        {
            _logger?.Debug("Adding security headers to request...");

            // Add client certificate for device authentication
            if (_clientCertificate != null)
            {
                var certBytes = _clientCertificate.Export(X509ContentType.Cert);
                var certBase64 = Convert.ToBase64String(certBytes);
                request.Headers.Add("X-Client-Certificate", certBase64);
                _logger?.Debug($"  X-Client-Certificate: {_clientCertificate.Thumbprint.Substring(0, Math.Min(16, _clientCertificate.Thumbprint.Length))}...");
            }
            else
            {
                _logger?.Debug("  X-Client-Certificate: NOT SENT (no certificate available)");
            }

            // Add hardware information for whitelist validation
            if (!string.IsNullOrEmpty(_manufacturer))
            {
                request.Headers.Add("X-Device-Manufacturer", _manufacturer);
                _logger?.Debug($"  X-Device-Manufacturer: {_manufacturer}");
            }

            if (!string.IsNullOrEmpty(_model))
            {
                request.Headers.Add("X-Device-Model", _model);
                _logger?.Debug($"  X-Device-Model: {_model}");
            }

            // Add serial number for optional backend validation against Intune Autopilot registration
            if (!string.IsNullOrEmpty(_serialNumber))
            {
                request.Headers.Add("X-Device-SerialNumber", _serialNumber);
                _logger?.Debug($"  X-Device-SerialNumber: {_serialNumber}");
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
