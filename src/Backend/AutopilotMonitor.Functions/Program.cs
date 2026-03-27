using System.Text.Json;
using System.Text.Json.Serialization;
using AutopilotMonitor.Functions.DataAccess;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Register middleware pipeline (Azure Functions .NET 8 isolated worker pattern)
// Order matters: API key auth → JWT authentication (401) → policy enforcement (403)
builder.UseMiddleware<ApiKeyMiddleware>();
builder.UseMiddleware<AuthenticationMiddleware>();
builder.UseMiddleware<PolicyEnforcementMiddleware>();

// Configure JSON serialization to use camelCase
builder.Services.Configure<JsonSerializerOptions>(options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    // Serialize enums as strings for better readability and frontend compatibility
    options.Converters.Add(new JsonStringEnumConverter());
});

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Configure JWT Authentication for Multi-Tenant Azure AD
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(options =>
    {
        // Configure JWT Bearer options if needed
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Auth");
                logger.LogWarning("Authentication failed: {Error}", context.Exception?.Message);
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Auth");
                var claims = context.Principal?.Claims;
                var tenantId = claims?.FirstOrDefault(c => c.Type == "tid")?.Value;
                logger.LogDebug("Token validated for tenant: {TenantId}", tenantId);
                return Task.CompletedTask;
            }
        };
    },
    options =>
    {
        // Multi-Tenant Configuration
        options.Instance = "https://login.microsoftonline.com/";
        options.TenantId = "organizations"; // Accept tokens from any Azure AD tenant
        options.ClientId = builder.Configuration["EntraId:ClientId"];

        // Token validation parameters
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuers = new[]
            {
                "https://login.microsoftonline.com/organizations/v2.0",
                "https://sts.windows.net/{tenantid}/"
            },
            // Temporarily accept Microsoft Graph tokens (used by frontend with User.Read scope)
            // TODO: Later expose custom API and use api://{clientId} scopes
            ValidAudiences = new[]
            {
                builder.Configuration["EntraId:ClientId"] // Our app's client ID
                //"https://graph.microsoft.com", // Microsoft Graph
                //"00000003-0000-0000-c000-000000000000" // Microsoft Graph App ID
            }
        };
    });

builder.Services.AddAuthorization();

// Enable ASP.NET Core integration for authentication
builder.Services.AddHttpContextAccessor();

// Register our services
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<TableStorageService>();
builder.Services.AddHostedService<TableInitializerService>(); // Initialize all tables at startup

// Data Access Layer — repository interfaces backed by Table Storage.
// To switch to Cosmos DB: replace AddTableStorageDataAccess() with AddCosmosDataAccess().
// To add event streaming: chain .AddEventStreaming<EventHubPublisher>() after this call.
builder.Services.AddTableStorageDataAccess();
builder.Services.AddSingleton<TenantConfigurationService>();
builder.Services.AddSingleton<AdminConfigurationService>();
builder.Services.AddSingleton<RateLimitService>();
builder.Services.AddSingleton<UsageMetricsService>();
builder.Services.AddSingleton<PlatformMetricsService>();
builder.Services.AddSingleton<GlobalAdminService>();
builder.Services.AddSingleton<PreviewWhitelistService>();
builder.Services.AddSingleton<TenantAdminsService>();
builder.Services.AddSingleton<HealthCheckService>();
builder.Services.AddSingleton<GatherRuleService>();
builder.Services.AddSingleton<AnalyzeRuleService>();
builder.Services.AddSingleton<ImeLogPatternService>();
builder.Services.AddHttpClient<GitHubRuleRepository>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<BlockedDeviceService>();
builder.Services.AddSingleton<BlockedVersionService>();
builder.Services.AddSingleton<SessionReportService>();
builder.Services.AddSingleton<BootstrapSessionService>();

// Programmatic SignalR push for background tasks (rule engine, vulnerability correlation)
builder.Services.AddSingleton<SignalRNotificationService>();

// Vulnerability correlation services
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Vulnerability.NvdApiClient>();
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Vulnerability.KevDataService>();
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Vulnerability.MsrcApiClient>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCorrelationService>();

// Register agent Function classes so bootstrap wrappers can inject them for code reuse
builder.Services.AddSingleton<IngestEventsFunction>();
builder.Services.AddSingleton<RegisterSessionFunction>();
builder.Services.AddSingleton<GetAgentConfigFunction>();
builder.Services.AddSingleton<ReportAgentErrorFunction>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.GraphTokenService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.AutopilotDeviceValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.CorporateIdentifierValidator>();
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Notifications.WebhookNotificationService>();
builder.Services.AddHttpClient<TelegramNotificationService>();
builder.Services.AddSingleton<ResendEmailService>();
builder.Services.AddSingleton<GlobalNotificationService>();

var app = builder.Build();

// Validate critical security configuration at startup
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var entraClientId = builder.Configuration["EntraId:ClientId"];
var entraClientSecret = builder.Configuration["EntraId:ClientSecret"];
if (string.IsNullOrEmpty(entraClientId))
    startupLogger.LogWarning("EntraId:ClientId is not configured — JWT audience validation and Graph API calls will fail");
if (string.IsNullOrEmpty(entraClientSecret))
    startupLogger.LogWarning("EntraId:ClientSecret is not configured — device validation via Graph API will fail at runtime");

// Log CORS configuration at startup so misconfigured origins are immediately visible
// in the log stream. CORS is enforced by Azure infrastructure, not by function code,
// so a blocked preflight never reaches the function worker and leaves no trace.
var corsOrigins = builder.Configuration["Host:CORS"]                   // local.settings.json
    ?? builder.Configuration["WEBSITE_CORS_ALLOWED_ORIGINS"]           // Azure App Settings
    ?? "(not configured - all cross-origin requests will be blocked!)";
var corsCredentials = builder.Configuration["Host:CORSCredentials"]
    ?? builder.Configuration["WEBSITE_CORS_SUPPORT_CREDENTIALS"]
    ?? "unknown";
startupLogger.LogInformation(
    "=== CORS CONFIG: AllowedOrigins={CorsOrigins} | SupportCredentials={CorsCredentials} ===",
    corsOrigins, corsCredentials);

app.Run();
