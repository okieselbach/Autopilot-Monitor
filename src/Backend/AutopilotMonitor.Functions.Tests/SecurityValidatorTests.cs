using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for SecurityValidator GUID validation.
/// StoreEventsBatchAsync relies on EnsureValidGuid to reject invalid TenantId/SessionId values —
/// these tests guard that contract.
/// </summary>
public class SecurityValidatorTests
{
    // --- IsValidGuid ---

    [Fact]
    public void IsValidGuid_WithValidLowercaseGuid_ReturnsTrue()
    {
        Assert.True(SecurityValidator.IsValidGuid("a1b2c3d4-e5f6-7890-abcd-ef1234567890"));
    }

    [Fact]
    public void IsValidGuid_WithValidUppercaseGuid_ReturnsTrue()
    {
        Assert.True(SecurityValidator.IsValidGuid("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"));
    }

    [Fact]
    public void IsValidGuid_WithValidMixedCaseGuid_ReturnsTrue()
    {
        Assert.True(SecurityValidator.IsValidGuid("a1B2c3D4-e5F6-7890-AbCd-Ef1234567890"));
    }

    [Fact]
    public void IsValidGuid_WithNewGuid_ReturnsTrue()
    {
        Assert.True(SecurityValidator.IsValidGuid(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void IsValidGuid_WithNull_ReturnsFalse()
    {
        Assert.False(SecurityValidator.IsValidGuid(null));
    }

    [Fact]
    public void IsValidGuid_WithEmptyString_ReturnsFalse()
    {
        Assert.False(SecurityValidator.IsValidGuid(""));
    }

    [Fact]
    public void IsValidGuid_WithWhitespace_ReturnsFalse()
    {
        Assert.False(SecurityValidator.IsValidGuid("   "));
    }

    [Fact]
    public void IsValidGuid_WithPlainString_ReturnsFalse()
    {
        Assert.False(SecurityValidator.IsValidGuid("not-a-guid"));
    }

    [Fact]
    public void IsValidGuid_WithDeviceName_ReturnsFalse()
    {
        // Regression: agent running as defaultuser0 could send "DESKTOP-DIU8038\defaultuser0" as TenantId
        Assert.False(SecurityValidator.IsValidGuid(@"DESKTOP-DIU8038\defaultuser0"));
    }

    [Fact]
    public void IsValidGuid_WithGuidWithoutDashes_ReturnsFalse()
    {
        // No dashes → not in standard format
        Assert.False(SecurityValidator.IsValidGuid("a1b2c3d4e5f67890abcdef1234567890"));
    }

    [Fact]
    public void IsValidGuid_WithGuidInBraces_ReturnsFalse()
    {
        // Braced format {…} is not accepted — we require plain hyphenated form
        Assert.False(SecurityValidator.IsValidGuid("{a1b2c3d4-e5f6-7890-abcd-ef1234567890}"));
    }

    // --- EnsureValidGuid ---

    [Fact]
    public void EnsureValidGuid_WithValidGuid_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            SecurityValidator.EnsureValidGuid(Guid.NewGuid().ToString(), "TenantId"));
        Assert.Null(ex);
    }

    [Fact]
    public void EnsureValidGuid_WithNull_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid(null, "TenantId"));
        Assert.Contains("TenantId", ex.Message);
    }

    [Fact]
    public void EnsureValidGuid_WithEmptyString_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid("", "SessionId"));
        Assert.Contains("SessionId", ex.Message);
    }

    [Fact]
    public void EnsureValidGuid_WithInvalidString_ThrowsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid("invalid-tenant", "TenantId"));
        Assert.Equal("TenantId", ex.ParamName);
    }

    [Fact]
    public void EnsureValidGuid_ErrorMessage_MentionsParameterName()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid("bad", "MyParam"));
        Assert.Contains("MyParam", ex.Message);
    }
}
