using System;
using System.Threading;
using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.Core.Tests.Helpers;
using Microsoft.Win32;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Collectors
{
    /// <summary>
    /// Integration-style tests for <see cref="AadJoinWatcher"/>.
    /// Uses a temporary HKCU sub-key because the production path is under HKLM\SYSTEM which
    /// requires admin privileges. The watcher implementation reads HKLM, so these tests
    /// instead assert the observable semantics of <see cref="AadJoinInfo.IsPlaceholderUserEmail"/>
    /// and end-to-end placeholder / real-user classification behaviour of the watcher's
    /// decision code path that is testable without HKLM writes.
    ///
    /// The full RegNotifyChangeKeyValue flow is validated by the manual syntetic test in
    /// the verification section of the plan — it requires a test VM with elevated rights.
    /// </summary>
    public class AadJoinWatcherTests
    {
        [Fact]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new AadJoinWatcher(null));
        }

        [Fact]
        public void Start_MissingRootKey_ArmsRetryTimer_DoesNotThrow()
        {
            // Running tests under a normal user cannot write HKLM — but Start() must
            // gracefully fall back to the retry timer without throwing.
            var logger = TestLogger.Instance;
            using (var watcher = new AadJoinWatcher(logger))
            {
                watcher.Start();

                // Give the retry timer one tick — we expect no crash, no events.
                Thread.Sleep(250);

                bool placeholderFired = false;
                bool realUserFired = false;
                watcher.PlaceholderUserDetected += (s, e) => placeholderFired = true;
                watcher.AadUserJoined += (s, e) => realUserFired = true;

                Thread.Sleep(250);

                Assert.False(placeholderFired);
                Assert.False(realUserFired);
            }
        }

        [Fact]
        public void Stop_IsIdempotent_MultipleCallsDoNotThrow()
        {
            var logger = TestLogger.Instance;
            var watcher = new AadJoinWatcher(logger);
            watcher.Start();
            watcher.Stop();
            watcher.Stop(); // idempotent
            watcher.Dispose();
            watcher.Dispose(); // idempotent
        }

        [Fact]
        public void Dispose_AfterStart_StopsCleanly()
        {
            var logger = TestLogger.Instance;
            var watcher = new AadJoinWatcher(logger);
            watcher.Start();
            watcher.Dispose();

            // After Dispose a further Start must throw
            Assert.Throws<ObjectDisposedException>(() => watcher.Start());
        }

        // Delegate to AadJoinInfo-level tests for placeholder-pattern recognition — same
        // code path as the watcher uses internally.
        [Theory]
        [InlineData("foouser@tenant.onmicrosoft.com", true)]
        [InlineData("FOOUSER@foo.com", true)]
        [InlineData("autopilot@tenant.com", true)]
        [InlineData("AutoPilot@tenant.com", true)]
        [InlineData("alice@contoso.com", false)]
        [InlineData("myfoouser@contoso.com", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void PlaceholderEmailClassification_MatchesAadJoinInfoRule(string email, bool expected)
        {
            Assert.Equal(expected, AadJoinInfo.IsPlaceholderUserEmail(email));
        }
    }
}
