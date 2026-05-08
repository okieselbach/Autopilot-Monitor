using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// - X.509 chain trust pinned to embedded Intune root certificate(s) via
    ///   X509ChainTrustMode.CustomRootTrust (OS trust store is ignored)
    /// - No revocation checking (Intune certificates have no CRL/OCSP endpoints)
    /// - Prevents self-signed certificate attacks
    /// - Validates Enhanced Key Usage for Client Authentication
    /// - In-memory cache for validated certificates (5 minute TTL)
    /// - Intune CA certificates embedded as resources (required on Linux/Azure Functions flex plan
    ///   where the OS trust store does not contain Microsoft Intune CAs)
    /// </remarks>
    public static class CertificateValidator
    {
        private static readonly ConcurrentDictionary<string, CachedValidationResult> _validationCache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        // Lazily loaded Intune CA certs from embedded PEM resources, split by role:
        //   roots         -> pinned trust anchors  (chain.ChainPolicy.CustomTrustStore)
        //   intermediates -> bridge candidates    (chain.ChainPolicy.ExtraStore)
        private static readonly Lazy<X509Certificate2[]> _intuneRootCerts =
            new(() => LoadIntunePemBundle("intune-root-ca.pem"));
        private static readonly Lazy<X509Certificate2[]> _intuneIntermediateCerts =
            new(() => LoadIntunePemBundle("intune-intermediate-ca.pem"));

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
                // Azure App Service may URL-encode the X-ARR-ClientCert header value.
                // Decode before Base64 parsing.
                if (certificateBase64.Contains('%'))
                {
                    certificateBase64 = Uri.UnescapeDataString(certificateBase64);
                }

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
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Certificate expired or not yet valid (NotBefore={certificate.NotBefore}, NotAfter={certificate.NotAfter})",
                        Thumbprint = thumbprint
                    };
                }

                // 2. Validate certificate chain against pinned Intune roots (no revocation - no CRL/OCSP).
                //
                // CustomTrustStore + CustomRootTrust pins trust to the embedded Intune root cert(s)
                // only and ignores the OS trust store entirely - works on Linux/Flex Consumption
                // (no machine store) and replaces the previous AllowUnknownCertificateAuthority
                // workaround, which was bypassable by self-signed leaf certs whose Subject contained
                // "Microsoft Intune".
                var roots = _intuneRootCerts.Value;
                var intermediates = _intuneIntermediateCerts.Value;

                if (roots.Length == 0)
                {
                    logger?.LogError("No embedded Intune root certificates loaded - failing closed");
                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Server misconfiguration: no Intune trust anchors available",
                        Thumbprint = thumbprint
                    };
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.VerificationTime = DateTime.UtcNow;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;

                foreach (var root in roots)
                    chain.ChainPolicy.CustomTrustStore.Add(root);
                foreach (var intermediate in intermediates)
                    chain.ChainPolicy.ExtraStore.Add(intermediate);

                logger?.LogDebug(
                    "Chain build with {RootCount} pinned Intune root(s) + {IntermediateCount} intermediate(s)",
                    roots.Length, intermediates.Length);

                var chainValid = chain.Build(certificate);

                if (!chainValid)
                {
                    var chainErrors = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation));
                    logger?.LogWarning("Certificate chain validation failed: {ChainErrors}", chainErrors);

                    return new CertificateValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Certificate chain validation failed: {chainErrors}",
                        Thumbprint = thumbprint
                    };
                }

                // chainValid == true under CustomRootTrust proves the leaf is transitively signed
                // by one of the pinned Intune roots. The previous Subject/Issuer DN substring check
                // is intentionally removed - it added no security on top of root pinning and would
                // false-negative if Microsoft renames the CA.

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
                        Thumbprint = thumbprint
                    };
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

        /// <summary>
        /// Loads one or more X.509 certs from a PEM-bundle embedded resource under
        /// Security/Certificates/. Roles are filename-segregated:
        ///   intune-root-ca.pem         -> pinned trust anchors (CustomTrustStore)
        ///   intune-intermediate-ca.pem -> chain bridge candidates (ExtraStore)
        /// </summary>
        private static X509Certificate2[] LoadIntunePemBundle(string fileName)
        {
            var resourceName = $"AutopilotMonitor.Functions.Security.Certificates.{fileName}";
            var assembly = Assembly.GetExecutingAssembly();

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return [];

            using var reader = new StreamReader(stream);
            var pem = reader.ReadToEnd();

            if (pem.Contains("PLACEHOLDER_REPLACE_WITH_BASE64"))
                return [];

            // PEM may contain multiple cert blocks (e.g. cross-signed roots); parse each.
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var certs = new List<X509Certificate2>();
            int idx = 0;
            while ((idx = pem.IndexOf(begin, idx, StringComparison.Ordinal)) >= 0)
            {
                var endIdx = pem.IndexOf(end, idx, StringComparison.Ordinal);
                if (endIdx < 0) break;
                var block = pem.Substring(idx, endIdx + end.Length - idx);
                try { certs.Add(X509Certificate2.CreateFromPem(block)); }
                catch { /* malformed block - skip; chain.Build() fails closed if no roots remain */ }
                idx = endIdx + end.Length;
            }
            return certs.ToArray();
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
