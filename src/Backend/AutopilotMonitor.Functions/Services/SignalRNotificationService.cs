using System;
using System.Threading.Tasks;
using Microsoft.Azure.SignalR.Management;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Programmatic SignalR message sender for background tasks that run outside
    /// of Azure Function output bindings (e.g., async rule engine, vulnerability correlation).
    /// Uses the Azure SignalR Management SDK to send messages to connected clients.
    /// </summary>
    public class SignalRNotificationService : IDisposable
    {
        private readonly ILogger<SignalRNotificationService> _logger;
        private readonly ServiceManager _serviceManager;
        private IServiceHubContext? _hubContext;
        private static readonly SemaphoreSlim _initLock = new(1, 1);

        private const string HubName = "autopilotmonitor";

        public SignalRNotificationService(IConfiguration configuration, ILogger<SignalRNotificationService> logger)
        {
            _logger = logger;
            var connectionString = configuration["AzureSignalRConnectionString"]
                ?? throw new InvalidOperationException("AzureSignalRConnectionString is not configured");

            _serviceManager = new ServiceManagerBuilder()
                .WithOptions(o =>
                {
                    o.ConnectionString = connectionString;
                    o.ServiceTransportType = ServiceTransportType.Persistent;
                })
                .BuildServiceManager();
        }

        /// <summary>
        /// Notify a session's connected clients that new rule analysis results are available.
        /// Sends to the session-specific SignalR group so only the relevant session detail page reacts.
        /// </summary>
        public async Task NotifyRuleResultsAvailableAsync(string tenantId, string sessionId, int resultCount)
        {
            try
            {
                var hub = await GetHubContextAsync();
                var groupName = $"session-{tenantId}-{sessionId}";

                await hub.Clients.Group(groupName).SendCoreAsync("ruleResultsReady", new object[]
                {
                    new
                    {
                        sessionId,
                        tenantId,
                        ruleResultCount = resultCount,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                });

                _logger.LogDebug("Sent ruleResultsReady to group {Group} ({Count} results)", groupName, resultCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send ruleResultsReady notification for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Notify a session's connected clients that vulnerability correlation results are available.
        /// </summary>
        public async Task NotifyVulnerabilityReportAvailableAsync(string tenantId, string sessionId, string overallRisk)
        {
            try
            {
                var hub = await GetHubContextAsync();
                var groupName = $"session-{tenantId}-{sessionId}";

                await hub.Clients.Group(groupName).SendCoreAsync("vulnerabilityReportReady", new object[]
                {
                    new
                    {
                        sessionId,
                        tenantId,
                        overallRisk,
                        timestamp = DateTime.UtcNow.ToString("o")
                    }
                });

                _logger.LogDebug("Sent vulnerabilityReportReady to group {Group} (risk={Risk})", groupName, overallRisk);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send vulnerabilityReportReady notification for session {SessionId}", sessionId);
            }
        }

        private async Task<IServiceHubContext> GetHubContextAsync()
        {
            if (_hubContext != null) return _hubContext;

            await _initLock.WaitAsync();
            try
            {
                _hubContext ??= await _serviceManager.CreateHubContextAsync(HubName, default);
                return _hubContext;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public void Dispose()
        {
            (_hubContext as IDisposable)?.Dispose();
            _serviceManager?.Dispose();
        }
    }
}
