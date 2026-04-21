using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// H-2 mitigation: removes the persisted bootstrap token (<c>bootstrap-config.json</c>)
    /// once the agent has proven it can authenticate with its MDM client certificate.
    ///
    /// The bootstrap token is only needed during OOBE (before the MDM cert is issued). Leaving
    /// it on disk after enrollment creates an unnecessary secret-at-rest window: <c>%ProgramData%</c>
    /// grants <c>BUILTIN\Users</c> read access by default, so any post-OOBE interactive user
    /// could exfiltrate the token and replay it against the bootstrap endpoints until expiry.
    ///
    /// The cleanup runs a single-shot, cert-authenticated probe against <c>/api/agent/config</c>
    /// using a dedicated <see cref="HttpClient"/>. If the probe succeeds, the file is deleted.
    /// Any failure (cert missing, network error, non-2xx response) is logged and the file is
    /// left in place — the next run will try again. This path is strictly additive: a failed
    /// probe never disrupts the in-progress bootstrap session.
    /// </summary>
    public static class BootstrapConfigCleanup
    {
        private const string BootstrapConfigFileName = "bootstrap-config.json";

        /// <summary>
        /// Attempts to remove the persisted bootstrap-config.json after verifying the MDM client
        /// certificate authenticates successfully against the backend. Safe no-op if the agent
        /// is not in bootstrap-token mode, the file is absent, the cert is missing, or the
        /// probe call fails.
        /// </summary>
        public static async Task TryDeleteIfCertReadyAsync(
            AgentConfiguration configuration,
            AgentLogger logger,
            string agentVersion)
        {
            if (configuration == null || !configuration.UseBootstrapTokenAuth)
                return;

            var bootstrapPath = Path.Combine(
                Environment.ExpandEnvironmentVariables(Constants.AgentDataDirectory),
                BootstrapConfigFileName);

            if (!File.Exists(bootstrapPath))
                return;

            X509Certificate2 cert;
            try
            {
                cert = CertificateHelper.FindMdmCertificate(logger: logger);
            }
            catch (Exception ex)
            {
                logger?.Warning($"Bootstrap cleanup: MDM cert lookup threw ({ex.Message}), keeping {BootstrapConfigFileName}");
                return;
            }

            if (cert == null)
            {
                logger?.Info($"Bootstrap cleanup: MDM cert not yet available, keeping {BootstrapConfigFileName}");
                return;
            }

            var handler = new HttpClientHandler();
            handler.ClientCertificates.Add(cert);

            using (handler)
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) })
            {
                var request = BuildProbeRequest(configuration, agentVersion);
                await ProbeAndDeleteCoreAsync(bootstrapPath, http, request, logger).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Builds the probe request: GET /api/agent/config with the standard agent security
        /// headers, but <b>without</b> the X-Bootstrap-Token header — authentication must come
        /// from the TLS-layer client certificate alone.
        /// </summary>
        private static HttpRequestMessage BuildProbeRequest(AgentConfiguration configuration, string agentVersion)
        {
            var url = $"{configuration.ApiBaseUrl.TrimEnd('/')}{Constants.ApiEndpoints.GetAgentConfig}";
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (!string.IsNullOrEmpty(configuration.TenantId))
                request.Headers.Add("X-Tenant-Id", configuration.TenantId);

            var hw = HardwareInfo.GetHardwareInfo(null);
            if (!string.IsNullOrEmpty(hw.Manufacturer))
                request.Headers.Add("X-Device-Manufacturer", hw.Manufacturer);
            if (!string.IsNullOrEmpty(hw.Model))
                request.Headers.Add("X-Device-Model", hw.Model);
            if (!string.IsNullOrEmpty(hw.SerialNumber))
                request.Headers.Add("X-Device-SerialNumber", hw.SerialNumber);
            if (!string.IsNullOrEmpty(agentVersion))
                request.Headers.Add("X-Agent-Version", agentVersion);

            var userAgent = string.IsNullOrEmpty(agentVersion)
                ? "AutopilotMonitor.Agent"
                : $"AutopilotMonitor.Agent/{agentVersion}";
            request.Headers.UserAgent.ParseAdd(userAgent);

            return request;
        }

        /// <summary>
        /// Pure-IO core: sends the probe, deletes the file on 2xx, logs + keeps on anything
        /// else. Exposed <c>internal</c> for unit tests that inject a fake
        /// <see cref="HttpMessageHandler"/>.
        /// </summary>
        internal static async Task ProbeAndDeleteCoreAsync(
            string bootstrapConfigPath,
            HttpClient httpClient,
            HttpRequestMessage request,
            AgentLogger logger)
        {
            try
            {
                using (request)
                using (var response = await httpClient.SendAsync(request).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode)
                    {
                        try
                        {
                            if (File.Exists(bootstrapConfigPath))
                                File.Delete(bootstrapConfigPath);
                            logger?.Info("Bootstrap cleanup: cert probe succeeded — bootstrap-config.json removed, cert auth is now operational");
                        }
                        catch (Exception delEx)
                        {
                            logger?.Warning($"Bootstrap cleanup: cert probe succeeded but delete failed ({delEx.Message}); will retry on next run");
                        }
                    }
                    else
                    {
                        logger?.Warning($"Bootstrap cleanup: cert probe returned HTTP {(int)response.StatusCode}, keeping {BootstrapConfigFileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warning($"Bootstrap cleanup: cert probe failed ({ex.Message}), keeping {BootstrapConfigFileName}");
            }
        }
    }
}
