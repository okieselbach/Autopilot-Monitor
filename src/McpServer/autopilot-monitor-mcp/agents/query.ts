/**
 * Autopilot-Monitor Agent
 *
 * Usage:
 *   npx tsx agents/query.ts "was ist die neueste bekannte IME Agent Version?"
 *   npx tsx agents/query.ts "welche Sessions haben IME Version 1.23.456?"
 *   npx tsx agents/query.ts "welche Monitor Agent Version hat Session abc-123?"
 *   npx tsx agents/query.ts "wie hoch ist die Failure Rate der letzten 30 Tage?"
 *   npx tsx agents/query.ts "welche Geräte haben Secure Boot deaktiviert?"
 *
 * Required env vars:
 *   ANTHROPIC_API_KEY   — Claude API key
 *   AUTOPILOT_API_URL   — Backend URL, e.g. https://autopilotmonitor-api.azurewebsites.net
 *   AUTOPILOT_API_KEY   — API key for authentication (Global Admin for cross-tenant queries)
 */

import Anthropic from '@anthropic-ai/sdk';

const BASE_URL = (process.env.AUTOPILOT_API_URL ?? '').replace(/\/$/, '');
const API_KEY  = process.env.AUTOPILOT_API_KEY ?? '';

if (!BASE_URL) { console.error('AUTOPILOT_API_URL is required'); process.exit(1); }
if (!API_KEY)  { console.error('AUTOPILOT_API_KEY is required'); process.exit(1); }
if (!process.env.ANTHROPIC_API_KEY) { console.error('ANTHROPIC_API_KEY is required'); process.exit(1); }

const question = process.argv.slice(2).join(' ');
if (!question) {
  console.error('Usage: npx tsx agents/query.ts "<question>"');
  process.exit(1);
}

// ---------------------------------------------------------------------------
// REST helpers
// ---------------------------------------------------------------------------

async function apiFetch(path: string): Promise<unknown> {
  const res = await fetch(`${BASE_URL}${path}`, {
    headers: { 'X-Api-Key': API_KEY, Accept: 'application/json' },
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`API ${res.status}: ${text.slice(0, 200)}`);
  }
  return res.json();
}

function qs(params: Record<string, string | number | boolean | undefined | null>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v != null && v !== '') p.set(k, String(v));
  }
  const s = p.toString();
  return s ? `?${s}` : '';
}

// ---------------------------------------------------------------------------
// Tool definitions (Claude sees these)
// ---------------------------------------------------------------------------

const TOOLS: Anthropic.Tool[] = [
  {
    name: 'search_sessions',
    description:
      'Search enrollment sessions. Supports paging via cursor. ' +
      'Returns sessions with agentVersion (Monitor Agent) and imeAgentVersion (IME Agent) fields. ' +
      'Call repeatedly with different cursors to page through all sessions for aggregation tasks. ' +
      'hasMore=true means more results are available — use the returned cursor for the next call.',
    input_schema: {
      type: 'object',
      properties: {
        status:         { type: 'string', enum: ['InProgress', 'Succeeded', 'Failed'] },
        serialNumber:   { type: 'string', description: 'Exact device serial number' },
        deviceName:     { type: 'string' },
        manufacturer:   { type: 'string', description: 'e.g. Microsoft, Dell, HP' },
        model:          { type: 'string' },
        osBuild:        { type: 'string' },
        enrollmentType: { type: 'string', enum: ['v1', 'v2'] },
        isPreProvisioned: { type: 'boolean' },
        isHybridJoin:   { type: 'boolean' },
        geoCountry:     { type: 'string', description: '2-letter ISO code e.g. DE, US' },
        startedAfter:   { type: 'string', description: 'ISO 8601 datetime' },
        startedBefore:  { type: 'string', description: 'ISO 8601 datetime' },
        agentVersion:   { type: 'string', description: 'Monitor Agent version exact match e.g. 1.4.2' },
        imeAgentVersion:{ type: 'string', description: 'IME Agent version exact match e.g. 1.23.456.789' },
        tpmSpecVersion: { type: 'string' },
        tpmActivated:   { type: 'boolean' },
        secureBootEnabled: { type: 'boolean' },
        bitlockerEnabled:  { type: 'boolean' },
        autopilotMode:  { type: 'string', description: 'e.g. UserDriven, SelfDeploying' },
        domainJoinMethod: { type: 'string', description: 'e.g. AzureAD, Hybrid, None' },
        connectionType: { type: 'string', enum: ['WiFi', 'Ethernet'] },
        minRamGB:       { type: 'number' },
        hasSSD:         { type: 'boolean' },
        tenantId:       { type: 'string', description: 'Filter by tenant (global API key only)' },
        limit:          { type: 'number', description: 'Max results 1-100, default 50' },
        cursor:         { type: 'string', description: 'Pagination cursor from previous response' },
      },
    },
  },
  {
    name: 'search_sessions_by_event',
    description:
      'Find sessions that contain a specific event type. ' +
      'Common values: app_install_failed, enrollment_failed, enrollment_complete, ' +
      'phase_transition, esp_failure, ime_agent_version, error_detected.',
    input_schema: {
      type: 'object',
      required: ['eventType'],
      properties: {
        eventType: { type: 'string' },
        tenantId:  { type: 'string' },
        limit:     { type: 'number' },
      },
    },
  },
  {
    name: 'get_session',
    description:
      'Get full details of a single session including agentVersion, imeAgentVersion, device metadata. ' +
      'Set includeAnalysis=true for failure explanations and remediation steps.',
    input_schema: {
      type: 'object',
      required: ['sessionId'],
      properties: {
        sessionId:       { type: 'string' },
        tenantId:        { type: 'string' },
        includeAnalysis: { type: 'boolean' },
      },
    },
  },
  {
    name: 'get_session_events',
    description:
      'Get the event timeline of a session. Filter by eventType, severity or source. ' +
      'Use eventType=ime_agent_version to find the IME version for older sessions that predate the ImeAgentVersion field.',
    input_schema: {
      type: 'object',
      required: ['sessionId'],
      properties: {
        sessionId: { type: 'string' },
        tenantId:  { type: 'string' },
        eventType: { type: 'string' },
        severity:  { type: 'string', enum: ['Info', 'Warning', 'Error', 'Critical'] },
        source:    { type: 'string' },
      },
    },
  },
  {
    name: 'get_metrics',
    description:
      'Aggregated metrics: failure rates, session counts, slowest/most-failing apps. ' +
      'Omit tenantId to get cross-tenant overview (requires global API key).',
    input_schema: {
      type: 'object',
      properties: {
        tenantId: { type: 'string' },
        days:     { type: 'number', enum: [7, 30, 90] },
      },
    },
  },
  {
    name: 'search_sessions_by_cve',
    description: 'Find sessions where a specific CVE was detected in the device software inventory.',
    input_schema: {
      type: 'object',
      required: ['cveId'],
      properties: {
        cveId:        { type: 'string', description: 'e.g. CVE-2024-21447' },
        tenantId:     { type: 'string' },
        minCvssScore: { type: 'number' },
        overallRisk:  { type: 'string', enum: ['low', 'medium', 'high', 'critical'] },
        limit:        { type: 'number' },
      },
    },
  },
];

// ---------------------------------------------------------------------------
// Tool execution
// ---------------------------------------------------------------------------

async function executeTool(name: string, input: Record<string, unknown>): Promise<string> {
  try {
    switch (name) {
      case 'search_sessions': {
        // Use global endpoint when tenantId not set (global API key scope)
        const endpoint = input.tenantId ? '/api/search/sessions' : '/api/global/search/sessions';
        const data = await apiFetch(`${endpoint}${qs(input as Record<string, string | number | boolean>)}`);
        return JSON.stringify(data, null, 2);
      }
      case 'search_sessions_by_event': {
        const endpoint = input.tenantId ? '/api/search/sessions-by-event' : '/api/global/search/sessions-by-event';
        const data = await apiFetch(`${endpoint}${qs(input as Record<string, string | number | boolean>)}`);
        return JSON.stringify(data, null, 2);
      }
      case 'get_session': {
        const { sessionId, tenantId, includeAnalysis } = input as Record<string, string | boolean>;
        const q = qs({ tenantId: tenantId as string });
        const session = await apiFetch(`/api/sessions/${sessionId}${q}`);
        if (!includeAnalysis) return JSON.stringify(session, null, 2);
        let analysis: unknown = null;
        try { analysis = await apiFetch(`/api/sessions/${sessionId}/analysis${q}`); } catch { /* optional */ }
        return JSON.stringify({ session, analysis }, null, 2);
      }
      case 'get_session_events': {
        const { sessionId, tenantId, ...filters } = input as Record<string, string>;
        const q = qs({ tenantId });
        const data = await apiFetch(`/api/sessions/${sessionId}/events${q}`) as {
          events?: Array<Record<string, unknown>>;
          count?: number;
        };
        // Client-side filter
        if (data?.events) {
          data.events = data.events.filter(e => {
            if (filters.eventType && e.eventType !== filters.eventType) return false;
            if (filters.severity && e.severity !== filters.severity) return false;
            if (filters.source && !String(e.source ?? '').toLowerCase().includes(filters.source.toLowerCase())) return false;
            return true;
          });
          data.count = data.events.length;
        }
        return JSON.stringify(data, null, 2);
      }
      case 'get_metrics': {
        const { tenantId, days } = input as { tenantId?: string; days?: number };
        const q = qs({ tenantId, days });
        const [summary, apps] = await Promise.all([
          apiFetch(`/api/global/metrics/summary${q}`).catch(() => null),
          apiFetch(`/api/global/metrics/app${q}`).catch(() => null),
        ]);
        return JSON.stringify({ summary, apps }, null, 2);
      }
      case 'search_sessions_by_cve': {
        const endpoint = input.tenantId ? '/api/search/sessions-by-cve' : '/api/global/search/sessions-by-cve';
        const data = await apiFetch(`${endpoint}${qs(input as Record<string, string | number | boolean>)}`);
        return JSON.stringify(data, null, 2);
      }
      default:
        return JSON.stringify({ error: `Unknown tool: ${name}` });
    }
  } catch (err) {
    return JSON.stringify({ error: String(err) });
  }
}

// ---------------------------------------------------------------------------
// Agent loop
// ---------------------------------------------------------------------------

const client = new Anthropic();

async function run() {
  const messages: Anthropic.MessageParam[] = [
    { role: 'user', content: question },
  ];

  console.error(`\nQuestion: ${question}\n`);

  for (let turn = 0; turn < 20; turn++) {
    const response = await client.messages.create({
      model: 'claude-opus-4-6',
      max_tokens: 4096,
      system:
        'You are an AI agent for Autopilot-Monitor, a system that monitors Windows Autopilot enrollment sessions. ' +
        'Answer the question by calling the available tools. Be thorough — page through results when needed to find aggregations. ' +
        'For "newest version" questions: call search_sessions repeatedly with cursor paging, collect all unique version values, return the highest. ' +
        'Answer in the same language as the question. Be concise and precise.',
      tools: TOOLS,
      messages,
    });

    // Collect text and tool-use blocks
    const toolUses = response.content.filter((b): b is Anthropic.ToolUseBlock => b.type === 'tool_use');
    const textBlocks = response.content.filter((b): b is Anthropic.TextBlock => b.type === 'text');

    if (response.stop_reason === 'end_turn' || toolUses.length === 0) {
      const answer = textBlocks.map(b => b.text).join('');
      console.log(answer);
      return;
    }

    // Log which tools are being called
    for (const tool of toolUses) {
      console.error(`  → ${tool.name}(${JSON.stringify(tool.input).slice(0, 120)})`);
    }

    // Add assistant turn
    messages.push({ role: 'assistant', content: response.content });

    // Execute all tool calls in parallel
    const results = await Promise.all(
      toolUses.map(async (tool) => ({
        type: 'tool_result' as const,
        tool_use_id: tool.id,
        content: await executeTool(tool.name, tool.input as Record<string, unknown>),
      }))
    );

    messages.push({ role: 'user', content: results });
  }

  console.error('Max turns reached');
}

run().catch(err => { console.error(err); process.exit(1); });
