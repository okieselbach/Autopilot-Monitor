using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="AdminConfigurationService.IsSessionDeletionKillSwitchActiveAsync"/>
/// — Plan §5 PR5 finding 1. The general-purpose <c>GetConfigurationAsync</c> caches for
/// 5 minutes per instance; that's wrong for an *emergency* switch. This helper bypasses the
/// cache so a flip-ON is honored across scaled-out Function-host instances within seconds,
/// and fails CLOSED on storage errors so a transient outage cannot accidentally enable
/// deletions during the incident the switch is mitigating.
/// </summary>
public class AdminConfigurationKillSwitchUncachedReadTests
{
    [Fact]
    public async Task IsSessionDeletionKillSwitchActiveAsync_returns_false_when_flag_is_false()
    {
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetAdminConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = false });

        var sut = new AdminConfigurationService(repo.Object, NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        Assert.False(await sut.IsSessionDeletionKillSwitchActiveAsync());
    }

    [Fact]
    public async Task IsSessionDeletionKillSwitchActiveAsync_returns_true_when_flag_is_true()
    {
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetAdminConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { SessionDeletionKillSwitch = true });

        var sut = new AdminConfigurationService(repo.Object, NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        Assert.True(await sut.IsSessionDeletionKillSwitchActiveAsync());
    }

    [Fact]
    public async Task IsSessionDeletionKillSwitchActiveAsync_bypasses_the_5min_cache()
    {
        // Pre-populate the cached path with flag=false so a cached read would return false.
        // Then have the repo return flag=true. The uncached path MUST observe true — that's the
        // whole point: the cache must not hide an emergency switch flip across instances.
        var cache = new MemoryCache(new MemoryCacheOptions());

        var repo = new Mock<IConfigRepository>();
        var flag = false;
        repo.Setup(r => r.GetAdminConfigurationAsync())
            .ReturnsAsync(() => new AdminConfiguration { SessionDeletionKillSwitch = flag });

        var sut = new AdminConfigurationService(repo.Object, NullLogger<AdminConfigurationService>.Instance, cache);

        // Warm the cache via the general getter — now the cache holds flag=false.
        var cached = await sut.GetConfigurationAsync();
        Assert.False(cached.SessionDeletionKillSwitch);

        // Operator flips the kill switch at the storage layer (other instance saves it).
        flag = true;

        // The cached reader would still see false — but the uncached helper MUST see true.
        var cachedAfter = await sut.GetConfigurationAsync();
        Assert.False(cachedAfter.SessionDeletionKillSwitch); // proves the cache is still serving stale
        Assert.True(await sut.IsSessionDeletionKillSwitchActiveAsync()); // uncached read sees reality
    }

    [Fact]
    public async Task IsSessionDeletionKillSwitchActiveAsync_returns_false_when_no_config_row_exists()
    {
        // Repo returns null (no admin-config row yet) — treat as "switch not active" so a
        // brand-new tenant install isn't 503'd into a non-functional state.
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetAdminConfigurationAsync()).ReturnsAsync((AdminConfiguration?)null);

        var sut = new AdminConfigurationService(repo.Object, NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        Assert.False(await sut.IsSessionDeletionKillSwitchActiveAsync());
    }

    [Fact]
    public async Task IsSessionDeletionKillSwitchActiveAsync_fails_CLOSED_on_storage_exception()
    {
        // Storage is down — for an emergency switch, blocking new deletes is the safe default.
        // Returning false here would be the dangerous case: deletes proceed during an incident.
        var repo = new Mock<IConfigRepository>();
        repo.Setup(r => r.GetAdminConfigurationAsync()).ThrowsAsync(new System.Exception("storage unavailable"));

        var sut = new AdminConfigurationService(repo.Object, NullLogger<AdminConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        Assert.True(await sut.IsSessionDeletionKillSwitchActiveAsync());
    }
}
