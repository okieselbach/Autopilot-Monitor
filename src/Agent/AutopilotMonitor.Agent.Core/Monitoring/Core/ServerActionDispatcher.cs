using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Logging;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.Core.Monitoring.Core
{
    /// <summary>
    /// Executes server-issued actions delivered via ingest responses.
    ///
    /// Design notes:
    /// - Idempotent per type: each handler tolerates repeated delivery (at-least-once channel).
    /// - Unknown action types are logged as <c>server_action_unknown</c> and skipped — rolling out a new
    ///   type on the server must not break older agents.
    /// - Every action emits two telemetry events: <c>server_action_received</c> on entry and either
    ///   <c>server_action_executed</c> or <c>server_action_failed</c> on exit, so operators can audit the
    ///   end-to-end lifecycle from backend App Insights → agent session events.
    /// - All three concrete handlers are passed in as callbacks so the dispatcher itself does not
    ///   depend on the internal wiring of <see cref="RemoteConfigService"/>, <see cref="DiagnosticsPackageService"/>,
    ///   or the orchestrator's shutdown sequence.
    /// </summary>
    public class ServerActionDispatcher
    {
        private readonly AgentConfiguration _configuration;
        private readonly AgentLogger _logger;
        private readonly Func<Task<bool>> _rotateConfigAsync;
        private readonly Func<string, Task<DiagnosticsUploadResult>> _uploadDiagnosticsAsync;
        private readonly Func<ServerAction, Task> _onTerminateRequested;
        private readonly Action<EnrollmentEvent> _emitEvent;

        /// <param name="rotateConfigAsync">
        /// Refetch remote config from the backend and apply to the agent's live state.
        /// Return true on success, false on failure (the handler will emit the appropriate telemetry).
        /// Typically wired to <c>RemoteConfigService.FetchConfigAsync</c>.
        /// </param>
        /// <param name="uploadDiagnosticsAsync">
        /// Trigger a diagnostics package upload with the given file-name suffix. Return the result
        /// (success/failure, blob name, error code). Typically wired to
        /// <c>DiagnosticsPackageService.CreateAndUploadAsync</c>.
        /// </param>
        /// <param name="onTerminateRequested">
        /// Called for <c>terminate_session</c> actions. The dispatcher does NOT execute the shutdown
        /// itself — shutdown requires coordinating timers, spool flush, and cleanup that lives in the
        /// orchestrator. The callback is responsible for the actual termination sequence.
        /// </param>
        public ServerActionDispatcher(
            AgentConfiguration configuration,
            AgentLogger logger,
            Func<Task<bool>> rotateConfigAsync,
            Func<string, Task<DiagnosticsUploadResult>> uploadDiagnosticsAsync,
            Func<ServerAction, Task> onTerminateRequested,
            Action<EnrollmentEvent> emitEvent)
        {
            _configuration = configuration;
            _logger = logger;
            _rotateConfigAsync = rotateConfigAsync;
            _uploadDiagnosticsAsync = uploadDiagnosticsAsync;
            _onTerminateRequested = onTerminateRequested;
            _emitEvent = emitEvent;
        }

        /// <summary>
        /// Routes a batch of actions to their handlers. Processes sequentially — ordering is defined
        /// by the order actions were queued on the server. A failing handler does not abort the batch;
        /// subsequent actions still run so a bad rotate_config doesn't block a terminate_session.
        /// </summary>
        public virtual async Task DispatchAsync(List<ServerAction> actions)
        {
            if (actions == null || actions.Count == 0)
                return;

            foreach (var action in actions)
            {
                if (action == null) continue;

                EmitReceived(action);

                try
                {
                    switch (action.Type?.ToLowerInvariant())
                    {
                        case ServerActionTypes.RotateConfig:
                            await HandleRotateConfigAsync(action);
                            break;

                        case ServerActionTypes.RequestDiagnostics:
                            await HandleRequestDiagnosticsAsync(action);
                            break;

                        case ServerActionTypes.TerminateSession:
                            if (_onTerminateRequested != null)
                                await _onTerminateRequested(action);
                            else
                                EmitFailed(action, "no_terminate_handler_wired");
                            break;

                        default:
                            _logger.Warning($"ServerActionDispatcher: unknown action type '{action.Type}' — skipping");
                            EmitFailed(action, "unknown_action_type");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"ServerActionDispatcher: handler for '{action.Type}' threw", ex);
                    EmitFailed(action, ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        private async Task HandleRotateConfigAsync(ServerAction action)
        {
            if (_rotateConfigAsync == null)
            {
                EmitFailed(action, "no_rotate_config_handler");
                return;
            }

            _logger.Info($"ServerAction rotate_config: refetching remote config (reason: {action.Reason})");
            var ok = await _rotateConfigAsync();
            if (ok)
                EmitExecuted(action);
            else
                EmitFailed(action, "config_fetch_failed");
        }

        private async Task HandleRequestDiagnosticsAsync(ServerAction action)
        {
            if (_uploadDiagnosticsAsync == null)
            {
                EmitFailed(action, "no_diagnostics_handler");
                return;
            }

            _logger.Info($"ServerAction request_diagnostics: initiating best-effort upload (reason: {action.Reason})");
            var result = await _uploadDiagnosticsAsync("server-requested");
            if (result != null && result.Success)
            {
                EmitExecuted(action, extraData: new Dictionary<string, object>
                {
                    { "blobName", result.BlobName ?? string.Empty }
                });
            }
            else
            {
                EmitFailed(action, result?.ErrorCode ?? "diagnostics_upload_failed");
            }
        }

        // ---- Telemetry helpers ----

        private void EmitReceived(ServerAction action)
        {
            _emitEvent?.Invoke(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "server_action_received",
                Severity = EventSeverity.Info,
                Source = "ServerActionDispatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Received server action '{action.Type}'",
                Timestamp = DateTime.UtcNow,
                Data = BuildTelemetryData(action)
            });
        }

        private void EmitExecuted(ServerAction action, Dictionary<string, object> extraData = null)
        {
            var data = BuildTelemetryData(action);
            if (extraData != null)
            {
                foreach (var kvp in extraData)
                    data[kvp.Key] = kvp.Value;
            }

            _emitEvent?.Invoke(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "server_action_executed",
                Severity = EventSeverity.Info,
                Source = "ServerActionDispatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Executed server action '{action.Type}'",
                Timestamp = DateTime.UtcNow,
                Data = data
            });
        }

        private void EmitFailed(ServerAction action, string reason)
        {
            var data = BuildTelemetryData(action);
            data["failureReason"] = reason ?? string.Empty;

            _emitEvent?.Invoke(new EnrollmentEvent
            {
                SessionId = _configuration.SessionId,
                TenantId = _configuration.TenantId,
                EventType = "server_action_failed",
                Severity = EventSeverity.Warning,
                Source = "ServerActionDispatcher",
                Phase = EnrollmentPhase.Unknown,
                Message = $"Failed to execute server action '{action.Type}': {reason}",
                Timestamp = DateTime.UtcNow,
                Data = data
            });
        }

        private static Dictionary<string, object> BuildTelemetryData(ServerAction action)
        {
            return new Dictionary<string, object>
            {
                { "actionType", action?.Type ?? string.Empty },
                { "reason", action?.Reason ?? string.Empty },
                { "ruleId", action?.RuleId ?? string.Empty },
                { "queuedAt", action?.QueuedAt.ToString("O") ?? string.Empty }
            };
        }
    }
}
