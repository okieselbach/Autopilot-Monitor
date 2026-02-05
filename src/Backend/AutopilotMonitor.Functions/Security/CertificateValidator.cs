using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates client certificates for device authentication
    /// </summary>
    public static class CertificateValidator
    {
        /// <summary>
        /// Validates a client certificate from HTTP request header
        /// </summary>
        /// <param name="certificateBase64">Base64-encoded certificate from X-Client-Certificate header</param>
        /// <param name="logger">Logger for diagnostic output</param>
        /// <returns>Validation result with certificate details</returns>
        public static CertificateValidationResult ValidateCertificate(string? certificateBase64, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(certificateBase64))
            {
                return new CertificateValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "No certificate provided"
                };
            }

            try
            {
                // Parse certificate from Base64
                var certBytes = Convert.FromBase64String(certificateBase64);
                var certificate = new X509Certificate2(certBytes);

                logger?.LogDebug($"Validating certificate: Subject={certificate.Subject}, Issuer={certificate.Issuer}, Thumbprint={certificate.Thumbprint}");

                // 1. Check expiry
                var now = DateTime.UtcNow;
                if (now < certificate.NotBefore || now > certificate.NotAfter)
                {
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Certificate expired or not yet valid (NotBefore={certificate.NotBefore}, NotAfter={certificate.NotAfter})",
                        Thumbprint = certificate.Thumbprint
                    };
                }

                // 2. Check issuer (Microsoft Intune MDM Device CA or similar)
                var issuerValid = certificate.Issuer.Contains("Microsoft Intune", StringComparison.OrdinalIgnoreCase) ||
                                 certificate.Issuer.Contains("MDM Device CA", StringComparison.OrdinalIgnoreCase) ||
                                 certificate.Issuer.Contains("Microsoft Device CA", StringComparison.OrdinalIgnoreCase);

                if (!issuerValid)
                {
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Invalid certificate issuer: {certificate.Issuer}",
                        Thumbprint = certificate.Thumbprint
                    };
                }

                // 3. Check Enhanced Key Usage for Client Authentication (1.3.6.1.5.5.7.3.2)
                var hasClientAuth = certificate.Extensions
                    .OfType<X509EnhancedKeyUsageExtension>()
                    .Any(ext => ext.EnhancedKeyUsages
                        .OfType<Oid>()
                        .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"));

                if (!hasClientAuth)
                {
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Certificate does not have Client Authentication EKU",
                        Thumbprint = certificate.Thumbprint
                    };
                }

                // All checks passed
                logger?.LogInformation($"Certificate validated successfully: Thumbprint={certificate.Thumbprint}");
                return new CertificateValidationResult
                {
                    IsValid = true,
                    Thumbprint = certificate.Thumbprint,
                    Subject = certificate.Subject,
                    Issuer = certificate.Issuer
                };
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Error validating certificate");
                return new CertificateValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Certificate validation error: {ex.Message}"
                };
            }
        }
    }

    /// <summary>
    /// Result of certificate validation
    /// </summary>
    public class CertificateValidationResult
    {
        /// <summary>
        /// Whether the certificate is valid
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// Certificate thumbprint (for logging and rate limiting)
        /// </summary>
        public string? Thumbprint { get; set; }

        /// <summary>
        /// Certificate subject
        /// </summary>
        public string? Subject { get; set; }

        /// <summary>
        /// Certificate issuer
        /// </summary>
        public string? Issuer { get; set; }

        /// <summary>
        /// Error message if validation failed
        /// </summary>
        public string? ErrorMessage { get; set; }
    }
}
