using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Single-rail refactor plan §5.1 — <see cref="EnrollmentOrchestrator.Start"/> accepts an
    /// optional <c>onIngressReady</c> hook that fires after the ingress worker is running but
    /// before any collector host is started. Program.cs uses this slot for agent-lifecycle
    /// signals (<c>agent_started</c>, <c>agent_version_check</c>, …) so they land on the wire
    /// with sequence numbers lower than anything the collectors produce.
    /// </summary>
    public sealed class EnrollmentOrchestratorOnIngressReadyTests
    {
        private static DateTime At => new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc);


        [Fact]
        public void Start_without_hook_still_succeeds_for_backward_compatibility()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            using var orchestrator = rig.Build();

            // No onIngressReady argument — existing callers must keep compiling and running.
            orchestrator.Start();

            Assert.NotNull(orchestrator.IngressSink);
        }

        [Fact]
        public void Start_invokes_hook_synchronously_with_a_live_ingress_sink()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            using var orchestrator = rig.Build();

            ISignalIngressSink? captured = null;
            int invocations = 0;

            orchestrator.Start(ingress =>
            {
                captured = ingress;
                invocations++;
            });

            Assert.Equal(1, invocations);
            Assert.NotNull(captured);
            // Same instance as the public IngressSink accessor (hook receives the real ingress,
            // not a relay or a wrapper).
            Assert.Same(orchestrator.IngressSink, captured);
        }

        [Fact]
        public void Hook_can_post_signals_through_the_ingress_without_throwing()
        {
            // Contract check: the hook fires after _ingress.Start(), so Post is legal — it must
            // not throw NullReferenceException or "ingress not started" on the caller thread.
            // The full signal-to-event pipeline is covered by dedicated reducer + EffectRunner
            // tests; this assertion only guards the timing contract exposed by Start.
            using var rig = new EnrollmentOrchestratorRig(At);
            using var orchestrator = rig.Build();

            Exception? captured = null;
            orchestrator.Start(ingress =>
            {
                try
                {
                    ingress.Post(
                        kind: DecisionSignalKind.InformationalEvent,
                        occurredAtUtc: rig.Clock.UtcNow,
                        sourceOrigin: "test",
                        evidence: new Evidence(EvidenceKind.Raw, "test:hook", "hook signal"),
                        payload: new Dictionary<string, string>
                        {
                            ["eventType"] = "agent_started_test",
                            ["source"] = "AutopilotMonitor.Agent.V2",
                        });
                }
                catch (Exception ex)
                {
                    captured = ex;
                }
            });

            Assert.Null(captured);
        }

        [Fact]
        public void Hook_exception_is_caught_and_start_completes_successfully()
        {
            // A broken caller hook must not brick the agent — the error is logged, Start
            // continues to launch collectors, and the orchestrator is usable afterwards.
            using var rig = new EnrollmentOrchestratorRig(At);
            using var orchestrator = rig.Build();

            orchestrator.Start(_ => throw new InvalidOperationException("boom"));

            // Post-Start, observability accessors work (= Start completed, did not throw out).
            Assert.NotNull(orchestrator.IngressSink);
        }

        [Fact]
        public void Hook_runs_exactly_once_even_if_Start_is_invoked_twice()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            using var orchestrator = rig.Build();

            int invocations = 0;
            orchestrator.Start(_ => invocations++);

            // Second Start throws per the existing idempotency guard — hook must not re-fire.
            Assert.Throws<InvalidOperationException>(() => orchestrator.Start(_ => invocations++));
            Assert.Equal(1, invocations);
        }

    }
}
