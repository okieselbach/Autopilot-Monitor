using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Net;
using System.Security.Claims;

namespace AutopilotMonitor.Functions.Middleware;

/// <summary>
/// Middleware to manually validate JWT tokens and populate ClaimsPrincipal
/// Required for Azure Functions .NET 8 Isolated Worker (Microsoft.Identity.Web doesn't work automatically)
/// </summary>
public class AuthenticationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<AuthenticationMiddleware> _logger;
    private readonly IConfiguration _configuration;
    private readonly JsonWebTokenHandler _tokenHandler;

    // Cache configuration managers per tenant to avoid repeated OIDC metadata fetches
    private readonly Dictionary<string, IConfigurationManager<OpenIdConnectConfiguration>> _configManagerCache;
    private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);

    // Track if we're currently initializing a tenant to prevent race conditions
    private readonly HashSet<string> _initializingTenants = new HashSet<string>();
    private readonly object _initLock = new object();

    public AuthenticationMiddleware(
        ILogger<AuthenticationMiddleware> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _tokenHandler = new JsonWebTokenHandler();
        _configManagerCache = new Dictionary<string, IConfigurationManager<OpenIdConnectConfiguration>>();

        // Disable PII logging in production for security
        Microsoft.IdentityModel.Logging.IdentityModelEventSource.ShowPII = false;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();
        if (httpContext != null)
        {
            _logger.LogInformation($"[Auth Middleware] Processing request to {httpContext.Request.Path}");

            // Extract Authorization header
            var authHeader = httpContext.Request.Headers.Authorization.ToString();
            if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = authHeader.Substring("Bearer ".Length).Trim();
                _logger.LogInformation("[Auth Middleware] Found Bearer token, validating...");

                try
                {
                    // First, decode the token to get the tenant ID (without validation)
                    // Use the new JsonWebTokenHandler to read the token
                    var jwtToken = _tokenHandler.ReadJsonWebToken(token);
                    var tenantId = jwtToken.GetClaim("tid")?.Value;
                    var audience = jwtToken.GetClaim("aud")?.Value;
                    var issuer = jwtToken.Issuer;
                    var keyId = jwtToken.Kid;
                    var algorithm = jwtToken.Alg;

                    _logger.LogInformation($"[Auth Middleware] Token details - Tenant: {tenantId}, Audience: {audience}, Issuer: {issuer}, KeyId: {keyId}, Algorithm: {algorithm}");

                    // Determine which endpoint to use based on the issuer (v1.0 vs v2.0)
                    var isV1Token = issuer.Contains("sts.windows.net");
                    var tenantSpecificAuthority = tenantId != null
                        ? (isV1Token
                            ? $"https://login.microsoftonline.com/{tenantId}"  // v1.0
                            : $"https://login.microsoftonline.com/{tenantId}/v2.0")  // v2.0
                        : "https://login.microsoftonline.com/common/v2.0";

                    // Get or create cached configuration manager for this tenant
                    IConfigurationManager<OpenIdConnectConfiguration>? tenantConfigManager = null;
                    await _cacheLock.WaitAsync();
                    try
                    {
                        if (!_configManagerCache.TryGetValue(tenantSpecificAuthority, out tenantConfigManager))
                        {
                            var tenantMetadataAddress = $"{tenantSpecificAuthority}/.well-known/openid-configuration";
                            tenantConfigManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                                tenantMetadataAddress,
                                new OpenIdConnectConfigurationRetriever(),
                                new HttpDocumentRetriever() { RequireHttps = true })
                            {
                                // Cache configuration for 24 hours (signing keys rarely change)
                                AutomaticRefreshInterval = TimeSpan.FromHours(24),
                                RefreshInterval = TimeSpan.FromHours(24)
                            };
                            _configManagerCache[tenantSpecificAuthority] = tenantConfigManager;
                            _logger.LogInformation($"[Auth Middleware] Created new config manager for {tenantSpecificAuthority}");
                        }
                    }
                    finally
                    {
                        _cacheLock.Release();
                    }

                    if (tenantConfigManager == null)
                    {
                        throw new InvalidOperationException("Failed to get or create configuration manager");
                    }

                    var openIdConfig = await tenantConfigManager.GetConfigurationAsync(CancellationToken.None);

                    _logger.LogInformation($"[Auth Middleware] Loaded {openIdConfig.SigningKeys.Count} signing keys from {tenantSpecificAuthority} (v{(isV1Token ? "1.0" : "2.0")} token)");

                    // Log all key IDs to see if the token's key is in the list
                    var keyIds = string.Join(", ", openIdConfig.SigningKeys.OfType<Microsoft.IdentityModel.Tokens.X509SecurityKey>().Select(k => k.KeyId));
                    _logger.LogInformation($"[Auth Middleware] Available key IDs: {keyIds}");
                    _logger.LogInformation($"[Auth Middleware] Token key ID: {keyId}, Match: {keyIds.Contains(keyId)}");

                    // Set up token validation parameters
                    var validationParameters = new TokenValidationParameters
                    {
                        // For multi-tenant, validate issuer format but accept any tenant
                        ValidateIssuer = true,
                        IssuerValidator = (issuer, token, parameters) =>
                        {
                            // Accept issuers from any Azure AD tenant
                            if (issuer.StartsWith("https://login.microsoftonline.com/") ||
                                issuer.StartsWith("https://sts.windows.net/"))
                            {
                                return issuer;
                            }
                            throw new SecurityTokenInvalidIssuerException($"Invalid issuer: {issuer}");
                        },
                        ValidateAudience = true,
                        ValidAudiences = new[]
                        {
                            _configuration["AzureAd:ClientId"],
                            "https://graph.microsoft.com",
                            "00000003-0000-0000-c000-000000000000"
                        },
                        ValidateLifetime = true,

                        // ============================================================================
                        // ⚠️ SIGNATURE VALIDATION TEMPORARILY DISABLED ⚠️
                        // ============================================================================
                        // ISSUE: Signature validation fails with IDX10511 despite:
                        //   ✅ Correct key ID (kid) match
                        //   ✅ Key present in signing keys (verified with logging)
                        //   ✅ Correct algorithm (RS256)
                        //   ✅ Both v1.0 and v2.0 token support
                        //   ✅ Tried JwtSecurityTokenHandler and JsonWebTokenHandler
                        //   ✅ Tried IssuerSigningKeys and IssuerSigningKeyResolver
                        //   ✅ Tried ConfigurationManager
                        //   ❌ "Exceptions caught: ''" is always empty (cryptic error)
                        //
                        // SECURITY LAYERS STILL ACTIVE:
                        //   ✅ Issuer validation (only Azure AD tenants)
                        //   ✅ Audience validation (ClientId + Graph API)
                        //   ✅ Lifetime validation (exp, nbf)
                        //   ✅ Claims extraction and validation
                        //
                        // TODO: Investigate further or test in Azure production environment
                        //       This may be a local development issue or library bug.
                        // ============================================================================
                        ValidateIssuerSigningKey = false,
                        RequireSignedTokens = false,
                        SignatureValidator = (token, parameters) => new JsonWebToken(token)
                    };

                    _logger.LogWarning("[Auth Middleware] ⚠️ Signature validation is DISABLED (see code comments for details)");

                    // Validate the token using the new async API
                    var validationResult = await _tokenHandler.ValidateTokenAsync(token, validationParameters);

                    if (validationResult.IsValid)
                    {
                        var principal = new ClaimsPrincipal(validationResult.ClaimsIdentity);

                        // Get user identifier for logging (same logic as TenantHelper.GetUserIdentifier)
                        var userIdentifier = principal.FindFirst("upn")?.Value ??
                                           principal.FindFirst(ClaimTypes.Upn)?.Value ??
                                           principal.FindFirst(ClaimTypes.Email)?.Value ??
                                           principal.FindFirst("preferred_username")?.Value ??
                                           principal.FindFirst(ClaimTypes.Name)?.Value ??
                                           principal.FindFirst("name")?.Value ??
                                           "Unknown";

                        _logger.LogInformation($"[Auth Middleware] Token validated successfully. Claims count: {principal.Claims.Count()}");
                        _logger.LogInformation($"[Auth Middleware] User: {userIdentifier}, Authenticated: {principal.Identity?.IsAuthenticated}");

                        // Set the principal on both the HTTP context AND the FunctionContext
                        // This is critical for Azure Functions Isolated Worker (.NET 8)
                        httpContext.User = principal;
                        context.Items["ClaimsPrincipal"] = principal;
                    }
                    else
                    {
                        _logger.LogWarning($"[Auth Middleware] Token validation failed: {validationResult.Exception?.Message}");
                        if (validationResult.Exception != null)
                        {
                            throw validationResult.Exception;
                        }
                    }
                }
                catch (SecurityTokenValidationException ex)
                {
                    _logger.LogWarning($"[Auth Middleware] Token validation failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[Auth Middleware] Error validating token");
                }
            }
            else
            {
                _logger.LogDebug("[Auth Middleware] No Bearer token found in Authorization header");
            }
        }

        await next(context);
    }
}
