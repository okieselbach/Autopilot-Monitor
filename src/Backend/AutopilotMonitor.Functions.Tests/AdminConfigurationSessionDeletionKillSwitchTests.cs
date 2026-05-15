using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Default-value tests for the cascade-delete global kill-switch (Plan §1 P8 / §9).
/// Default-off is the contract: a freshly created AdminConfiguration row must NOT
/// halt the cascade subsystem out of the box.
/// </summary>
public class AdminConfigurationSessionDeletionKillSwitchTests
{
    [Fact]
    public void SessionDeletionKillSwitch_defaults_to_false_on_new_config()
    {
        var cfg = new AdminConfiguration();
        Assert.False(cfg.SessionDeletionKillSwitch);
    }

    [Fact]
    public void SessionDeletionKillSwitch_defaults_to_false_on_CreateDefault()
    {
        // CreateDefault() is called when no admin-config row exists yet — if the kill-switch
        // somehow defaulted to true, the cascade subsystem would be 503'd out of the box.
        var cfg = AdminConfiguration.CreateDefault();
        Assert.False(cfg.SessionDeletionKillSwitch);
    }

    [Fact]
    public void SessionDeletionKillSwitch_true_persists_on_the_config()
    {
        var cfg = new AdminConfiguration { SessionDeletionKillSwitch = true };
        Assert.True(cfg.SessionDeletionKillSwitch);
    }
}
