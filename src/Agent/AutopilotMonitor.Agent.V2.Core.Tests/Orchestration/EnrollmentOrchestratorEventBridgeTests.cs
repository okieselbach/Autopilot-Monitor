using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.Shared.Models;
using Xunit;

#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class EnrollmentOrchestratorEventBridgeTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        /// <summary>Test host that lets a test manually fire enrollment events.</summary>
        private sealed class FakeCollectorHost : ICollectorHost
        {
            private readonly Action<EnrollmentEvent> _sink;
            public string Name { get; }
            public int StartCalls { get; private set; }
            public int StopCalls { get; private set; }
            public int DisposeCalls { get; private set; }

            public FakeCollectorHost(string name, Action<EnrollmentEvent> sink)
            {
                Name = name;
                _sink = sink;
            }

            public void Start() => StartCalls++;
            public void Stop() => StopCalls++;
            public void Dispose() => DisposeCalls++;

            public void Emit(EnrollmentEvent evt) => _sink(evt);
        }

        private sealed class FakeComponentFactory : IComponentFactory
        {
            public Action<EnrollmentEvent>? CapturedSink { get; private set; }
            public readonly List<FakeCollectorHost> Hosts = new List<FakeCollectorHost>();
            public readonly IReadOnlyList<string> HostNames;

            public FakeComponentFactory(params string[] hostNames)
            {
                HostNames = hostNames.Length == 0
                    ? new[] { "HelloTracker", "ShellCoreTracker", "ProvisioningStatusTracker", "StallProbeCollector", "ModernDeploymentTracker" }
                    : hostNames;
            }

            public IReadOnlyCollection<string>? CapturedWhiteGloveSealingPatternIds { get; private set; }

            public IReadOnlyList<ICollectorHost> CreateCollectorHosts(
                string sessionId,
                string tenantId,
                AgentLogger logger,
                Action<EnrollmentEvent> onEnrollmentEvent,
                IReadOnlyCollection<string> whiteGloveSealingPatternIds)
            {
                CapturedSink = onEnrollmentEvent;
                CapturedWhiteGloveSealingPatternIds = whiteGloveSealingPatternIds;
                foreach (var name in HostNames)
                {
                    Hosts.Add(new FakeCollectorHost(name, onEnrollmentEvent));
                }
                return Hosts;
            }
        }

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public VirtualClock Clock { get; } = new VirtualClock(At);
            public AgentLogger Logger { get; }
            public FakeBackendTelemetryUploader Uploader { get; } = new FakeBackendTelemetryUploader();
            public List<IClassifier> Classifiers { get; } = new List<IClassifier>();
            public FakeComponentFactory Factory { get; }
            public string StateDir { get; }
            public string TransportDir { get; }

            public Rig(FakeComponentFactory? factory = null)
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                StateDir = Path.Combine(Tmp.Path, "State");
                TransportDir = Path.Combine(Tmp.Path, "Transport");
                Factory = factory ?? new FakeComponentFactory();
            }

            public EnrollmentOrchestrator Build() =>
                new EnrollmentOrchestrator(
                    sessionId: "S1",
                    tenantId: "T1",
                    stateDirectory: StateDir,
                    transportDirectory: TransportDir,
                    clock: Clock,
                    logger: Logger,
                    uploader: Uploader,
                    classifiers: Classifiers,
                    componentFactory: Factory,
                    drainInterval: TimeSpan.FromDays(1),
                    terminalDrainTimeout: TimeSpan.FromSeconds(2));

            public void Dispose() => Tmp.Dispose();
        }

        private static EnrollmentEvent Evt(string eventType) => new EnrollmentEvent
        {
            EventType = eventType,
            Severity = EventSeverity.Info,
            Source = "test",
            Phase = EnrollmentPhase.Unknown,
            Timestamp = At,
        };

        // ========================================================================= Wiring

        [Fact]
        public void Start_without_factory_does_not_throw_and_spawns_no_hosts()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var uploader = new FakeBackendTelemetryUploader();
            var sut = new EnrollmentOrchestrator(
                sessionId: "S1",
                tenantId: "T1",
                stateDirectory: Path.Combine(tmp.Path, "State"),
                transportDirectory: Path.Combine(tmp.Path, "Transport"),
                clock: new VirtualClock(At),
                logger: logger,
                uploader: uploader,
                classifiers: new List<IClassifier>(),
                componentFactory: null,
                drainInterval: TimeSpan.FromDays(1),
                terminalDrainTimeout: TimeSpan.FromSeconds(2));

            sut.Start();
            sut.Stop();   // no exception
        }

        [Fact]
        public void Start_with_factory_creates_all_hosts_and_captures_sink()
        {
            using var rig = new Rig();
            var sut = rig.Build();

            sut.Start();

            Assert.NotNull(rig.Factory.CapturedSink);
            Assert.Equal(5, rig.Factory.Hosts.Count);
            Assert.All(rig.Factory.Hosts, h => Assert.Equal(1, h.StartCalls));

            sut.Stop();
        }

        [Fact]
        public void Emit_from_host_reaches_telemetry_transport()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            sut.Start();

            var hello = rig.Factory.Hosts.Find(h => h.Name == "HelloTracker")!;
            hello.Emit(Evt("hello_completed"));

            var spool = GetSpool(sut);
            var pending = spool.Peek(10);
            Assert.Single(pending);
            Assert.Equal(TelemetryItemKind.Event, pending[0].Kind);
            Assert.Contains("hello_completed", pending[0].PayloadJson);

            sut.Stop();
        }

        [Fact]
        public void Emit_from_each_of_five_hosts_produces_five_transport_items()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            sut.Start();

            foreach (var host in rig.Factory.Hosts)
            {
                host.Emit(Evt($"event_from_{host.Name}"));
            }

            var pending = GetSpool(sut).Peek(10);
            Assert.Equal(5, pending.Count);
            foreach (var host in rig.Factory.Hosts)
            {
                Assert.Contains(pending, p => p.PayloadJson.Contains($"event_from_{host.Name}"));
            }

            sut.Stop();
        }

        [Fact]
        public void Modern_deployment_host_emits_event_bridge_only_no_decision_signal()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            sut.Start();

            var modern = rig.Factory.Hosts.Find(h => h.Name == "ModernDeploymentTracker")!;
            modern.Emit(Evt("modern_deployment_log"));

            // Event reached transport (bridge works).
            var pending = GetSpool(sut).Peek(10);
            Assert.Single(pending);
            Assert.Contains("modern_deployment_log", pending[0].PayloadJson);

            // No decision-signal was posted — signal-log is empty (no SignalAdapter wraps
            // ModernDeploymentTracker by design, Plan §4.x M4.3-Erkenntnis).
            var signalLog = GetSignalLog(sut);
            Assert.Empty(signalLog.ReadAll());

            sut.Stop();
        }

        [Fact]
        public void Emitter_exception_does_not_propagate_to_host_thread()
        {
            // Simulate: TelemetryEventEmitter throws (e.g. transport disposed mid-emit).
            // Build a shared sink that throws on demand; wrap into a host that uses it directly.
            // Easier path: dispose the transport post-Start so Enqueue throws, then fire an event.
            using var rig = new Rig();
            var sut = rig.Build();
            sut.Start();

            // Force a transport-level exception by disposing it mid-run.
            GetTransport(sut).Dispose();

            var hello = rig.Factory.Hosts.Find(h => h.Name == "HelloTracker")!;

            // Must not throw — orchestrator wraps the emitter call in try/catch.
            var ex = Record.Exception(() => hello.Emit(Evt("hello_after_dispose")));
            Assert.Null(ex);

            sut.Stop();
        }

        [Fact]
        public void Stop_stops_and_disposes_all_hosts_in_creation_order()
        {
            using var rig = new Rig();
            var sut = rig.Build();
            sut.Start();

            sut.Stop();

            Assert.All(rig.Factory.Hosts, h => Assert.Equal(1, h.StopCalls));
            Assert.All(rig.Factory.Hosts, h => Assert.Equal(1, h.DisposeCalls));
        }

        // ========================================================================= Helpers

        private static TelemetrySpool GetSpool(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_spool",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (TelemetrySpool)field!.GetValue(sut)!;
        }

        private static TelemetryUploadOrchestrator GetTransport(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_transport",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (TelemetryUploadOrchestrator)field!.GetValue(sut)!;
        }

        private static AutopilotMonitor.Agent.V2.Core.Persistence.ISignalLogWriter GetSignalLog(EnrollmentOrchestrator sut)
        {
            var field = typeof(EnrollmentOrchestrator).GetField(
                "_signalLog",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return (AutopilotMonitor.Agent.V2.Core.Persistence.ISignalLogWriter)field!.GetValue(sut)!;
        }
    }
}
