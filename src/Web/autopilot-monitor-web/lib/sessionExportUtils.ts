/**
 * Shared session export utilities.
 * Used by both the admin-config SessionExportSection and the session report modal.
 */

export interface SessionExportEvent {
  eventId: string;
  sessionId: string;
  tenantId: string;
  timestamp: string;
  eventType: string;
  severity: string;
  source: string;
  phase: number;
  phaseName?: string;
  message: string;
  sequence: number;
  rowKey?: string;
  data?: Record<string, unknown>;
}

export const EXPORT_V1_PHASE_NAMES: Record<number, string> = {
  0: "Start", 1: "Device Preparation", 2: "Device Setup",
  3: "Apps (Device)", 4: "Account Setup", 5: "Apps (User)",
  6: "Finalizing Setup", 7: "Complete", 99: "Failed"
};
export const EXPORT_V2_PHASE_NAMES: Record<number, string> = {
  0: "Start", 1: "Device Preparation", 2: "Device Setup",
  3: "App Installation", 4: "Account Setup", 5: "Apps (User)",
  6: "Finalizing Setup", 7: "Complete", 99: "Failed"
};
export const EXPORT_V1_PHASE_ORDER = ["Start", "Device Preparation", "Device Setup",
  "Apps (Device)", "Account Setup", "Apps (User)", "Finalizing Setup", "Complete", "Failed"];
export const EXPORT_V2_PHASE_ORDER = ["Start", "Device Preparation", "App Installation",
  "Finalizing Setup", "Complete", "Failed"];

export function generateCsvExport(events: SessionExportEvent[]) {
  const isV1 = events.some(e => e.phase === 2);
  const phaseNames = isV1 ? EXPORT_V1_PHASE_NAMES : EXPORT_V2_PHASE_NAMES;
  const esc = (v: string) => `"${v.replace(/"/g, '""')}"`;
  // Sort exactly as Azure Table Storage: by timestamp ascending, then sequence ascending
  const sorted = [...events].sort((a, b) => {
    const tCmp = (a.timestamp ?? "").localeCompare(b.timestamp ?? "");
    if (tCmp !== 0) return tCmp;
    return (a.sequence ?? 0) - (b.sequence ?? 0);
  });
  const header = "RowKey,EventId,SessionId,TenantId,Timestamp,EventType,Severity,Source,Phase,PhaseName,Message,Sequence,Data";
  const rows = sorted.map(e => [
    esc(e.rowKey ?? ""),
    esc(e.eventId ?? ""),
    esc(e.sessionId ?? ""),
    esc(e.tenantId ?? ""),
    esc(e.timestamp ?? ""),
    esc(e.eventType ?? ""),
    esc(e.severity ?? ""),
    esc(e.source ?? ""),
    String(e.phase ?? 0),
    esc(phaseNames[e.phase] ?? "Unknown"),
    esc(e.message ?? ""),
    String(e.sequence ?? 0),
    esc(e.data ? JSON.stringify(e.data) : ""),
  ].join(","));
  return "\uFEFF" + header + "\n" + rows.join("\n");
}

export function generateUiExport(events: SessionExportEvent[], sessionId: string, tenantId: string) {
  const isV1 = events.some(e => e.phase === 2);
  const phaseNames = isV1 ? EXPORT_V1_PHASE_NAMES : EXPORT_V2_PHASE_NAMES;
  const phaseOrder = isV1 ? EXPORT_V1_PHASE_ORDER : EXPORT_V2_PHASE_ORDER;

  const sorted = [...events].sort((a, b) => a.sequence - b.sequence);

  // Replicate eventsByPhase useMemo logic: assign Unknown-phase events to active named phase
  const grouped: Record<string, SessionExportEvent[]> = {};
  phaseOrder.forEach(p => { grouped[p] = []; });

  let lastNamedPhase = phaseOrder[0];
  for (const ev of sorted) {
    const name = phaseNames[ev.phase];
    if (name && name !== "Unknown") {
      lastNamedPhase = name;
      if (grouped[name]) grouped[name].push({ ...ev, phaseName: name });
    } else {
      // Unknown phase — insert into currently active named phase
      if (grouped[lastNamedPhase]) grouped[lastNamedPhase].push({ ...ev, phaseName: "Unknown" });
    }
  }

  const pad = (s: string, len: number) => s.padEnd(len);
  const severityLabel = (s: string) => pad(s ?? "Unknown", 7);

  const lines: string[] = [];
  lines.push("AUTOPILOT MONITOR \u2014 SESSION EVENT EXPORT");
  lines.push("=========================================");
  lines.push(`Session ID   : ${sessionId}`);
  lines.push(`Tenant ID    : ${tenantId}`);
  lines.push(`Exported at  : ${new Date().toISOString()}`);
  lines.push(`Total events : ${events.length}`);
  lines.push(`Enrollment   : ${isV1 ? "V1" : "V2"}`);

  for (const phase of phaseOrder) {
    const phaseEvents = grouped[phase] ?? [];
    lines.push("");
    lines.push("\u2550".repeat(43));
    lines.push(`  ${phase}  (${phaseEvents.length} event${phaseEvents.length !== 1 ? "s" : ""})`);
    lines.push("\u2550".repeat(43));
    if (phaseEvents.length === 0) {
      lines.push("  (no events)");
    } else {
      for (const ev of phaseEvents) {
        const ts = ev.timestamp ? new Date(ev.timestamp).toISOString().replace("T", " ").substring(0, 23) : "?";
        lines.push(`[${ts}] [${severityLabel(ev.severity)}] ${ev.eventType} \u2014 ${ev.message}`);
        let detail = `  Source: ${ev.source ?? "?"} | Seq: ${ev.sequence ?? "?"} | EventId: ${ev.eventId ?? "?"}`;
        if (ev.phaseName === "Unknown") detail += ` | RawPhase: ${ev.phase}`;
        lines.push(detail);
        if (ev.data && Object.keys(ev.data).length > 0) {
          lines.push(`  Data: ${JSON.stringify(ev.data)}`);
        }
      }
    }
  }

  return lines.join("\n");
}
