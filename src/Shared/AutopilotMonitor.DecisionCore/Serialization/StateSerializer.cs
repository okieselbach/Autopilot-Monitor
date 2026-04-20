using System;
using AutopilotMonitor.DecisionCore.State;
using Newtonsoft.Json;

namespace AutopilotMonitor.DecisionCore.Serialization
{
    /// <summary>
    /// Serialize / deserialize <see cref="DecisionState"/> snapshots.
    /// Plan §2.7 (snapshot.json) — fast-start optimization, jederzeit verwerfbar bei
    /// Checksum-Mismatch (§2.7c: Snapshot ist Cache, SignalLog ist Wahrheit).
    /// <para>
    /// Newtonsoft binds JSON properties onto the single public constructor via
    /// case-insensitive parameter matching. Hypothesen-Instanzen, SignalFact&lt;T&gt;-Werte
    /// und ActiveDeadline-Listen werden automatisch rekursiv deserialisiert, weil ihre
    /// ctor-Signaturen zu den Property-Namen passen.
    /// </para>
    /// </summary>
    public static class StateSerializer
    {
        public static string Serialize(DecisionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return JsonConvert.SerializeObject(state, DecisionCoreJsonSettings.Create());
        }

        public static DecisionState Deserialize(string payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            try
            {
                var result = JsonConvert.DeserializeObject<DecisionState>(payload, DecisionCoreJsonSettings.Create());
                if (result == null)
                {
                    throw new JsonSerializationException("State deserialization produced null.");
                }
                return result;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArgumentOutOfRangeException)
            {
                throw new JsonSerializationException(
                    $"DecisionState constructor rejected payload: {ex.Message}", ex);
            }
        }
    }
}
