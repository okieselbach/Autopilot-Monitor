const DEFAULT_HARMLESS_EVENT_IDS = "100, 1005, 1010";

/** Convert stored JSON array (e.g. "[100,1005]") to the display/edit form "100, 1005". */
export function parseHarmlessEventIdsJson(json: string | null | undefined): string {
  if (!json) return DEFAULT_HARMLESS_EVENT_IDS;
  try {
    const parsed = JSON.parse(json);
    if (!Array.isArray(parsed)) return DEFAULT_HARMLESS_EVENT_IDS;
    return parsed.filter((x) => Number.isInteger(x)).join(", ");
  } catch {
    return DEFAULT_HARMLESS_EVENT_IDS;
  }
}

/** Convert display form "100, 1005, abc, 7" to canonical JSON "[100,1005,7]" (invalid parts dropped). */
export function serializeHarmlessEventIds(input: string): string {
  const ids = (input || "")
    .split(/[,;\s]+/)
    .map((s) => s.trim())
    .filter((s) => s.length > 0)
    .map((s) => parseInt(s, 10))
    .filter((n) => Number.isInteger(n) && n >= 0);
  return JSON.stringify(ids);
}
