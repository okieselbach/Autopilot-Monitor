using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Repository for bootstrap session management.
    /// Covers: BootstrapSessions table.
    /// </summary>
    public interface IBootstrapRepository
    {
        Task<bool> CreateBootstrapSessionAsync(BootstrapSession session);
        Task<BootstrapSession?> GetBootstrapSessionByCodeAsync(string shortCode);
        Task<BootstrapSession?> ValidateBootstrapTokenAsync(string token);
        Task<List<BootstrapSession>> GetBootstrapSessionsAsync(string tenantId);
        Task<bool> RevokeBootstrapSessionAsync(string shortCode);
        Task<bool> IncrementBootstrapUsageAsync(string shortCode);
        Task<int> CleanupExpiredAsync();
    }
}
