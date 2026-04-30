using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verifies ComputeCutoffRowKeyPrefix produces a SessionsIndex RowKey upper bound that
/// lexicographically separates sessions newer than `now - days` (kept) from older ones (excluded).
/// </summary>
public class MetricsCutoffRowKeyTests
{
    private static string IndexRowKey(DateTime startedAt, string sessionId)
        => $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public void Cutoff_keeps_sessions_inside_window_excludes_outside(int days)
    {
        var cutoffPrefix = TableStorageService.ComputeCutoffRowKeyPrefix(days);

        // Inside the window: 1 hour newer than the cutoff.
        var insideRowKey = IndexRowKey(DateTime.UtcNow.AddDays(-days).AddHours(1), Guid.NewGuid().ToString());
        // Outside the window: 1 hour older than the cutoff.
        var outsideRowKey = IndexRowKey(DateTime.UtcNow.AddDays(-days).AddHours(-1), Guid.NewGuid().ToString());

        // RowKey lt cutoffPrefix → include
        Assert.True(string.CompareOrdinal(insideRowKey, cutoffPrefix) < 0,
            $"days={days}: session inside window should be < cutoff (inside={insideRowKey}, cutoff={cutoffPrefix})");
        Assert.False(string.CompareOrdinal(outsideRowKey, cutoffPrefix) < 0,
            $"days={days}: session outside window should NOT be < cutoff (outside={outsideRowKey}, cutoff={cutoffPrefix})");
    }

    [Fact]
    public void Cutoff_prefix_is_19_digits_padded()
    {
        var prefix = TableStorageService.ComputeCutoffRowKeyPrefix(30);
        Assert.Equal(19, prefix.Length);
        Assert.True(prefix.All(char.IsDigit), $"Prefix must be all digits, got: {prefix}");
    }

    [Fact]
    public void Larger_window_produces_lexicographically_larger_prefix()
    {
        // Older cutoff → larger inverted ticks → larger prefix string.
        var prefix7 = TableStorageService.ComputeCutoffRowKeyPrefix(7);
        var prefix30 = TableStorageService.ComputeCutoffRowKeyPrefix(30);
        var prefix90 = TableStorageService.ComputeCutoffRowKeyPrefix(90);

        Assert.True(string.CompareOrdinal(prefix7, prefix30) < 0);
        Assert.True(string.CompareOrdinal(prefix30, prefix90) < 0);
    }
}
