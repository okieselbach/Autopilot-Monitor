using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime
{
    /// <summary>
    /// Partial: Event handlers for pattern matches (app state, scripts, ESP phases),
    /// telemetry processing, and utility methods.
    /// </summary>
    public partial class ImeLogTracker
    {
        private void HandleDoTelemetry(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // Extract app GUID from FileId
                string fileId = root.TryGetProperty("FileId", out var fileIdProp)
                    ? fileIdProp.GetString() : null;

                if (string.IsNullOrEmpty(fileId))
                {
                    _logger.Debug("ImeLogTracker: DO TEL entry has no FileId, skipping");
                    return;
                }

                string appId = ExtractAppIdFromDoFileId(fileId);
                if (string.IsNullOrEmpty(appId))
                {
                    appId = _packageStates.CurrentPackageId;
                    _logger.Debug($"ImeLogTracker: Could not extract app ID from DO FileId, using current app: {appId}");
                }

                if (string.IsNullOrEmpty(appId)) return;

                var pkg = _packageStates.GetPackage(appId);
                if (pkg == null)
                {
                    _logger.Debug($"ImeLogTracker: DO TEL for unknown app {appId}, skipping");
                    return;
                }

                // Extract all DO fields
                long fileSize = root.TryGetProperty("FileSize", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long totalBytes = root.TryGetProperty("TotalBytesDownloaded", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long bytesFromPeers = root.TryGetProperty("BytesFromPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                int peerCachingPct = root.TryGetProperty("PercentPeerCaching", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                long bytesLanPeers = root.TryGetProperty("BytesFromLanPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long bytesGroupPeers = root.TryGetProperty("BytesFromGroupPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                long bytesInternetPeers = root.TryGetProperty("BytesFromInternetPeers", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
                int downloadMode = root.TryGetProperty("DownloadMode", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : -1;
                string downloadDuration = root.TryGetProperty("DownloadDuration", out p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                long bytesFromHttp = root.TryGetProperty("BytesFromHttp", out p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;

                pkg.UpdateDoTelemetry(fileSize, totalBytes, bytesFromPeers, peerCachingPct,
                    bytesLanPeers, bytesGroupPeers, bytesInternetPeers,
                    downloadMode, downloadDuration, bytesFromHttp);

                _logger.Info($"ImeLogTracker: DO TEL for {pkg.Name ?? appId}: " +
                    $"size={fileSize}, peers={bytesFromPeers} ({peerCachingPct}%), " +
                    $"http={bytesFromHttp}, mode={downloadMode}, duration={downloadDuration}");

                OnDoTelemetryReceived?.Invoke(pkg);
                _stateDirty = true;
            }
            catch (Exception ex)
            {
                _logger.Warning($"ImeLogTracker: Failed to parse DO TEL JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// Extracts app GUID from DO FileId string.
        /// Format: ...intunewin-bin_{appGuid}_{number}
        /// Falls back to trying the second-to-last GUID-like segment.
        /// </summary>
        internal static string ExtractAppIdFromDoFileId(string fileId)
        {
            // Primary: look for "intunewin-bin_" marker
            const string marker = "intunewin-bin_";
            var idx = fileId.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var afterMarker = fileId.Substring(idx + marker.Length);
                if (afterMarker.Length >= 36)
                {
                    var candidate = afterMarker.Substring(0, 36);
                    if (Guid.TryParse(candidate, out var guid))
                        return guid.ToString().ToLowerInvariant();
                }
            }

            // Fallback: split by underscore and find GUIDs, take the second-to-last one
            var segments = fileId.Split('_');
            for (int i = segments.Length - 2; i >= 0; i--)
            {
                if (Guid.TryParse(segments[i], out var guid))
                    return guid.ToString().ToLowerInvariant();
            }

            return null;
        }

        // -----------------------------------------------------------------------
        // PowerShell script tracking handlers
        // -----------------------------------------------------------------------

        private ScriptExecutionState GetCurrentScript(Dictionary<string, string> parameters)
        {
            var scriptType = parameters != null && parameters.TryGetValue("scriptType", out var st) ? st : null;
            return string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase)
                ? _currentRemediationScript
                : GetCurrentPlatformScript();
        }

        private ScriptExecutionState GetCurrentPlatformScript()
        {
            if (!string.IsNullOrEmpty(_lastPlatformScriptPolicyId) &&
                _pendingPlatformScripts.TryGetValue(_lastPlatformScriptPolicyId, out var state))
                return state;
            return null;
        }

        private void HandleScriptStarted(Match match, Dictionary<string, string> parameters)
        {
            var id = match.Groups["id"]?.Value;
            var scriptType = parameters != null && parameters.TryGetValue("scriptType", out var st) ? st : "platform";
            var source = parameters != null && parameters.TryGetValue("source", out var src) ? src : null;

            if (string.IsNullOrEmpty(id))
            {
                _logger.Debug("ImeLogTracker: scriptStarted with no policyId captured, skipping");
                return;
            }

            if (string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase))
            {
                // Flush any pending remediation script (shouldn't happen normally, but defensive)
                if (_currentRemediationScript != null)
                {
                    _logger.Debug($"ImeLogTracker: flushing pending remediation script {_currentRemediationScript.PolicyId}");
                    EmitScriptEvent(_currentRemediationScript);
                }

                _currentRemediationScript = new ScriptExecutionState
                {
                    PolicyId = id,
                    ScriptType = "remediation",
                    ScriptPart = "detection" // detection runs first; updated if remediation runs
                };
                _logger.Info($"ImeLogTracker: remediation script started: {id}");
            }
            else
            {
                // Platform script: AgentExecutor.log entries create/enrich, IME log entries also create
                if (!_pendingPlatformScripts.ContainsKey(id))
                {
                    _pendingPlatformScripts[id] = new ScriptExecutionState
                    {
                        PolicyId = id,
                        ScriptType = "platform"
                    };
                }
                _lastPlatformScriptPolicyId = id;
                _logger.Info($"ImeLogTracker: platform script started: {id} (source: {source ?? "ime"})");
            }
        }

        private void HandleScriptContext(Match match, Dictionary<string, string> parameters)
        {
            var context = match.Groups["context"]?.Value;
            if (string.IsNullOrEmpty(context)) return;

            var runContext = string.Equals(context, "machine", StringComparison.OrdinalIgnoreCase) ? "System" : "User";
            var script = GetCurrentScript(parameters);
            if (script != null)
            {
                script.RunContext = runContext;
                _logger.Debug($"ImeLogTracker: script context set to {runContext} for {script.PolicyId}");
            }
        }

        private void HandleScriptExitCode(Match match, Dictionary<string, string> parameters)
        {
            var exitCodeStr = match.Groups["exitCode"]?.Value;
            if (string.IsNullOrEmpty(exitCodeStr) || !int.TryParse(exitCodeStr, out var exitCode)) return;

            var script = GetCurrentScript(parameters);
            if (script != null)
            {
                script.ExitCode = exitCode;
                _logger.Debug($"ImeLogTracker: script exit code {exitCode} for {script.PolicyId}");
            }
        }

        private void HandleScriptOutput(Match match, Dictionary<string, string> parameters)
        {
            var outputType = parameters != null && parameters.TryGetValue("outputType", out var ot) ? ot : null;
            var script = GetCurrentScript(parameters);
            if (script == null) return;

            // PS-AGENT-OUTPUT captures both stdout and stderr in one pattern
            var output = match.Groups["output"]?.Value;
            var error = match.Groups["error"]?.Value;

            if (!string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(error))
            {
                // Combined pattern (write output done. output = ..., error = ...)
                script.Stdout = TruncateOutput(output.Trim());
                script.Stderr = TruncateOutput(error.Trim());
            }
            else if (string.Equals(outputType, "stderr", StringComparison.OrdinalIgnoreCase))
            {
                script.Stderr = TruncateOutput(output?.Trim());
            }
            else
            {
                script.Stdout = TruncateOutput(output?.Trim());
            }

            _logger.Debug($"ImeLogTracker: script output captured for {script.PolicyId} (type: {outputType ?? "combined"})");
        }

        private void HandleScriptCompleted(Match match, Dictionary<string, string> parameters)
        {
            var scriptType = parameters != null && parameters.TryGetValue("scriptType", out var st) ? st : "platform";
            var part = parameters != null && parameters.TryGetValue("part", out var p) ? p : null;

            if (string.Equals(scriptType, "remediation", StringComparison.OrdinalIgnoreCase))
            {
                if (_currentRemediationScript == null) return;

                var compliance = match.Groups["compliance"]?.Value;
                var id = match.Groups["id"]?.Value;

                _currentRemediationScript.ComplianceResult = compliance;
                if (!string.IsNullOrEmpty(part))
                    _currentRemediationScript.ScriptPart = part;
                if (!string.IsNullOrEmpty(id))
                    _currentRemediationScript.PolicyId = id; // confirm policyId from compliance line

                _logger.Info($"ImeLogTracker: remediation detection completed: {_currentRemediationScript.PolicyId}, " +
                    $"compliance={compliance}, exit={_currentRemediationScript.ExitCode}");

                EmitScriptEvent(_currentRemediationScript);
                _currentRemediationScript = null;
            }
            else
            {
                // Platform script: final result from IntuneManagementExtension.log
                var id = match.Groups["id"]?.Value;
                var result = match.Groups["result"]?.Value;

                if (string.IsNullOrEmpty(id)) return;

                // Merge with pending AgentExecutor data if available
                if (!_pendingPlatformScripts.TryGetValue(id, out var script))
                {
                    script = new ScriptExecutionState
                    {
                        PolicyId = id,
                        ScriptType = "platform"
                    };
                }

                script.Result = result;

                _logger.Info($"ImeLogTracker: platform script completed: {id}, result={result}, exit={script.ExitCode}");

                EmitScriptEvent(script);
                _pendingPlatformScripts.Remove(id);
                if (string.Equals(_lastPlatformScriptPolicyId, id, StringComparison.OrdinalIgnoreCase))
                    _lastPlatformScriptPolicyId = null;
            }
        }

        private void EmitScriptEvent(ScriptExecutionState script)
        {
            if (script == null || string.IsNullOrEmpty(script.PolicyId)) return;
            OnScriptCompleted?.Invoke(script);
        }

        private static string TruncateOutput(string output)
        {
            if (string.IsNullOrEmpty(output) || output.Length <= MaxScriptOutputLength)
                return output;
            return output.Substring(0, MaxScriptOutputLength) + "...[truncated]";
        }

        // -----------------------------------------------------------------------
        // IME lifecycle handlers
        // -----------------------------------------------------------------------

        private void HandleImeStarted()
        {
            _logger.Info("ImeLogTracker: IME Agent Started detected");

            // Log any currently active app — it will be re-evaluated by new log entries after IME restart.
            // We intentionally do NOT mark it as Installed here because IME may retry the app.
            if (!string.IsNullOrEmpty(_packageStates.CurrentPackageId))
            {
                var currentPkg = _packageStates.GetPackage(_packageStates.CurrentPackageId);
                if (currentPkg?.IsActive == true)
                    _logger.Info($"ImeLogTracker: Active package {currentPkg.Name ?? currentPkg.Id} ({currentPkg.InstallationState}) will be re-evaluated after IME restart");
            }

            OnImeStarted?.Invoke();
        }

        private void HandleImeShutdown()
        {
            _logger.Info("ImeLogTracker: IME shutdown detected — marking all active packages as Postponed");

            foreach (var pkg in _packageStates.Where(p => p.IsActive).ToList())
            {
                _logger.Info($"ImeLogTracker: Postponing active package {pkg.Name ?? pkg.Id} ({pkg.InstallationState})");
                UpdateStateWithCallback(pkg.Id, AppInstallationState.Postponed);
            }
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

                    // Snapshot package states from the completed phase before clearing.
                    // We hold the actual AppPackageState references (cheap; List<T>.Clear() on
                    // _packageStates does not destroy them) so the termination summary path
                    // can iterate per-phase apps with their full typed state — see
                    // GetAllKnownPackageStates() in the partial Core class.
                    if (_packageStates.CountAll > 0)
                    {
                        _phasePackageSnapshots[_lastEspPhaseDetected] =
                            new List<AppPackageState>(_packageStates);
                        _logger.Info($"ImeLogTracker: Snapshotted {_packageStates.CountAll} package states from {_lastEspPhaseDetected} phase");
                    }

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

            var pkg = _packageStates.GetPackage(id);
            var label = pkg?.Name ?? id;

            if (string.Equals(to, "InProgress", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"ImeLogTracker: ESP track status {label}: {from} -> InProgress");
                _packageStates.SetCurrent(id);
                UpdateStateWithCallback(id, AppInstallationState.Installing);
            }
            else if (string.Equals(to, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"ImeLogTracker: ESP track status {label}: {from} -> Completed");
                UpdateStateWithCallback(id, AppInstallationState.Installed);
            }
            else if (string.Equals(to, "Error", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Info($"ImeLogTracker: ESP track status {label}: {from} -> Error");
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
                _logger.Info($"ImeLogTracker: cancelling stuck app {currentPkg.Name ?? _packageStates.CurrentPackageId} ({currentPkg.InstallationState}), switching to {newId}");
                UpdateStateWithCallback(_packageStates.CurrentPackageId, AppInstallationState.Skipped);
            }

            _packageStates.SetCurrent(newId);
        }

        private void UpdateStateWithCallback(string id, AppInstallationState newState, int? progressPercent = null, string errorPatternId = null, string errorDetail = null, string errorCode = null)
        {
            var pkg = _packageStates.GetPackage(id);
            if (pkg == null) return;

            // Set error context before state change so it's available in ToEventData()
            if (newState == AppInstallationState.Error && !string.IsNullOrEmpty(errorPatternId))
                pkg.SetErrorContext(errorPatternId, errorDetail, errorCode);

            var oldState = pkg.InstallationState;
            var changed = _packageStates.UpdateState(id, newState, progressPercent);

            if (changed)
            {
                _logger.Info($"ImeLogTracker: {pkg.Name ?? id} state: {oldState} -> {newState}");
                OnAppStateChanged?.Invoke(pkg, oldState, newState);

                // Check if all apps are now completed - only fire once
                if (!_allAppsCompletedFired && _packageStates.CountAll > 0 && _packageStates.IsAllCompleted())
                {
                    _allAppsCompletedFired = true;
                    _logger.Info($"ImeLogTracker: all {_packageStates.CountAll} apps completed");
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

