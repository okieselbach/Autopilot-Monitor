/**
 * Typed API URL builder
 * All backend endpoint URLs are defined here for type-safety and maintainability.
 */
import { API_BASE_URL } from "./config";

function qs(params: Record<string, string | undefined>): string {
  const p = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== "") p.set(key, value);
  }
  const str = p.toString();
  return str ? `?${str}` : "";
}

export const api = {
  // ── Auth ──────────────────────────────────────────────────────────────────
  auth: {
    me: () => `${API_BASE_URL}/api/auth/me`,
  },

  // ── Sessions ──────────────────────────────────────────────────────────────
  sessions: {
    list: (tenantId?: string) =>
      `${API_BASE_URL}/api/sessions${qs({ tenantId })}`,
    get: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}${qs({ tenantId })}`,
    events: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/events${qs({ tenantId })}`,
    delete: (sessionId: string, tenantId: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}${qs({ tenantId })}`,
    analysis: (sessionId: string, tenantId?: string, reanalyze?: boolean) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/analysis${qs({ tenantId, ...(reanalyze ? { reanalyze: "true" } : {}) })}`,
    vulnerabilityReport: (sessionId: string, tenantId?: string, rescan?: boolean) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/vulnerability-report${qs({ tenantId, ...(rescan ? { rescan: "true" } : {}) })}`,
    markFailed: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/mark-failed${qs({ tenantId })}`,
    markSucceeded: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/mark-succeeded${qs({ tenantId })}`,
    report: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/report${qs({ tenantId })}`,
    quickSearch: (q: string) =>
      `${API_BASE_URL}/api/search/quick${qs({ q })}`,
  },

  // ── Global Sessions (global admin) ────────────────────────────────────────
  globalSessions: {
    list: (tenantId?: string) =>
      `${API_BASE_URL}/api/global/sessions${qs({ tenantId })}`,
  },

  // ── Config ────────────────────────────────────────────────────────────────
  config: {
    all: () => `${API_BASE_URL}/api/config/all`,
    tenant: (tenantId: string) => `${API_BASE_URL}/api/config/${tenantId}`,
    featureFlags: (tenantId: string) => `${API_BASE_URL}/api/config/${tenantId}/feature-flags`,
    autopilotConsentUrl: (tenantId: string, redirectUri: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-url${qs({ redirectUri })}`,
    autopilotConsentStatus: (tenantId: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-status`,
    autopilotConsentFailure: (tenantId: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-failure`,
    testNotification: (tenantId: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/test-notification`,
  },

  // ── Global Config (global admin) ──────────────────────────────────────────
  globalConfig: {
    get: () => `${API_BASE_URL}/api/global/config`,
    tenant: (tenantId: string) => `${API_BASE_URL}/api/global/config/${tenantId}`,
  },

  // ── Tenants ───────────────────────────────────────────────────────────────
  tenants: {
    admins: (tenantId: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/admins`,
    admin: (tenantId: string, adminUpn: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}`,
    adminAction: (tenantId: string, adminUpn: string, action: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}/${action}`,
    adminPermissions: (tenantId: string, adminUpn: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}/permissions`,
    offboard: (tenantId: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/offboard`,
  },

  // ── Devices ───────────────────────────────────────────────────────────────
  devices: {
    blocked: (tenantId: string) =>
      `${API_BASE_URL}/api/devices/blocked${qs({ tenantId })}`,
    block: () => `${API_BASE_URL}/api/devices/block`,
    unblock: (serialNumber: string, tenantId: string) =>
      `${API_BASE_URL}/api/devices/block/${encodeURIComponent(serialNumber)}${qs({ tenantId })}`,
    allBlocked: () => `${API_BASE_URL}/api/global/devices/blocked`,
  },

  // ── Versions ──────────────────────────────────────────────────────────────
  versions: {
    blocked: () => `${API_BASE_URL}/api/versions/blocked`,
    block: () => `${API_BASE_URL}/api/versions/block`,
    unblock: (pattern: string) =>
      `${API_BASE_URL}/api/versions/block/${encodeURIComponent(pattern)}`,
  },

  // ── Rules ─────────────────────────────────────────────────────────────────
  rules: {
    analyze: () => `${API_BASE_URL}/api/rules/analyze`,
    analyzeRule: (ruleId: string) =>
      `${API_BASE_URL}/api/rules/analyze/${encodeURIComponent(ruleId)}`,
    globalAnalyze: (tenantId?: string) =>
      `${API_BASE_URL}/api/global/rules/analyze${qs({ tenantId })}`,
    gather: (tenantId?: string) =>
      `${API_BASE_URL}/api/rules/gather${qs({ tenantId })}`,
    gatherRule: (ruleId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/rules/gather/${ruleId}${qs({ tenantId })}`,
    globalGather: (tenantId?: string) =>
      `${API_BASE_URL}/api/global/rules/gather${qs({ tenantId })}`,
    reseedFromGitHub: (type: "analyze" | "gather" | "ime" | "all") =>
      `${API_BASE_URL}/api/rules/reseed-from-github${qs({ type })}`,
    imeLogPatterns: () => `${API_BASE_URL}/api/rules/ime-log-patterns`,
    imeLogPattern: (patternId: string) =>
      `${API_BASE_URL}/api/rules/ime-log-patterns/${encodeURIComponent(patternId)}${qs({ global: "true" })}`,
  },

  // ── Metrics ───────────────────────────────────────────────────────────────
  metrics: {
    usage: (tenantId?: string) =>
      `${API_BASE_URL}/api/metrics/usage${qs({ tenantId })}`,
    globalUsage: (tenantId?: string) =>
      `${API_BASE_URL}/api/global/metrics/usage${qs({ tenantId })}`,
    app: (tenantId: string, days: number) =>
      `${API_BASE_URL}/api/metrics/app${qs({ tenantId, days: String(days) })}`,
    globalApp: (days: number) =>
      `${API_BASE_URL}/api/global/metrics/app${qs({ days: String(days) })}`,
    geographic: (tenantId: string, days: number, groupBy: string) =>
      `${API_BASE_URL}/api/metrics/geographic${qs({ tenantId, days: String(days), groupBy })}`,
    globalGeographic: (days: number, groupBy: string) =>
      `${API_BASE_URL}/api/global/metrics/geographic${qs({ days: String(days), groupBy })}`,
    geographicSessions: (tenantId: string, days: number, groupBy: string, locationKey: string) =>
      `${API_BASE_URL}/api/metrics/geographic/sessions${qs({ tenantId, days: String(days), groupBy, locationKey })}`,
    globalGeographicSessions: (days: number, groupBy: string, locationKey: string) =>
      `${API_BASE_URL}/api/global/metrics/geographic/sessions${qs({ days: String(days), groupBy, locationKey })}`,
    platform: () => `${API_BASE_URL}/api/global/metrics/platform`,
  },

  // ── Diagnostics ───────────────────────────────────────────────────────────
  diagnostics: {
    downloadUrl: (tenantId: string, blobName: string) =>
      `${API_BASE_URL}/api/diagnostics/download-url${qs({ tenantId, blobName })}`,
  },

  // ── Progress ──────────────────────────────────────────────────────────────
  progress: {
    sessions: (tenantId: string) =>
      `${API_BASE_URL}/api/progress/sessions${qs({ tenantId })}`,
    sessionEvents: (sessionId: string, tenantId: string) =>
      `${API_BASE_URL}/api/progress/sessions/${sessionId}/events${qs({ tenantId })}`,
  },

  // ── Bootstrap ─────────────────────────────────────────────────────────────
  bootstrap: {
    sessions: (tenantId?: string) =>
      `${API_BASE_URL}/api/bootstrap/sessions${qs({ tenantId })}`,
    session: (code: string, tenantId: string) =>
      `${API_BASE_URL}/api/bootstrap/sessions/${code}${qs({ tenantId })}`,
  },

  // ── Reports ───────────────────────────────────────────────────────────────
  reports: {
    list: () => `${API_BASE_URL}/api/global/session-reports`,
    downloadUrl: (blobName: string) =>
      `${API_BASE_URL}/api/global/session-reports/download-url${qs({ blobName })}`,
    note: (reportId: string) =>
      `${API_BASE_URL}/api/global/session-reports/${reportId}/note`,
  },

  // ── Distress Reports ──────────────────────────────────────────────────────
  distressReports: {
    list: () => `${API_BASE_URL}/api/global/distress-reports`,
  },

  // ── Ops Events ───────────────────────────────────────────────────────────
  opsEvents: {
    list: (category?: string, maxResults?: number) =>
      `${API_BASE_URL}/api/global/ops-events${qs({ category, maxResults: maxResults?.toString() })}`,
  },

  // ── Notifications ─────────────────────────────────────────────────────────
  notifications: {
    list: () => `${API_BASE_URL}/api/global/notifications`,
    dismiss: (id: string) => `${API_BASE_URL}/api/global/notifications/${id}/dismiss`,
    dismissAll: () => `${API_BASE_URL}/api/global/notifications/dismiss-all`,
  },

  // ── Audit ─────────────────────────────────────────────────────────────────
  audit: {
    logs: () => `${API_BASE_URL}/api/audit/logs`,
    globalLogs: () => `${API_BASE_URL}/api/global/audit/logs`,
  },

  // ── Feedback ──────────────────────────────────────────────────────────────
  feedback: {
    status: () => `${API_BASE_URL}/api/feedback/status`,
    submit: () => `${API_BASE_URL}/api/feedback`,
    all: () => `${API_BASE_URL}/api/feedback/all`,
  },

  // ── Preview ───────────────────────────────────────────────────────────────
  preview: {
    whitelist: () => `${API_BASE_URL}/api/preview/whitelist`,
    whitelistTenant: (tenantId: string) =>
      `${API_BASE_URL}/api/preview/whitelist/${tenantId}`,
    sendWelcomeEmail: (tenantId: string) =>
      `${API_BASE_URL}/api/preview/send-welcome-email/${tenantId}`,
    notificationEmail: () => `${API_BASE_URL}/api/preview/notification-email`,
    notificationEmailTenant: (tenantId: string) =>
      `${API_BASE_URL}/api/preview/notification-email/${tenantId}`,
  },

  // ── Vulnerability ─────────────────────────────────────────────────────────
  vulnerability: {
    sync: () => `${API_BASE_URL}/api/vulnerability/sync`,
    syncReseed: (type: string) =>
      `${API_BASE_URL}/api/vulnerability/sync${qs({ reseed: type })}`,
    cpeMappings: () => `${API_BASE_URL}/api/vulnerability/cpe-mappings`,
    cpeMapping: () => `${API_BASE_URL}/api/vulnerability/cpe-mapping`,
    unmatchedSoftware: () => `${API_BASE_URL}/api/vulnerability/unmatched-software`,
  },

  // ── MCP Users ─────────────────────────────────────────────────────────────
  mcpUsers: {
    list: () => `${API_BASE_URL}/api/global/mcp-users`,
    add: () => `${API_BASE_URL}/api/global/mcp-users`,
    remove: (upn: string) =>
      `${API_BASE_URL}/api/global/mcp-users/${encodeURIComponent(upn)}`,
    enable: (upn: string) =>
      `${API_BASE_URL}/api/global/mcp-users/${encodeURIComponent(upn)}/enable`,
    disable: (upn: string) =>
      `${API_BASE_URL}/api/global/mcp-users/${encodeURIComponent(upn)}/disable`,
    check: () => `${API_BASE_URL}/api/global/mcp-users/check`,
    setUsagePlan: (upn: string) =>
      `${API_BASE_URL}/api/global/mcp-users/${encodeURIComponent(upn)}/usage-plan`,
  },

  // ── MCP Usage ──────────────────────────────────────────────────────────────
  mcpUsage: {
    me: (dateFrom?: string, dateTo?: string) =>
      `${API_BASE_URL}/api/metrics/mcp-usage/me${qs({ dateFrom, dateTo })}`,
    user: (userId: string, dateFrom?: string, dateTo?: string) =>
      `${API_BASE_URL}/api/metrics/mcp-usage/user/${encodeURIComponent(userId)}${qs({ dateFrom, dateTo })}`,
    global: (tenantId?: string, dateFrom?: string, dateTo?: string) =>
      `${API_BASE_URL}/api/global/metrics/mcp-usage${qs({ tenantId, dateFrom, dateTo })}`,
    daily: (tenantId?: string, dateFrom?: string, dateTo?: string) =>
      `${API_BASE_URL}/api/global/metrics/mcp-usage/daily${qs({ tenantId, dateFrom, dateTo })}`,
    planTiers: () => `${API_BASE_URL}/api/global/config/plan-tiers`,
  },

  // ── Maintenance ───────────────────────────────────────────────────────────
  maintenance: {
    trigger: (date?: string) =>
      `${API_BASE_URL}/api/maintenance/trigger${qs({ date })}`,
  },

  // ── Health ────────────────────────────────────────────────────────────────
  health: {
    detailed: () => `${API_BASE_URL}/api/health/detailed`,
  },

  // ── Realtime (SignalR) ────────────────────────────────────────────────────
  realtime: {
    hub: () => `${API_BASE_URL}/api/realtime`,
    joinGroup: () => `${API_BASE_URL}/api/realtime/groups/join`,
    leaveGroup: () => `${API_BASE_URL}/api/realtime/groups/leave`,
  },
};
