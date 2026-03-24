using System;
using System.Collections.Generic;
using System.Reflection;

namespace AutopilotMonitor.Functions.Helpers
{
    /// <summary>
    /// Compares two configuration objects and returns a dictionary of changed properties.
    /// Used to produce meaningful audit-log details for configuration updates.
    /// </summary>
    public static class ConfigDiffHelper
    {
        /// <summary>
        /// Properties that are metadata / not user-editable and should be excluded from the diff.
        /// </summary>
        private static readonly HashSet<string> ExcludedProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "PartitionKey", "RowKey", "Timestamp", "ETag",
            "LastUpdated", "UpdatedBy",
            "TenantId", "DomainName", "OnboardedAt"
        };

        /// <summary>
        /// Compares <paramref name="before"/> and <paramref name="after"/> and returns
        /// a dictionary mapping property names to "oldValue → newValue" strings
        /// for every property whose value changed.
        /// </summary>
        public static Dictionary<string, string> GetChanges<T>(T before, T after) where T : class
        {
            var changes = new Dictionary<string, string>();
            if (before == null || after == null) return changes;

            foreach (var prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanRead) continue;
                if (ExcludedProperties.Contains(prop.Name)) continue;

                // Skip methods / indexers
                if (prop.GetIndexParameters().Length > 0) continue;

                var oldVal = prop.GetValue(before);
                var newVal = prop.GetValue(after);

                if (Equals(oldVal, newVal)) continue;

                var oldStr = Sanitize(prop.Name, oldVal);
                var newStr = Sanitize(prop.Name, newVal);
                changes[prop.Name] = $"{oldStr} → {newStr}";
            }

            return changes;
        }

        private static string Sanitize(string propertyName, object? value)
        {
            if (value == null) return "(empty)";

            var str = value.ToString() ?? string.Empty;

            // Mask sensitive-looking values (SAS URLs, webhook URLs, etc.)
            var nameLower = propertyName.ToLowerInvariant();
            if ((nameLower.Contains("sas") || nameLower.Contains("webhook") || nameLower.Contains("url"))
                && !string.IsNullOrEmpty(str))
            {
                return str.Length > 20 ? str.Substring(0, 20) + "***" : "***";
            }

            // Truncate very long values (e.g. JSON blobs)
            if (str.Length > 120)
                return str.Substring(0, 120) + "…";

            return str;
        }
    }
}
