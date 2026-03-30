using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for timestamp validation and sanitization in the ingest pipeline.
///
/// REGRESSION GUARD: Invalid agent-side timestamps (DateTime.MinValue, far-future dates,
/// clock-skewed values) previously flowed through to RowKey generation, duration calculations,
/// and Azure Table Storage writes without any validation — causing production issues.
///
/// EventTimestampValidator clamps out-of-range timestamps to a safe range while preserving
/// the original value in OriginalTimestamp for troubleshooting.
/// </summary>
public class EventTimestampValidationTests
{
    // Fixed reference time for deterministic tests
    private static readonly DateTime FixedUtcNow = new(2026, 3, 30, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string ValidTenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private static readonly string ValidSessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // =========================================================================
    // SanitizeTimestamp
    // =========================================================================

    [Fact]
    public void SanitizeTimestamp_ValidRecentUtcTimestamp_ReturnsSameValue()
    {
        var valid = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);

        var result = EventTimestampValidator.SanitizeTimestamp(valid, FixedUtcNow);

        Assert.Equal(valid, result);
    }

    [Fact]
    public void SanitizeTimestamp_DateTimeMinValue_ClampsToMinReasonable()
    {
        var result = EventTimestampValidator.SanitizeTimestamp(DateTime.MinValue, FixedUtcNow);

        Assert.Equal(EventTimestampValidator.MinReasonableTimestamp, result);
    }

    [Fact]
    public void SanitizeTimestamp_DateTimeMaxValue_ClampsToUtcNow()
    {
        var result = EventTimestampValidator.SanitizeTimestamp(DateTime.MaxValue, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_FarPast1999_ClampsToMinReasonable()
    {
        var farPast = new DateTime(1999, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = EventTimestampValidator.SanitizeTimestamp(farPast, FixedUtcNow);

        Assert.Equal(EventTimestampValidator.MinReasonableTimestamp, result);
    }

    [Fact]
    public void SanitizeTimestamp_FarFuture2099_ClampsToUtcNow()
    {
        var farFuture = new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var result = EventTimestampValidator.SanitizeTimestamp(farFuture, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_SlightFutureWithin24h_PassesThrough()
    {
        // Agent clock is 2 hours ahead — within tolerance
        var slightFuture = FixedUtcNow.AddHours(2);

        var result = EventTimestampValidator.SanitizeTimestamp(slightFuture, FixedUtcNow);

        Assert.Equal(slightFuture, result);
    }

    [Fact]
    public void SanitizeTimestamp_ExactlyAt24hBoundary_PassesThrough()
    {
        var boundary = FixedUtcNow.AddHours(24);

        var result = EventTimestampValidator.SanitizeTimestamp(boundary, FixedUtcNow);

        Assert.Equal(boundary, result);
    }

    [Fact]
    public void SanitizeTimestamp_25HoursInFuture_ClampsToUtcNow()
    {
        var tooFar = FixedUtcNow.AddHours(25);

        var result = EventTimestampValidator.SanitizeTimestamp(tooFar, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_LocalKind_ConvertsToUtcThenValidates()
    {
        // A recent Local kind timestamp — should be converted to UTC, then pass validation
        var local = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Local);
        var expectedUtc = local.ToUniversalTime();

        var result = EventTimestampValidator.SanitizeTimestamp(local, FixedUtcNow);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(expectedUtc, result);
    }

    [Fact]
    public void SanitizeTimestamp_UnspecifiedKind_TreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Unspecified);

        var result = EventTimestampValidator.SanitizeTimestamp(unspecified, FixedUtcNow);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(2026, result.Year);
        Assert.Equal(3, result.Month);
        Assert.Equal(15, result.Day);
    }

    [Fact]
    public void SanitizeTimestamp_ExactMinBound_PassesThrough()
    {
        var result = EventTimestampValidator.SanitizeTimestamp(
            EventTimestampValidator.MinReasonableTimestamp, FixedUtcNow);

        Assert.Equal(EventTimestampValidator.MinReasonableTimestamp, result);
    }

    // =========================================================================
    // IsReasonableTimestamp
    // =========================================================================

    [Fact]
    public void IsReasonableTimestamp_ValidDate_ReturnsTrue()
    {
        var valid = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(EventTimestampValidator.IsReasonableTimestamp(valid, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_MinValue_ReturnsFalse()
    {
        Assert.False(EventTimestampValidator.IsReasonableTimestamp(DateTime.MinValue, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_MaxValue_ReturnsFalse()
    {
        Assert.False(EventTimestampValidator.IsReasonableTimestamp(DateTime.MaxValue, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_FarFuture_ReturnsFalse()
    {
        var farFuture = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(EventTimestampValidator.IsReasonableTimestamp(farFuture, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_JustBeforeMin_ReturnsFalse()
    {
        var justBefore = new DateTime(2019, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        Assert.False(EventTimestampValidator.IsReasonableTimestamp(justBefore, FixedUtcNow));
    }

    // =========================================================================
    // SafeDurationSeconds
    // =========================================================================

    [Fact]
    public void SafeDurationSeconds_NormalRange_ReturnsCorrectDuration()
    {
        var start = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddSeconds(300);

        Assert.Equal(300, EventTimestampValidator.SafeDurationSeconds(start, end));
    }

    [Fact]
    public void SafeDurationSeconds_EndBeforeStart_ReturnsZero()
    {
        var start = new DateTime(2026, 3, 30, 10, 5, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, EventTimestampValidator.SafeDurationSeconds(start, end));
    }

    [Fact]
    public void SafeDurationSeconds_MinValueToMaxValue_ClampsToMax()
    {
        // This would overflow int if not clamped: ~315 billion seconds
        var result = EventTimestampValidator.SafeDurationSeconds(DateTime.MinValue, DateTime.MaxValue);

        Assert.Equal(EventTimestampValidator.DefaultMaxDurationSeconds, result);
    }

    [Fact]
    public void SafeDurationSeconds_SameTimestamp_ReturnsZero()
    {
        var ts = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, EventTimestampValidator.SafeDurationSeconds(ts, ts));
    }

    [Fact]
    public void SafeDurationSeconds_ExceedsMaxDuration_ClampsToMax()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc); // 31 days apart

        var result = EventTimestampValidator.SafeDurationSeconds(start, end);

        Assert.Equal(EventTimestampValidator.DefaultMaxDurationSeconds, result); // 7 days max
    }

    [Fact]
    public void SafeDurationSeconds_CustomMaxDuration_Respected()
    {
        var start = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddSeconds(500);

        var result = EventTimestampValidator.SafeDurationSeconds(start, end, maxDurationSeconds: 100);

        Assert.Equal(100, result);
    }

    // =========================================================================
    // SafeRowKeyTimestamp
    // =========================================================================

    [Fact]
    public void SafeRowKeyTimestamp_ValidDate_FormatsCorrectly()
    {
        var ts = new DateTime(2026, 3, 30, 14, 25, 30, 123, DateTimeKind.Utc);

        var result = EventTimestampValidator.SafeRowKeyTimestamp(ts, FixedUtcNow);

        Assert.Equal("20260330142530123", result);
    }

    [Fact]
    public void SafeRowKeyTimestamp_MinValue_ProducesMinBoundKey()
    {
        // DateTime.MinValue must be clamped — never produce "00010101000000000"
        var result = EventTimestampValidator.SafeRowKeyTimestamp(DateTime.MinValue, FixedUtcNow);

        Assert.Equal("20200101000000000", result);
        Assert.DoesNotContain("0001", result);
    }

    [Fact]
    public void SafeRowKeyTimestamp_MaxValue_ProducesReasonableKey()
    {
        // DateTime.MaxValue must be clamped — never produce "99991231235959999"
        var result = EventTimestampValidator.SafeRowKeyTimestamp(DateTime.MaxValue, FixedUtcNow);

        Assert.DoesNotContain("9999", result);
        Assert.StartsWith("2026", result); // Clamped to FixedUtcNow year
    }

    // =========================================================================
    // SanitizeEventTimestamps (pipeline integration)
    // =========================================================================

    [Fact]
    public void SanitizeEventTimestamps_ValidTimestamp_NoFlagsSet()
    {
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc) }
        };

        IngestEventsFunction.SanitizeEventTimestamps(events, FixedUtcNow);

        Assert.Null(events[0].OriginalTimestamp);
        Assert.False(events[0].TimestampClamped);
    }

    [Fact]
    public void SanitizeEventTimestamps_InvalidTimestamp_PreservesOriginalAndSetsFlag()
    {
        var badTimestamp = DateTime.MinValue;
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = badTimestamp }
        };

        IngestEventsFunction.SanitizeEventTimestamps(events, FixedUtcNow);

        Assert.True(events[0].TimestampClamped);
        Assert.Equal(badTimestamp, events[0].OriginalTimestamp);
        Assert.Equal(EventTimestampValidator.MinReasonableTimestamp, events[0].Timestamp);
    }

    [Fact]
    public void SanitizeEventTimestamps_MixedValidAndInvalid_OnlyInvalidGetFlag()
    {
        var validTs = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = validTs, EventType = "valid_event" },
            new() { Timestamp = DateTime.MinValue, EventType = "bad_past_event" },
            new() { Timestamp = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), EventType = "bad_future_event" },
        };

        IngestEventsFunction.SanitizeEventTimestamps(events, FixedUtcNow);

        // Valid event: unchanged
        Assert.False(events[0].TimestampClamped);
        Assert.Null(events[0].OriginalTimestamp);
        Assert.Equal(validTs, events[0].Timestamp);

        // Bad past: clamped to min, original preserved
        Assert.True(events[1].TimestampClamped);
        Assert.Equal(DateTime.MinValue, events[1].OriginalTimestamp);
        Assert.Equal(EventTimestampValidator.MinReasonableTimestamp, events[1].Timestamp);

        // Bad future: clamped to utcNow, original preserved
        Assert.True(events[2].TimestampClamped);
        Assert.Equal(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), events[2].OriginalTimestamp);
        Assert.Equal(FixedUtcNow, events[2].Timestamp);
    }

    [Fact]
    public void SanitizeEventTimestamps_NoEventsDropped()
    {
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = DateTime.MinValue },
            new() { Timestamp = DateTime.MaxValue },
            new() { Timestamp = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc) },
        };

        IngestEventsFunction.SanitizeEventTimestamps(events, FixedUtcNow);

        Assert.Equal(3, events.Count); // All events preserved
    }

    [Fact]
    public void SanitizeEventTimestamps_EmptyList_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            IngestEventsFunction.SanitizeEventTimestamps(new List<EnrollmentEvent>(), FixedUtcNow));
        Assert.Null(ex);
    }

    // =========================================================================
    // RecalculateAppDurations (exposed as internal static for testing)
    // =========================================================================

    [Fact]
    public void RecalculateAppDurations_NormalInstall_CorrectDuration()
    {
        var start = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddSeconds(60);

        var state = new AppInstallAggregationState
        {
            Summary = new AppInstallSummary
            {
                AppName = "TestApp",
                StartedAt = start,
                CompletedAt = end,
                Status = "Succeeded"
            }
        };

        IngestEventsFunction.RecalculateAppDurations(state);

        Assert.Equal(60, state.Summary.DurationSeconds);
    }

    [Fact]
    public void RecalculateAppDurations_DownloadThenInstall_CorrectDownloadDuration()
    {
        var downloadStart = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        var installStart = downloadStart.AddSeconds(30);
        var completed = installStart.AddSeconds(45);

        var state = new AppInstallAggregationState
        {
            DownloadStartedAt = downloadStart,
            InstallStartedAt = installStart,
            Summary = new AppInstallSummary
            {
                AppName = "TestApp",
                StartedAt = downloadStart,
                CompletedAt = completed,
                Status = "Succeeded"
            }
        };

        IngestEventsFunction.RecalculateAppDurations(state);

        Assert.Equal(30, state.Summary.DownloadDurationSeconds); // download→install gap
        Assert.Equal(75, state.Summary.DurationSeconds);         // full duration
    }

    [Fact]
    public void RecalculateAppDurations_ExtremeTimestampGap_DurationClamped()
    {
        // Even if timestamps somehow bypass sanitization, SafeDurationSeconds prevents overflow
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc); // ~7 years apart

        var state = new AppInstallAggregationState
        {
            Summary = new AppInstallSummary
            {
                AppName = "TestApp",
                StartedAt = start,
                CompletedAt = end,
                Status = "Succeeded"
            }
        };

        IngestEventsFunction.RecalculateAppDurations(state);

        // Should be clamped to max 7 days (604800 seconds), not ~220 million seconds
        Assert.Equal(EventTimestampValidator.DefaultMaxDurationSeconds, state.Summary.DurationSeconds);
    }

    // =========================================================================
    // End-to-End Pipeline: NDJSON parse → stamp → sanitize → safe for storage
    // =========================================================================

    [Fact]
    public void FullPipeline_ParseStampSanitize_TimestampsAreSafe()
    {
        // Simulate NDJSON payload with events containing bad timestamps
        var ndjsonLines = new[]
        {
            Newtonsoft.Json.JsonConvert.SerializeObject(
                new { SessionId = ValidSessionId, TenantId = ValidTenantId }),
            Newtonsoft.Json.JsonConvert.SerializeObject(
                new EnrollmentEvent
                {
                    EventType = "phase_changed",
                    Timestamp = new DateTime(2026, 3, 15, 10, 0, 0, DateTimeKind.Utc) // valid
                }),
            Newtonsoft.Json.JsonConvert.SerializeObject(
                new EnrollmentEvent
                {
                    EventType = "bad_clock_event",
                    Timestamp = DateTime.MinValue // invalid
                }),
        };
        var ndjson = string.Join('\n', ndjsonLines);

        // Step 1: Parse
        var (sessionId, tenantId, events) = NdjsonParser.ParseNdjson(ndjson);

        // Step 2: Stamp server fields
        IngestEventsFunction.StampServerFields(events, tenantId, sessionId, FixedUtcNow);

        // Step 3: Sanitize timestamps
        IngestEventsFunction.SanitizeEventTimestamps(events, FixedUtcNow);

        // Verify: all timestamps are within valid range
        foreach (var evt in events)
        {
            Assert.True(EventTimestampValidator.IsReasonableTimestamp(evt.Timestamp, FixedUtcNow),
                $"Event {evt.EventType} has unreasonable timestamp: {evt.Timestamp}");
        }

        // Verify: no events were dropped
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void FullPipeline_MinValueTimestamp_ClampedWithOriginalPreserved()
    {
        var events = new List<EnrollmentEvent>
        {
            new()
            {
                EventType = "bad_event",
                Timestamp = DateTime.MinValue,
                TenantId = ValidTenantId,
                SessionId = ValidSessionId
            }
        };

        IngestEventsFunction.StampServerFields(events, ValidTenantId, ValidSessionId, FixedUtcNow);
        IngestEventsFunction.SanitizeEventTimestamps(events, FixedUtcNow);

        var evt = events[0];

        // Clamped timestamp produces valid RowKey format
        var rowKey = $"{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence:D10}";
        Assert.StartsWith("2020", rowKey); // Clamped to MinReasonableTimestamp
        Assert.DoesNotContain("0001", rowKey);

        // Original preserved for troubleshooting
        Assert.True(evt.TimestampClamped);
        Assert.Equal(DateTime.MinValue, evt.OriginalTimestamp);
    }

    [Fact]
    public void FullPipeline_MixedTimestamps_AllEventsPreservedFlagsCorrect()
    {
        var validTs = new DateTime(2026, 3, 25, 8, 0, 0, DateTimeKind.Utc);
        var events = new List<EnrollmentEvent>
        {
            new() { EventType = "valid", Timestamp = validTs },
            new() { EventType = "past", Timestamp = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { EventType = "future", Timestamp = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { EventType = "also_valid", Timestamp = FixedUtcNow.AddHours(1) },
        };

        IngestEventsFunction.StampServerFields(events, ValidTenantId, ValidSessionId, FixedUtcNow);
        IngestEventsFunction.SanitizeEventTimestamps(events, FixedUtcNow);

        // All 4 events preserved
        Assert.Equal(4, events.Count);

        // Valid: no flags
        Assert.False(events[0].TimestampClamped);
        Assert.False(events[3].TimestampClamped);

        // Invalid: flags set, originals preserved
        Assert.True(events[1].TimestampClamped);
        Assert.True(events[2].TimestampClamped);
        Assert.NotNull(events[1].OriginalTimestamp);
        Assert.NotNull(events[2].OriginalTimestamp);
    }
}
