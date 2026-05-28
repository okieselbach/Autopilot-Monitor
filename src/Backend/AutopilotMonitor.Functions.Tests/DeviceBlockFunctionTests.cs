using AutopilotMonitor.Functions.Functions.Admin;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the small body-parsing helper in <see cref="DeviceBlockFunction"/>.
/// <para>
/// The HTTP entry point is intentionally not tested here — mocking <c>HttpRequestData</c>
/// + the entire middleware chain would be more setup than the test is worth, and the
/// underlying <c>BlockedDeviceService.BlockDeviceAsync</c> session-id behavior is covered
/// by <see cref="BlockedDeviceServiceCrossInstanceTests"/>. The wire-up between the
/// function and the service is a single-line forward.
/// </para>
/// </summary>
public class DeviceBlockFunctionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void NormalizeOptionalSessionId_ReturnsNull_ForMissingOrWhitespace(string? raw)
    {
        Assert.Null(DeviceBlockFunction.NormalizeOptionalSessionId(raw));
    }

    [Theory]
    [InlineData("806f61c3-1978-4e5c-8fd7-a571cb0fe6bc", "806f61c3-1978-4e5c-8fd7-a571cb0fe6bc")]
    [InlineData("  806f61c3-1978-4e5c-8fd7-a571cb0fe6bc  ", "806f61c3-1978-4e5c-8fd7-a571cb0fe6bc")]
    public void NormalizeOptionalSessionId_TrimsValidValue(string raw, string expected)
    {
        Assert.Equal(expected, DeviceBlockFunction.NormalizeOptionalSessionId(raw));
    }
}
