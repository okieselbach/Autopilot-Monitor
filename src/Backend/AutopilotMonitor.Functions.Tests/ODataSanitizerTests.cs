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

    [Fact]
    public void EscapeValue_CompositeKey_InjectionNeutralized()
    {
        // Simulates: PartitionKey eq '{tenantId}_{eventType}'
        // Attack: eventType = "app_install' or '1'='1"
        var safeEvent = ODataSanitizer.EscapeValue("app_install' or '1'='1");
        var filter = $"PartitionKey eq 'tenant123_{safeEvent}'";
        Assert.Equal("PartitionKey eq 'tenant123_app_install'' or ''1''=''1'", filter);
        // Single quotes are doubled — no breakout from the literal
    }

    [Fact]
    public void EscapeValue_RangeFilter_InjectionNeutralized()
    {
        // Simulates: PartitionKey ge '{cveId}' and PartitionKey lt '{cveId}~'
        // Attack: cveId = "CVE-2024' or PartitionKey ne '"
        var safeCve = ODataSanitizer.EscapeValue("CVE-2024' or PartitionKey ne '");
        var filter = $"PartitionKey ge '{safeCve}' and PartitionKey lt '{safeCve}~'";
        Assert.Contains("CVE-2024'' or PartitionKey ne ''", filter);
        // Injection payload stays inside the quoted literal
    }

    [Fact]
    public void EscapeValue_TenantBreakout_InjectionNeutralized()
    {
        // Attack: manufacturer = "Dell' or PartitionKey ne '"
        // Would break tenant scoping without escaping
        var safeMfg = ODataSanitizer.EscapeValue("Dell' or PartitionKey ne '");
        var filter = $"PartitionKey eq 'tenant123' and Manufacturer eq '{safeMfg}'";
        Assert.Equal("PartitionKey eq 'tenant123' and Manufacturer eq 'Dell'' or PartitionKey ne '''", filter);
    }
}
