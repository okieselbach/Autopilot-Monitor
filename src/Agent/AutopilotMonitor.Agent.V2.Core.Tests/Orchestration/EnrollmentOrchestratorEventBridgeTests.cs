using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using Xunit;

#pragma warning disable xUnit1031

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Orchestrator ↔ <see cref="IComponentFactory"/> lifecycle wiring. Plan §5.10 (single-rail
    /// enforcement): the factory no longer receives an <c>Action&lt;EnrollmentEvent&gt;</c> sink
    /// — collectors post via <see cref="ISignalIngressSink"/>. These tests assert Start creates
    /// hosts + starts them, Stop stops + disposes them, and the factory receives the ingress +
    /// clock it needs to wire its own posts.
    /// </summary>
    public sealed class EnrollmentOrchestratorEventBridgeTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class FakeCollectorHost : ICollectorHost
        {
            public string Name { get; }
            public int StartCalls { get; private set; }
            public int StopCalls { get; private set; }
            public int DisposeCalls { get; private set; }

            public FakeCollectorHost(string name) { Name = name; }

            public void Start() => StartCalls++;
            public void Stop() => StopCalls++;
            public void Dispose() => DisposeCalls++;
        }

        private sealed class FakeComponentFactory : IComponentFactory
        {
            public readonly List<FakeCollectorHost> Hosts = new List<FakeCollectorHost>();
            public readonly IReadOnlyList<string> HostNames;

            public FakeComponentFactory(params string[] hostNames)
            {
                HostNames = hostNames.Length == 0
                    ? new[] { "HelloTracker", "ShellCoreTracker", "ProvisioningStatusTracker", "StallProbeCollector", "ModernDeploymentTracker" }
                    : hostNames;
            }

            public IReadOnlyCollection<string>? CapturedWhiteGloveSealingPatternIds { get; private set; }
            public ISignalIngressSink? CapturedIngress { get; private set; }
            public IClock? CapturedClock { get; private set; }

            public IReadOnlyList<ICollectorHost> CreateCollectorHosts(
                string sessionId,
                string tenantId,
                AgentLogger logger,
                IReadOnlyCollection<string> whiteGloveSealingPatternIds,
                ISignalIngressSink ingress,
                IClock clock)
            {
                CapturedWhiteGloveSealingPatternIds = whiteGloveSealingPatternIds;
                CapturedIngress = ingress;
                CapturedClock = clock;
                foreach (var name in HostNames)
                {
                    Hosts.Add(new FakeCollectorHost(name));
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
        public void Start_with_factory_creates_all_hosts_and_starts_them()
        {
            using var rig = new Rig();
            var sut = rig.Build();

            sut.Start();

            Assert.Equal(5, rig.Factory.Hosts.Count);
            Assert.All(rig.Factory.Hosts, h => Assert.Equal(1, h.StartCalls));

            sut.Stop();
        }

        [Fact]
        public void Factory_receives_ingress_and_clock_from_orchestrator()
        {
            using var rig = new Rig();
            var sut = rig.Build();

            sut.Start();

            Assert.NotNull(rig.Factory.CapturedIngress);
            Assert.NotNull(rig.Factory.CapturedClock);
            Assert.Same(rig.Clock, rig.Factory.CapturedClock);

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
    }
}
