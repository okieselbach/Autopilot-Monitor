#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office
{
    /// <summary>
    /// Classifies a C2R <c>ProductReleaseIds</c> set as consumer / OEM-inbox Office vs an IT-managed
    /// deployment. Used to label an already-resident Office (core binaries present at the first
    /// signal) as preinstalled inbox Office — e.g. the <c>O365HomePremRetail</c> + <c>OneNoteFreeRetail</c>
    /// that ship pre-provisioned on many OEM Windows images and run background CLIENTUPDATE scenarios
    /// during an enrollment — rather than mis-reporting it as a Microsoft 365 Apps install/failure.
    /// </summary>
    internal static class OfficeProductClassifier
    {
        // Enterprise / IT-managed C2R product markers (Microsoft 365 Apps for enterprise/business,
        // volume). When ANY product carries one of these the set is treated as managed, never
        // "consumer inbox" — even if a consumer SKU is co-listed. NOTE: the business marker is the
        // specific "O365Business" prefix (the managed "Microsoft 365 Apps for business" id, e.g.
        // O365BusinessRetail), NOT a bare "Business" — otherwise the consumer perpetual HomeBusiness*Retail
        // SKUs would be wrongly caught here before the consumer markers below.
        private static readonly string[] ManagedMarkers = { "ProPlus", "O365Business", "Volume" };

        // Consumer / OEM-preinstall SKU markers (substring, case-insensitive).
        private static readonly string[] ConsumerMarkers =
        {
            "HomePrem", "HomeStudent", "HomeBusiness", "Personal", "OneNoteFree", "Professional",
        };

        /// <summary>
        /// True when the product set is non-empty AND every entry looks like a consumer / OEM-inbox SKU
        /// and none is an enterprise/business/volume SKU. Conservative: an unknown SKU (matching neither
        /// list) makes the set NOT consumer, so an unrecognized managed product is never mislabeled.
        /// </summary>
        internal static bool IsConsumerInboxProductSet(IReadOnlyList<string>? products)
        {
            if (products == null || products.Count == 0) return false;
            foreach (var p in products)
            {
                if (string.IsNullOrWhiteSpace(p)) return false;
                if (ManagedMarkers.Any(m => p.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
                if (!ConsumerMarkers.Any(m => p.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)) return false;
            }
            return true;
        }
    }
}
