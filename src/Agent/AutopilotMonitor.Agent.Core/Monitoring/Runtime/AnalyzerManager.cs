using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Agent.Core.Monitoring.Analyzers;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Runtime
{
    /// <summary>
    /// Manages agent analyzers: initialization from remote config, startup execution
    /// on a background thread, and shutdown execution with optional WhiteGlove part.
    /// </summary>
    public class AnalyzerManager
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly RemoteConfigService _remoteConfigService;

        private readonly List<IAgentAnalyzer> _analyzers = new List<IAgentAnalyzer>();

        public AnalyzerManager(
            AgentConfiguration configuration,
            AgentLogger logger,
            Action<EnrollmentEvent> emitEvent,
            RemoteConfigService remoteConfigService)
        {
            _configuration = configuration;
            _logger = logger;
            _emitEvent = emitEvent;
            _remoteConfigService = remoteConfigService;
        }

        public void Initialize()
        {
            _analyzers.Clear();

            var analyzerConfig = _remoteConfigService?.CurrentConfig?.Analyzers ?? new AnalyzerConfiguration();

            if (analyzerConfig.EnableLocalAdminAnalyzer)
            {
                _analyzers.Add(new LocalAdminAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger,
                    analyzerConfig.LocalAdminAllowedAccounts
                ));
                _logger.Info("LocalAdminAnalyzer registered");
            }
            else
            {
                _logger.Info("LocalAdminAnalyzer disabled by remote config");
            }

            if (analyzerConfig.EnableSoftwareInventoryAnalyzer)
            {
                _analyzers.Add(new SoftwareInventoryAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _emitEvent,
                    _logger
                ));
                _logger.Info("SoftwareInventoryAnalyzer registered");
            }
            else
            {
                _logger.Info("SoftwareInventoryAnalyzer disabled by remote config");
            }

            _logger.Info($"Analyzers initialized: {_analyzers.Count} active");
        }

        public void RunStartup()
        {
            if (_analyzers.Count == 0)
                return;

            var analyzers = new List<IAgentAnalyzer>(_analyzers);

            _logger.Info($"Scheduling {analyzers.Count} startup analyzer(s) on background thread");

            Task.Run(() =>
            {
                foreach (var analyzer in analyzers)
                {
                    try
                    {
                        analyzer.AnalyzeAtStartup();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Analyzer {analyzer.Name} threw during startup", ex);
                    }
                }
            });
        }

        public void RunShutdown(int? whiteGlovePart = null)
        {
            if (_analyzers.Count == 0)
                return;

            _logger.Info($"Running {_analyzers.Count} shutdown analyzer(s) (whiteGlovePart={whiteGlovePart?.ToString() ?? "none"})");

            foreach (var analyzer in _analyzers)
            {
                try
                {
                    if (analyzer is SoftwareInventoryAnalyzer softwareAnalyzer)
                        softwareAnalyzer.AnalyzeAtShutdown(whiteGlovePart);
                    else
                        analyzer.AnalyzeAtShutdown();
                }
                catch (Exception ex)
                {
                    _logger.Error($"Analyzer {analyzer.Name} threw during shutdown", ex);
                }
            }
        }
    }
}
