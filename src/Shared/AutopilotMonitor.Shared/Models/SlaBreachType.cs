namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// String constants identifying SLA breach types. Used as notification subtypes,
    /// logger context, and stable wire identifiers. Storage rows hold all four types
    /// as parallel property prefixes on a single row per tenant.
    /// </summary>
    public static class SlaBreachType
    {
        public const string SuccessRate = "SuccessRate";
        public const string Duration = "Duration";
        public const string AppInstall = "AppInstall";
        public const string ConsecutiveFailures = "ConsecutiveFailures";

        public static readonly string[] All =
        {
            SuccessRate,
            Duration,
            AppInstall,
            ConsecutiveFailures
        };
    }
}
