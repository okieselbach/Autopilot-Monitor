using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Monitoring
{
    /// <summary>
    /// Sanity tests for IntegrityBypassAnalyzer. The analyzer reads HKLM/HKU, Win32_Tpm
    /// and the real filesystem — we don't mock those here. Instead we verify the event
    /// contract (type / phase / severity / required payload shape) against whatever the
    /// test host reports. On any healthy dev machine this should emit a single
    /// "integrity_bypass_analysis" event with Phase=Unknown and the documented payload.
    /// </summary>
    public class IntegrityBypassAnalyzerTests
    {
        private IntegrityBypassAnalyzer CreateAnalyzer(List<EnrollmentEvent> sink)
        {
            return new IntegrityBypassAnalyzer(
                sessionId: "test-session",
                tenantId: "test-tenant",
                emitEvent: evt => sink.Add(evt),
                logger: TestLogger.Instance);
        }

        [Fact]
        public void AnalyzeAtStartup_EmitsSingleIntegrityBypassEvent_WithUnknownPhase()
        {
            var events = new List<EnrollmentEvent>();
            var analyzer = CreateAnalyzer(events);

            analyzer.AnalyzeAtStartup();

            var evt = Assert.Single(events);
            Assert.Equal("integrity_bypass_analysis", evt.EventType);
            Assert.Equal(EnrollmentPhase.Unknown, evt.Phase);
            Assert.Equal("IntegrityBypassAnalyzer", evt.Source);
            Assert.Equal("test-session", evt.SessionId);
            Assert.Equal("test-tenant", evt.TenantId);
            Assert.NotNull(evt.Data);
            Assert.True(evt.Data.ContainsKey("severity"));
            Assert.True(evt.Data.ContainsKey("finding"));
            Assert.True(evt.Data.ContainsKey("findings"));
            Assert.True(evt.Data.ContainsKey("triggered_at"));
            Assert.Equal("startup", evt.Data["triggered_at"]);
            Assert.True(evt.Data.ContainsKey("checks"));
        }

        [Fact]
        public void AnalyzeAtShutdown_EmitsSingleIntegrityBypassEvent_WithShutdownTrigger()
        {
            var events = new List<EnrollmentEvent>();
            var analyzer = CreateAnalyzer(events);

            analyzer.AnalyzeAtShutdown();

            var evt = Assert.Single(events);
            Assert.Equal("integrity_bypass_analysis", evt.EventType);
            Assert.Equal(EnrollmentPhase.Unknown, evt.Phase);
            Assert.Equal("shutdown", evt.Data["triggered_at"]);
        }

        [Fact]
        public void Payload_ContainsDocumentedCheckSections()
        {
            var events = new List<EnrollmentEvent>();
            var analyzer = CreateAnalyzer(events);

            analyzer.AnalyzeAtStartup();

            var evt = Assert.Single(events);
            var checks = Assert.IsAssignableFrom<Dictionary<string, object>>(evt.Data["checks"]);
            Assert.True(checks.ContainsKey("lab_config"));
            Assert.True(checks.ContainsKey("mo_setup"));
            Assert.True(checks.ContainsKey("pchc_upgrade_eligibility"));
            Assert.True(checks.ContainsKey("setup_scripts"));
            Assert.True(checks.ContainsKey("correlation"));

            // Setup scripts entry is always a list of 2 (SetupComplete + ErrorHandler)
            var scripts = Assert.IsAssignableFrom<System.Collections.IEnumerable>(checks["setup_scripts"]);
            var count = scripts.Cast<object>().Count();
            Assert.Equal(2, count);
        }

        [Fact]
        public void Severity_IsLowerCaseString()
        {
            var events = new List<EnrollmentEvent>();
            var analyzer = CreateAnalyzer(events);

            analyzer.AnalyzeAtStartup();

            var evt = Assert.Single(events);
            var severity = Assert.IsType<string>(evt.Data["severity"]);
            Assert.Equal(severity, severity.ToLowerInvariant());
            // Must be one of the documented bucket labels
            Assert.Contains(severity, new[] { "trace", "debug", "info", "warning", "error", "critical" });
        }

        [Fact]
        public void NullConstructorArgs_Throw()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new IntegrityBypassAnalyzer(null, "t", evt => { }, TestLogger.Instance));
            Assert.Throws<ArgumentNullException>(() =>
                new IntegrityBypassAnalyzer("s", null, evt => { }, TestLogger.Instance));
            Assert.Throws<ArgumentNullException>(() =>
                new IntegrityBypassAnalyzer("s", "t", null, TestLogger.Instance));
            Assert.Throws<ArgumentNullException>(() =>
                new IntegrityBypassAnalyzer("s", "t", evt => { }, null));
        }
    }
}
