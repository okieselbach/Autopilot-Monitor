using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.EventCollection
{
    /// <summary>
    /// Validates certificate chains for critical enrollment endpoints
    /// Detects SSL inspection, untrusted CAs, and certificate issues
    /// Optional collector - toggled on/off via remote config
    /// </summary>
    public class CertValidationCollector : IDisposable
    {
        private readonly AgentLogger _logger;
        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly Action<EnrollmentEvent> _onEventCollected;
        private bool _hasRun;

        // Critical enrollment endpoints to validate
        private static readonly EndpointCheck[] Endpoints = new[]
        {
            new EndpointCheck("enterpriseregistration.windows.net", 443, new[] { "Microsoft", "DigiCert" }),
            new EndpointCheck("login.microsoftonline.com", 443, new[] { "Microsoft", "DigiCert" }),
            new EndpointCheck("device.login.microsoftonline.com", 443, new[] { "Microsoft", "DigiCert" }),
            new EndpointCheck("enrollment.manage.microsoft.com", 443, new[] { "Microsoft", "DigiCert", "Baltimore" }),
            new EndpointCheck("portal.manage.microsoft.com", 443, new[] { "Microsoft", "DigiCert", "Baltimore" })
        };

        public CertValidationCollector(string sessionId, string tenantId, Action<EnrollmentEvent> onEventCollected,
            AgentLogger logger)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _onEventCollected = onEventCollected ?? throw new ArgumentNullException(nameof(onEventCollected));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Starts the collector - runs validation once at startup
        /// Call RunValidation() again for subsequent checks (e.g., on network phase completion)
        /// </summary>
        public void Start()
        {
            _logger.Info("Starting Certificate Validation collector");
            ThreadPool.QueueUserWorkItem(_ => RunValidation());
        }

        public void Stop()
        {
            _logger.Info("Stopping Certificate Validation collector");
        }

        /// <summary>
        /// Runs certificate validation for all endpoints
        /// Can be called multiple times (e.g., at startup + after network phase)
        /// </summary>
        public void RunValidation()
        {
            _logger.Info("Running certificate validation checks...");

            foreach (var endpoint in Endpoints)
            {
                try
                {
                    ValidateEndpoint(endpoint);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Cert validation for {endpoint.Hostname} failed: {ex.Message}");

                    _onEventCollected(new EnrollmentEvent
                    {
                        SessionId = _sessionId,
                        TenantId = _tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "cert_validation",
                        Severity = EventSeverity.Warning,
                        Source = "CertValidationCollector",
                        Message = $"Certificate validation failed for {endpoint.Hostname}: {ex.Message}",
                        Data = new Dictionary<string, object>
                        {
                            { "endpoint", endpoint.Hostname },
                            { "port", endpoint.Port },
                            { "chain_valid", false },
                            { "error", ex.Message },
                            { "ssl_inspection_detected", false }
                        }
                    });
                }
            }

            _hasRun = true;
            _logger.Info("Certificate validation checks complete");
        }

        private void ValidateEndpoint(EndpointCheck endpoint)
        {
            X509Certificate2 serverCert = null;
            X509Chain chain = null;
            var chainDetails = new List<Dictionary<string, string>>();
            bool chainValid = false;
            bool sslInspectionDetected = false;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create($"https://{endpoint.Hostname}:{endpoint.Port}");
                request.Timeout = 10000;
                request.AllowAutoRedirect = false;

                // Custom validation callback to capture certificate details
                request.ServerCertificateValidationCallback = (sender, certificate, certChain, sslPolicyErrors) =>
                {
                    if (certificate != null)
                    {
                        serverCert = new X509Certificate2(certificate);
                    }
                    chain = certChain;
                    chainValid = sslPolicyErrors == SslPolicyErrors.None;

                    // Capture chain details
                    if (certChain != null)
                    {
                        foreach (var element in certChain.ChainElements)
                        {
                            chainDetails.Add(new Dictionary<string, string>
                            {
                                { "subject", element.Certificate.Subject },
                                { "issuer", element.Certificate.Issuer },
                                { "thumbprint", element.Certificate.Thumbprint },
                                { "notBefore", element.Certificate.NotBefore.ToString("o") },
                                { "notAfter", element.Certificate.NotAfter.ToString("o") }
                            });
                        }
                    }

                    return true; // Accept all certs for inspection purposes
                };

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    // We just needed the certificate - response content doesn't matter
                }
            }
            catch (WebException ex)
            {
                // Connection failures or HTTP errors are OK - we still got the certificate
                if (serverCert == null)
                {
                    throw new Exception($"Could not connect to {endpoint.Hostname}: {ex.Message}");
                }
            }

            if (serverCert == null)
                return;

            // Check for SSL inspection by examining the issuer
            var issuer = serverCert.Issuer;
            sslInspectionDetected = !endpoint.ExpectedIssuers.Any(
                expected => issuer.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0
            );

            var severity = EventSeverity.Info;
            var message = $"Certificate valid for {endpoint.Hostname}";

            if (sslInspectionDetected)
            {
                severity = EventSeverity.Warning;
                message = $"SSL inspection detected for {endpoint.Hostname} (Issuer: {issuer})";
                _logger.Warning(message);
            }
            else if (!chainValid)
            {
                severity = EventSeverity.Warning;
                message = $"Certificate chain invalid for {endpoint.Hostname}";
                _logger.Warning(message);
            }

            var data = new Dictionary<string, object>
            {
                { "endpoint", endpoint.Hostname },
                { "port", endpoint.Port },
                { "chain_valid", chainValid },
                { "ssl_inspection_detected", sslInspectionDetected },
                { "subject", serverCert.Subject },
                { "issuer", serverCert.Issuer },
                { "thumbprint", serverCert.Thumbprint },
                { "not_before", serverCert.NotBefore.ToString("o") },
                { "not_after", serverCert.NotAfter.ToString("o") }
            };

            if (chainDetails.Count > 0)
            {
                data["chain_length"] = chainDetails.Count;
            }

            _onEventCollected(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                Timestamp = DateTime.UtcNow,
                EventType = "cert_validation",
                Severity = severity,
                Source = "CertValidationCollector",
                Message = message,
                Data = data
            });
        }

        public void Dispose()
        {
            Stop();
        }

        private class EndpointCheck
        {
            public string Hostname { get; }
            public int Port { get; }
            public string[] ExpectedIssuers { get; }

            public EndpointCheck(string hostname, int port, string[] expectedIssuers)
            {
                Hostname = hostname;
                Port = port;
                ExpectedIssuers = expectedIssuers;
            }
        }
    }
}
