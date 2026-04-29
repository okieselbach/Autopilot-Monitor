using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Classification of pre-auth distress signals sent when the authenticated error channel
    /// is unreachable (cert missing, hardware blocked, device not registered, etc.).
    /// </summary>
    public enum DistressErrorType
    {
        /// <summary>No MDM certificate found in the local certificate store.</summary>
        AuthCertificateMissing = 0,

        /// <summary>Certificate found but failed local validation (expired, wrong issuer, etc.).</summary>
        AuthCertificateInvalid = 1,

        /// <summary>Backend returned 401 — certificate rejected by server.</summary>
        AuthCertificateRejected = 2,

        /// <summary>Backend returned 403 — device manufacturer/model not in hardware whitelist.</summary>
        HardwareNotAllowed = 3,

        /// <summary>Backend returned 403 — device not found in Autopilot/Corporate Identifier registry.</summary>
        DeviceNotRegistered = 4,

        /// <summary>Backend returned 403 — tenant not found or suspended.</summary>
        TenantRejected = 5,

        /// <summary>GET /api/agent/config returned an auth error (401/403).</summary>
        ConfigFetchDenied = 6,

        /// <summary>POST /api/agent/register-session returned an auth error (401/403).</summary>
        SessionRegistrationDenied = 7,

        /// <summary>
        /// The device's TPM-backed client certificate cannot perform RSA-PSS signing — Schannel
        /// silently filters the cert out of TLS client-auth on Windows 11 25H2+ (which prefers
        /// PSS), so every mTLS handshake completes with no client cert and the backend rejects
        /// it. Surfaced after a terminal <c>SecureChannelFailure</c> when the PSS capability
        /// probe (<c>TpmPssCapabilityProbe</c>) confirms PKCS#1 works but PSS fails. Common on
        /// 2015-era Infineon TPM 2.0 firmware (e.g. Surface Book 1 SLB 9665); the fix is a TPM
        /// firmware update or device replacement, not an agent change.
        /// </summary>
        TpmPssUnsupported = 8,
    }

    /// <summary>
    /// Lightweight payload sent by the agent to the pre-auth distress channel endpoint
    /// when authentication-related failures prevent use of the normal error channel.
    ///
    /// All fields are treated as UNVERIFIED claims — the distress endpoint has no auth.
    /// Never stored on disk; fire-and-forget from the agent.
    /// </summary>
    public class DistressReport
    {
        /// <summary>
        /// Tenant ID from the device's MDM enrollment registry key. Unverified.
        /// </summary>
        public string TenantId { get; set; } = default!;

        /// <summary>
        /// Classification of the distress signal.
        /// </summary>
        public DistressErrorType ErrorType { get; set; }

        /// <summary>
        /// Device manufacturer (from WMI). Unverified. Max 64 chars.
        /// Useful for whitelist diagnostics ("Acer devices are being blocked").
        /// </summary>
        public string? Manufacturer { get; set; }

        /// <summary>
        /// Device model (from WMI). Unverified. Max 64 chars.
        /// </summary>
        public string? Model { get; set; }

        /// <summary>
        /// Device serial number (from WMI). Unverified. Max 64 chars.
        /// </summary>
        public string? SerialNumber { get; set; }

        /// <summary>
        /// Agent version string. Unverified. Max 32 chars.
        /// </summary>
        public string? AgentVersion { get; set; }

        /// <summary>
        /// HTTP status code returned by the backend, if applicable.
        /// Null for local failures (cert not found, etc.).
        /// </summary>
        public int? HttpStatusCode { get; set; }

        /// <summary>
        /// Short error message. Max 256 chars, sanitized on ingestion.
        /// No stack traces, no sensitive data.
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// UTC timestamp of when the distress occurred on the agent.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
