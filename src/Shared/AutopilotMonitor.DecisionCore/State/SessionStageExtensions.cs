namespace AutopilotMonitor.DecisionCore.State
{
    /// <summary>
    /// Classification helpers for <see cref="SessionStage"/>. Plan §4.x M4.6.β.
    /// </summary>
    public static class SessionStageExtensions
    {
        /// <summary>
        /// <c>true</c> when the stage represents a session-terminal state — the orchestrator
        /// must stop, peripheral termination handlers (CleanupService, SummaryDialog,
        /// DiagnosticsPackageService) run, and no further signals are expected.
        /// <para>
        /// <see cref="SessionStage.WhiteGloveSealed"/> is terminal for Part 1 only — the agent
        /// exits but the session is paused (Part 2 resumes after reboot). The distinction
        /// matters for CleanupService: Part-1 exit must NOT self-destruct; Part-2 exit
        /// (<see cref="SessionStage.WhiteGloveCompletedPart2"/>) and the classic terminals
        /// (<see cref="SessionStage.Completed"/> / <see cref="SessionStage.Failed"/>) do.
        /// </para>
        /// </summary>
        public static bool IsTerminal(this SessionStage stage)
        {
            switch (stage)
            {
                case SessionStage.Completed:
                case SessionStage.Failed:
                case SessionStage.WhiteGloveSealed:
                case SessionStage.WhiteGloveCompletedPart2:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// <c>true</c> when the stage terminates the current agent run but leaves the session
        /// paused for a future restart (WhiteGlove Part 1 → post-reboot Part 2 resume). Caller
        /// (typically Program.cs) must skip CleanupService.ExecuteSelfDestruct in this case.
        /// </summary>
        public static bool IsPauseBeforePart2(this SessionStage stage)
            => stage == SessionStage.WhiteGloveSealed;
    }
}
