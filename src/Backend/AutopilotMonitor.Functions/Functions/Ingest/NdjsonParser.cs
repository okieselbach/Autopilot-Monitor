using System.IO.Compression;
using System.Text;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Ingest
{
    /// <summary>
    /// Pure NDJSON+gzip parsing logic — no service dependencies.
    /// Isolated here so it can be unit-tested without DI infrastructure.
    /// </summary>
    internal static class NdjsonParser
    {
        /// <summary>
        /// Decompresses a gzip stream and parses NDJSON content.
        /// First line: metadata (sessionId, tenantId). Subsequent lines: EnrollmentEvent objects.
        /// </summary>
        internal static async Task<(string sessionId, string tenantId, List<EnrollmentEvent> events)>
            ParseGzipAsync(Stream body, int maxSizeBytes)
        {
            using var decompressed = new MemoryStream();
            using (var gzip = new GZipStream(body, CompressionMode.Decompress, leaveOpen: true))
            {
                var buffer = new byte[8192];
                int bytesRead;
                long totalBytesRead = 0;

                while ((bytesRead = await gzip.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    totalBytesRead += bytesRead;
                    if (totalBytesRead > maxSizeBytes)
                        throw new InvalidOperationException(
                            $"NDJSON payload size exceeds maximum allowed size. " +
                            $"Current size: {totalBytesRead / 1024.0 / 1024.0:F2} MB");
                    await decompressed.WriteAsync(buffer, 0, bytesRead);
                }
            }

            decompressed.Position = 0;
            var ndjson = await new StreamReader(decompressed, Encoding.UTF8).ReadToEndAsync();
            return ParseNdjson(ndjson);
        }

        /// <summary>
        /// Parses a raw NDJSON string (uncompressed).
        /// Exposed as internal so tests can call it directly without building gzip payloads.
        /// </summary>
        internal static (string sessionId, string tenantId, List<EnrollmentEvent> events)
            ParseNdjson(string ndjson)
        {
            var lines = ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            if (lines.Length < 1)
                throw new InvalidOperationException("NDJSON must contain at least a metadata line");

            var metadata = JsonConvert.DeserializeObject<NdjsonMetadata>(lines[0]);
            if (metadata == null)
                throw new InvalidOperationException("Failed to parse NDJSON metadata");

            var events = new List<EnrollmentEvent>();
            for (int i = 1; i < lines.Length; i++)
            {
                try
                {
                    var evt = JsonConvert.DeserializeObject<EnrollmentEvent>(lines[i]);
                    if (evt != null)
                    {
                        NormalizeEventData(evt);
                        events.Add(evt);
                    }
                }
                catch (JsonException)
                {
                    // Skip malformed event lines rather than failing the entire batch.
                    // A single corrupted event should not cause data loss for valid events.
                }
            }

            return (metadata.SessionId, metadata.TenantId, events);
        }

        /// <summary>
        /// Builds a gzip-compressed NDJSON payload for testing.
        /// </summary>
        internal static byte[] BuildGzipPayload(string sessionId, string tenantId, IEnumerable<object> events)
        {
            var sb = new StringBuilder();
            sb.AppendLine(JsonConvert.SerializeObject(new { SessionId = sessionId, TenantId = tenantId }));
            foreach (var evt in events)
                sb.AppendLine(JsonConvert.SerializeObject(evt));

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
            {
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                gzip.Write(bytes, 0, bytes.Length);
            }
            return output.ToArray();
        }

        internal static void NormalizeEventData(EnrollmentEvent evt)
        {
            if (evt.Data == null || evt.Data.Count == 0) return;
            var normalized = new Dictionary<string, object>();
            foreach (var kvp in evt.Data)
                normalized[kvp.Key] = ConvertJTokenToNative(kvp.Value);
            evt.Data = normalized;
        }

        private static object ConvertJTokenToNative(object value)
        {
            if (value is JArray jArray)
                return jArray.Select(item => ConvertJTokenToNative(item)).ToList<object>();
            if (value is JObject jObject)
            {
                var dict = new Dictionary<string, object>();
                foreach (var prop in jObject.Properties())
                    dict[prop.Name] = ConvertJTokenToNative(prop.Value);
                return dict;
            }
            if (value is JValue jValue)
                return jValue.Value ?? string.Empty;
            return value;
        }
    }
}
