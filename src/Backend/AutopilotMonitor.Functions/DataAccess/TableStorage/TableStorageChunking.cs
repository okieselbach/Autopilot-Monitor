using System;
using System.Collections.Generic;
using System.Text;
using Azure.Data.Tables;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Splits large string values across multiple Azure Table Storage properties
    /// to avoid the 64KB (32K UTF-16 chars) per-property limit.
    /// Backward-compatible: small values use the original property name unchanged.
    /// </summary>
    public static class TableStorageChunking
    {
        private const int DefaultMaxChunkSize = 30_000;

        /// <summary>
        /// Splits a string value into chunked properties if it exceeds maxChunkSize.
        /// Small values: { "Prop": value }
        /// Large values: { "Prop_0": chunk0, "Prop_1": chunk1, "Prop_ChunkCount": "2" }
        /// </summary>
        public static Dictionary<string, string> ChunkProperty(string propertyName, string value, int maxChunkSize = DefaultMaxChunkSize)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxChunkSize)
                return new Dictionary<string, string> { { propertyName, value ?? "" } };

            var result = new Dictionary<string, string>();
            int chunkIndex = 0;
            for (int offset = 0; offset < value.Length; offset += maxChunkSize)
            {
                int length = Math.Min(maxChunkSize, value.Length - offset);
                result[$"{propertyName}_{chunkIndex}"] = value.Substring(offset, length);
                chunkIndex++;
            }
            result[$"{propertyName}_ChunkCount"] = chunkIndex.ToString();
            return result;
        }

        /// <summary>
        /// Reassembles a potentially chunked property from a dictionary entity.
        /// Handles both old (single property) and new (chunked) formats.
        /// </summary>
        public static string? ReassembleProperty(IDictionary<string, object> entity, string propertyName)
        {
            // Backward compat: single non-chunked property
            if (entity.TryGetValue(propertyName, out var single) && single != null)
            {
                var s = single.ToString();
                if (!string.IsNullOrEmpty(s))
                    return s;
            }

            // Chunked format
            if (!entity.TryGetValue($"{propertyName}_ChunkCount", out var countObj))
                return null;

            if (!int.TryParse(countObj?.ToString(), out var count) || count <= 0)
                return null;

            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (entity.TryGetValue($"{propertyName}_{i}", out var chunk) && chunk != null)
                    sb.Append(chunk.ToString());
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>
        /// Reassembles a potentially chunked property from a TableEntity.
        /// Handles both old (single property) and new (chunked) formats.
        /// </summary>
        public static string? ReassembleProperty(TableEntity entity, string propertyName)
        {
            // Backward compat: single non-chunked property
            var single = entity.GetString(propertyName);
            if (!string.IsNullOrEmpty(single))
                return single;

            // Chunked format
            var countStr = entity.GetString($"{propertyName}_ChunkCount");
            if (!int.TryParse(countStr, out var count) || count <= 0)
                return null;

            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var chunk = entity.GetString($"{propertyName}_{i}");
                if (chunk != null)
                    sb.Append(chunk);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
    }
}
