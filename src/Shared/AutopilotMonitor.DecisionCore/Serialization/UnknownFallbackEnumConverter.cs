using System;
using Newtonsoft.Json;

namespace AutopilotMonitor.DecisionCore.Serialization
{
    /// <summary>
    /// Forward-compatibility converter for enums in DecisionCore DTOs. Plan §2.15 L.14.
    /// <para>
    /// Serializes <typeparamref name="T"/> as its <see cref="Enum.ToString()"/> name.
    /// Deserialization maps unknown / unrecognized string values to a caller-provided
    /// default — typically <c>Unknown</c> — instead of throwing. This lets an older
    /// consumer (e.g. a backend built against an older DecisionCore version) read rows
    /// written by a newer producer (new enum values) without crashing; the consumer
    /// flags those rows as <c>VerificationStatus=drift_tolerated</c> per plan §2.10.
    /// </para>
    /// </summary>
    public sealed class UnknownFallbackEnumConverter<T> : JsonConverter
        where T : struct, Enum
    {
        private readonly T _unknownFallback;

        public UnknownFallbackEnumConverter(T unknownFallback)
        {
            _unknownFallback = unknownFallback;
        }

        public override bool CanConvert(Type objectType)
        {
            var t = Nullable.GetUnderlyingType(objectType) ?? objectType;
            return t == typeof(T);
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value == null)
            {
                writer.WriteNull();
                return;
            }
            writer.WriteValue(value.ToString());
        }

        public override object? ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return Nullable.GetUnderlyingType(objectType) != null ? (object?)null : _unknownFallback;
            }

            if (reader.TokenType == JsonToken.Integer)
            {
                // Legacy / numeric payloads. Try to cast; fallback if out of range.
                var numeric = Convert.ToInt32(reader.Value);
                if (Enum.IsDefined(typeof(T), numeric))
                {
                    return (T)Enum.ToObject(typeof(T), numeric);
                }
                return _unknownFallback;
            }

            if (reader.TokenType == JsonToken.String)
            {
                var raw = reader.Value as string;
                if (string.IsNullOrEmpty(raw))
                {
                    return _unknownFallback;
                }
                if (Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(T), parsed))
                {
                    return parsed;
                }
                return _unknownFallback;
            }

            return _unknownFallback;
        }
    }
}
