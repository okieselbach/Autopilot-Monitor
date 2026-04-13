using AutopilotMonitor.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Metrics
{
    /// <summary>
    /// Timer trigger that evaluates SLA compliance for all tenants every 2 hours.
    /// Same cadence as the maintenance timer.
    /// </summary>
    public class SlaBreachEvaluationFunction
    {
        private readonly SlaBreachEvaluationService _slaService;
        private readonly ILogger<SlaBreachEvaluationFunction> _logger;

        public SlaBreachEvaluationFunction(
            SlaBreachEvaluationService slaService,
            ILogger<SlaBreachEvaluationFunction> logger)
        {
            _slaService = slaService;
            _logger = logger;
        }

        [Function("SlaBreachEvaluation")]
        public async Task Run([TimerTrigger("0 0 */2 * * *")] object timer)
        {
            _logger.LogInformation("SLA breach evaluation timer fired at {Time}", System.DateTime.UtcNow);
            await _slaService.EvaluateAllTenantsAsync();
        }
    }
}
