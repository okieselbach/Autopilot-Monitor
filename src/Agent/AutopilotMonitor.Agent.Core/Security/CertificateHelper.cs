using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Agent.Core.Logging;

namespace AutopilotMonitor.Agent.Core.Security
{
    /// <summary>
    /// Helper for finding and validating MDM client certificates
    /// </summary>
    public static class CertificateHelper
    {
        /// <summary>
        /// Finds the MDM device certificate for client authentication
        /// </summary>
        /// <param name="thumbprint">Optional specific thumbprint to search for</param>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <returns>The MDM certificate, or null if not found</returns>
        public static X509Certificate2 FindMdmCertificate(string thumbprint = null, AgentLogger logger = null)
        {
            try
            {
                // Search locations for MDM certificate
                var stores = new[]
                {
                    new { Location = StoreLocation.LocalMachine, Name = StoreName.My },
                    new { Location = StoreLocation.CurrentUser, Name = StoreName.My }
                };

                foreach (var storeInfo in stores)
                {
                    using (var store = new X509Store(storeInfo.Name, storeInfo.Location))
                    {
                        store.Open(OpenFlags.ReadOnly);

                        logger?.Debug($"Searching for MDM certificate in {storeInfo.Location}\\{storeInfo.Name}");

                        // If thumbprint specified, search by thumbprint
                        if (!string.IsNullOrEmpty(thumbprint))
                        {
                            var cert = store.Certificates
                                .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
                                .OfType<X509Certificate2>()
                                .FirstOrDefault();

                            if (cert != null)
                            {
                                logger?.Info($"Found certificate by thumbprint: {thumbprint}");
                                return cert;
                            }
                        }
                        else
                        {
                            // Auto-detect MDM certificate by issuer and EKU
                            var mdmCerts = store.Certificates
                                .OfType<X509Certificate2>()
                                .Where(c =>
                                {
                                    // Check issuer (Microsoft Intune MDM Device CA or similar)
                                    var issuerContainsMdm = c.Issuer.Contains("Microsoft Intune", StringComparison.OrdinalIgnoreCase) ||
                                                           c.Issuer.Contains("MDM Device CA", StringComparison.OrdinalIgnoreCase) ||
                                                           c.Issuer.Contains("Microsoft Device CA", StringComparison.OrdinalIgnoreCase);

                                    // Check Enhanced Key Usage for Client Authentication (1.3.6.1.5.5.7.3.2)
                                    var hasClientAuth = c.Extensions
                                        .OfType<X509EnhancedKeyUsageExtension>()
                                        .Any(ext => ext.EnhancedKeyUsages
                                            .OfType<Oid>()
                                            .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"));

                                    return issuerContainsMdm && hasClientAuth;
                                })
                                .OrderByDescending(c => c.NotAfter) // Prefer certs with longer validity
                                .ToList();

                            if (mdmCerts.Any())
                            {
                                var cert = mdmCerts.First();
                                logger?.Info($"Found MDM certificate: Issuer={cert.Issuer}, Subject={cert.Subject}, Thumbprint={cert.Thumbprint}");
                                return cert;
                            }
                        }
                    }
                }

                logger?.Warning("No MDM certificate found");
                return null;
            }
            catch (Exception ex)
            {
                logger?.Error("Error finding MDM certificate", ex);
                return null;
            }
        }

        /// <summary>
        /// Validates that a certificate is still valid
        /// </summary>
        public static bool IsCertificateValid(X509Certificate2 certificate)
        {
            if (certificate == null)
                return false;

            var now = DateTime.UtcNow;
            return now >= certificate.NotBefore && now <= certificate.NotAfter;
        }
    }
}
