#nullable enable
using System;
using AutopilotMonitor.DecisionCore.Serialization;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// Serialize / deserialize <see cref="TelemetryItem"/> JSONL records.
    /// Plan §2.7a Spool on disk.
    /// <para>
    /// Die innere <see cref="TelemetryItem.PayloadJson"/> wird als escaped-JSON-String geschrieben
    /// (nicht als geöffnetes Objekt) — das hält den Spool-Eintrag vollständig ctor-bindbar und
    /// transportiert den Payload byte-für-byte unverändert zum Backend.
    /// </para>
    /// </summary>
    public static class TelemetryItemSerializer
    {
        public static string Serialize(TelemetryItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            return JsonConvert.SerializeObject(item, DecisionCoreJsonSettings.Create());
        }

        public static TelemetryItem Deserialize(string line)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));

            try
            {
                var result = JsonConvert.DeserializeObject<TelemetryItem>(line, DecisionCoreJsonSettings.Create());
                if (result == null)
                {
                    throw new JsonSerializationException($"TelemetryItem deserialization produced null: {line}");
                }
                return result;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArgumentOutOfRangeException)
            {
                throw new JsonSerializationException(
                    $"TelemetryItem constructor rejected payload: {ex.Message}", ex);
            }
        }
    }
}
