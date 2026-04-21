#nullable enable
using System;
using System.Net;
using System.Net.Http;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Baut einen <see cref="HttpClient"/> mit mTLS-Client-Cert für den
    /// <see cref="Transport.Telemetry.BackendTelemetryUploader"/>. Plan §4.x M4.4.5.d.
    /// <para>
    /// <b>Design</b>: Static Factory + <see cref="ICertificateResolver"/>-Seam statt direkte
    /// Abhängigkeit an <see cref="CertificateHelper"/>. Tests injizieren einen
    /// Fake-Resolver mit einem self-signed Cert; Prod nutzt
    /// <see cref="DefaultCertificateResolver"/>, der den Windows-Cert-Store durchsucht.
    /// </para>
    /// <para>
    /// <b>Konfiguration</b> (analog Legacy <c>BackendApiClient</c>):
    /// <list type="bullet">
    ///   <item><see cref="DecompressionMethods.GZip"/> + <see cref="DecompressionMethods.Deflate"/></item>
    ///   <item>Default-Timeout 30s (override via Parameter)</item>
    ///   <item>Keine <c>CookieContainer</c>, keine Default-Credentials</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class MtlsHttpClientFactory
    {
        /// <summary>Plan §4.x M4.4.5.d — Default-Timeout (analog Legacy).</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Baut einen <see cref="HttpClientHandler"/> mit attached Client-Cert. Wirft
        /// <see cref="InvalidOperationException"/> wenn <paramref name="resolver"/> kein
        /// Zertifikat liefert — der Agent darf ohne Cert nicht starten (mTLS ist Pflicht).
        /// <para>
        /// Separat exponiert, damit Tests direkt auf dem Handler asserten können
        /// (<see cref="HttpClient"/>'s <c>_handler</c>-Feld ist internal und in net48
        /// reflection-unzuverlässig).
        /// </para>
        /// </summary>
        public static HttpClientHandler CreateHandler(
            ICertificateResolver resolver,
            AgentLogger logger)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            var cert = resolver.FindClientCertificate(logger);
            if (cert == null)
            {
                throw new InvalidOperationException(
                    "MtlsHttpClientFactory: no MDM client certificate found. " +
                    "Agent cannot start without a client cert (mTLS is mandatory). " +
                    "Check cert enrollment status in HKLM\\SOFTWARE\\Microsoft\\Enrollments.");
            }

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            };
            handler.ClientCertificates.Add(cert);

            logger.Info(
                $"MtlsHttpClientFactory: handler ready (cert thumbprint={cert.Thumbprint}).");

            return handler;
        }

        /// <summary>
        /// Baut einen fertig konfigurierten <see cref="HttpClient"/> — Handler via
        /// <see cref="CreateHandler"/>, Timeout via Parameter (default <see cref="DefaultTimeout"/>).
        /// </summary>
        public static HttpClient Create(
            ICertificateResolver resolver,
            AgentLogger logger,
            TimeSpan? timeout = null)
        {
            var handler = CreateHandler(resolver, logger);
            return new HttpClient(handler)
            {
                Timeout = timeout ?? DefaultTimeout,
            };
        }
    }
}
