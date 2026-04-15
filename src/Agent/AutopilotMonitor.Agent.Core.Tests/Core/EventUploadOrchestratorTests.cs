using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Configuration;
using AutopilotMonitor.Agent.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Moq;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;

namespace AutopilotMonitor.Agent.Core.Tests.Core
{
    public class EventUploadOrchestratorTests : IDisposable
    {
        private readonly AgentConfiguration _config;
        private readonly Mock<BackendApiClient> _mockApiClient;
        private readonly Mock<EmergencyReporter> _mockEmergency;
        private readonly Mock<DistressReporter> _mockDistress;
        private readonly Mock<CleanupService> _mockCleanup;
        private readonly Mock<EventSpool> _mockSpool;
        private readonly List<EnrollmentEvent> _emittedEvents;
        private readonly List<(string type, string message, Dictionary<string, object> data)> _shutdownEvents;
        private bool _terminalEventSeen;
        private int? _exitCode;

        public EventUploadOrchestratorTests()
        {
            _config = new AgentConfiguration
            {
                SessionId = "test-session",
                TenantId = "test-tenant",
                MaxBatchSize = 100,
                MaxAuthFailures = 5,
                AuthFailureTimeoutMinutes = 0,
                UploadIntervalSeconds = 30
            };

            _mockApiClient = new Mock<BackendApiClient>();
            _mockEmergency = new Mock<EmergencyReporter>(
                _mockApiClient.Object, "test-session", "test-tenant", "1.0", TestLogger.Instance);
            _mockDistress = new Mock<DistressReporter>(
                "http://localhost", "test-tenant", "TestMfg", "TestModel", "SN123", "1.0", TestLogger.Instance);
            _mockCleanup = new Mock<CleanupService>(_config, TestLogger.Instance);
            _mockSpool = new Mock<EventSpool>();

            _emittedEvents = new List<EnrollmentEvent>();
            _shutdownEvents = new List<(string, string, Dictionary<string, object>)>();
            _terminalEventSeen = false;
            _exitCode = null;
        }

        public void Dispose()
        {
            // Orchestrator disposes timers internally
        }

        private EventUploadOrchestrator CreateOrchestrator()
        {
            var orch = new Mock<EventUploadOrchestrator>(
                _config,
                TestLogger.Instance,
                _mockSpool.Object,
                _mockApiClient.Object,
                _mockEmergency.Object,
                _mockDistress.Object,
                _mockCleanup.Object,
                (Action<EnrollmentEvent>)(e => _emittedEvents.Add(e)),
                (Action<string, string, Dictionary<string, object>>)((t, m, d) => _shutdownEvents.Add((t, m, d))),
                (Func<bool>)(() => _terminalEventSeen))
            { CallBase = true };

            orch.Setup(x => x.ExitProcess(It.IsAny<int>()))
                .Callback<int>(code => _exitCode = code);

            return orch.Object;
        }

        /// <summary>
        /// Returns a Mock ServerActionDispatcher whose DispatchAsync captures the batch into the
        /// supplied callback. Used by tests that verify the synthesis contract between the orchestrator
        /// and the dispatcher (kill / admin-action → terminate_session ServerAction).
        /// </summary>
        private ServerActionDispatcher CreateCapturingDispatcher(Action<List<ServerAction>> onDispatched)
        {
            var mock = new Mock<ServerActionDispatcher>(
                _config,
                TestLogger.Instance,
                (Func<Task<bool>>)(() => Task.FromResult(true)),
                (Func<string, Task<DiagnosticsUploadResult>>)(_ => Task.FromResult(new DiagnosticsUploadResult())),
                (Func<ServerAction, Task>)(_ => Task.CompletedTask),
                (Action<EnrollmentEvent>)(_ => { }))
            { CallBase = false };

            mock.Setup(d => d.DispatchAsync(It.IsAny<List<ServerAction>>()))
                .Returns<List<ServerAction>>(batch => { onDispatched(batch); return Task.CompletedTask; });

            return mock.Object;
        }

        private List<EnrollmentEvent> CreateTestEvents(int count = 3)
        {
            return Enumerable.Range(1, count)
                .Select(i => new EnrollmentEvent
                {
                    SessionId = _config.SessionId,
                    TenantId = _config.TenantId,
                    EventType = $"test_event_{i}",
                    Timestamp = DateTime.UtcNow
                }).ToList();
        }

        // ===== Auth Failure Circuit Breaker =====

        [Fact]
        public void HandleAuthFailure_FirstFailure_401_SendsDistressWithCertRejected()
        {
            var orch = CreateOrchestrator();
            orch.HandleAuthFailure(401);

            _mockDistress.Verify(d => d.TrySendAsync(
                DistressErrorType.AuthCertificateRejected,
                It.IsAny<string>(),
                401), Times.Once);
        }

        [Fact]
        public void HandleAuthFailure_FirstFailure_403_SendsDistressWithDeviceNotRegistered()
        {
            var orch = CreateOrchestrator();
            orch.HandleAuthFailure(403);

            _mockDistress.Verify(d => d.TrySendAsync(
                DistressErrorType.DeviceNotRegistered,
                It.IsAny<string>(),
                403), Times.Once);
        }

        [Fact]
        public void HandleAuthFailure_SecondFailure_DoesNotSendDistressAgain()
        {
            var orch = CreateOrchestrator();
            orch.HandleAuthFailure(401);
            orch.HandleAuthFailure(401);

            _mockDistress.Verify(d => d.TrySendAsync(
                It.IsAny<DistressErrorType>(),
                It.IsAny<string>(),
                It.IsAny<int?>()), Times.Once);
        }

        [Fact]
        public void HandleAuthFailure_BelowThreshold_DoesNotExit()
        {
            _config.MaxAuthFailures = 5;
            var orch = CreateOrchestrator();

            for (int i = 0; i < 4; i++)
                orch.HandleAuthFailure(401);

            Assert.Null(_exitCode);
            Assert.Empty(_shutdownEvents);
        }

        [Fact]
        public void HandleAuthFailure_ReachesMaxAuthFailures_EmitsShutdownAndExits()
        {
            _config.MaxAuthFailures = 3;
            var orch = CreateOrchestrator();

            for (int i = 0; i < 3; i++)
                orch.HandleAuthFailure(401);

            Assert.Equal(1, _exitCode);
            Assert.Single(_shutdownEvents);
            Assert.Equal("auth_failure", _shutdownEvents[0].type);
            Assert.Equal("max_attempts", _shutdownEvents[0].data["shutdownTrigger"]);
        }

        [Fact]
        public void HandleAuthFailure_MaxAuthFailuresDisabled_NeverExitsOnCount()
        {
            _config.MaxAuthFailures = 0;
            var orch = CreateOrchestrator();

            for (int i = 0; i < 100; i++)
                orch.HandleAuthFailure(401);

            Assert.Null(_exitCode);
        }

        [Fact]
        public void HandleAuthFailure_TimeoutDisabled_NeverExitsOnTime()
        {
            _config.MaxAuthFailures = 0;
            _config.AuthFailureTimeoutMinutes = 0;
            var orch = CreateOrchestrator();

            for (int i = 0; i < 10; i++)
                orch.HandleAuthFailure(401);

            Assert.Null(_exitCode);
        }

        // ===== Admin Signal Processing =====

        [Fact]
        public async Task UploadEvents_KillSignal_SynthesizesTerminateActionWithForceSelfDestruct()
        {
            // After the consolidation the orchestrator no longer runs kill-specific inline logic.
            // It synthesizes a terminate_session ServerAction and hands it to the dispatcher.
            // The actual self-destruct + exit happens in MonitoringService.HandleTerminateSessionAsync
            // (see dedicated tests there). We verify the synthesis contract here.
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ReturnsAsync(new IngestEventsResponse { DeviceKillSignal = true });

            List<ServerAction> dispatched = null;
            var dispatcher = CreateCapturingDispatcher(captured => dispatched = captured);

            var orch = CreateOrchestrator();
            orch.SetServerActionDispatcher(dispatcher);
            await orch.UploadEventsAsync();

            Assert.NotNull(dispatched);
            Assert.Single(dispatched);
            Assert.Equal(ServerActionTypes.TerminateSession, dispatched[0].Type);
            Assert.Equal("true", dispatched[0].Params["forceSelfDestruct"]);
            Assert.Equal("0", dispatched[0].Params["gracePeriodSeconds"]);
            Assert.Equal("kill_signal", dispatched[0].Params["origin"]);
        }

        [Fact]
        public async Task UploadEvents_DeviceBlocked_StopsTimers_NoSelfDestruct()
        {
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ReturnsAsync(new IngestEventsResponse { DeviceBlocked = true });

            var orch = CreateOrchestrator();
            await orch.UploadEventsAsync();

            _mockCleanup.Verify(c => c.ExecuteSelfDestruct(), Times.Never);
            Assert.Null(_exitCode);
            Assert.Empty(_shutdownEvents);
        }

        [Fact]
        public async Task UploadEvents_AdminOverride_Succeeded_SynthesizesTerminateWithOutcome()
        {
            // AdminAction is a soft-shutdown path — no forceSelfDestruct. The adminOutcome param
            // tells MonitoringService.HandleTerminateSessionAsync to emit enrollment_complete
            // instead of agent_shutdown.
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ReturnsAsync(new IngestEventsResponse { AdminAction = "Succeeded" });
            _terminalEventSeen = false;

            List<ServerAction> dispatched = null;
            var dispatcher = CreateCapturingDispatcher(captured => dispatched = captured);

            var orch = CreateOrchestrator();
            orch.SetServerActionDispatcher(dispatcher);
            await orch.UploadEventsAsync();

            Assert.NotNull(dispatched);
            Assert.Single(dispatched);
            Assert.Equal(ServerActionTypes.TerminateSession, dispatched[0].Type);
            Assert.Equal("Succeeded", dispatched[0].Params["adminOutcome"]);
            Assert.Equal("admin_action", dispatched[0].Params["origin"]);
            Assert.False(dispatched[0].Params.ContainsKey("forceSelfDestruct"));
        }

        [Fact]
        public async Task UploadEvents_AdminOverride_Failed_SynthesizesTerminateWithFailedOutcome()
        {
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ReturnsAsync(new IngestEventsResponse { AdminAction = "Failed" });
            _terminalEventSeen = false;

            List<ServerAction> dispatched = null;
            var dispatcher = CreateCapturingDispatcher(captured => dispatched = captured);

            var orch = CreateOrchestrator();
            orch.SetServerActionDispatcher(dispatcher);
            await orch.UploadEventsAsync();

            Assert.NotNull(dispatched);
            Assert.Single(dispatched);
            Assert.Equal(ServerActionTypes.TerminateSession, dispatched[0].Type);
            Assert.Equal("Failed", dispatched[0].Params["adminOutcome"]);
        }

        [Fact]
        public async Task UploadEvents_AdminOverride_TerminalEventAlreadySeen_Ignored()
        {
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ReturnsAsync(new IngestEventsResponse { AdminAction = "Succeeded" });
            _terminalEventSeen = true;

            var orch = CreateOrchestrator();
            await orch.UploadEventsAsync();

            Assert.Empty(_emittedEvents);
        }

        // ===== Upload Success/Failure Tracking =====

        [Fact]
        public async Task UploadEvents_Success_RemovesEventsFromSpool()
        {
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ReturnsAsync(new IngestEventsResponse { Success = true, EventsProcessed = 3 });

            var orch = CreateOrchestrator();
            await orch.UploadEventsAsync();

            _mockSpool.Verify(s => s.RemoveEvents(events), Times.Once);
        }

        [Fact]
        public async Task UploadEvents_EmptySpool_NoApiCall()
        {
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(new List<EnrollmentEvent>());

            var orch = CreateOrchestrator();
            await orch.UploadEventsAsync();

            _mockApiClient.Verify(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()), Times.Never);
        }

        [Fact]
        public async Task UploadEvents_AuthException_TriggersHandleAuthFailure()
        {
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ThrowsAsync(new BackendAuthException("Unauthorized", 401));
            _config.MaxAuthFailures = 0; // disable exit so test doesn't trip ExitProcess

            var orch = CreateOrchestrator();
            await orch.UploadEventsAsync();

            // Distress should be sent (first auth failure)
            _mockDistress.Verify(d => d.TrySendAsync(
                DistressErrorType.AuthCertificateRejected,
                It.IsAny<string>(),
                401), Times.Once);
        }

        [Fact]
        public async Task UploadEvents_GenericException_IncrementsUploadFailureCounter()
        {
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ThrowsAsync(new Exception("Network error"));

            var orch = CreateOrchestrator();

            // Call 3 times to reach the emergency threshold
            for (int i = 0; i < EmergencyReporter.ConsecutiveFailureThreshold; i++)
                await orch.UploadEventsAsync();

            _mockEmergency.Verify(e => e.TrySendAsync(
                AgentErrorType.IngestFailed,
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<long?>()), Times.Once);
        }

        [Fact]
        public async Task UploadEvents_Success_ResetsAllFailureCounters()
        {
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);

            // First call: fail with generic exception
            _mockApiClient.SetupSequence(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .ThrowsAsync(new Exception("Network error"))
                .ReturnsAsync(new IngestEventsResponse { Success = true, EventsProcessed = 3 })
                .ThrowsAsync(new Exception("Network error again"));

            var orch = CreateOrchestrator();

            // Failure #1
            await orch.UploadEventsAsync();
            // Success — resets counter
            await orch.UploadEventsAsync();
            // Failure #1 again (counter was reset, so emergency at threshold from fresh start)
            await orch.UploadEventsAsync();

            // Emergency should NOT be sent because the counter was reset after 1 failure
            // (threshold is 3, we only had 1 failure after reset)
            _mockEmergency.Verify(e => e.TrySendAsync(
                It.IsAny<AgentErrorType>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<long?>()), Times.Never);
        }

        [Fact]
        public async Task UploadEvents_ConcurrentCall_SecondSkipped()
        {
            var events = CreateTestEvents();
            _mockSpool.Setup(s => s.GetBatch(It.IsAny<int>())).Returns(events);

            var tcs = new TaskCompletionSource<IngestEventsResponse>();
            _mockApiClient.Setup(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()))
                .Returns(tcs.Task);

            var orch = CreateOrchestrator();

            // Start first upload (will block on tcs)
            var task1 = orch.UploadEventsAsync();

            // Second call should be skipped immediately
            await orch.UploadEventsAsync();

            // Complete the first call
            tcs.SetResult(new IngestEventsResponse { Success = true, EventsProcessed = 3 });
            await task1;

            // API should only be called once (second was skipped)
            _mockApiClient.Verify(a => a.IngestEventsAsync(It.IsAny<IngestEventsRequest>()), Times.Once);
        }
    }
}
