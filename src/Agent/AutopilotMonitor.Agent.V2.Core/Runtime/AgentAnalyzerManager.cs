#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Runtime
{
    /// <summary>
    /// Manages the three agent analyzers: initialisation from remote config, startup execution
    /// on a background thread, and shutdown execution with optional WhiteGlove part discriminator.
    /// Plan §4.x M4.6.δ.
    /// <para>
    /// Ported from Legacy <c>Monitoring/Runtime/AnalyzerManager.cs</c> with the sole change that
    /// V2 reads the <see cref="AnalyzerConfiguration"/> directly from <see cref="AgentConfigResponse.Analyzers"/>
    /// rather than through a <c>RemoteConfigService</c> indirection — the service-level cache is
    /// already resolved by <c>Program.cs</c> before it instantiates this manager.
    /// </para>
    /// </summary>
    public sealed class AgentAnalyzerManager
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly InformationalEventPost _post;
        private readonly AnalyzerConfiguration _analyzerConfig;

        private readonly List<IAgentAnalyzer> _analyzers = new List<IAgentAnalyzer>();
        private bool _initialised;

        public AgentAnalyzerManager(
            AgentConfiguration configuration,
            AgentLogger logger,
            InformationalEventPost post,
            AnalyzerConfiguration? analyzerConfig)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _analyzerConfig = analyzerConfig ?? new AnalyzerConfiguration();
        }

        /// <summary>Exposed for tests / observability.</summary>
        public IReadOnlyList<IAgentAnalyzer> Analyzers => _analyzers;

        public void Initialize()
        {
            if (_initialised) return;
            _initialised = true;
            _analyzers.Clear();

            if (_analyzerConfig.EnableLocalAdminAnalyzer)
            {
                _analyzers.Add(new LocalAdminAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _post,
                    _logger,
                    _analyzerConfig.LocalAdminAllowedAccounts));
                _logger.Info("LocalAdminAnalyzer registered");
            }
            else
            {
                _logger.Info("LocalAdminAnalyzer disabled by remote config");
            }

            if (_analyzerConfig.EnableSoftwareInventoryAnalyzer)
            {
                _analyzers.Add(new SoftwareInventoryAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _post,
                    _logger));
                _logger.Info("SoftwareInventoryAnalyzer registered");
            }
            else
            {
                _logger.Info("SoftwareInventoryAnalyzer disabled by remote config");
            }

            if (_analyzerConfig.EnableIntegrityBypassAnalyzer)
            {
                _analyzers.Add(new IntegrityBypassAnalyzer(
                    _configuration.SessionId,
                    _configuration.TenantId,
                    _post,
                    _logger));
                _logger.Info("IntegrityBypassAnalyzer registered");
            }
            else
            {
                _logger.Info("IntegrityBypassAnalyzer disabled by remote config");
            }

            _logger.Info($"Analyzers initialized: {_analyzers.Count} active");
        }

        /// <summary>
        /// Runs <see cref="IAgentAnalyzer.AnalyzeAtStartup"/> on a background thread for every
        /// registered analyzer. Fire-and-forget: one analyzer throwing must not affect the others
        /// or the caller's critical path.
        /// </summary>
        public void RunStartup()
        {
            if (!_initialised) Initialize();
            if (_analyzers.Count == 0) return;

            var snapshot = new List<IAgentAnalyzer>(_analyzers);
            _logger.Info($"Scheduling {snapshot.Count} startup analyzer(s) on background thread");

            Task.Run(() =>
            {
                foreach (var analyzer in snapshot)
                {
                    try { analyzer.AnalyzeAtStartup(); }
                    catch (Exception ex) { _logger.Error($"Analyzer {analyzer.Name} threw during startup", ex); }
                }
            });
        }

        /// <summary>
        /// Runs <see cref="IAgentAnalyzer.AnalyzeAtShutdown"/> synchronously so the caller can
        /// sequence it before <c>CleanupService.ExecuteSelfDestruct</c>. <see cref="SoftwareInventoryAnalyzer"/>
        /// receives the optional WhiteGlove-part discriminator via its overload.
        /// </summary>
        public void RunShutdown(int? whiteGlovePart = null)
        {
            if (!_initialised) Initialize();
            if (_analyzers.Count == 0) return;

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
