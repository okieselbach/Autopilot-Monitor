using System;
using System.Net.Http;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Phase 3 + Phase 5 of <see cref="Program"/>'s <c>RunAgent</c>: backend HTTP-clients
    /// (BackendApiClient + reporters + auth-failure tracker) and the mTLS-backed telemetry
    /// uploader. Phase 4 (RemoteConfig fetch + merge + binary-integrity check) lives in
    /// <c>RunAgent</c> between the two factory calls and consumes Phase 3's clients to
    /// produce the merged <see cref="AgentConfigResponse"/>.
    /// </summary>
    internal static class BackendClientFactory
    {
        /// <summary>
        /// Phase 3 — construct the backend HTTP clients, the distress / emergency reporters
        /// and the auth-failure tracker. Fires a one-shot <c>AuthCertificateMissing</c>
        /// distress when client-cert auth is configured but no cert was found in either
        /// LocalMachine or CurrentUser store (Legacy parity — pre-MDM-enrollment dead-end
        /// surface). Tracker ceilings reflect the CLI/bootstrap defaults; tenant-policy
        /// overrides are applied later via <see cref="AuthFailureTracker.UpdateLimits"/>
        /// once <c>RemoteConfigMerger</c> has run.
        /// </summary>
        public static BackendAuthBundle BuildAuthClients(
            AgentConfiguration agentConfig,
            string agentVersion,
            AgentLogger logger)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            var backendApiClient = new BackendApiClient(
                baseUrl: agentConfig.ApiBaseUrl,
                configuration: agentConfig,
                logger: logger,
                agentVersion: agentVersion);

            // M4.6.γ — Emergency + Distress reporters. Plumbed into RemoteConfigService so
            // Config-fetch failures (auth vs network) flow to the correct channel. Also fires
            // an initial AuthCertificateMissing distress when the MDM cert was expected but
            // not found (Legacy parity — surfaces pre-MDM-enrollment dead-ends to the backend
            // via the cert-less distress channel).
            var hardware = HardwareInfo.GetHardwareInfo(logger);
            var distressReporter = new DistressReporter(
                baseUrl: agentConfig.ApiBaseUrl,
                tenantId: agentConfig.TenantId,
                manufacturer: hardware.Manufacturer,
                model: hardware.Model,
                serialNumber: hardware.SerialNumber,
                agentVersion: agentVersion,
                logger: logger);

            var emergencyReporter = new EmergencyReporter(
                apiClient: backendApiClient,
                sessionId: agentConfig.SessionId,
                tenantId: agentConfig.TenantId,
                agentVersion: agentVersion,
                logger: logger);

            if (agentConfig.UseClientCertAuth && backendApiClient.ClientCertificate == null)
            {
                _ = distressReporter.TrySendAsync(
                    DistressErrorType.AuthCertificateMissing,
                    "MDM certificate not found in LocalMachine or CurrentUser store");
            }

            // Central observer for consecutive 401/403 responses. Initialised with the CLI/bootstrap
            // defaults on AgentConfiguration; UpdateLimits is called after RemoteConfigMerger.Merge
            // so tenant-policy overrides take effect. ThresholdExceeded is wired in RunAgent, once
            // the shutdown signal is available, to trigger a clean agent exit when the limit is hit.
            // V1 parity — the distress reporter is plumbed in at construction so the tracker is
            // the single dispatch point for auth-failure distress (first failure only).
            var authFailureTracker = new AuthFailureTracker(
                maxFailures: agentConfig.MaxAuthFailures,
                timeoutMinutes: agentConfig.AuthFailureTimeoutMinutes,
                clock: SystemClock.Instance,
                logger: logger,
                distressReporter: distressReporter);

            return new BackendAuthBundle(
                backendApiClient: backendApiClient,
                manufacturer: hardware.Manufacturer,
                model: hardware.Model,
                serialNumber: hardware.SerialNumber,
                distressReporter: distressReporter,
                emergencyReporter: emergencyReporter,
                authFailureTracker: authFailureTracker);
        }

        /// <summary>
        /// Phase 5 — construct the mTLS-backed <see cref="HttpClient"/> and the
        /// <see cref="BackendTelemetryUploader"/>. The uploader's <see cref="NetworkMetrics"/>
        /// instance is shared with the legacy <see cref="BackendApiClient"/>'s pipeline so
        /// the agent_metrics_snapshot net_total_requests reflects every outbound HTTP call,
        /// including the dominant <c>/api/agent/telemetry</c> POST (Plan §5 Fix 5 — without
        /// this the counter undercounts ~25-30x).
        /// </summary>
        public static TelemetryClientResult BuildTelemetryClients(
            AgentConfiguration agentConfig,
            BackendAuthBundle auth,
            string agentVersion,
            AgentLogger logger)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            HttpClient mtlsHttpClient;
            try
            {
                // Share BackendApiClient.NetworkMetrics so the mTLS pipeline records every
                // outbound request — most importantly BackendTelemetryUploader's POST
                // /api/agent/telemetry, which dominates per-session traffic. Without this the
                // agent_metrics_snapshot net_total_requests undercounts ~25-30x (only the
                // legacy BackendApiClient calls would show up).
                mtlsHttpClient = MtlsHttpClientFactory.Create(
                    resolver: new DefaultCertificateResolver(),
                    logger: logger,
                    metrics: auth.BackendApiClient.NetworkMetrics);
            }
            catch (InvalidOperationException ex)
            {
                logger.Error("mTLS HttpClient creation failed — cannot upload telemetry.", ex);
                return TelemetryClientResult.Exit(4);
            }

            BackendTelemetryUploader uploader;
            try
            {
                uploader = new BackendTelemetryUploader(
                    httpClient: mtlsHttpClient,
                    baseUrl: agentConfig.ApiBaseUrl,
                    tenantId: agentConfig.TenantId,
                    manufacturer: auth.Manufacturer,
                    model: auth.Model,
                    serialNumber: auth.SerialNumber,
                    bootstrapToken: agentConfig.UseBootstrapTokenAuth ? agentConfig.BootstrapToken : null,
                    agentVersion: agentVersion,
                    authFailureTracker: auth.AuthFailureTracker,
                    logger: logger);     // Plan §5 Fix 5 — upload-cadence logging
            }
            catch (Exception ex)
            {
                logger.Error("BackendTelemetryUploader construction failed.", ex);
                return TelemetryClientResult.Exit(5);
            }

            return TelemetryClientResult.Continue(mtlsHttpClient, uploader);
        }
    }

    /// <summary>
    /// Phase 3 outcome — the four backend-facing clients and the cached hardware identity
    /// they were built with. RunAgent retains this bundle through the rest of startup so
    /// the lifecycle / termination wiring can read each client without re-construction.
    /// </summary>
    internal sealed class BackendAuthBundle
    {
        public BackendApiClient BackendApiClient { get; }
        public string Manufacturer { get; }
        public string Model { get; }
        public string SerialNumber { get; }
        public DistressReporter DistressReporter { get; }
        public EmergencyReporter EmergencyReporter { get; }
        public AuthFailureTracker AuthFailureTracker { get; }

        public BackendAuthBundle(
            BackendApiClient backendApiClient,
            string manufacturer,
            string model,
            string serialNumber,
            DistressReporter distressReporter,
            EmergencyReporter emergencyReporter,
            AuthFailureTracker authFailureTracker)
        {
            BackendApiClient = backendApiClient;
            Manufacturer = manufacturer;
            Model = model;
            SerialNumber = serialNumber;
            DistressReporter = distressReporter;
            EmergencyReporter = emergencyReporter;
            AuthFailureTracker = authFailureTracker;
        }
    }

    /// <summary>
    /// Phase 5 outcome — either an early exit (V1 parity: 4 = mTLS construction failure,
    /// 5 = uploader construction failure) or a Continue payload carrying the live mTLS
    /// <see cref="HttpClient"/> and <see cref="BackendTelemetryUploader"/>.
    /// </summary>
    internal sealed class TelemetryClientResult
    {
        public bool ShouldExit { get; }
        public int ExitCode { get; }
        public HttpClient MtlsHttpClient { get; }
        public BackendTelemetryUploader Uploader { get; }

        private TelemetryClientResult(bool shouldExit, int exitCode, HttpClient mtls, BackendTelemetryUploader uploader)
        {
            ShouldExit = shouldExit;
            ExitCode = exitCode;
            MtlsHttpClient = mtls;
            Uploader = uploader;
        }

        public static TelemetryClientResult Exit(int code)
            => new TelemetryClientResult(true, code, null, null);

        public static TelemetryClientResult Continue(HttpClient mtls, BackendTelemetryUploader uploader)
            => new TelemetryClientResult(false, 0, mtls, uploader);
    }
}
