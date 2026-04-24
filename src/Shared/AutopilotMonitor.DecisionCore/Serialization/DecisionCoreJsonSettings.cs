using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.DecisionCore.Serialization
{
    /// <summary>
    /// Centralized <see cref="JsonSerializerSettings"/> factory for DecisionCore DTOs.
    /// Plan §2.8 / §2.15 L.14.
    /// <para>
    /// Produces a <see cref="JsonSerializerSettings"/> instance with:
    /// <list type="bullet">
    ///   <item>All DecisionCore enums registered via <see cref="UnknownFallbackEnumConverter{T}"/>
    ///         so unknown values land on <c>Unknown</c> instead of throwing.</item>
    ///   <item><see cref="TypeNameHandling.None"/> — no polymorphism loopholes over the wire.</item>
    ///   <item><see cref="NullValueHandling.Include"/> — preserve nullable fact/hypothesis fields.</item>
    ///   <item>UTC round-trip for <c>DateTime</c> — <see cref="DateTimeZoneHandling.Utc"/>,
    ///         ISO-8601 format.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class DecisionCoreJsonSettings
    {
        /// <summary>
        /// Create a fresh settings instance. Not a static singleton — Newtonsoft mutates
        /// internal state during serialization (binder caches, etc.), so handing out
        /// shared instances across threads is a well-known footgun.
        /// </summary>
        public static JsonSerializerSettings Create()
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.None,
                NullValueHandling = NullValueHandling.Include,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                DateParseHandling = DateParseHandling.DateTime,
                Formatting = Formatting.None,
                MissingMemberHandling = MissingMemberHandling.Ignore,
            };

            // Enum converters with Unknown-fallback — plan §2.15 L.14.
            settings.Converters.Add(new UnknownFallbackEnumConverter<DecisionSignalKind>(DecisionSignalKind.SessionStarted));
            // ^ SessionStarted is a neutral fallback; unknown signal kinds should be rare (SchemaVersion gate),
            //   and when they do occur we prefer a benign no-op over a crash.
            settings.Converters.Add(new UnknownFallbackEnumConverter<EvidenceKind>(EvidenceKind.Synthetic));
            settings.Converters.Add(new UnknownFallbackEnumConverter<HypothesisLevel>(HypothesisLevel.Unknown));
            settings.Converters.Add(new UnknownFallbackEnumConverter<SessionStage>(SessionStage.Unknown));
            settings.Converters.Add(new UnknownFallbackEnumConverter<SessionOutcome>(SessionOutcome.Unknown));
            settings.Converters.Add(new UnknownFallbackEnumConverter<DecisionEffectKind>(DecisionEffectKind.PersistSnapshot));
            settings.Converters.Add(new UnknownFallbackEnumConverter<EnrollmentPhase>(EnrollmentPhase.Unknown));

            // Codex follow-up #5 (post-#51) — EnrollmentScenarioProfile dimensions. Without
            // these registrations the new enums would round-trip as numeric JSON with no
            // unknown-value fallback, breaking drift-tolerated cross-version reads.
            settings.Converters.Add(new UnknownFallbackEnumConverter<EnrollmentMode>(EnrollmentMode.Unknown));
            settings.Converters.Add(new UnknownFallbackEnumConverter<EnrollmentJoinMode>(EnrollmentJoinMode.Unknown));
            settings.Converters.Add(new UnknownFallbackEnumConverter<EspConfig>(EspConfig.Unknown));
            settings.Converters.Add(new UnknownFallbackEnumConverter<PreProvisioningSide>(PreProvisioningSide.None));
            settings.Converters.Add(new UnknownFallbackEnumConverter<ProfileConfidence>(ProfileConfidence.Low));

            return settings;
        }
    }
}
