#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Runtime
{
    /// <summary>
    /// M4.6.γ — event-shape tests for the Legacy-parity startup probes. We cover the pure event
    /// builders directly; the end-to-end probe runner itself needs live HTTP/UDP/tzutil (no value
    /// in unit testing — excercised via the V2-agent test-VM smoke run deferred to Follow-Up).
    /// </summary>
    public sealed class StartupEnvironmentProbesTests
    {
        private static AgentConfiguration Config(bool geoEnabled = true, bool tzAutoSet = false, string? ntpServer = null) =>
            new AgentConfiguration
            {
                SessionId = "S1",
                TenantId = "T1",
                EnableGeoLocation = geoEnabled,
                EnableTimezoneAutoSet = tzAutoSet,
                NtpServer = ntpServer,
            };

        // ================================================================= Geo success

        [Fact]
        public void BuildGeoEvent_returns_info_device_location_event_with_all_fields()
        {
            var cfg = Config();
            var loc = new GeoLocationResult
            {
                Country = "DE",
                Region = "BW",
                City = "Stuttgart",
                Loc = "48.7,9.1",
                Timezone = "Europe/Berlin",
                Source = "ipinfo.io",
            };

            var evt = StartupEnvironmentProbes.BuildGeoEvent(cfg, loc);

            Assert.Equal("device_location", evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Equal("Network", evt.Source);
            Assert.Equal("S1", evt.SessionId);
            Assert.Equal("T1", evt.TenantId);
            Assert.True(evt.ImmediateUpload);
            Assert.Contains("Stuttgart", evt.Message);
            Assert.Equal("DE", evt.Data["country"]);
            Assert.Equal("ipinfo.io", evt.Data["source"]);
        }

        // ================================================================= Geo failure

        [Fact]
        public void BuildGeoFailureEvent_returns_warning_agent_trace_with_provider_errors()
        {
            var cfg = Config();
            var attempt = new GeoLocationAttemptResult
            {
                PrimaryError = "connection refused",
                PrimaryRetryError = "DNS failure",
                FallbackError = "timeout",
            };

            var evt = StartupEnvironmentProbes.BuildGeoFailureEvent(cfg, attempt);

            Assert.Equal("agent_trace", evt.EventType);
            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Equal("geo_location_failed", evt.Data["decision"]);
            Assert.Equal("connection refused", evt.Data["primaryError"]);
            Assert.Equal("DNS failure", evt.Data["primaryRetryError"]);
            Assert.Equal("timeout", evt.Data["fallbackError"]);
        }

        [Fact]
        public void BuildGeoFailureEvent_handles_null_attempt_gracefully()
        {
            var evt = StartupEnvironmentProbes.BuildGeoFailureEvent(Config(), attempt: null);

            Assert.Equal("agent_trace", evt.EventType);
            Assert.Equal("unknown", evt.Data["primaryError"]);
            Assert.Equal("unknown", evt.Data["primaryRetryError"]);
            Assert.Equal("unknown", evt.Data["fallbackError"]);
        }

        // ================================================================= Timezone

        [Fact]
        public void BuildTimezoneEvent_info_on_success()
        {
            var tz = new TimezoneSetResult
            {
                Success = true,
                IanaTimezone = "Europe/Berlin",
                WindowsTimezoneId = "W. Europe Standard Time",
                PreviousTimezone = "UTC",
            };

            var evt = StartupEnvironmentProbes.BuildTimezoneEvent(Config(), tz);

            Assert.Equal("timezone_auto_set", evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Contains("W. Europe Standard Time", evt.Message);
            Assert.Equal(true, evt.Data["success"]);
        }

        [Fact]
        public void BuildTimezoneEvent_warning_on_failure()
        {
            var tz = new TimezoneSetResult
            {
                Success = false,
                IanaTimezone = "Europe/Berlin",
                Error = "tzutil failed",
            };

            var evt = StartupEnvironmentProbes.BuildTimezoneEvent(Config(), tz);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Contains("tzutil failed", evt.Message);
            Assert.Equal("tzutil failed", evt.Data["error"]);
        }

        // ================================================================= NTP

        [Fact]
        public void BuildNtpEvent_info_for_small_offset()
        {
            var r = new NtpCheckResult
            {
                Success = true,
                OffsetSeconds = 2.3,
                NtpTime = new System.DateTime(2026, 4, 21, 10, 0, 0, System.DateTimeKind.Utc),
                LocalTime = new System.DateTime(2026, 4, 21, 10, 0, 2, System.DateTimeKind.Utc),
            };

            var evt = StartupEnvironmentProbes.BuildNtpEvent(Config(), "time.windows.com", r);

            Assert.Equal("ntp_time_check", evt.EventType);
            Assert.Equal(EventSeverity.Info, evt.Severity);
            Assert.Equal("time.windows.com", evt.Data["ntpServer"]);
            Assert.Equal(2.3, evt.Data["offsetSeconds"]);
        }

        [Fact]
        public void BuildNtpEvent_warning_for_large_offset()
        {
            var r = new NtpCheckResult
            {
                Success = true,
                OffsetSeconds = 120.0,
                NtpTime = System.DateTime.UtcNow,
                LocalTime = System.DateTime.UtcNow,
            };

            var evt = StartupEnvironmentProbes.BuildNtpEvent(Config(), "time.windows.com", r);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
        }

        [Fact]
        public void BuildNtpEvent_warning_on_failure_with_error_detail()
        {
            var r = new NtpCheckResult
            {
                Success = false,
                Error = "DNS resolution failed",
            };

            var evt = StartupEnvironmentProbes.BuildNtpEvent(Config(), "time.windows.com", r);

            Assert.Equal(EventSeverity.Warning, evt.Severity);
            Assert.Contains("DNS resolution failed", evt.Message);
            Assert.Equal("DNS resolution failed", evt.Data["error"]);
        }
    }
}
