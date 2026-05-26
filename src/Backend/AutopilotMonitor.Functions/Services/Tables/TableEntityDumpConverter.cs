using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Azure.Data.Tables;
using AutopilotMonitor.Shared.Models.Deletion;

namespace AutopilotMonitor.Functions.Services.Tables
{
    /// <summary>
    /// Forward converter <see cref="TableEntity"/> -> <see cref="DeletionRowDump"/>.
    /// Lifted from <c>DeletionManifestBuilder</c> so both the cascade-delete manifest builder
    /// and the critical-table backup pipeline can share a single, exhaustively tested
    /// EDM-typed row-dump representation.
    /// <para>
    /// The reverse direction (Dump -> TableEntity) lives in
    /// <c>TableStorageService.ConvertDumpToEntity</c> / <c>ConvertFromPropValue</c>.
    /// </para>
    /// </summary>
    public static class TableEntityDumpConverter
    {
        // Azure Tables system properties that must not appear in the per-row Props bag.
        // PartitionKey + RowKey + Timestamp + odata.etag are carried separately on
        // DeletionRowDump or assigned server-side on restore.
        private static readonly HashSet<string> SystemPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "PartitionKey", "RowKey", "Timestamp", "odata.etag",
        };

        /// <summary>
        /// Captures a TableEntity as an EDM-tagged <see cref="DeletionRowDump"/>.
        /// </summary>
        public static DeletionRowDump MapEntityToDump(TableEntity entity)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));

            var props = new Dictionary<string, DeletionPropValue>(entity.Count, StringComparer.Ordinal);
            foreach (var key in entity.Keys)
            {
                if (SystemPropertyNames.Contains(key)) continue;
                props[key] = ConvertToPropValue(entity[key]);
            }
            return new DeletionRowDump
            {
                Pk = entity.PartitionKey ?? string.Empty,
                Rk = entity.RowKey ?? string.Empty,
                Etag = entity.ETag.ToString(),
                Props = props,
            };
        }

        /// <summary>
        /// Converts a single TableEntity property value into an EDM-tagged
        /// <see cref="DeletionPropValue"/>. The restore path reads <see cref="DeletionPropValue.EdmType"/>
        /// to choose the right strongly-typed entity setter; without the tag a DateTime
        /// timestamp coming out of JSON would be re-inserted as a plain string and the
        /// Azure Tables column would silently change EDM type.
        /// </summary>
        public static DeletionPropValue ConvertToPropValue(object? value)
        {
            // null -> null JsonElement, EdmType=String (Azure Tables has no first-class null type).
            if (value == null)
            {
                return new DeletionPropValue
                {
                    EdmType = DeletionPropEdmType.String,
                    Value = ParseJson("null"),
                };
            }

            switch (value)
            {
                case string s:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.String, Value = ParseJson(JsonSerializer.Serialize(s)) };
                case bool b:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.Boolean, Value = ParseJson(b ? "true" : "false") };
                case int i:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.Int32, Value = ParseJson(i.ToString(CultureInfo.InvariantCulture)) };
                case long l:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.Int64, Value = ParseJson(l.ToString(CultureInfo.InvariantCulture)) };
                case double d:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.Double, Value = ParseJson(JsonSerializer.Serialize(d)) };
                case Guid g:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.Guid, Value = ParseJson(JsonSerializer.Serialize(g.ToString("D"))) };
                case byte[] bytes:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.Binary, Value = ParseJson(JsonSerializer.Serialize(Convert.ToBase64String(bytes))) };
                case DateTimeOffset dto:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.DateTime, Value = ParseJson(JsonSerializer.Serialize(dto.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))) };
                case DateTime dt:
                    return new DeletionPropValue { EdmType = DeletionPropEdmType.DateTime, Value = ParseJson(JsonSerializer.Serialize(dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))) };
                default:
                    // Unknown type -> fall back to round-tripping through JSON as a string.
                    return new DeletionPropValue
                    {
                        EdmType = DeletionPropEdmType.String,
                        Value = ParseJson(JsonSerializer.Serialize(value.ToString() ?? string.Empty)),
                    };
            }
        }

        private static JsonElement ParseJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
    }
}
