#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Office
{
    /// <summary>
    /// OfficeInstallDetector (Rev 2, event-driven) — the pure decision core driven via its On*
    /// entry points with synthetic snapshots (no real WMI / RegNotify / registry). Asserts: events
    /// only after a worker start, a single started, progress on real change only (no heartbeat),
    /// DO-sample folding (download-%), and completed / failed terminal resolution with version + duration.
    /// </summary>
    public sealed class OfficeInstallDetectorTests
    {
        private static readonly DateTime At = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);

        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        private sealed class Rig : IDisposable
        {
            public FakeSignalIngressSink Sink { get; }
            public VirtualClock Clock { get; }
            public OfficeInstallDetector Sut { get; }
            public OfficeC2RSnapshot Current { get; set; }
            private readonly TempDirectory _tmp;

            public Rig(OfficeC2RSnapshot initial)
            {
                _tmp = new TempDirectory();
                Sink = new FakeSignalIngressSink();
                Clock = new VirtualClock(At);
                Current = initial;
                var post = new InformationalEventPost(Sink, Clock);
                Sut = new OfficeInstallDetector("S1", "T1", post, NewLogger(_tmp.Path), Clock)
                {
                    SnapshotProvider = () => Current,
                };
            }

            public List<FakeSignalIngressSink.PostedSignal> OfficeEvents() => Sink.Posted
                .Where(p => p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et != null && et.StartsWith("office_install_", StringComparison.Ordinal))
                .ToList();

            public void Dispose() => _tmp.Dispose();
        }

        private static OfficeC2RSnapshot ActiveStreaming()
        {
            var s = new OfficeC2RSnapshot
            {
                ConfigurationKeyPresent = true,
                Channel = "Current",
                Platform = "x64",
                StreamingFinished = false,
                ActiveScenarioPresent = true,
                ActiveScenarioName = "INSTALL",
                OfficeC2RClientRunning = true,
            };
            s.Products.Add("O365ProPlusRetail");
            return s;
        }

        private static OfficeC2RSnapshot CompletedIdle(string version = "16.0.17628.20144")
        {
            var s = new OfficeC2RSnapshot
            {
                ConfigurationKeyPresent = true,
                Channel = "Current",
                Platform = "x64",
                StreamingFinished = true,
                ActiveScenarioPresent = false,
                ActiveScenarioName = null,
                OfficeC2RClientRunning = false,
                VersionToReport = version,
            };
            s.Products.Add("O365ProPlusRetail");
            return s;
        }

        private static string EventType(FakeSignalIngressSink.PostedSignal p) => p.Payload![SignalPayloadKeys.EventType];
        private static string Severity(FakeSignalIngressSink.PostedSignal p) => p.Payload![SignalPayloadKeys.Severity];
        private static Dictionary<string, object> Data(FakeSignalIngressSink.PostedSignal p)
            => Assert.IsType<Dictionary<string, object>>(p.TypedPayload);

        // ---------------------------------------------------------------- idle guard

        [Fact]
        public void Non_start_signals_while_idle_emit_nothing()
        {
            using var rig = new Rig(CompletedIdle());

            // A zero-job DO sample is not a start signal, and a registry change with no active scenario
            // (already-installed/idle device) must not open a lifecycle.
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 0, FileSize = 0, TotalBytesDownloaded = 0 });
            rig.Sut.OnRegistryChanged();

            Assert.Empty(rig.OfficeEvents());
        }

        // ---------------------------------------------------------------- started (three triggers, idempotent)

        [Fact]
        public void Worker_started_emits_exactly_one_started_with_products_and_phase()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted();

            var started = Assert.Single(rig.OfficeEvents());
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(started));
            Assert.Equal("Info", Severity(started));

            var data = Data(started);
            Assert.Equal("Installing", data["phase"]);
            Assert.Equal("INSTALL", data["scenario"]);
            Assert.Contains("O365ProPlusRetail", (List<string>)data["products"]);
            Assert.Equal("Current", data["channel"]);
            Assert.Equal("x64", data["platform"]);
            Assert.Equal("process", data["startedTrigger"]);
        }

        [Fact]
        public void Office_do_job_alone_starts_lifecycle_earliest_trigger()
        {
            // The DO-CDN download is visible long before the worker process — the first sample carrying
            // jobs is the earliest start trigger (no process, no registry scenario required).
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 2, FileSize = 1000, TotalBytesDownloaded = 100 });

            var started = Assert.Single(rig.OfficeEvents());
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(started));
            Assert.Equal("do", Data(started)["startedTrigger"]);
        }

        [Fact]
        public void Registry_scenario_alone_starts_lifecycle()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnRegistryChanged(); // snapshot has ActiveScenarioPresent = true

            var started = Assert.Single(rig.OfficeEvents());
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(started));
            Assert.Equal("registry", Data(started)["startedTrigger"]);
        }

        [Fact]
        public void Registry_change_without_active_scenario_does_not_start()
        {
            using var rig = new Rig(CompletedIdle()); // ActiveScenarioPresent = false

            rig.Sut.OnRegistryChanged();

            Assert.Empty(rig.OfficeEvents());
        }

        [Fact]
        public void Start_is_idempotent_across_all_three_signals()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 1, FileSize = 1000, TotalBytesDownloaded = 100 });
            rig.Sut.OnRegistryChanged();
            rig.Sut.OnWorkerStarted();

            var started = Assert.Single(rig.OfficeEvents());
            Assert.Equal("do", Data(started)["startedTrigger"]); // first signal wins
        }

        [Fact]
        public void Second_worker_started_is_ignored_one_lifecycle()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted();
            rig.Sut.OnWorkerStarted(); // already Active — must not emit a second started

            Assert.Single(rig.OfficeEvents());
        }

        // ---------------------------------------------------------------- progress (no heartbeat)

        [Fact]
        public void Registry_change_without_real_delta_emits_no_progress_heartbeat()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted(); // started
            rig.Sut.OnRegistryChanged(); // identical snapshot — must NOT emit progress
            rig.Sut.OnRegistryChanged();

            var events = rig.OfficeEvents();
            Assert.Single(events);
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(events[0]));
        }

        [Fact]
        public void Scenario_value_churn_alone_emits_no_progress_heartbeat()
        {
            // Field session 58d52632: during an active C2R operation the worker rewrites the
            // undocumented Scenario values many times/second and the subtree RegNotify watcher fires on
            // each write. Those raw values are NOT a surfaced progress dimension, so churn alone must NOT
            // emit progress (it previously produced bursts of identical, frozen-% progress events).
            var s1 = ActiveStreaming();
            s1.ScenarioValues["INSTALL\\CurrentOperation"] = "Downloading";
            using var rig = new Rig(s1);

            rig.Sut.OnWorkerStarted(); // started

            var s2 = ActiveStreaming();
            s2.ScenarioValues["INSTALL\\CurrentOperation"] = "Applying"; // churn only — no version/DO movement
            rig.Current = s2;
            rig.Sut.OnRegistryChanged();
            rig.Sut.OnRegistryChanged();

            var events = rig.OfficeEvents();
            Assert.Single(events);
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(events[0]));
        }

        [Fact]
        public void Version_change_emits_one_progress_event()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted(); // started

            var s2 = ActiveStreaming();
            s2.VersionToReport = "16.0.20026.20140"; // real forward movement
            rig.Current = s2;
            rig.Sut.OnRegistryChanged(); // progress

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(Constants.EventTypes.OfficeInstallProgress, EventType(events[1]));
        }

        // ---------------------------------------------------------------- DO folding

        [Fact]
        public void Office_do_sample_folds_download_percent_into_progress()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted(); // started

            rig.Sut.OnOfficeDoSample(new OfficeDoSample
            {
                JobCount = 2,
                FileSize = 1000,
                TotalBytesDownloaded = 250,
                BytesFromPeers = 100,
                BytesFromHttp = 150,
                PercentPeerCaching = 40,
                DownloadMode = 1,
            });

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            var progress = events[1];
            Assert.Equal(Constants.EventTypes.OfficeInstallProgress, EventType(progress));

            var data = Data(progress);
            Assert.Equal(250L, data["doTotalBytesDownloaded"]);
            Assert.Equal(25, data["downloadPercent"]);
            Assert.Equal(2, data["doJobCount"]);
        }

        [Fact]
        public void Repeated_identical_do_sample_emits_no_progress()
        {
            using var rig = new Rig(ActiveStreaming());
            rig.Sut.OnWorkerStarted();

            var sample = new OfficeDoSample { JobCount = 1, FileSize = 1000, TotalBytesDownloaded = 250 };
            rig.Sut.OnOfficeDoSample(sample);          // progress (bytes advanced)
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 1, FileSize = 1000, TotalBytesDownloaded = 250 }); // same bytes — no progress

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count); // started + one progress only
        }

        // ---------------------------------------------------------------- completion (filesystem proof)

        [Fact]
        public void TryFinalizeCompletion_with_core_binaries_emits_completed()
        {
            using var rig = new Rig(ActiveStreaming());
            rig.Sut.OnWorkerStarted(); // started

            // Download ended + worker gone; the integrate phase has laid down the binaries on disk.
            rig.Current = CompletedIdle();          // InstallationPath-bearing snapshot, no error
            rig.Sut.CoreBinariesProbe = _ => true;  // a core Office binary exists under root\*

            var outcome = rig.Sut.TryFinalizeCompletion();

            Assert.Equal(OfficeInstallDetector.CompletionOutcome.Completed, outcome);
            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            var completed = events[1];
            Assert.Equal(Constants.EventTypes.OfficeInstallCompleted, EventType(completed));
            Assert.Equal("Info", Severity(completed));
            Assert.Equal(true, Data(completed)["coreBinariesPresent"]);
            Assert.Equal("16.0.17628.20144", Data(completed)["versionReached"]);
        }

        [Fact]
        public void TryFinalizeCompletion_without_binaries_returns_notyet_and_emits_nothing()
        {
            using var rig = new Rig(ActiveStreaming());
            rig.Sut.OnWorkerStarted(); // started

            rig.Sut.CoreBinariesProbe = _ => false; // integrate not finished / nothing on disk

            var outcome = rig.Sut.TryFinalizeCompletion();

            Assert.Equal(OfficeInstallDetector.CompletionOutcome.NotYet, outcome);
            Assert.Single(rig.OfficeEvents()); // started only — no premature terminal
        }

        [Fact]
        public void TryFinalizeCompletion_then_binaries_appear_completes_on_retry()
        {
            using var rig = new Rig(ActiveStreaming());
            rig.Sut.OnWorkerStarted();

            rig.Sut.CoreBinariesProbe = _ => false;
            Assert.Equal(OfficeInstallDetector.CompletionOutcome.NotYet, rig.Sut.TryFinalizeCompletion());

            rig.Sut.CoreBinariesProbe = _ => true; // lay-down finished by the next probe
            Assert.Equal(OfficeInstallDetector.CompletionOutcome.Completed, rig.Sut.TryFinalizeCompletion());

            Assert.Equal(Constants.EventTypes.OfficeInstallCompleted, EventType(rig.OfficeEvents()[1]));
        }

        [Fact]
        public void TryFinalizeCompletion_with_error_code_emits_failed()
        {
            using var rig = new Rig(ActiveStreaming());
            rig.Sut.OnWorkerStarted();

            var withError = CompletedIdle();
            withError.ErrorCode = "17002";
            rig.Current = withError;
            rig.Sut.CoreBinariesProbe = _ => true; // error takes precedence over binary presence

            var outcome = rig.Sut.TryFinalizeCompletion();

            Assert.Equal(OfficeInstallDetector.CompletionOutcome.Failed, outcome);
            Assert.Equal(Constants.EventTypes.OfficeInstallFailed, EventType(rig.OfficeEvents()[1]));
        }

        [Fact]
        public void AbandonSilently_latches_terminal_without_an_event()
        {
            using var rig = new Rig(ActiveStreaming());
            rig.Sut.OnWorkerStarted(); // started

            rig.Sut.AbandonSilently();

            // No terminal event, and the lifecycle is closed — further signals do nothing.
            rig.Sut.CoreBinariesProbe = _ => true;
            Assert.Equal(OfficeInstallDetector.CompletionOutcome.NotYet, rig.Sut.TryFinalizeCompletion());
            rig.Current = ActiveStreaming();
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 1, FileSize = 100, TotalBytesDownloaded = 50 });

            Assert.Single(rig.OfficeEvents());
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(rig.OfficeEvents()[0]));
        }

        // ---------------------------------------------------------------- failed (error code only)

        [Fact]
        public void Observed_error_code_during_progress_emits_failed_error_with_code()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted();

            var withError = ActiveStreaming();
            withError.ErrorCode = "0x80070005";
            rig.Current = withError;
            rig.Sut.OnRegistryChanged(); // failed (error code)

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            var failed = events[1];
            Assert.Equal(Constants.EventTypes.OfficeInstallFailed, EventType(failed));
            Assert.Equal("Error", Severity(failed));
            Assert.Equal("0x80070005", Data(failed)["errorCode"]);
        }

        // ---------------------------------------------------------------- error-code heuristic

        [Theory]
        [InlineData("0x80070005", true)]   // hex HRESULT
        [InlineData("17002", true)]        // decimal C2R exit code
        [InlineData("-2147024891", true)]  // negative decimal HRESULT
        [InlineData("0x0", false)]         // zero hex
        [InlineData("0", false)]           // zero decimal
        [InlineData("Success", false)]     // benign textual value (e.g. Result=Success)
        [InlineData("Completed", false)]
        [InlineData("InProgress", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsNonZeroNumericCode_only_treats_nonzero_numbers_as_errors(string? value, bool expected)
        {
            Assert.Equal(expected, OfficeInstallDetector.IsNonZeroNumericCode(value!));
        }

        // ---------------------------------------------------------------- ctor

        [Fact]
        public void Ctor_rejects_null_dependencies()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var clock = new VirtualClock(At);
            var post = new InformationalEventPost(new FakeSignalIngressSink(), clock);

            Assert.Throws<ArgumentNullException>(() => new OfficeInstallDetector(null!, "T1", post, logger, clock));
            Assert.Throws<ArgumentNullException>(() => new OfficeInstallDetector("S1", "T1", post, logger, null!));
        }
    }
}
