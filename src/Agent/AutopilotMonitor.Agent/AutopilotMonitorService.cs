using System;
using System.ServiceProcess;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring;
using AutopilotMonitor.Agent.Core.Storage;

namespace AutopilotMonitor.Agent
{
    /// <summary>
    /// Windows Service wrapper for the monitoring agent
    /// </summary>
    public class AutopilotMonitorService : ServiceBase
    {
        private MonitoringService _monitoringService;
        private AgentLogger _logger;

        public AutopilotMonitorService()
        {
            ServiceName = "AutopilotMonitor";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                var config = LoadConfiguration();
                var logDir = Environment.ExpandEnvironmentVariables(config.LogDirectory);
                _logger = new AgentLogger(logDir, config.EnableDebugLogging);

                _logger.Info("Windows Service starting");

                _monitoringService = new MonitoringService(config, _logger);
                _monitoringService.Start();

                _logger.Info("Windows Service started");
            }
            catch (Exception ex)
            {
                _logger?.Error("Failed to start service", ex);
                throw;
            }
        }

        protected override void OnStop()
        {
            try
            {
                _logger?.Info("Windows Service stopping");
                _monitoringService?.Stop();
                _monitoringService?.Dispose();
                _logger?.Info("Windows Service stopped");
            }
            catch (Exception ex)
            {
                _logger?.Error("Error stopping service", ex);
            }
        }

        private AgentConfiguration LoadConfiguration()
        {
            // Load or create persisted session ID
            var dataDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor");
            var sessionPersistence = new SessionPersistence(dataDirectory);
            var sessionId = sessionPersistence.LoadOrCreateSessionId();

            return new AgentConfiguration
            {
                ApiBaseUrl = Environment.GetEnvironmentVariable("AUTOPILOT_MONITOR_API") ?? "http://localhost:7071",
                SessionId = sessionId,
                TenantId = "default-tenant",
                SpoolDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Spool"),
                LogDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Logs"),
                UploadIntervalSeconds = 30,
                MaxBatchSize = 100,
                EnableDebugLogging = false
            };
        }
    }
}
