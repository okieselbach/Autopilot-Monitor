using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.SystemSignals;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Collectors
{
    public class AadJoinInfoTests
    {
        [Theory]
        [InlineData("foouser@contoso.onmicrosoft.com", true)]
        [InlineData("FOOUSER@CONTOSO.onmicrosoft.com", true)]
        [InlineData("FooUser@tenant.com", true)]
        [InlineData("autopilot@contoso.onmicrosoft.com", true)]
        [InlineData("AutoPilot@tenant.com", true)]
        [InlineData("AUTOPILOT@xyz.com", true)]
        public void IsPlaceholderUserEmail_KnownPatterns_ReturnsTrue(string email, bool expected)
        {
            Assert.Equal(expected, AadJoinInfo.IsPlaceholderUserEmail(email));
        }

        [Theory]
        [InlineData("alice@contoso.onmicrosoft.com")]
        [InlineData("bob.smith@company.com")]
        [InlineData("foo@bar.com")]
        [InlineData("autopilotadmin@company.com")]
        [InlineData("myfoouser@company.com")]
        public void IsPlaceholderUserEmail_RealEmails_ReturnsFalse(string email)
        {
            Assert.False(AadJoinInfo.IsPlaceholderUserEmail(email));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void IsPlaceholderUserEmail_NullOrEmpty_ReturnsFalse(string email)
        {
            Assert.False(AadJoinInfo.IsPlaceholderUserEmail(email));
        }
    }
}
