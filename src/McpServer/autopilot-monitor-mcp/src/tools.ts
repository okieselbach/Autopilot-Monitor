import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, buildQuery } from './client.js';

export function registerTools(server: McpServer): void {
  // Tool 1: search_sessions
  server.tool(
    'search_sessions',
    'Search enrollment sessions by device properties. Basic properties (status, serial number, OS build, manufacturer, model, enrollment type, geo) are indexed directly. Extended device properties (TPM version, Secure Boot, BitLocker, Autopilot mode, RAM, SSD, WiFi/Ethernet) use the device snapshot index. Use agentVersion to filter by Monitor Agent version, imeAgentVersion to filter by IME (Intune Management Extension) version. Examples: all Surface devices with TPM 2.0, all failed hybrid join sessions, all devices without Secure Boot enabled.',
    {
      tenantId: z.string().optional().describe('Tenant ID (only effective with a global-scoped API key; ignored with tenant-scoped key)'),
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
      agentVersion: z.string().optional().describe('Monitor Agent version (exact match, e.g. "1.4.2"). Use get_metrics to discover known versions.'),
      imeAgentVersion: z.string().optional().describe('IME Agent version (exact match, e.g. "1.23.456.789"). Populated when IME log was detected during enrollment.'),
      tpmSpecVersion: z.string().optional().describe('TPM specification version (e.g. "2.0", "1.2")'),
      tpmActivated: z.boolean().optional().describe('Filter by TPM activation status'),
      secureBootEnabled: z.boolean().optional().describe('Filter by Secure Boot status'),
      bitlockerEnabled: z.boolean().optional().describe('Filter by BitLocker system drive protection status'),
      autopilotMode: z.string().optional().describe('Autopilot deployment mode (e.g. "UserDriven", "SelfDeploying")'),
      domainJoinMethod: z.string().optional().describe('Domain join method (e.g. "AzureAD", "Hybrid", "None")'),
      connectionType: z.enum(['WiFi', 'Ethernet']).optional().describe('Network connection type during enrollment'),
      minRamGB: z.number().optional().describe('Minimum RAM in GB'),
      hasSSD: z.boolean().optional().describe('Filter by presence of SSD or NVMe storage'),
      limit: z.number().min(1).max(100).optional().default(50).describe('Maximum number of results (1-100, default 50)'),
    },
    async (args) => {
      const data = await apiFetch(`/api/sessions/search${buildQuery(args as Record<string, string | number | boolean | undefined | null>)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 2: search_sessions_by_event
  server.tool(
    'search_sessions_by_event',
    'Find sessions that contain a specific event type (e.g. app install failure, phase transitions, errors). Check the event_types resource for valid eventType values. Use this to answer: which devices had a failed Teams install, which sessions had an error in DeviceSetup phase.',
    {
      eventType: z.string().describe('Event type string — see event_types resource for valid values (e.g. "app_install_failed", "enrollment_failed")'),
      tenantId: z.string().optional().describe('Tenant ID filter (global-scoped key only)'),
      limit: z.number().min(1).max(100).optional().default(50),
    },
    async (args) => {
      const data = await apiFetch(`/api/sessions/search-by-event${buildQuery(args as Record<string, string | number | boolean | undefined | null>)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 3: get_session
  server.tool(
    'get_session',
    'Get full details of a single enrollment session including all device metadata. Set includeAnalysis=true to also get AI rule analysis results explaining why the session failed and remediation suggestions.',
    {
      sessionId: z.string().describe('Session UUID'),
      tenantId: z.string().optional().describe('Tenant ID (required for tenant-scoped key; optional for global-scoped key)'),
      includeAnalysis: z.boolean().optional().default(false).describe('Include rule analysis results (failure explanations and remediation steps)'),
    },
    async ({ sessionId, tenantId, includeAnalysis }) => {
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
    }
  );

  // Tool 4: get_session_events
  server.tool(
    'get_session_events',
    'Get the event timeline for a session. Filter by eventType, severity, or source (app name) to focus on relevant events. Useful for root cause analysis of failures.',
    {
      sessionId: z.string().describe('Session UUID'),
      tenantId: z.string().optional(),
      eventType: z.string().optional().describe('Filter to only events of this type'),
      severity: z.enum(['Info', 'Warning', 'Error', 'Critical']).optional(),
      source: z.string().optional().describe('Filter by event source/app name (e.g. "MicrosoftTeams")'),
    },
    async ({ sessionId, tenantId, ...filters }) => {
      const q = buildQuery({ tenantId } as Record<string, string | undefined>);
      const data = await apiFetch(`/api/sessions/${sessionId}/events${q}`) as {
        events?: Array<{ eventType?: string; severity?: string; source?: string }>;
        count?: number;
      };
      // Client-side filter since backend doesn't support these filters on the events endpoint
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
    }
  );

  // Tool 5: get_metrics
  server.tool(
    'get_metrics',
    'Get aggregated metrics: failure rates, slowest/most-failing apps, session counts per tenant. Omit tenantId (global-scoped key only) to get a cross-tenant overview.',
    {
      tenantId: z.string().optional(),
      days: z.union([z.literal(7), z.literal(30), z.literal(90)]).optional().default(30),
    },
    async (args) => {
      const q = buildQuery(args as Record<string, string | number | undefined>);
      const [summary, apps] = await Promise.all([
        apiFetch(`/api/metrics/summary${q}`).catch(() => null),
        apiFetch(`/api/metrics/app${q}`).catch(() => null),
      ]);
      return { content: [{ type: 'text' as const, text: JSON.stringify({ summary, apps }, null, 2) }] };
    }
  );

  // Tool 6: search_sessions_by_cve
  server.tool(
    'search_sessions_by_cve',
    "Find all enrollment sessions where a specific CVE was detected in the device's software inventory. Requires vulnerability scanning to be enabled. Use this to answer: which devices are affected by CVE-2024-XXXX, show all critical KEV (known exploited) vulnerability sessions.",
    {
      cveId: z.string().describe('CVE identifier (e.g. "CVE-2024-21447")'),
      tenantId: z.string().optional(),
      minCvssScore: z.number().min(0).max(10).optional().describe('Minimum CVSS score filter (e.g. 7.0 for high+critical)'),
      overallRisk: z.enum(['low', 'medium', 'high', 'critical']).optional(),
      limit: z.number().min(1).max(100).optional().default(50),
    },
    async (args) => {
      const data = await apiFetch(`/api/sessions/search-by-cve${buildQuery(args as Record<string, string | number | undefined | null>)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 7: list_blocked_devices
  server.tool(
    'list_blocked_devices',
    'List devices currently blocked from enrolling. Blocked devices have their enrollment sessions rejected by the backend.',
    {
      tenantId: z.string().optional().describe('Tenant ID (global-scoped key: optional; tenant-scoped: ignored)'),
    },
    async ({ tenantId }) => {
      const q = buildQuery({ tenantId } as Record<string, string | undefined>);
      const data = await apiFetch(`/api/devices/blocked${q}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );
}
