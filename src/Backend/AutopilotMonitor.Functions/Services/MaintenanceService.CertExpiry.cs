using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Cert-expiry monitoring leg of MaintenanceService. Runs on the existing 2h
    /// timer and emits tiered OpsEvents for embedded Intune CA certs nearing
    /// NotAfter, so the operator gets a Telegram ping months before agent mTLS
    /// would break - bundle rotation is the only fix and needs lead time to
    /// source new PEMs from a corporate-enrolled device.
    /// </summary>
    public partial class MaintenanceService
    {
        // Tier thresholds, expressed as days remaining until NotAfter.
        // Order matters: most-severe first.
        internal const int CertExpiryCriticalThresholdDays = 7;
        internal const int CertExpiryUrgentThresholdDays = 30;
        internal const int CertExpirySoonThresholdDays = 90;

        // Emit-once-per-UTC-day dedup. Runtime caches by (eventType, thumbprint)
        // tuple from today's Security ops events. A cold start re-builds from the
        // table, so Flex Consumption scale-to-zero does not multiply pings.
        internal const string CertExpiryEventTypePrefix = "EmbeddedCert";

        /// <summary>
        /// Pure tier classifier - returns the OpsEvent EventType for a given
        /// daysUntilExpiry, or null if the cert is comfortably far from expiry.
        /// Extracted so the tier boundaries are unit-testable without
        /// constructing a full MaintenanceService.
        /// </summary>
        internal static string? ClassifyCertExpiryTier(int daysUntilExpiry)
        {
            if (daysUntilExpiry <= CertExpiryCriticalThresholdDays) return "EmbeddedCertExpired";
            if (daysUntilExpiry <= CertExpiryUrgentThresholdDays) return "EmbeddedCertExpiringUrgent";
            if (daysUntilExpiry <= CertExpirySoonThresholdDays) return "EmbeddedCertExpiringSoon";
            return null;
        }

        /// <summary>
        /// Inspects the embedded Intune CA bundle (roots + intermediates) and
        /// emits Warning/Error/Critical OpsEvents for any cert near expiry.
        /// Dedup: one event per cert per tier per UTC day, by querying today's
        /// Security ops events.
        /// </summary>
        public async Task CheckEmbeddedCertExpiryAsync()
        {
            var roots = CertificateValidator.GetEmbeddedRoots();
            var intermediates = CertificateValidator.GetEmbeddedIntermediates();
            var seenToday = await BuildCertExpirySeenIndexAsync();

            if (roots.Count == 0)
            {
                if (seenToday.Add($"EmbeddedCertBundleEmpty|"))
                    await _opsEventService.RecordEmbeddedCertBundleEmptyAsync();
                _logger.LogError("Embedded Intune root bundle is empty - agent mTLS will fail closed");
                return;
            }

            await EvaluateCertBucketAsync(roots, "Root", seenToday);
            await EvaluateCertBucketAsync(intermediates, "Intermediate", seenToday);
        }

        private async Task EvaluateCertBucketAsync(
            IReadOnlyList<X509Certificate2> certs, string role, HashSet<string> seenToday)
        {
            var now = DateTime.UtcNow;
            foreach (var cert in certs)
            {
                var notAfterUtc = cert.NotAfter.ToUniversalTime();
                var daysLeft = (int)Math.Floor((notAfterUtc - now).TotalDays);
                var eventType = ClassifyCertExpiryTier(daysLeft);
                if (eventType is null) continue;

                var dedupKey = $"{eventType}|{cert.Thumbprint}";
                if (!seenToday.Add(dedupKey))
                {
                    _logger.LogDebug("Cert expiry already emitted today: {Key}", dedupKey);
                    continue;
                }

                switch (eventType)
                {
                    case "EmbeddedCertExpired":
                        await _opsEventService.RecordEmbeddedCertExpiredAsync(
                            role, cert.Subject, cert.Thumbprint, notAfterUtc, daysLeft);
                        break;
                    case "EmbeddedCertExpiringUrgent":
                        await _opsEventService.RecordEmbeddedCertExpiringUrgentAsync(
                            role, cert.Subject, cert.Thumbprint, notAfterUtc, daysLeft);
                        break;
                    case "EmbeddedCertExpiringSoon":
                        await _opsEventService.RecordEmbeddedCertExpiringSoonAsync(
                            role, cert.Subject, cert.Thumbprint, notAfterUtc, daysLeft);
                        break;
                }
            }
        }

        private async Task<HashSet<string>> BuildCertExpirySeenIndexAsync()
        {
            var todayUtc = DateTime.UtcNow.Date;
            var todaysSecurityEvents = await _opsEventRepo.GetOpsEventsAsync(
                category: OpsEventCategory.Security,
                dateFrom: todayUtc,
                dateTo: todayUtc.AddDays(1));

            return todaysSecurityEvents
                .Where(e => e.EventType.StartsWith(CertExpiryEventTypePrefix, StringComparison.Ordinal))
                .Select(e => $"{e.EventType}|{ExtractThumbprint(e.Details)}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        internal static string ExtractThumbprint(string? detailsJson)
        {
            if (string.IsNullOrEmpty(detailsJson)) return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(detailsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("thumbprint", out var t) &&
                    t.ValueKind == JsonValueKind.String)
                {
                    return t.GetString() ?? string.Empty;
                }
            }
            catch (JsonException)
            {
                // Malformed details JSON - dedup key falls back to empty thumbprint
            }
            return string.Empty;
        }
    }
}
