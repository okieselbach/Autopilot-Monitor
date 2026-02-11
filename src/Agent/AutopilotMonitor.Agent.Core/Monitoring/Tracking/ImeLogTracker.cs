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

        // Simulation mode
        public bool SimulationMode { get; set; }
        public double SpeedFactor { get; set; } = 50;
        private DateTime _lastLogTimestamp = DateTime.MinValue;

        // Background task
        private Task _pollingTask;
        private CancellationTokenSource _cts;
        private bool _allAppsCompletedFired;

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

        public ImeLogTracker(string logFolder, List<ImeLogPattern> patterns, AgentLogger logger, int pollingIntervalMs = 100, string matchLogPath = null)
        {
            _logFolder = Environment.ExpandEnvironmentVariables(logFolder);
            _logger = logger;
            _pollingIntervalMs = pollingIntervalMs;
            _matchLogPath = matchLogPath;
            _packageStates = new AppPackageStateList(logger);

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

                    try { await Task.Delay(_pollingIntervalMs, token); } catch (OperationCanceledException) { break; }
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
                                UpdateStateWithCallback(id, AppInstallationState.Error);
                        }
                        else if (!string.IsNullOrEmpty(id))
                        {
                            UpdateStateWithCallback(id, AppInstallationState.Error);
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
            bool logPhaseIsCurrentPhase;

            if (string.Equals(espPhaseString, "DeviceSetup", StringComparison.OrdinalIgnoreCase))
                logPhaseIsCurrentPhase = true; // We treat DeviceSetup as current phase
            else if (string.Equals(espPhaseString, "AccountSetup", StringComparison.OrdinalIgnoreCase))
                logPhaseIsCurrentPhase = true; // AccountSetup is also current phase
            else
                return; // Not a valid/relevant phase

            // If the ESP phase actually changed (e.g. DeviceSetup -> AccountSetup),
            // move all known apps into the ignore list so they won't emit events again in the new phase.
            // The IME re-reports already-installed apps at the start of each new phase; we must silence them.
            if (!string.Equals(_lastEspPhaseDetected, espPhaseString, StringComparison.OrdinalIgnoreCase))
            {
                if (_lastEspPhaseDetected != null) // Not the first phase detection
                {
                    _logger.Info($"ImeLogTracker: ESP phase changed from {_lastEspPhaseDetected} to {espPhaseString} - silencing {_packageStates.Count} previous-phase apps");
                    foreach (var pkg in _packageStates)
                        _packageStates.AddToIgnoreList(pkg.Id);
                    _packageStates.Clear();
                    _allAppsCompletedFired = false;
                }
                _lastEspPhaseDetected = espPhaseString;
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
                UpdateStateWithCallback(id, AppInstallationState.Error);
            }
        }

        private void HandlePoliciesDiscovered(string policiesJson)
        {
            try
            {
                var policies = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(policiesJson);
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

        private void UpdateStateWithCallback(string id, AppInstallationState newState, int? progressPercent = null)
        {
            var pkg = _packageStates.GetPackage(id);
            if (pkg == null) return;

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
