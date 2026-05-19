using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Gate-coverage for the post-offboarding farewell-email send path. The actual Resend HTTP
/// call is not exercised here — the const-false arm-flag short-circuits before any client
/// construction. These tests instead guard the disarmed-by-default contract:
/// <list type="bullet">
///   <item>The arm constant defaults to <c>false</c>, so an accidental merge cannot ship
///         the placeholder template.</item>
///   <item><see cref="ResendEmailService.SendAsync"/> never throws under disarmed state —
///         not even when the API key is empty or the recipient is null. The handler's
///         fail-soft try/catch is a belt; this is the suspenders.</item>
///   <item>The disarmed code path logs a debug-level "feature disarmed" line so operators
///         have a positive signal in app insights that "the build is intentionally not
///         sending farewell emails", as opposed to a silent regression.</item>
/// </list>
/// </summary>
public sealed class ResendEmailServiceTests
{
    [Fact]
    public void OffboardFarewellEmailArmed_DefaultsToFalse()
    {
        // Guard the disarmed-by-design contract. Flipping this constant to true is the
        // explicit "scharf schalten" action the user does after finalising the template.
        // If this test fails, somebody flipped the flag — make sure the template, feedback
        // form, and unsubscribe story are all signed off before letting the change land.
        var field = typeof(ResendEmailService).GetField(
            "OffboardFarewellEmailArmed",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var armed = (bool)field!.GetRawConstantValue()!;
        Assert.False(armed,
            "OffboardFarewellEmailArmed must default to false until the offboarding farewell template + feedback-form story is signed off.");
    }

    [Fact]
    public async Task SendAsync_Disarmed_DoesNotThrow_AndLogsDisarmedDebugLine()
    {
        var logger = new CapturingLogger<ResendEmailService>();
        var sut = Build(apiKey: "any-non-empty-key", logger);

        await sut.SendAsync(
            toEmail: "ops@contoso.invalid",
            domainName: "contoso.invalid",
            tenantId: "88888888-8888-8888-8888-888888888888",
            ct: CancellationToken.None);

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("OffboardFarewellEmail disarmed"));
    }

    [Fact]
    public async Task SendAsync_Disarmed_EmptyApiKey_StillNoOps()
    {
        // Even with no API key configured the disarmed early-return fires FIRST — so the
        // missing-key debug line must NOT show up; only the disarmed line does. This pins
        // the gate ordering: disarmed > missing-key > missing-email.
        var logger = new CapturingLogger<ResendEmailService>();
        var sut = Build(apiKey: "", logger);

        await sut.SendAsync(
            toEmail: "ops@contoso.invalid",
            domainName: "contoso.invalid",
            tenantId: "88888888-8888-8888-8888-888888888888");

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("OffboardFarewellEmail disarmed"));
        Assert.DoesNotContain(logger.Entries,
            e => e.Message.Contains("RESEND_API_KEY not configured"));
    }

    [Fact]
    public async Task SendAsync_Disarmed_EmptyRecipient_StillNoOps()
    {
        var logger = new CapturingLogger<ResendEmailService>();
        var sut = Build(apiKey: "any-non-empty-key", logger);

        await sut.SendAsync(
            toEmail: "",
            domainName: "contoso.invalid",
            tenantId: "88888888-8888-8888-8888-888888888888");

        Assert.Contains(logger.Entries,
            e => e.Level == LogLevel.Debug && e.Message.Contains("OffboardFarewellEmail disarmed"));
        Assert.DoesNotContain(logger.Entries,
            e => e.Message.Contains("No notification email captured"));
    }

    private static ResendEmailService Build(string apiKey, ILogger<ResendEmailService> logger)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RESEND_API_KEY"] = apiKey,
            })
            .Build();
        return new ResendEmailService(config, logger);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, System.Exception? exception,
            System.Func<TState, System.Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
