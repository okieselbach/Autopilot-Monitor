using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// Diagnostic probe that asks the local key-storage provider to produce three RSA signatures
    /// over a fixed payload — PKCS#1 v1.5 SHA-256 (legacy, what every TPM 2.0 supports),
    /// PSS SHA-256, and PSS SHA-384 (what modern Schannel prefers for TLS 1.2/1.3 client-auth
    /// on Windows 11 25H2+).
    /// <para>
    /// The probe exists for one reason: to distinguish a generic <c>SecureChannelFailure</c>
    /// from the specific case where a TPM-backed cert is silently filtered out of TLS client-auth
    /// because its firmware can't do PSS. On 2015-era Infineon TPM 2.0 chips (Surface Book 1
    /// SLB 9665 is the canonical example) PKCS#1 succeeds but PSS reports
    /// <c>"The requested salt size for signing with RSAPSS does not match what the TPM uses"</c>
    /// or <c>"The requested operation is not supported"</c>. Schannel observes this during cert
    /// pre-filtering, drops the cert, and the handshake then completes with an empty client
    /// Certificate message — which the backend rejects, surfacing in .NET as the generic
    /// <c>WebException.Status = SecureChannelFailure</c>.
    /// </para>
    /// <para>
    /// <b>Hot-path discipline:</b> this probe is intentionally NOT called at agent startup.
    /// It must only be invoked on the failure path after the session-registration retry loop
    /// has terminally failed with <c>SecureChannelFailure</c>, so healthy devices never pay
    /// the ~1 second of TPM operations this involves.
    /// </para>
    /// </summary>
    public static class TpmPssCapabilityProbe
    {
        /// <summary>
        /// Performs three signing attempts (PKCS#1 SHA-256, PSS SHA-256, PSS SHA-384) and returns
        /// a result describing which the local key provider can do. Never throws.
        /// </summary>
        public static TpmPssProbeResult Probe(X509Certificate2 cert, AgentLogger logger = null)
        {
            if (cert == null)
                return TpmPssProbeResult.Unprobable("(no certificate)");

            RSA rsa;
            try
            {
                rsa = RSACertificateExtensions.GetRSAPrivateKey(cert);
            }
            catch (Exception ex)
            {
                logger?.Debug($"TpmPssCapabilityProbe: cannot acquire RSA private key: {ex.Message}");
                return TpmPssProbeResult.Unprobable($"GetRSAPrivateKey failed: {ex.Message}");
            }

            if (rsa == null)
                return TpmPssProbeResult.Unprobable("(certificate has no RSA private key)");

            var providerName = TryGetProviderName(rsa);
            var keySizeBits = TryGetKeySize(rsa);
            var data = Encoding.UTF8.GetBytes("autopilot-monitor-tpm-probe");

            var pkcs1 = TrySignData(rsa, data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var pssSha256 = TrySignData(rsa, data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            var pssSha384 = TrySignData(rsa, data, HashAlgorithmName.SHA384, RSASignaturePadding.Pss);

            try { rsa.Dispose(); } catch { /* best-effort */ }

            return new TpmPssProbeResult(
                pkcs1Sha256Works: pkcs1.Success,
                pssSha256Works: pssSha256.Success,
                pssSha384Works: pssSha384.Success,
                providerName: providerName,
                keySizeBits: keySizeBits,
                pssSha256Error: pssSha256.ErrorMessage,
                pssSha384Error: pssSha384.ErrorMessage);
        }

        private static (bool Success, string ErrorMessage) TrySignData(
            RSA rsa, byte[] data, HashAlgorithmName hash, RSASignaturePadding padding)
        {
            try
            {
                var sig = rsa.SignData(data, hash, padding);
                return (sig != null && sig.Length > 0, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static string TryGetProviderName(RSA rsa)
        {
            // RSACng is the .NET wrapper around CNG keys — TPM-backed keys (Microsoft Platform
            // Crypto Provider) and software CNG keys (Microsoft Software Key Storage Provider)
            // both surface as RSACng. RSACryptoServiceProvider is the legacy CryptoAPI path.
            try
            {
                if (rsa is RSACng rsaCng)
                    return rsaCng.Key?.Provider?.Provider ?? "(cng-no-provider)";
            }
            catch { /* fall through */ }

            try
            {
                if (rsa is RSACryptoServiceProvider rsaCsp)
                    return rsaCsp.CspKeyContainerInfo?.ProviderName ?? "(csp-no-provider)";
            }
            catch { /* fall through */ }

            return rsa.GetType().Name;
        }

        private static int TryGetKeySize(RSA rsa)
        {
            try { return rsa.KeySize; } catch { return 0; }
        }
    }

    /// <summary>
    /// Outcome of <see cref="TpmPssCapabilityProbe.Probe"/>. Treat as a snapshot — do not retain
    /// across cert refreshes.
    /// </summary>
    public sealed class TpmPssProbeResult
    {
        public bool Pkcs1Sha256Works { get; }
        public bool PssSha256Works { get; }
        public bool PssSha384Works { get; }
        public string ProviderName { get; }
        public int KeySizeBits { get; }
        public string PssSha256Error { get; }
        public string PssSha384Error { get; }
        public bool IsProbable { get; }

        /// <summary>
        /// True iff PKCS#1 succeeded but PSS did not — the canonical "TPM firmware is too old
        /// for modern Schannel" footprint. Distinguishes the TPM-PSS regression from generic
        /// crypto failures (which would also fail PKCS#1).
        /// </summary>
        public bool IsTpmPssBroken => IsProbable && Pkcs1Sha256Works && !PssSha256Works;

        /// <summary>
        /// True iff the provider name explicitly identifies a TPM-backed key. Lets the caller
        /// avoid emitting <c>TpmPssUnsupported</c> distress when PSS fails on a software key
        /// (which would mean a different bug class entirely).
        /// </summary>
        public bool IsTpmBacked =>
            !string.IsNullOrEmpty(ProviderName) &&
            ProviderName.IndexOf("Platform Crypto", StringComparison.OrdinalIgnoreCase) >= 0;

        public TpmPssProbeResult(
            bool pkcs1Sha256Works,
            bool pssSha256Works,
            bool pssSha384Works,
            string providerName,
            int keySizeBits,
            string pssSha256Error,
            string pssSha384Error)
        {
            Pkcs1Sha256Works = pkcs1Sha256Works;
            PssSha256Works = pssSha256Works;
            PssSha384Works = pssSha384Works;
            ProviderName = providerName ?? "(unknown)";
            KeySizeBits = keySizeBits;
            PssSha256Error = pssSha256Error;
            PssSha384Error = pssSha384Error;
            IsProbable = true;
        }

        private TpmPssProbeResult(string failureSummary)
        {
            ProviderName = "(unprobable)";
            PssSha256Error = failureSummary;
            IsProbable = false;
        }

        public static TpmPssProbeResult Unprobable(string reason) => new TpmPssProbeResult(reason);

        /// <summary>
        /// Compact, distress-message-friendly summary (under 256 chars). Caller passes this to
        /// <c>DistressReporter.TrySendAsync</c>.
        /// </summary>
        public string ToDistressMessage()
        {
            var sb = new StringBuilder();
            sb.Append("provider=").Append(ProviderName);
            if (KeySizeBits > 0) sb.Append(" keySize=").Append(KeySizeBits);
            sb.Append(" pkcs1=").Append(Pkcs1Sha256Works ? "ok" : "fail");
            sb.Append(" pssSha256=").Append(PssSha256Works ? "ok" : "fail");
            sb.Append(" pssSha384=").Append(PssSha384Works ? "ok" : "fail");
            if (!PssSha256Works && !string.IsNullOrEmpty(PssSha256Error))
            {
                sb.Append(" err256=").Append(PssSha256Error);
            }
            const int maxLen = 240;
            return sb.Length <= maxLen ? sb.ToString() : sb.ToString(0, maxLen);
        }
    }
}
