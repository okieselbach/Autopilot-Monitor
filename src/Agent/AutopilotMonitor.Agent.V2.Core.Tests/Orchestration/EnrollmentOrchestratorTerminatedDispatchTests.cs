using System;
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Codex review follow-up (Finding 2, 2026-04-30) — the <see cref="EnrollmentOrchestrator.Terminated"/>
    /// event must NOT fire on the SignalIngress worker thread. The previous synchronous
    /// dispatch was a deadlock-by-design for any handler that:
    /// <list type="bullet">
    ///   <item>Posts new lifecycle events back to the ingress (the worker can only process
    ///         them after the handler returns), AND</item>
    ///   <item>Then waits for those events to drain (the ingress can only drain on the
    ///         worker thread, which the handler is blocking).</item>
    /// </list>
    /// The orchestrator now wraps the synchronous <c>Terminated?.Invoke</c> in a
    /// <see cref="System.Threading.Tasks.Task.Run"/> so the worker is freed immediately and
    /// the handler runs on a thread-pool thread that CAN wait for ingress drain.
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class EnrollmentOrchestratorTerminatedDispatchTests
    {
        private static DateTime At => new DateTime(2026, 4, 30, 10, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void DecisionTerminalStage_dispatches_Terminated_off_ingress_worker_thread()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            // Production wires WhiteGloveSealingClassifier — the test needs it too because
            // we drive the engine to a terminal stage via the WG fast-path.
            rig.Classifiers.Add(new WhiteGloveSealingClassifier());
            var sut = rig.Build();

            string? handlerThreadName = null;
            using var fired = new ManualResetEventSlim(false);
            sut.Terminated += (_, _) =>
            {
                handlerThreadName = Thread.CurrentThread.Name;
                fired.Set();
            };

            sut.Start();
            try
            {
                // Drive the engine to WhiteGloveSealed via the strong WG signal — fast-path
                // transitions in a single reduce step, OnDecisionTerminalStage fires.
                sut.IngressSink.Post(
                    kind: DecisionSignalKind.WhiteGloveShellCoreSuccess,
                    occurredAtUtc: At.AddMinutes(1),
                    sourceOrigin: "ShellCoreTracker",
                    evidence: new Evidence(EvidenceKind.Raw, "ShellCore-62407", "WhiteGlove_Success"));

                Assert.True(fired.Wait(TimeSpan.FromSeconds(5)),
                    "Terminated event did not fire within 5s.");

                // The SignalIngress worker thread is named "SignalIngress.Worker". The
                // handler must NOT have observed itself running there — Task.Run sends it
                // to a thread-pool worker, which has no name (null) or "ThreadPool*" name.
                Assert.NotEqual("SignalIngress.Worker", handlerThreadName);
            }
            finally { sut.Stop(); }
        }

        [Fact]
        public void Off_worker_dispatch_lets_handler_post_signals_that_get_processed()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            rig.Classifiers.Add(new WhiteGloveSealingClassifier());
            var sut = rig.Build();

            using var handlerEnteredFromTask = new ManualResetEventSlim(false);
            using var handlerCompletedDrain = new ManualResetEventSlim(false);

            sut.Terminated += (_, _) =>
            {
                handlerEnteredFromTask.Set();
                // Post a follow-up signal from inside the handler. With synchronous
                // dispatch (the OLD behaviour) this would just enqueue and never get
                // processed because the worker is blocked on us. With off-worker dispatch
                // the worker is free, processes the signal, and PendingSignalCount goes
                // back to 0.
                sut.IngressSink.Post(
                    kind: DecisionSignalKind.InformationalEvent,
                    occurredAtUtc: At.AddMinutes(2),
                    sourceOrigin: "TerminationHandlerTest",
                    evidence: new Evidence(EvidenceKind.Synthetic, "lifecycle-event", "from handler"),
                    payload: new Dictionary<string, string>
                    {
                        [SignalPayloadKeys.EventType] = "test_lifecycle_event",
                        [SignalPayloadKeys.Source] = "test",
                    });

                // Wait until the worker actually drained our just-posted signal — must
                // settle within a generous bound; if dispatch were synchronous this would
                // time out and the test would fail loudly.
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (sut.IngressPendingSignalCount > 0 && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(20);
                }
                handlerCompletedDrain.Set();
            };

            sut.Start();
            try
            {
                sut.IngressSink.Post(
                    kind: DecisionSignalKind.WhiteGloveShellCoreSuccess,
                    occurredAtUtc: At.AddMinutes(1),
                    sourceOrigin: "ShellCoreTracker",
                    evidence: new Evidence(EvidenceKind.Raw, "ShellCore-62407", "WhiteGlove_Success"));

                Assert.True(handlerEnteredFromTask.Wait(TimeSpan.FromSeconds(5)),
                    "Terminated handler did not start.");
                Assert.True(handlerCompletedDrain.Wait(TimeSpan.FromSeconds(10)),
                    "Handler-posted signal did not drain — worker is likely blocked, regression of off-worker dispatch.");
                Assert.Equal(0L, sut.IngressPendingSignalCount);
            }
            finally { sut.Stop(); }
        }
    }
}
