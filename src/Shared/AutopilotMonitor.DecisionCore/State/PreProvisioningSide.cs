namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Identifies the pre-provisioning side when the enrollment is a WhiteGlove flow.
    /// Codex follow-up #5 — dimension of <see cref="EnrollmentScenarioProfile"/>.
    /// <list type="bullet">
    ///   <item><see cref="None"/> — non-WhiteGlove flow or pre-provisioning side not yet known.</item>
    ///   <item><see cref="Technician"/> — Part-1 (pre-reboot, technician at IT bench).</item>
    ///   <item><see cref="User"/> — Part-2 (post-reboot, end-user sign-in at their workplace).</item>
    /// </list>
    /// </summary>
    public enum PreProvisioningSide
    {
        None = 0,
        Technician = 1,
        User = 2,
    }
}
