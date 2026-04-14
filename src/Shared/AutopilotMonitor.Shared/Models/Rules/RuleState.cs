namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Per-tenant override of a rule's default behavior.
    /// Stored in the RuleStates table (PartitionKey=TenantId, RowKey=RuleId).
    /// Absent entries mean "use rule defaults from the definition".
    /// </summary>
    public class RuleState
    {
        /// <summary>
        /// Whether the rule is enabled for this tenant. Overrides the rule's default.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Tenant-scoped override for <see cref="AnalyzeRule.MarkSessionAsFailedDefault"/>.
        /// Null means the tenant has not opted in or out — the rule's default applies.
        /// Only meaningful for analyze rules; ignored for gather rules.
        /// </summary>
        public bool? MarkSessionAsFailed { get; set; }
    }
}
