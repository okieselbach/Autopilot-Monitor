using AutopilotMonitor.Agent.Core.Monitoring.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Monitoring
{
    /// <summary>
    /// Phase 3 of the hosted-diagnostics work: V1 agent must build the blob PUT URL
    /// destination-aware so a Hosted SAS (already blob-scoped at
    /// <c>{tenantId}/{filename}</c>) is used as-is, while a CustomerSas (container-scoped)
    /// continues to get the blob name appended. Mirrors the V2 tests in
    /// <c>AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime.DiagnosticsPackageServiceTests</c>.
    /// V1 stays on this code path until phase-out per <c>feedback_codex_findings_v2_only</c>.
    /// </summary>
    public class DiagnosticsPackageServiceUrlBuilderTests
    {
        [Theory]
        [InlineData("Hosted")]
        [InlineData("hosted")]
        [InlineData("HOSTED")]
        public void BuildBlobUploadUrl_HostedDestination_ReturnsSasUnchanged(string destination)
        {
            const string hostedSas = "https://account.blob.core.windows.net/diagnostics/11111111-1111-1111-1111-111111111111/AgentDiagnostics-x.zip?sig=abc";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(hostedSas, "AgentDiagnostics-x.zip", destination);
            Assert.Equal(hostedSas, result);
        }

        [Fact]
        public void BuildBlobUploadUrl_CustomerSas_AppendsBlobNameBeforeQuery()
        {
            const string containerSas = "https://customer.blob.core.windows.net/diagnostics?sv=2024-10-04&sig=xyz";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(containerSas, "AgentDiagnostics-x.zip", "CustomerSas");
            Assert.Equal(
                "https://customer.blob.core.windows.net/diagnostics/AgentDiagnostics-x.zip?sv=2024-10-04&sig=xyz",
                result);
        }

        [Fact]
        public void BuildBlobUploadUrl_NullDestination_AppendsBlobName_LegacyBackendCompat()
        {
            // Older backends without the Destination field return null; agent must
            // preserve the historical container-SAS append behaviour.
            const string containerSas = "https://customer.blob.core.windows.net/diag?sig=abc";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(containerSas, "diag.zip", null);
            Assert.Equal("https://customer.blob.core.windows.net/diag/diag.zip?sig=abc", result);
        }

        [Fact]
        public void BuildBlobUploadUrl_UnknownDestination_FallsBackToCustomerSasBehaviour()
        {
            const string containerSas = "https://customer.blob.core.windows.net/diag?sig=abc";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(containerSas, "diag.zip", "Vendor");
            Assert.EndsWith("/diag.zip?sig=abc", result);
        }

        [Fact]
        public void BuildBlobUploadUrl_SasWithoutQueryString_AppendsBlobName()
        {
            const string noQuery = "https://customer.blob.core.windows.net/diag";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(noQuery, "diag.zip", "CustomerSas");
            Assert.Equal("https://customer.blob.core.windows.net/diag/diag.zip", result);
        }

        [Fact]
        public void BuildBlobUploadUrl_HostedWithoutQueryString_StillReturnsUnchanged()
        {
            const string hostedNoQuery = "https://account.blob.core.windows.net/diagnostics/tenant/x.zip";
            var result = DiagnosticsPackageService.BuildBlobUploadUrl(hostedNoQuery, "x.zip", "Hosted");
            Assert.Equal(hostedNoQuery, result);
        }
    }
}
