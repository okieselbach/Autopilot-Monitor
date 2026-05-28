// Pure helpers for the Excessive-Session-Events auto-action UI in
// OpsAlertRulesSection. Kept as a `.ts` file so vitest (no JSX transform) can
// import them directly — analog to opsEventSessionHelpers.ts.

export type AutoActionMode = "Off" | "Block" | "Kill";

export const AUTO_ACTION_MODES: AutoActionMode[] = ["Off", "Block", "Kill"];

/**
 * Soft-validation message rendered under the auto-action threshold input.
 * Returns `null` when the configuration is consistent, otherwise the human
 * message to surface inline. We never block submit — the backend tolerates
 * any combination and applies whichever path catches the session first.
 */
export function describeAutoActionWarning(
  mode: AutoActionMode,
  autoThreshold: number,
  warnThreshold: number,
): string | null {
  if (mode === "Off") return null;
  if (autoThreshold <= 0) {
    return "Threshold must be greater than 0 (or set mode to Off).";
  }
  if (warnThreshold > 0 && autoThreshold <= warnThreshold) {
    return `Auto-action threshold should be higher than the warn threshold (${warnThreshold}).`;
  }
  return null;
}
