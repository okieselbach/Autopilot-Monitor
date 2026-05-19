using System.Text.Json;
using AutopilotMonitor.Functions.Functions.Diagnostics;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the destination-routing decision in <see cref="GetDiagnosticsUploadUrlFunction"/>
/// and the wire-shape of <see cref="GetDiagnosticsUploadUrlResponse"/>.
/// <para>
/// The HTTP entry point (<c>Run</c>) is intentionally NOT tested here — mocking
/// <c>HttpRequestData</c> + the entire <c>ValidateSecurityAsync</c> dependency chain
/// would be more setup than the test is worth, and the actual SAS-issuance logic is
/// already covered by <see cref="HostedDiagnosticsBlobServiceTests"/>. The end-to-end
/// behaviour is exercised by Phase 7's manual e2e per the plan.
/// </para>
/// </summary>
public class GetDiagnosticsUploadUrlFunctionTests
{
    // ── Destination normalization ─────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "CustomerSas")]                  // legacy rows
    [InlineData("", "CustomerSas")]                    // empty
    [InlineData("CustomerSas", "CustomerSas")]         // canonical
    [InlineData("customersas", "CustomerSas")]         // case-insensitive
    [InlineData("CUSTOMERSAS", "CustomerSas")]
    [InlineData("Hosted", "Hosted")]                   // canonical
    [InlineData("hosted", "Hosted")]                   // case-insensitive
    [InlineData("HOSTED", "Hosted")]
    public void NormalizeDestination_MapsKnownValuesCanonical(string? raw, string expected)
    {
        Assert.Equal(expected, GetDiagnosticsUploadUrlFunction.NormalizeDestination(raw));
    }

    [Theory]
    [InlineData("Whatever")]
    [InlineData("vendor")]   // old name that must NOT silently resolve
    [InlineData("self")]
    public void NormalizeDestination_PassesUnknownThroughForCallerRejection(string raw)
    {
        // Unknown values are passed through verbatim so the Function can reject with a
        // 500 + log line, instead of silently defaulting (which could mask a misconfig).
        Assert.Equal(raw, GetDiagnosticsUploadUrlFunction.NormalizeDestination(raw));
    }

    // ── DTO wire shape ────────────────────────────────────────────────────────────

    [Fact]
    public void Response_NewFields_SerializeAndRoundtripViaJson()
    {
        var dto = new GetDiagnosticsUploadUrlResponse
        {
            Success = true,
            UploadUrl = "https://example.blob.core.windows.net/diagnostics/abc?sig=...",
            BlobName = "11111111-1111-1111-1111-111111111111/AgentDiagnostics-x.zip",
            Destination = "Hosted",
            ExpiresAt = new DateTime(2026, 5, 19, 12, 0, 0, DateTimeKind.Utc),
        };

        var json = JsonSerializer.Serialize(dto);
        Assert.Contains("\"BlobName\"", json);
        Assert.Contains("\"Destination\"", json);

        var roundtrip = JsonSerializer.Deserialize<GetDiagnosticsUploadUrlResponse>(json);
        Assert.NotNull(roundtrip);
        Assert.Equal(dto.BlobName, roundtrip!.BlobName);
        Assert.Equal(dto.Destination, roundtrip.Destination);
    }

    [Fact]
    public void Response_NewFields_AreOptionalForBackCompat()
    {
        // An older backend response without BlobName/Destination must still deserialize —
        // the agent's fallback path needs these as nullable to detect "old backend, use
        // request filename as BlobName".
        const string legacyJson = """
            { "Success": true, "UploadUrl": "https://example/?sig=...", "ExpiresAt": "2026-05-19T12:00:00Z" }
            """;
        var dto = JsonSerializer.Deserialize<GetDiagnosticsUploadUrlResponse>(legacyJson);
        Assert.NotNull(dto);
        Assert.Null(dto!.BlobName);
        Assert.Null(dto.Destination);
        Assert.True(dto.Success);
    }
}
