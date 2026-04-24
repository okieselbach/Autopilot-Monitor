namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// ESP (Enrollment Status Page) phase coverage for this enrollment. Codex follow-up #5 —
    /// dimension of <see cref="EnrollmentScenarioProfile"/>. Derived from the
    /// <c>skipUserEsp</c> / <c>skipDeviceEsp</c> payload on the <c>EspConfigDetected</c>
    /// signal. Replaces the legacy <c>SkipUserEsp</c> / <c>SkipDeviceEsp</c>
    /// <see cref="SignalFact{T}"/> facts.
    /// </summary>
    public enum EspConfig
    {
        Unknown = 0,
        /// <summary>Both Device-ESP and Account-ESP run (the default).</summary>
        FullEsp = 1,
        /// <summary>Device-ESP is skipped; Account-ESP still runs (<c>skipDeviceEsp=true</c>).</summary>
        UserEspOnly = 2,
        /// <summary>Account-ESP is skipped; Device-ESP still runs (<c>skipUserEsp=true</c>).</summary>
        DeviceEspOnly = 3,
        /// <summary>Both phases are skipped (<c>skipUserEsp=true</c> AND <c>skipDeviceEsp=true</c>).</summary>
        NoEsp = 4,
    }
}
