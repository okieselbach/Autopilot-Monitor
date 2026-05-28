// Helpers for the Ops Events detail modal's "Block this device" / "Kill this
// device" deep-link buttons. Lives in a `.ts` file so vitest (which currently
// only loads `.test.ts` and doesn't transform JSX) can import and test them.

const SESSION_ID_KEYS = ["sessionId", "SessionId", "sessionID", "session_id"];

export function extractSessionId(detailsJson: string | null): string | null {
  if (!detailsJson) return null;
  try {
    const parsed = JSON.parse(detailsJson);
    if (!parsed || typeof parsed !== "object") return null;
    for (const key of SESSION_ID_KEYS) {
      const value = (parsed as Record<string, unknown>)[key];
      if (typeof value === "string" && value.trim().length > 0) return value.trim();
    }
    return null;
  } catch {
    return null;
  }
}

export function buildAutoReason(eventType: string, sessionId: string): string {
  // Always cap to 8 chars — for GUIDs this is exactly the first hex group, for
  // anything else it stays readable in audit-log columns.
  const shortId = (sessionId.split("-")[0] ?? sessionId).slice(0, 8);
  if (eventType === "ExcessiveSessionEvents") {
    return `Excessive session events on session ${shortId}`;
  }
  return `From ops alert: ${eventType} (session ${shortId})`;
}
