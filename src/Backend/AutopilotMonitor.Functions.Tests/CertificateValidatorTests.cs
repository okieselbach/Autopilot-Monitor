using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for CertificateValidator chain-trust enforcement.
///
/// The validator pins trust to embedded Intune root certs via X509ChainTrustMode.CustomRootTrust.
/// Two layers of coverage:
///   - Happy path: a real Intune-issued device cert (public-key only, copied from a test VM)
///     must validate successfully. This guards against rollouts where the embedded root/intermediate
///     bundle drifts from the chain Microsoft is actually issuing.
///   - Negative path: self-signed leaf certs (with or without Intune-shaped DNs) must NOT validate.
/// </summary>
public class CertificateValidatorTests
{
    private const string DeviceSamplePem = "device-cert-sample.pem";

    [Fact]
    public void ValidateCertificate_WithRealIntuneDeviceCert_ReturnsValid()
    {
        // Real device cert (public-key only) issued by "Microsoft Intune MDM Device CA".
        // This is the regression test that would have caught the previous outage where the
        // embedded root bundle didn't match the chain Microsoft uses for current devices.
        var pemPath = ResolveSamplePath();
        Assert.True(File.Exists(pemPath), $"Sample cert not found at {pemPath}");

        var pem = File.ReadAllText(pemPath);
        using var cert = X509Certificate2.CreateFromPem(pem);
        var b64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.True(result.IsValid, $"Real device cert rejected: {result.ErrorMessage}");
        Assert.False(string.IsNullOrEmpty(result.Thumbprint));
    }

    [Fact]
    public void ValidateCertificate_WithEmptyBase64_ReturnsInvalid()
    {
        var result = CertificateValidator.ValidateCertificate("");

        Assert.False(result.IsValid);
        Assert.Contains("No certificate", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCertificate_WithNull_ReturnsInvalid()
    {
        var result = CertificateValidator.ValidateCertificate(null);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCertificate_WithMalformedBase64_ReturnsInvalid()
    {
        var result = CertificateValidator.ValidateCertificate("not-base64!!!");

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCertificate_WithSelfSignedCertImpersonatingIntune_ReturnsInvalid()
    {
        // Regression test: a self-signed leaf with Subject CN="Microsoft Intune MDM Device CA"
        // and Client-Auth EKU previously passed validation under
        // AllowUnknownCertificateAuthority + DN substring match. Under CustomRootTrust the
        // chain has no signature link to a pinned Intune root and must be rejected.
        var b64 = CreateSelfSignedCertBase64(
            subjectCn: "Microsoft Intune MDM Device CA",
            includeClientAuthEku: true);

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.False(result.IsValid);
        Assert.Contains("chain validation failed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateCertificate_WithSelfSignedCertImpersonatingMdmDeviceCa_ReturnsInvalid()
    {
        var b64 = CreateSelfSignedCertBase64(
            subjectCn: "MDM Device CA",
            includeClientAuthEku: true);

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCertificate_WithSelfSignedNonIntuneCert_ReturnsInvalid()
    {
        var b64 = CreateSelfSignedCertBase64(
            subjectCn: "test-device.contoso.example",
            includeClientAuthEku: true);

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidateCertificate_WithExpiredSelfSignedCert_ReturnsInvalid()
    {
        var b64 = CreateSelfSignedCertBase64(
            subjectCn: "Microsoft Intune MDM Device CA",
            includeClientAuthEku: true,
            notBefore: DateTimeOffset.UtcNow.AddYears(-2),
            notAfter: DateTimeOffset.UtcNow.AddYears(-1));

        var result = CertificateValidator.ValidateCertificate(b64);

        Assert.False(result.IsValid);
        Assert.Contains("expired", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveSamplePath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(CertificateValidatorTests).Assembly.Location)!;
        return Path.Combine(assemblyDir, DeviceSamplePem);
    }

    private static string CreateSelfSignedCertBase64(
        string subjectCn,
        bool includeClientAuthEku,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            $"CN={subjectCn}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (includeClientAuthEku)
        {
            // 1.3.6.1.5.5.7.3.2 = Client Authentication
            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
                    critical: false));
        }

        var nb = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        var na = notAfter ?? DateTimeOffset.UtcNow.AddYears(1);
        using var cert = req.CreateSelfSigned(nb, na);

        return Convert.ToBase64String(cert.Export(X509ContentType.Cert));
    }
}
