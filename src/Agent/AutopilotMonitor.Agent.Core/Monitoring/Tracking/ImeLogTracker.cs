using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;
using AutopilotMonitor.Agent.Core.Monitoring.Tracking;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Parses IME (Intune Management Extension) log files using regex patterns from the backend.
    /// Tracks app installation state transitions and emits strategic events.
    /// Split into partial classes: Core, LogProcessing, Handlers.
    ///
    /// Key design: Regex patterns are NOT hardcoded - they come from backend config via ImeLogPattern list.
    /// This allows updating patterns without agent rebuild when Microsoft changes IME log output.
    /// </summary>
    public partial class ImeLogTracker : IDisposable
    {
        private readonly string _logFolder;
        private readonly AgentLogger _logger;
        private readonly LogFilePositionTracker _positionTracker = new LogFilePositionTracker();
        private readonly AppPackageStateList _packageStates;
        private readonly int _pollingIntervalMs;
        private readonly string _matchLogPath; // Optional: path to write every matched raw line
        private readonly object _matchLogLock = new object();

        // Compiled pattern matchers grouped by category
        private List<CompiledPattern> _patternsAlways = new List<CompiledPattern>();
        private List<CompiledPattern> _patternsCurrentPhase = new List<CompiledPattern>();
        private List<CompiledPattern> _patternsOtherPhases = new List<CompiledPattern>();

        // Active matchers (changes based on ESP phase)
        private List<CompiledPattern> _activePatterns = new List<CompiledPattern>();
        private bool _logPhaseIsCurrentPhase = false;

        // Phase isolation: track ALL app IDs seen during pattern matching in the current phase.
        // On phase change, these are added to the ignore list to prevent device-phase apps from
        // bleeding into AccountSetup. This is more comprehensive than only ignoring packageStates
        // because setcurrentapp, esptrackstatus etc. see IDs that never enter packageStates.
        private readonly HashSet<string> _seenAppIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Forward-only phase progression: prevents accidental phase bounce (e.g. DeviceSetup
        // re-detected during AccountSetup if IME re-evaluates device apps and logs the old phase).
        private static readonly Dictionary<string, int> EspPhaseOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "DeviceSetup", 1 },
            { "AccountSetup", 2 }
        };
        private int _currentPhaseOrder = 0;

        // Snapshots of package states from completed ESP phases (e.g. DeviceSetup apps before AccountSetup starts)
        private readonly Dictionary<string, List<Dictionary<string, object>>> _phasePackageSnapshots =
            new Dictionary<string, List<Dictionary<string, object>>>(StringComparer.OrdinalIgnoreCase);

        // Simulation mode
        public bool SimulationMode { get; set; }
        public double SpeedFactor { get; set; } = 50;
        private DateTime _lastLogTimestamp = DateTime.MinValue;

        // Background task
        private Task _pollingTask;
        private CancellationTokenSource _cts;
        private bool _allAppsCompletedFired;

        // State persistence: saves tracker state to disk so agent restart continues
        // from the exact log position without re-parsing or re-building ignore lists.
        private readonly ImeTrackerStatePersistence _statePersistence;
        private bool _stateDirty;

        // Standard GUID capture pattern used as {GUID} placeholder in patterns
        private const string GuidPattern = @"(?<id>[a-z0-9]{8}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{12})";

        // Log files to monitor
        private static readonly string[] LogFilePatterns = new[]
        {
            "IntuneManagementExtension.log",
            "_IntuneManagementExtension.log",
            "IntuneManagementExtension-????????-??????.log",
            "AppWorkload.log",
            "AppWorkload-????????-??????.log",
            // PowerShell script tracking
            "AgentExecutor.log",
            "AgentExecutor-????????-??????.log",
            "HealthScripts.log",
            "HealthScripts-????????-??????.log"
        };

        // Script execution state tracking (accumulates multi-line data before emitting events)
        private ScriptExecutionState _currentRemediationScript;
        private readonly Dictionary<string, ScriptExecutionState> _pendingPlatformScripts =
            new Dictionary<string, ScriptExecutionState>(StringComparer.OrdinalIgnoreCase);
        private string _lastPlatformScriptPolicyId;
        private const int MaxScriptOutputLength = 2048;

        // Set synchronously during HandlePatternMatch so callbacks can read it
        public string LastMatchedPatternId { get; private set; }
        public DateTime? LastMatchedLogTimestamp { get; private set; }

        // Callbacks to EnrollmentTracker
        public Action<string> OnEspPhaseChanged { get; set; }
        public Action<string> OnImeAgentVersion { get; set; }
        public Action OnImeStarted { get; set; }
        public Action<AppPackageState, AppInstallationState, AppInstallationState> OnAppStateChanged { get; set; }
        public Action<string> OnPoliciesDiscovered { get; set; }
        public Action OnAllAppsCompleted { get; set; }
        public Action OnUserSessionCompleted { get; set; }
        public Action<string> OnImeSessionChange { get; set; }
        public Action<AppPackageState> OnDoTelemetryReceived { get; set; }
        public Action<ScriptExecutionState> OnScriptCompleted { get; set; }

        /// <summary>
        /// Access to the tracked package states
        /// </summary>
        public AppPackageStateList PackageStates => _packageStates;

        /// <summary>
        /// Snapshots of package states from completed ESP phases (keyed by phase name, e.g. "DeviceSetup").
        /// Captured before package states are cleared on phase transition.
        /// </summary>
        public Dictionary<string, List<Dictionary<string, object>>> PhasePackageSnapshots => _phasePackageSnapshots;

        public ImeLogTracker(string logFolder, List<ImeLogPattern> patterns, AgentLogger logger, int pollingIntervalMs = 100, string matchLogPath = null, string stateDirectory = null)
        {
            _logFolder = Environment.ExpandEnvironmentVariables(logFolder);
            _logger = logger;
            _pollingIntervalMs = pollingIntervalMs;
            _matchLogPath = matchLogPath;
            _packageStates = new AppPackageStateList(logger);

            // State persistence setup
            if (!string.IsNullOrEmpty(stateDirectory))
            {
                _statePersistence = new ImeTrackerStatePersistence(stateDirectory, logger);
            }

            if (!string.IsNullOrEmpty(_matchLogPath))
            {
                try { Directory.CreateDirectory(Path.GetDirectoryName(_matchLogPath)); } catch { }
                _logger.Info($"ImeLogTracker: match log enabled -> {_matchLogPath}");
            }

            CompilePatterns(patterns);
            ActivatePatterns(logPhaseIsCurrentPhase: false, force: true);
        }

        /// <summary>
        /// Compiles ImeLogPattern list from backend into Regex objects grouped by category.
        /// </summary>
        public void CompilePatterns(List<ImeLogPattern> patterns)
        {
            _patternsAlways = new List<CompiledPattern>();
            _patternsCurrentPhase = new List<CompiledPattern>();
            _patternsOtherPhases = new List<CompiledPattern>();

            if (patterns == null) return;

            foreach (var pattern in patterns.Where(p => p.Enabled))
            {
                try
                {
                    // Replace {GUID} placeholder with actual GUID capture regex
                    var regexStr = pattern.Pattern.Replace("{GUID}", GuidPattern);
                    var regex = new Regex(regexStr, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));

                    var compiled = new CompiledPattern
                    {
                        PatternId = pattern.PatternId,
                        Regex = regex,
                        Action = pattern.Action,
                        Parameters = pattern.Parameters ?? new Dictionary<string, string>()
                    };

                    switch (pattern.Category?.ToLower())
                    {
                        case "always":
                            _patternsAlways.Add(compiled);
                            break;
                        case "currentphase":
                            _patternsCurrentPhase.Add(compiled);
                            break;
                        case "otherphases":
                            _patternsOtherPhases.Add(compiled);
                            break;
                        default:
                            _patternsAlways.Add(compiled); // Default to always
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to compile IME pattern {pattern.PatternId}: {ex.Message}");
                }
            }

            _logger.Info($"ImeLogTracker: compiled {_patternsAlways.Count} always, {_patternsCurrentPhase.Count} currentPhase, {_patternsOtherPhases.Count} otherPhases patterns");

            // Re-activate with current phase state
            ActivatePatterns(_logPhaseIsCurrentPhase, force: true);
        }

        /// <summary>
        /// Starts the background polling task
        /// </summary>
        public void Start()
        {
            if (_pollingTask != null) return;

            // Restore persisted state from previous agent lifetime (handles agent restart mid-enrollment).
            // This recovers phase order, ignore list, app states, and log file positions so we continue
            // exactly where we left off — no re-parsing, no device-phase bleeding.
            LoadState();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _pollingTask = Task.Run(async () =>
            {
                _logger.Info("ImeLogTracker: polling started");

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await CheckLogFilesAsync(token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _logger.Warning($"ImeLogTracker: error during log check: {ex.Message}");
                        try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
                    }

                    // Persist state after each polling cycle so agent restart continues from here
                    if (_stateDirty)
                    {
                        SaveState();
                        _stateDirty = false;
                    }

                    try { await Task.Delay(_pollingIntervalMs, token); } catch (OperationCanceledException) { break; }
                }

                // Final state save on shutdown
                if (_stateDirty)
                {
                    SaveState();
                    _stateDirty = false;
                }

                _logger.Info("ImeLogTracker: polling stopped");
            }, token);
        }

        /// <summary>
        /// Stops the background polling task
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
            try { _pollingTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            _pollingTask = null;
        }

        #region State Persistence

        /// <summary>
        /// Loads persisted state from disk. Called on Start() to restore tracker state
        /// after an agent restart, so parsing continues exactly where it left off.
        /// </summary>
        private void LoadState()
        {
            if (_statePersistence == null) return;

            var state = _statePersistence.Load();
            if (state == null) return;

            // Restore phase tracking
            _currentPhaseOrder = state.CurrentPhaseOrder;
            _lastEspPhaseDetected = state.LastEspPhaseDetected;
            _allAppsCompletedFired = state.AllAppsCompletedFired;
            _logPhaseIsCurrentPhase = state.LogPhaseIsCurrentPhase;

            // Restore seen app IDs
            _seenAppIds.Clear();
            if (state.SeenAppIds != null)
            {
                foreach (var id in state.SeenAppIds)
                    _seenAppIds.Add(id);
            }

            // Restore ignore list
            if (state.IgnoreList != null)
            {
                foreach (var id in state.IgnoreList)
                    _packageStates.AddToIgnoreList(id);
            }

            // Restore current package ID
            _packageStates.CurrentPackageId = state.CurrentPackageId;

            // Restore package states
            if (state.Packages != null)
            {
                _packageStates.Clear();
                foreach (var p in state.Packages)
                {
                    var pkg = AppPackageState.Restore(
                        p.Id, p.ListPos, p.Name,
                        (AppRunAs)p.RunAs, (AppIntent)p.Intent, (AppTargeted)p.Targeted,
                        p.DependsOn != null ? new HashSet<string>(p.DependsOn) : new HashSet<string>(),
                        (AppInstallationState)p.InstallationState, p.DownloadingOrInstallingSeen,
                        p.ProgressPercent, p.BytesDownloaded, p.BytesTotal,
                        doFileSize: p.DoFileSize, doTotalBytesDownloaded: p.DoTotalBytesDownloaded,
                        doBytesFromPeers: p.DoBytesFromPeers, doPercentPeerCaching: p.DoPercentPeerCaching,
                        doBytesFromLanPeers: p.DoBytesFromLanPeers, doBytesFromGroupPeers: p.DoBytesFromGroupPeers,
                        doBytesFromInternetPeers: p.DoBytesFromInternetPeers, doDownloadMode: p.DoDownloadMode,
                        doDownloadDuration: p.DoDownloadDuration, doBytesFromHttp: p.DoBytesFromHttp,
                        hasDoTelemetry: p.HasDoTelemetry);
                    _packageStates.Add(pkg);
                }
            }

            // Restore file positions
            if (state.FilePositions != null)
            {
                foreach (var kvp in state.FilePositions)
                {
                    var fullPath = Path.Combine(_logFolder, kvp.Key);
                    _positionTracker.RestorePosition(fullPath, kvp.Value.Position, kvp.Value.LastKnownSize);
                }
            }

            // Re-activate patterns based on restored phase state
            ActivatePatterns(_logPhaseIsCurrentPhase, force: true);

            _logger.Info($"ImeLogTracker: state restored - phase: {_lastEspPhaseDetected ?? "(none)"} (order: {_currentPhaseOrder}), " +
                         $"ignore list: {_packageStates.IgnoreList.Count}, packages: {_packageStates.Count}, " +
                         $"file positions: {state.FilePositions?.Count ?? 0}");
        }

        /// <summary>
        /// Persists current tracker state to disk as JSON.
        /// Called after each polling cycle when state has changed.
        /// </summary>
        private void SaveState()
        {
            if (_statePersistence == null) return;

            // Build state DTO
            var state = new ImeTrackerStateData
            {
                CurrentPhaseOrder = _currentPhaseOrder,
                LastEspPhaseDetected = _lastEspPhaseDetected,
                AllAppsCompletedFired = _allAppsCompletedFired,
                LogPhaseIsCurrentPhase = _logPhaseIsCurrentPhase,
                SeenAppIds = _seenAppIds.ToList(),
                IgnoreList = _packageStates.IgnoreList.ToList(),
                CurrentPackageId = _packageStates.CurrentPackageId,
                Packages = _packageStates.Select(p => new PackageStateData
                {
                    Id = p.Id,
                    ListPos = p.ListPos,
                    Name = p.Name,
                    RunAs = (int)p.RunAs,
                    Intent = (int)p.Intent,
                    Targeted = (int)p.Targeted,
                    DependsOn = p.DependsOn?.ToList() ?? new List<string>(),
                    InstallationState = (int)p.InstallationState,
                    DownloadingOrInstallingSeen = p.DownloadingOrInstallingSeen,
                    ProgressPercent = p.ProgressPercent,
                    BytesDownloaded = p.BytesDownloaded,
                    BytesTotal = p.BytesTotal,
                    DoFileSize = p.DoFileSize,
                    DoTotalBytesDownloaded = p.DoTotalBytesDownloaded,
                    DoBytesFromPeers = p.DoBytesFromPeers,
                    DoPercentPeerCaching = p.DoPercentPeerCaching,
                    DoBytesFromLanPeers = p.DoBytesFromLanPeers,
                    DoBytesFromGroupPeers = p.DoBytesFromGroupPeers,
                    DoBytesFromInternetPeers = p.DoBytesFromInternetPeers,
                    DoDownloadMode = p.DoDownloadMode,
                    DoDownloadDuration = p.DoDownloadDuration,
                    DoBytesFromHttp = p.DoBytesFromHttp,
                    HasDoTelemetry = p.HasDoTelemetry
                }).ToList(),
                FilePositions = new Dictionary<string, FilePositionData>()
            };

            // Store file positions by filename only (log folder is known)
            foreach (var kvp in _positionTracker.GetAllPositions())
            {
                var fileName = Path.GetFileName(kvp.Key);
                state.FilePositions[fileName] = new FilePositionData
                {
                    Position = kvp.Value.Position,
                    LastKnownSize = kvp.Value.LastKnownSize
                };
            }

            _statePersistence.Save(state);
        }

        /// <summary>
        /// Deletes persisted state file. Called on enrollment complete to ensure
        /// a fresh state on the next enrollment cycle.
        /// </summary>
        public void DeleteState()
        {
            _statePersistence?.Delete();
        }

        #endregion

    }
}
