using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions
{
    /// <summary>
    /// Timer-triggered Azure Function that runs daily maintenance.
    /// Delegates all logic to MaintenanceService.
    /// </summary>
    public class DailyMaintenanceFunction
    {
        private readonly MaintenanceService _maintenanceService;
        private readonly ILogger<DailyMaintenanceFunction> _logger;

        public DailyMaintenanceFunction(
            MaintenanceService maintenanceService,
            ILogger<DailyMaintenanceFunction> logger)
        {
            _maintenanceService = maintenanceService;
            _logger = logger;
        }

        /// <summary>
        /// Timer trigger: Runs daily at 2:00 AM UTC
        /// NCRONTAB format: {second} {minute} {hour} {day} {month} {day-of-week}
        /// </summary>
        [Function("DailyMaintenance")]
        public async Task Run([TimerTrigger("0 0 2 * * *")] object timer)
        {
            _logger.LogInformation("DailyMaintenance timer trigger fired");
            await _maintenanceService.RunAllAsync();
        }
    }
}
