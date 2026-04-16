import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, buildQuery } from '../client.js';
import { withToolTelemetry } from '../telemetry.js';
import { READ_ONLY } from './shared.js';
import { toolError } from './error-handler.js';

// ── Session summary constants ───────────────────────────────────────────

const EXCLUDED_EVENT_TYPES = new Set([
  'performance_snapshot', 'agent_metrics_snapshot',
  'performance_snapshot_stopped', 'agent_metrics_snapshot_stopped',
  'gather_result', 'gather_rules_collection_completed',
  'software_inventory_analysis', 'security_audit',
  'device_location', 'ntp_time_check', 'ime_agent_version',
]);
const KEY_EVENT_TYPES = new Set([
  'phase_transition', 'esp_phase_changed', 'enrollment_type_detected',
  'app_install_started', 'app_install_completed', 'app_install_failed', 'app_install_skipped',
  'app_tracking_summary', 'error_detected',
  'enrollment_complete', 'enrollment_failed', 'completion_check',
  'desktop_arrived', 'hello_policy_detected', 'waiting_for_hello', 'hello_completion_timeout',
  'agent_started', 'agent_shutdown', 'trace_event',
  'script_completed', 'script_failed', 'vulnerability_report',
]);
const SEVERITY_RANK: Record<string, number> = { Trace: -1, Debug: 0, Info: 1, Warning: 2, Error: 3, Critical: 4 };
const PHASE_NAMES: Record<number, string> = {
  0: 'Unknown', 1: 'Device Preparation', 2: 'Device Setup', 3: 'Account Setup',
  4: 'Device ESP', 5: 'User ESP', 6: 'Complete', 7: 'Pre-Provisioning',
};

// ── Registration ────────────────────────────────────────────────────────

export function registerSessionTools(server: McpServer): void {
  // Tool 1: search_sessions
  server.tool(
    'search_sessions',
    'Search enrollment sessions. Omit tenantId for cross-tenant search (Global Admin), or specify tenantId for single-tenant. ' +
    'Basic properties (status, serial number, manufacturer, model, etc.) filter on the session index. ' +
    'Use deviceProperties for ANY device hardware/config filter — keys use "eventType.propertyName" notation. ' +
    'Consult the device_properties resource for available keys. ' +
    'Examples: {"tpm_status.specVersion": "2.0"}, {"hardware_spec.ramTotalGB": ">=8"}, {"secureboot_status.uefiSecureBootEnabled": "True"}. ' +
    'Array values are searched as substring match (e.g. disks containing "NVMe").',
    {
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant search (Global Admin only).'),
      status: z.enum(['InProgress', 'Succeeded', 'Failed']).optional().describe('Enrollment status filter'),
      serialNumber: z.string().optional().describe('Device serial number (exact match)'),
      deviceName: z.string().optional().describe('Device name (prefix match, e.g. "DESKTOP-")'),
      manufacturer: z.string().optional().describe('Hardware manufacturer (e.g. "Microsoft", "Dell", "HP")'),
      model: z.string().optional().describe('Hardware model (e.g. "Surface Pro 9")'),
      osBuild: z.string().optional().describe('OS build number prefix (e.g. "26100")'),
      enrollmentType: z.enum(['v1', 'v2']).optional().describe('Autopilot enrollment type'),
      isPreProvisioned: z.boolean().optional().describe('Filter by White Glove / pre-provisioned enrollment'),
      isHybridJoin: z.boolean().optional().describe('Filter by Hybrid Azure AD Join'),
      geoCountry: z.string().optional().describe('Country of enrollment (2-letter ISO code, e.g. "DE", "US")'),
      startedAfter: z.string().optional().describe('ISO 8601 datetime — only sessions started after this'),
      startedBefore: z.string().optional().describe('ISO 8601 datetime — only sessions started before this'),
      agentVersion: z.string().optional().describe('Monitor Agent version (exact match, e.g. "1.4.2")'),
      imeAgentVersion: z.string().optional().describe('IME Agent version (exact match, e.g. "1.23.456.789")'),
      deviceProperties: z.record(z.string(), z.string()).optional().describe(
        'Dynamic device property filters. Keys use "eventType.propertyName" dot notation. ' +
        'See the device_properties resource for all available keys and types. ' +
        'Values: exact match by default. Prefix with >=, <=, >, < for numeric ranges (e.g. ">=8"). ' +
        'Booleans: use "True" or "False". Arrays: substring match in any element.'
      ),
      limit: z.coerce.number().min(1).max(100).optional().default(50).describe('Maximum number of results (1-100, default 50)'),
    },
    READ_ONLY,
    async (args) => withToolTelemetry('search_sessions', async () => {
      try {
        const { deviceProperties, tenantId, ...rest } = args;
        const queryParams: Record<string, string | number | boolean | undefined | null> = { ...rest };
        if (tenantId) queryParams.tenantId = tenantId;
        if (deviceProperties) {
          for (const [key, value] of Object.entries(deviceProperties)) {
            queryParams[`prop.${key}`] = value;
          }
        }
        const basePath = tenantId ? '/api/search/sessions' : '/api/global/search/sessions';
        const data = await apiFetch(`${basePath}${buildQuery(queryParams)}`);
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        return toolError('search_sessions', args, error);
      }
    })
  );

  // Tool 2: search_sessions_by_event
  server.tool(
    'search_sessions_by_event',
    'Find sessions that contain a specific event type (e.g. app install failure, phase transitions, errors). ' +
    'Omit tenantId for cross-tenant search (Global Admin). ' +
    'Check the event_types resource for valid eventType values. ' +
    'Use this to answer: which devices had a failed Teams install, which sessions had an error in DeviceSetup phase.',
    {
      eventType: z.string().describe('Event type string — see event_types resource for valid values (e.g. "app_install_failed", "enrollment_failed")'),
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant search (Global Admin only).'),
      limit: z.coerce.number().min(1).max(100).optional().default(50),
    },
    READ_ONLY,
    async (args) => withToolTelemetry('search_sessions_by_event', async () => {
      try {
        const { tenantId, ...rest } = args;
        const params: Record<string, string | number | boolean | undefined | null> = { ...rest };
        if (tenantId) params.tenantId = tenantId;
        const basePath = tenantId ? '/api/search/sessions-by-event' : '/api/global/search/sessions-by-event';
        const data = await apiFetch(`${basePath}${buildQuery(params)}`);
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        return toolError('search_sessions_by_event', args, error);
      }
    })
  );

  // Tool 3: get_session
  server.tool(
    'get_session',
    'Get full details of a single enrollment session including all device metadata. Set includeAnalysis=true to also get AI rule analysis results explaining why the session failed and remediation suggestions.',
    {
      sessionId: z.string().describe('Session UUID'),
      tenantId: z.string().optional().describe('Tenant ID. If omitted, auto-resolved from the session (Global Admin can access any tenant).'),
      includeAnalysis: z.boolean().optional().default(false).describe('Include rule analysis results (failure explanations and remediation steps)'),
    },
    READ_ONLY,
    async (args) => withToolTelemetry('get_session', async () => {
      try {
        const { sessionId, tenantId, includeAnalysis } = args;
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        const sessionData = await apiFetch(`/api/sessions/${sessionId}${q}`);
        let analysisData: unknown = null;
        if (includeAnalysis) {
          try {
            analysisData = await apiFetch(`/api/sessions/${sessionId}/analysis${q}`);
          } catch {
            // analysis may not exist yet
          }
        }
        return { content: [{ type: 'text' as const, text: JSON.stringify({ session: sessionData, analysis: analysisData }, null, 2) }] };
      } catch (error: unknown) {
        return toolError('get_session', args, error);
      }
    })
  );

  // Tool 4: get_session_events
  server.tool(
    'get_session_events',
    'TIER 2 — RAW EVENT RETRIEVAL (fallback when semantic search misses). ' +
    'Get the complete event timeline for a single session. Filter by eventType, severity, or source (app name). ' +
    'Use this when search_events_semantic returns incomplete results and you need the full unfiltered event stream, ' +
    'or for root cause analysis when you need every event in chronological sequence. ' +
    'If you omit tenantId, the backend auto-resolves it from the session (Global Admin can access any tenant).',
    {
      sessionId: z.string().describe('Session UUID'),
      tenantId: z.string().optional().describe('Tenant ID. If omitted, auto-resolved from the session.'),
      eventType: z.string().optional().describe('Filter to only events of this type'),
      severity: z.enum(['Info', 'Warning', 'Error', 'Critical']).optional(),
      source: z.string().optional().describe('Filter by event source/app name (e.g. "MicrosoftTeams")'),
    },
    READ_ONLY,
    async (args) => withToolTelemetry('get_session_events', async () => {
      try {
        const { sessionId, tenantId, ...filters } = args;
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        const data = await apiFetch(`/api/sessions/${sessionId}/events${q}`) as {
          events?: Array<{ eventType?: string; severity?: string; source?: string }>;
          count?: number;
        };
        if (data?.events && (filters.eventType || filters.severity || filters.source)) {
          data.events = data.events.filter((e) => {
            if (filters.eventType && e.eventType !== filters.eventType) return false;
            if (filters.severity && e.severity !== filters.severity) return false;
            if (filters.source && !e.source?.toLowerCase().includes(filters.source.toLowerCase())) return false;
            return true;
          });
          data.count = data.events.length;
        }
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        return toolError('get_session_events', args, error);
      }
    })
  );

  // Tool 5: get_session_summary
  server.tool(
    'get_session_summary',
    'Get a concise, structured summary of an enrollment session optimized for analysis. ' +
    'Returns: session overview (status, duration, device, enrollment config), ' +
    'key events timeline (errors, warnings, phase transitions, app installs — noise filtered out), ' +
    'rule analysis results (probable cause, remediation), and aggregate stats. ' +
    'Use this as the FIRST tool when investigating a session. ' +
    'For raw unfiltered events use get_session_events. For full metadata use get_session.',
    {
      sessionId: z.string().describe('Session UUID'),
      tenantId: z.string().optional().describe('Tenant ID. If omitted, auto-resolved from the session (Global Admin can access any tenant).'),
    },
    READ_ONLY,
    async (args) => withToolTelemetry('get_session_summary', async () => {
      try {
        const { sessionId, tenantId } = args;
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        const fetchOpts = { signal: AbortSignal.timeout(90_000) };

        const [sessionData, eventsData, analysisData] = await Promise.all([
          apiFetch(`/api/sessions/${sessionId}${q}`, fetchOpts) as Promise<Record<string, unknown>>,
          apiFetch(`/api/sessions/${sessionId}/events${q}`, fetchOpts) as Promise<{ events?: Array<Record<string, unknown>>; count?: number }>,
          apiFetch(`/api/sessions/${sessionId}/analysis${q}`, fetchOpts).catch(() => null) as Promise<Record<string, unknown> | null>,
        ]);

        const s = (sessionData.session ?? sessionData) as Record<string, unknown>;

        const overview = {
          sessionId,
          tenantId: s.tenantId ?? tenantId,
          status: s.status,
          failureReason: s.failureReason ?? null,
          startedAt: s.startedAt,
          completedAt: s.completedAt ?? null,
          durationSeconds: s.durationSeconds ?? null,
          currentPhase: PHASE_NAMES[Number(s.currentPhase)] ?? String(s.currentPhase ?? 'Unknown'),
          enrollmentType: s.enrollmentType,
          isPreProvisioned: s.isPreProvisioned ?? false,
          isHybridJoin: s.isHybridJoin ?? false,
          isUserDriven: s.isUserDriven ?? false,
          device: {
            name: s.deviceName,
            serialNumber: s.serialNumber,
            manufacturer: s.manufacturer,
            model: s.model,
            osBuild: s.osBuild,
            osEdition: s.osEdition,
          },
          agent: {
            version: s.agentVersion,
            imeVersion: s.imeAgentVersion,
          },
          location: (s.geoCountry || s.geoRegion || s.geoCity)
            ? { country: s.geoCountry, region: s.geoRegion, city: s.geoCity }
            : null,
        };

        const allEvents = (eventsData?.events ?? []) as Array<Record<string, unknown>>;

        let errorCount = 0;
        let warningCount = 0;
        let appTotal = 0;
        let appSucceeded = 0;
        let appFailed = 0;
        let appSkipped = 0;
        for (const e of allEvents) {
          const sev = String(e.severity ?? '');
          if (sev === 'Error' || sev === 'Critical') errorCount++;
          if (sev === 'Warning') warningCount++;
          const et = String(e.eventType ?? '');
          if (et === 'app_install_started') appTotal++;
          if (et === 'app_install_completed') appSucceeded++;
          if (et === 'app_install_failed') appFailed++;
          if (et === 'app_install_skipped') appSkipped++;
        }

        let keyEvents = allEvents.filter((e) => {
          const et = String(e.eventType ?? '');
          if (EXCLUDED_EVENT_TYPES.has(et)) return false;
          if (KEY_EVENT_TYPES.has(et)) return true;
          return (SEVERITY_RANK[String(e.severity ?? '')] ?? -1) >= 2;
        });

        const mappedEvents = keyEvents.map((e) => ({
          timestamp: e.timestamp,
          eventType: e.eventType,
          severity: e.severity,
          phase: PHASE_NAMES[Number(e.phase)] ?? String(e.phase ?? ''),
          message: e.message,
          source: e.source,
          details: e.data && typeof e.data === 'object' && Object.keys(e.data as object).length > 0
            ? e.data
            : (e.dataJson ? (() => { try { return JSON.parse(String(e.dataJson)); } catch { return null; } })() : null),
        }));

        let analysis = null;
        if (analysisData) {
          const a = analysisData as Record<string, unknown>;
          const results = (a.results ?? []) as Array<Record<string, unknown>>;
          analysis = {
            totalIssues: a.totalIssues ?? results.length,
            criticalCount: a.criticalCount ?? 0,
            highCount: a.highCount ?? 0,
            warningCount: a.warningCount ?? 0,
            issues: results.map((r) => ({
              ruleTitle: r.ruleTitle ?? r.title,
              severity: r.severity,
              explanation: r.explanation,
              remediation: r.remediation,
            })),
          };
        }

        const result = {
          overview,
          keyEvents: mappedEvents,
          analysis,
          stats: {
            totalEvents: allEvents.length,
            keyEventsShown: mappedEvents.length,
            errorCount,
            warningCount,
            appInstalls: { total: appTotal, succeeded: appSucceeded, failed: appFailed, skipped: appSkipped },
          },
        };

        return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
      } catch (error: unknown) {
        return toolError('get_session_summary', args, error);
      }
    })
  );

  // Tool 6: get_metrics
  server.tool(
    'get_metrics',
    'Get aggregated enrollment metrics: failure rates, slowest/most-failing apps, session counts. ' +
    'Omit tenantId for cross-tenant platform overview (Global Admin). Specify tenantId for single-tenant metrics.',
    {
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant overview (Global Admin only).'),
      days: z.coerce.number().refine(v => [7, 30, 90].includes(v), { message: 'days must be 7, 30, or 90' }).optional().default(30),
    },
    READ_ONLY,
    async (args) => withToolTelemetry('get_metrics', async () => {
      try {
        const { tenantId, ...rest } = args;
        const params: Record<string, string | number | undefined> = { ...rest };
        if (tenantId) params.tenantId = tenantId;
        const q = buildQuery(params);
        const prefix = tenantId ? '/api/metrics' : '/api/global/metrics';
        const [summary, apps] = await Promise.all([
          apiFetch(`${prefix}/summary${q}`).catch(() => null),
          apiFetch(`${prefix}/app${q}`).catch(() => null),
        ]);
        return { content: [{ type: 'text' as const, text: JSON.stringify({ summary, apps }, null, 2) }] };
      } catch (error: unknown) {
        return toolError('get_metrics', args, error);
      }
    })
  );

  // Tool 7: search_sessions_by_cve
  server.tool(
    'search_sessions_by_cve',
    "Find enrollment sessions where a specific CVE was detected in the device's software inventory. " +
    "Omit tenantId for cross-tenant search (Global Admin). " +
    "Requires vulnerability scanning to be enabled. Use this to answer: which devices are affected by CVE-2024-XXXX, show all critical vulnerability sessions.",
    {
      cveId: z.string().describe('CVE identifier (e.g. "CVE-2024-21447")'),
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant search (Global Admin only).'),
      minCvssScore: z.coerce.number().min(0).max(10).optional().describe('Minimum CVSS score filter (e.g. 7.0 for high+critical)'),
      overallRisk: z.enum(['low', 'medium', 'high', 'critical']).optional(),
      limit: z.coerce.number().min(1).max(100).optional().default(50),
    },
    READ_ONLY,
    async (args) => withToolTelemetry('search_sessions_by_cve', async () => {
      try {
        const { tenantId, ...rest } = args;
        const params: Record<string, string | number | boolean | undefined | null> = { ...rest };
        if (tenantId) params.tenantId = tenantId;
        const basePath = tenantId ? '/api/search/sessions-by-cve' : '/api/global/search/sessions-by-cve';
        const data = await apiFetch(`${basePath}${buildQuery(params)}`);
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        return toolError('search_sessions_by_cve', args, error);
      }
    })
  );

  // Tool 8: list_blocked_devices
  server.tool(
    'list_blocked_devices',
    'List devices currently blocked from enrolling. Blocked devices have their enrollment sessions rejected by the backend. ' +
    'Omit tenantId to list blocked devices across ALL tenants (requires Global Admin).',
    {
      tenantId: z.string().optional().describe('Tenant ID to scope results. Omit for cross-tenant listing (Global Admin only).'),
    },
    READ_ONLY,
    async (args) => withToolTelemetry('list_blocked_devices', async () => {
      try {
        const { tenantId } = args;
        const endpoint = tenantId
          ? `/api/devices/blocked${buildQuery({ tenantId } as Record<string, string | undefined>)}`
          : '/api/global/devices/blocked';
        const data = await apiFetch(endpoint);
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        return toolError('list_blocked_devices', args, error);
      }
    })
  );

  // Tool: get_ime_version_history
  server.tool(
    'get_ime_version_history',
    'Get the history of all IME (Intune Management Extension) agent versions seen across enrollments. ' +
    'Shows when each version was first and last seen, and how many sessions reported it. ' +
    'This is a permanent archive that survives data retention — useful for tracking Microsoft IME release rollouts over time. ' +
    'Available to all tenant members (no tenantId needed, data is global).',
    {},
    READ_ONLY,
    async (args) => withToolTelemetry('get_ime_version_history', async () => {
      try {
        const data = await apiFetch('/api/metrics/ime-versions');
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        return toolError('get_ime_version_history', args, error);
      }
    })
  );
}
