using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Partial: Log file polling and pattern matching logic.
    /// </summary>
    public partial class ImeLogTracker
    {
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
                    if (startPos >= fileInfo.Length)
                    {
                        _logger.Trace($"ImeLogTracker: {Path.GetFileName(filePath)} — no new data (pos={startPos}, size={fileInfo.Length})");
                        continue;
                    }
                    _logger.Trace($"ImeLogTracker: reading {Path.GetFileName(filePath)} from pos {startPos} (size={fileInfo.Length}, delta={fileInfo.Length - startPos})");

                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        stream.Seek(startPos, SeekOrigin.Begin);

                        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
                        {
                            // Buffer for multiline CMTrace entries (e.g. AgentExecutor.log
                            // "write output done. output = ..." spans many lines)
                            StringBuilder multiLineBuffer = null;
                            int multiLineCount = 0;

                            string line;
                            while ((line = await reader.ReadLineAsync()) != null)
                            {
                                if (token.IsCancellationRequested) break;

                                // --- Multiline CMTrace buffering ---
                                // CMTrace entries: <![LOG[message]LOG]!><time=...>
                                // When message contains newlines, the entry spans multiple lines.
                                // We buffer until we find the closing ]LOG]!> tag.
                                if (multiLineBuffer != null)
                                {
                                    // Continuing a multiline entry
                                    multiLineBuffer.Append('\n').Append(line);
                                    multiLineCount++;

                                    if (line.Contains("]LOG]!>"))
                                    {
                                        // Entry complete — use the assembled line
                                        line = multiLineBuffer.ToString();
                                        multiLineBuffer = null;
                                        multiLineCount = 0;
                                    }
                                    else if (multiLineCount >= MaxMultiLineBufferLines)
                                    {
                                        // Safety limit — discard to prevent unbounded memory usage
                                        _logger.Debug($"ImeLogTracker: discarding multiline CMTrace buffer after {multiLineCount} lines (corrupt entry?)");
                                        multiLineBuffer = null;
                                        multiLineCount = 0;
                                        continue;
                                    }
                                    else
                                    {
                                        // Still accumulating — read next line
                                        continue;
                                    }
                                }
                                else if (line.StartsWith("<![LOG[") && !line.Contains("]LOG]!>"))
                                {
                                    // Start of a multiline CMTrace entry
                                    multiLineBuffer = new StringBuilder(line);
                                    multiLineCount = 1;
                                    continue;
                                }

                                // --- Normal processing (single-line or completed multiline) ---
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
                                    try
                                    {
                                        var match = pattern.Regex.Match(messageToMatch);
                                        if (match.Success)
                                        {
                                            WriteMatchLog(filePath, line, pattern.PatternId);
                                            HandlePatternMatch(pattern, match, messageToMatch, entry);
                                        }
                                    }
                                    catch (RegexMatchTimeoutException)
                                    {
                                        _logger.Debug($"ImeLogTracker: regex timeout for pattern '{pattern.PatternId}' — skipped to prevent ReDoS");
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
                    _logger.Debug($"ImeLogTracker: IO error reading {filePath}: {ex.Message}");
                }
            }
        }

        private void HandlePatternMatch(CompiledPattern pattern, Match match, string message, CmTraceLogEntry entry)
        {
            LastMatchedPatternId = pattern.PatternId;
            LastMatchedLogTimestamp = entry?.Timestamp;
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

                    case "imeshutdown":
                        HandleImeShutdown();
                        break;

                    case "imesessionchange":
                        var sessionChange = match.Groups["change"]?.Value;
                        _logger.Debug($"IME session change: {sessionChange}");
                        OnImeSessionChange?.Invoke(sessionChange);
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
                        // Extract structured error code from named capture groups (exitCode, hresult, errorCode)
                        var extractedErrorCode = match.Groups["exitCode"]?.Value;
                        if (string.IsNullOrEmpty(extractedErrorCode))
                            extractedErrorCode = match.Groups["hresult"]?.Value;
                        if (string.IsNullOrEmpty(extractedErrorCode))
                            extractedErrorCode = match.Groups["errorCode"]?.Value;

                        if (pattern.Parameters.ContainsKey("checkTo") && pattern.Parameters["checkTo"] == "true")
                        {
                            // Only apply error if the "to" value is "Error"
                            var toValue = match.Groups["to"]?.Value;
                            if (string.Equals(toValue, "Error", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(id))
                                UpdateStateWithCallback(id, AppInstallationState.Error, errorPatternId: pattern.PatternId, errorDetail: message, errorCode: extractedErrorCode);
                        }
                        else if (!string.IsNullOrEmpty(id))
                        {
                            UpdateStateWithCallback(id, AppInstallationState.Error, errorPatternId: pattern.PatternId, errorDetail: message, errorCode: extractedErrorCode);
                        }
                        break;

                    case "captureexitcode":
                        var exitCodeVal = match.Groups["exitCode"]?.Value;
                        if (!string.IsNullOrEmpty(exitCodeVal) && !string.IsNullOrEmpty(_packageStates.CurrentPackageId))
                            _packageStates.GetPackage(_packageStates.CurrentPackageId)?.UpdateExitCode(exitCodeVal);
                        break;

                    case "capturehresult":
                        var hresultVal = match.Groups["hresult"]?.Value;
                        if (!string.IsNullOrEmpty(hresultVal) && !string.IsNullOrEmpty(_packageStates.CurrentPackageId))
                            _packageStates.GetPackage(_packageStates.CurrentPackageId)?.UpdateHResult(hresultVal);
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

                    case "updatedotelemetry":
                        var doTelJson = match.Groups["doTelJson"]?.Value;
                        if (!string.IsNullOrEmpty(doTelJson))
                            HandleDoTelemetry(doTelJson);
                        break;

                    // PowerShell script tracking actions
                    case "scriptstarted":
                        HandleScriptStarted(match, pattern.Parameters);
                        break;

                    case "scriptcontext":
                        HandleScriptContext(match, pattern.Parameters);
                        break;

                    case "scriptexitcode":
                        HandleScriptExitCode(match, pattern.Parameters);
                        break;

                    case "scriptoutput":
                        HandleScriptOutput(match, pattern.Parameters);
                        break;

                    case "scriptcompleted":
                        HandleScriptCompleted(match, pattern.Parameters);
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

        /// <summary>
        /// Parses [DO TEL] JSON and links it to the correct app via FileId.
        /// The FileId contains the app GUID in the format: ...intunewin-bin_{appGuid}_{number}
        /// </summary>
    }
}
