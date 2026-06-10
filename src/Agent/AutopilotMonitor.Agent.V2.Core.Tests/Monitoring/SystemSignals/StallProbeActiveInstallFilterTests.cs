using System;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Review MON-B8: the AppWorkload active-install scan must age-filter so a wedged install whose
    /// last "EnforcementState: Installing" line lingers in the 200 KB tail stops counting as
    /// progress (it was suppressing <c>session_stalled</c> forever). Conservative: only lines with a
    /// CMTrace timestamp provably older than the freshness window are dropped; unparseable /
    /// unknown-age lines stay counted so a long-but-active installer is never mis-flagged.
    /// </summary>
    public sealed class StallProbeActiveInstallFilterTests
    {
        private const int FreshnessMinutes = 15;

        // CMTrace timestamps are LOCAL in the log and the parser converts to UTC. Build lines from a
        // local DateTime and compare against DateTime.UtcNow so the round-trip is timezone-correct.
        private static string CmTraceLine(string message, DateTime localTime) =>
            $"<![LOG[{message}]LOG]!><time=\"{localTime:HH:mm:ss.fffffff}\" date=\"{localTime:M-d-yyyy}\" " +
            "component=\"AppEnforce\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";

        [Fact]
        public void Fresh_cmtrace_install_line_is_counted()
        {
            var content = CmTraceLine("EnforcementState: Installing app X", DateTime.Now);

            var fresh = StallProbeCollector.FilterFreshActiveInstalls(content, DateTime.UtcNow, FreshnessMinutes);

            Assert.Single(fresh);
            Assert.Contains("EnforcementState: Installing", fresh[0]);
        }

        [Fact]
        public void Stale_cmtrace_install_line_is_excluded()
        {
            // Last install marker is an hour old → no longer evidence of progress.
            var content = CmTraceLine("EnforcementState: Installing app X", DateTime.Now.AddMinutes(-60));

            var fresh = StallProbeCollector.FilterFreshActiveInstalls(content, DateTime.UtcNow, FreshnessMinutes);

            Assert.Empty(fresh);
        }

        [Fact]
        public void Non_cmtrace_line_without_timestamp_stays_fresh()
        {
            // A raw line that does not parse as CMTrace has unknown age → kept (safe default).
            var content = "some prefix EnforcementState: Downloading something";

            var fresh = StallProbeCollector.FilterFreshActiveInstalls(content, DateTime.UtcNow, FreshnessMinutes);

            Assert.Single(fresh);
        }

        [Fact]
        public void Mixed_tail_keeps_only_fresh_lines()
        {
            var content = string.Join("\n", new[]
            {
                CmTraceLine("EnforcementState: Installing fresh-app", DateTime.Now),
                CmTraceLine("EnforcementState: Installing stale-app", DateTime.Now.AddMinutes(-90)),
                CmTraceLine("Nothing interesting here", DateTime.Now),
            });

            var fresh = StallProbeCollector.FilterFreshActiveInstalls(content, DateTime.UtcNow, FreshnessMinutes);

            // Only the fresh install marker survives; the stale one and the non-matching line drop.
            Assert.Single(fresh);
        }

        [Fact]
        public void Empty_or_null_content_returns_empty()
        {
            Assert.Empty(StallProbeCollector.FilterFreshActiveInstalls(null, DateTime.UtcNow, FreshnessMinutes));
            Assert.Empty(StallProbeCollector.FilterFreshActiveInstalls(string.Empty, DateTime.UtcNow, FreshnessMinutes));
        }

        [Fact]
        public void Line_without_active_install_marker_is_not_counted()
        {
            var content = CmTraceLine("DetectionState: Detected app Y", DateTime.Now);

            var fresh = StallProbeCollector.FilterFreshActiveInstalls(content, DateTime.UtcNow, FreshnessMinutes);

            Assert.Empty(fresh);
        }
    }
}
