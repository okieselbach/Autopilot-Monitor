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

const SEVERITY_INT: Record<string, number> = {
  Debug: 0, Info: 1, Warning: 2, Error: 3, Critical: 4
};

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
  // Columns match the Events Azure Table Storage schema exactly
  // PartitionKey = TenantId_SessionId, RowKey = Timestamp_Sequence
  // PhaseName is a derived extra column appended after DataJson for convenience
  const header = "PartitionKey,RowKey,EventId,SessionId,TenantId,Timestamp,EventType,Severity,Source,Phase,Message,Sequence,DataJson,PhaseName";
  const rows = sorted.map(e => {
    const sev = e.severity ?? "";
    const sevInt = SEVERITY_INT[sev] ?? SEVERITY_INT[sev.charAt(0).toUpperCase() + sev.slice(1).toLowerCase()];
    const severityCell = sevInt !== undefined ? `${sev} (${sevInt})` : sev;
    return [
      esc(`${e.tenantId ?? ""}_${e.sessionId ?? ""}`),
      esc(e.rowKey ?? ""),
      esc(e.eventId ?? ""),
      esc(e.sessionId ?? ""),
      esc(e.tenantId ?? ""),
      esc(e.timestamp ?? ""),
      esc(e.eventType ?? ""),
      esc(severityCell),
      esc(e.source ?? ""),
      String(e.phase ?? 0),
      esc(e.message ?? ""),
      String(e.sequence ?? 0),
      esc(e.data ? JSON.stringify(e.data) : ""),
      esc(phaseNames[e.phase] ?? "Unknown"),
    ].join(",");
  });
  return "\uFEFF" + header + "\n" + rows.join("\n");
}

export interface SessionCsvData {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  osBuild?: string;
  osEdition?: string;
  osLanguage?: string;
  isUserDriven?: boolean;
  isPreProvisioned?: boolean;
  startedAt: string;
  completedAt?: string;
  agentVersion?: string;
  enrollmentType?: string;
  currentPhase: number;
  status: string;
  eventCount: number;
  failureReason?: string;
  lastEventAt?: string;
  durationSeconds: number;
  diagnosticsBlobName?: string;
}

export function generateSessionCsvExport(session: SessionCsvData): string {
  const esc = (v: string | undefined | null) => `"${String(v ?? "").replace(/"/g, '""')}"`;
  // Columns match the Sessions Azure Table Storage schema exactly
  // PartitionKey = TenantId, RowKey = SessionId
  const header = "PartitionKey,RowKey,SerialNumber,DeviceName,Manufacturer,Model,OsBuild,OsEdition,OsLanguage,IsUserDriven,IsPreProvisioned,StartedAt,CompletedAt,AgentVersion,EnrollmentType,CurrentPhase,Status,EventCount,FailureReason,LastEventAt,DurationSeconds,DiagnosticsBlobName";
  const row = [
    esc(session.tenantId),
    esc(session.sessionId),
    esc(session.serialNumber),
    esc(session.deviceName),
    esc(session.manufacturer),
    esc(session.model),
    esc(session.osBuild),
    esc(session.osEdition),
    esc(session.osLanguage),
    String(session.isUserDriven ?? ""),
    String(session.isPreProvisioned ?? ""),
    esc(session.startedAt),
    esc(session.completedAt),
    esc(session.agentVersion),
    esc(session.enrollmentType),
    String(session.currentPhase ?? ""),
    esc(session.status),
    String(session.eventCount ?? ""),
    esc(session.failureReason),
    esc(session.lastEventAt),
    String(session.durationSeconds ?? ""),
    esc(session.diagnosticsBlobName),
  ].join(",");
  return "\uFEFF" + header + "\n" + row;
}

export interface RuleResultCsvData {
  resultId: string;
  sessionId: string;
  tenantId: string;
  ruleId: string;
  ruleTitle: string;
  severity: string;
  category: string;
  confidenceScore: number;
  explanation: string;
  remediation: { title: string; steps: string[] }[];
  relatedDocs: { title: string; url: string }[];
  matchedConditions: Record<string, unknown>;
  detectedAt: string;
}

export function generateRuleResultsCsvExport(results: RuleResultCsvData[]): string {
  const esc = (v: string | undefined | null) => `"${String(v ?? "").replace(/"/g, '""')}"`;
  // Columns match the RuleResults Azure Table Storage schema exactly
  // PartitionKey = TenantId_SessionId, RowKey = RuleId
  const header = "PartitionKey,RowKey,ResultId,SessionId,TenantId,RuleId,RuleTitle,Severity,Category,ConfidenceScore,Explanation,RemediationJson,RelatedDocsJson,MatchedConditionsJson,DetectedAt";
  const rows = results.map(r => [
    esc(`${r.tenantId}_${r.sessionId}`),
    esc(r.ruleId),
    esc(r.resultId),
    esc(r.sessionId),
    esc(r.tenantId),
    esc(r.ruleId),
    esc(r.ruleTitle),
    esc(r.severity),
    esc(r.category),
    String(r.confidenceScore ?? ""),
    esc(r.explanation),
    esc(r.remediation ? JSON.stringify(r.remediation) : ""),
    esc(r.relatedDocs ? JSON.stringify(r.relatedDocs) : ""),
    esc(r.matchedConditions ? JSON.stringify(r.matchedConditions) : ""),
    esc(r.detectedAt),
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
