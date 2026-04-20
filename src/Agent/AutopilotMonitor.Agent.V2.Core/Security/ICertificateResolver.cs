#nullable enable
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Abstraktion über <see cref="CertificateHelper.FindMdmCertificate"/>, damit Tests die
    /// Zert-Auflösung ohne echten Windows-Cert-Store ersetzen können. Plan §4.x M4.4.5.d.
    /// </summary>
    public interface ICertificateResolver
    {
        /// <summary>
        /// Sucht das MDM-Client-Zertifikat. <c>null</c> wenn nichts gefunden — der Caller
        /// (typischerweise <see cref="Orchestration.MtlsHttpClientFactory"/>) entscheidet dann
        /// über Fehlerbehandlung (wirft in Prod, Test-Resolver liefert null für die negative
        /// Test-Variante).
        /// </summary>
        X509Certificate2? FindClientCertificate(AgentLogger logger);
    }

    /// <summary>
    /// Produktiver <see cref="ICertificateResolver"/>. Delegiert an
    /// <see cref="CertificateHelper.FindMdmCertificate"/>.
    /// </summary>
    public sealed class DefaultCertificateResolver : ICertificateResolver
    {
        private readonly string? _thumbprintOverride;

        public DefaultCertificateResolver(string? thumbprintOverride = null)
        {
            _thumbprintOverride = thumbprintOverride;
        }

        public X509Certificate2? FindClientCertificate(AgentLogger logger) =>
            CertificateHelper.FindMdmCertificate(_thumbprintOverride, logger);
    }
}
