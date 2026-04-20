using System;
using AutopilotMonitor.Agent.V2.Core.Telemetry.Events;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.Events
{
    public sealed class BackPressureEventObserverTests
    {
        private static readonly DateTime At = new DateTime(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public FakeTelemetryTransport Transport { get; } = new FakeTelemetryTransport();
            public VirtualClock Clock { get; } = new VirtualClock(At);
            public EventSequenceCounter Counter { get; }
            public TelemetryEventEmitter Inner { get; }
            public BackPressureEventObserver Sut { get; }

            public Rig()
            {
                Counter = new EventSequenceCounter(new EventSequencePersistence(Tmp.File("seq.json")));
                Inner = new TelemetryEventEmitter(Transport, Counter, "S1", "T1");
                Sut = new BackPressureEventObserver(Inner, Clock);
            }

            public void Dispose() => Tmp.Dispose();
        }

        [Fact]
        public void OnBackPressure_emits_ingress_backpressure_event_with_warning_severity()
        {
            using var r = new Rig();
            r.Sut.OnBackPressure(origin: "ImeLogTracker", channelCapacity: 256, queueLength: 256, blockDuration: TimeSpan.FromMilliseconds(137));

            Assert.Equal(1, r.Transport.EnqueueCount);
            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal("ingress_backpressure", (string?)parsed["EventType"]);
            Assert.Equal("Warning", (string?)parsed["SeverityString"]);
            Assert.Equal("signal_ingress", (string?)parsed["Source"]);
        }

        [Fact]
        public void Data_payload_carries_origin_capacity_queueLength_and_blockDurationMs()
        {
            using var r = new Rig();
            r.Sut.OnBackPressure("Origin-A", 256, 250, TimeSpan.FromMilliseconds(42));

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            var data = (JObject)parsed["Data"]!;
            Assert.Equal("Origin-A", (string?)data["origin"]);
            Assert.Equal(256, (int)data["channelCapacity"]!);
            Assert.Equal(250, (int)data["queueLength"]!);
            Assert.Equal(42, (long)data["blockDurationMs"]!);
        }

        [Fact]
        public void Timestamp_uses_injected_clock_not_wallclock()
        {
            using var r = new Rig();
            r.Clock.SetUtcNow(new DateTime(2030, 2, 28, 23, 59, 59, 500, DateTimeKind.Utc));

            r.Sut.OnBackPressure("O", 1, 1, TimeSpan.FromMilliseconds(1));

            Assert.Equal("20300228235959500_0000000001", r.Transport.Enqueued[0].RowKey);
        }

        [Fact]
        public void Phase_is_Unknown_for_backpressure_traces()
        {
            using var r = new Rig();
            r.Sut.OnBackPressure("O", 1, 1, TimeSpan.Zero);

            var parsed = JObject.Parse(r.Transport.Enqueued[0].PayloadJson);
            Assert.Equal((int)EnrollmentPhase.Unknown, (int)parsed["PhaseNumber"]!);
        }

        [Fact]
        public void Each_call_consumes_one_Sequence()
        {
            using var r = new Rig();
            r.Sut.OnBackPressure("A", 1, 1, TimeSpan.Zero);
            r.Sut.OnBackPressure("B", 1, 1, TimeSpan.Zero);
            r.Sut.OnBackPressure("A", 1, 1, TimeSpan.Zero);

            Assert.Equal(3, r.Counter.LastAssigned);
            Assert.Equal(3, r.Transport.EnqueueCount);
        }

        [Fact]
        public void Constructor_rejects_null_dependencies()
        {
            using var tmp = new TempDirectory();
            var counter = new EventSequenceCounter(new EventSequencePersistence(tmp.File("seq.json")));
            var inner = new TelemetryEventEmitter(new FakeTelemetryTransport(), counter, "S1", "T1");

            Assert.Throws<ArgumentNullException>(() => new BackPressureEventObserver(null!, new VirtualClock(At)));
            Assert.Throws<ArgumentNullException>(() => new BackPressureEventObserver(inner, null!));
        }
    }
}
