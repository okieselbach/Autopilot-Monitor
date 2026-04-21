namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Terminal outcome of a session. Plan §2.3.
    /// Non-null only when <see cref="SessionStage"/> is a terminal stage
    /// (<c>Completed</c>, <c>Failed</c>, or <c>WhiteGloveCompletedPart2</c>).
    /// </summary>
    public enum SessionOutcome
    {
        Unknown = 0,
        EnrollmentComplete,
        EnrollmentFailed,
        WhiteGlovePart1Sealed,
        WhiteGlovePart2Complete,
        Aborted,
    }
}
