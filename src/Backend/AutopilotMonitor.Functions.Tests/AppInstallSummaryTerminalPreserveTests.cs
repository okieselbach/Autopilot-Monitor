using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

// Regression cover for session 59a0f7f3-... (V2 agent 2.0.396, 2026-04-27): all 11 apps
// landed with Status="InProgress" + DurationSeconds=0 even though app_install_completed
// fired and CompletedAt was set. Root cause: a fresh AppInstallSummary defaults Status to
// "InProgress" and download_progress / do_telemetry-only follow-up batches did not enter
// any Status-mutating switch case, so the upsert clobbered the prior terminal write.
public class AppInstallSummaryTerminalPreserveTests
{
    private static TableEntity ExistingEntity(
        string status,
        DateTime? completedAt = null,
        int durationSeconds = 0,
        string failureCode = "",
        string failureMessage = "")
    {
        var entity = new TableEntity("tenant", "session_App")
        {
            ["Status"] = status,
            ["DurationSeconds"] = durationSeconds,
            ["FailureCode"] = failureCode,
            ["FailureMessage"] = failureMessage
        };
        if (completedAt.HasValue)
            entity["CompletedAt"] = DateTime.SpecifyKind(completedAt.Value, DateTimeKind.Utc);
        return entity;
    }

    [Fact]
    public void DownloadProgressOnlyBatch_DoesNotClobberSucceeded()
    {
        var existing = ExistingEntity(
            "Succeeded",
            completedAt: new DateTime(2026, 4, 27, 16, 9, 21, DateTimeKind.Utc),
            durationSeconds: 105);

        // Simulates the per-batch summary the aggregator builds when only download_progress
        // (e.g. the trailing "completed" tick) lands in a batch — Status falls back to its
        // "InProgress" default and the case never touches it.
        var incoming = new AppInstallSummary
        {
            AppName = "App",
            SessionId = "session",
            TenantId = "tenant",
            Status = "InProgress",
            CompletedAt = null,
            DurationSeconds = 0
        };

        TableStorageService.PreserveTerminalStateIfIncomingIsNotTerminal(incoming, existing);

        Assert.Equal("Succeeded", incoming.Status);
        Assert.Equal(new DateTime(2026, 4, 27, 16, 9, 21, DateTimeKind.Utc), incoming.CompletedAt);
        Assert.Equal(105, incoming.DurationSeconds);
    }

    [Fact]
    public void DoTelemetryOnlyBatch_DoesNotClobberFailed_AndKeepsFailureFields()
    {
        var existing = ExistingEntity(
            "Failed",
            completedAt: new DateTime(2026, 4, 27, 16, 7, 13, DateTimeKind.Utc),
            durationSeconds: 47,
            failureCode: "0x80070005",
            failureMessage: "Access denied");

        var incoming = new AppInstallSummary
        {
            AppName = "App",
            SessionId = "session",
            TenantId = "tenant",
            Status = "InProgress",
            FailureCode = string.Empty,
            FailureMessage = string.Empty
        };

        TableStorageService.PreserveTerminalStateIfIncomingIsNotTerminal(incoming, existing);

        Assert.Equal("Failed", incoming.Status);
        Assert.Equal(47, incoming.DurationSeconds);
        Assert.Equal("0x80070005", incoming.FailureCode);
        Assert.Equal("Access denied", incoming.FailureMessage);
    }

    [Fact]
    public void TerminalSucceeded_OverwritesPriorInProgress_LastWriteWins()
    {
        // existing row was still InProgress (e.g. only app_install_started had landed).
        var existing = ExistingEntity("InProgress");

        var incoming = new AppInstallSummary
        {
            AppName = "App",
            SessionId = "session",
            TenantId = "tenant",
            Status = "Succeeded",
            CompletedAt = new DateTime(2026, 4, 27, 16, 9, 21, DateTimeKind.Utc),
            DurationSeconds = 105
        };

        TableStorageService.PreserveTerminalStateIfIncomingIsNotTerminal(incoming, existing);

        Assert.Equal("Succeeded", incoming.Status);
        Assert.Equal(105, incoming.DurationSeconds);
    }

    [Fact]
    public void RetryAfterFailure_NewSucceededOverwritesFailed()
    {
        // Retry semantics: when a later attempt produces a real Succeeded event, it must
        // win even though the existing row is Failed. The preserve guard must not lock the
        // row into the first terminal state.
        var existing = ExistingEntity(
            "Failed",
            completedAt: new DateTime(2026, 4, 27, 16, 5, 0, DateTimeKind.Utc),
            durationSeconds: 30,
            failureCode: "0x1",
            failureMessage: "boom");

        var incoming = new AppInstallSummary
        {
            AppName = "App",
            SessionId = "session",
            TenantId = "tenant",
            Status = "Succeeded",
            CompletedAt = new DateTime(2026, 4, 27, 16, 8, 0, DateTimeKind.Utc),
            DurationSeconds = 90
        };

        TableStorageService.PreserveTerminalStateIfIncomingIsNotTerminal(incoming, existing);

        Assert.Equal("Succeeded", incoming.Status);
        Assert.Equal(new DateTime(2026, 4, 27, 16, 8, 0, DateTimeKind.Utc), incoming.CompletedAt);
        Assert.Equal(90, incoming.DurationSeconds);
        Assert.Equal(string.Empty, incoming.FailureCode);
        Assert.Equal(string.Empty, incoming.FailureMessage);
    }

    [Fact]
    public void IncomingHasNewerCompletedAt_StillRespectsExistingTerminalIfIncomingIsNotTerminal()
    {
        // Edge case: existing row is Succeeded; a later download_progress batch happens to
        // populate a CompletedAt by some other path. The preserve guard sees Status=InProgress
        // on the incoming and must keep existing's CompletedAt + DurationSeconds (which carry
        // the authoritative timestamps tied to the terminal state).
        var existing = ExistingEntity(
            "Succeeded",
            completedAt: new DateTime(2026, 4, 27, 16, 9, 21, DateTimeKind.Utc),
            durationSeconds: 105);

        var incoming = new AppInstallSummary
        {
            AppName = "App",
            SessionId = "session",
            TenantId = "tenant",
            Status = "InProgress",
            CompletedAt = new DateTime(2026, 4, 27, 16, 9, 25, DateTimeKind.Utc),
            DurationSeconds = 0
        };

        TableStorageService.PreserveTerminalStateIfIncomingIsNotTerminal(incoming, existing);

        Assert.Equal("Succeeded", incoming.Status);
        // Preserve only when incoming had no value; here it had one — so it stays.
        Assert.Equal(new DateTime(2026, 4, 27, 16, 9, 25, DateTimeKind.Utc), incoming.CompletedAt);
        Assert.Equal(105, incoming.DurationSeconds);
    }
}
