using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Agent.Core.Security;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Handles enrollment terminal events: enrollment_complete, enrollment_failed, and
    /// whiteglove_complete shutdown sequences including diagnostics upload, summary dialog,
    /// self-destruct, and reboot.
    /// </summary>
    public class EnrollmentCompletionHandler
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly string _agentVersion;
        private readonly Action<EnrollmentEvent> _emitEvent;
        private readonly Action<string, string, Dictionary<string, object>> _emitShutdownEvent;
        private readonly Func<Task> _uploadEventsAsync;
        private readonly Action _stopUploadTimers;
        private readonly CleanupService _cleanupService;
        private readonly DiagnosticsPackageService _diagnosticsService;
        private readonly SessionPersistence _sessionPersistence;
        private readonly EventSpool _spool;

        public EnrollmentCompletionHandler(
            AgentConfiguration configuration,
            AgentLogger logger,
            string agentVersion,
            Action<EnrollmentEvent> emitEvent,
            Action<string, string, Dictionary<string, object>> emitShutdownEvent,
            Func<Task> uploadEventsAsync,
            Action stopUploadTimers,
            CleanupService cleanupService,
            DiagnosticsPackageService diagnosticsService,
            SessionPersistence sessionPersistence,
            EventSpool spool)
        {
            _configuration = configuration;
            _logger = logger;
            _agentVersion = agentVersion;
            _emitEvent = emitEvent;
            _emitShutdownEvent = emitShutdownEvent;
            _uploadEventsAsync = uploadEventsAsync;
            _stopUploadTimers = stopUploadTimers;
            _cleanupService = cleanupService;
            _diagnosticsService = diagnosticsService;
            _sessionPersistence = sessionPersistence;
            _spool = spool;
        }

        /// <summary>
        /// Handles normal enrollment completion (success or failure).
        /// Stops collectors, runs shutdown analyzers, uploads diagnostics, optionally self-destructs.
        /// </summary>
        public async Task HandleEnrollmentComplete(
            bool enrollmentSucceeded,
            bool isWhiteGlovePart2,
            Action stopCollectors,
            Action<int?> runShutdownAnalyzers)
        {
            try
            {
                _logger.Info("===== ENROLLMENT COMPLETE - Starting shutdown sequence =====");

                _logger.Info("Stopping event collectors...");
                stopCollectors();
                _spool.StopWatching();

                runShutdownAnalyzers(isWhiteGlovePart2 ? 2 : (int?)null);

                _logger.Info("Uploading final events...");
                await _uploadEventsAsync();

                await Task.Delay(2000);

                await UploadDiagnosticsWithTimelineEvents(enrollmentSucceeded);

                if (_configuration.ShowEnrollmentSummary)
                    LaunchEnrollmentSummaryDialog();

                if (_configuration.SelfDestructOnComplete)
                {
                    _cleanupService.ExecuteSelfDestruct();
                }
                else if (_configuration.RebootOnComplete)
                {
                    _logger.Info($"RebootOnComplete enabled - initiating reboot in {_configuration.RebootDelaySeconds}s");

                    _emitEvent(new EnrollmentEvent
                    {
                        SessionId = _configuration.SessionId,
                        TenantId = _configuration.TenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = "reboot_triggered",
                        Severity = EventSeverity.Info,
                        Source = "MonitoringService",
                        Phase = EnrollmentPhase.Complete,
                        Message = $"Reboot configured — triggering reboot in {_configuration.RebootDelaySeconds} seconds",
                        Data = new Dictionary<string, object>
                        {
                            { "rebootDelaySeconds", _configuration.RebootDelaySeconds }
                        },
                        ImmediateUpload = true
                    });

                    await _uploadEventsAsync();

                    StartReboot(_configuration.RebootDelaySeconds);
                }

                _emitShutdownEvent(
                    enrollmentSucceeded ? "enrollment_complete" : "enrollment_failed",
                    $"Agent shutting down after enrollment {(enrollmentSucceeded ? "completion" : "failure")}",
                    new Dictionary<string, object>
                    {
                        { "enrollmentSucceeded", enrollmentSucceeded },
                        { "selfDestruct", _configuration.SelfDestructOnComplete },
                        { "reboot", _configuration.RebootOnComplete }
                    });
                await _uploadEventsAsync();

                _logger.Info("Shutdown sequence complete. Agent will now exit.");
                ExitProcess(0);
            }
            catch (Exception ex)
            {
                _logger.Error("Error during self-destruct sequence", ex);
                ExitProcess(1);
            }
        }

        /// <summary>
        /// Graceful shutdown after WhiteGlove (Pre-Provisioning) completes.
        /// Does NOT delete the session ID — the session must survive for Part 2.
        /// </summary>
        public async Task HandleWhiteGloveComplete(
            Action stopCollectors,
            Action<int?> runShutdownAnalyzers,
            Func<long> readEventSequence)
        {
            try
            {
                _logger.Info("===== WHITEGLOVE COMPLETE - Starting graceful shutdown sequence =====");

                runShutdownAnalyzers(1);

                _logger.Info("Uploading final events (draining spool)...");
                int maxIterations = 20;
                while (_spool.GetCount() > 0 && maxIterations-- > 0)
                {
                    await _uploadEventsAsync();
                }

                await Task.Delay(2000);

                await UploadDiagnosticsWithTimelineEvents(enrollmentSucceeded: true, fileNameSuffix: "preprov");

                _sessionPersistence.SaveWhiteGloveComplete();
                _logger.Info("WhiteGlove marker persisted for Part 2 detection");

                _emitShutdownEvent(
                    "whiteglove_complete",
                    "Agent shutting down after WhiteGlove Part 1 — no cleanup, session preserved for Part 2 on next boot",
                    new Dictionary<string, object>
                    {
                        { "sessionPreserved", true },
                        { "selfDestruct", false },
                        { "reboot", false },
                        { "cleanup", false },
                        { "willResumeOnNextBoot", true }
                    });
                await _uploadEventsAsync();

                var finalSequence = readEventSequence();
                _sessionPersistence.SaveSequence(finalSequence);
                _logger.Info($"Sequence counter persisted at {finalSequence} for next boot");

                _logger.Info("WhiteGlove graceful shutdown complete. Agent exiting.");
                ExitProcess(0);
            }
            catch (Exception ex)
            {
                _logger.Error("Error during WhiteGlove shutdown sequence", ex);
                ExitProcess(0);
            }
        }

        internal virtual void ExitProcess(int code) => Environment.Exit(code);

        internal virtual void StartReboot(int delaySeconds)
        {
            var psi = new ProcessStartInfo
            {
                FileName = SystemPaths.Shutdown,
                Arguments = $"/r /t {delaySeconds} /c \"Autopilot enrollment completed - rebooting\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }

        /// <summary>
        /// Deletes the persisted session ID and sequence files.
        /// </summary>
        public void DeleteSessionId()
        {
            _logger.Info("Deleting persisted session ID and sequence...");
            try
            {
                _sessionPersistence.DeleteSession();
                _logger.Info("Session ID and sequence deleted successfully");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to delete session ID: {ex.Message}");
            }
        }

        private async Task UploadDiagnosticsWithTimelineEvents(bool enrollmentSucceeded, string fileNameSuffix = null)
        {
            var mode = _configuration.DiagnosticsUploadMode ?? "Off";
            if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
                return;
            if (!_configuration.DiagnosticsUploadEnabled)
                return;
            if (string.Equals(mode, "OnFailure", StringComparison.OrdinalIgnoreCase) && enrollmentSucceeded)
                return;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                Timestamp = DateTime.UtcNow,
                EventType = "diagnostics_collecting",
                Severity = EventSeverity.Info,
                Source = "MonitoringService",
                Phase = EnrollmentPhase.Complete,
                Message = $"Collecting diagnostics package (mode={mode}{(fileNameSuffix != null ? $", type={fileNameSuffix}" : "")})",
                Data = new Dictionary<string, object>
                {
                    { "uploadMode", mode }
                }
            });
            await _uploadEventsAsync();

            var uploadResult = await _diagnosticsService.CreateAndUploadAsync(enrollmentSucceeded, fileNameSuffix);
            var success = uploadResult?.Success == true;

            var resultData = new Dictionary<string, object>
            {
                { "uploadMode", mode }
            };
            if (uploadResult?.BlobName != null)
                resultData["blobName"] = uploadResult.BlobName;
            if (uploadResult?.SasUrlPrefix != null)
                resultData["sasUrl"] = uploadResult.SasUrlPrefix;
            if (uploadResult?.ErrorCode != null)
                resultData["errorCode"] = uploadResult.ErrorCode;

            _emitEvent(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                Timestamp = DateTime.UtcNow,
                EventType = success ? "diagnostics_uploaded" : "diagnostics_upload_failed",
                Severity = success ? EventSeverity.Info : EventSeverity.Warning,
                Source = "MonitoringService",
                Phase = EnrollmentPhase.Complete,
                Message = success
                    ? $"Diagnostics package uploaded: {uploadResult.BlobName}"
                    : $"Diagnostics package upload failed{(uploadResult?.ErrorCode != null ? $": {uploadResult.ErrorCode}" : "")}",
                Data = resultData
            });
            await _uploadEventsAsync();
        }

        private void LaunchEnrollmentSummaryDialog()
        {
            try
            {
                var agentDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty;
                var dialogExe = Path.Combine(agentDir, "AutopilotMonitor.SummaryDialog.exe");

                if (!File.Exists(dialogExe))
                {
                    _logger.Warning($"Summary dialog EXE not found at {dialogExe} — skipping");
                    return;
                }

                var statusFile = Path.Combine(
                    Environment.ExpandEnvironmentVariables(@"%ProgramData%\AutopilotMonitor\State"),
                    "final-status.json");

                if (!File.Exists(statusFile))
                {
                    _logger.Warning($"final-status.json not found at {statusFile} — skipping summary dialog");
                    return;
                }

                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "AutopilotMonitor-Summary");

                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                Directory.CreateDirectory(tempDir);

                GrantUserDeletePermission(tempDir);

                var tempDialogExe = Path.Combine(tempDir, "AutopilotMonitor.SummaryDialog.exe");
                File.Copy(dialogExe, tempDialogExe);

                var jsonDll = Path.Combine(agentDir, "Newtonsoft.Json.dll");
                if (File.Exists(jsonDll))
                    File.Copy(jsonDll, Path.Combine(tempDir, "Newtonsoft.Json.dll"));

                var exeConfig = dialogExe + ".config";
                if (File.Exists(exeConfig))
                    File.Copy(exeConfig, tempDialogExe + ".config");

                var tempStatusFile = Path.Combine(tempDir, "final-status.json");
                File.Copy(statusFile, tempStatusFile);

                var args = $"--status-file \"{tempStatusFile}\" --timeout {_configuration.EnrollmentSummaryTimeoutSeconds} --cleanup";
                if (!string.IsNullOrEmpty(_configuration.EnrollmentSummaryBrandingImageUrl))
                    args += $" --branding-url \"{_configuration.EnrollmentSummaryBrandingImageUrl}\"";

                _logger.Info($"Launching enrollment summary dialog: {tempDialogExe} {args}");

                _emitEvent(new EnrollmentEvent
                {
                    SessionId = _configuration.SessionId,
                    TenantId = _configuration.TenantId,
                    Timestamp = DateTime.UtcNow,
                    EventType = "enrollment_summary_shown",
                    Severity = EventSeverity.Info,
                    Source = "MonitoringService",
                    Phase = EnrollmentPhase.Complete,
                    Message = "Launching enrollment summary dialog to user"
                });

                try { _uploadEventsAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }

                var launched = UserSessionProcessLauncher.LaunchInUserSession(
                    tempDialogExe, args, _logger,
                    _configuration.EnrollmentSummaryLaunchRetrySeconds);
                if (!launched)
                {
                    _logger.Warning("Could not launch summary dialog in user session (no interactive session found)");
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to launch enrollment summary dialog: {ex.Message}");
            }
        }

        private void GrantUserDeletePermission(string directoryPath)
        {
            try
            {
                var dirInfo = new DirectoryInfo(directoryPath);
                var security = dirInfo.GetAccessControl();
                var usersIdentity = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                security.AddAccessRule(new FileSystemAccessRule(
                    usersIdentity,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                dirInfo.SetAccessControl(security);
            }
            catch (Exception ex)
            {
                _logger.Debug($"Could not set ACL on summary folder: {ex.Message}");
            }
        }
    }
}
