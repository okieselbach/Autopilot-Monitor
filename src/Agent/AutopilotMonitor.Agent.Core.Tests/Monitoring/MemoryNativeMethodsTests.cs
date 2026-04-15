using AutopilotMonitor.Agent.Core.Monitoring.Interop;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Monitoring
{
    public class MemoryNativeMethodsTests
    {
        [Fact]
        public void TryGetMemoryInfo_ReturnsPlausibleValues()
        {
            var ok = MemoryNativeMethods.TryGetMemoryInfo(out var availBytes, out var totalBytes, out var loadPercent);

            Assert.True(ok, "GlobalMemoryStatusEx should succeed on a normal Windows test host");
            Assert.True(totalBytes > 0, "total physical memory should be > 0");
            Assert.True(availBytes <= totalBytes, "available must not exceed total");
            Assert.InRange(loadPercent, 0u, 100u);
        }
    }
}
