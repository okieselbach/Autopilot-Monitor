"use client";

import { useState } from "react";
import { API_BASE_URL } from "@/lib/config";
import { TenantConfiguration } from "./TenantManagementSection";

interface SessionExportEvent {
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

const EXPORT_V1_PHASE_NAMES: Record<number, string> = {
  0: "Start", 1: "Device Preparation", 2: "Device Setup",
  3: "Apps (Device)", 4: "Account Setup", 5: "Apps (User)",
  6: "Finalizing Setup", 7: "Complete", 99: "Failed"
};
const EXPORT_V2_PHASE_NAMES: Record<number, string> = {
  0: "Start", 1: "Device Preparation", 2: "Device Setup",
  3: "App Installation", 4: "Account Setup", 5: "Apps (User)",
  6: "Finalizing Setup", 7: "Complete", 99: "Failed"
};
const EXPORT_V1_PHASE_ORDER = ["Start", "Device Preparation", "Device Setup",
  "Apps (Device)", "Account Setup", "Apps (User)", "Finalizing Setup", "Complete", "Failed"];
const EXPORT_V2_PHASE_ORDER = ["Start", "Device Preparation", "App Installation",
  "Finalizing Setup", "Complete", "Failed"];

function downloadFile(content: string, filename: string, mimeType: string) {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

function generateCsvExport(events: SessionExportEvent[]) {
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

function generateUiExport(events: SessionExportEvent[], sessionId: string, tenantId: string) {
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

interface SessionExportSectionProps {
  tenants: TenantConfiguration[];
  getAccessToken: () => Promise<string | null>;
}

export function SessionExportSection({
  tenants,
  getAccessToken,
}: SessionExportSectionProps) {
  const [exportSessionId, setExportSessionId] = useState("");
  const [exportTenantId, setExportTenantId] = useState("");
  const [exportLoading, setExportLoading] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);
  const [exportedEvents, setExportedEvents] = useState<SessionExportEvent[] | null>(null);

  const cleanId = (v: string) => v.trim().replace(/^["'\s]+|["'\s]+$/g, "");

  const handleFetchExportEvents = async () => {
    const sid = cleanId(exportSessionId);
    const tid = cleanId(exportTenantId);
    if (!sid || !tid) {
      setExportError("Session ID and Tenant ID are required.");
      return;
    }
    try {
      setExportLoading(true);
      setExportError(null);
      setExportedEvents(null);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const res = await fetch(
        `${API_BASE_URL}/api/sessions/${sid}/events?tenantId=${encodeURIComponent(tid)}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}: ${res.statusText}`);
      const data = await res.json();
      if (!data.success) throw new Error(data.message || "Backend returned error");
      setExportedEvents(data.events ?? []);
    } catch (err) {
      setExportError(err instanceof Error ? err.message : "Failed to fetch events");
    } finally {
      setExportLoading(false);
    }
  };

  return (
    <div className="bg-gradient-to-br from-teal-50 to-cyan-50 dark:from-gray-800 dark:to-gray-800 border-2 border-teal-300 dark:border-teal-700 rounded-lg shadow-lg">
      <div className="p-6 border-b border-teal-200 dark:border-teal-700 bg-gradient-to-r from-teal-100 to-cyan-100 dark:from-teal-900/40 dark:to-cyan-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-teal-600 dark:text-teal-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-teal-900 dark:text-teal-100">Session Event Export</h2>
            <p className="text-sm text-teal-600 dark:text-teal-300 mt-1">Fetch and export all events directly from storage — use to analyze timeline phase grouping and ordering</p>
          </div>
        </div>
      </div>
      <div className="p-6">
        <div className="space-y-4">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-teal-900 dark:text-teal-100 mb-1">Session ID</label>
              <input
                type="text"
                placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                value={exportSessionId}
                onChange={e => setExportSessionId(e.target.value)}
                className="w-full px-3 py-2 border border-teal-300 dark:border-teal-600 rounded-lg text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-teal-900 dark:text-teal-100 mb-1">Tenant ID</label>
              {tenants.length > 0 ? (
                <div className="space-y-1.5">
                  <select
                    value={tenants.some(t => t.tenantId === exportTenantId) ? exportTenantId : ""}
                    onChange={e => { if (e.target.value) setExportTenantId(e.target.value); }}
                    className="w-full px-3 py-2 border border-teal-300 dark:border-teal-600 rounded-lg text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
                  >
                    <option value="">&mdash; select tenant &mdash;</option>
                    {[...tenants].sort((a, b) => (a.domainName || a.tenantId).localeCompare(b.domainName || b.tenantId)).map(t => (
                      <option key={t.tenantId} value={t.tenantId}>
                        {t.domainName ? `${t.domainName} (${t.tenantId})` : t.tenantId}
                      </option>
                    ))}
                  </select>
                  <input
                    type="text"
                    placeholder="or enter Tenant ID directly"
                    value={exportTenantId}
                    onChange={e => setExportTenantId(e.target.value)}
                    className="w-full px-3 py-2 border border-teal-300 dark:border-teal-600 rounded-lg text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
                  />
                </div>
              ) : (
                <input
                  type="text"
                  placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                  value={exportTenantId}
                  onChange={e => setExportTenantId(e.target.value)}
                  className="w-full px-3 py-2 border border-teal-300 dark:border-teal-600 rounded-lg text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
                />
              )}
            </div>
          </div>

          <div className="flex justify-end">
            <button
              onClick={handleFetchExportEvents}
              disabled={exportLoading}
              className="px-5 py-2.5 bg-gradient-to-r from-teal-600 to-cyan-600 text-white rounded-lg hover:from-teal-700 hover:to-cyan-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2 text-sm font-medium"
            >
              {exportLoading ? (
                <>
                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                  <span>Fetching...</span>
                </>
              ) : (
                <>
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                  </svg>
                  <span>Fetch Events</span>
                </>
              )}
            </button>
          </div>

          {exportError && (
            <div className="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-700 rounded-lg p-3 flex items-center space-x-2">
              <svg className="w-4 h-4 text-red-600 dark:text-red-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              <span className="text-sm text-red-800 dark:text-red-300">{exportError}</span>
            </div>
          )}

          {exportedEvents !== null && (
            <div className="space-y-3">
              <div className="flex items-center space-x-2 text-sm text-teal-800 dark:text-teal-200 bg-teal-50 dark:bg-teal-900/20 border border-teal-200 dark:border-teal-700 rounded-lg p-3">
                <svg className="w-4 h-4 text-teal-600 dark:text-teal-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span>
                  <strong>{exportedEvents.length}</strong> events loaded
                  {" \u00B7 "}
                  {exportedEvents.some(e => e.phase === 2) ? "V1" : "V2"}
                  {" \u00B7 "}
                  session {cleanId(exportSessionId).slice(0, 8)}
                </span>
              </div>
              <div className="flex flex-col sm:flex-row gap-3">
                <button
                  onClick={() => {
                    const sid = cleanId(exportSessionId);
                    const tid = cleanId(exportTenantId);
                    downloadFile(
                      generateUiExport(exportedEvents, sid, tid),
                      `session-${sid.slice(0, 8)}-timeline.txt`,
                      "text/plain;charset=utf-8"
                    );
                  }}
                  className="flex items-center justify-center space-x-2 px-4 py-2.5 bg-white dark:bg-gray-700 border-2 border-teal-400 dark:border-teal-600 text-teal-800 dark:text-teal-200 rounded-lg hover:bg-teal-50 dark:hover:bg-teal-900/20 transition-colors text-sm font-medium"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                  </svg>
                  <span>Download Timeline Export (.txt)</span>
                </button>
                <button
                  onClick={() => {
                    const sid = cleanId(exportSessionId);
                    downloadFile(
                      generateCsvExport(exportedEvents),
                      `session-${sid.slice(0, 8)}-events.csv`,
                      "text/csv;charset=utf-8"
                    );
                  }}
                  className="flex items-center justify-center space-x-2 px-4 py-2.5 bg-white dark:bg-gray-700 border-2 border-teal-400 dark:border-teal-600 text-teal-800 dark:text-teal-200 rounded-lg hover:bg-teal-50 dark:hover:bg-teal-900/20 transition-colors text-sm font-medium"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                  </svg>
                  <span>Download Raw CSV Export (.csv)</span>
                </button>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
