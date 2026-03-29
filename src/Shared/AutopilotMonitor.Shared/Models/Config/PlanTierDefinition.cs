namespace AutopilotMonitor.Shared.Models.Config
{
    /// <summary>
    /// Defines a usage plan tier with request limits.
    /// Stored as JSON array in AdminConfiguration.PlanTierDefinitionsJson.
    /// </summary>
    public class PlanTierDefinition
    {
        public string Name { get; set; } = string.Empty;
        public int DailyRequestLimit { get; set; } = 100;
        public int MonthlyRequestLimit { get; set; } = 3000;
        public string Description { get; set; } = string.Empty;
    }
}
