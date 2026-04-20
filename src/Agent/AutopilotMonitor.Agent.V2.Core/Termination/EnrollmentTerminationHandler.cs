using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Orchestrates peripheral termination work in response to
    /// <see cref="EnrollmentOrchestrator.Terminated"/>. Plan §4.x M4.6.β.
    /// <para>
    /// <b>Sequence</b> (Legacy parity — single-writer, best-effort each step, never throws):
    /// </para>
    /// <list type="number">
    ///   <item>Compose <see cref="FinalStatus"/> via <see cref="FinalStatusBuilder"/>.</item>
    ///   <item>Write final-status.json + launch SummaryDialog via <see cref="SummaryDialogLauncher"/>.</item>
    ///   <item>Upload diagnostics via <see cref="DiagnosticsPackageService"/> (respects
    ///     <c>DiagnosticsUploadMode</c>: Off / Always / OnFailure).</item>
    ///   <item>Write <c>enrollment-complete.marker</c> (so ghost-restart detection on next boot
    ///     exits cleanly even if <see cref="CleanupService"/> failed).</item>
    ///   <item>Run <see cref="CleanupService.ExecuteSelfDestruct"/> — UNLESS the stage is
    ///     <see cref="SessionStage.WhiteGloveSealed"/> (Part 1 exit, session resumes Part 2).</item>
    ///   <item>Signal the caller-owned shutdown <see cref="ManualResetEventSlim"/> so Program.cs'
    ///     main loop exits the <c>shutdown.Wait()</c> and calls <c>orchestrator.Stop()</c>.</item>
    /// </list>
    /// <para>
    /// Each step logs + continues on failure — nothing in here is allowed to prevent the agent
    /// from shutting down. CleanupService is fire-and-forget (spawns a PowerShell cleanup script
    /// that waits for process exit); this handler does not block waiting for the script.
    /// </para>
    /// </summary>
    public sealed class EnrollmentTerminationHandler
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly string _stateDirectory;
        private readonly DateTime _agentStartTimeUtc;
        private readonly Func<DecisionState> _currentStateAccessor;
        private readonly Func<AppPackageStateList> _packageStatesAccessor;
        private readonly Func<CleanupService> _cleanupServiceFactory;
        private readonly Func<bool, string, Task<DiagnosticsUploadResult>> _uploadDiagnosticsAsync;
        private readonly Action _signalShutdown;
        private readonly string _dialogExePathOverride;

        private int _handled;

        public EnrollmentTerminationHandler(
            AgentConfiguration configuration,
            AgentLogger logger,
            string stateDirectory,
            DateTime agentStartTimeUtc,
            Func<DecisionState> currentStateAccessor,
            Func<AppPackageStateList> packageStatesAccessor,
            Func<CleanupService> cleanupServiceFactory,
            Func<bool, string, Task<DiagnosticsUploadResult>> uploadDiagnosticsAsync,
            Action signalShutdown,
            string dialogExePathOverride = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _stateDirectory = string.IsNullOrEmpty(stateDirectory) ? throw new ArgumentNullException(nameof(stateDirectory)) : stateDirectory;
            _agentStartTimeUtc = agentStartTimeUtc;
            _currentStateAccessor = currentStateAccessor ?? throw new ArgumentNullException(nameof(currentStateAccessor));
            _packageStatesAccessor = packageStatesAccessor ?? throw new ArgumentNullException(nameof(packageStatesAccessor));
            _cleanupServiceFactory = cleanupServiceFactory ?? throw new ArgumentNullException(nameof(cleanupServiceFactory));
            _uploadDiagnosticsAsync = uploadDiagnosticsAsync ?? throw new ArgumentNullException(nameof(uploadDiagnosticsAsync));
            _signalShutdown = signalShutdown ?? throw new ArgumentNullException(nameof(signalShutdown));
            _dialogExePathOverride = dialogExePathOverride;
        }

        /// <summary>
        /// Handler for <see cref="EnrollmentOrchestrator.Terminated"/>. Idempotent — runs at most once.
        /// </summary>
        public void Handle(object sender, EnrollmentTerminatedEventArgs args)
        {
            if (Interlocked.Exchange(ref _handled, 1) == 1) return;

            try
            {
                _logger.Info(
                    $"EnrollmentTerminationHandler: handling Terminated (reason={args.Reason}, outcome={args.Outcome}, stage={args.StageName}).");

                var state = TryGetCurrentState();
                if (state == null)
                {
                    _logger.Warning("EnrollmentTerminationHandler: current state unavailable — skipping FinalStatus + SummaryDialog.");
                }
                else
                {
                    RunBuildAndLaunchDialog(state, args);
                }

                RunUploadDiagnostics(args);
                WriteEnrollmentCompleteMarker(args);
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

        private AppPackageStateList TryGetPackageStates()
        {
            try { return _packageStatesAccessor(); }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: package states accessor threw: {ex.Message}");
                return null;
            }
        }

        private void RunBuildAndLaunchDialog(DecisionState state, EnrollmentTerminatedEventArgs args)
        {
            try
            {
                var packages = TryGetPackageStates();
                var status = FinalStatusBuilder.Build(state, args, packages, _agentStartTimeUtc);
                SummaryDialogLauncher.WriteAndLaunch(status, _configuration, _stateDirectory, _logger, _dialogExePathOverride);
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: FinalStatus/SummaryDialog step failed: {ex.Message}");
            }
        }

        private void RunUploadDiagnostics(EnrollmentTerminatedEventArgs args)
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

            try
            {
                var suffix = enrollmentSucceeded ? "success" : "failure";
                var result = _uploadDiagnosticsAsync(enrollmentSucceeded, suffix).GetAwaiter().GetResult();
                if (result != null && result.Success)
                    _logger.Info($"EnrollmentTerminationHandler: diagnostics uploaded (blob={result.BlobName}).");
                else
                    _logger.Warning($"EnrollmentTerminationHandler: diagnostics upload returned failure: {result?.ErrorCode ?? "null-result"}.");
            }
            catch (Exception ex)
            {
                _logger.Warning($"EnrollmentTerminationHandler: diagnostics upload threw: {ex.Message}");
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
    }
}
