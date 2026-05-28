using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure-function tests for the runaway-session auto-action decision gate.
/// A full <see cref="MaintenanceService"/> smoke test would have to build ~16
/// dependencies; the boundary logic that decides Block vs Kill vs no-action is
/// the part most likely to silently break (off-by-one, casing drift, mis-cast
/// of an "Off" mode), so it lives behind <see cref="MaintenanceService.DecideAutoAction"/>
/// — analog to <c>ClassifyCertExpiryTier</c>.
/// </summary>
public class MaintenanceServiceExcessiveEventAutoActionTests
{
    [Fact]
    public void DecideAutoAction_ModeOff_ReturnsNull()
    {
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 9999, autoMode: "Off", autoThreshold: 2500));
    }

    [Fact]
    public void DecideAutoAction_ThresholdZero_IsAlwaysDisabled()
    {
        // Defensive: 0 disables the feature even when the mode is set, so an admin can
        // park the mode without losing the value, and the warn path keeps running alone.
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 99999, autoMode: "Block", autoThreshold: 0));
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 99999, autoMode: "Kill", autoThreshold: 0));
    }

    [Fact]
    public void DecideAutoAction_NegativeThreshold_IsDisabled()
    {
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 99999, autoMode: "Block", autoThreshold: -1));
    }

    [Theory]
    [InlineData(2500)]   // exactly at threshold → not yet
    [InlineData(2499)]
    [InlineData(0)]
    public void DecideAutoAction_BelowOrAtThreshold_ReturnsNull(int eventCount)
    {
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount, autoMode: "Block", autoThreshold: 2500));
    }

    [Theory]
    [InlineData(2501)]
    [InlineData(3000)]
    [InlineData(int.MaxValue)]
    public void DecideAutoAction_AboveThreshold_Block_ReturnsBlock(int eventCount)
    {
        Assert.Equal("Block", MaintenanceService.DecideAutoAction(eventCount, autoMode: "Block", autoThreshold: 2500));
    }

    [Fact]
    public void DecideAutoAction_AboveThreshold_Kill_ReturnsKill()
    {
        Assert.Equal("Kill", MaintenanceService.DecideAutoAction(eventCount: 5000, autoMode: "Kill", autoThreshold: 2500));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("BLOCK")]
    [InlineData(" Block ")]
    public void DecideAutoAction_ToleratesCasingAndPaddingForBlock(string mode)
    {
        // Storage round-trips may surface different casings, and admin imports could carry
        // padding from CSV cells. Normalize so the gate isn't silently disabled.
        Assert.Equal("Block", MaintenanceService.DecideAutoAction(eventCount: 5000, autoMode: mode, autoThreshold: 2500));
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("KILL")]
    public void DecideAutoAction_ToleratesCasingForKill(string mode)
    {
        Assert.Equal("Kill", MaintenanceService.DecideAutoAction(eventCount: 5000, autoMode: mode, autoThreshold: 2500));
    }

    [Theory]
    [InlineData("Suspend")]
    [InlineData("Quarantine")]
    [InlineData("")]
    [InlineData(null)]
    public void DecideAutoAction_UnknownMode_FailsClosed(string? mode)
    {
        // Unknown values (typo, future enum extension not yet wired here) must NOT execute
        // — better to no-op than block/kill on an unintended config.
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 5000, autoMode: mode, autoThreshold: 2500));
    }
}
