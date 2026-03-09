using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.Core.Logging;

namespace AutopilotMonitor.Agent.Core.Security
{
    /// <summary>
    /// Waits for an Intune MDM certificate to appear in the certificate store.
    /// Used in await-enrollment mode when the agent is deployed before MDM enrollment completes.
    /// Polls CertificateHelper.FindMdmCertificate() at a fixed interval (Timer-based, no busy-wait).
    /// </summary>
    public static class EnrollmentAwaiter
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Polls the certificate store until a valid MDM certificate is found or the timeout expires.
        /// </summary>
        /// <param name="thumbprint">Optional specific certificate thumbprint to wait for</param>
        /// <param name="timeoutMinutes">Maximum wait time in minutes (0 = wait indefinitely)</param>
        /// <param name="logger">Logger for status output</param>
        /// <param name="cancellationToken">Cancellation token for clean shutdown</param>
        /// <returns>The MDM certificate, or null if the timeout expired or was cancelled</returns>
        public static async Task<X509Certificate2> WaitForMdmCertificateAsync(
            string thumbprint = null,
            int timeoutMinutes = 480,
            AgentLogger logger = null,
            CancellationToken cancellationToken = default)
        {
            var timeoutDisplay = timeoutMinutes > 0 ? $"{timeoutMinutes}min" : "unlimited";
            logger?.Info($"Await-enrollment: Waiting for MDM certificate (timeout: {timeoutDisplay}, poll interval: {PollInterval.TotalSeconds}s)");

            var startTime = DateTime.UtcNow;
            var timeout = timeoutMinutes > 0 ? TimeSpan.FromMinutes(timeoutMinutes) : TimeSpan.MaxValue;
            var lastLogTime = DateTime.MinValue;

            while (!cancellationToken.IsCancellationRequested)
            {
                // Lightweight check: opens cert store, scans by issuer+EKU, closes — takes milliseconds
                var cert = CertificateHelper.FindMdmCertificate(thumbprint);

                if (cert != null && CertificateHelper.IsCertificateValid(cert))
                {
                    var elapsed = DateTime.UtcNow - startTime;
                    logger?.Info($"Await-enrollment: MDM certificate found after {FormatElapsed(elapsed)} — Issuer={cert.Issuer}, Thumbprint={cert.Thumbprint}");
                    return cert;
                }

                var totalElapsed = DateTime.UtcNow - startTime;

                // Check timeout
                if (totalElapsed >= timeout)
                {
                    logger?.Warning($"Await-enrollment: Timeout after {FormatElapsed(totalElapsed)} — no MDM certificate found");
                    return null;
                }

                // Periodic status log (every 1 minute)
                if (DateTime.UtcNow - lastLogTime >= LogInterval)
                {
                    var remaining = timeout - totalElapsed;
                    logger?.Info($"Await-enrollment: Waiting for MDM certificate... (elapsed: {FormatElapsed(totalElapsed)}, remaining: {FormatElapsed(remaining)})");
                    lastLogTime = DateTime.UtcNow;
                }

                try
                {
                    await Task.Delay(PollInterval, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            logger?.Info("Await-enrollment: Cancelled");
            return null;
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
                return $"{(int)elapsed.TotalHours}h{elapsed.Minutes:D2}m";
            return $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s";
        }
    }
}
