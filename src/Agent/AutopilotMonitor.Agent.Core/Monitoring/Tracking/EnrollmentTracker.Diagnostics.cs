using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Tracking
{
    /// <summary>
    /// Partial: Device info collection at key lifecycle points, app tracking summaries,
    /// debug state timer, and final status file writing.
    /// </summary>
    public partial class EnrollmentTracker
    {
        private void CollectDeviceInfoAtEnrollmentStart()
        {
            lock (_stateLock)
            {
                if (_enrollmentStartDeviceInfoCollected)
                    return;
                _enrollmentStartDeviceInfoCollected = true;
            }

            if (!_isBootstrapMode)
            {
                _logger.Debug("EnrollmentTracker: skipping enrollment-start device info re-collection (not bootstrap mode)");
                return;
            }

            _logger.Info("EnrollmentTracker: first ESP phase detected — re-collecting enrollment-dependent device info (bootstrap mode)");
            Task.Run(() =>
            {
                try
                {
                    _deviceInfoCollector.CollectAtEnrollmentStart();
                }
                catch (Exception ex)
                {
                    _logger.Warning($"EnrollmentTracker: failed to re-collect device info at enrollment start: {ex.Message}");
                }
            });
        }

        private void CollectDeviceInfoAtFinalizingSetup(string triggerReason)
        {
            bool alreadyCollected;
            lock (_stateLock)
            {
                alreadyCollected = _finalDeviceInfoCollected;
                if (!alreadyCollected)
                    _finalDeviceInfoCollected = true;
            }

            if (alreadyCollected)
            {
                _logger.Debug($"EnrollmentTracker: final device info already collected, skipping (trigger: {triggerReason})");
                return;
            }
            _logger.Info($"EnrollmentTracker: triggering final device info collection at FinalizingSetup (trigger: {triggerReason})");
            CollectDeviceInfoAtEnd();
        }

        private void SummaryTimerCallback(object state)
        {
            // Only emit if state-breakdown counters changed since last emission (backstop for missed events)
            EmitAppTrackingSummaryIfChanged();

            // Signal-correlated WhiteGlove detection (Ebene 2.6) — event-driven in the sense
            // that it only triggers when the observed signal set matches; zero-cost when the
            // session does not look like a sealing candidate.
            try { CheckSignalCorrelatedWhiteGlove(); }
            catch (Exception ex) { _logger.Error("CheckSignalCorrelatedWhiteGlove failed", ex); }

            // Periodic state save (piggybacks on the existing 30s timer)
            bool dirty;
            EnrollmentStateData stateSnapshot;
            lock (_stateLock)
            {
                dirty = _stateDirty;
                if (dirty)
                {
                    _stateDirty = false;
                    stateSnapshot = _stateData;
                }
                else
                {
                    stateSnapshot = null;
                }
            }

            if (dirty)
                _statePersistence.Save(stateSnapshot);
        }

        /// <summary>
        /// Periodic debug state snapshot (every 60s). Writes to agent log only (not as event) to aid
        /// post-mortem debugging of stuck enrollments without consuming Azure Table Storage.
        /// </summary>
        private void DebugStateTimerCallback(object state)
        {
            try
            {
                string lastPhaseDbg;
                bool espFinalExitDbg, desktopDbg;
                lock (_stateLock)
                {
                    lastPhaseDbg = _lastEspPhase;
                    espFinalExitDbg = _espFinalExitSeen;
                    desktopDbg = _desktopArrived;
                }

                var states = _imeLogTracker?.PackageStates;
                if (states == null || states.CountAll == 0) return;

                var active = states.Where(x => x.IsActive).Select(x => $"{x.Name ?? x.Id}({x.InstallationState})");
                var errors = states.Where(x => x.IsError).Select(x => $"{x.Name ?? x.Id}");

                _logger.Debug($"[StateSnapshot] Apps: {states.CountCompleted}/{states.CountAll} completed, " +
                    $"{states.ErrorCount} errors (device:{states.Count(x => x.IsError && x.Targeted == AppTargeted.Device)}, " +
                    $"user:{states.Count(x => x.IsError && x.Targeted == AppTargeted.User)}), " +
                    $"active: [{string.Join(", ", active)}], " +
                    $"failed: [{string.Join(", ", errors)}], " +
                    $"phase: {lastPhaseDbg ?? "none"}, espExit: {espFinalExitDbg}, desktop: {desktopDbg}");
            }
            catch (Exception ex)
            {
                _logger.Debug($"[StateSnapshot] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Emits app_tracking_summary only if state-breakdown counters changed since last emission.
        /// Called both event-driven (from HandleAppStateChanged) and periodically (30s timer backstop).
        /// </summary>
        private void EmitAppTrackingSummaryIfChanged()
        {
            var states = _imeLogTracker?.PackageStates;
            if (states == null || states.CountAll == 0) return;

            var hash = GetSummaryHash(states);

            EventSeverity severity;
            lock (_stateLock)
            {
                if (hash == _lastEmittedSummaryHash) return;
                _lastEmittedSummaryHash = hash;

                var compactHash = $"{states.CountCompleted}_{states.ErrorCount}";
                severity = (compactHash != _lastEmittedSummaryCompactHash)
                    ? (states.HasError ? EventSeverity.Warning : EventSeverity.Info)
                    : EventSeverity.Debug;
                _lastEmittedSummaryCompactHash = compactHash;
            }

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "app_tracking_summary",
                Severity = severity,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"App tracking: {states.CountCompleted}/{states.CountAll} completed" +
                          (states.HasError ? $" ({states.ErrorCount} errors)" : ""),
                Data = states.GetSummaryData(),
                ImmediateUpload = (severity != EventSeverity.Debug)
            });
        }

        /// <summary>
        /// Emits app_tracking_summary unconditionally (for final summary on completion).
        /// </summary>
        private void EmitAppTrackingSummary()
        {
            var states = _imeLogTracker?.PackageStates;
            if (states == null || states.CountAll == 0) return;

            lock (_stateLock) { _lastEmittedSummaryHash = GetSummaryHash(states); }

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _sessionId,
                TenantId = _tenantId,
                EventType = "app_tracking_summary",
                Severity = states.HasError ? EventSeverity.Warning : EventSeverity.Info,
                Source = "EnrollmentTracker",
                Phase = EnrollmentPhase.Unknown,
                Message = $"App tracking: {states.CountCompleted}/{states.CountAll} completed" +
                          (states.HasError ? $" ({states.ErrorCount} errors)" : ""),
                Data = states.GetSummaryData(),
                ImmediateUpload = true
            });
        }

        private static string GetSummaryHash(AppPackageStateList states)
        {
            return $"{states.CountAll}_{states.CountCompleted}_{states.ErrorCount}_" +
                   $"{states.Count(x => x.InstallationState == AppInstallationState.Downloading)}_" +
                   $"{states.Count(x => x.InstallationState == AppInstallationState.Installing)}_" +
                   $"{states.Count(x => x.InstallationState == AppInstallationState.Installed)}";
        }

        public void Dispose()
        {
            Stop();

            // Unsubscribe from EspAndHelloTracker events
            if (_espAndHelloTracker != null)
            {
                _espAndHelloTracker.HelloCompleted -= OnHelloCompleted;
                _espAndHelloTracker.FinalizingSetupPhaseTriggered -= OnFinalizingSetupPhaseTriggered;
                _espAndHelloTracker.WhiteGloveCompleted -= OnWhiteGloveCompleted;
                _espAndHelloTracker.EspFailureDetected -= OnEspFailureDetected;
                _espAndHelloTracker.DeviceSetupProvisioningComplete -= OnDeviceSetupProvisioningComplete;
            }

            _espFailureTimer?.Dispose();
            _deviceOnlyEspTimer?.Dispose();
            _waitingForHelloSafetyTimer?.Dispose();
            _waitingForEspSettleTimer?.Dispose();
            _deviceOnlyCompletionSafetyTimer?.Dispose();
            _summaryTimer?.Dispose();
            _debugStateTimer?.Dispose();
            _imeLogTracker?.Dispose();
        }

        /// <summary>
        /// Writes an enrollment complete marker to the state directory.
        /// This marker is checked on agent restart to handle cleanup retry if scheduled task fails.
        /// </summary>
        private void WriteEnrollmentCompleteMarker()
        {
            try
            {
                var stateDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\State");
                Directory.CreateDirectory(stateDirectory);

                var markerPath = Path.Combine(stateDirectory, "enrollment-complete.marker");
                File.WriteAllText(markerPath, $"Enrollment completed at {DateTime.UtcNow:O}");

                _logger.Info($"Enrollment complete marker written: {markerPath}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to write enrollment complete marker: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a comprehensive final-status.json with all app states, signals, and error summary.
        /// Automatically included in diagnostics uploads for post-mortem analysis.
        /// </summary>
        private void WriteFinalStatus(string outcome, string source)
        {
            try
            {
                var stateDirectory = Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\State");
                Directory.CreateDirectory(stateDirectory);

                var appStates = _imeLogTracker?.PackageStates;

                var signalTimestamps = new Dictionary<string, string>();
                if (_stateData.EspFirstSeenUtc.HasValue)
                    signalTimestamps["espFirstSeen"] = _stateData.EspFirstSeenUtc.Value.ToString("o");
                if (_stateData.EspFinalExitUtc.HasValue)
                    signalTimestamps["espFinalExit"] = _stateData.EspFinalExitUtc.Value.ToString("o");
                if (_stateData.DesktopArrivedUtc.HasValue)
                    signalTimestamps["desktopArrived"] = _stateData.DesktopArrivedUtc.Value.ToString("o");
                if (_stateData.HelloResolvedUtc.HasValue)
                    signalTimestamps["helloResolved"] = _stateData.HelloResolvedUtc.Value.ToString("o");
                if (_stateData.ImePatternSeenUtc.HasValue)
                    signalTimestamps["imePatternSeen"] = _stateData.ImePatternSeenUtc.Value.ToString("o");
                if (_stateData.DeviceSetupProvisioningCompleteUtc.HasValue)
                    signalTimestamps["deviceSetupProvisioningComplete"] = _stateData.DeviceSetupProvisioningCompleteUtc.Value.ToString("o");

                // Build per-phase package states: snapshots from completed phases + current phase
                var phaseSnapshots = _imeLogTracker?.PhasePackageSnapshots;
                var packageStatesByPhase = new Dictionary<string, object>();

                // Add snapshots from completed phases (e.g. DeviceSetup apps captured before phase transition)
                if (phaseSnapshots != null)
                {
                    foreach (var kvp in phaseSnapshots)
                        packageStatesByPhase[kvp.Key] = kvp.Value;
                }

                // Add current phase (the phase active at completion, e.g. AccountSetup)
                var currentPhaseName = _lastEspPhase ?? "Unknown";
                var currentPhaseList = appStates?.ToFinalStatusList() ?? new List<Dictionary<string, object>>();
                packageStatesByPhase[currentPhaseName] = currentPhaseList;

                // Aggregate app summary across ALL phases (snapshots + current)
                var currentPhaseTotal = appStates?.CountAll ?? 0;
                var currentPhaseCompleted = appStates?.CountCompleted ?? 0;
                var currentPhaseErrors = appStates?.ErrorCount ?? 0;
                var currentPhaseDeviceErrors = appStates?.Count(x => x.IsError && x.Targeted == AppTargeted.Device) ?? 0;
                var currentPhaseUserErrors = appStates?.Count(x => x.IsError && x.Targeted == AppTargeted.User) ?? 0;

                var allPhaseTotals = new Dictionary<string, int>();
                var totalAppsAllPhases = currentPhaseTotal;
                var completedAppsAllPhases = currentPhaseCompleted;
                var errorCountAllPhases = currentPhaseErrors;
                var deviceErrorsAllPhases = currentPhaseDeviceErrors;
                var userErrorsAllPhases = currentPhaseUserErrors;

                allPhaseTotals[currentPhaseName] = currentPhaseTotal;

                if (phaseSnapshots != null)
                {
                    foreach (var kvp in phaseSnapshots)
                    {
                        var phaseApps = kvp.Value;
                        allPhaseTotals[kvp.Key] = phaseApps.Count;
                        totalAppsAllPhases += phaseApps.Count;
                        foreach (var app in phaseApps)
                        {
                            object stateVal;
                            var isError = app.TryGetValue("state", out stateVal) &&
                                          string.Equals(stateVal?.ToString(), "Error", StringComparison.OrdinalIgnoreCase);
                            var isCompleted = app.TryGetValue("state", out stateVal) &&
                                              (stateVal?.ToString() == "Installed" || stateVal?.ToString() == "Skipped" ||
                                               stateVal?.ToString() == "Postponed" || stateVal?.ToString() == "Error");
                            if (isCompleted) completedAppsAllPhases++;
                            if (isError)
                            {
                                errorCountAllPhases++;
                                object targetVal;
                                if (app.TryGetValue("targeted", out targetVal))
                                {
                                    if (string.Equals(targetVal?.ToString(), "Device", StringComparison.OrdinalIgnoreCase))
                                        deviceErrorsAllPhases++;
                                    else if (string.Equals(targetVal?.ToString(), "User", StringComparison.OrdinalIgnoreCase))
                                        userErrorsAllPhases++;
                                }
                            }
                        }
                    }
                }

                var status = new Dictionary<string, object>
                {
                    { "timestamp", DateTime.UtcNow.ToString("o") },
                    { "outcome", outcome },
                    { "completionSource", source },
                    { "helloOutcome", _espAndHelloTracker?.HelloOutcome ?? "unknown" },
                    { "enrollmentType", _enrollmentType ?? "unknown" },
                    { "agentUptimeSeconds", (DateTime.UtcNow - _agentStartTimeUtc).TotalSeconds },
                    { "signalsSeen", SnapshotSignalsSeen() },
                    { "signalTimestamps", signalTimestamps },
                    { "appSummary", new Dictionary<string, object>
                        {
                            { "totalApps", totalAppsAllPhases },
                            { "completedApps", completedAppsAllPhases },
                            { "errorCount", errorCountAllPhases },
                            { "deviceErrors", deviceErrorsAllPhases },
                            { "userErrors", userErrorsAllPhases },
                            { "appsByPhase", allPhaseTotals }
                        }
                    },
                    { "packageStatesByPhase", packageStatesByPhase }
                };

                var json = System.Text.Json.JsonSerializer.Serialize(status,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var path = Path.Combine(stateDirectory, "final-status.json");
                File.WriteAllText(path, json);
                _logger.Info($"Final status written: {path}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to write final status: {ex.Message}");
            }
        }
    }
}

