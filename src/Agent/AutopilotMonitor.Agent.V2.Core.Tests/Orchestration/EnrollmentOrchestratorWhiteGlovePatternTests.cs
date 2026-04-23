using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    public sealed class EnrollmentOrchestratorWhiteGlovePatternTests
    {
        private static DateTime At => new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class CapturingFactory : IComponentFactory
        {
            public IReadOnlyCollection<string>? CapturedPatternIds { get; private set; }

            public IReadOnlyList<ICollectorHost> CreateCollectorHosts(
                string sessionId,
                string tenantId,
                AgentLogger logger,
                IReadOnlyCollection<string> whiteGloveSealingPatternIds,
                ISignalIngressSink ingress,
                IClock clock)
            {
                CapturedPatternIds = whiteGloveSealingPatternIds;
                return Array.Empty<ICollectorHost>();
            }
        }

        private EnrollmentOrchestrator Build(
            TempDirectory tmp,
            IComponentFactory? factory = null,
            IReadOnlyCollection<string>? whiteGloveSealingPatternIds = null)
        {
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            return new EnrollmentOrchestrator(
                sessionId: "S1",
                tenantId: "T1",
                stateDirectory: Path.Combine(tmp.Path, "State"),
                transportDirectory: Path.Combine(tmp.Path, "Transport"),
                clock: new VirtualClock(At),
                logger: logger,
                uploader: new FakeBackendTelemetryUploader(),
                classifiers: new List<IClassifier>(),
                componentFactory: factory,
                whiteGloveSealingPatternIds: whiteGloveSealingPatternIds,
                drainInterval: TimeSpan.FromDays(1),
                terminalDrainTimeout: TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void Null_pattern_ids_reach_factory_as_empty_collection()
        {
            using var tmp = new TempDirectory();
            var factory = new CapturingFactory();
            var sut = Build(tmp, factory, whiteGloveSealingPatternIds: null);

            sut.Start();

            Assert.NotNull(factory.CapturedPatternIds);
            Assert.Empty(factory.CapturedPatternIds!);

            sut.Stop();
        }

        [Fact]
        public void Configured_pattern_ids_reach_factory_verbatim()
        {
            using var tmp = new TempDirectory();
            var factory = new CapturingFactory();
            var ids = new[] { "WG_SEAL_1", "WG_SEAL_2", "WG_SEAL_3" };
            var sut = Build(tmp, factory, whiteGloveSealingPatternIds: ids);

            sut.Start();

            Assert.NotNull(factory.CapturedPatternIds);
            Assert.Equal(ids, factory.CapturedPatternIds);

            sut.Stop();
        }

        [Fact]
        public void Empty_pattern_ids_collection_forwards_as_empty_not_null()
        {
            using var tmp = new TempDirectory();
            var factory = new CapturingFactory();
            var sut = Build(tmp, factory, whiteGloveSealingPatternIds: Array.Empty<string>());

            sut.Start();

            Assert.NotNull(factory.CapturedPatternIds);
            Assert.Empty(factory.CapturedPatternIds!);

            sut.Stop();
        }
    }
}
