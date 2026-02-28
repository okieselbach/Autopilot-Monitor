using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Service for managing Azure Table Storage operations.
    /// Split into partial class files by domain:
    ///   - TableStorageService.cs          (this file: core, initialization, helpers)
    ///   - TableStorageService.Sessions.cs (sessions, events, mapping)
    ///   - TableStorageService.Rules.cs    (gather/analyze rules, rule states, IME patterns)
    ///   - TableStorageService.Metrics.cs  (usage metrics, platform stats, user activity, app installs)
    ///   - TableStorageService.Maintenance.cs (audit logs, data retention, deletion helpers)
    /// </summary>
    public partial class TableStorageService
    {
        private readonly TableServiceClient _tableServiceClient;
        private readonly ILogger<TableStorageService> _logger;
        private bool _tablesInitialized = false;
        private readonly object _initLock = new object();

        public TableStorageService(IConfiguration configuration, ILogger<TableStorageService> logger)
        {
            _logger = logger;
            var connectionString = configuration["AzureTableStorageConnectionString"];
            _tableServiceClient = new TableServiceClient(connectionString);
        }

        /// <summary>
        /// Initializes all Azure Table Storage tables.
        /// This method is idempotent and safe to call multiple times.
        /// Should be called at application startup.
        /// </summary>
        public async Task InitializeTablesAsync()
        {
            if (_tablesInitialized)
            {
                _logger.LogDebug("Tables already initialized, skipping");
                return;
            }

            lock (_initLock)
            {
                if (_tablesInitialized) return;
            }

            _logger.LogInformation("Initializing Azure Table Storage tables...");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var successCount = 0;
            var failCount = 0;

            foreach (var tableName in Constants.TableNames.All)
            {
                try
                {
                    await _tableServiceClient.CreateTableIfNotExistsAsync(tableName);
                    _logger.LogDebug($"Table '{tableName}' initialized");
                    successCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to initialize table '{tableName}'");
                    failCount++;
                }
            }

            stopwatch.Stop();
            _logger.LogInformation($"Table initialization completed in {stopwatch.ElapsedMilliseconds}ms: {successCount} succeeded, {failCount} failed");

            lock (_initLock)
            {
                _tablesInitialized = failCount == 0;
            }
        }

        /// <summary>
        /// Gets the TableServiceClient for direct access (used by other services)
        /// </summary>
        public TableServiceClient GetTableServiceClient() => _tableServiceClient;

        // ===== HELPER METHODS =====

        /// <summary>
        /// Safely reads an Int32 property from a TableEntity.
        /// Returns null instead of throwing when the property has a different type (legacy data).
        /// </summary>
        private int? SafeGetInt32(TableEntity entity, string key)
        {
            try
            {
                return entity.GetInt32(key);
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("Property '{Key}' on entity {PK}/{RK} is not Int32, attempting string parse", key, entity.PartitionKey, entity.RowKey);
                var str = entity.GetString(key);
                if (str != null && int.TryParse(str, out var parsed))
                    return parsed;
                return null;
            }
        }

        /// <summary>
        /// Safely reads a DateTime property from a TableEntity.
        /// Returns null instead of throwing when the property has a different type (legacy data).
        /// </summary>
        private DateTime? SafeGetDateTime(TableEntity entity, string key)
        {
            try
            {
                return entity.GetDateTime(key);
            }
            catch (InvalidOperationException)
            {
                _logger.LogWarning("Property '{Key}' on entity {PK}/{RK} is not DateTime, attempting string parse", key, entity.PartitionKey, entity.RowKey);
                var str = entity.GetString(key);
                if (str != null && DateTime.TryParse(str, out var parsed))
                    return parsed;
                return null;
            }
        }

        private T DeserializeJson<T>(string? json) where T : new()
        {
            if (string.IsNullOrEmpty(json))
                return new T();

            try
            {
                return JsonConvert.DeserializeObject<T>(json) ?? new T();
            }
            catch
            {
                return new T();
            }
        }

        /// <summary>
        /// Deserializes MatchedConditions JSON and normalizes nested JObject/JArray values
        /// to plain Dictionary/List so System.Text.Json can serialize them correctly.
        /// </summary>
        private Dictionary<string, object> DeserializeMatchedConditions(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return new Dictionary<string, object>();

            try
            {
                var raw = JsonConvert.DeserializeObject<Dictionary<string, object>>(json)
                          ?? new Dictionary<string, object>();

                var result = new Dictionary<string, object>();
                foreach (var kv in raw)
                    result[kv.Key] = NormalizeJToken(kv.Value);
                return result;
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        private static object NormalizeJToken(object? value)
        {
            if (value is Newtonsoft.Json.Linq.JObject jObj)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObj.Properties())
                    dict[prop.Name] = NormalizeJToken(prop.Value);
                return dict;
            }
            if (value is Newtonsoft.Json.Linq.JArray jArr)
            {
                var list = new List<object>();
                foreach (var item in jArr)
                    list.Add(NormalizeJToken(item));
                return list;
            }
            if (value is Newtonsoft.Json.Linq.JValue jVal)
                return jVal.Value ?? string.Empty;
            return value ?? string.Empty;
        }

        private string[] DeserializeJsonArray(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return Array.Empty<string>();

            try
            {
                return JsonConvert.DeserializeObject<string[]>(json) ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Deserializes event data JSON and converts JToken objects to native .NET types
        /// </summary>
        private Dictionary<string, object> DeserializeEventData(string? dataJson)
        {
            if (string.IsNullOrEmpty(dataJson))
                return new Dictionary<string, object>();

            try
            {
                var deserialized = JsonConvert.DeserializeObject<Dictionary<string, object>>(dataJson);
                if (deserialized == null)
                    return new Dictionary<string, object>();

                // Convert all JToken values to native types
                var result = new Dictionary<string, object>();
                foreach (var kvp in deserialized)
                {
                    result[kvp.Key] = ConvertJTokenToNative(kvp.Value);
                }
                return result;
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Converts JToken objects (JArray, JObject) to native .NET types
        /// This fixes the issue where Newtonsoft.Json deserialization creates JToken objects
        /// that get serialized incorrectly as nested empty arrays
        /// </summary>
        private object ConvertJTokenToNative(object value)
        {
            if (value is JArray jArray)
            {
                return jArray.Select(item => ConvertJTokenToNative(item)).ToList();
            }
            else if (value is JObject jObject)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObject.Properties())
                {
                    dict[prop.Name] = ConvertJTokenToNative(prop.Value);
                }
                return dict;
            }
            else if (value is JValue jValue)
            {
                return jValue.Value ?? string.Empty;
            }
            return value;
        }
    }

    public class AuditLogEntry
    {
        public string Id { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;
        public string PerformedBy { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Details { get; set; } = string.Empty;
    }

    public class UserActivityMetrics
    {
        public int TotalUniqueUsers { get; set; }
        public int DailyLogins { get; set; }
        public int ActiveUsersLast7Days { get; set; }
        public int ActiveUsersLast30Days { get; set; }
    }
}
