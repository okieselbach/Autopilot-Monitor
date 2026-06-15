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
            public OfficeInstallStatePersistence? Persistence { get; }
            private readonly TempDirectory _tmp;

            public Rig(OfficeC2RSnapshot initial, bool withPersistence = false)
            {
                _tmp = new TempDirectory();
                Sink = new FakeSignalIngressSink();
                Clock = new VirtualClock(At);
                Current = initial;
                var logger = NewLogger(_tmp.Path);
                Persistence = withPersistence ? new OfficeInstallStatePersistence(_tmp.Path, logger) : null;
                var post = new InformationalEventPost(Sink, Clock);
                Sut = new OfficeInstallDetector("S1", "T1", post, logger, Clock,
                    statePersistence: Persistence)
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

        [Fact]
        public void Started_is_emitted_before_a_synchronous_completion()
        {
            // Office CSP / Win32-wrapper specialty (field session a7525e97): Office is already on disk
            // when C2R runs, so the host's OfficeBinaryWatcher (wired here via onInstallationPathObserved)
            // completes the lifecycle synchronously from inside BeginIfIdle. started MUST still be emitted
            // first — otherwise completed-before-started breaks the install-progress duration/timer.
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var clock = new VirtualClock(At);
            var post = new InformationalEventPost(sink, clock);

            OfficeInstallDetector sut = null!;
            sut = new OfficeInstallDetector("S1", "T1", post, NewLogger(tmp.Path), clock,
                onInstallationPathObserved: _ =>
                {
                    sut.CoreBinariesProbe = __ => true; // binaries already present → immediate completion
                    sut.TryFinalizeCompletion();
                });
            var snap = ActiveStreaming();
            snap.InstallationPath = @"C:\Program Files\Microsoft Office";
            sut.SnapshotProvider = () => snap;

            sut.OnWorkerStarted();

            var office = sink.Posted
                .Where(p => p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et != null && et.StartsWith("office_install_", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(2, office.Count);
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, office[0].Payload![SignalPayloadKeys.EventType]);
            Assert.Equal(Constants.EventTypes.OfficeInstallCompleted, office[1].Payload![SignalPayloadKeys.EventType]);
        }

        // ---------------------------------------------------------------- no progress events

        [Fact]
        public void Do_samples_after_start_emit_no_progress_events()
        {
            // Progress was removed: a per-poll download-% is noise for Office (Connected-Cache delivery
            // is near-instant and the multi-job aggregate never yields a clean 0→100 bar — field session
            // 7da7dead). DO samples only update the data summarized once on completed.
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted(); // started
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 2, FileSize = 1000, TotalBytesDownloaded = 250 });
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 3, FileSize = 1000, TotalBytesDownloaded = 620 });
            rig.Sut.OnRegistryChanged();

            var events = rig.OfficeEvents();
            Assert.Single(events);
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(events[0]));
        }

        // ---------------------------------------------------------------- completion (filesystem proof)

        [Fact]
        public void TryFinalizeCompletion_with_core_binaries_emits_completed_with_do_summary()
        {
            using var rig = new Rig(ActiveStreaming());
            rig.Sut.OnWorkerStarted(); // started

            // A DO sample during the download — its breakdown is summarized once on completed.
            // MCC network: BytesFromHttp includes the cache-server bytes → pure CDN = 1000-800 = 200.
            rig.Sut.OnOfficeDoSample(new OfficeDoSample
            {
                JobCount = 3,
                FileSize = 1200,
                TotalBytesDownloaded = 1000,
                BytesFromCacheServer = 800,
                BytesFromHttp = 1000,
                BytesFromPeers = 0,
                DownloadMode = 1,
            });

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

            var summary = Assert.IsType<Dictionary<string, object>>(Data(completed)["doSummary"]);
            Assert.Equal(1000L, summary["totalBytesDownloaded"]);
            Assert.Equal(800L, summary["bytesFromCacheServer"]);
            Assert.Equal(200L, summary["bytesFromCdn"]);   // 1000 http - 800 cache
            Assert.Equal(0L, summary["bytesFromPeers"]);
            Assert.Equal(80, summary["percentFromCacheServer"]);
            Assert.Equal(20, summary["percentFromCdn"]);
            Assert.Equal(83, summary["downloadPercent"]);   // 1000 of 1200 declared
            Assert.Equal(true, summary["streamingFinished"]);
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

        // ---------------------------------------------------------------- core-binary on-disk probe

        [Fact]
        public void CoreBinariesPresentOnDisk_true_for_any_core_binary_under_enumerated_version_folder()
        {
            using var tmp = new TempDirectory();
            // {InstallationPath}\root\Office16\EXCEL.EXE — version folder enumerated, not hardcoded.
            var versionDir = System.IO.Path.Combine(tmp.Path, "root", "Office16");
            System.IO.Directory.CreateDirectory(versionDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(versionDir, "EXCEL.EXE"), "stub");

            Assert.True(OfficeInstallDetector.CoreBinariesPresentOnDisk(tmp.Path, NewLogger(tmp.Path)));
        }

        [Fact]
        public void CoreBinariesPresentOnDisk_false_when_absent_or_path_invalid()
        {
            using var tmp = new TempDirectory();
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(tmp.Path, "root", "Office16")); // no binaries
            var logger = NewLogger(tmp.Path);

            Assert.False(OfficeInstallDetector.CoreBinariesPresentOnDisk(tmp.Path, logger));
            Assert.False(OfficeInstallDetector.CoreBinariesPresentOnDisk(null, logger));
            Assert.False(OfficeInstallDetector.CoreBinariesPresentOnDisk(System.IO.Path.Combine(tmp.Path, "does-not-exist"), logger));
        }

        // ---------------------------------------------------------------- persistence across restarts

        [Fact]
        public void Started_persists_active_state_with_trigger_and_start_time()
        {
            using var rig = new Rig(ActiveStreaming(), withPersistence: true);

            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 2, FileSize = 1000, TotalBytesDownloaded = 100 });

            var state = rig.Persistence!.Load();
            Assert.NotNull(state);
            Assert.Equal(OfficeInstallStateData.StateActive, state!.State);
            Assert.Equal(At, state.StartedAtUtc);
            Assert.Equal("do", state.StartedTrigger);
            Assert.Equal(100, state.PeakDo!.TotalBytesDownloaded);
        }

        [Fact]
        public void Completed_persists_terminal_state()
        {
            using var rig = new Rig(ActiveStreaming(), withPersistence: true);
            rig.Sut.OnWorkerStarted();

            rig.Current = CompletedIdle();
            rig.Sut.CoreBinariesProbe = _ => true;
            Assert.Equal(OfficeInstallDetector.CompletionOutcome.Completed, rig.Sut.TryFinalizeCompletion());

            var state = rig.Persistence!.Load();
            Assert.Equal(OfficeInstallStateData.StateCompleted, state!.State);
            Assert.True(state.IsTerminal);
        }

        [Fact]
        public void Failed_persists_terminal_state()
        {
            using var rig = new Rig(ActiveStreaming(), withPersistence: true);
            rig.Sut.OnWorkerStarted();

            var withError = ActiveStreaming();
            withError.ErrorCode = "0x80070005";
            rig.Current = withError;
            rig.Sut.OnRegistryChanged();

            var state = rig.Persistence!.Load();
            Assert.Equal(OfficeInstallStateData.StateFailed, state!.State);
            Assert.True(state.IsTerminal);
        }

        [Fact]
        public void AbandonSilently_does_not_persist_a_terminal_state()
        {
            // AbandonSilently fires on every host dispose/shutdown. Persisting it as terminal would
            // block the next agent run from resuming the open lifecycle — and the resume is exactly
            // what delivers a completion missed across a mid-install reboot.
            using var rig = new Rig(ActiveStreaming(), withPersistence: true);
            rig.Sut.OnWorkerStarted();

            rig.Sut.AbandonSilently();

            var state = rig.Persistence!.Load();
            Assert.Equal(OfficeInstallStateData.StateActive, state!.State);
            Assert.False(state.IsTerminal);
        }

        [Fact]
        public void Active_peak_growth_is_persisted_with_throttle()
        {
            using var rig = new Rig(ActiveStreaming(), withPersistence: true);
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 2, FileSize = 1000, TotalBytesDownloaded = 100 }); // started + persisted

            // Within the 30s throttle window: peak grows in memory but the file is not rewritten.
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 2, FileSize = 1000, TotalBytesDownloaded = 300 });
            Assert.Equal(100, rig.Persistence!.Load()!.PeakDo!.TotalBytesDownloaded);

            // After the throttle window the next grown peak is persisted.
            rig.Clock.Advance(TimeSpan.FromSeconds(31));
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 2, FileSize = 1000, TotalBytesDownloaded = 600 });
            Assert.Equal(600, rig.Persistence.Load()!.PeakDo!.TotalBytesDownloaded);
        }

        [Fact]
        public void ResumeActive_completes_without_second_started_using_original_start_time_and_peak()
        {
            // Agent restart after a mid-install reboot: started went out in the previous run. The
            // resumed lifecycle must not emit a second started, and the completion proven in this
            // run reports the true total duration + the persisted DO summary.
            using var rig = new Rig(CompletedIdle(), withPersistence: true);

            rig.Sut.ResumeActive(new OfficeInstallStateData
            {
                State = OfficeInstallStateData.StateActive,
                StartedAtUtc = At.AddMinutes(-10),
                StartedTrigger = "do",
                PeakDo = new OfficeDoPeakData
                {
                    JobCount = 3,
                    FileSize = 1200,
                    TotalBytesDownloaded = 1000,
                    BytesFromHttp = 1000,
                    BytesFromCacheServer = 800,
                    DownloadMode = 1,
                },
            });
            Assert.Empty(rig.OfficeEvents()); // no second started

            // New start triggers must not re-open a window either — the lifecycle is already open.
            rig.Sut.OnWorkerStarted();
            Assert.Empty(rig.OfficeEvents());

            rig.Sut.CoreBinariesProbe = _ => true;
            Assert.Equal(OfficeInstallDetector.CompletionOutcome.Completed, rig.Sut.TryFinalizeCompletion());

            var completed = Assert.Single(rig.OfficeEvents());
            Assert.Equal(Constants.EventTypes.OfficeInstallCompleted, EventType(completed));
            Assert.Equal(600, Data(completed)["durationSeconds"]); // 10 min across the restart
            Assert.Equal("do", Data(completed)["startedTrigger"]);
            var summary = Assert.IsType<Dictionary<string, object>>(Data(completed)["doSummary"]);
            Assert.Equal(1000L, summary["totalBytesDownloaded"]);
            Assert.Equal(800L, summary["bytesFromCacheServer"]);
            Assert.Equal(200L, summary["bytesFromCdn"]);
            Assert.Equal(83, summary["downloadPercent"]);
            Assert.Equal(true, summary["streamingFinished"]);
            Assert.Equal(OfficeInstallStateData.StateCompleted, rig.Persistence!.Load()!.State);
        }

        [Fact]
        public void ResumeActive_surfaces_installation_path_for_synchronous_completion()
        {
            // Office finished installing while the agent was down: the resume reads a fresh snapshot
            // and surfaces the InstallationPath; the host's binary watcher (wired here via the
            // callback) finds the binaries immediately and completes the lifecycle synchronously —
            // the previously missed completed is delivered, with no started this run.
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var clock = new VirtualClock(At);
            var post = new InformationalEventPost(sink, clock);

            OfficeInstallDetector sut = null!;
            sut = new OfficeInstallDetector("S1", "T1", post, NewLogger(tmp.Path), clock,
                onInstallationPathObserved: _ =>
                {
                    sut.CoreBinariesProbe = __ => true;
                    sut.TryFinalizeCompletion();
                });
            var snap = CompletedIdle();
            snap.InstallationPath = @"C:\Program Files\Microsoft Office";
            sut.SnapshotProvider = () => snap;

            sut.ResumeActive(new OfficeInstallStateData
            {
                State = OfficeInstallStateData.StateActive,
                StartedAtUtc = At.AddMinutes(-5),
                StartedTrigger = "registry",
            });

            var office = sink.Posted
                .Where(p => p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et != null && et.StartsWith("office_install_", StringComparison.Ordinal))
                .ToList();
            var completed = Assert.Single(office);
            Assert.Equal(Constants.EventTypes.OfficeInstallCompleted, completed.Payload![SignalPayloadKeys.EventType]);
        }

        [Fact]
        public void ResumeActive_ignores_terminal_or_invalid_state()
        {
            using var rig = new Rig(ActiveStreaming(), withPersistence: true);

            rig.Sut.ResumeActive(new OfficeInstallStateData { State = OfficeInstallStateData.StateCompleted });
            rig.Sut.ResumeActive(new OfficeInstallStateData { State = null });

            // Still Idle — a real start trigger opens a fresh lifecycle normally.
            rig.Sut.OnWorkerStarted();
            var started = Assert.Single(rig.OfficeEvents());
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(started));
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
