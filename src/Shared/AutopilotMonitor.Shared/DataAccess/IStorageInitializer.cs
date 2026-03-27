using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// Initializes the storage backend (creates tables, containers, collections, etc.).
    /// Each storage provider implements this for its specific setup needs.
    /// </summary>
    public interface IStorageInitializer
    {
        Task InitializeAsync();
    }
}
