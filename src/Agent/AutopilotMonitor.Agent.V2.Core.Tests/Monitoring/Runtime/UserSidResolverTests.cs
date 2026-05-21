using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    public sealed class UserSidResolverTests
    {
        [Fact]
        public void Resolves_well_known_builtin_administrators_to_canonical_sid()
        {
            // BUILTIN\Administrators is a well-known local group with a deterministic SID on
            // every Windows installation. Using it as the round-trip oracle avoids any
            // environment-specific user-database dependency.
            Assert.True(UserSidResolver.TryResolveSid("BUILTIN\\Administrators", out var sid));
            Assert.Equal("S-1-5-32-544", sid);
        }

        [Fact]
        public void Resolves_bare_user_form_for_well_known_account()
        {
            // SYSTEM ("NT AUTHORITY\SYSTEM") is well-known with SID S-1-5-18. The resolver
            // must accept the qualified form on every Windows installation.
            Assert.True(UserSidResolver.TryResolveSid("NT AUTHORITY\\SYSTEM", out var sid));
            Assert.Equal("S-1-5-18", sid);
        }

        [Fact]
        public void Returns_false_for_obviously_invalid_account()
        {
            Assert.False(UserSidResolver.TryResolveSid("CONTOSO\\definitely-not-a-real-user-7f3e2c9a", out var sid));
            Assert.Null(sid);
        }

        [Fact]
        public void Returns_false_for_empty_input()
        {
            Assert.False(UserSidResolver.TryResolveSid(string.Empty, out var sid));
            Assert.Null(sid);
        }
    }
}
