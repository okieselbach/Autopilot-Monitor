using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
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

        // M4.5.a: ImeTrackerStatePersistence removed. V2 relies on SignalLog-replay + the
        // SignalAdapter's fire-once dedup for resume semantics; fresh-start on restart is
        // intentional. See plan §4.x M4.5 Legacy-Cleanup.
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
        private const int MaxMultiLineBufferLines = 100;

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
        /// Fires on every pattern match with the matched <c>PatternId</c>. Plan §4.x M4.4.4.
        /// Invoked by <c>HandlePatternMatch</c> after <see cref="LastMatchedPatternId"/> is
        /// set, before action-specific callbacks — callers can read <see cref="LastMatchedPatternId"/>
        /// / <see cref="LastMatchedLogTimestamp"/> synchronously inside the handler.
        /// Added to enable the <c>WhiteGloveSealingPatternDetected</c> signal path in
        /// <c>ImeLogTrackerAdapter</c> without the legacy polling-on-LastMatchedPatternId anti-pattern.
        /// </summary>
        public Action<string> OnPatternMatched { get; set; }

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

            // M4.5.a: state-persistence removed; stateDirectory retained in signature for
            // backwards-compat with existing callers/tests but intentionally unused.
            _ = stateDirectory;

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
                    // Singleline: make '.' match newlines too — required because multiline CMTrace
                    // entries are reassembled with '\n' chars (e.g. DO TEL JSON in IME >= 1.101)
                    var regex = new Regex(regexStr, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

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

        #region State Persistence (M4.5.a: no-op stubs — legacy ImeTrackerStatePersistence removed)

        // V2 resume semantics come from SignalLog-replay + SignalAdapter fire-once dedup.
        // These stubs preserve the call-sites (LoadState / SaveState / DeleteState) across
        // the polling loop + event handlers so the tracker compiles unchanged outside this
        // region. See plan §4.x M4.5 Legacy-Cleanup.

        private void LoadState()
        {
            // no-op — fresh start on restart
        }

        private void SaveState()
        {
            // no-op — nothing to persist
        }

        /// <summary>Kept as public no-op for API compatibility; callers treat it as a hint.</summary>
        public void DeleteState()
        {
            // no-op
        }

        #endregion

    }
}
