// Session status helpers. Mirror of the backend SessionStatus enum
// (src/Shared/AutopilotMonitor.Shared/Models/SessionApiModels.cs).
// Non-terminal: InProgress, Pending, Stalled, Unknown.
// Terminal: Succeeded, Failed.

export function isTerminalStatus(status: string | null | undefined): boolean {
  return status === "Succeeded" || status === "Failed";
}
