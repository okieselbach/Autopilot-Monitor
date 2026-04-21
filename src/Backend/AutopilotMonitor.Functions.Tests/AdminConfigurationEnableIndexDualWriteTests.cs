using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Default-value + round-trip tests for <see cref="AdminConfiguration.EnableIndexDualWrite"/>
/// (Plan §M5.d). The flag gates the index-table dual-write rollout — defaulting to false
/// keeps pre-M5.d behaviour bit-exact and lets operators flip it on deliberately.
/// </summary>
public class AdminConfigurationEnableIndexDualWriteTests
{
    [Fact]
    public void EnableIndexDualWrite_defaults_to_false_on_new_config()
    {
        var cfg = new AdminConfiguration();
        Assert.False(cfg.EnableIndexDualWrite);
    }

    [Fact]
    public void EnableIndexDualWrite_defaults_to_false_on_CreateDefault()
    {
        // CreateDefault() is called when no admin-config row exists yet — if it somehow
        // enabled dual-write it would break the "default-off" contract immediately.
        var cfg = AdminConfiguration.CreateDefault();
        Assert.False(cfg.EnableIndexDualWrite);
    }

    [Fact]
    public void EnableIndexDualWrite_true_persists_on_the_config()
    {
        var cfg = new AdminConfiguration { EnableIndexDualWrite = true };
        Assert.True(cfg.EnableIndexDualWrite);
    }
}
