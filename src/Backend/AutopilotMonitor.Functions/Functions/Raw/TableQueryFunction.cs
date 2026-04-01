using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using Azure.Data.Tables;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class TableQueryFunction
    {
        private readonly ILogger<TableQueryFunction> _logger;
        private readonly TableStorageService _storage;

        // Tables that must never be exposed (contain secrets)
        private static readonly HashSet<string> _blacklistedTables = new(StringComparer.OrdinalIgnoreCase)
        {
        };

        public TableQueryFunction(ILogger<TableQueryFunction> logger, TableStorageService storage)
        {
            _logger = logger;
            _storage = storage;
        }

        /// <summary>
        /// GET /api/global/raw/tables — List all available table names
        /// </summary>
        [Function("ListRawTables")]
        public async Task<HttpResponseData> ListTables(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/tables")] HttpRequestData req)
        {
            try
            {
                var tables = Constants.TableNames.All
                    .Where(t => !_blacklistedTables.Contains(t))
                    .OrderBy(t => t)
                    .ToList();

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { count = tables.Count, tables });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error listing tables");
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteAsJsonAsync(new { error = "Internal server error" });
                return err;
            }
        }

        /// <summary>
        /// GET /api/global/raw/tables/{tableName} — Query any table directly
        /// Query params: partitionKey, rowKeyPrefix, filter, limit
        /// </summary>
        [Function("QueryRawTable")]
        public async Task<HttpResponseData> QueryTable(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/raw/tables/{tableName}")] HttpRequestData req,
            string tableName)
        {
            try
            {
                // Validate table name
                if (!Constants.TableNames.All.Contains(tableName) &&
                    !Constants.TableNames.All.Any(t => t.Equals(tableName, StringComparison.OrdinalIgnoreCase)))
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { error = $"Table '{tableName}' not found" });
                    return notFound;
                }

                if (_blacklistedTables.Contains(tableName))
                {
                    var forbidden = req.CreateResponse(HttpStatusCode.Forbidden);
                    await forbidden.WriteAsJsonAsync(new { error = $"Table '{tableName}' is not accessible" });
                    return forbidden;
                }

                // Resolve actual table name (case-insensitive match)
                var actualTableName = Constants.TableNames.All
                    .First(t => t.Equals(tableName, StringComparison.OrdinalIgnoreCase));

                var partitionKey = req.Query["partitionKey"];
                var rowKeyPrefix = req.Query["rowKeyPrefix"];
                var filter = req.Query["filter"];
                var limitStr = req.Query["limit"];
                var limit = int.TryParse(limitStr, out var l) ? Math.Clamp(l, 1, 500) : 100;

                // Build OData filter
                var oDataFilter = BuildFilter(partitionKey, rowKeyPrefix, filter);

                var tableClient = _storage.GetTableClient(actualTableName);
                var entities = new List<Dictionary<string, object?>>();

                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: oDataFilter))
                {
                    var dict = new Dictionary<string, object?>
                    {
                        ["PartitionKey"] = entity.PartitionKey,
                        ["RowKey"] = entity.RowKey,
                        ["Timestamp"] = entity.Timestamp,
                    };

                    foreach (var kvp in entity)
                    {
                        if (kvp.Key is "odata.etag" or "PartitionKey" or "RowKey" or "Timestamp")
                            continue;
                        dict[kvp.Key] = kvp.Value;
                    }

                    entities.Add(dict);
                    if (entities.Count >= limit)
                        break;
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    table = actualTableName,
                    count = entities.Count,
                    entities
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying table {TableName}", tableName);
                var err = req.CreateResponse(HttpStatusCode.InternalServerError);
                await err.WriteAsJsonAsync(new { error = "Internal server error" });
                return err;
            }
        }

        private static string? BuildFilter(string? partitionKey, string? rowKeyPrefix, string? customFilter)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(partitionKey))
                parts.Add($"PartitionKey eq '{SanitizeOData(partitionKey)}'");

            if (!string.IsNullOrEmpty(rowKeyPrefix))
            {
                var sanitized = SanitizeOData(rowKeyPrefix);
                parts.Add($"RowKey ge '{sanitized}' and RowKey lt '{sanitized}~'");
            }

            if (!string.IsNullOrEmpty(customFilter))
            {
                // WARNING: This custom filter is passed through with minimal sanitization.
                // This is acceptable because this endpoint requires admin JWT auth,
                // but a proper OData filter allowlist should be considered in a future iteration.
                var sanitizedFilter = customFilter
                    .Replace(";", "")
                    .Replace("--", "");
                parts.Add($"({sanitizedFilter})");
            }

            return parts.Count > 0 ? string.Join(" and ", parts) : null;
        }

        private static string SanitizeOData(string value) => ODataSanitizer.EscapeValue(value);
    }
}
