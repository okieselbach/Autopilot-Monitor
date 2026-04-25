using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Orchestrates peripheral termination work in response to
    /// <see cref="EnrollmentOrchestrator.Terminated"/>. Plan §4.x M4.6.β.
    /// <para>
    /// <b>Sequence</b> (V1 parity — single-writer, best-effort each step, never throws):
    /// </para>
    /// <list type="number">
    ///   <item>Run shutdown analyzers (optional) so their delta events land before diagnostics.</item>
    ///   <item>Compose <see cref="FinalStatus"/> via <see cref="FinalStatusBuilder"/>.</item>
    ///   <item>Write final-status.json + launch <see cref="SummaryDialogLauncher"/> if configured.</item>
    ///   <item>Emit <c>enrollment_summary_shown</c> event once the dialog has been handed to the user session.</item>
    ///   <item><see cref="Task.Delay(int)"/> 2s grace so late events can land before the next step.</item>
    ///   <item>Emit <c>diagnostics_collecting</c> → upload diagnostics → emit <c>diagnostics_uploaded</c> / <c>diagnostics_upload_failed</c>.</item>
    ///   <item>Write <c>enrollment-complete.marker</c> (ghost-restart guard on next boot).</item>
    ///   <item>Standalone-reboot path: if <c>RebootOnComplete &amp;&amp; !SelfDestructOnComplete</c> emit
    ///     <c>reboot_triggered</c>, drain spool, call <c>shutdown.exe /r /t &lt;delay&gt;</c>.</item>
    ///   <item>Run <see cref="CleanupService.ExecuteSelfDestruct"/> — UNLESS the stage is
    ///     <see cref="SessionStage.WhiteGloveSealed"/> (Part-1 exit, session resumes Part 2).</item>
    ///   <item>WhiteGlove Part-1 path: emit <c>whiteglove_part1_complete</c>, drain spool, and
    ///     write <c>whiteglove.complete</c> marker via <see cref="SessionIdPersistence.SaveWhiteGloveComplete"/>
    ///     so Part-2 resume is detected on the next boot.</item>
    ///   <item>Signal the caller-owned shutdown <see cref="ManualResetEventSlim"/>.</item>
    /// </list>
    /// <para>
    /// Each step logs + continues on failure — nothing in here is allowed to prevent the agent
    /// from shutting down. <see cref="CleanupService"/> is fire-and-forget (spawns a PowerShell
    /// cleanup script that waits for process exit); this handler does not block waiting for it.
    /// </para>
    /// </summary>
    public sealed class EnrollmentTerminationHandler
    {
        private static readonly TimeSpan DefaultLateEventGrace = TimeSpan.FromMilliseconds(2000);
        private static readonly TimeSpan DefaultSpoolDrain = TimeSpan.FromMilliseconds(10000); // V1 parity: up to 20 × 500ms drains before shutdown.exe

        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly string _stateDirectory;
        private readonly DateTime _agentStartTimeUtc;
        private readonly Func<DecisionState> _currentStateAccessor;
        // F5 (debrief 7dd4e593) — accept any IReadOnlyList<AppPackageState> so the
        // termination summary can iterate the union of phase-snapshotted apps + live
        // _packageStates (V2's clear-on-phase-transition would otherwise drop the
        // DeviceSetup apps from app_tracking_summary and the SummaryDialog).
        private readonly Func<IReadOnlyList<AppPackageState>> _packageStatesAccessor;
        private readonly Func<IReadOnlyDictionary<string, AppInstallTiming>> _appTimingsAccessor;
        private readonly Func<CleanupService> _cleanupServiceFactory;
        private readonly Func<bool, string, Task<DiagnosticsUploadResult>> _uploadDiagnosticsAsync;
        private readonly Action _signalShutdown;
        private readonly string _dialogExePathOverride;
        private readonly AgentAnalyzerManager _analyzerManager;
        private readonly InformationalEventPost _post;
        private readonly SessionIdPersistence _sessionPersistence;
        private readonly Action<int> _triggerReboot;
        private readonly TimeSpan _lateEventGracePeriod;
        private readonly TimeSpan _spoolDrainPeriod;

        private int _handled;

        public EnrollmentTerminationHandler(
            AgentConfiguration configuration,
            AgentLogger logger,
            string stateDirectory,
            DateTime agentStartTimeUtc,
            Func<DecisionState> currentStateAccessor,
            Func<IReadOnlyList<AppPackageState>> packageStatesAccessor,
            Func<CleanupService> cleanupServiceFactory,
            Func<bool, string, Task<DiagnosticsUploadResult>> uploadDiagnosticsAsync,
            Action signalShutdown,
            string dialogExePathOverride = null,
            AgentAnalyzerManager analyzerManager = null,
            InformationalEventPost post = null,
            SessionIdPersistence sessionPersistence = null,
            Action<int> triggerReboot = null,
            TimeSpan? lateEventGracePeriod = null,
            TimeSpan? spoolDrainPeriod = null,
            Func<IReadOnlyDictionary<string, AppInstallTiming>> appTimingsAccessor = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _stateDirectory = string.IsNullOrEmpty(stateDirectory) ? throw new ArgumentNullException(nameof(stateDirectory)) : stateDirectory;
            _agentStartTimeUtc = agentStartTimeUtc;
            _currentStateAccessor = currentStateAccessor ?? throw new ArgumentNullException(nameof(currentStateAccessor));
            _packageStatesAccessor = packageStatesAccessor ?? throw new ArgumentNullException(nameof(packageStatesAccessor));
            // Plan §5 Fix 4 — optional timing accessor (older call sites / tests without IME
            // plumbing pass null → empty timings, which is handled below).
            _appTimingsAccessor = appTimingsAccessor ?? (() => new Dictionary<string, AppInstallTiming>());
            _cleanupServiceFactory = cleanupServiceFactory ?? throw new ArgumentNullException(nameof(cleanupServiceFactory));
            _uploadDiagnosticsAsync = uploadDiagnosticsAsync ?? throw new ArgumentNullException(nameof(uploadDiagnosticsAsync));
            _signalShutdown = signalShutdown ?? throw new ArgumentNullException(nameof(signalShutdown));
            _dialogExePathOverride = dialogExePathOverride;
            _analyzerManager = analyzerManager;
            _post = post;
            _sessionPersistence = sessionPersistence;
            _triggerReboot = triggerReboot ?? DefaultTriggerReboot;
            _lateEventGracePeriod = lateEventGracePeriod ?? DefaultLateEventGrace;
            _spoolDrainPeriod = spoolDrainPeriod ?? DefaultSpoolDrain;
        }

        /// <summary>
        /// Handler for <see cref="EnrollmentOrchestrator.Terminated"/>. Idempotent — runs at most once.
        /// </summary>
        public void Handle(object sender, EnrollmentTerminatedEventArgs args)
        {
            if (Interlocked.Exchange(ref _handled, 1) == 1) return;

            var isWhiteGlovePart1 = args.StageName == SessionStage.WhiteGloveSealed.ToString();

            try
            {
                _logger.Info(
                    $"EnrollmentTerminationHandler: handling Terminated (reason={args.Reason}, outcome={args.Outcome}, stage={args.StageName}).");

                var state = TryGetCurrentState();

                // M4.6.δ — shutdown analyzers run BEFORE the dialog / diagnostics upload so
                // their delta events make it into the final diagnostics ZIP. The
                // AnalyzerManager is optional (null in tests where analyzers are out-of-scope).
                RunShutdownAnalyzers(args);

                if (state == null)
                {
                    _logger.Warning("EnrollmentTerminationHandler: current state unavailable — skipping FinalStatus + SummaryDialog.");
                }
                else
                {
                    RunBuildAndLaunchDialog(state, args);
                }

                // Plan §5 Fix 4b — emit app_tracking_summary terminal event before the
                // late-event grace + diagnostics upload, so the backend has the final per-app
                // summary even if the diagnostics upload fails. Skipped on WhiteGlove Part 1
                // (apps haven't installed yet — Part 2 is where user apps land).
                if (!isWhiteGlovePart1)
                {
                    EmitAppTrackingSummary();
                }

                // WhiteGlove Part-1 exit: keep the session alive, but announce the handoff so
                // the timeline clearly marks the transition. The `whiteglove.complete` marker
                // lets the next agent boot classify itself as a Part-2 resume.
                if (isWhiteGlovePart1)
                {
                    EmitEventSafe(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = "whiteglove_part1_complete",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTerminationHandler",
                        Phase = EnrollmentPhase.Unknown,
                        Message = "WhiteGlove Part 1 complete — device will seal for end-user.",
                        ImmediateUpload = true,
                    });

                    DelayLateEventGrace();
                    DrainSpool();

                    TrySaveWhiteGloveComplete();
                    return;
                }

                DelayLateEventGrace();

                RunUploadDiagnosticsWithEvents(args);
                WriteEnrollmentCompleteMarker(args);
                RunStandaloneRebootIfRequested();
                RunSelfDestructIfAppropriate(args);
            }
            catch (Exception ex)
            {
                _logger.Error("EnrollmentTerminationHandler: unhandled exception during termination sequence.", ex);
            }
            finally
            {
                try { _signalShutdown(); }
                catch (Exception ex) { _logger.Warning($"EnrollmentTerminationHandler: signalShutdown threw: {ex.Message}"); }
            }
        }

        private DecisionState TryGetCurrentState()
        {
            try { return _currentStateAccessor(); }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: current state accessor threw: {ex.Message}");
                return null;
            }
        }

        private IReadOnlyList<AppPackageState> TryGetPackageStates()
        {
            try { return _packageStatesAccessor(); }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: package states accessor threw: {ex.Message}");
                return null;
            }
        }

        private IReadOnlyDictionary<string, AppInstallTiming> TryGetAppTimings()
        {
            try { return _appTimingsAccessor() ?? new Dictionary<string, AppInstallTiming>(); }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: app timings accessor threw: {ex.Message}");
                return new Dictionary<string, AppInstallTiming>();
            }
        }

        private void RunShutdownAnalyzers(EnrollmentTerminatedEventArgs args)
        {
            if (_analyzerManager == null) return;
            try
            {
                // WhiteGlove Part 1 exit passes whiteGlovePart=1 so SoftwareInventoryAnalyzer
                // takes a baseline snapshot rather than computing a pre/post delta (the user
                // sign-in phase has not run yet; the real delta computes on Part-2 completion).
                int? wgPart = args.StageName == SessionStage.WhiteGloveSealed.ToString() ? 1 : (int?)null;
                _analyzerManager.RunShutdown(wgPart);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: analyzer shutdown threw: {ex.Message}");
            }
        }

        private void RunBuildAndLaunchDialog(DecisionState state, EnrollmentTerminatedEventArgs args)
        {
            try
            {
                var packages = TryGetPackageStates();
                var timings = TryGetAppTimings();
                var status = FinalStatusBuilder.Build(state, args, packages, _agentStartTimeUtc, timings);
                SummaryDialogLauncher.WriteAndLaunch(status, _configuration, _stateDirectory, _logger, _dialogExePathOverride);

                if (_configuration.ShowEnrollmentSummary)
                {
                    EmitEventSafe(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        EventType = "enrollment_summary_shown",
                        Severity = EventSeverity.Info,
                        Source = "EnrollmentTerminationHandler",
                        Phase = EnrollmentPhase.Unknown,
                        Message = "Enrollment summary dialog shown to user.",
                        Data = new Dictionary<string, object>
                        {
                            { "totalApps", status?.AppSummary?.TotalApps ?? 0 },
                            { "errorCount", status?.AppSummary?.ErrorCount ?? 0 },
                            { "outcome", status?.Outcome ?? string.Empty },
                            { "timeoutSeconds", _configuration.EnrollmentSummaryTimeoutSeconds },
                        },
                        ImmediateUpload = true,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: FinalStatus/SummaryDialog step failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Plan §5 Fix 4b — emit a single <c>app_tracking_summary</c> event at termination time
        /// with aggregate counts, per-phase breakdown, and per-app install-lifecycle timing.
        /// Consumed by the Web UI's <c>useSessionDerivedData</c> hook to build authoritative
        /// per-app durations (matching V1's <c>EnrollmentTracker.Diagnostics.cs:164</c> behaviour).
        /// Best-effort: if any accessor throws, we log a warning and skip — the dialog has
        /// already written <c>final-status.json</c> at this point, so the summary event is
        /// supplementary observability, not load-bearing.
        /// </summary>
        private void EmitAppTrackingSummary()
        {
            if (_post == null)
            {
                // No informational event post (constructed in tests without wiring) — skip.
                return;
            }

            try
            {
                var packages = TryGetPackageStates();
                var timings = TryGetAppTimings();

                var totalApps = 0;
                var installedApps = 0;
                var skippedApps = 0;
                var postponedApps = 0;
                var failedApps = 0;
                var byPhase = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                var perApp = new List<Dictionary<string, object>>();

                if (packages != null)
                {
                    foreach (var pkg in packages)
                    {
                        totalApps++;
                        switch (pkg.InstallationState)
                        {
                            case AppInstallationState.Installed: installedApps++; break;
                            case AppInstallationState.Skipped: skippedApps++; break;
                            case AppInstallationState.Postponed: postponedApps++; break;
                            case AppInstallationState.Error: failedApps++; break;
                        }

                        var phaseKey = pkg.Targeted.ToString();
                        if (!byPhase.TryGetValue(phaseKey, out var bucket))
                        {
                            bucket = new Dictionary<string, int>(StringComparer.Ordinal)
                            {
                                ["total"] = 0, ["installed"] = 0, ["skipped"] = 0, ["postponed"] = 0, ["failed"] = 0,
                            };
                            byPhase[phaseKey] = bucket;
                        }
                        bucket["total"]++;
                        if (pkg.InstallationState == AppInstallationState.Installed) bucket["installed"]++;
                        else if (pkg.InstallationState == AppInstallationState.Skipped) bucket["skipped"]++;
                        else if (pkg.InstallationState == AppInstallationState.Postponed) bucket["postponed"]++;
                        else if (pkg.InstallationState == AppInstallationState.Error) bucket["failed"]++;

                        timings.TryGetValue(pkg.Id, out var timing);
                        var appEntry = new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["appId"] = pkg.Id,
                            ["appName"] = pkg.Name ?? string.Empty,
                            ["phase"] = phaseKey,
                            ["finalState"] = pkg.InstallationState.ToString(),
                        };
                        if (timing?.StartedAtUtc != null) appEntry["startedAt"] = timing.StartedAtUtc.Value.ToString("o");
                        if (timing?.CompletedAtUtc != null) appEntry["completedAt"] = timing.CompletedAtUtc.Value.ToString("o");
                        if (timing?.DurationSeconds != null) appEntry["durationSeconds"] = timing.DurationSeconds.Value;
                        perApp.Add(appEntry);
                    }
                }

                var completedApps = installedApps + skippedApps + postponedApps + failedApps;

                var data = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["totalApps"] = totalApps,
                    ["completedApps"] = completedApps,
                    ["installedApps"] = installedApps,
                    ["skippedApps"] = skippedApps,
                    ["postponedApps"] = postponedApps,
                    ["failedApps"] = failedApps,
                    ["byPhase"] = byPhase,
                    ["perApp"] = perApp,
                };

                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = Constants.EventTypes.AppTrackingSummary,
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTerminationHandler",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"App summary: {completedApps}/{totalApps} completed, {failedApps} failed.",
                    Data = data,
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: app_tracking_summary emit failed: {ex.Message}");
            }
        }

        private void RunUploadDiagnosticsWithEvents(EnrollmentTerminatedEventArgs args)
        {
            var mode = _configuration.DiagnosticsUploadMode ?? "Off";
            if (!_configuration.DiagnosticsUploadEnabled || string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Debug("EnrollmentTerminationHandler: diagnostics upload skipped (disabled or mode=Off).");
                return;
            }

            var enrollmentSucceeded = args.Outcome == EnrollmentTerminationOutcome.Succeeded;
            if (string.Equals(mode, "OnFailure", StringComparison.OrdinalIgnoreCase) && enrollmentSucceeded)
            {
                _logger.Debug("EnrollmentTerminationHandler: diagnostics upload skipped (mode=OnFailure + enrollment succeeded).");
                return;
            }

            EmitEventSafe(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "diagnostics_collecting",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTerminationHandler",
                Phase = EnrollmentPhase.Unknown,
                Message = "Collecting diagnostics package.",
                Data = new Dictionary<string, object>
                {
                    { "mode", mode },
                    { "enrollmentSucceeded", enrollmentSucceeded },
                },
                ImmediateUpload = true,
            });

            DiagnosticsUploadResult result = null;
            try
            {
                var suffix = enrollmentSucceeded ? "success" : "failure";
                result = _uploadDiagnosticsAsync(enrollmentSucceeded, suffix).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: diagnostics upload threw: {ex.Message}");
            }

            if (result != null && result.Success)
            {
                _logger.Info($"EnrollmentTerminationHandler: diagnostics uploaded (blob={result.BlobName}).");
                EmitEventSafe(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = "diagnostics_uploaded",
                    Severity = EventSeverity.Info,
                    Source = "EnrollmentTerminationHandler",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Diagnostics package uploaded ({result.BlobName}).",
                    Data = new Dictionary<string, object>
                    {
                        { "blobName", result.BlobName ?? string.Empty },
                        { "sasUrlPrefix", result.SasUrlPrefix ?? string.Empty },
                    },
                    ImmediateUpload = true,
                });
            }
            else
            {
                var errorCode = result?.ErrorCode ?? "null-result";
                _logger.Warning($"EnrollmentTerminationHandler: diagnostics upload failed: {errorCode}.");
                EmitEventSafe(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    EventType = "diagnostics_upload_failed",
                    Severity = EventSeverity.Warning,
                    Source = "EnrollmentTerminationHandler",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Diagnostics upload failed: {errorCode}.",
                    Data = new Dictionary<string, object>
                    {
                        { "errorCode", errorCode },
                        { "blobName", result?.BlobName ?? string.Empty },
                    },
                    ImmediateUpload = true,
                });
            }
        }

        private void WriteEnrollmentCompleteMarker(EnrollmentTerminatedEventArgs args)
        {
            // On WhiteGlove Part 1 exit we keep the session alive for Part 2 — DO NOT write the
            // marker (ghost-restart detection would fire + destroy the in-flight session state).
            if (args.StageName == SessionStage.WhiteGloveSealed.ToString())
            {
                _logger.Info("EnrollmentTerminationHandler: WhiteGlove Part 1 exit — enrollment-complete marker NOT written.");
                return;
            }

            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var markerPath = Path.Combine(_stateDirectory, "enrollment-complete.marker");
                File.WriteAllText(markerPath,
                    $"Terminated at {args.TerminatedAtUtc:O} (reason={args.Reason}, outcome={args.Outcome}, stage={args.StageName}).");
                _logger.Info($"EnrollmentTerminationHandler: enrollment-complete.marker written to {markerPath}.");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: enrollment-complete.marker write failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V1 parity — standalone-reboot flow. When the tenant config disables self-destruct
        /// but enables <c>RebootOnComplete</c>, the agent's final act is <c>shutdown.exe /r</c>
        /// with the configured delay, giving the user a visible countdown.
        /// </summary>
        private void RunStandaloneRebootIfRequested()
        {
            if (_configuration.SelfDestructOnComplete) return;
            if (!_configuration.RebootOnComplete) return;

            var delay = _configuration.RebootDelaySeconds > 0 ? _configuration.RebootDelaySeconds : 10;

            EmitEventSafe(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "reboot_triggered",
                Severity = EventSeverity.Info,
                Source = "EnrollmentTerminationHandler",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Standalone reboot triggered (delay={delay}s).",
                Data = new Dictionary<string, object>
                {
                    { "rebootDelaySeconds", delay },
                    { "selfDestructOnComplete", _configuration.SelfDestructOnComplete },
                },
                ImmediateUpload = true,
            });

            DrainSpool();

            try
            {
                _triggerReboot(delay);
                _logger.Info($"EnrollmentTerminationHandler: standalone reboot queued via shutdown.exe /r /t {delay}.");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: standalone reboot invocation failed: {ex.Message}");
            }
        }

        private void RunSelfDestructIfAppropriate(EnrollmentTerminatedEventArgs args)
        {
            if (!_configuration.SelfDestructOnComplete)
            {
                _logger.Info("EnrollmentTerminationHandler: SelfDestructOnComplete=false — cleanup skipped.");
                return;
            }

            if (args.StageName == SessionStage.WhiteGloveSealed.ToString())
            {
                _logger.Info("EnrollmentTerminationHandler: WhiteGlove Part 1 exit — cleanup skipped (session resumes on reboot).");
                return;
            }

            // Plan §6.2 terminate-hygiene — post an explicit agent_shutting_down acknowledgement BEFORE
            // the CleanupService tears the agent down. This guarantees the backend sees the agent
            // accept the termination (so it can stop re-queuing terminate_session) even when the
            // cleanup script races ahead of the final telemetry flush.
            EmitEventSafe(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = Constants.EventTypes.AgentShuttingDown,
                Severity = EventSeverity.Info,
                Source = "EnrollmentTerminationHandler",
                Phase = EnrollmentPhase.Unknown,
                Message = "Agent accepted termination — running CleanupService.",
                Data = new Dictionary<string, object>
                {
                    { "reason", args.Reason.ToString() },
                    { "outcome", args.Outcome.ToString() },
                    { "stage", args.StageName ?? string.Empty },
                },
                ImmediateUpload = true,
            });

            try
            {
                var service = _cleanupServiceFactory();
                service.ExecuteSelfDestruct();
                _logger.Info("EnrollmentTerminationHandler: CleanupService.ExecuteSelfDestruct() invoked (fire-and-forget).");
            }
            catch (Exception ex)
            {
                _logger.Error("EnrollmentTerminationHandler: cleanup service invocation threw.", ex);
            }
        }

        private void TrySaveWhiteGloveComplete()
        {
            if (_sessionPersistence == null)
            {
                _logger.Warning("EnrollmentTerminationHandler: sessionPersistence not wired — whiteglove.complete marker NOT written (Part-2 detection will fail).");
                return;
            }

            try { _sessionPersistence.SaveWhiteGloveComplete(_logger); }
            catch (Exception ex) { _logger.Warning($"EnrollmentTerminationHandler: SaveWhiteGloveComplete threw: {ex.Message}"); }
        }

        private void DelayLateEventGrace()
        {
            if (_lateEventGracePeriod <= TimeSpan.Zero) return;
            try { Task.Delay(_lateEventGracePeriod).Wait(); }
            catch { /* best-effort */ }
        }

        private void DrainSpool()
        {
            // V1 parity — block briefly so pending events can land before the next destructive
            // step (shutdown.exe, self-destruct). A precise HasPending/PendingCount signal is
            // internal to the orchestrator transport, so the V2 equivalent is a bounded wait.
            if (_spoolDrainPeriod <= TimeSpan.Zero) return;
            try { Task.Delay(_spoolDrainPeriod).Wait(); }
            catch { /* best-effort */ }
        }

        private void EmitEventSafe(EnrollmentEvent evt)
        {
            if (_post == null || evt == null) return;
            try { _post.Emit(evt); }
            catch (Exception ex) { _logger.Debug($"EnrollmentTerminationHandler: event emission '{evt?.EventType}' threw: {ex.Message}"); }
        }

        private static void DefaultTriggerReboot(int delaySeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
                Arguments = $"/r /t {delaySeconds} /c \"Autopilot enrollment completed - rebooting\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(psi);
        }
    }
}
