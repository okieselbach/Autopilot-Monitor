using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Configuration
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
        private readonly EmergencyReporter _emergencyReporter;
        private readonly DistressReporter _distressReporter;
        private readonly AuthFailureTracker _authFailureTracker;

        private AgentConfigResponse _currentConfig;
        private readonly object _configLock = new object();

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

        public RemoteConfigService(BackendApiClient apiClient, string tenantId, AgentLogger logger, EmergencyReporter emergencyReporter = null, DistressReporter distressReporter = null, AuthFailureTracker authFailureTracker = null)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _emergencyReporter = emergencyReporter;
            _distressReporter = distressReporter;
            _authFailureTracker = authFailureTracker;

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

                if (config != null)
                {
                    _logger.Info($"Remote config fetched: ConfigVersion={config.ConfigVersion}, " +
                                 $"GatherRules={config.GatherRules?.Count ?? 0}");

                    LogCollectorSettings(config.Collectors);
                    SetConfig(config);
                    CacheConfig(config);
                    _authFailureTracker?.RecordSuccess();
                    return config;
                }

                _logger.Warning("Remote config fetch returned empty response");
            }
            catch (BackendAuthException ex)
            {
                _logger.Warning($"Config fetch authentication failed: {ex.Message}");

                // V1 parity — AuthFailureTracker is the single dispatch point for auth-failure
                // distress signals (see HandleAuthFailure in EventUploadOrchestrator). It sends
                // the first-failure report via its constructor-injected DistressReporter with
                // the correct DistressErrorType per status code (401 → AuthCertificateRejected,
                // 403 → DeviceNotRegistered) and suppresses duplicates on subsequent failures.
                _authFailureTracker?.RecordFailure(ex.StatusCode, "agent/config");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to fetch remote config: {ex.Message}");

                // Non-auth failure → use authenticated emergency channel
                if (_emergencyReporter != null)
                {
                    _ = _emergencyReporter.TrySendAsync(
                        AgentErrorType.ConfigFetchFailed,
                        ex.Message);
                }
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

                // SECURITY: Never persist UnrestrictedMode to disk — require live backend auth
                var liveValue = config.UnrestrictedMode;
                config.UnrestrictedMode = false;
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                config.UnrestrictedMode = liveValue;

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
                    var config = JsonConvert.DeserializeObject<AgentConfigResponse>(json);
                    if (config != null)
                    {
                        // SECURITY: Never trust cached UnrestrictedMode — always require live backend auth
                        config.UnrestrictedMode = false;
                    }
                    return config;
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
                ConfigVersion = 0,
                UploadIntervalSeconds = 30,
                SelfDestructOnComplete = true,
                KeepLogFile = false,
                EnableGeoLocation = true,
                EnableImeMatchLog = false,
                Collectors = CollectorConfiguration.CreateDefault(),
                GatherRules = new System.Collections.Generic.List<GatherRule>(),
                ImeLogPatterns = new System.Collections.Generic.List<ImeLogPattern>()
            };
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }
}
