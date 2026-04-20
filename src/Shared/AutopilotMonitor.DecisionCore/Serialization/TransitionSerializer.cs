using System;
using AutopilotMonitor.DecisionCore.Engine;
using Newtonsoft.Json;

namespace AutopilotMonitor.DecisionCore.Serialization
{
    /// <summary>
    /// Serialize / deserialize <see cref="DecisionTransition"/> JSONL records.
    /// Plan §2.7 (journal.jsonl) + §2.8 (DecisionTransitions Azure table row).
    /// <para>
    /// Uses Newtonsoft.Json with <see cref="DecisionCoreJsonSettings"/>. Newtonsoft binds
    /// JSON properties onto the single public constructor via case-insensitive parameter
    /// matching. Any ctor-validation exception surfaces as <see cref="JsonSerializationException"/>
    /// so a corrupt tail is detectable during recovery without crashing the load loop.
    /// </para>
    /// </summary>
    public static class TransitionSerializer
    {
        public static string Serialize(DecisionTransition transition)
        {
            if (transition == null) throw new ArgumentNullException(nameof(transition));
            return JsonConvert.SerializeObject(transition, DecisionCoreJsonSettings.Create());
        }

        public static DecisionTransition Deserialize(string line)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));

            try
            {
                var result = JsonConvert.DeserializeObject<DecisionTransition>(line, DecisionCoreJsonSettings.Create());
                if (result == null)
                {
                    throw new JsonSerializationException($"Transition deserialization produced null for line: {line}");
                }
                return result;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is ArgumentOutOfRangeException)
            {
                // Ctor validation rejects malformed records — wrap so callers see a
                // serialization failure instead of a raw argument exception.
                throw new JsonSerializationException(
                    $"Transition constructor rejected payload: {ex.Message}", ex);
            }
        }
    }
}
