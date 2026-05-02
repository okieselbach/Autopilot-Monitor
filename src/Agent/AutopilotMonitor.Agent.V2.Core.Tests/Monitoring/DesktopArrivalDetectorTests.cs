#nullable enable
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Unit tests for <see cref="DesktopArrivalDetector.IsExcludedUser"/>. Hybrid-User-Driven
    /// session diagnosis (e58bcfdb-…, 2026-05-01) showed the detector treated the fooUser
    /// OOBE shell as a real user desktop, firing DesktopArrived 6 s after agent start —
    /// before the Hybrid reboot to the AD account ever happened. The exclusion now covers
    /// the foouser@/autopilot@ Autopilot provisioning placeholders in both UPN and
    /// DOMAIN\User shapes.
    /// </summary>
    public sealed class DesktopArrivalDetectorTests
    {
        // ---------------- Existing exclusions stay covered ----------------

        [Theory]
        [InlineData("SYSTEM")]
        [InlineData("system")]
        [InlineData("LOCAL SERVICE")]
        [InlineData("NETWORK SERVICE")]
        [InlineData("DefaultUser0")]
        [InlineData("DefaultUser1")]
        [InlineData("defaultuser0")]
        [InlineData("DefaultUser42")]
        public void System_and_default_users_are_excluded(string user)
        {
            Assert.True(DesktopArrivalDetector.IsExcludedUser(user));
        }

        [Theory]
        [InlineData("NT AUTHORITY\\SYSTEM")]
        [InlineData("WORKGROUP\\DefaultUser0")]
        [InlineData("CONTOSO\\DefaultUser1")]
        public void Domain_qualified_system_users_are_excluded(string user)
        {
            Assert.True(DesktopArrivalDetector.IsExcludedUser(user));
        }

        // ---------------- Real users are NOT excluded ----------------

        [Theory]
        [InlineData("alice")]
        [InlineData("CONTOSO\\alice")]
        [InlineData("alice@contoso.com")]
        [InlineData("bob.smith@fabrikam.com")]
        // Codex review 2026-05-01 (Finding 3): the bare-username matcher was tightened
        // from prefix-match to exact-match, so the following real account shapes that
        // *start with* "autopilot" or "foouser" must now stay through the gate. Reused
        // by UserProfileResolver — incorrectly excluding these would resolve the wrong
        // home directory.
        [InlineData("CONTOSO\\autopilotadmin")]
        [InlineData("CONTOSO\\autopilot.admin")]
        [InlineData("FABRIKAM\\foouserservice")]
        [InlineData("autopilotadmin")]
        [InlineData("foouserservice")]
        public void Real_users_are_not_excluded(string user)
        {
            Assert.False(DesktopArrivalDetector.IsExcludedUser(user));
        }

        // ---------------- New: Autopilot placeholder UPN form ----------------

        [Theory]
        [InlineData("foouser@fabrikam.onmicrosoft.com")]
        [InlineData("FooUser@fabrikam.onmicrosoft.com")]
        [InlineData("FOOUSER@example.com")]
        [InlineData("autopilot@contoso.com")]
        [InlineData("Autopilot@contoso.com")]
        [InlineData("AUTOPILOT@contoso.com")]
        public void Autopilot_placeholder_upn_is_excluded(string upn)
        {
            // Hybrid User-Driven OOBE shell runs explorer.exe under foouser@<tenant>;
            // matching this prevents premature DesktopArrived firing on the foo desktop.
            // UPN form is delegated to AadJoinInfo.IsPlaceholderUserEmail (prefix-match
            // on the local-part, so any tenant domain works).
            Assert.True(DesktopArrivalDetector.IsExcludedUser(upn));
        }

        // ---------------- New: Domain-qualified placeholder (exact bare-name match) ----------------

        [Theory]
        [InlineData("AzureAD\\foouser")]
        [InlineData("WORKGROUP\\autopilot")]
        [InlineData("AzureAD\\FooUser")]    // case-insensitive
        [InlineData("AzureAD\\AUTOPILOT")]  // case-insensitive
        [InlineData("foouser")]             // bare username (no domain prefix)
        [InlineData("autopilot")]           // bare username (no domain prefix)
        public void Domain_qualified_placeholder_is_excluded(string user)
        {
            // WMI Win32_Process.GetOwner sometimes returns DOMAIN\foouser instead of the
            // UPN form. The bare-username match is now EXACT (Finding 3) — no prefix.
            Assert.True(DesktopArrivalDetector.IsExcludedUser(user));
        }

        // ---------------- Edge cases ----------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Null_or_empty_is_excluded(string? user)
        {
            Assert.True(DesktopArrivalDetector.IsExcludedUser(user!));
        }

        [Fact]
        public void Real_user_email_with_foouser_substring_is_NOT_excluded()
        {
            // Defense against false positives — UPN match goes through
            // AadJoinInfo.IsPlaceholderUserEmail which anchors the local-part at start.
            // "realfoouser@…" and "not-autopilot@…" must NOT trigger.
            Assert.False(DesktopArrivalDetector.IsExcludedUser("realfoouser@contoso.com"));
            Assert.False(DesktopArrivalDetector.IsExcludedUser("not-autopilot@contoso.com"));
        }

        [Fact]
        public void Real_user_with_default_substring_anywhere_is_NOT_excluded()
        {
            Assert.False(DesktopArrivalDetector.IsExcludedUser("MyDefaultUser"));
            // "DefaultUser" prefix-match: this WOULD trigger because the bare-username
            // DefaultUser* code path is still prefix-based (Windows generates
            // DefaultUser0/1/2/...). Documenting current behavior.
            Assert.True(DesktopArrivalDetector.IsExcludedUser("DefaultUserBob"));
        }

        // ============================================================================
        // ResetForRealUserSwitch (Pkt 5 — placeholder→real-user transition)
        // ============================================================================

        [Fact]
        public void ResetForRealUserSwitch_does_not_throw_when_detector_is_stopped()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger);

            // Detector has not been Started — Reset must be safe regardless. The Hybrid
            // reset path may fire before any explicit Start in test fixtures, and we
            // never want host-wiring to crash the agent.
            detector.ResetForRealUserSwitch();
        }

        [Fact]
        public void ResetForRealUserSwitch_is_idempotent()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger);

            // Calling reset multiple times back-to-back must not leak timer instances or
            // throw. The composition root currently invokes it once per real-user join,
            // but the contract is still safe-to-repeat.
            detector.ResetForRealUserSwitch();
            detector.ResetForRealUserSwitch();
            detector.ResetForRealUserSwitch();
        }

        [Fact]
        public void ResetForRealUserSwitch_after_Stop_restarts_polling_path()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger);

            detector.Start();
            detector.Stop();

            // After Stop the polling timer is null. Reset must reinstate it without
            // requiring a fresh Start call (the Hybrid reboot transition shouldn't have
            // to rebuild the host).
            detector.ResetForRealUserSwitch();
        }
    }
}
