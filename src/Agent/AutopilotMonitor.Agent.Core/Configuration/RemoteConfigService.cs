using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Monitoring.Network;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.Core.Configuration
{
    /// <summary>
    /// Fetches and caches remote configuration from the backend API
    /// Provides collector toggles and gather rules to the agent
    /// </summary>
    public class RemoteConfigService : IDisposable
    {
        private readonly BackendApiClient _apiClient;
        private readonly string _tenantId;
        private readonly AgentLogger _logger;
        private readonly string _cacheFilePath;
        private Timer _refreshTimer;

        private AgentConfigResponse _currentConfig;
        private readonly object _configLock = new object();

        /// <summary>
        /// Fires when the remote configuration changes
        /// </summary>
        public event EventHandler<AgentConfigResponse> ConfigChanged;

        /// <summary>
        /// Gets the current configuration (thread-safe)
        /// </summary>
        public AgentConfigResponse CurrentConfig
        {
            get
            {
                lock (_configLock)
                {
                    return _currentConfig;
                }
            }
        }

        public RemoteConfigService(BackendApiClient apiClient, string tenantId, AgentLogger logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            var cacheDir = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\Config");
            _cacheFilePath = Path.Combine(cacheDir, "remote-config.json");
        }

        /// <summary>
        /// Fetches the initial configuration from the backend
        /// Falls back to cached config if the API is unreachable
        /// </summary>
        public async Task<AgentConfigResponse> FetchConfigAsync()
        {
            try
            {
                _logger.Info("Fetching remote configuration from backend...");
                var config = await _apiClient.GetAgentConfigAsync(_tenantId);

                if (config != null && config.Success)
                {
                    _logger.Info($"Remote config fetched: ConfigVersion={config.ConfigVersion}, " +
                                 $"GatherRules={config.GatherRules?.Count ?? 0}, " +
                                 $"RefreshInterval={config.RefreshIntervalSeconds}s");

                    LogCollectorSettings(config.Collectors);
                    SetConfig(config);
                    CacheConfig(config);
                    return config;
                }

                _logger.Warning($"Remote config fetch returned unsuccessful: {config?.Message}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to fetch remote config: {ex.Message}");
            }

            // Fall back to cached config
            var cached = LoadCachedConfig();
            if (cached != null)
            {
                _logger.Info("Using cached remote configuration");
                SetConfig(cached);
                return cached;
            }

            // Fall back to defaults
            _logger.Info("No cached config available, using defaults (all optional collectors disabled)");
            var defaults = CreateDefaultConfig();
            SetConfig(defaults);
            return defaults;
        }

        /// <summary>
        /// Starts periodic config refresh
        /// </summary>
        public void StartPeriodicRefresh()
        {
            var interval = CurrentConfig?.RefreshIntervalSeconds ?? 300;
            _logger.Info($"Starting periodic config refresh every {interval}s");

            _refreshTimer = new Timer(
                _ => RefreshConfigAsync(),
                null,
                TimeSpan.FromSeconds(interval),
                TimeSpan.FromSeconds(interval)
            );
        }

        /// <summary>
        /// Stops periodic config refresh
        /// </summary>
        public void StopPeriodicRefresh()
        {
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        private async void RefreshConfigAsync()
        {
            try
            {
                var newConfig = await _apiClient.GetAgentConfigAsync(_tenantId);

                if (newConfig != null && newConfig.Success)
                {
                    var oldConfig = CurrentConfig;

                    // Check if config actually changed
                    if (oldConfig == null || newConfig.ConfigVersion != oldConfig.ConfigVersion)
                    {
                        _logger.Info($"Remote config updated: version {oldConfig?.ConfigVersion} -> {newConfig.ConfigVersion}");
                        LogCollectorSettings(newConfig.Collectors);
                        SetConfig(newConfig);
                        CacheConfig(newConfig);
                        ConfigChanged?.Invoke(this, newConfig);
                    }

                    // Update refresh interval if changed
                    if (newConfig.RefreshIntervalSeconds != (oldConfig?.RefreshIntervalSeconds ?? 300))
                    {
                        _logger.Info($"Config refresh interval changed to {newConfig.RefreshIntervalSeconds}s");
                        _refreshTimer?.Change(
                            TimeSpan.FromSeconds(newConfig.RefreshIntervalSeconds),
                            TimeSpan.FromSeconds(newConfig.RefreshIntervalSeconds)
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Config refresh failed (will retry): {ex.Message}");
            }
        }

        private void SetConfig(AgentConfigResponse config)
        {
            lock (_configLock)
            {
                _currentConfig = config;
            }
        }

        private void LogCollectorSettings(CollectorConfiguration collectors)
        {
            if (collectors == null) return;

            _logger.Info("  Collector settings:");
            _logger.Info($"    Performance: {(collectors.EnablePerformanceCollector ? "ON" : "OFF")} (interval: {collectors.PerformanceIntervalSeconds}s)");
        }

        private void CacheConfig(AgentConfigResponse config)
        {
            try
            {
                var dir = Path.GetDirectoryName(_cacheFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(_cacheFilePath, json);
                _logger.Debug("Remote config cached to disk");
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to cache remote config: {ex.Message}");
            }
        }

        private AgentConfigResponse LoadCachedConfig()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    var json = File.ReadAllText(_cacheFilePath);
                    return JsonConvert.DeserializeObject<AgentConfigResponse>(json);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Failed to load cached config: {ex.Message}");
            }
            return null;
        }

        private AgentConfigResponse CreateDefaultConfig()
        {
            return new AgentConfigResponse
            {
                Success = true,
                Message = "Default configuration (offline fallback)",
                Collectors = CollectorConfiguration.CreateDefault(),
                ConfigVersion = 0,
                RefreshIntervalSeconds = 300
            };
        }

        public void Dispose()
        {
            StopPeriodicRefresh();
        }
    }
}
