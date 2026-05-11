using System;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure mapping roundtrip for <see cref="TableSlaTenantStatusRepository"/>.
/// Asserts every per-type field survives serialization in both directions so the
/// shape locked into Azure Storage stays in sync with the POCO definition.
/// </summary>
public class TableSlaTenantStatusRepositoryMappingTests
{
    [Fact]
    public void Roundtrip_AllFieldsPreserved()
    {
        var first = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        var last = new DateTime(2026, 5, 11, 6, 0, 0, DateTimeKind.Utc);
        var resolved = new DateTime(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);
        var notified = new DateTime(2026, 5, 10, 6, 0, 0, DateTimeKind.Utc);

        var original = new SlaTenantStatus
        {
            TenantId = "tenant-XYZ",
            LastEvaluatedAt = last,

            SuccessRate_IsActive = true,
            SuccessRate_CurrentValue = 88.5,
            SuccessRate_TargetValue = 95.0,
            SuccessRate_ThresholdValue = 90.0,
            SuccessRate_TotalSessions = 17,
            SuccessRate_FailedSessions = 2,
            SuccessRate_FirstBreachAt = first,
            SuccessRate_LastBreachAt = last,
            SuccessRate_LastNotifiedAt = notified,
            SuccessRate_ResolvedAt = null,

            Duration_IsActive = false,
            Duration_CurrentP95Minutes = 72.3,
            Duration_TargetMinutes = 60,
            Duration_TotalSessions = 19,
            Duration_FirstBreachAt = first,
            Duration_LastBreachAt = last,
            Duration_LastNotifiedAt = notified,
            Duration_ResolvedAt = resolved,

            AppInstall_IsActive = true,
            AppInstall_CurrentRate = 91.0,
            AppInstall_TargetRate = 99.0,
            AppInstall_TopFailingApp = "Adobe Reader",
            AppInstall_FirstBreachAt = first,
            AppInstall_LastBreachAt = last,
            AppInstall_LastNotifiedAt = notified,
            AppInstall_ResolvedAt = null,

            ConsecutiveFailures_IsActive = true,
            ConsecutiveFailures_Count = 5,
            ConsecutiveFailures_LastDevice = "TEST-DEVICE-01",
            ConsecutiveFailures_LastReason = "Autopilot timeout",
            ConsecutiveFailures_FirstAt = first,
            ConsecutiveFailures_LastNotifiedAt = notified,
            ConsecutiveFailures_ResolvedAt = null,
        };

        var entity = TableSlaTenantStatusRepository.MapToEntity(original);

        // PartitionKey is lowercased tenantId; RowKey is the constant "status"
        Assert.Equal("tenant-xyz", entity.PartitionKey);
        Assert.Equal(SlaTenantStatus.StatusRowKey, entity.RowKey);

        var roundTripped = TableSlaTenantStatusRepository.MapFromEntity(entity, original.TenantId);

        Assert.Equal(original.TenantId, roundTripped.TenantId);
        Assert.Equal(original.LastEvaluatedAt, roundTripped.LastEvaluatedAt);

        Assert.Equal(original.SuccessRate_IsActive, roundTripped.SuccessRate_IsActive);
        Assert.Equal(original.SuccessRate_CurrentValue, roundTripped.SuccessRate_CurrentValue);
        Assert.Equal(original.SuccessRate_TargetValue, roundTripped.SuccessRate_TargetValue);
        Assert.Equal(original.SuccessRate_ThresholdValue, roundTripped.SuccessRate_ThresholdValue);
        Assert.Equal(original.SuccessRate_TotalSessions, roundTripped.SuccessRate_TotalSessions);
        Assert.Equal(original.SuccessRate_FailedSessions, roundTripped.SuccessRate_FailedSessions);
        Assert.Equal(original.SuccessRate_FirstBreachAt, roundTripped.SuccessRate_FirstBreachAt);
        Assert.Equal(original.SuccessRate_LastBreachAt, roundTripped.SuccessRate_LastBreachAt);
        Assert.Equal(original.SuccessRate_LastNotifiedAt, roundTripped.SuccessRate_LastNotifiedAt);
        Assert.Equal(original.SuccessRate_ResolvedAt, roundTripped.SuccessRate_ResolvedAt);

        Assert.Equal(original.Duration_IsActive, roundTripped.Duration_IsActive);
        Assert.Equal(original.Duration_CurrentP95Minutes, roundTripped.Duration_CurrentP95Minutes);
        Assert.Equal(original.Duration_TargetMinutes, roundTripped.Duration_TargetMinutes);
        Assert.Equal(original.Duration_TotalSessions, roundTripped.Duration_TotalSessions);
        Assert.Equal(original.Duration_ResolvedAt, roundTripped.Duration_ResolvedAt);

        Assert.Equal(original.AppInstall_IsActive, roundTripped.AppInstall_IsActive);
        Assert.Equal(original.AppInstall_CurrentRate, roundTripped.AppInstall_CurrentRate);
        Assert.Equal(original.AppInstall_TargetRate, roundTripped.AppInstall_TargetRate);
        Assert.Equal(original.AppInstall_TopFailingApp, roundTripped.AppInstall_TopFailingApp);

        Assert.Equal(original.ConsecutiveFailures_IsActive, roundTripped.ConsecutiveFailures_IsActive);
        Assert.Equal(original.ConsecutiveFailures_Count, roundTripped.ConsecutiveFailures_Count);
        Assert.Equal(original.ConsecutiveFailures_LastDevice, roundTripped.ConsecutiveFailures_LastDevice);
        Assert.Equal(original.ConsecutiveFailures_LastReason, roundTripped.ConsecutiveFailures_LastReason);
        Assert.Equal(original.ConsecutiveFailures_FirstAt, roundTripped.ConsecutiveFailures_FirstAt);
        Assert.Equal(original.ConsecutiveFailures_LastNotifiedAt, roundTripped.ConsecutiveFailures_LastNotifiedAt);
    }

    [Fact]
    public void EmptyRow_RoundtripsToAllDefaults()
    {
        var original = SlaTenantStatus.CreateEmpty("test-tenant");
        original.LastEvaluatedAt = new DateTime(2026, 5, 11, 0, 0, 0, DateTimeKind.Utc);

        var entity = TableSlaTenantStatusRepository.MapToEntity(original);
        var roundTripped = TableSlaTenantStatusRepository.MapFromEntity(entity, original.TenantId);

        Assert.False(roundTripped.IsAnyTypeActive());
        Assert.False(roundTripped.SuccessRate_IsActive);
        Assert.False(roundTripped.Duration_IsActive);
        Assert.False(roundTripped.AppInstall_IsActive);
        Assert.False(roundTripped.ConsecutiveFailures_IsActive);
        Assert.Null(roundTripped.SuccessRate_FirstBreachAt);
        Assert.Null(roundTripped.Duration_LastNotifiedAt);
        Assert.Equal(original.LastEvaluatedAt, roundTripped.LastEvaluatedAt);
    }
}
