using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Monitoring.Core;
using AutopilotMonitor.Agent.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Moq;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Core
{
    public class EnrollmentCompletionHandlerTests
    {
        private readonly AgentConfiguration _config;
        private readonly Mock<CleanupService> _mockCleanup;
        private readonly Mock<DiagnosticsPackageService> _mockDiagnostics;
        private readonly Mock<SessionPersistence> _mockPersistence;
        private readonly Mock<EventSpool> _mockSpool;
        private readonly List<EnrollmentEvent> _emittedEvents;
        private readonly List<(string type, string message, Dictionary<string, object> data)> _shutdownEvents;
        private int _uploadCallCount;
#pragma warning disable CS0414
        private bool _stopTimersCalled;
#pragma warning restore CS0414
        private bool _stopCollectorsCalled;
        private int? _shutdownAnalyzerPhase;
        private int? _exitCode;
        private int? _rebootDelaySeconds;

        public EnrollmentCompletionHandlerTests()
        {
            _config = new AgentConfiguration
            {
                SessionId = "test-session",
                TenantId = "test-tenant",
                SelfDestructOnComplete = false,
                RebootOnComplete = false,
                RebootDelaySeconds = 10,
                DiagnosticsUploadMode = "Off",
                DiagnosticsUploadEnabled = false,
                ShowEnrollmentSummary = false
            };

            var mockApiClient = new Mock<BackendApiClient>();
            _mockCleanup = new Mock<CleanupService>(_config, TestLogger.Instance);
            _mockDiagnostics = new Mock<DiagnosticsPackageService>(_config, TestLogger.Instance, mockApiClient.Object);
            _mockPersistence = new Mock<SessionPersistence>();
            _mockSpool = new Mock<EventSpool>();

            _emittedEvents = new List<EnrollmentEvent>();
            _shutdownEvents = new List<(string, string, Dictionary<string, object>)>();
            _uploadCallCount = 0;
            _stopTimersCalled = false;
            _stopCollectorsCalled = false;
            _shutdownAnalyzerPhase = null;
            _exitCode = null;
            _rebootDelaySeconds = null;
        }

        private EnrollmentCompletionHandler CreateHandler()
        {
            var mock = new Mock<EnrollmentCompletionHandler>(
                _config,
                TestLogger.Instance,
                "1.0.0",
                (Action<EnrollmentEvent>)(e => _emittedEvents.Add(e)),
                (Action<string, string, Dictionary<string, object>>)((t, m, d) => _shutdownEvents.Add((t, m, d))),
                (Func<Task>)(() => { _uploadCallCount++; return Task.CompletedTask; }),
                (Action)(() => _stopTimersCalled = true),
                _mockCleanup.Object,
                _mockDiagnostics.Object,
                _mockPersistence.Object,
                _mockSpool.Object)
            { CallBase = true };

            mock.Setup(x => x.ExitProcess(It.IsAny<int>()))
                .Callback<int>(code => _exitCode = code);
            mock.Setup(x => x.StartReboot(It.IsAny<int>()))
                .Callback<int>(delay => _rebootDelaySeconds = delay);

            return mock.Object;
        }

        private void StopCollectors() => _stopCollectorsCalled = true;
        private void RunShutdownAnalyzers(int? phase) => _shutdownAnalyzerPhase = phase;

        // ===== Diagnostics Upload Mode Logic =====

        [Fact]
        public async Task HandleEnrollmentComplete_DiagnosticsModeOff_NoDiagnosticsEmitted()
        {
            _config.DiagnosticsUploadMode = "Off";
            _config.DiagnosticsUploadEnabled = true;

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            Assert.DoesNotContain(_emittedEvents, e => e.EventType == "diagnostics_collecting");
            _mockDiagnostics.Verify(d => d.CreateAndUploadAsync(It.IsAny<bool>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_DiagnosticsEnabledFalse_NoDiagnosticsEmitted()
        {
            _config.DiagnosticsUploadMode = "Always";
            _config.DiagnosticsUploadEnabled = false;

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            Assert.DoesNotContain(_emittedEvents, e => e.EventType == "diagnostics_collecting");
        }

        [Fact]
        public async Task HandleEnrollmentComplete_OnFailure_SucceededEnrollment_Skipped()
        {
            _config.DiagnosticsUploadMode = "OnFailure";
            _config.DiagnosticsUploadEnabled = true;

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            Assert.DoesNotContain(_emittedEvents, e => e.EventType == "diagnostics_collecting");
        }

        [Fact]
        public async Task HandleEnrollmentComplete_OnFailure_FailedEnrollment_Uploads()
        {
            _config.DiagnosticsUploadMode = "OnFailure";
            _config.DiagnosticsUploadEnabled = true;
            _mockDiagnostics.Setup(d => d.CreateAndUploadAsync(false, null))
                .ReturnsAsync(new DiagnosticsUploadResult { BlobName = "test-blob.zip" });

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(false, false, StopCollectors, RunShutdownAnalyzers);

            Assert.Contains(_emittedEvents, e => e.EventType == "diagnostics_collecting");
            Assert.Contains(_emittedEvents, e => e.EventType == "diagnostics_uploaded");
            _mockDiagnostics.Verify(d => d.CreateAndUploadAsync(false, null), Times.Once);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_Always_SucceededEnrollment_Uploads()
        {
            _config.DiagnosticsUploadMode = "Always";
            _config.DiagnosticsUploadEnabled = true;
            _mockDiagnostics.Setup(d => d.CreateAndUploadAsync(true, null))
                .ReturnsAsync(new DiagnosticsUploadResult { BlobName = "test-blob.zip" });

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            Assert.Contains(_emittedEvents, e => e.EventType == "diagnostics_uploaded");
        }

        [Fact]
        public async Task HandleEnrollmentComplete_DiagnosticsUploadFailed_EmitsFailedEvent()
        {
            _config.DiagnosticsUploadMode = "Always";
            _config.DiagnosticsUploadEnabled = true;
            _mockDiagnostics.Setup(d => d.CreateAndUploadAsync(true, null))
                .ReturnsAsync(new DiagnosticsUploadResult { ErrorCode = "BlobStorageError" });

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            Assert.Contains(_emittedEvents, e => e.EventType == "diagnostics_upload_failed");
        }

        // ===== HandleEnrollmentComplete Flow =====

        [Fact]
        public async Task HandleEnrollmentComplete_Success_FullSequence()
        {
            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            Assert.True(_stopCollectorsCalled);
            _mockSpool.Verify(s => s.StopWatching(), Times.Once);
            Assert.Null(_shutdownAnalyzerPhase); // null for non-WhiteGlove
            Assert.True(_uploadCallCount >= 1);
            Assert.Single(_shutdownEvents);
            Assert.Equal("enrollment_complete", _shutdownEvents[0].type);
            Assert.Equal(true, _shutdownEvents[0].data["enrollmentSucceeded"]);
            Assert.Equal(0, _exitCode);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_Failure_EmitsEnrollmentFailed()
        {
            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(false, false, StopCollectors, RunShutdownAnalyzers);

            Assert.Single(_shutdownEvents);
            Assert.Equal("enrollment_failed", _shutdownEvents[0].type);
            Assert.Equal(false, _shutdownEvents[0].data["enrollmentSucceeded"]);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_SelfDestruct_CallsCleanupService()
        {
            _config.SelfDestructOnComplete = true;

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            _mockCleanup.Verify(c => c.ExecuteSelfDestruct(), Times.Once);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_RebootOnComplete_EmitsRebootEvent()
        {
            _config.SelfDestructOnComplete = false;
            _config.RebootOnComplete = true;
            _config.RebootDelaySeconds = 30;

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            Assert.Contains(_emittedEvents, e =>
                e.EventType == "reboot_triggered" &&
                (int)e.Data["rebootDelaySeconds"] == 30);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_NoSelfDestruct_NoReboot_JustExits()
        {
            _config.SelfDestructOnComplete = false;
            _config.RebootOnComplete = false;

            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, false, StopCollectors, RunShutdownAnalyzers);

            _mockCleanup.Verify(c => c.ExecuteSelfDestruct(), Times.Never);
            Assert.DoesNotContain(_emittedEvents, e => e.EventType == "reboot_triggered");
            Assert.Equal(0, _exitCode);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_WhiteGlovePart2_PassesPhase2()
        {
            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, isWhiteGlovePart2: true, StopCollectors, RunShutdownAnalyzers);

            Assert.Equal(2, _shutdownAnalyzerPhase);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_NotWhiteGlovePart2_PassesNull()
        {
            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(true, isWhiteGlovePart2: false, StopCollectors, RunShutdownAnalyzers);

            Assert.Null(_shutdownAnalyzerPhase);
        }

        [Fact]
        public async Task HandleEnrollmentComplete_Exception_ExitsWithCode1()
        {
            var handler = CreateHandler();
            await handler.HandleEnrollmentComplete(
                true, false,
                () => throw new InvalidOperationException("collector exploded"),
                RunShutdownAnalyzers);

            Assert.Equal(1, _exitCode);
        }

        // ===== HandleWhiteGloveComplete Flow =====

        [Fact]
        public async Task HandleWhiteGloveComplete_DrainsSpool()
        {
            var drainCalls = 0;
            // Spool returns 5 events for 2 iterations, then 0
            _mockSpool.Setup(s => s.GetCount())
                .Returns(() => drainCalls < 2 ? 5 : 0);

            var mock = new Mock<EnrollmentCompletionHandler>(
                _config,
                TestLogger.Instance,
                "1.0.0",
                (Action<EnrollmentEvent>)(e => _emittedEvents.Add(e)),
                (Action<string, string, Dictionary<string, object>>)((t, m, d) => _shutdownEvents.Add((t, m, d))),
                (Func<Task>)(() => { drainCalls++; return Task.CompletedTask; }),
                (Action)(() => _stopTimersCalled = true),
                _mockCleanup.Object,
                _mockDiagnostics.Object,
                _mockPersistence.Object,
                _mockSpool.Object)
            { CallBase = true };

            mock.Setup(x => x.ExitProcess(It.IsAny<int>()))
                .Callback<int>(code => _exitCode = code);
            mock.Setup(x => x.StartReboot(It.IsAny<int>()));

            var handler = mock.Object;
            await handler.HandleWhiteGloveComplete(StopCollectors, RunShutdownAnalyzers, () => 42L);

            Assert.True(drainCalls >= 2);
        }

        [Fact]
        public async Task HandleWhiteGloveComplete_SavesMarkerAndSequence()
        {
            _mockSpool.Setup(s => s.GetCount()).Returns(0);

            var handler = CreateHandler();
            await handler.HandleWhiteGloveComplete(StopCollectors, RunShutdownAnalyzers, () => 99L);

            _mockPersistence.Verify(p => p.SaveWhiteGloveComplete(), Times.Once);
            _mockPersistence.Verify(p => p.SaveSequence(99L), Times.Once);
        }

        [Fact]
        public async Task HandleWhiteGloveComplete_EmitsCorrectShutdownEvent()
        {
            _mockSpool.Setup(s => s.GetCount()).Returns(0);

            var handler = CreateHandler();
            await handler.HandleWhiteGloveComplete(StopCollectors, RunShutdownAnalyzers, () => 1L);

            Assert.Single(_shutdownEvents);
            Assert.Equal("whiteglove_complete", _shutdownEvents[0].type);
            Assert.Equal(true, _shutdownEvents[0].data["sessionPreserved"]);
            Assert.Equal(false, _shutdownEvents[0].data["selfDestruct"]);
            Assert.Equal(true, _shutdownEvents[0].data["willResumeOnNextBoot"]);
        }

        [Fact]
        public async Task HandleWhiteGloveComplete_RunsPhase1Analyzers()
        {
            _mockSpool.Setup(s => s.GetCount()).Returns(0);

            var handler = CreateHandler();
            await handler.HandleWhiteGloveComplete(StopCollectors, RunShutdownAnalyzers, () => 1L);

            Assert.Equal(1, _shutdownAnalyzerPhase);
        }

        [Fact]
        public async Task HandleWhiteGloveComplete_Exception_ExitsWithCode0()
        {
            _mockSpool.Setup(s => s.GetCount()).Returns(0);

            var handler = CreateHandler();
            await handler.HandleWhiteGloveComplete(
                StopCollectors,
                (int? _) => throw new InvalidOperationException("boom"),
                () => 1L);

            Assert.Equal(0, _exitCode); // graceful, not 1
        }

        [Fact]
        public async Task HandleWhiteGloveComplete_DiagnosticsPreprovSuffix()
        {
            _config.DiagnosticsUploadMode = "Always";
            _config.DiagnosticsUploadEnabled = true;
            _mockSpool.Setup(s => s.GetCount()).Returns(0);
            _mockDiagnostics.Setup(d => d.CreateAndUploadAsync(true, "preprov"))
                .ReturnsAsync(new DiagnosticsUploadResult { BlobName = "diag-preprov.zip" });

            var handler = CreateHandler();
            await handler.HandleWhiteGloveComplete(StopCollectors, RunShutdownAnalyzers, () => 1L);

            _mockDiagnostics.Verify(d => d.CreateAndUploadAsync(true, "preprov"), Times.Once);
        }

        // ===== DeleteSessionId =====

        [Fact]
        public void DeleteSessionId_DelegatesToPersistence()
        {
            var handler = CreateHandler();
            handler.DeleteSessionId();

            _mockPersistence.Verify(p => p.DeleteSession(), Times.Once);
        }

        [Fact]
        public void DeleteSessionId_PersistenceThrows_DoesNotRethrow()
        {
            _mockPersistence.Setup(p => p.DeleteSession())
                .Throws(new System.IO.IOException("locked"));

            var handler = CreateHandler();
            handler.DeleteSessionId(); // should not throw

            _mockPersistence.Verify(p => p.DeleteSession(), Times.Once);
        }
    }
}
