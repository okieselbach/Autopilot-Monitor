using AutopilotMonitor.Functions.Functions.Diagnostics;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Plan §5a: the download function classifies blob names by shape into
/// <c>CustomerSas</c> (no slash) or <c>Hosted</c> ({tenantId}/{filename}). Hostile
/// inputs (path traversal, double encoding, cross-tenant prefix, nested paths)
/// must all be rejected — every one of these is a vector for either leaking the
/// wrong tenant's blob or reading outside the diagnostics container entirely.
/// </summary>
public class DiagnosticsDownloadFunctionClassifyTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    // ── CustomerSas (no slash) ─────────────────────────────────────────────────────

    [Fact]
    public void Classify_PlainName_NoSlash_IsCustomerSas()
    {
        var (dest, reason) = InvokeClassify("AgentDiagnostics-abc-20260519.zip", TenantA);
        Assert.Equal(DiagnosticsDownloadFunction.BlobDestination.CustomerSas, dest);
        Assert.Null(reason);
    }

    // ── Hosted (tenantId/filename) ─────────────────────────────────────────────────

    [Fact]
    public void Classify_TenantPrefixMatches_IsHosted()
    {
        var (dest, reason) = InvokeClassify($"{TenantA}/AgentDiagnostics-abc.zip", TenantA);
        Assert.Equal(DiagnosticsDownloadFunction.BlobDestination.Hosted, dest);
        Assert.Null(reason);
    }

    [Fact]
    public void Classify_TenantPrefixUrlEncoded_StillHosted()
    {
        // URL-encoded slash (%2F) is a single round-trip decode away from a real /;
        // legitimate UI clients may URL-encode the prefix slash. One decode is OK;
        // double encoding is the smuggling attempt and gets rejected separately.
        var (dest, reason) = InvokeClassify($"{TenantA}%2FAgentDiagnostics-abc.zip", TenantA);
        Assert.Equal(DiagnosticsDownloadFunction.BlobDestination.Hosted, dest);
        Assert.Null(reason);
    }

    // ── Cross-tenant rejection ─────────────────────────────────────────────────────

    [Fact]
    public void Classify_TenantPrefixMismatch_Rejected()
    {
        // Tenant A's user tries to read tenant B's hosted blob. The TenantScoping
        // middleware would have already validated the ?tenantId= query param, but
        // we defence-in-depth check the path prefix here so a forged blobName
        // also fails closed.
        var (dest, reason) = InvokeClassify($"{TenantB}/x.zip", TenantA);
        Assert.Equal(DiagnosticsDownloadFunction.BlobDestination.Hosted, dest);
        Assert.NotNull(reason);
    }

    // ── Hostile inputs ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("..")]
    [InlineData("../escape.zip")]
    [InlineData("foo/../bar.zip")]
    public void Classify_TraversalAttempt_Rejected(string blob)
    {
        var (_, reason) = InvokeClassify(blob, TenantA);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Classify_NullByte_Rejected()
    {
        var (_, reason) = InvokeClassify("ok\0bad.zip", TenantA);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Classify_Backslash_Rejected()
    {
        // Backslash is a Windows path separator; blob storage rejects it but we
        // belt-and-suspenders block before constructing any URL.
        var (_, reason) = InvokeClassify("foo\\bar.zip", TenantA);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Classify_DoubleEncoding_Rejected()
    {
        // %252F decodes once to %2F (legal) and twice to / (would smuggle a path
        // separator past the slash-check). Reject anything that survives a second
        // decode pass.
        var (_, reason) = InvokeClassify($"{TenantA}%252Fescape.zip", TenantA);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Classify_NestedHostedPath_Rejected()
    {
        // Hosted layout is exactly {tenantId}/{filename} — no deeper nesting.
        // A request for {tenantId}/sub/dir/x.zip would either 404 (best case) or
        // accidentally serve something unexpected; reject up front.
        var (_, reason) = InvokeClassify($"{TenantA}/sub/x.zip", TenantA);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_EmptyOrNull_Rejected(string? blob)
    {
        var (_, reason) = InvokeClassify(blob, TenantA);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Classify_LeadingSlashOnly_Rejected()
    {
        // "/x.zip" has an empty tenant prefix — definitely not a Hosted blob.
        var (_, reason) = InvokeClassify("/x.zip", TenantA);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Classify_TrailingSlashOnly_Rejected()
    {
        var (_, reason) = InvokeClassify($"{TenantA}/", TenantA);
        Assert.NotNull(reason);
    }

    private static (DiagnosticsDownloadFunction.BlobDestination Destination, string? Reason)
        InvokeClassify(string? rawBlobName, string tenantId)
        => DiagnosticsDownloadFunction.ClassifyBlobName(rawBlobName, tenantId);
}
