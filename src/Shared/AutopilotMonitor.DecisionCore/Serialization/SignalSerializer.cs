using System;
using System.Collections.Generic;
using AutopilotMonitor.DecisionCore.Signals;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.DecisionCore.Serialization
{
    /// <summary>
    /// Serialize / deserialize <see cref="DecisionSignal"/> JSONL records.
    /// Plan §2.7 / §4 M2 harness integration.
    /// <para>
    /// Uses Newtonsoft.Json with <see cref="DecisionCoreJsonSettings"/>. The reader
    /// tolerates missing optional fields but enforces the evidence-kind mandatory-field
    /// contract (plan §2.2) — a line that would fail <c>new Evidence(...)</c> also fails
    /// here with the same <see cref="ArgumentException"/>.
    /// </para>
    /// </summary>
    public static class SignalSerializer
    {
        public static string Serialize(DecisionSignal signal)
        {
            if (signal == null) throw new ArgumentNullException(nameof(signal));
            return JsonConvert.SerializeObject(signal, DecisionCoreJsonSettings.Create());
        }

        public static DecisionSignal Deserialize(string line)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));

            var settings = DecisionCoreJsonSettings.Create();
            var obj = JsonConvert.DeserializeObject<JObject>(line, settings)
                ?? throw new JsonReaderException($"Failed to parse line as JSON object: {line}");

            // Evidence is required.
            var evidenceTok = obj["Evidence"] ?? throw new JsonSerializationException("Signal is missing Evidence block.");
            var evidenceKind = evidenceTok.Value<string>("Kind") ?? throw new JsonSerializationException("Evidence.Kind missing.");
            var evidenceIdentifier = evidenceTok.Value<string>("Identifier")
                ?? throw new JsonSerializationException("Evidence.Identifier missing.");
            var evidenceSummary = evidenceTok.Value<string>("Summary") ?? string.Empty;
            var rawPointer = evidenceTok.Value<string>("RawPointer");

            IReadOnlyDictionary<string, string>? derivationInputs = null;
            if (evidenceTok["DerivationInputs"] is JObject diObj)
            {
                var d = new Dictionary<string, string>();
                foreach (var prop in diObj.Properties())
                {
                    d[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                }
                derivationInputs = d;
            }

            var evidence = new Evidence(
                kind: ParseEnum<EvidenceKind>(evidenceKind, EvidenceKind.Synthetic),
                identifier: evidenceIdentifier,
                summary: evidenceSummary,
                rawPointer: rawPointer,
                derivationInputs: derivationInputs);

            IReadOnlyDictionary<string, string>? payload = null;
            if (obj["Payload"] is JObject payloadObj)
            {
                var p = new Dictionary<string, string>();
                foreach (var prop in payloadObj.Properties())
                {
                    p[prop.Name] = prop.Value?.ToString() ?? string.Empty;
                }
                payload = p;
            }

            return new DecisionSignal(
                sessionSignalOrdinal: obj.Value<long>("SessionSignalOrdinal"),
                sessionTraceOrdinal: obj.Value<long>("SessionTraceOrdinal"),
                kind: ParseEnum<DecisionSignalKind>(obj.Value<string>("Kind"), DecisionSignalKind.SessionStarted),
                kindSchemaVersion: obj.Value<int?>("KindSchemaVersion") ?? 1,
                occurredAtUtc: obj.Value<DateTime>("OccurredAtUtc"),
                sourceOrigin: obj.Value<string>("SourceOrigin") ?? "unknown",
                evidence: evidence,
                payload: payload);
        }

        private static T ParseEnum<T>(string? raw, T fallback) where T : struct, Enum
        {
            if (string.IsNullOrEmpty(raw)) return fallback;
            return Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(T), parsed)
                ? parsed
                : fallback;
        }
    }
}
