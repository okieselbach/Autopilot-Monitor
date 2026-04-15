using AutopilotMonitor.Agent.Core.Monitoring.Runtime;
using Xunit;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Flows;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;
using AutopilotMonitor.Agent.Core.Monitoring.Collectors;

namespace AutopilotMonitor.Agent.Core.Tests.Core
{
    /// <summary>
    /// Tests for CollectorCoordinator's static classification logic.
    /// Prevents: real enrollment events being misclassified as periodic (no idle reset),
    /// or periodic events resetting idle timers (masking stalled enrollment).
    /// </summary>
    public class CollectorCoordinatorTests
    {
        // -- Periodic events must NOT reset idle tracking --

        [Fact]
        public void IsPeriodicEvent_PerformanceSnapshot_True()
        {
            Assert.True(CollectorCoordinator.IsPeriodicEvent("performance_snapshot"));
        }

        [Fact]
        public void IsPeriodicEvent_AgentMetricsSnapshot_True()
        {
            Assert.True(CollectorCoordinator.IsPeriodicEvent("agent_metrics_snapshot"));
        }

        [Fact]
        public void IsPeriodicEvent_PerformanceCollectorStopped_True()
        {
            Assert.True(CollectorCoordinator.IsPeriodicEvent("performance_collector_stopped"));
        }

        [Fact]
        public void IsPeriodicEvent_AgentMetricsCollectorStopped_True()
        {
            Assert.True(CollectorCoordinator.IsPeriodicEvent("agent_metrics_collector_stopped"));
        }

        [Fact]
        public void IsPeriodicEvent_StallProbeCheck_True()
        {
            Assert.True(CollectorCoordinator.IsPeriodicEvent("stall_probe_check"));
        }

        [Fact]
        public void IsPeriodicEvent_StallProbeResult_True()
        {
            Assert.True(CollectorCoordinator.IsPeriodicEvent("stall_probe_result"));
        }

        [Fact]
        public void IsPeriodicEvent_SessionStalled_True()
        {
            Assert.True(CollectorCoordinator.IsPeriodicEvent("session_stalled"));
        }

        // -- Real enrollment events must reset idle tracking --

        [Fact]
        public void IsPeriodicEvent_EspPhaseChanged_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("esp_phase_changed"));
        }

        [Fact]
        public void IsPeriodicEvent_AppInstall_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("app_install"));
        }

        [Fact]
        public void IsPeriodicEvent_EnrollmentComplete_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("enrollment_complete"));
        }

        [Fact]
        public void IsPeriodicEvent_EnrollmentFailed_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("enrollment_failed"));
        }

        [Fact]
        public void IsPeriodicEvent_AgentStarted_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("agent_started"));
        }

        [Fact]
        public void IsPeriodicEvent_DesktopArrived_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("desktop_arrived"));
        }

        [Fact]
        public void IsPeriodicEvent_WhitegloveComplete_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("whiteglove_complete"));
        }

        [Fact]
        public void IsPeriodicEvent_DoTelemetry_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("do_telemetry"));
        }

        [Fact]
        public void IsPeriodicEvent_DownloadProgress_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("download_progress"));
        }

        [Fact]
        public void IsPeriodicEvent_EmptyString_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent(""));
        }

        [Fact]
        public void IsPeriodicEvent_ArbitraryEvent_False()
        {
            Assert.False(CollectorCoordinator.IsPeriodicEvent("some_custom_event"));
        }
    }
}
