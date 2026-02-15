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
using AutopilotMonitor.Agent.Core.Monitoring.Simulation;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Parses IME (Intune Management Extension) log files using regex patterns from the backend.
    /// Tracks app installation state transitions and emits strategic events.
    /// Adapted from EspOverlay's ImeLogFileParser + RjLogFileParser.
    ///
    /// Key design: Regex patterns are NOT hardcoded - they come from backend config via ImeLogPattern list.
    /// This allows updating patterns without agent rebuild when Microsoft changes IME log output.
    /// </summary>
    public class ImeLogTracker : IDisposable
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
            "AppWorkload-????????-??????.log"
        };

        // Callbacks to EnrollmentTracker
        public Action<string> OnEspPhaseChanged { get; set; }
        public Action<string> OnImeAgentVersion { get; set; }
        public Action OnImeStarted { get; set; }
        public Action<AppPackageState, AppInstallationState, AppInstallationState> OnAppStateChanged { get; set; }
        public Action<string> OnPoliciesDiscovered { get; set; }
        public Action OnAllAppsCompleted { get; set; }
        public Action OnUserSessionCompleted { get; set; }

        /// <summary>
        /// Access to the tracked package states
        /// </summary>
        public AppPackageStateList PackageStates => _packageStates;

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
                    var regex = new Regex(regexStr, RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
                        p.ProgressPercent, p.BytesDownloaded, p.BytesTotal);
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
                    BytesTotal = p.BytesTotal
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

        private async Task CheckLogFilesAsync(CancellationToken token)
        {
            if (!Directory.Exists(_logFolder))
                return;

            // Get all matching log files, sorted by name (archived files come before current)
            var files = new List<string>();
            foreach (var pattern in LogFilePatterns)
            {
                try
                {
                    files.AddRange(Directory.GetFiles(_logFolder, pattern));
                }
                catch (DirectoryNotFoundException) { }
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in files)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    var fileInfo = new FileInfo(filePath);
                    if (!fileInfo.Exists) continue;

                    var startPos = _positionTracker.GetSafePosition(filePath, fileInfo.Length);
                    if (startPos >= fileInfo.Length) continue;

                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Seek(startPos, SeekOrigin.Begin);

                        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
                        {
                            string line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                if (token.IsCancellationRequested) break;

                                // Parse CMTrace format to get the message content
                                CmTraceLogEntry entry;
                                string messageToMatch;
                                if (CmTraceLogParser.TryParseLine(line, out entry))
                                {
                                    messageToMatch = entry.Message;
                                }
                                else
                                {
                                    // Non-CMTrace line - match raw
                                    messageToMatch = line;
                                    entry = null;
                                }

                                if (string.IsNullOrEmpty(messageToMatch)) continue;

                                // Simulation mode delay
                                if (SimulationMode && entry != null)
                                {
                                    await ApplySimulationDelay(entry.Timestamp, token);
                                }

                                // Match against active patterns
                                foreach (var pattern in _activePatterns)
                                {
                                    var match = pattern.Regex.Match(messageToMatch);
                                    if (match.Success)
                                    {
                                        WriteMatchLog(filePath, line, pattern.PatternId);
                                        HandlePatternMatch(pattern, match, messageToMatch);
                                    }
                                }
                            }
                        }

                        _positionTracker.SetPosition(filePath, stream.Position);
                        _stateDirty = true;
                    }
                }
                catch (FileNotFoundException) { }
                catch (IOException ex)
                {
                    _logger.Debug($"ImeLogTracker: IO error reading {Path.GetFileName(filePath)}: {ex.Message}");
                }
            }
        }

        private void HandlePatternMatch(CompiledPattern pattern, Match match, string message)
        {
            try
            {
                var id = match.Groups["id"]?.Value;
                var useCurrentApp = pattern.Parameters.ContainsKey("useCurrentApp") &&
                                    pattern.Parameters["useCurrentApp"] == "true";

                if (useCurrentApp && string.IsNullOrEmpty(id))
                    id = _packageStates.CurrentPackageId;

                // Track every app ID seen during the current phase for comprehensive ignore on phase change
                if (!string.IsNullOrEmpty(id))
                    _seenAppIds.Add(id);

                switch (pattern.Action?.ToLower())
                {
                    case "imestarted":
                        HandleImeStarted();
                        break;

                    case "imesessionchange":
                        _logger.Debug($"IME session change: {match.Groups["change"]?.Value}");
                        break;

                    case "espphasedetected":
                        var phase = match.Groups["espPhase"]?.Value;
                        if (string.IsNullOrEmpty(phase) && pattern.Parameters.ContainsKey("phase"))
                            phase = pattern.Parameters["phase"];
                        if (!string.IsNullOrEmpty(phase))
                            HandleEspPhaseDetected(phase);
                        break;

                    case "setcurrentapp":
                        if (!string.IsNullOrEmpty(id))
                            _packageStates.SetCurrent(id);
                        break;

                    case "imeagentversion":
                        var version = match.Groups["agentVersion"]?.Value;
                        if (!string.IsNullOrEmpty(version))
                            OnImeAgentVersion?.Invoke(version);
                        break;

                    case "imeimpersonation":
                        _logger.Debug($"IME impersonation: {match.Groups["user"]?.Value}");
                        break;

                    case "enrollmentcompleted":
                        _logger.Info("ImeLogTracker: User session completed detected");
                        OnUserSessionCompleted?.Invoke();
                        break;

                    case "updatestateinstalled":
                        if (!string.IsNullOrEmpty(id))
                            UpdateStateWithCallback(id, AppInstallationState.Installed);
                        break;

                    case "updatestatedownloading":
                        if (!string.IsNullOrEmpty(id))
                        {
                            var bytes = match.Groups["bytes"]?.Value;
                            var ofbytes = match.Groups["ofbytes"]?.Value;
                            if (!string.IsNullOrEmpty(bytes) && !string.IsNullOrEmpty(ofbytes))
                                UpdateDownloadingWithCallback(id, bytes, ofbytes);
                            else
                                UpdateStateWithCallback(id, AppInstallationState.Downloading);
                        }
                        break;

                    case "updatestateinstalling":
                        if (!string.IsNullOrEmpty(id))
                            UpdateStateWithCallback(id, AppInstallationState.Installing);
                        break;

                    case "updatestateskipped":
                        if (!string.IsNullOrEmpty(id))
                            UpdateStateWithCallback(id, AppInstallationState.Skipped);
                        break;

                    case "updatestateerror":
                        if (pattern.Parameters.ContainsKey("checkTo") && pattern.Parameters["checkTo"] == "true")
                        {
                            // Only apply error if the "to" value is "Error"
                            var toValue = match.Groups["to"]?.Value;
                            if (string.Equals(toValue, "Error", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(id))
                                UpdateStateWithCallback(id, AppInstallationState.Error, errorPatternId: pattern.PatternId, errorDetail: message);
                        }
                        else if (!string.IsNullOrEmpty(id))
                        {
                            UpdateStateWithCallback(id, AppInstallationState.Error, errorPatternId: pattern.PatternId, errorDetail: message);
                        }
                        break;

                    case "updatestatepostponed":
                        if (!string.IsNullOrEmpty(id))
                        {
                            // Only postpone if not already in a terminal state
                            var pkg = _packageStates.GetPackage(id);
                            if (pkg != null && pkg.InstallationState != AppInstallationState.Installed &&
                                pkg.InstallationState != AppInstallationState.Error)
                            {
                                UpdateStateWithCallback(id, AppInstallationState.Postponed);
                            }
                        }
                        break;

                    case "esptrackstatus":
                        HandleEspTrackStatus(match);
                        break;

                    case "policiesdiscovered":
                        var policiesJson = match.Groups["policies"]?.Value;
                        if (!string.IsNullOrEmpty(policiesJson))
                            HandlePoliciesDiscovered(policiesJson);
                        break;

                    case "ignorecompletedapp":
                        _packageStates.AddToIgnoreList(_packageStates.CurrentPackageId);
                        break;

                    case "updatename":
                        var name = match.Groups["name"]?.Value;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                            _packageStates.UpdateName(id, name);
                        break;

                    case "updatewin32appstate":
                        var state = match.Groups["state"]?.Value;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(state))
                            _packageStates.UpdateStateFromWin32AppState(id, state);
                        break;

                    case "cancelstuckandsetcurrent":
                        HandleCancelStuckAndSetCurrent(id);
                        break;

                    default:
                        _logger.Debug($"ImeLogTracker: unhandled action '{pattern.Action}' for pattern {pattern.PatternId}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"ImeLogTracker: error handling match for {pattern.PatternId}: {ex.Message}");
            }
        }

        private void HandleImeStarted()
        {
            _logger.Info("ImeLogTracker: IME Agent Started detected");

            // Mark any currently active app as installed (it was running before restart)
            if (!string.IsNullOrEmpty(_packageStates.CurrentPackageId))
            {
                var currentPkg = _packageStates.GetPackage(_packageStates.CurrentPackageId);
                if (currentPkg?.IsActive == true)
                    UpdateStateWithCallback(_packageStates.CurrentPackageId, AppInstallationState.Installed);
            }

            OnImeStarted?.Invoke();
        }

        private string _lastEspPhaseDetected;

        private void HandleEspPhaseDetected(string espPhaseString)
        {
            // Validate phase and get its ordinal for forward-only enforcement
            int phaseOrd;
            if (!EspPhaseOrder.TryGetValue(espPhaseString, out phaseOrd))
                return; // Not a recognized ESP phase

            bool logPhaseIsCurrentPhase = true; // Both DeviceSetup and AccountSetup are "current"

            // Forward-only phase progression: reject backward transitions.
            // During AccountSetup, IME may re-evaluate device apps and log "In EspPhase: DeviceSetup"
            // which would corrupt tracking if we allowed it to trigger a phase change.
            if (phaseOrd < _currentPhaseOrder)
            {
                _logger.Debug($"ImeLogTracker: ignoring backward phase transition to {espPhaseString} (current phase order: {_currentPhaseOrder})");
                return;
            }

            // If the ESP phase actually changed (e.g. DeviceSetup -> AccountSetup),
            // move ALL known app IDs into the ignore list so they won't emit events in the new phase.
            // We use both _packageStates (apps from policiesdiscovered) AND _seenAppIds (all IDs from
            // any pattern match) to ensure comprehensive coverage - apps seen via setcurrentapp,
            // esptrackstatus etc. that never entered _packageStates are also silenced.
            if (!string.Equals(_lastEspPhaseDetected, espPhaseString, StringComparison.OrdinalIgnoreCase))
            {
                if (_lastEspPhaseDetected != null) // Not the first phase detection
                {
                    var ignoredFromStates = 0;
                    var ignoredFromSeen = 0;

                    foreach (var pkg in _packageStates)
                    {
                        if (_packageStates.AddToIgnoreList(pkg.Id))
                            ignoredFromStates++;
                    }
                    foreach (var appId in _seenAppIds)
                    {
                        if (_packageStates.AddToIgnoreList(appId))
                            ignoredFromSeen++;
                    }

                    _logger.Info($"ImeLogTracker: ESP phase changed from {_lastEspPhaseDetected} to {espPhaseString} - " +
                                 $"silenced {ignoredFromStates} packages + {ignoredFromSeen} additional seen IDs " +
                                 $"(total ignore list: {_packageStates.IgnoreList.Count})");

                    _packageStates.Clear();
                    _packageStates.SetCurrent(""); // Reset current package to avoid stale device-phase reference
                    _seenAppIds.Clear();
                    _allAppsCompletedFired = false;
                }
                _lastEspPhaseDetected = espPhaseString;
                _currentPhaseOrder = phaseOrd;
            }

            _logger.Info($"ImeLogTracker: ESP phase detected: {espPhaseString}");
            ActivatePatterns(logPhaseIsCurrentPhase);

            OnEspPhaseChanged?.Invoke(espPhaseString);
        }

        private void HandleEspTrackStatus(Match match)
        {
            var from = match.Groups["from"]?.Value;
            var to = match.Groups["to"]?.Value;
            var id = match.Groups["id"]?.Value;

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(to)) return;

            if (string.Equals(to, "InProgress", StringComparison.OrdinalIgnoreCase))
            {
                _packageStates.SetCurrent(id);
                UpdateStateWithCallback(id, AppInstallationState.Installing);
            }
            else if (string.Equals(to, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                UpdateStateWithCallback(id, AppInstallationState.Installed);
            }
            else if (string.Equals(to, "Error", StringComparison.OrdinalIgnoreCase))
            {
                UpdateStateWithCallback(id, AppInstallationState.Error, errorPatternId: "IME-ESP-TRACK-STATUS", errorDetail: $"ESP track status changed from {from} to {to}");
            }
        }

        private void HandlePoliciesDiscovered(string policiesJson)
        {
            try
            {
                var policies = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object>>>(policiesJson);
                if (policies != null)
                {
                    // Ignore user-targeted packages during device setup (we only track device phase in general)
                    _packageStates.AddUpdateFromJsonPolicies(policies, ignoreUserTargeted: false);
                    _logger.Info($"ImeLogTracker: discovered {policies.Count} policies, tracking {_packageStates.Count} packages");
                    OnPoliciesDiscovered?.Invoke(policiesJson);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"ImeLogTracker: failed to parse policies JSON: {ex.Message}");
            }
        }

        private void HandleCancelStuckAndSetCurrent(string newId)
        {
            if (string.IsNullOrEmpty(newId)) return;

            // If current app is stuck in an active state, mark it as skipped
            var currentPkg = _packageStates.GetPackage(_packageStates.CurrentPackageId);
            if (currentPkg != null && (currentPkg.InstallationState == AppInstallationState.Installing ||
                                       currentPkg.InstallationState == AppInstallationState.InProgress ||
                                       currentPkg.InstallationState == AppInstallationState.Downloading))
            {
                UpdateStateWithCallback(_packageStates.CurrentPackageId, AppInstallationState.Skipped);
            }

            _packageStates.SetCurrent(newId);
        }

        private void UpdateStateWithCallback(string id, AppInstallationState newState, int? progressPercent = null, string errorPatternId = null, string errorDetail = null)
        {
            var pkg = _packageStates.GetPackage(id);
            if (pkg == null) return;

            // Set error context before state change so it's available in ToEventData()
            if (newState == AppInstallationState.Error && !string.IsNullOrEmpty(errorPatternId))
                pkg.SetErrorContext(errorPatternId, errorDetail);

            var oldState = pkg.InstallationState;
            var changed = _packageStates.UpdateState(id, newState, progressPercent);

            if (changed)
            {
                _logger.Debug($"ImeLogTracker: {pkg.Name ?? id} state: {oldState} -> {newState}");
                OnAppStateChanged?.Invoke(pkg, oldState, newState);

                // Check if all apps are now completed - only fire once
                if (!_allAppsCompletedFired && _packageStates.CountAll > 0 && _packageStates.IsAllCompleted())
                {
                    _allAppsCompletedFired = true;
                    OnAllAppsCompleted?.Invoke();
                }
            }
        }

        private void UpdateDownloadingWithCallback(string id, string bytes, string ofbytes)
        {
            var pkg = _packageStates.GetPackage(id);
            if (pkg == null) return;

            var oldState = pkg.InstallationState;
            var changed = _packageStates.UpdateStateToDownloading(id, bytes, ofbytes);

            if (changed)
            {
                _logger.Debug($"ImeLogTracker: {pkg.Name ?? id} downloading: {bytes}/{ofbytes}");
                OnAppStateChanged?.Invoke(pkg, oldState, AppInstallationState.Downloading);
            }
        }

        private void ActivatePatterns(bool logPhaseIsCurrentPhase, bool force = false)
        {
            if (!force && _logPhaseIsCurrentPhase == logPhaseIsCurrentPhase)
                return;

            var patterns = new List<CompiledPattern>(_patternsAlways);
            if (logPhaseIsCurrentPhase)
            {
                patterns.AddRange(_patternsCurrentPhase);
                _lastLogTimestamp = DateTime.MinValue; // Reset simulation delay
            }
            else
            {
                patterns.AddRange(_patternsOtherPhases);
            }

            _activePatterns = patterns;
            _packageStates.SetCurrent(""); // Reset current package
            _logPhaseIsCurrentPhase = logPhaseIsCurrentPhase;
        }

        private async Task ApplySimulationDelay(DateTime logTimestamp, CancellationToken token)
        {
            if (!SimulationMode || logTimestamp == DateTime.MinValue)
                return;

            if (_lastLogTimestamp != DateTime.MinValue)
            {
                var timeSpan = logTimestamp - _lastLogTimestamp;
                if (timeSpan.TotalMilliseconds > 0)
                {
                    var delayMs = (int)(timeSpan.TotalMilliseconds / SpeedFactor);
                    delayMs = Math.Max(0, Math.Min(delayMs, 5000)); // Cap at 5 seconds
                    if (delayMs > 0)
                        await Task.Delay(delayMs, token);
                }
            }
            _lastLogTimestamp = logTimestamp;
        }

        public void Dispose()
        {
            Stop();
            _cts?.Dispose();
        }

        private void WriteMatchLog(string sourceFile, string rawLine, string patternId)
        {
            if (string.IsNullOrEmpty(_matchLogPath)) return;
            try
            {
                var entry = $"[{Path.GetFileName(sourceFile)}] [{patternId}] {rawLine}";
                lock (_matchLogLock)
                {
                    File.AppendAllText(_matchLogPath, entry + Environment.NewLine);
                }
            }
            catch { }
        }

        /// <summary>
        /// A compiled regex pattern with its action and parameters
        /// </summary>
        private class CompiledPattern
        {
            public string PatternId { get; set; }
            public Regex Regex { get; set; }
            public string Action { get; set; }
            public Dictionary<string, string> Parameters { get; set; }
        }
    }
}
