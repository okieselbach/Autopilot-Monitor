namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// High-level Autopilot enrollment mode inferred from registry + signals.
    /// Codex follow-up #5 — dimension of <see cref="EnrollmentScenarioProfile"/>.
    /// <para>
    /// <see cref="Classic"/>, <see cref="SelfDeploying"/> and <see cref="WhiteGlove"/> are
    /// the three v1 sub-modes the reducer distinguishes through later signals (the
    /// <c>SessionStarted</c> registry probe only separates v1/v2). <see cref="DevicePreparation"/>
    /// is Windows Autopilot Device Preparation (v2 / WDP) — no ESP, no pre-reboot user stage.
    /// </para>
    /// </summary>
    public enum EnrollmentMode
    {
        Unknown = 0,
        Classic = 1,
        DevicePreparation = 2,
        SelfDeploying = 3,
        WhiteGlove = 4,
    }
}
