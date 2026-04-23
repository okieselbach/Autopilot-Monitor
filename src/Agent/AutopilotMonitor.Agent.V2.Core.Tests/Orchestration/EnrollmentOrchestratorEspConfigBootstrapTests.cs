using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Codex PR-1 pass-1 review (Hoch) — <see cref="EnrollmentOrchestrator.Start"/> must post
    /// <see cref="DecisionSignalKind.EspConfigDetected"/> synchronously before any collector
    /// host starts. Otherwise on SkipUser=true (device-only) flows the tracker's Shell-Core
    /// <c>esp_exiting</c> → <c>EspPhaseChanged(FinalizingSetup)</c> forward would race with
    /// <c>DeviceInfoHost.CollectAll</c> (fire-and-forget on ThreadPool) and the reducer's
    /// <c>ShouldTransitionToAwaitingHello</c> guard would block the legitimate promotion —
    /// leaving the session stuck, because <see cref="AutopilotMonitor.Agent.V2.Core.SignalAdapters.EspAndHelloTrackerAdapter"/>
    /// forwards Finalizing exactly once.
    /// </summary>
    public sealed class EnrollmentOrchestratorEspConfigBootstrapTests
    {
        private static DateTime At => new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Rig : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public VirtualClock Clock { get; } = new VirtualClock(At);
            public AgentLogger Logger { get; }
            public string StateDir { get; }
            public string TransportDir { get; }

            public Rig()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                StateDir = Path.Combine(Tmp.Path, "State");
                TransportDir = Path.Combine(Tmp.Path, "Transport");
            }

            public EnrollmentOrchestrator Build() =>
                new EnrollmentOrchestrator(
                    sessionId: "S1",
                    tenantId: "T1",
                    stateDirectory: StateDir,
                    transportDirectory: TransportDir,
                    clock: Clock,
                    logger: Logger,
                    uploader: new FakeBackendTelemetryUploader(),
                    classifiers: new List<IClassifier>(),
                    drainInterval: TimeSpan.FromDays(1),
                    terminalDrainTimeout: TimeSpan.FromSeconds(2));

            public IReadOnlyList<DecisionSignal> ReadSignalLog()
            {
                var path = Path.Combine(StateDir, "signal-log.jsonl");
                if (!File.Exists(path)) return Array.Empty<DecisionSignal>();
                var signals = new List<DecisionSignal>();
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    signals.Add(SignalSerializer.Deserialize(line));
                }
                return signals;
            }

            public void Dispose() => Tmp.Dispose();
        }

        [Fact]
        public void Start_posts_EspConfigDetected_before_collectors_when_FirstSync_available()
        {
            using var rig = new Rig();
            using var _ = new EspSkipConfigurationProbe.ScopedOverride(
                _log => ((bool?)true, (bool?)false));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = rig.ReadSignalLog();
            var espConfig = Assert.Single(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
            Assert.Equal("EnrollmentOrchestrator", espConfig.SourceOrigin);
            Assert.Equal("true", espConfig.Payload![SignalPayloadKeys.SkipUserEsp]);
            Assert.Equal("false", espConfig.Payload![SignalPayloadKeys.SkipDeviceEsp]);
        }

        [Fact]
        public void Start_skips_EspConfigDetected_post_when_registry_returns_null_null()
        {
            // Defensive: on a machine where FirstSync has not yet been populated (edge case on
            // very early boot) the bootstrap no-ops so the SignalLog does not carry a
            // meaningless signal. The collector's later CollectAll is the backup path.
            using var rig = new Rig();
            using var _ = new EspSkipConfigurationProbe.ScopedOverride(
                _log => ((bool?)null, (bool?)null));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = rig.ReadSignalLog();
            Assert.DoesNotContain(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
        }

        [Fact]
        public void Start_posts_EspConfigDetected_with_only_skipDevice_when_SkipUser_registry_missing()
        {
            // Partial payload is valid — the reducer's per-fact set-once fills in SkipUserEsp
            // later from a subsequent post (DeviceInfoCollector.CollectEspConfiguration or
            // CollectAtEnrollmentStart).
            using var rig = new Rig();
            using var _ = new EspSkipConfigurationProbe.ScopedOverride(
                _log => ((bool?)null, (bool?)false));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = rig.ReadSignalLog();
            var espConfig = Assert.Single(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
            Assert.False(espConfig.Payload!.ContainsKey(SignalPayloadKeys.SkipUserEsp));
            Assert.Equal("false", espConfig.Payload[SignalPayloadKeys.SkipDeviceEsp]);
        }

        [Fact]
        public void Start_posts_EspConfigDetected_before_SessionStarted_ingest_reaches_reducer()
        {
            // The bootstrap post must precede any Classic reducer signal that could land
            // AwaitingHello (EspPhaseChanged(Finalizing) / EspExiting). Ordering via ordinal: the
            // bootstrap signal's SessionSignalOrdinal must be the lowest among any signal whose
            // kind could drive Classic stage promotion. SessionStarted itself is always first
            // (ordinal 0); EspConfigDetected should come right after.
            using var rig = new Rig();
            using var _ = new EspSkipConfigurationProbe.ScopedOverride(
                _log => ((bool?)true, (bool?)false));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = rig.ReadSignalLog();
            var espConfig = signals.First(s => s.Kind == DecisionSignalKind.EspConfigDetected);

            // Nothing of Kind EspPhaseChanged or EspExiting may precede EspConfigDetected.
            Assert.DoesNotContain(
                signals.TakeWhile(s => s.SessionSignalOrdinal < espConfig.SessionSignalOrdinal),
                s => s.Kind == DecisionSignalKind.EspPhaseChanged || s.Kind == DecisionSignalKind.EspExiting);
        }
    }
}
