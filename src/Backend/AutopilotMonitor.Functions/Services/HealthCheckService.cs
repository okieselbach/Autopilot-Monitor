using System.Security.Cryptography.X509Certificates;
using AutopilotMonitor.Functions.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for performing comprehensive health checks
/// </summary>
public class HealthCheckService
{
    private readonly ILogger<HealthCheckService> _logger;
    private readonly TableStorageService _tableStorageService;
    private readonly IConfiguration _configuration;

    public HealthCheckService(
        ILogger<HealthCheckService> logger,
        TableStorageService tableStorageService,
        IConfiguration configuration)
    {
        _logger = logger;
        _tableStorageService = tableStorageService;
        _configuration = configuration;
    }

    /// <summary>
    /// Performs all health checks and returns the results
    /// </summary>
    public async Task<HealthCheckResult> PerformAllChecksAsync()
    {
        var result = new HealthCheckResult
        {
            Timestamp = DateTime.UtcNow,
            Checks = new List<HealthCheck>()
        };

        // Perform all checks in parallel
        var checks = new List<Task<HealthCheck>>
        {
            CheckTableStorageAsync(),
            CheckConfigurationAsync(),
            CheckAuthenticationAsync(),
            CheckSignalRAsync(),
            CheckCertificateValidationAsync()
        };

        var completedChecks = await Task.WhenAll(checks);
        result.Checks.AddRange(completedChecks);

        // Overall status is unhealthy if any check is unhealthy
        result.OverallStatus = result.Checks.All(c => c.Status == "healthy") ? "healthy" : "unhealthy";

        return result;
    }

    /// <summary>
    /// Checks Azure Table Storage connectivity
    /// </summary>
    private async Task<HealthCheck> CheckTableStorageAsync()
    {
        var check = new HealthCheck
        {
            Name = "Table Storage",
            Description = "Azure Table Storage connectivity"
        };

        try
        {
            var startTime = DateTime.UtcNow;

            // Try to get sessions to verify connectivity (use tenant ID as test)
            // This is a simple check that verifies the service can query the database
            var testTenantId = "health-check-test";
            await _tableStorageService.GetSessionsAsync(testTenantId);

            var responseTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

            check.Status = "healthy";
            check.Message = "Table Storage connectivity verified";
            check.Details = new Dictionary<string, object>
            {
                { "Service", "TableStorageService" },
                { "ResponseTime", $"{responseTime:F2}ms" }
            };
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"Table Storage check failed: {ex.Message}";
            _logger.LogError(ex, "Table Storage health check failed");
        }

        return check;
    }

    /// <summary>
    /// Checks that all required configuration settings are present
    /// </summary>
    private Task<HealthCheck> CheckConfigurationAsync()
    {
        var check = new HealthCheck
        {
            Name = "Configuration",
            Description = "Required configuration settings"
        };

        var requiredSettings = new[]
        {
            "AzureWebJobsStorage",
            "AzureAd:ClientId",
            "AzureAd:TenantId",
            "AzureSignalR:ConnectionString"
        };

        var missingSettings = new List<string>();
        var presentSettings = new List<string>();

        foreach (var setting in requiredSettings)
        {
            var value = _configuration[setting];
            if (string.IsNullOrEmpty(value))
            {
                missingSettings.Add(setting);
            }
            else
            {
                presentSettings.Add(setting);
            }
        }

        if (missingSettings.Count == 0)
        {
            check.Status = "healthy";
            check.Message = $"All {requiredSettings.Length} required settings present";
            check.Details = new Dictionary<string, object>
            {
                { "RequiredSettings", requiredSettings.Length },
                { "PresentSettings", presentSettings }
            };
        }
        else
        {
            check.Status = "unhealthy";
            check.Message = $"{missingSettings.Count} required settings missing";
            check.Details = new Dictionary<string, object>
            {
                { "MissingSettings", missingSettings },
                { "PresentSettings", presentSettings }
            };
        }

        return Task.FromResult(check);
    }

    /// <summary>
    /// Checks authentication configuration
    /// </summary>
    private Task<HealthCheck> CheckAuthenticationAsync()
    {
        var check = new HealthCheck
        {
            Name = "Authentication",
            Description = "Azure AD / MSAL configuration"
        };

        try
        {
            var clientId = _configuration["AzureAd:ClientId"];
            var tenantId = _configuration["AzureAd:TenantId"];

            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId))
            {
                check.Status = "unhealthy";
                check.Message = "Azure AD configuration incomplete";
                check.Details = new Dictionary<string, object>
                {
                    { "ClientIdPresent", !string.IsNullOrEmpty(clientId) },
                    { "TenantIdPresent", !string.IsNullOrEmpty(tenantId) }
                };
            }
            else
            {
                check.Status = "healthy";
                check.Message = "Azure AD configuration present";
                check.Details = new Dictionary<string, object>
                {
                    { "ClientId", clientId.Substring(0, Math.Min(8, clientId.Length)) + "..." },
                    { "TenantId", tenantId.Substring(0, Math.Min(8, tenantId.Length)) + "..." }
                };
            }
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"Authentication check failed: {ex.Message}";
            _logger.LogError(ex, "Authentication health check failed");
        }

        return Task.FromResult(check);
    }

    /// <summary>
    /// Checks SignalR service configuration
    /// </summary>
    private Task<HealthCheck> CheckSignalRAsync()
    {
        var check = new HealthCheck
        {
            Name = "SignalR",
            Description = "Azure SignalR Service configuration"
        };

        try
        {
            var connectionString = _configuration["AzureSignalR:ConnectionString"];

            if (string.IsNullOrEmpty(connectionString))
            {
                check.Status = "unhealthy";
                check.Message = "SignalR connection string not configured";
            }
            else
            {
                // Parse connection string to extract endpoint
                var endpointMatch = System.Text.RegularExpressions.Regex.Match(
                    connectionString,
                    @"Endpoint=([^;]+)"
                );

                check.Status = "healthy";
                check.Message = "SignalR configuration present";
                check.Details = new Dictionary<string, object>
                {
                    { "Endpoint", endpointMatch.Success ? endpointMatch.Groups[1].Value : "configured" }
                };
            }
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"SignalR check failed: {ex.Message}";
            _logger.LogError(ex, "SignalR health check failed");
        }

        return Task.FromResult(check);
    }

    /// <summary>
    /// Checks certificate validation with test self-signed certificate
    /// Demonstrates that self-signed certificates are properly rejected
    /// </summary>
    private Task<HealthCheck> CheckCertificateValidationAsync()
    {
        var check = new HealthCheck
        {
            Name = "Certificate Validation",
            Description = "X.509 chain validation and security checks"
        };

        try
        {
            // Create a test self-signed certificate that mimics an attack
            using var testCert = CreateTestSelfSignedCertificate();
            var testCertBase64 = Convert.ToBase64String(testCert.Export(X509ContentType.Cert));

            // Validate the test certificate - should FAIL
            var validationResult = CertificateValidator.ValidateCertificate(testCertBase64, _logger);

            if (!validationResult.IsValid)
            {
                // Expected: Self-signed certificate should be rejected
                check.Status = "healthy";
                check.Message = "Certificate validation properly rejects self-signed certificates";
                check.Details = new Dictionary<string, object>
                {
                    { "ChainValidation", "Enabled (X509Chain.Build)" },
                    { "RevocationCheck", "Online (OCSP/CRL)" },
                    { "TestResult", "Self-signed cert rejected ✓" },
                    { "RejectionReason", validationResult.ErrorMessage ?? "Unknown" },
                    { "SecurityLevel", "High - Chain validation active" }
                };
            }
            else
            {
                // Unexpected: Self-signed certificate should NOT pass
                check.Status = "unhealthy";
                check.Message = "WARNING: Certificate validation accepted self-signed certificate!";
                check.Details = new Dictionary<string, object>
                {
                    { "SecurityIssue", "Self-signed certificates are being accepted" },
                    { "Recommendation", "Review CertificateValidator implementation" },
                    { "TestCertThumbprint", validationResult.Thumbprint ?? "N/A" }
                };
                _logger.LogError("Certificate validation security check failed - self-signed cert was accepted!");
            }
        }
        catch (Exception ex)
        {
            check.Status = "warning";
            check.Message = $"Certificate validation check error: {ex.Message}";
            check.Details = new Dictionary<string, object>
            {
                { "Note", "Check may fail if certificate creation is not supported on this platform" }
            };
            _logger.LogWarning(ex, "Certificate validation health check encountered an error");
        }

        return Task.FromResult(check);
    }

    /// <summary>
    /// Creates a test self-signed certificate that mimics an attack scenario
    /// </summary>
    private X509Certificate2 CreateTestSelfSignedCertificate()
    {
        // Create a self-signed certificate with fake Microsoft Intune issuer
        // This simulates the attack described in the security audit
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var certRequest = new CertificateRequest(
            "CN=FakeDevice, O=AttackerOrg",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        // Add EKU for Client Authentication (like a real MDM cert)
        certRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new System.Security.Cryptography.OidCollection
                {
                    new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.2") // Client Authentication
                },
                false));

        // Create self-signed certificate with fake issuer name
        var cert = certRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        return cert;
    }
}

/// <summary>
/// Result of a health check operation
/// </summary>
public class HealthCheckResult
{
    public DateTime Timestamp { get; set; }
    public string OverallStatus { get; set; } = "unknown";
    public List<HealthCheck> Checks { get; set; } = new();
}

/// <summary>
/// Individual health check result
/// </summary>
public class HealthCheck
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object>? Details { get; set; }
}
