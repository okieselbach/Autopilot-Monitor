import { EnrollmentEvent } from "../page";

// Pure helper — groups a flat event list into phase buckets.
// Extracted from the useMemo so it can be called multiple times for WhiteGlove split timelines.
//
// preventPhaseRegression: when true, once the phase advances past a certain point it cannot
// regress to an earlier phase. Used for WhiteGlove Part 2 (User Enrollment) to absorb
// mid-enrollment reboots that emit a new agent_started (Phase=Start) without disrupting the
// timeline flow. The reboot events stay in whatever phase was active before the reboot.
export function groupEventsByPhase(
  events: EnrollmentEvent[],
  phaseNamesMap: Record<number, string>,
  phaseOrder: string[],
  options?: { preventPhaseRegression?: boolean }
): { eventsByPhase: Record<string, EnrollmentEvent[]>; orderedPhases: string[] } {
  const sortedEvents = [...events]
    .sort((a, b) => {
      const seqDiff = a.sequence - b.sequence;
      if (seqDiff !== 0) return seqDiff;
      // Fallback: timestamp breaks ties when sequence counter was not persisted before reboot
      return (a.timestamp ?? "").localeCompare(b.timestamp ?? "");
    })
    .map(e => ({ ...e, phaseName: phaseNamesMap[e.phase] ?? "Unknown" }));

  const preventRegression = options?.preventPhaseRegression === true;
  const eventsByPhase: Record<string, EnrollmentEvent[]> = {};
  let currentActivePhaseName = "Start";
  let maxPhaseIndex = 0;

  for (const event of sortedEvents) {
    let targetPhase = event.phaseName || "Unknown";
    if (targetPhase !== "Unknown") {
      if (preventRegression) {
        const candidateIndex = phaseOrder.indexOf(targetPhase);
        if (candidateIndex >= 0 && candidateIndex >= maxPhaseIndex) {
          currentActivePhaseName = targetPhase;
          maxPhaseIndex = candidateIndex;
        } else {
          // Phase would regress (e.g. reboot agent_started) — keep current phase
          targetPhase = currentActivePhaseName;
        }
      } else {
        currentActivePhaseName = targetPhase;
      }
    } else {
      targetPhase = currentActivePhaseName;
    }
    if (!eventsByPhase[targetPhase]) eventsByPhase[targetPhase] = [];
    eventsByPhase[targetPhase].push(event);
  }

  // Order phase sections chronologically by first event sequence (not hardcoded).
  // This ensures the display always matches the actual event sequence — critical when
  // SkipUserStatusPage=true reorders phases (FinalizingSetup before AppsUser).
  const orderedPhases = Object.keys(eventsByPhase)
    .filter(p => eventsByPhase[p]?.length > 0)
    .sort((a, b) => {
      const aFirst = eventsByPhase[a][0]?.sequence ?? 0;
      const bFirst = eventsByPhase[b][0]?.sequence ?? 0;
      return aFirst - bFirst;
    });
  return { eventsByPhase, orderedPhases };
}

export function normalizeJsonLikeValue(value: any): any {
  if (typeof value === "string") {
    const trimmed = value.trim();
    const looksLikeJson =
      (trimmed.startsWith("{") && trimmed.endsWith("}")) ||
      (trimmed.startsWith("[") && trimmed.endsWith("]"));

    if (!looksLikeJson) return value;

    try {
      return normalizeJsonLikeValue(JSON.parse(value));
    } catch {
      return value;
    }
  }

  if (Array.isArray(value)) {
    return value.map(normalizeJsonLikeValue);
  }

  if (value && typeof value === "object") {
    const normalized: Record<string, any> = {};
    for (const [k, v] of Object.entries(value)) {
      normalized[k] = normalizeJsonLikeValue(v);
    }
    return normalized;
  }

  return value;
}

export function normalizeEventDataForDisplay(data?: Record<string, any>): Record<string, any> | null {
  if (!data) return null;
  return normalizeJsonLikeValue(data);
}
