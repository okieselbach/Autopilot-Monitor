using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Idempotency guards on <see cref="SoftwareInventoryAnalyzer.AnalyzeAtShutdown(int?)"/>.
    /// The Part-1 shutdown snapshot is fired by two independent paths
    /// (<see cref="WhiteGloveInventoryTrigger"/> and
    /// <c>EnrollmentTerminationHandler.RunShutdownAnalyzers</c> at the WhiteGloveSealed exit) —
    /// without the per-phase guard the analyzer would emit two shutdown events for the same
    /// session, which the backend would translate to two queue messages and two correlation
    /// runs. The guard wins single-fire while keeping different phases independent.
    /// <para>
    /// These tests exercise the real <see cref="SoftwareInventoryAnalyzer"/> end-to-end (it
    /// reads the local Uninstall registry hives during <c>CollectAndNormalize</c>). They are
    /// intentionally lightweight: assertions count <c>software_inventory_analysis</c> events
    /// posted to the fake ingress, never the inventory contents themselves.
    /// </para>
    /// </summary>
    public sealed class SoftwareInventoryAnalyzerIdempotencyTests : IDisposable
    {
        private readonly TempDirectory _tmp = new();
        private readonly AgentLogger _logger;
        private readonly FakeSignalIngressSink _ingress = new();
        private readonly VirtualClock _clock = new(new DateTime(2026, 4, 30, 8, 45, 0, DateTimeKind.Utc));
        private readonly SoftwareInventoryAnalyzer _sut;

        public SoftwareInventoryAnalyzerIdempotencyTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            var post = new InformationalEventPost(_ingress, _clock);
            _sut = new SoftwareInventoryAnalyzer(
                sessionId: "session-1",
                tenantId: "tenant-1",
                post: post,
                logger: _logger);
        }

        public void Dispose()
        {
            try { _tmp.Dispose(); } catch { }
        }

        [Fact]
        public void AnalyzeAtShutdown_Part1_called_twice_emits_only_one_event()
        {
            _sut.AnalyzeAtShutdown(whiteGlovePart: 1);
            _sut.AnalyzeAtShutdown(whiteGlovePart: 1); // second call — must be a no-op

            Assert.Equal(1, CountChunkZeroEventsForPart(part: 1));
        }

        [Fact]
        public void AnalyzeAtShutdown_Part2_called_after_Part1_runs_independently()
        {
            _sut.AnalyzeAtShutdown(whiteGlovePart: 1);
            _sut.AnalyzeAtShutdown(whiteGlovePart: 2);

            Assert.Equal(1, CountChunkZeroEventsForPart(part: 1));
            Assert.Equal(1, CountChunkZeroEventsForPart(part: 2));
        }

        [Fact]
        public void AnalyzeAtShutdown_Part2_called_twice_emits_only_one_event()
        {
            _sut.AnalyzeAtShutdown(whiteGlovePart: 2);
            _sut.AnalyzeAtShutdown(whiteGlovePart: 2);

            Assert.Equal(1, CountChunkZeroEventsForPart(part: 2));
        }

        /// <summary>
        /// Counts the chunk-0 software_inventory_analysis events for a given WG part.
        /// EnrollmentEvent.Data is forwarded as TypedPayload by InformationalEventPost; the
        /// inventory analyzer always emits at least one chunk-0 event per shutdown call, so a
        /// chunk-0 count of 1 means the call was non-deduped, 0 means deduped, 2+ means the
        /// guard failed.
        /// </summary>
        private int CountChunkZeroEventsForPart(int part) =>
            _ingress.Posted
                .Where(p =>
                    p.Kind == DecisionSignalKind.InformationalEvent
                    && p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et == "software_inventory_analysis"
                    && p.TypedPayload is IDictionary<string, object> data
                    && data.TryGetValue("triggered_at", out var ta) && ta?.ToString() == "shutdown"
                    && data.TryGetValue("chunk_index", out var ci) && Convert.ToInt32(ci) == 0
                    && data.TryGetValue("whiteglove_part", out var wp) && Convert.ToInt32(wp) == part)
                .Count();
    }
}
