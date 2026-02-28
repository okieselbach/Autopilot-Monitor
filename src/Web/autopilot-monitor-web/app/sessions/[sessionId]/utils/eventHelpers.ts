import { EnrollmentEvent } from "../page";

// Pure helper — groups a flat event list into phase buckets.
// Extracted from the useMemo so it can be called multiple times for WhiteGlove split timelines.
export function groupEventsByPhase(
  events: EnrollmentEvent[],
  phaseNamesMap: Record<number, string>,
  phaseOrder: string[]
): { eventsByPhase: Record<string, EnrollmentEvent[]>; orderedPhases: string[] } {
  const sortedEvents = [...events]
    .sort((a, b) => a.sequence - b.sequence)
    .map(e => ({ ...e, phaseName: phaseNamesMap[e.phase] ?? "Unknown" }));

  const eventsByPhase: Record<string, EnrollmentEvent[]> = {};
  let currentActivePhaseName = "Start";

  for (const event of sortedEvents) {
    let targetPhase = event.phaseName || "Unknown";
    if (targetPhase !== "Unknown") {
      currentActivePhaseName = targetPhase;
    } else {
      targetPhase = currentActivePhaseName;
    }
    if (!eventsByPhase[targetPhase]) eventsByPhase[targetPhase] = [];
    eventsByPhase[targetPhase].push(event);
  }

  const orderedPhases = phaseOrder.filter(p => eventsByPhase[p]?.length > 0);
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
