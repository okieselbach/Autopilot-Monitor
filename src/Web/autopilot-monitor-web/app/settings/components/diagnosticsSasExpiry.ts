/**
 * Parses the expiry date from the `se=` parameter of a SAS URL.
 * Returns null if the URL has no query string, no `se=` parameter, or the value
 * cannot be parsed as a date.
 *
 * Extracted into a plain .ts module so it can be unit-tested by vitest without
 * pulling in the parent .tsx React component. See
 * `lib/__tests__/diagnosticsDestination.test.ts`.
 */
export function parseSasExpiry(sasUrl: string): Date | null {
  try {
    const qIndex = sasUrl.indexOf("?");
    if (qIndex < 0) return null;
    const params = new URLSearchParams(sasUrl.substring(qIndex + 1));
    const se = params.get("se");
    if (!se) return null;
    const d = new Date(se);
    return isNaN(d.getTime()) ? null : d;
  } catch {
    return null;
  }
}
