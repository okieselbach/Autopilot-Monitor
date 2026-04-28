using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for tenant-scoped persistent in-app notifications.
    /// Backing table is partitioned by tenantId so reads/writes are inherently isolated per tenant.
    /// </summary>
    public interface ITenantNotificationRepository
    {
        Task<bool> AddNotificationAsync(string tenantId, GlobalNotification notification);
        Task<List<GlobalNotification>> GetNotificationsAsync(string tenantId, int maxResults = 50);
        Task<bool> DismissNotificationAsync(string tenantId, string notificationId, string dismissedBy);
        Task<int> DismissAllNotificationsAsync(string tenantId);
    }
}
