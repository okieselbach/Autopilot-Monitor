using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using Microsoft.Extensions.DependencyInjection;

namespace AutopilotMonitor.Functions.DataAccess
{
    /// <summary>
    /// Extension methods for registering Data Access Layer services.
    /// Currently registers Azure Table Storage implementations.
    /// To switch to Cosmos DB, replace the implementation registrations here.
    /// </summary>
    public static class DataAccessServiceExtensions
    {
        /// <summary>
        /// Registers all Data Access Layer services with Table Storage implementations.
        /// </summary>
        public static IServiceCollection AddTableStorageDataAccess(this IServiceCollection services)
        {
            // Event publisher (no-op by default; replace with Event Hub/Service Bus implementation)
            services.AddSingleton<IDataEventPublisher, NullDataEventPublisher>();

            // Storage initializer
            services.AddSingleton<IStorageInitializer, TableStorageInitializer>();

            // Repository implementations (delegate to existing TableStorageService)
            services.AddSingleton<ISessionRepository, TableSessionRepository>();
            services.AddSingleton<IRuleRepository, TableRuleRepository>();
            services.AddSingleton<IMetricsRepository, TableMetricsRepository>();
            services.AddSingleton<IMaintenanceRepository, TableMaintenanceRepository>();
            services.AddSingleton<IVulnerabilityRepository, TableVulnerabilityRepository>();
            services.AddSingleton<IAdminRepository, TableAdminRepository>();
            services.AddSingleton<IConfigRepository, TableConfigRepository>();
            services.AddSingleton<IBootstrapRepository, TableBootstrapRepository>();
            services.AddSingleton<INotificationRepository, TableNotificationRepository>();
            services.AddSingleton<IDeviceSecurityRepository, TableDeviceSecurityRepository>();
            services.AddSingleton<IUserUsageRepository, TableUserUsageRepository>();
            services.AddSingleton<IDistressReportRepository, TableDistressReportRepository>();
            services.AddSingleton<IOpsEventRepository, TableOpsEventRepository>();
            return services;
        }

        /// <summary>
        /// Replaces the default NullDataEventPublisher with a custom implementation.
        /// Call this AFTER AddTableStorageDataAccess() to override.
        /// Example: services.AddEventStreaming&lt;EventHubPublisher&gt;();
        /// </summary>
        public static IServiceCollection AddEventStreaming<TPublisher>(this IServiceCollection services)
            where TPublisher : class, IDataEventPublisher
        {
            // Remove existing registration and replace
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IDataEventPublisher));
            if (descriptor != null)
                services.Remove(descriptor);

            services.AddSingleton<IDataEventPublisher, TPublisher>();
            return services;
        }
    }
}
