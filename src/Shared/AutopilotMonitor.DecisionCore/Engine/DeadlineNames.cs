namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Canonical deadline identifiers. Plan §2.6 pflicht-deadlines.
    /// <para>
    /// Every decision-relevant timer in the engine uses one of these names. The
    /// <see cref="State.ActiveDeadline"/> record is keyed on <see cref="State.ActiveDeadline.Name"/>,
    /// so a re-scheduled deadline with the same name replaces the prior entry (see
    /// <c>DecisionStateBuilder.AddDeadline</c>). The name also appears in the
    /// <c>DeadlineFired</c> signal payload under <see cref="SignalPayloadKeys.Deadline"/>.
    /// </para>
    /// </summary>
    public static class DeadlineNames
    {
        /// <summary>Post-ESP-exit grace period for Hello resolution. Plan §2.7 (300 s).</summary>
        public const string HelloSafety = "hello_safety";

        /// <summary>Brief settle window after ESP exit before we emit completion.</summary>
        public const string EspSettle = "esp_settle";

        /// <summary>Detects "no ESP at all" for self-deploying / device-only paths.</summary>
        public const string DeviceOnlyEspDetection = "device_only_esp_detection";

        /// <summary>Safety net for device-only sessions that never arrive at a terminal signal.</summary>
        public const string DeviceOnlySafety = "device_only_safety";

        /// <summary>Secondary Hello wait window (separate from HelloSafety; plan §2.7).</summary>
        public const string HelloWait = "hello_wait";

        /// <summary>Periodic classifier-tick every 30 s — replaces legacy signal-correlated loop.</summary>
        public const string ClassifierTick = "classifier_tick";

        /// <summary>24 h watchdog after White-Glove Part 1 → reboot → awaiting user sign-in. Plan §2.3.</summary>
        public const string WhiteGlovePart2Safety = "whiteglove_part2_safety";
    }

    /// <summary>
    /// Well-known payload keys on a <see cref="Signals.DecisionSignal"/>.
    /// </summary>
    public static class SignalPayloadKeys
    {
        /// <summary>On <c>DeadlineFired</c>: the deadline name from <see cref="DeadlineNames"/>.</summary>
        public const string Deadline = "deadline";

        /// <summary>On <c>EspPhaseChanged</c>: the raw phase name as observed by the collector.</summary>
        public const string EspPhase = "phase";

        /// <summary>On <c>HelloResolved</c> / <c>HelloResolvedPart2</c>: outcome string (e.g. Success, Timeout, Skipped).</summary>
        public const string HelloOutcome = "outcome";

        /// <summary>On <c>AadUserJoinedLate</c>: user presence indicator ("true" / "false").</summary>
        public const string AadJoinedWithUser = "aadJoinedWithUser";

        /// <summary>On <c>ImeUserSessionCompleted</c>: matched pattern id.</summary>
        public const string ImePatternId = "patternId";

        // --- InformationalEvent payload (plan §1.3, single-rail refactor) ------------
        // Mirrors the EnrollmentEvent fields the reducer must reconstruct for the
        // EmitEventTimelineEntry effect. EventType / Source are mandatory; the rest are
        // optional. Missing Severity defaults to Info, missing ImmediateUpload defaults
        // to false. DataJson, when present, is a JSON object whose properties are merged
        // into the effect parameter dictionary as individual string entries.

        /// <summary>On <c>InformationalEvent</c>: mandatory. Becomes <c>EnrollmentEvent.EventType</c>.</summary>
        public const string EventType = "eventType";

        /// <summary>On <c>InformationalEvent</c>: mandatory. Becomes <c>EnrollmentEvent.Source</c>.</summary>
        public const string Source = "source";

        /// <summary>On <c>InformationalEvent</c>: optional. Becomes <c>EnrollmentEvent.Message</c>.</summary>
        public const string Message = "message";

        /// <summary>On <c>InformationalEvent</c>: optional. Enum-name string (e.g. "Info", "Warning", "Error"); unknown / missing → Info.</summary>
        public const string Severity = "severity";

        /// <summary>On <c>InformationalEvent</c>: optional. "true" / "false"; missing → false.</summary>
        public const string ImmediateUpload = "immediateUpload";

        /// <summary>On <c>InformationalEvent</c>: optional. Pre-serialized JSON object whose top-level keys are copied into the emitted event's Data dictionary.</summary>
        public const string DataJson = "dataJson";
    }
}
