/**
 * Pure logic for the ScriptExecutions panel: turning a stream of script_started /
 * script_completed / script_failed events into the deduped, ordered list of items
 * the UI renders. Extracted from the React component so the 2-pass reducer + label
 * mapping can be unit-tested without React Testing Library.
 */
import { extractBootstrapVersion } from "@/utils/bootstrapVersion";

export interface ScriptInputEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, any>;
}

export interface ScriptItem {
  policyId: string;
  scriptType: string;        // "platform" | "remediation"
  scriptPart?: string;        // "detection" | "remediation" | "post-detection"
  runContext?: string;
  exitCode?: number;
  result?: string;
  complianceResult?: string;
  remediationStatus?: number;
  targetType?: number;
  errorCode?: number;
  errorDetails?: string;
  stdout?: string;
  stderr?: string;
  state: "Running" | "Success" | "Failed";
  timestamp: string;
  bootstrapVersion?: string | null;
}

/**
 * Stable React key for a ScriptItem. Based ONLY on identity-shaped fields (policyId,
 * scriptType, scriptPart) so re-renders triggered by upstream prop changes (live event
 * polling, SignalR) keep the same component instance mounted — preserving showDetails
 * and other local state. Do NOT include timestamp or insertion index here, otherwise
 * any reducer re-run will remount and collapse expanded detail panels.
 */
export function scriptItemKey(item: Pick<ScriptItem, "policyId" | "scriptType" | "scriptPart" | "state">): string {
  const part = item.state === "Running" ? "_running" : (item.scriptPart ?? "_nopart");
  return `${item.policyId || "_noid"}-${item.scriptType}-${part}`;
}

/** Threshold above which a Running placeholder is rendered as "stuck?" rather than animated. */
export const STALE_RUNNING_THRESHOLD_SECONDS = 600;

/**
 * Score how complete a script item's data is (higher = better). Used by the reducer to
 * pick the best entry when re-emissions of the same script collapse into one row.
 * Counts the presence of fields that meaningfully describe the script's outcome.
 */
function dataCompleteness(item: ScriptItem): number {
  let score = 0;
  if (item.exitCode != null) score += 4; // exit code is the most important signal
  if (item.result) score += 2;
  if (item.complianceResult) score += 2;
  if (item.remediationStatus != null) score += 1;
  if (item.stdout && item.stdout.length > 0) score += 1;
  if (item.stderr && item.stderr.length > 0) score += 1;
  if (item.runContext) score += 1;
  return score;
}

/**
 * Coerce a wire value to a number. Backend serializes integer event-data fields as strings
 * (`Dictionary<string, string>` payload format), but newer in-process emitters could pass
 * raw numbers. Accept both shapes so the reducer doesn't quietly drop fields based on the
 * wire encoding. Returns undefined for null / undefined / non-numeric strings.
 */
export function toNumber(v: unknown): number | undefined {
  if (typeof v === "number" && Number.isFinite(v)) return v;
  if (typeof v === "string" && v.length > 0) {
    const n = Number(v);
    return Number.isFinite(n) ? n : undefined;
  }
  return undefined;
}

/** Map RemediationStatus enum to the human-readable label shown in the detail panel. */
export function mapRemediationStatus(status?: number): string | null {
  switch (status) {
    case 0: return "Unknown";
    case 1: return "Compliant";
    case 2: return "Remediated";
    case 3: return "RemediationFailed";
    case 4: return "NoRemediation";
    default: return null;
  }
}

/**
 * Pick the headline label for a row. We use Intune Admin Center terminology — proactive
 * remediation policies are labelled "Remediation" in the Intune UI, regardless of which
 * phase (detection / remediation / post-detection) of the cycle this row represents.
 * The phase is conveyed via the badge next to the title, not the title itself.
 */
export function buildScriptItemLabel(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "state" | "remediationStatus">): string {
  if (item.scriptType === "remediation") {
    return item.state === "Running" ? "Remediation (running)" : "Remediation";
  }
  return item.state === "Running" ? "Platform Script (running)" : "Platform Script";
}

/**
 * Phase badge text for remediation rows (e.g. "detection", "remediation", "post-detection").
 * Returns null when no phase badge should render (platform scripts, Running placeholders).
 */
export function getPhaseBadge(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "state">): string | null {
  if (item.scriptType !== "remediation") return null;
  if (item.state === "Running") return null;
  if (!item.scriptPart) return null;
  return item.scriptPart;
}

/** True when this row should display the "detect-only" badge. */
export function isDetectOnlyRow(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "remediationStatus" | "state">): boolean {
  return item.state !== "Running"
    && item.scriptType === "remediation"
    && item.scriptPart === "detection"
    && item.remediationStatus === 4;
}

/**
 * True when this row represents a non-compliant health-script reading (detection or
 * post-detection that returned exit != 0). The script ran successfully — state stays
 * "Success" — but the compliance verdict is False, so the UI should style it amber to
 * draw attention without crying "failure".
 */
export function isNonCompliantReport(item: Pick<ScriptItem, "scriptType" | "scriptPart" | "complianceResult" | "state">): boolean {
  return item.state === "Success"
    && item.scriptType === "remediation"
    && (item.scriptPart === "detection" || item.scriptPart === "post-detection")
    && item.complianceResult === "False";
}

/**
 * 2-pass reducer:
 *   1. Sort events by timestamp; collect every script_completed / script_failed final.
 *      Dedupe by (policyId, scriptType, scriptPart) so re-fetched events don't double-render.
 *   2. For every script_started whose policyId hasn't been finalized yet, append a "Running"
 *      placeholder. When a final lands later (SignalR or re-fetch) the placeholder vanishes
 *      naturally on the next render because the second pass re-runs.
 *
 * Returns rows in insertion order: finals first (sorted by timestamp), then any live
 * placeholders. The component sees a stable, deduped list with optimistic live indicators.
 */
export function reduceScriptEvents(events: ScriptInputEvent[]): ScriptItem[] {
  if (events.length === 0) return [];

  const sorted = [...events].sort(
    (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
  );

  // Map of dedupe-key → ScriptItem. Same (policyId, scriptType, scriptPart) re-emissions
  // collapse into one row keeping the entry with the most-complete data. This is the
  // common case in IME logs where the same script appears multiple times across ESP-phase
  // transitions, sometimes with degraded data on the second emission (lost exit code etc).
  // For Autopilot enrollment sessions (time-bounded, < 1 h typical) this is the right
  // trade-off — long-running scheduled-script monitoring is out of scope for this UI.
  const finalsByKey = new Map<string, ScriptItem>();
  const policyIdsWithFinal = new Set<string>();

  for (let idx = 0; idx < sorted.length; idx++) {
    const evt = sorted[idx];
    if (evt.eventType !== "script_completed" && evt.eventType !== "script_failed") continue;
    const d = evt.data;
    if (!d) continue;

    const policyId = d.policyId ?? d.policy_id ?? "";
    const scriptType = d.scriptType ?? d.script_type ?? "platform";
    const scriptPart = d.scriptPart ?? d.script_part;
    const dedupeId = policyId || `_noid_${idx}`;
    const key = `${dedupeId}-${scriptType}-${scriptPart ?? ""}`;
    if (policyId) policyIdsWithFinal.add(`${policyId}-${scriptType}`);

    const exitCode = toNumber(d.exitCode ?? d.exit_code);
    const remediationStatus = toNumber(d.remediationStatus ?? d.remediation_status);
    const targetType = toNumber(d.targetType ?? d.target_type);
    const errorCode = toNumber(d.errorCode ?? d.error_code);
    const stdout = typeof d.stdout === "string" ? d.stdout : undefined;
    const stderr = typeof d.stderr === "string" ? d.stderr : undefined;
    const hasStderr = !!stderr && stderr.trim().length > 0;

    // State derivation — three rules in priority order:
    //   1. stderr present → Failed. Consistent across script types: any time a script
    //      writes to stderr the user wants visibility, even if exit was 0. Per user
    //      preference (debrief 2026-05-11): "exit 0 bedeutet zwar success aber wenn auf
    //      stderr was gezeigt wird sollte es failed sein".
    //   2. Phase-aware exit handling: detection / post-detection use exit code as a
    //      compliance verdict (not a crash signal), so non-zero exit alone is NOT
    //      failure for those phases. Only remediation phase + platform scripts route
    //      non-zero exit to Failed.
    //   3. Defensive: explicit script_failed eventType OR result === "Failed" → Failed.
    const isHealthComplianceReport = scriptType === "remediation"
      && (scriptPart === "detection" || scriptPart === "post-detection");
    const isFailureSignal = evt.eventType === "script_failed"
      || hasStderr
      || (!isHealthComplianceReport && exitCode != null && exitCode !== 0)
      || d.result === "Failed";

    const candidate: ScriptItem = {
      policyId,
      scriptType,
      scriptPart,
      runContext: d.runContext ?? d.run_context,
      exitCode,
      result: d.result,
      complianceResult: d.complianceResult ?? d.compliance_result,
      remediationStatus,
      targetType,
      errorCode,
      errorDetails: d.errorDetails ?? d.error_details,
      stdout,
      stderr,
      state: isFailureSignal ? "Failed" : "Success",
      timestamp: evt.timestamp,
      bootstrapVersion: scriptType === "platform" ? extractBootstrapVersion(stdout) : null,
    };

    const existing = finalsByKey.get(key);
    if (!existing || dataCompleteness(candidate) > dataCompleteness(existing)) {
      finalsByKey.set(key, candidate);
    }
  }

  // Assemble the ordered list of finals. Sort by the timestamp of the kept entry so
  // the timeline reflects actual chronology of the surviving events.
  const items: ScriptItem[] = Array.from(finalsByKey.values()).sort(
    (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
  );

  // Suppression set for Running placeholders: track which (policyId, scriptType) pairs
  // already have a final, so a started signal that arrives after its own completion
  // doesn't surface a stale Running indicator.
  const finalTimestampsByPolicy = new Map<string, number[]>();
  for (const item of items) {
    if (!item.policyId) continue;
    const policyKey = `${item.policyId}-${item.scriptType}`;
    const ts = new Date(item.timestamp).getTime();
    const arr = finalTimestampsByPolicy.get(policyKey);
    if (arr) arr.push(ts);
    else finalTimestampsByPolicy.set(policyKey, [ts]);
  }

  // Running placeholders: emit one per (policyId, scriptType) when a started signal
  // exists without any final at-or-after its timestamp. Collapsed by policyId+type so
  // a single row shows "running" rather than one per started signal — matches the
  // collapsed-final dedupe semantics above.
  const runningEmitted = new Set<string>();
  for (const evt of sorted) {
    if (evt.eventType !== "script_started") continue;
    const d = evt.data;
    if (!d) continue;

    const policyId = d.policyId ?? d.policy_id ?? "";
    const scriptType = d.scriptType ?? d.script_type ?? "platform";
    if (!policyId) continue;

    const policyKey = `${policyId}-${scriptType}`;
    if (runningEmitted.has(policyKey)) continue;

    const startedTs = new Date(evt.timestamp).getTime();
    const finals = finalTimestampsByPolicy.get(policyKey);
    if (finals && finals.some(ts => ts >= startedTs)) continue;

    runningEmitted.add(policyKey);
    items.push({
      policyId,
      scriptType,
      state: "Running",
      timestamp: evt.timestamp,
      bootstrapVersion: null,
    });
  }

  return items;
}
