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
      limit: z.coerce.number().min(1).max(100).optional().default(50).describe('Maximum number of results (1-100, default 50)'),
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
      limit: z.coerce.number().min(1).max(100).optional().default(50),
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
      tenantId: z.string().optional().describe('Tenant ID. If omitted, auto-resolved from the session (Global Admin can access any tenant).'),
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
    'Get the event timeline for a session. Filter by eventType, severity, or source (app name) to focus on relevant events. ' +
    'Useful for root cause analysis of failures. ' +
    'If you omit tenantId, the backend auto-resolves it from the session (Global Admin can access any tenant).',
    {
      sessionId: z.string().describe('Session UUID'),
      tenantId: z.string().optional().describe('Tenant ID. If omitted, auto-resolved from the session.'),
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

  // Tool 5: get_session_summary
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
    async ({ sessionId, tenantId }) => {
      const q = buildQuery({ tenantId } as Record<string, string | undefined>);
      const fetchOpts = { signal: AbortSignal.timeout(90_000) };

      const [sessionData, eventsData, analysisData] = await Promise.all([
        apiFetch(`/api/sessions/${sessionId}${q}`, fetchOpts) as Promise<Record<string, unknown>>,
        apiFetch(`/api/sessions/${sessionId}/events${q}`, fetchOpts) as Promise<{ events?: Array<Record<string, unknown>>; count?: number }>,
        apiFetch(`/api/sessions/${sessionId}/analysis${q}`, fetchOpts).catch(() => null) as Promise<Record<string, unknown> | null>,
      ]);

      // Unwrap session from response envelope if needed
      const s = (sessionData.session ?? sessionData) as Record<string, unknown>;

      // Build overview
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

      // Filter events
      const allEvents = (eventsData?.events ?? []) as Array<Record<string, unknown>>;

      // Compute stats from full event list
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

      // Filter to key events
      let keyEvents = allEvents.filter((e) => {
        const et = String(e.eventType ?? '');
        if (EXCLUDED_EVENT_TYPES.has(et)) return false;
        if (KEY_EVENT_TYPES.has(et)) return true;
        return (SEVERITY_RANK[String(e.severity ?? '')] ?? -1) >= 2; // Warning+
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

      // Build analysis
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
    }
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
    async (args) => {
      const { tenantId, ...rest } = args;
      const params: Record<string, string | number | boolean | undefined | null> = { ...rest };
      if (tenantId) params.tenantId = tenantId;
      const basePath = tenantId ? '/api/search/sessions-by-cve' : '/api/global/search/sessions-by-cve';
      const data = await apiFetch(`${basePath}${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 8: list_blocked_devices
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

  // Tool 9: search_events_semantic
  server.tool(
    'search_events_semantic',
    'Semantic/fuzzy search over enrollment event messages within a session or across recent failed sessions. ' +
    'Unlike exact filters, this finds events by MEANING — e.g. "network timeout" also matches "connection timed out", "request failed after waiting". ' +
    'Use this when you need to find events that match a symptom description rather than an exact event type. ' +
    'Provide a sessionId to search within one session, or omit it to search across recent failed sessions.',
    {
      query: z.string().describe('Natural language description of what to find (e.g. "app download stuck", "certificate error", "disk space low")'),
      sessionId: z.string().optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
      tenantId: z.string().optional().describe('Tenant ID. Required for non-Global Admin users; Global Admin can omit to search across tenants.'),
      topK: z.coerce.number().min(1).max(30).optional().default(10).describe('Number of matching events to return (1-30, default 10)'),
      minScore: z.coerce.number().min(0).max(1).optional().default(0.35)
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

  // Tool 10: search_knowledge
  server.tool(
    'search_knowledge',
    'Semantic/fuzzy search over the Autopilot Monitor knowledge base: analysis rules, gather rules, and IME log patterns. ' +
    'Use natural language queries like "app install timeout", "BitLocker issues", "detection script failure". ' +
    'Returns the most relevant rules and patterns ranked by similarity. ' +
    'Great for finding remediation steps, understanding error patterns, or discovering relevant diagnostic rules.',
    {
      query: z.string().describe('Natural language search query (e.g. "app download timeout", "TPM not ready", "ESP stuck")'),
      topK: z.coerce.number().min(1).max(20).optional().default(5).describe('Number of results to return (1-20, default 5)'),
      type: z.enum(['all', 'analyze-rule', 'gather-rule', 'ime-log-pattern']).optional().default('all')
        .describe('Filter by document type. Default: search all types.'),
      minScore: z.coerce.number().min(0).max(1).optional().default(0.3)
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

  // Tool 11: get_api_usage
  server.tool(
    'get_api_usage',
    'Get API/MCP usage statistics. Shows request counts per endpoint per day. ' +
    'Use to monitor platform usage, identify heavy users, or debug rate limiting. ' +
    'Use userId for a specific user, daily for aggregated summaries, or neither for global per-record breakdown.',
    {
      userId: z.string().optional().describe('Specific user object ID to query usage for'),
      tenantId: z.string().optional().describe('Filter usage by tenant ID'),
      dateFrom: z.string().optional().describe('Start date (YYYY-MM-DD)'),
      dateTo: z.string().optional().describe('End date (YYYY-MM-DD)'),
      daily: z.boolean().optional().default(false).describe('Return daily aggregated summary instead of per-endpoint breakdown'),
    },
    async (args) => {
      try {
        let data: unknown;
        const params: Record<string, string | undefined> = { tenantId: args.tenantId, dateFrom: args.dateFrom, dateTo: args.dateTo };
        if (args.userId) {
          data = await apiFetch(`/api/metrics/mcp-usage/${encodeURIComponent(args.userId)}${buildQuery(params)}`);
        } else if (args.daily) {
          data = await apiFetch(`/api/global/metrics/mcp-usage/daily${buildQuery(params)}`);
        } else {
          data = await apiFetch(`/api/global/metrics/mcp-usage${buildQuery(params)}`);
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

  // Tool 12: get_geographic_metrics
  server.tool(
    'get_geographic_metrics',
    'Get geographic distribution of enrollments — where devices are enrolling from, with performance comparisons. ' +
    'Shows per-location: session counts, success rates, avg/median/p95 duration, throughput, and outlier detection. ' +
    'Omit tenantId for cross-tenant view (Global Admin). Use get_geographic_sessions to drill into a specific location.',
    {
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant view (Global Admin only).'),
      days: z.coerce.number().optional().default(30).describe('Time range in days (default: 30)'),
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

  // Tool 13: get_geographic_sessions
  server.tool(
    'get_geographic_sessions',
    'Drill into a specific geographic location. Returns all enrollment sessions at that location. ' +
    'Either provide locationKey (from get_geographic_metrics results, e.g. "Falkenstein, Saxony, DE") ' +
    'or use country/region/city filters to find sessions by location.',
    {
      locationKey: z.string().optional().describe('Location key from get_geographic_metrics (e.g. "Falkenstein, Saxony, DE"). If provided, country/region/city are ignored.'),
      country: z.string().optional().describe('2-letter country code filter (e.g. "DE", "US", "CH"). Used when locationKey is not provided.'),
      region: z.string().optional().describe('Region/state filter (e.g. "Saxony", "North Carolina"). Used with country.'),
      city: z.string().optional().describe('City filter (e.g. "Falkenstein"). Used with country.'),
      tenantId: z.string().optional().describe('Tenant ID. Omit for cross-tenant view (Global Admin only).'),
      days: z.coerce.number().optional().default(30).describe('Time range in days (default: 30)'),
    },
    async ({ tenantId, country, region, city, ...rest }) => {
      const params: Record<string, string | number | undefined> = { ...rest };
      if (tenantId) params.tenantId = tenantId;
      // Build locationKey from country/region/city if not explicitly provided
      if (!params.locationKey && country) {
        const parts = [city, region, country].filter(Boolean);
        params.locationKey = parts.join(', ');
        params.groupBy = city ? 'city' : region ? 'region' : 'country';
      }
      const prefix = tenantId ? '/api/metrics' : '/api/global/metrics';
      const data = await apiFetch(`${prefix}/geographic/sessions${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 14: get_platform_metrics
  server.tool(
    'get_platform_metrics',
    'Get aggregated platform-level agent performance metrics across recent sessions. ' +
    'Returns: avg/max/p95 CPU, memory (working set, private bytes), network (bytes up/down, latency, requests), ' +
    'top sessions by CPU/memory, and per-agent-version breakdown. Global Admin only.',
    {},
    async () => {
      type SessionMetric = {
        sessionId: string; tenantId: string; deviceName?: string; model?: string; status?: string;
        agentVersion?: string; snapshotCount: number;
        avgCpu: number; maxCpu: number; avgWorkingSet: number; maxWorkingSet: number;
        avgPrivateBytes: number; avgLatency: number;
        totalBytesUp: number; totalBytesDown: number; totalRequests: number;
      };
      const raw = await apiFetch('/api/global/metrics/platform') as { sessions?: SessionMetric[] };
      const sessions = raw?.sessions ?? [];
      if (sessions.length === 0) {
        return { content: [{ type: 'text' as const, text: JSON.stringify({ sessionsAnalyzed: 0, message: 'No performance data available' }) }] };
      }

      const avg = (arr: number[]) => arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : 0;
      const p95 = (arr: number[]) => { const s = [...arr].sort((a, b) => a - b); return s[Math.floor(s.length * 0.95)] ?? 0; };
      const round = (n: number) => Math.round(n * 100) / 100;

      const cpus = sessions.map(s => s.avgCpu);
      const maxCpus = sessions.map(s => s.maxCpu);
      const ws = sessions.map(s => s.avgWorkingSet);
      const pb = sessions.map(s => s.avgPrivateBytes);
      const lat = sessions.filter(s => s.avgLatency > 0).map(s => s.avgLatency);

      // Top 5 by CPU
      const topCpu = [...sessions].sort((a, b) => b.maxCpu - a.maxCpu).slice(0, 5).map(s => ({
        sessionId: s.sessionId, device: s.deviceName, model: s.model, maxCpu: round(s.maxCpu), avgCpu: round(s.avgCpu),
      }));

      // Top 5 by memory
      const topMem = [...sessions].sort((a, b) => b.avgWorkingSet - a.avgWorkingSet).slice(0, 5).map(s => ({
        sessionId: s.sessionId, device: s.deviceName, model: s.model, avgWorkingSetMB: round(s.avgWorkingSet),
      }));

      // Per agent version
      const byVersion: Record<string, { count: number; avgCpu: number[]; avgMem: number[] }> = {};
      for (const s of sessions) {
        const v = s.agentVersion ?? 'unknown';
        if (!byVersion[v]) byVersion[v] = { count: 0, avgCpu: [], avgMem: [] };
        byVersion[v].count++;
        byVersion[v].avgCpu.push(s.avgCpu);
        byVersion[v].avgMem.push(s.avgWorkingSet);
      }
      const versionBreakdown = Object.entries(byVersion).map(([version, d]) => ({
        version, sessions: d.count, avgCpu: round(avg(d.avgCpu)), avgMemMB: round(avg(d.avgMem)),
      })).sort((a, b) => b.sessions - a.sessions);

      const summary = {
        sessionsAnalyzed: sessions.length,
        cpu: { avgPercent: round(avg(cpus)), maxPercent: round(Math.max(...maxCpus)), p95Percent: round(p95(maxCpus)) },
        memory: {
          avgWorkingSetMB: round(avg(ws)), maxWorkingSetMB: round(Math.max(...ws)), p95WorkingSetMB: round(p95(ws)),
          avgPrivateBytesMB: round(avg(pb)),
        },
        network: {
          totalBytesUp: sessions.reduce((a, s) => a + s.totalBytesUp, 0),
          totalBytesDown: sessions.reduce((a, s) => a + s.totalBytesDown, 0),
          totalRequests: sessions.reduce((a, s) => a + s.totalRequests, 0),
          avgLatencyMs: round(avg(lat)),
        },
        topSessionsByCpu: topCpu,
        topSessionsByMemory: topMem,
        agentVersionBreakdown: versionBreakdown,
      };
      return { content: [{ type: 'text' as const, text: JSON.stringify(summary, null, 2) }] };
    }
  );

  // Tool 15: get_usage_metrics
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

  // Tool 16: get_audit_logs
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

  // Tool 17: list_session_reports
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

  // Tool 18: query_raw_events
  server.tool(
    'query_raw_events',
    'Query raw enrollment events with flexible filters. Unlike get_session_events, this can query across sessions within a tenant. ' +
    'tenantId is always required (events are partitioned per tenant). Global Admin can query any tenant. ' +
    'Use sessionId for single-session events, or eventType for cross-session search. Returns raw event data.',
    {
      tenantId: z.string().describe('Tenant ID to query (required). Global Admin can query any tenant.'),
      sessionId: z.string().optional().describe('Filter to a specific session'),
      eventType: z.string().optional().describe('Event type filter (e.g. "app_install_failed", "error_detected")'),
      severity: z.enum(['Info', 'Warning', 'Error', 'Critical']).optional(),
      source: z.string().optional().describe('Filter by event source/app name (substring match)'),
      startedAfter: z.string().optional().describe('ISO 8601 datetime — only events after this'),
      startedBefore: z.string().optional().describe('ISO 8601 datetime — only events before this'),
      limit: z.coerce.number().min(1).max(500).optional().default(100),
    },
    async (args) => {
      const { tenantId, ...rest } = args;
      const params: Record<string, string | number | boolean | undefined | null> = { ...rest, tenantId };
      const data = await apiFetch(`/api/raw/events${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // Tool 19: query_raw_sessions
  server.tool(
    'query_raw_sessions',
    'Query raw session data with flexible filters and field projection. ' +
    'Specify tenantId for a specific tenant, or omit for cross-tenant access (Global Admin only). ' +
    'Use the fields parameter to select specific properties (e.g. "sessionId,status,startedAt,serialNumber"). Returns raw session entities.',
    {
      tenantId: z.string().optional().describe('Tenant ID to query. Omit for cross-tenant access (Global Admin only).'),
      status: z.enum(['InProgress', 'Succeeded', 'Failed']).optional(),
      startedAfter: z.string().optional().describe('ISO 8601 datetime'),
      startedBefore: z.string().optional().describe('ISO 8601 datetime'),
      serialNumber: z.string().optional(),
      fields: z.string().optional().describe('Comma-separated fields to return (e.g. "sessionId,status,startedAt,serialNumber,durationSeconds")'),
      limit: z.coerce.number().min(1).max(200).optional().default(50),
    },
    async (args) => {
      const { tenantId, ...rest } = args;
      const params: Record<string, string | number | boolean | undefined | null> = { ...rest };
      // Tenant-scoped → /api/raw/sessions?tenantId=X; cross-tenant → /api/global/raw/sessions
      const basePath = tenantId ? '/api/raw/sessions' : '/api/global/raw/sessions';
      if (tenantId) params.tenantId = tenantId;
      const data = await apiFetch(`${basePath}${buildQuery(params)}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }] };
    }
  );

  // ── Phase 3.2: Admin Diagnostic Tools (Tier 2 — internal) ──────────

  // Tool 20: list_tables
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

  // Tool 21: query_table
  server.tool(
    'query_table',
    'Query any Azure Table Storage table directly with OData filters. Global Admin only. ' +
    'Use list_tables to see available tables. Useful for inspecting TenantConfiguration, RuleResults, or any raw data.',
    {
      tableName: z.string().describe('Table name (e.g. "Sessions", "Events", "RuleResults", "TenantConfiguration")'),
      partitionKey: z.string().optional().describe('Filter by exact partition key (usually TenantId)'),
      rowKeyPrefix: z.string().optional().describe('Filter by row key prefix'),
      filter: z.string().optional().describe('OData filter expression (e.g. "Status eq \'Failed\'")'),
      limit: z.coerce.number().min(1).max(500).optional().default(100),
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

  // Tool 22: query_backend_logs
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
