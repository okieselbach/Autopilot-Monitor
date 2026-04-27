using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

// Regression cover for session 59a0f7f3-... (V2 agent 2.0.396, 2026-04-27): all 11 apps
// landed with Status="InProgress" + DurationSeconds=0 even though app_install_completed
// fired and CompletedAt was set. Root cause: AppInstallSummary.Status defaulted to
// "InProgress", and a fresh per-batch summary thus carried that default into the upsert
// even when the current batch had only progress / telemetry events. Fix: empty Status is
// the new sentinel ("no observation in this batch"), the entity builder omits sentinel-
// gated columns, and Merge-mode preserves any prior real value across batches.
public class AppInstallSummaryTerminalPreserveTests
{
    private const string RowKey = "session_App";

    private static AppInstallSummary BaseSummary(
        string status = "",
        DateTime? completedAt = null,
        int durationSeconds = 0,
        string failureCode = "",
        string failureMessage = "")
    {
        return new AppInstallSummary
        {
            AppName = "App",
            SessionId = "session",
            TenantId = "tenant",
            StartedAt = new DateTime(2026, 4, 27, 16, 7, 36, DateTimeKind.Utc),
            Status = status,
            CompletedAt = completedAt,
            DurationSeconds = durationSeconds,
            FailureCode = failureCode,
            FailureMessage = failureMessage
        };
    }

    [Fact]
    public void EmptyStatus_OmitsLifecycleColumnsFromEntity()
    {
        // The exact bug case: progress-only batch builds a fresh summary that carries
        // the empty-string sentinel. Entity must NOT include Status / CompletedAt /
        // DurationSeconds / FailureCode / FailureMessage so Merge-mode preserves any
        // prior terminal write.
        var summary = BaseSummary();

        var entity = TableStorageService.BuildAppInstallSummaryEntity(summary, RowKey);

        Assert.False(entity.ContainsKey("Status"));
        Assert.False(entity.ContainsKey("CompletedAt"));
        Assert.False(entity.ContainsKey("DurationSeconds"));
        Assert.False(entity.ContainsKey("FailureCode"));
        Assert.False(entity.ContainsKey("FailureMessage"));

        // Always-present scaffolding still goes through.
        Assert.Equal("App", entity.GetString("AppName"));
        Assert.Equal("session", entity.GetString("SessionId"));
        Assert.Equal("tenant", entity.GetString("TenantId"));
    }

    [Fact]
    public void SucceededStatus_IncludesLifecycleColumnsInEntity()
    {
        var completedAt = new DateTime(2026, 4, 27, 16, 9, 21, DateTimeKind.Utc);
        var summary = BaseSummary(status: "Succeeded", completedAt: completedAt, durationSeconds: 105);

        var entity = TableStorageService.BuildAppInstallSummaryEntity(summary, RowKey);

        Assert.Equal("Succeeded", entity.GetString("Status"));
        Assert.Equal(completedAt, entity.GetDateTimeOffset("CompletedAt")?.UtcDateTime);
        Assert.Equal(105, entity.GetInt32("DurationSeconds"));
    }

    [Fact]
    public void FailedStatus_IncludesFailureColumns()
    {
        var summary = BaseSummary(
            status: "Failed",
            completedAt: new DateTime(2026, 4, 27, 16, 7, 13, DateTimeKind.Utc),
            durationSeconds: 47,
            failureCode: "0x80070005",
            failureMessage: "Access denied");

        var entity = TableStorageService.BuildAppInstallSummaryEntity(summary, RowKey);

        Assert.Equal("Failed", entity.GetString("Status"));
        Assert.Equal("0x80070005", entity.GetString("FailureCode"));
        Assert.Equal("Access denied", entity.GetString("FailureMessage"));
        Assert.Equal(47, entity.GetInt32("DurationSeconds"));
    }

    [Fact]
    public void InProgressStatus_StillWrittenSoFreshRowsHaveAValue()
    {
        // app_install_started / app_download_started set Status="InProgress" explicitly.
        // The entity must include this so a brand-new row is created with a real Status
        // (not just the read-side fallback).
        var summary = BaseSummary(status: "InProgress");

        var entity = TableStorageService.BuildAppInstallSummaryEntity(summary, RowKey);

        Assert.Equal("InProgress", entity.GetString("Status"));
        Assert.False(entity.ContainsKey("CompletedAt"));
        Assert.False(entity.ContainsKey("DurationSeconds"));
    }

    [Fact]
    public void PartialLifecycleData_OnlyIncludesObservedColumns()
    {
        // Batch saw a terminal event but DurationSeconds couldn't be computed (e.g. fresh
        // per-batch summary where summary.StartedAt was DateTime.MinValue and the case
        // therefore left DurationSeconds=0). CompletedAt is set, DurationSeconds is not.
        var completedAt = new DateTime(2026, 4, 27, 16, 9, 21, DateTimeKind.Utc);
        var summary = BaseSummary(status: "Succeeded", completedAt: completedAt, durationSeconds: 0);

        var entity = TableStorageService.BuildAppInstallSummaryEntity(summary, RowKey);

        Assert.Equal("Succeeded", entity.GetString("Status"));
        Assert.Equal(completedAt, entity.GetDateTimeOffset("CompletedAt")?.UtcDateTime);
        // DurationSeconds <= 0 → omitted. Merge preserves any prior value, or absent on a
        // fresh row (reader treats absent column as 0).
        Assert.False(entity.ContainsKey("DurationSeconds"));
    }

    [Fact]
    public void EmptyFailureFields_OmittedEvenWhenStatusIsFailed()
    {
        // Defensive: Status=Failed but the agent didn't provide errorCode / errorMessage.
        // Empty strings stay sentinels so we don't overwrite a prior batch's real failure
        // text with blanks.
        var summary = BaseSummary(
            status: "Failed",
            completedAt: new DateTime(2026, 4, 27, 16, 7, 13, DateTimeKind.Utc),
            durationSeconds: 47);

        var entity = TableStorageService.BuildAppInstallSummaryEntity(summary, RowKey);

        Assert.Equal("Failed", entity.GetString("Status"));
        Assert.False(entity.ContainsKey("FailureCode"));
        Assert.False(entity.ContainsKey("FailureMessage"));
    }
}
