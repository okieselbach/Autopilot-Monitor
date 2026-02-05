using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Validates client certificates for device authentication with full chain validation
    /// </summary>
    /// <remarks>
    /// Security measures:
    /// - Full X.509 chain validation using X509Chain.Build()
    /// - No revocation checking (Intune certificates have no CRL/OCSP endpoints)
    /// - Validates entire chain contains Microsoft Intune CA
    /// - Prevents self-signed certificate attacks
    /// - Validates Enhanced Key Usage for Client Authentication
    /// - In-memory cache for validated certificates (5 minute TTL)
    /// </remarks>
    public static class CertificateValidator
    {
        private static readonly ConcurrentDictionary<string, CachedValidationResult> _validationCache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private class CachedValidationResult
        {
            public CertificateValidationResult Result { get; set; } = null!;
            public DateTime ExpiresAt { get; set; }
        }
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
                var thumbprint = certificate.Thumbprint;

                logger?.LogDebug($"Validating certificate: Subject={certificate.Subject}, Issuer={certificate.Issuer}, Thumbprint={thumbprint}");

                // Check cache first
                if (_validationCache.TryGetValue(thumbprint, out var cached))
                {
                    if (cached.ExpiresAt > DateTime.UtcNow)
                    {
                        logger?.LogDebug($"Certificate validation cache HIT for thumbprint {thumbprint}");
                        return cached.Result;
                    }
                    else
                    {
                        // Remove expired entry
                        _validationCache.TryRemove(thumbprint, out _);
                        logger?.LogDebug($"Certificate validation cache EXPIRED for thumbprint {thumbprint}");
                    }
                }

                logger?.LogDebug($"Certificate validation cache MISS for thumbprint {thumbprint}, performing full validation");

                // 1. Check expiry
                var now = DateTime.UtcNow;
                if (now < certificate.NotBefore || now > certificate.NotAfter)
                {
                    var result = new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Certificate expired or not yet valid (NotBefore={certificate.NotBefore}, NotAfter={certificate.NotAfter})",
                        Thumbprint = thumbprint
                    };

                    // Don't cache failures
                    return result;
                }

                // 2. Validate certificate chain (without revocation check - Intune certs have no CRL/OCSP)
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);

                var chainValid = chain.Build(certificate);

                if (!chainValid)
                {
                    var chainErrors = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation));
                    logger?.LogWarning($"Certificate chain validation failed: {chainErrors}");

                    var result = new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Certificate chain validation failed: {chainErrors}",
                        Thumbprint = thumbprint
                    };

                    // Don't cache failures
                    return result;
                }

                // 3. Verify issuer chain contains Microsoft Intune CA
                var hasValidIssuer = chain.ChainElements
                    .Cast<X509ChainElement>()
                    .Any(element =>
                        element.Certificate.Issuer.Contains("Microsoft Intune", StringComparison.OrdinalIgnoreCase) ||
                        element.Certificate.Issuer.Contains("MDM Device CA", StringComparison.OrdinalIgnoreCase) ||
                        element.Certificate.Subject.Contains("Microsoft Intune", StringComparison.OrdinalIgnoreCase) ||
                        element.Certificate.Subject.Contains("MDM Device CA", StringComparison.OrdinalIgnoreCase));

                if (!hasValidIssuer)
                {
                    var chainInfo = string.Join(" -> ", chain.ChainElements.Cast<X509ChainElement>().Select(e => e.Certificate.Subject));
                    logger?.LogWarning($"Certificate chain does not contain Microsoft Intune CA: {chainInfo}");

                    var result = new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Certificate not issued by Microsoft Intune CA",
                        Thumbprint = thumbprint
                    };

                    // Don't cache failures
                    return result;
                }

                // 4. Check Enhanced Key Usage for Client Authentication (1.3.6.1.5.5.7.3.2)
                var hasClientAuth = certificate.Extensions
                    .OfType<X509EnhancedKeyUsageExtension>()
                    .Any(ext => ext.EnhancedKeyUsages
                        .OfType<Oid>()
                        .Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2"));

                if (!hasClientAuth)
                {
                    var result = new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Certificate does not have Client Authentication EKU",
                        Thumbprint = thumbprint
                    };

                    // Don't cache failures
                    return result;
                }

                // All checks passed - create and cache success result
                logger?.LogInformation($"Certificate validated successfully: Thumbprint={thumbprint}");
                var successResult = new CertificateValidationResult
                {
                    IsValid = true,
                    Thumbprint = thumbprint,
                    Subject = certificate.Subject,
                    Issuer = certificate.Issuer
                };

                // Cache successful validation
                _validationCache[thumbprint] = new CachedValidationResult
                {
                    Result = successResult,
                    ExpiresAt = DateTime.UtcNow.Add(CacheDuration)
                };

                logger?.LogDebug($"Certificate validation cached for thumbprint {thumbprint}, expires at {DateTime.UtcNow.Add(CacheDuration):u}");

                return successResult;
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
