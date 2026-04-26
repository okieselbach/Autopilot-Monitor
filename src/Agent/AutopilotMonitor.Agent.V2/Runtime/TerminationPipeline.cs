using System;
using System.Net.Http;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// The agent's clean-shutdown pipeline — extracted from <see cref="Program"/>'s
    /// <c>RunAgent</c> finally block.
    /// <para>
    /// Order of operations is contractual (Plan §6.2 synchronous-shutdown):
    /// </para>
    /// <list type="number">
    ///   <item>Unsubscribe Ctrl+C / ProcessExit / Terminated / ThresholdExceeded handlers
    ///     so no late event drives a re-entrant shutdown.</item>
    ///   <item><see cref="EnrollmentOrchestrator.Stop"/> — drains the spool, runs the
    ///     terminal upload, stops collector hosts, disposes the transport.</item>
    ///   <item>Dispose the mTLS <see cref="HttpClient"/> and the legacy
    ///     <see cref="BackendApiClient"/> so no further outbound HTTP can race the exit.</item>
    ///   <item><c>shutdownComplete.Set()</c> — releases any ingest-dispatcher thread
    ///     parked inside the <c>ServerActionDispatcher</c> terminate-callback. Must be
    ///     LAST so the released thread sees a fully cleaned-up agent.</item>
    /// </list>
    /// </summary>
    internal static class TerminationPipeline
    {
        public static void Run(
            EnrollmentOrchestrator orchestrator,
            AuthFailureTracker authFailureTracker,
            ConsoleCancelEventHandler cancelHandler,
            EventHandler processExitHandler,
            EventHandler<EnrollmentTerminatedEventArgs> terminatedDispatch,
            EventHandler<EnrollmentTerminatedEventArgs> maxLifetimeEmitter,
            EventHandler<AuthFailureThresholdEventArgs> authThresholdHandler,
            HttpClient mtlsHttpClient,
            BackendApiClient backendApiClient,
            ManualResetEventSlim shutdownComplete,
            AgentLogger logger)
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
            orchestrator.Terminated -= terminatedDispatch;
            orchestrator.Terminated -= maxLifetimeEmitter;
            authFailureTracker.ThresholdExceeded -= authThresholdHandler;

            try { orchestrator.Stop(); }
            catch (Exception ex) { logger.Error("Orchestrator stop failed.", ex); }

            try { mtlsHttpClient.Dispose(); } catch { }
            try { backendApiClient.Dispose(); } catch { }

            // Plan §6.2 synchronous-shutdown — release any ingest-dispatcher thread
            // currently parked in the terminate callback (see ServerControlPlane's
            // OnTerminateRequested). Signalled AFTER orchestrator.Stop and client disposal
            // so the ingest thread only returns once no more HTTP work can happen.
            shutdownComplete.Set();
        }
    }
}
