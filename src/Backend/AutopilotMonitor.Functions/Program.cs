using System.Text.Json;
using System.Text.Json.Serialization;
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

// Register authentication middleware in the pipeline (Azure Functions .NET 8 isolated worker pattern)
builder.UseMiddleware<AuthenticationMiddleware>();

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
        options.ClientId = builder.Configuration["AzureAd:ClientId"];

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
                builder.Configuration["AzureAd:ClientId"] // Our app's client ID
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
builder.Services.AddSingleton<TenantConfigurationService>();
builder.Services.AddSingleton<AdminConfigurationService>();
builder.Services.AddSingleton<RateLimitService>();
builder.Services.AddSingleton<UsageMetricsService>();
builder.Services.AddSingleton<GalacticAdminService>();
builder.Services.AddSingleton<PreviewWhitelistService>();
builder.Services.AddSingleton<TenantAdminsService>();
builder.Services.AddSingleton<HealthCheckService>();
builder.Services.AddSingleton<GatherRuleService>();
builder.Services.AddSingleton<AnalyzeRuleService>();
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.SerialNumberValidator>();

var app = builder.Build();

// Log CORS configuration at startup so misconfigured origins are immediately visible
// in the log stream. CORS is enforced by Azure infrastructure, not by function code,
// so a blocked preflight never reaches the function worker and leaves no trace.
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
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
