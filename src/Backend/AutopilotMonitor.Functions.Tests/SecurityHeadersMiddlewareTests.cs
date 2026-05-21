using AutopilotMonitor.Functions.Middleware;
using Microsoft.AspNetCore.Http;

namespace AutopilotMonitor.Functions.Tests;

public class SecurityHeadersMiddlewareTests
{
    [Fact]
    public void ApplyHeaders_StampsAllThreeBaselineHeaders()
    {
        var headers = new HeaderDictionary();

        SecurityHeadersMiddleware.ApplyHeaders(headers);

        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("strict-origin-when-cross-origin", headers["Referrer-Policy"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
    }

    [Fact]
    public void ApplyHeaders_DoesNotOverwriteExistingValues()
    {
        // An endpoint may legitimately need to override e.g. X-Frame-Options to
        // SAMEORIGIN for an embedded admin view. The middleware MUST defer.
        var headers = new HeaderDictionary
        {
            ["X-Content-Type-Options"] = "custom",
            ["Referrer-Policy"] = "no-referrer",
            ["X-Frame-Options"] = "SAMEORIGIN",
        };

        SecurityHeadersMiddleware.ApplyHeaders(headers);

        Assert.Equal("custom", headers["X-Content-Type-Options"]);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.Equal("SAMEORIGIN", headers["X-Frame-Options"]);
    }

    [Fact]
    public void ApplyHeaders_PartialExisting_FillsOnlyMissing()
    {
        var headers = new HeaderDictionary
        {
            ["Referrer-Policy"] = "no-referrer",
        };

        SecurityHeadersMiddleware.ApplyHeaders(headers);

        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);   // preserved
        Assert.Equal("DENY", headers["X-Frame-Options"]);
    }

    [Fact]
    public void ApplyHeaders_NullDictionary_DoesNotThrow()
    {
        var ex = Record.Exception(() => SecurityHeadersMiddleware.ApplyHeaders(null!));
        Assert.Null(ex);
    }
}
