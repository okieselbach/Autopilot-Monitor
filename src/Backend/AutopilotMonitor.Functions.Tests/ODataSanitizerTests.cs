using AutopilotMonitor.Functions.Security;

namespace AutopilotMonitor.Functions.Tests;

public class ODataSanitizerTests
{
    [Fact]
    public void EscapeValue_WithSingleQuote_DoublesIt()
    {
        Assert.Equal("O''Brien", ODataSanitizer.EscapeValue("O'Brien"));
    }

    [Fact]
    public void EscapeValue_WithInjectionPayload_EscapesQuotes()
    {
        var escaped = ODataSanitizer.EscapeValue("' or '1'='1");
        Assert.Equal("'' or ''1''=''1", escaped);
        // When placed in filter: Token eq ''' or ''1''=''1' — no breakout
    }

    [Fact]
    public void EscapeValue_WithGuid_ReturnsUnchanged()
    {
        var guid = Guid.NewGuid().ToString();
        Assert.Equal(guid, ODataSanitizer.EscapeValue(guid));
    }

    [Fact]
    public void EscapeValue_WithEmptyString_ReturnsEmpty()
    {
        Assert.Equal("", ODataSanitizer.EscapeValue(""));
    }

    [Fact]
    public void EscapeValue_WithMultipleQuotes_EscapesAll()
    {
        Assert.Equal("a''b''c''d", ODataSanitizer.EscapeValue("a'b'c'd"));
    }
}
