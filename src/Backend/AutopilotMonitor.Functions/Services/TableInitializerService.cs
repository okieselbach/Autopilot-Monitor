using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Hosted service that initializes all Azure Table Storage tables at application startup.
/// This ensures all tables exist before any requests are processed.
/// </summary>
public class TableInitializerService : IHostedService
{
    private readonly TableStorageService _tableStorageService;
    private readonly ILogger<TableInitializerService> _logger;

    public TableInitializerService(
        TableStorageService tableStorageService,
        ILogger<TableInitializerService> logger)
    {
        _tableStorageService = tableStorageService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("TableInitializerService starting - initializing all Azure Table Storage tables");

        try
        {
            await _tableStorageService.InitializeTablesAsync();
            _logger.LogInformation("TableInitializerService completed - all tables ready");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TableInitializerService failed to initialize tables");
            // Don't throw - allow the application to start even if table creation fails
            // Individual operations will fail gracefully with appropriate error messages
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
