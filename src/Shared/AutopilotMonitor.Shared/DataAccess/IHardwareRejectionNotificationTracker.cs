using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Tracks which (tenant, manufacturer, model) combinations have already triggered a bell
    /// notification. Used by ReportDistressFunction to fire a tenant-admin notification at most
    /// once per model per tenant (lifetime dedup).
    /// </summary>
    public interface IHardwareRejectionNotificationTracker
    {
        /// <summary>
        /// Atomically records a first-time notification for the given (tenantId, manufacturer, model).
        /// Returns true if this is the first time (caller should fire the notification),
        /// false if an entry already exists (caller should skip).
        /// Manufacturer and model are matched case-insensitively after trim.
        /// </summary>
        Task<bool> TryRegisterFirstNotificationAsync(string tenantId, string manufacturer, string model);
    }
}
