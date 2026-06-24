using System;
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

        /// <summary>
        /// Hybrid retention cleanup across all tenant partitions: deletes dismissed notifications whose
        /// <c>DismissedAt</c> is older than <paramref name="dismissedCutoffUtc"/> (tail measured from
        /// dismissal) and any notification whose <c>CreatedAt</c> is older than
        /// <paramref name="unreadCutoffUtc"/>. Returns the number of rows deleted.
        /// </summary>
        Task<int> DeleteNotificationsByRetentionAsync(DateTime dismissedCutoffUtc, DateTime unreadCutoffUtc);
    }
}
