namespace AutopilotMonitor.DecisionCore.Signals
{
    /// <summary>
    /// All signal kinds consumed by the Decision Engine.
    /// Plan §2.2. Every kind is versioned via <see cref="DecisionSignal.KindSchemaVersion"/>;
    /// reducer handlers dispatch on (Kind, SchemaVersion). New kinds or version bumps
    /// require a replay fixture in <c>tests/fixtures/signal-kinds/{kind}-v{n}.json</c>
    /// — missing fixture = merge block.
    /// </summary>
    public enum DecisionSignalKind
    {
        // --- Raw — Part 1 ---
        EspPhaseChanged,
        EspExiting,
        EspResumed,
        EspTerminalFailure,
        DesktopArrived,
        HelloResolved,
        ImeUserSessionCompleted,
        DeviceSetupProvisioningComplete,
        AppInstallCompleted,
        AppInstallFailed,
        WhiteGloveShellCoreSuccess,
        WhiteGloveSealingPatternDetected,
        AadUserJoinedLate,
        SystemRebootObserved,
        DeviceInfoCollected,
        AutopilotProfileRead,

        // --- Raw — WhiteGlove Part 2 (Post-Reboot User-Sign-In) ---
        UserAadSignInComplete,
        HelloResolvedPart2,
        DesktopArrivedPart2,
        AccountSetupCompletedPart2,

        // --- Synthetic ---
        DeadlineFired,
        ClassifierVerdictIssued,

        // --- Lifecycle ---
        SessionStarted,
        SessionAborted,
        SessionRecovered,

        // --- Admin-driven preemption (Plan §2.7 admin-action audit, V2 parity PR-B3) ---
        AdminPreemptionDetected,
    }
}
