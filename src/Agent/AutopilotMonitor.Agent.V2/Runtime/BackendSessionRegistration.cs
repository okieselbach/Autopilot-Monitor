using System;
using System.Net.Http;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Phase 6 of <see cref="Program"/>'s <c>RunAgent</c>: blocking
    /// <c>POST /api/agent/register-session</c> with 5-retry exponential backoff before the
    /// orchestrator starts. V1 parity (MonitoringService.RegisterSessionAsync) — without
    /// this call the backend's Sessions table never gets a row for this session, so
    /// <c>IncrementSessionEventCountAsync</c> / <c>UpdateSessionStatusAsync</c> silently
    /// no-op and session status / phase / admin-overrides / validator reconcile all break.
    /// On registration failure we follow V1's rule: collectors MUST NOT start and the
    /// agent exits cleanly so the next Scheduled-Task tick can retry.
    /// </summary>
    internal static class BackendSessionRegistration
    {
        public static SessionRegistrationOutcomeResult Register(
            AgentConfiguration agentConfig,
            BackendAuthBundle auth,
            HttpClient mtlsHttpClient,
            string agentVersion,
            bool consoleMode,
            AgentLogger logger)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            var registrationResult = SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient: auth.BackendApiClient,
                agentConfig: agentConfig,
                agentVersion: agentVersion,
                logger: logger,
                authFailureTracker: auth.AuthFailureTracker,
                emergencyReporter: auth.EmergencyReporter).GetAwaiter().GetResult();

            if (registrationResult.Outcome != SessionRegistrationOutcome.Succeeded)
            {
                logger.Error(
                    $"=== SESSION REGISTRATION FAILED ({registrationResult.Outcome}: {registrationResult.ErrorMessage}) — " +
                    "collectors will NOT start to prevent orphaned events. ===");
                if (consoleMode)
                    Console.Error.WriteLine($"FATAL: session registration failed ({registrationResult.Outcome}). Agent exiting.");
                try { mtlsHttpClient?.Dispose(); } catch { }
                try { auth.BackendApiClient?.Dispose(); } catch { }
                // Exit code differs so the diag skill can distinguish Auth vs Network in Scheduled-Task history.
                return SessionRegistrationOutcomeResult.Exit(
                    registrationResult.Outcome == SessionRegistrationOutcome.AuthFailed ? 6 : 7);
            }

            return SessionRegistrationOutcomeResult.Continue(registrationResult);
        }
    }

    /// <summary>
    /// Phase 6 outcome — either an early exit (V1 parity: 6 = AuthFailed, 7 = anything else)
    /// or a Continue payload carrying the successful <see cref="SessionRegistrationResult"/>
    /// for downstream consumers (initial-signal posting, admin-preemption handling,
    /// session-started anchor, AdminAction surface in the wired telemetry response).
    /// </summary>
    internal sealed class SessionRegistrationOutcomeResult
    {
        public bool ShouldExit { get; }
        public int ExitCode { get; }
        public SessionRegistrationResult Registration { get; }

        private SessionRegistrationOutcomeResult(bool shouldExit, int exitCode, SessionRegistrationResult registration)
        {
            ShouldExit = shouldExit;
            ExitCode = exitCode;
            Registration = registration;
        }

        public static SessionRegistrationOutcomeResult Exit(int code)
            => new SessionRegistrationOutcomeResult(true, code, null);

        public static SessionRegistrationOutcomeResult Continue(SessionRegistrationResult registration)
            => new SessionRegistrationOutcomeResult(false, 0, registration);
    }
}
