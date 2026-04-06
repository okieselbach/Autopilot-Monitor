import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, buildQuery } from '../client.js';
import type { SearchProvider } from '../search-provider.js';
import { READ_ONLY } from './shared.js';
import { toolError } from './error-handler.js';

// ── Helpers ─────────────────────────────────────────────────────────────

type EventEntry = {
  eventType?: string; severity?: string; source?: string;
  message?: string; timestamp?: string; phase?: string;
  data?: Record<string, unknown>; _sessionId?: string;
};

/** Fetch events for a single session or across recent failed sessions. */
async function fetchSessionEvents(
  sessionId: string | undefined,
  tenantId: string | undefined,
): Promise<{ events: EventEntry[]; sessionIds: string[] }> {
  if (sessionId) {
    const q = buildQuery({ tenantId } as Record<string, string | undefined>);
    const data = await apiFetch(`/api/sessions/${sessionId}/events${q}`) as { events?: EventEntry[] };
    return { events: data?.events ?? [], sessionIds: [sessionId] };
  }
  const searchParams: Record<string, string | number | undefined> = { status: 'Failed', limit: 5 };
  if (tenantId) searchParams.tenantId = tenantId;
  const searchQ = buildQuery(searchParams);
  const searchBase = tenantId ? '/api/search/sessions' : '/api/global/search/sessions';
  const sessions = await apiFetch(`${searchBase}${searchQ}`) as {
    sessions?: Array<{ sessionId?: string }>;
  };
  const ids = (sessions?.sessions ?? []).map((s) => s.sessionId).filter(Boolean) as string[];
  const q = buildQuery({ tenantId } as Record<string, string | undefined>);
  const allEvents = await Promise.all(
    ids.map(async (sid) => {
      try {
        const d = await apiFetch(`/api/sessions/${sid}/events${q}`) as { events?: EventEntry[] };
        return (d?.events ?? []).map((e) => ({ ...e, _sessionId: sid }));
      } catch { return [] as EventEntry[]; }
    })
  );
  return { events: allEvents.flat(), sessionIds: ids };
}

const KEYWORD_STOP_WORDS = new Set([
  'the', 'a', 'an', 'is', 'are', 'was', 'were', 'be', 'been', 'being',
  'have', 'has', 'had', 'do', 'does', 'did', 'will', 'would', 'could',
  'should', 'may', 'might', 'can', 'shall', 'to', 'of', 'in', 'for',
  'on', 'with', 'at', 'by', 'from', 'as', 'into', 'through', 'during',
  'before', 'after', 'above', 'below', 'between', 'out', 'off', 'over',
  'under', 'again', 'further', 'then', 'once', 'and', 'but', 'or', 'nor',
  'not', 'so', 'yet', 'both', 'either', 'neither', 'each', 'every', 'all',
  'any', 'few', 'more', 'most', 'other', 'some', 'such', 'no', 'only',
  'own', 'same', 'than', 'too', 'very', 'just', 'about', 'also', 'it',
  'its', 'this', 'that', 'these', 'those', 'what', 'which', 'who', 'whom',
  'how', 'when', 'where', 'why', 'find', 'search', 'show', 'get', 'events',
  'event', 'check', 'look', 'see',
]);

/** Short domain-specific terms that bypass the length filter. */
const DOMAIN_SHORT_KEYWORDS = new Set(['do', 'os', 'ad', 'ip', 'id']);

/** Multi-word domain phrases mapped to their technical search terms. */
const DOMAIN_SYNONYMS: [RegExp, string][] = [
  [/\bdelivery\s+optimization\b/gi, 'do_telemetry'],
  [/\bactive\s+directory\b/gi, 'aad'],
  [/\benrollment\s+status\s+page\b/gi, 'esp'],
  [/\bwindows\s+installer\b/gi, 'msi'],
  [/\bintune\s+management\s+extension\b/gi, 'ime'],
];

/** Expand domain synonyms in the query before keyword extraction. */
function expandSynonyms(query: string): string {
  let expanded = query;
  for (const [pattern, replacement] of DOMAIN_SYNONYMS) {
    expanded = expanded.replace(pattern, `${replacement} $&`);
  }
  return expanded;
}

/** Extract meaningful keywords from a natural language query. */
function extractKeywords(query: string): string[] {
  return expandSynonyms(query)
    .toLowerCase()
    .replace(/[^\w\s-]/g, ' ')
    .split(/\s+/)
    .filter((w) => (w.length > 2 || DOMAIN_SHORT_KEYWORDS.has(w)) && (DOMAIN_SHORT_KEYWORDS.has(w) || !KEYWORD_STOP_WORDS.has(w)));
}

// ── Weighted keyword scoring ────────────────────────────────────────────

const MIN_PREFIX_LEN = 4;

/** Check if keyword matches text via substring or shared prefix (min 4 chars). */
function prefixAwareMatch(text: string, keyword: string): boolean {
  if (text.includes(keyword)) return true;
  // Split text into words and check shared prefix
  const words = text.split(/[\s_\-.:,/]+/);
  for (const word of words) {
    if (word.length < MIN_PREFIX_LEN || keyword.length < MIN_PREFIX_LEN) continue;
    const prefixLen = Math.min(word.length, keyword.length, MIN_PREFIX_LEN + 2);
    if (word.slice(0, prefixLen) === keyword.slice(0, prefixLen)) return true;
  }
  return false;
}

/** Field weights for scoring — eventType is the most discriminating field. */
const FIELD_WEIGHTS = {
  eventType: 3.0,
  message: 2.0,
  source: 1.5,
  severity: 1.0,
  data: 0.5,
} as const;

type ScoredEvent = {
  index: number;
  score: number;
  matchedKeywords: string[];
  bestFields: string[];
};

/** Score an event against query keywords with weighted field matching. */
function scoreEvent(e: EventEntry, queryKeywords: string[]): ScoredEvent | null {
  const fields: Array<{ name: string; text: string; weight: number }> = [
    { name: 'eventType', text: (e.eventType ?? '').toLowerCase(), weight: FIELD_WEIGHTS.eventType },
    { name: 'message', text: (e.message ?? '').toLowerCase(), weight: FIELD_WEIGHTS.message },
    { name: 'source', text: (e.source ?? '').toLowerCase(), weight: FIELD_WEIGHTS.source },
    { name: 'severity', text: (e.severity ?? '').toLowerCase(), weight: FIELD_WEIGHTS.severity },
    { name: 'data', text: e.data ? JSON.stringify(e.data).toLowerCase() : '', weight: FIELD_WEIGHTS.data },
  ];

  let totalScore = 0;
  const matched: string[] = [];
  const bestFields = new Set<string>();

  for (const kw of queryKeywords) {
    let kwBestWeight = 0;
    let kwBestField = '';
    for (const field of fields) {
      if (field.text && prefixAwareMatch(field.text, kw)) {
        if (field.weight > kwBestWeight) {
          kwBestWeight = field.weight;
          kwBestField = field.name;
        }
      }
    }
    if (kwBestWeight > 0) {
      totalScore += kwBestWeight;
      matched.push(kw);
      bestFields.add(kwBestField);
    }
  }

  if (matched.length === 0) return null;

  // Normalize: max possible = all keywords matching in eventType (weight 3.0)
  const maxPossible = queryKeywords.length * FIELD_WEIGHTS.eventType;
  const normalizedScore = totalScore / maxPossible;

  // Bonus for matching MORE keywords (coverage matters)
  const coverageBonus = (matched.length / queryKeywords.length) * 0.2;

  return {
    index: 0, // set by caller
    score: Math.min(normalizedScore + coverageBonus, 1.0),
    matchedKeywords: matched,
    bestFields: Array.from(bestFields),
  };
}

// ── Registration ────────────────────────────────────────────────────────

export function registerSearchTools(server: McpServer, knowledgeBase?: SearchProvider): void {
  // Tool 9: search_events_semantic — weighted keyword search
  server.tool(
    'search_events_semantic',
    'TIER 1 — FAST EVENT SEARCH (try this first). ' +
    'Searches enrollment events by matching keywords against event type, message, source, severity, and data fields. ' +
    'Uses prefix-aware matching (e.g. "install" matches "installation", "installed", "app_install_failed") ' +
    'and weighted field scoring (matches in eventType rank higher than in data). ' +
    'Provide sessionId to search within one session, or omit to search across recent failed sessions. ' +
    'If results seem incomplete, escalate to deep_search_events for exhaustive coverage.',
    {
      query: z.string().describe('Natural language description of what to find (e.g. "app download stuck", "certificate error", "disk space low")'),
      sessionId: z.string().optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
      tenantId: z.string().optional().describe('Tenant ID. Required for non-Global Admin users; Global Admin can omit to search across tenants.'),
      topK: z.coerce.number().min(1).max(30).optional().default(10).describe('Number of matching events to return (1-30, default 10)'),
      minScore: z.coerce.number().min(0).max(1).optional().default(0.1)
        .describe('Minimum relevance score (0-1, default 0.1). Events matching at least one keyword in any field pass this threshold.'),
    },
    READ_ONLY,
    async (args) => {
      try {
        const { query, sessionId, tenantId, topK, minScore } = args;
        const { events, sessionIds } = await fetchSessionEvents(sessionId, tenantId);

        const queryKeywords = extractKeywords(query);
        if (queryKeywords.length === 0) {
          return {
            content: [{
              type: 'text' as const,
              text: JSON.stringify({ query, resultCount: 0, results: [], note: 'No searchable keywords extracted from query.' }),
            }],
          };
        }

        const scored: Array<ScoredEvent & { event: EventEntry }> = [];
        for (let i = 0; i < events.length; i++) {
          const result = scoreEvent(events[i], queryKeywords);
          if (result && result.score >= minScore) {
            scored.push({ ...result, index: i, event: events[i] });
          }
        }

        scored.sort((a, b) => b.score - a.score);
        const results = scored.slice(0, topK).map((s) => ({
          score: Math.round(s.score * 1000) / 1000,
          matchedKeywords: s.matchedKeywords,
          bestFields: s.bestFields,
          sessionId: s.event._sessionId ?? sessionId,
          eventType: s.event.eventType,
          severity: s.event.severity,
          source: s.event.source,
          phase: s.event.phase,
          timestamp: s.event.timestamp,
          message: s.event.message,
        }));

        return {
          content: [{
            type: 'text' as const,
            text: JSON.stringify({
              query,
              searchBackend: 'weighted-keyword',
              keywordsUsed: queryKeywords,
              sessionsSearched: sessionIds,
              eventsScanned: events.length,
              eventsMatched: scored.length,
              resultCount: results.length,
              results,
            }, null, 2),
          }],
        };
      } catch (error: unknown) {
        return toolError('search_events_semantic', args, error);
      }
    }
  );

  // Tool 10: search_knowledge (unchanged — vector, pre-indexed at startup)
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
    READ_ONLY,
    async (args) => {
      try {
        const { query, topK, type, minScore } = args;
        if (!knowledgeBase || knowledgeBase.size === 0) {
          return {
            isError: true,
            content: [{
              type: 'text' as const,
              text: JSON.stringify({ error: 'Knowledge base not initialized. The server may still be loading.' }),
            }],
          };
        }

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
      } catch (error: unknown) {
        return toolError('search_knowledge', args, error);
      }
    }
  );

  // Tool 23: deep_search_events — same scoring + exhaustive data scan
  server.tool(
    'deep_search_events',
    'TIER 3 — DEEP SEARCH (thorough, use when accuracy is critical). ' +
    'Uses the same weighted keyword scoring as search_events_semantic but with lower thresholds ' +
    'and higher result limits for maximum recall. Searches ALL event fields including full DataJson content. ' +
    'Results include matched keywords and which fields they were found in. ' +
    'Use this when: (1) a previous search may have missed events, (2) you need high confidence in completeness, ' +
    'or (3) you want to see which specific fields matched. ' +
    'Provide sessionId to search within one session, or omit to search across recent failed sessions. ' +
    'Omit tenantId for cross-tenant search (Global Admin), or specify tenantId for single-tenant.',
    {
      query: z.string().describe('Natural language description of what to find (e.g. "app download stuck", "certificate error", "disk space low")'),
      sessionId: z.string().optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
      tenantId: z.string().optional().describe('Tenant ID. Required for non-Global Admin users; Global Admin can omit to search across tenants.'),
      topK: z.coerce.number().min(1).max(50).optional().default(20)
        .describe('Max results to return (1-50, default 20). Higher default for thoroughness.'),
      minScore: z.coerce.number().min(0).max(1).optional().default(0.05)
        .describe('Min relevance score (0-1, default 0.05). Very low for maximum recall.'),
      keywords: z.array(z.string()).optional()
        .describe('Additional exact keywords for matching. Auto-extracted from query if omitted.'),
    },
    READ_ONLY,
    async (args) => {
      try {
        const { query, sessionId, tenantId, topK, minScore, keywords } = args;
        const { events, sessionIds } = await fetchSessionEvents(sessionId, tenantId);

        if (events.length === 0) {
          return {
            content: [{
              type: 'text' as const,
              text: JSON.stringify({
                query, resultCount: 0, eventsMatched: 0,
                results: [], note: 'No events found.',
              }),
            }],
          };
        }

        const queryKeywords = keywords ?? extractKeywords(query);
        if (queryKeywords.length === 0) {
          return {
            content: [{
              type: 'text' as const,
              text: JSON.stringify({ query, resultCount: 0, results: [], note: 'No searchable keywords extracted from query.' }),
            }],
          };
        }

        const scored: Array<ScoredEvent & { event: EventEntry }> = [];
        for (let i = 0; i < events.length; i++) {
          const result = scoreEvent(events[i], queryKeywords);
          if (result && result.score >= minScore) {
            scored.push({ ...result, index: i, event: events[i] });
          }
        }

        scored.sort((a, b) => b.score - a.score);
        const results = scored.slice(0, topK).map((s) => ({
          score: Math.round(s.score * 1000) / 1000,
          matchedKeywords: s.matchedKeywords,
          bestFields: s.bestFields,
          sessionId: s.event._sessionId ?? sessionId,
          eventType: s.event.eventType,
          severity: s.event.severity,
          source: s.event.source,
          phase: s.event.phase,
          timestamp: s.event.timestamp,
          message: s.event.message,
        }));

        return {
          content: [{
            type: 'text' as const,
            text: JSON.stringify({
              query,
              searchBackend: 'weighted-keyword',
              keywordsUsed: queryKeywords,
              sessionsSearched: sessionIds,
              totalEventsScanned: events.length,
              eventsMatched: scored.length,
              resultCount: results.length,
              results,
            }, null, 2),
          }],
        };
      } catch (error: unknown) {
        return toolError('deep_search_events', args, error);
      }
    }
  );
}
