using System;
using AutopilotMonitor.Shared.Diagnostics;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Round-trip + tamper + replay tests for the diagnostics download-ticket codec.
/// The ticket is the SOLE authority on the anonymous download route, so every
/// forgery / retarget / expiry vector must fail closed. Signing key is injected
/// process-wide by <see cref="TestSetup"/>.
/// </summary>
public class DiagnosticsDownloadTicketTests
{
    private const string TenantA = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string TenantB = "b2c3d4e5-f6a7-8901-bcde-f12345678901";
    private const string BlobA = "AgentDiagnostics-abc-20260622.zip";
    private const string BlobHosted = "a1b2c3d4-e5f6-7890-abcd-ef1234567890/AgentDiagnostics-abc.zip";

    [Fact]
    public void RoundTrip_ValidTicket_DecodesToSameValues()
    {
        var token = DiagnosticsDownloadTicket.Encode(TenantA, BlobA, "CustomerSas");

        var ok = DiagnosticsDownloadTicket.TryDecode(token, out var tid, out var blob, out var dst, out var reason);

        Assert.True(ok);
        Assert.Null(reason);
        Assert.Equal(TenantA, tid);
        Assert.Equal(BlobA, blob);
        Assert.Equal("CustomerSas", dst);
    }

    [Fact]
    public void RoundTrip_HostedBlobName_Preserved()
    {
        var token = DiagnosticsDownloadTicket.Encode(TenantA, BlobHosted, "Hosted");
        var ok = DiagnosticsDownloadTicket.TryDecode(token, out var tid, out var blob, out var dst, out _);
        Assert.True(ok);
        Assert.Equal(TenantA, tid);
        Assert.Equal(BlobHosted, blob);
        Assert.Equal("Hosted", dst);
    }

    [Fact]
    public void Tampered_Payload_Rejected()
    {
        var token = DiagnosticsDownloadTicket.Encode(TenantA, BlobA, "CustomerSas");
        // Flip a character in the middle of the base64url payload.
        var chars = token.ToCharArray();
        var mid = chars.Length / 2;
        chars[mid] = chars[mid] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        var ok = DiagnosticsDownloadTicket.TryDecode(tampered, out _, out _, out _, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Expired_Ticket_Rejected()
    {
        var issued = DateTimeOffset.UtcNow.AddMinutes(-30);
        var token = DiagnosticsDownloadTicket.Encode(TenantA, BlobA, "CustomerSas", issued);

        var ok = DiagnosticsDownloadTicket.TryDecode(token, out _, out _, out _, out var reason);

        Assert.False(ok);
        Assert.Equal("expired", reason);
    }

    [Fact]
    public void WithinTtl_Ticket_Accepted()
    {
        var issued = DateTimeOffset.UtcNow.AddMinutes(-5);
        var token = DiagnosticsDownloadTicket.Encode(TenantA, BlobA, "CustomerSas", issued);

        var ok = DiagnosticsDownloadTicket.TryDecode(token, out _, out _, out _, out _);

        Assert.True(ok);
    }

    [Fact]
    public void CannotRetarget_BlobOrTenant_BecauseTheyAreSigned()
    {
        // The download endpoint reads tenantId+blobName FROM the ticket, never from the query.
        // A ticket minted for TenantA/BlobA always decodes to exactly those — there is no API
        // surface to swap them without re-signing (which requires the server key).
        var token = DiagnosticsDownloadTicket.Encode(TenantA, BlobA, "CustomerSas");
        Assert.True(DiagnosticsDownloadTicket.TryDecode(token, out var tid, out var blob, out _, out _));
        Assert.Equal(TenantA, tid);
        Assert.Equal(BlobA, blob);
        Assert.NotEqual(TenantB, tid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("....")]
    public void Malformed_Tokens_Rejected(string raw)
    {
        var ok = DiagnosticsDownloadTicket.TryDecode(raw, out _, out _, out _, out var reason);
        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public void ContinuationToken_CannotBeReplayed_AsDownloadTicket()
    {
        // Domain separation: a token signed with the SAME key but for pagination must not
        // validate as a download ticket (different purpose tag + JSON shape).
        var fp = AutopilotMonitor.Shared.Pagination.ContinuationToken.ComputeFingerprint(
            new[] { new System.Collections.Generic.KeyValuePair<string, string?>("tenantId", TenantA) });
        var continuation = AutopilotMonitor.Shared.Pagination.ContinuationToken.Encode("azureTok", TenantA, fp);

        var ok = DiagnosticsDownloadTicket.TryDecode(continuation, out _, out _, out _, out var reason);

        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Encode_RequiresTenantAndBlob()
    {
        Assert.Throws<ArgumentException>(() => DiagnosticsDownloadTicket.Encode("", BlobA, "CustomerSas"));
        Assert.Throws<ArgumentException>(() => DiagnosticsDownloadTicket.Encode(TenantA, "", "CustomerSas"));
    }
}
