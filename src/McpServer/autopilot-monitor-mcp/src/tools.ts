import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, buildQuery } from './client.js';
import type { SearchProvider } from './search-provider.js';
import { createSearchProvider } from './search-factory.js';

export function registerTools(server: McpServer, knowledgeBase?: SearchProvider): void {
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
      limit: z.number().min(1).max(100).optional().default(50).describe('Maximum number of results (1-100, default 50)'),
    },
    async (args) => {
      const { deviceProperties, tenantId, ...rest } = args;
      const queryParams: Record<string, string | number | boolean | undefined | null> = { ...rest };
      if (tenantId) queryParams.tenantId = tenantId;
      if (deviceProperties) {
        for (const [key, value] of Object.entries(deviceProperties)) {
          queryParams[`prop.${key}`] = value;
        }
      }
      // Cross-tenant (no tenantId) → global endpoint; single-tenant → tenant-scoped
      const basePath = tenantId ? '/api/search/sessions' : '/api/global/search/sessions';
      const data = await apiFetch(`${basePath}${buildQuery(queryParams)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
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
      limit: z.number().min(1).max(100).optional().default(50),
    },
    async (args) => {
      const { tenantId, ...rest } = args;
      const params: Record<string, string | number | boolean | undefined | null> = { ...rest };
      if (tenantId) params.tenantId = tenantId;
      const basePath = tenantId ? '/api/search/sessions-by-event' : '/api/global/search/sessions-by-event';
      const data = await apiFetch(`${basePath}${buildQuery(params)}`);
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
    'Get aggregated enrollment metrics: failure rates, slowest/most-failing apps, session counts. ' +
    'Omit tenantId for cross-tenant platform overview (Global Admin). Specify tenantId for single-tenant metrics.',
    {
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant overview (Global Admin only).'),
      days: z.union([z.literal(7), z.literal(30), z.literal(90)]).optional().default(30),
    },
    async (args) => {
      const { tenantId, ...rest } = args;
      const params: Record<string, string | number | undefined> = { ...rest };
      if (tenantId) params.tenantId = tenantId;
      const q = buildQuery(params);
      // Cross-tenant → global endpoints; single-tenant → tenant-scoped
      const prefix = tenantId ? '/api/metrics' : '/api/global/metrics';
      const [summary, apps] = await Promise.all([
        apiFetch(`${prefix}/summary${q}`).catch(() => null),
        apiFetch(`${prefix}/app${q}`).catch(() => null),
      ]);
      return { content: [{ type: 'text' as const, text: JSON.stringify({ summary, apps }, null, 2) }] };
    }
  );

  // Tool 6: search_sessions_by_cve
  server.tool(
    'search_sessions_by_cve',
    "Find enrollment sessions where a specific CVE was detected in the device's software inventory. " +
    "Omit tenantId for cross-tenant search (Global Admin). " +
    "Requires vulnerability scanning to be enabled. Use this to answer: which devices are affected by CVE-2024-XXXX, show all critical vulnerability sessions.",
    {
      cveId: z.string().describe('CVE identifier (e.g. "CVE-2024-21447")'),
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant search (Global Admin only).'),
      minCvssScore: z.number().min(0).max(10).optional().describe('Minimum CVSS score filter (e.g. 7.0 for high+critical)'),
      overallRisk: z.enum(['low', 'medium', 'high', 'critical']).optional(),
      limit: z.number().min(1).max(100).optional().default(50),
    },
    async (args) => {
      const { tenantId, ...rest } = args;
      const params: Record<string, string | number | boolean | undefined | null> = { ...rest };
      if (tenantId) params.tenantId = tenantId;
      const basePath = tenantId ? '/api/search/sessions-by-cve' : '/api/global/search/sessions-by-cve';
      const data = await apiFetch(`${basePath}${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 7: list_blocked_devices
  server.tool(
    'list_blocked_devices',
    'List devices currently blocked from enrolling. Blocked devices have their enrollment sessions rejected by the backend. ' +
    'Omit tenantId to list blocked devices across ALL tenants (requires Global Admin).',
    {
      tenantId: z.string().optional().describe('Tenant ID to scope results. Omit for cross-tenant listing (Global Admin only).'),
    },
    async ({ tenantId }) => {
      // Without tenantId → global endpoint (cross-tenant, Global Admin only)
      // With tenantId → tenant-scoped endpoint
      const endpoint = tenantId
        ? `/api/devices/blocked${buildQuery({ tenantId } as Record<string, string | undefined>)}`
        : '/api/global/devices/blocked';
      const data = await apiFetch(endpoint);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 8: search_events_semantic
  server.tool(
    'search_events_semantic',
    'Semantic/fuzzy search over enrollment event messages within a session or across recent failed sessions. ' +
    'Unlike exact filters, this finds events by MEANING — e.g. "network timeout" also matches "connection timed out", "request failed after waiting". ' +
    'Use this when you need to find events that match a symptom description rather than an exact event type. ' +
    'Provide a sessionId to search within one session, or omit it to search across recent failed sessions.',
    {
      query: z.string().describe('Natural language description of what to find (e.g. "app download stuck", "certificate error", "disk space low")'),
      sessionId: z.string().optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
      tenantId: z.string().optional().describe('Tenant ID (global-scoped key only)'),
      topK: z.number().min(1).max(30).optional().default(10).describe('Number of matching events to return (1-30, default 10)'),
      minScore: z.number().min(0).max(1).optional().default(0.35)
        .describe('Minimum similarity score (0-1, default 0.35)'),
    },
    async ({ query, sessionId, tenantId, topK, minScore }) => {
      type EventEntry = { eventType?: string; severity?: string; source?: string; message?: string; timestamp?: string; phase?: string; data?: Record<string, unknown> };
      let events: EventEntry[] = [];
      let sessionIds: string[] = [];

      if (sessionId) {
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        const data = await apiFetch(`/api/sessions/${sessionId}/events${q}`) as { events?: EventEntry[] };
        events = data?.events ?? [];
        sessionIds = [sessionId];
      } else {
        const searchParams: Record<string, string | number | undefined> = { status: 'Failed', limit: 5 };
        if (tenantId) searchParams.tenantId = tenantId;
        const searchQ = buildQuery(searchParams);
        const searchBase = tenantId ? '/api/search/sessions' : '/api/global/search/sessions';
        const sessions = await apiFetch(`${searchBase}${searchQ}`) as {
          sessions?: Array<{ sessionId?: string }>;
        };
        const ids = (sessions?.sessions ?? []).map((s) => s.sessionId).filter(Boolean) as string[];
        sessionIds = ids;
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        const allEvents = await Promise.all(
          ids.map(async (sid) => {
            try {
              const d = await apiFetch(`/api/sessions/${sid}/events${q}`) as { events?: EventEntry[] };
              return (d?.events ?? []).map((e) => ({ ...e, _sessionId: sid }));
            } catch { return []; }
          })
        );
        events = allEvents.flat();
      }

      const candidates = events.filter((e) => e.message && e.message.length > 5);

      if (candidates.length === 0) {
        return {
          content: [{
            type: 'text' as const,
            text: JSON.stringify({ query, resultCount: 0, results: [], note: 'No events with messages found.' }),
          }],
        };
      }

      // Build searchable documents from events
      const docs = candidates.map((e, i) => {
        const parts = [e.message];
        if (e.eventType) parts.push(`Event: ${e.eventType}`);
        if (e.severity) parts.push(`Severity: ${e.severity}`);
        if (e.source) parts.push(`Source: ${e.source}`);
        return {
          id: `event-${i}`,
          text: parts.join(' | '),
          metadata: { index: i } as Record<string, unknown>,
        };
      });

      // Create a temporary search provider for this batch of events
      const provider = await createSearchProvider();
      await provider.index(docs);
      const searchResults = await provider.search(query, { topK, minScore });

      const results = searchResults.map((r) => {
        const idx = r.metadata.index as number;
        const e = candidates[idx];
        return {
          score: Math.round(r.score * 1000) / 1000,
          sessionId: (e as Record<string, unknown>)._sessionId ?? sessionId,
          eventType: e.eventType,
          severity: e.severity,
          source: e.source,
          phase: e.phase,
          timestamp: e.timestamp,
          message: e.message,
        };
      });

      return {
        content: [{
          type: 'text' as const,
          text: JSON.stringify({
            query,
            searchBackend: provider.name,
            sessionsSearched: sessionIds,
            eventsAnalyzed: candidates.length,
            resultCount: results.length,
            results,
          }, null, 2),
        }],
      };
    }
  );

  // Tool 9: search_knowledge
  server.tool(
    'search_knowledge',
    'Semantic/fuzzy search over the Autopilot Monitor knowledge base: analysis rules, gather rules, and IME log patterns. ' +
    'Use natural language queries like "app install timeout", "BitLocker issues", "detection script failure". ' +
    'Returns the most relevant rules and patterns ranked by similarity. ' +
    'Great for finding remediation steps, understanding error patterns, or discovering relevant diagnostic rules.',
    {
      query: z.string().describe('Natural language search query (e.g. "app download timeout", "TPM not ready", "ESP stuck")'),
      topK: z.number().min(1).max(20).optional().default(5).describe('Number of results to return (1-20, default 5)'),
      type: z.enum(['all', 'analyze-rule', 'gather-rule', 'ime-log-pattern']).optional().default('all')
        .describe('Filter by document type. Default: search all types.'),
      minScore: z.number().min(0).max(1).optional().default(0.3)
        .describe('Minimum similarity score threshold (0-1, default 0.3). Lower = more results, higher = stricter matching.'),
    },
    async ({ query, topK, type, minScore }) => {
      if (!knowledgeBase || knowledgeBase.size === 0) {
        return {
          content: [{
            type: 'text' as const,
            text: JSON.stringify({ error: 'Knowledge base not initialized. The server may still be loading.' }),
          }],
        };
      }

      // Over-fetch when filtering by type, then trim
      const fetchK = type === 'all' ? topK : topK * 3;
      let results = await knowledgeBase.search(query, { topK: fetchK, minScore });

      if (type !== 'all') {
        results = results.filter((r) => r.metadata.type === type);
      }

      results = results.slice(0, topK);

      const formatted = results.map((r) => ({
        id: r.id,
        score: Math.round(r.score * 1000) / 1000,
        type: r.metadata.type,
        title: r.metadata.title ?? r.metadata.description ?? r.id,
        content: r.text,
        metadata: r.metadata,
      }));

      return {
        content: [{
          type: 'text' as const,
          text: JSON.stringify({
            query,
            searchBackend: knowledgeBase.name,
            resultCount: formatted.length,
            results: formatted,
          }, null, 2),
        }],
      };
    }
  );

  // Tool 10: get_api_usage
  server.tool(
    'get_api_usage',
    'Get API/MCP usage statistics. Shows request counts per endpoint per day. ' +
    'Use to monitor platform usage, identify heavy users, or debug rate limiting. Global Admin only.',
    {
      keyId: z.string().optional().describe('Specific API key ID to query usage for'),
      tenantId: z.string().optional().describe('Filter usage by tenant ID'),
      dateFrom: z.string().optional().describe('Start date (YYYY-MM-DD)'),
      dateTo: z.string().optional().describe('End date (YYYY-MM-DD)'),
      daily: z.boolean().optional().default(false).describe('Return daily aggregated summary instead of per-endpoint breakdown'),
    },
    async (args) => {
      try {
        let data: unknown;
        if (args.keyId) {
          const params: Record<string, string | undefined> = { dateFrom: args.dateFrom, dateTo: args.dateTo };
          data = await apiFetch(`/api/api-keys/${args.keyId}/usage${buildQuery(params)}`);
        } else if (args.daily) {
          const params: Record<string, string | undefined> = { tenantId: args.tenantId, dateFrom: args.dateFrom, dateTo: args.dateTo };
          data = await apiFetch(`/api/global/api-usage/daily${buildQuery(params)}`);
        } else {
          const params: Record<string, string | undefined> = { tenantId: args.tenantId, dateFrom: args.dateFrom, dateTo: args.dateTo };
          data = await apiFetch(`/api/global/api-usage${buildQuery(params)}`);
        }
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        const message = error instanceof Error ? error.message : String(error);
        if (message.includes('403')) {
          return { content: [{ type: 'text' as const, text: 'Access denied. This tool requires Global Admin permissions.' }] };
        }
        throw error;
      }
    }
  );

  // Tool 16: get_geographic_metrics
  server.tool(
    'get_geographic_metrics',
    'Get geographic distribution of enrollments — where devices are enrolling from, with performance comparisons. ' +
    'Shows per-location: session counts, success rates, avg/median/p95 duration, throughput, and outlier detection. ' +
    'Omit tenantId for cross-tenant view (Global Admin). Use get_geographic_sessions to drill into a specific location.',
    {
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant view (Global Admin only).'),
      days: z.number().optional().default(30).describe('Time range in days (default: 30)'),
      groupBy: z.enum(['country', 'region', 'city']).optional().default('city')
        .describe('Geographic grouping level (default: "city")'),
    },
    async ({ tenantId, ...rest }) => {
      const params: Record<string, string | number | undefined> = { ...rest };
      if (tenantId) params.tenantId = tenantId;
      const prefix = tenantId ? '/api/metrics' : '/api/global/metrics';
      const data = await apiFetch(`${prefix}/geographic${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 17: get_geographic_sessions
  server.tool(
    'get_geographic_sessions',
    'Drill into a specific geographic location from get_geographic_metrics. Returns all enrollment sessions at that location. ' +
    'Use the locationKey from get_geographic_metrics results.',
    {
      locationKey: z.string().describe('Location key from get_geographic_metrics results (e.g. "DE|Saxony|Falkenstein")'),
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant view (Global Admin only).'),
      days: z.number().optional().default(30).describe('Time range in days (default: 30)'),
      groupBy: z.enum(['country', 'region', 'city']).optional().default('city'),
    },
    async ({ tenantId, ...rest }) => {
      const params: Record<string, string | number | undefined> = { ...rest };
      if (tenantId) params.tenantId = tenantId;
      const prefix = tenantId ? '/api/metrics' : '/api/global/metrics';
      const data = await apiFetch(`${prefix}/geographic/sessions${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 18: get_platform_metrics
  server.tool(
    'get_platform_metrics',
    'Get platform-level agent performance metrics: per-session CPU usage, memory consumption, and network throughput. ' +
    'Global Admin only. Useful for monitoring agent health and identifying resource-heavy enrollments.',
    {},
    async () => {
      const data = await apiFetch('/api/global/metrics/platform');
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 19: get_usage_metrics
  server.tool(
    'get_usage_metrics',
    'Get platform usage statistics: active tenants, session volumes, feature adoption. ' +
    'Omit tenantId for platform-wide overview (Global Admin), or specify tenantId for tenant-specific usage.',
    {
      tenantId: z.string().optional().describe('Tenant ID for tenant-specific metrics. Omit for platform-wide overview (Global Admin only).'),
    },
    async ({ tenantId }) => {
      if (tenantId) {
        const data = await apiFetch(`/api/global/metrics/usage${buildQuery({ tenantId })}`);
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      }
      const data = await apiFetch('/api/global/metrics/usage');
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 20: get_audit_logs
  server.tool(
    'get_audit_logs',
    'Get audit trail of administrative actions: config changes, device blocks, user management, report submissions. ' +
    'Omit tenantId for cross-tenant audit log (Global Admin). Returns up to 100 most recent entries.',
    {
      tenantId: z.string().optional().describe('Tenant ID for tenant-scoped audit log. Omit for cross-tenant view (Global Admin only).'),
    },
    async ({ tenantId }) => {
      const endpoint = tenantId
        ? `/api/audit/logs${buildQuery({ tenantId })}`
        : '/api/global/audit/logs';
      const data = await apiFetch(endpoint);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 21: list_session_reports
  server.tool(
    'list_session_reports',
    'List session reports submitted by tenant admins. Reports contain user comments, screenshots, and agent logs for troubleshooting. ' +
    'Global Admin only — returns reports across all tenants.',
    {},
    async () => {
      const data = await apiFetch('/api/global/session-reports');
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // ── Phase 3.1: Raw Data Tools (Tier 1 — customer-facing) ──────────

  // Tool 11: query_raw_events
  server.tool(
    'query_raw_events',
    'Query raw enrollment events with flexible filters. Unlike get_session_events, this can query across sessions. ' +
    'Provide tenantId for cross-tenant access (Global Admin), or omit for your own tenant. ' +
    'Use sessionId for single-session events, or eventType for cross-session search. Returns raw event data.',
    {
      tenantId: z.string().optional().describe('Tenant ID (global-scoped key: required; tenant-scoped key: ignored)'),
      sessionId: z.string().optional().describe('Filter to a specific session'),
      eventType: z.string().optional().describe('Event type filter (e.g. "app_install_failed", "error_detected")'),
      severity: z.enum(['Info', 'Warning', 'Error', 'Critical']).optional(),
      source: z.string().optional().describe('Filter by event source/app name (substring match)'),
      startedAfter: z.string().optional().describe('ISO 8601 datetime — only events after this'),
      startedBefore: z.string().optional().describe('ISO 8601 datetime — only events before this'),
      limit: z.number().min(1).max(500).optional().default(100),
    },
    async (args) => {
      const { tenantId, ...rest } = args;
      const isGlobal = !!tenantId;
      const basePath = isGlobal ? '/api/global/raw/events' : '/api/raw/events';
      const params: Record<string, string | number | boolean | undefined | null> = { ...rest };
      if (isGlobal) params.tenantId = tenantId;
      const data = await apiFetch(`${basePath}${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 12: query_raw_sessions
  server.tool(
    'query_raw_sessions',
    'Query raw session data with flexible filters and field projection. ' +
    'Provide tenantId for cross-tenant access (Global Admin), or omit for your own tenant. ' +
    'Use the fields parameter to select specific properties (e.g. "sessionId,status,startedAt,serialNumber"). Returns raw session entities.',
    {
      tenantId: z.string().optional().describe('Tenant ID (global-scoped key: required; tenant-scoped key: ignored)'),
      status: z.enum(['InProgress', 'Succeeded', 'Failed']).optional(),
      startedAfter: z.string().optional().describe('ISO 8601 datetime'),
      startedBefore: z.string().optional().describe('ISO 8601 datetime'),
      serialNumber: z.string().optional(),
      fields: z.string().optional().describe('Comma-separated fields to return (e.g. "sessionId,status,startedAt,serialNumber,durationSeconds")'),
      limit: z.number().min(1).max(200).optional().default(50),
    },
    async (args) => {
      const { tenantId, ...rest } = args;
      const isGlobal = !!tenantId;
      const basePath = isGlobal ? '/api/global/raw/sessions' : '/api/raw/sessions';
      const params: Record<string, string | number | boolean | undefined | null> = { ...rest };
      if (isGlobal) params.tenantId = tenantId;
      const data = await apiFetch(`${basePath}${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // ── Phase 3.2: Admin Diagnostic Tools (Tier 2 — internal) ──────────

  // Tool 13: list_tables
  server.tool(
    'list_tables',
    'List all available Azure Table Storage tables that can be queried via query_table. Global Admin only.',
    {},
    async () => {
      try {
        const data = await apiFetch('/api/global/raw/tables');
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        const message = error instanceof Error ? error.message : String(error);
        if (message.includes('403')) {
          return { content: [{ type: 'text' as const, text: 'Access denied. This tool requires Global Admin permissions.' }] };
        }
        throw error;
      }
    }
  );

  // Tool 14: query_table
  server.tool(
    'query_table',
    'Query any Azure Table Storage table directly with OData filters. Global Admin only. ' +
    'Use list_tables to see available tables. Useful for inspecting TenantConfiguration, RuleResults, or any raw data.',
    {
      tableName: z.string().describe('Table name (e.g. "Sessions", "Events", "RuleResults", "TenantConfiguration")'),
      partitionKey: z.string().optional().describe('Filter by exact partition key (usually TenantId)'),
      rowKeyPrefix: z.string().optional().describe('Filter by row key prefix'),
      filter: z.string().optional().describe('OData filter expression (e.g. "Status eq \'Failed\'")'),
      limit: z.number().min(1).max(500).optional().default(100),
    },
    async (args) => {
      try {
        const { tableName, ...rest } = args;
        const data = await apiFetch(`/api/global/raw/tables/${encodeURIComponent(tableName)}${buildQuery(rest)}`);
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        const message = error instanceof Error ? error.message : String(error);
        if (message.includes('403')) {
          return { content: [{ type: 'text' as const, text: 'Access denied. This tool requires Global Admin permissions.' }] };
        }
        throw error;
      }
    }
  );

  // Tool 15: query_backend_logs
  server.tool(
    'query_backend_logs',
    'Query backend Application Insights logs using KQL. Global Admin only. ' +
    'Use for debugging backend issues, tracing requests by correlation ID, and platform diagnostics.',
    {
      query: z.string().describe('KQL query (e.g. "traces | where message contains \'error\' | take 50")'),
      timespan: z.string().optional().default('PT1H').describe('ISO 8601 duration (default: PT1H = last 1 hour). Examples: PT30M, PT6H, P1D'),
    },
    async (args) => {
      try {
        const data = await apiFetch('/api/global/raw/logs', {
          method: 'POST',
          body: JSON.stringify({ query: args.query, timespan: args.timespan }),
        });
        return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
      } catch (error: unknown) {
        const message = error instanceof Error ? error.message : String(error);
        if (message.includes('403')) {
          return { content: [{ type: 'text' as const, text: 'Access denied. This tool requires Global Admin permissions.' }] };
        }
        if (message.includes('503')) {
          return { content: [{ type: 'text' as const, text: 'Application Insights diagnostics is not configured. Set APPINSIGHTS_APP_ID and assign Monitoring Reader role to the Function App Managed Identity.' }] };
        }
        throw error;
      }
    }
  );
}
