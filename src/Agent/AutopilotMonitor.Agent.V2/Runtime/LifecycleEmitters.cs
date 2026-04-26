using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Lifecycle event/signal emitters extracted from <see cref="Program"/>'s <c>RunAgent</c>:
    /// the InformationalEvent emits for <c>agent_started</c> / <c>agent_version_check</c> /
    /// <c>agent_unrestricted_mode_changed</c>; the post-startup decision signals
    /// (<see cref="DecisionSignalKind.SessionStarted"/>, <see cref="DecisionSignalKind.AdminPreemptionDetected"/>,
    /// <see cref="DecisionSignalKind.SystemRebootObserved"/>); and the two watchdog factory
    /// methods that close over a deferred <see cref="InformationalEventPost"/> accessor
    /// (max-lifetime + auth-failure threshold). Single-rail refactor (plan §5.1) — every
    /// emit flows through the same <c>InformationalEventPost</c> the orchestrator's
    /// onIngressReady hook constructs.
    /// </summary>
    internal static class LifecycleEmitters
    {
        // ============================================================ InformationalEvent emits

        /// <summary>
        /// V1 parity — fire-and-forget <c>agent_started</c> event emitted after
        /// <see cref="EnrollmentOrchestrator.Start"/>. Carries a snapshot of the boot classification
        /// and the tenant-influenced runtime knobs so dashboards can classify crash-loops,
        /// backend-rejected sessions and forced self-destruct runs. Phase stays
        /// <see cref="EnrollmentPhase.Unknown"/> — the event is NOT a phase declaration.
        /// </summary>
        public static void EmitAgentStarted(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            Program.PreviousExitSummary previousExit,
            string agentVersion,
            AgentLogger logger)
        {
            try
            {
                var data = new Dictionary<string, object>
                {
                    { "agentVersion", agentVersion },
                    { "commandLineArgs", agentConfig.CommandLineArgs ?? string.Empty },
                    { "isBootstrapSession", agentConfig.UseBootstrapTokenAuth },
                    { "awaitEnrollment", agentConfig.AwaitEnrollment },
                    { "selfDestructOnComplete", agentConfig.SelfDestructOnComplete },
                    { "certAuth", !agentConfig.UseBootstrapTokenAuth },
                    { "agentMaxLifetimeMinutes", agentConfig.AgentMaxLifetimeMinutes },
                    { "diagnosticsUploadMode", agentConfig.DiagnosticsUploadMode ?? "Off" },
                    { "previousExitType", previousExit?.ExitType ?? "unknown" },
                    { "unrestrictedMode", agentConfig.UnrestrictedMode },
                };

                if (!string.IsNullOrEmpty(previousExit?.CrashExceptionType))
                    data["previousCrashException"] = previousExit.CrashExceptionType;

                if (previousExit?.LastBootUtc.HasValue == true)
                    data["previousBootUtc"] = previousExit.LastBootUtc.Value.ToString("o");

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = "agent_started",
                    Severity = EventSeverity.Info,
                    Source = "Agent",
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"Agent v{agentVersion} started (previousExit={previousExit?.ExitType ?? "unknown"}).",
                    Data = data,
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                logger.Warning($"agent_started emission failed: {ex.Message}");
            }
        }

        public static void EmitVersionCheckIfAny(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            AgentLogger logger)
        {
            try
            {
                var buildResult = VersionCheckEventBuilder.TryBuild(
                    sessionId: agentConfig.SessionId,
                    tenantId: agentConfig.TenantId,
                    agentStartTimeUtc: DateTime.UtcNow);

                if (!string.IsNullOrEmpty(buildResult?.ParseError))
                    logger.Warning($"VersionCheckEventBuilder parse error: {buildResult.ParseError}");

                if (buildResult?.Event != null)
                {
                    post.Emit(buildResult.Event);
                    logger.Info($"agent_version_check emitted (outcome={buildResult.Outcome}).");
                }
                else if (buildResult?.Deduped == true)
                {
                    logger.Debug($"agent_version_check deduped (outcome={buildResult.Outcome}).");
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"VersionCheckEventBuilder emission failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V1 parity — when <see cref="RemoteConfigMerger"/> flips the tenant-controlled
        /// <c>UnrestrictedMode</c> guardrail, surface the transition as an auditable event on
        /// the session timeline so operators can correlate subsequent gather-rule exec with the
        /// elevated policy. The V1 code lives in
        /// <c>MonitoringService.AuditUnrestrictedModeChange</c>.
        /// </summary>
        public static void EmitUnrestrictedModeAuditIfChanged(
            InformationalEventPost post,
            AgentConfiguration agentConfig,
            RemoteConfigMergeResult mergeResult,
            AgentLogger logger)
        {
            if (mergeResult == null || !mergeResult.UnrestrictedModeChanged) return;

            try
            {
                var newValue = mergeResult.NewUnrestrictedMode;
                logger.Warning(
                    $"UnrestrictedMode changed: {mergeResult.OldUnrestrictedMode} → {newValue}. Emitting audit event.");

                post.Emit(new EnrollmentEvent
                {
                    SessionId = agentConfig.SessionId,
                    TenantId = agentConfig.TenantId,
                    EventType = "agent_unrestricted_mode_changed",
                    Severity = newValue ? EventSeverity.Warning : EventSeverity.Info,
                    Source = "RemoteConfigMerger",
                    Phase = EnrollmentPhase.Unknown,
                    Message = newValue
                        ? "Agent unrestricted mode ENABLED — gather rules can now execute without AllowList checks"
                        : "Agent unrestricted mode disabled — gather rules revert to AllowList checks",
                    Data = new Dictionary<string, object>
                    {
                        { "oldValue", mergeResult.OldUnrestrictedMode },
                        { "newValue", newValue },
                        { "changedAtUtc", DateTime.UtcNow.ToString("o") },
                    },
                    ImmediateUpload = true,
                });
            }
            catch (Exception ex)
            {
                logger.Warning($"EmitUnrestrictedModeAuditIfChanged: emission failed: {ex.Message}");
            }
        }

        // ================================================================ Decision-signal posts

        /// <summary>
        /// V2 parity — post a <see cref="DecisionSignalKind.SessionStarted"/> signal with the
        /// tenant-registered session metadata so the reducer's session-anchor handler runs.
        /// Without this the DecisionState.Stage stays at the initial value and subsequent
        /// raw signals (ESP / Hello) see an uninitialised session.
        /// </summary>
        public static void PostSessionStarted(
            ISignalIngressSink ingressSink,
            SessionRegistrationResult registrationResult,
            AgentConfiguration agentConfig,
            string agentVersion,
            AgentLogger logger)
        {
            try
            {
                var payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["enrollmentType"] = EnrollmentRegistryDetector.DetectEnrollmentType(),
                    ["isHybridJoin"] = EnrollmentRegistryDetector.DetectHybridJoin() ? "true" : "false",
                    ["validatedBy"] = registrationResult.ValidatedBy.ToString(),
                    ["agentVersion"] = agentVersion,
                    ["isBootstrapSession"] = agentConfig.UseBootstrapTokenAuth ? "true" : "false",
                };

                var evidence = new Evidence(
                    kind: EvidenceKind.Synthetic,
                    identifier: "register_session_success",
                    summary: "Session registration handshake succeeded; posting SessionStarted anchor for reducer.");

                ingressSink.Post(
                    kind: DecisionSignalKind.SessionStarted,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "Program.RunAgent",
                    evidence: evidence,
                    payload: payload);

                logger.Debug($"SessionStarted signal posted (validatedBy={registrationResult.ValidatedBy}, enrollmentType={payload["enrollmentType"]}).");
            }
            catch (Exception ex)
            {
                logger.Warning($"SessionStarted post failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V2 parity — post <see cref="DecisionSignalKind.AdminPreemptionDetected"/> when the
        /// register-session response carried an <c>AdminAction</c>. The reducer transitions
        /// Stage to terminal and emits the enrollment_complete/_failed telemetry event; the
        /// orchestrator's DecisionStepProcessor then raises the Terminated event, which the
        /// subscribed termination handler picks up — no direct synthesis needed.
        /// </summary>
        public static void PostAdminPreemption(
            ISignalIngressSink ingressSink,
            SessionRegistrationResult registrationResult,
            AgentLogger logger)
        {
            try
            {
                var adminOutcome = registrationResult.AdminAction; // "Succeeded" | "Failed"
                logger.Warning(
                    $"=== ADMIN OVERRIDE on startup: session already marked as {adminOutcome} by administrator — posting AdminPreemptionDetected signal ===");

                ingressSink.Post(
                    kind: DecisionSignalKind.AdminPreemptionDetected,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "register_session_response",
                    evidence: new Evidence(
                        kind: EvidenceKind.Synthetic,
                        identifier: $"admin_preemption:{adminOutcome}",
                        summary: $"Operator marked session as {adminOutcome} via portal before agent startup."),
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["adminOutcome"] = adminOutcome,
                    });
            }
            catch (Exception ex)
            {
                logger.Warning($"AdminPreemptionDetected post failed: {ex.Message}");
            }
        }

        /// <summary>
        /// V2 parity — post <see cref="DecisionSignalKind.SystemRebootObserved"/> when the prior
        /// agent process was terminated by an OS reboot. The reducer records the reboot fact
        /// (used by the WhiteGlove reboot-observed scoring weight, plan §2.4) and emits the
        /// <c>system_reboot_detected</c> telemetry event as a side effect.
        /// </summary>
        public static void PostSystemRebootObserved(
            ISignalIngressSink ingressSink,
            Program.PreviousExitSummary previousExit,
            AgentLogger logger)
        {
            try
            {
                var payload = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["previousExitType"] = previousExit?.ExitType ?? string.Empty,
                    ["lastBootUtc"] = previousExit?.LastBootUtc?.ToString("o") ?? string.Empty,
                };

                ingressSink.Post(
                    kind: DecisionSignalKind.SystemRebootObserved,
                    occurredAtUtc: DateTime.UtcNow,
                    sourceOrigin: "Program.DetectPreviousExit",
                    evidence: new Evidence(
                        kind: EvidenceKind.Synthetic,
                        identifier: "previous_exit_reboot_kill",
                        summary: $"Prior agent process terminated by OS reboot (exitType={previousExit?.ExitType})."),
                    payload: payload);
            }
            catch (Exception ex)
            {
                logger.Warning($"SystemRebootObserved post failed: {ex.Message}");
            }
        }

        // ================================================================ Watchdog handlers

        /// <summary>
        /// V1 parity — when the kernel's max-lifetime watchdog fires, emit an explicit
        /// <c>enrollment_failed</c> event with <c>failureType=agent_timeout</c> BEFORE the
        /// regular termination path runs. Dashboards + KQL queries key on the event type +
        /// data dictionary to distinguish a genuine enrollment failure from a timeout shutdown.
        /// <para>
        /// The post is captured by accessor because the live <see cref="InformationalEventPost"/>
        /// is built inside the orchestrator's onIngressReady callback, AFTER subscription. The
        /// returned handler null-checks the accessor result: if the watchdog somehow fires
        /// before the ingress is up, we log-and-skip rather than crash.
        /// </para>
        /// </summary>
        public static EventHandler<EnrollmentTerminatedEventArgs> CreateMaxLifetimeEmitter(
            Func<InformationalEventPost> getLifecyclePost,
            AgentConfiguration agentConfig,
            DateTime agentStartTimeUtc,
            AgentLogger logger)
        {
            return (s, e) =>
            {
                if (e.Reason != EnrollmentTerminationReason.MaxLifetimeExceeded) return;
                var post = getLifecyclePost();
                if (post == null)
                {
                    logger.Warning("enrollment_failed (max_lifetime) suppressed — ingress not ready.");
                    return;
                }
                try
                {
                    var uptimeMin = (DateTime.UtcNow - agentStartTimeUtc).TotalMinutes;
                    // Phase stays Unknown per plan §1.4 phase-invariant — the UI timeline
                    // buckets chronologically into the last-declared phase. This fixes the
                    // legacy violation where enrollment_failed (max_lifetime) carried
                    // Phase=Complete and caused a phantom phase in the UI.
                    post.Emit(new EnrollmentEvent
                    {
                        SessionId = agentConfig.SessionId,
                        TenantId = agentConfig.TenantId,
                        EventType = "enrollment_failed",
                        Severity = EventSeverity.Error,
                        Source = "EnrollmentOrchestrator",
                        Phase = EnrollmentPhase.Unknown,
                        Message = $"Agent max lifetime expired ({uptimeMin:F0} min) — enrollment did not complete in time",
                        Data = new Dictionary<string, object>
                        {
                            { "failureType", "agent_timeout" },
                            { "failureSource", "max_lifetime_timer" },
                            { "agentUptimeMinutes", Math.Round(uptimeMin, 1) },
                            { "agentMaxLifetimeMinutes", agentConfig.AgentMaxLifetimeMinutes },
                            { "stageAtTimeout", e.StageName ?? string.Empty },
                        },
                        ImmediateUpload = true,
                    });
                }
                catch (Exception emitEx)
                {
                    logger.Warning($"enrollment_failed (max_lifetime) emission failed: {emitEx.Message}");
                }
            };
        }

        /// <summary>
        /// Auth-failure watchdog: when MaxAuthFailures / AuthFailureTimeoutMinutes is exceeded
        /// the agent must shut down cleanly instead of hammering a backend that has definitely
        /// said no. Event fires at most once. V1 parity — emit a structured
        /// <c>agent_shutdown</c> event with reason=auth_failure and the full telemetry payload
        /// before tripping the shutdown signal so the backend sees WHY the agent terminated in
        /// the session timeline.
        /// </summary>
        public static EventHandler<AuthFailureThresholdEventArgs> CreateAuthThresholdHandler(
            Func<InformationalEventPost> getLifecyclePost,
            AgentConfiguration agentConfig,
            Action signalShutdown,
            AgentLogger logger)
        {
            return (s, e) =>
            {
                logger.Error($"Auth-failure threshold exceeded ({e.Reason}) — initiating shutdown.");
                var post = getLifecyclePost();
                if (post != null)
                {
                    try
                    {
                        post.Emit(new EnrollmentEvent
                        {
                            SessionId = agentConfig.SessionId,
                            TenantId = agentConfig.TenantId,
                            EventType = "agent_shutdown",
                            Severity = EventSeverity.Error,
                            Source = "AuthFailureTracker",
                            Phase = EnrollmentPhase.Unknown,
                            Message = $"Agent shut down after {e.ConsecutiveFailures} consecutive auth failures",
                            Data = new Dictionary<string, object>
                            {
                                { "reason", "auth_failure" },
                                { "consecutiveFailures", e.ConsecutiveFailures },
                                { "firstFailureTime", e.FirstFailureUtc.ToString("o") },
                                { "maxFailures", agentConfig.MaxAuthFailures },
                                { "timeoutMinutes", agentConfig.AuthFailureTimeoutMinutes },
                                { "lastOperation", e.LastOperation ?? string.Empty },
                                { "lastStatusCode", e.LastStatusCode },
                                { "thresholdReason", e.Reason ?? string.Empty },
                            },
                            ImmediateUpload = true,
                        });
                    }
                    catch (Exception emitEx)
                    {
                        logger.Warning($"agent_shutdown emission failed: {emitEx.Message}");
                    }
                }
                else
                {
                    logger.Warning("agent_shutdown (auth_failure) suppressed — ingress not ready.");
                }

                signalShutdown();
            };
        }
    }
}
