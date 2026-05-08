using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for CertificateValidator chain-trust enforcement.
///
/// The validator pins trust to embedded Intune root certs via X509ChainTrustMode.CustomRootTrust.
/// A self-signed leaf cert whose Subject contains "Microsoft Intune" must NOT validate -
/// guards against the prior bypass where AllowUnknownCertificateAuthority + DN-substring
/// matching let any forged self-signed cert pass.
/// </summary>
public class CertificateValidatorTests
{
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
        // Second variant of the Subject-substring bypass.
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
