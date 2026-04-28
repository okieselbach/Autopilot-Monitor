using AutopilotMonitor.Functions.DataAccess.TableStorage;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="TableHardwareRejectionNotificationTracker.BuildRowKey"/>.
///
/// CORRECTNESS GUARD: The RowKey is the dedup identity for "has this tenant already been
/// notified about this model?". A change in casing or whitespace must NOT produce a second
/// bell notification. Without case-insensitive trim, "Lenovo X1" and "lenovo x1" would
/// produce two distinct rows and two notifications — exactly what the user explicitly
/// rejected ("einmal pro model").
/// </summary>
public class HardwareRejectionNotificationTrackerKeyTests
{
    [Fact]
    public void BuildRowKey_LowercasesAndJoinsWithPipe()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildRowKey("Lenovo", "ThinkPad X1");
        Assert.Equal("lenovo|thinkpad x1", key);
    }

    [Fact]
    public void BuildRowKey_TrimsLeadingAndTrailingWhitespace()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildRowKey("  Dell  ", "  Latitude 5520 ");
        Assert.Equal("dell|latitude 5520", key);
    }

    [Theory]
    [InlineData("Lenovo", "ThinkPad X1", "lenovo", "thinkpad x1")]
    [InlineData("LENOVO", "THINKPAD X1", "lenovo", "thinkpad x1")]
    [InlineData("LeNoVo", "ThInKpAd X1", "lenovo", "thinkpad x1")]
    public void BuildRowKey_IsCaseInsensitive(string mfrA, string mdlA, string mfrB, string mdlB)
    {
        var keyA = TableHardwareRejectionNotificationTracker.BuildRowKey(mfrA, mdlA);
        var keyB = TableHardwareRejectionNotificationTracker.BuildRowKey(mfrB, mdlB);
        Assert.Equal(keyA, keyB);
    }

    [Fact]
    public void BuildRowKey_DistinctModels_ProduceDistinctKeys()
    {
        var keyX1 = TableHardwareRejectionNotificationTracker.BuildRowKey("Lenovo", "ThinkPad X1");
        var keyX13 = TableHardwareRejectionNotificationTracker.BuildRowKey("Lenovo", "ThinkPad X13");
        Assert.NotEqual(keyX1, keyX13);
    }

    [Fact]
    public void BuildRowKey_NullInputs_ReturnPipeDelimiterOnly()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildRowKey(null!, null!);
        Assert.Equal("|", key);
    }

    [Fact]
    public void BuildRowKey_EmptyInputs_ReturnPipeDelimiterOnly()
    {
        var key = TableHardwareRejectionNotificationTracker.BuildRowKey("", "");
        Assert.Equal("|", key);
    }
}
