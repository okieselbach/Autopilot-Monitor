using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Default-value + round-trip tests for the cascade-delete kill-switch + per-tenant flag
/// (Plan §1 P8). Both flags default to false so the new V2 cascade pipeline is opt-in;
/// flipping the kill-switch is the global emergency stop.
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
        // somehow defaulted to true, the cascade subsystem (and the legacy delete path) would
        // be 503'd out of the box. Default-off is the contract.
        var cfg = AdminConfiguration.CreateDefault();
        Assert.False(cfg.SessionDeletionKillSwitch);
    }

    [Fact]
    public void SessionDeletionKillSwitch_true_persists_on_the_config()
    {
        var cfg = new AdminConfiguration { SessionDeletionKillSwitch = true };
        Assert.True(cfg.SessionDeletionKillSwitch);
    }

    [Fact]
    public void EnableCascadeDeleteV2_defaults_to_false_on_new_tenant_config()
    {
        var cfg = new TenantConfiguration();
        Assert.False(cfg.EnableCascadeDeleteV2);
    }

    [Fact]
    public void EnableCascadeDeleteV2_defaults_to_false_on_CreateDefault()
    {
        var cfg = TenantConfiguration.CreateDefault("00000000-0000-0000-0000-000000000000");
        Assert.False(cfg.EnableCascadeDeleteV2);
    }

    [Fact]
    public void EnableCascadeDeleteV2_true_persists_on_the_config()
    {
        var cfg = new TenantConfiguration { EnableCascadeDeleteV2 = true };
        Assert.True(cfg.EnableCascadeDeleteV2);
    }
}
