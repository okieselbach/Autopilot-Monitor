import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, buildQuery } from '../client.js';
import type { SearchProvider } from '../search-provider.js';
import { createSearchProvider } from '../search-factory.js';
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

/** Extract meaningful keywords from a natural language query. */
function extractKeywords(query: string): string[] {
  return query
    .toLowerCase()
    .replace(/[^\w\s-]/g, ' ')
    .split(/\s+/)
    .filter((w) => w.length > 2 && !KEYWORD_STOP_WORDS.has(w));
}

// ── Registration ────────────────────────────────────────────────────────

export function registerSearchTools(server: McpServer, knowledgeBase?: SearchProvider): void {
  // Tool 9: search_events_semantic
  server.tool(
    'search_events_semantic',
    'TIER 1 — FAST SEMANTIC SEARCH (try this first). ' +
    'Semantic/fuzzy search over enrollment event messages within a session or across recent failed sessions. ' +
    'Finds events by MEANING — e.g. "network timeout" also matches "connection timed out", "request failed after waiting". ' +
    'Use this when you need to find events matching a symptom description rather than an exact event type. ' +
    'Provide sessionId to search within one session, or omit to search across recent failed sessions. ' +
    'If results seem incomplete or you need guaranteed completeness, escalate to deep_search_events.',
    {
      query: z.string().describe('Natural language description of what to find (e.g. "app download stuck", "certificate error", "disk space low")'),
      sessionId: z.string().optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
      tenantId: z.string().optional().describe('Tenant ID. Required for non-Global Admin users; Global Admin can omit to search across tenants.'),
      topK: z.coerce.number().min(1).max(30).optional().default(10).describe('Number of matching events to return (1-30, default 10)'),
      minScore: z.coerce.number().min(0).max(1).optional().default(0.35)
        .describe('Minimum similarity score (0-1, default 0.35)'),
    },
    READ_ONLY,
    async (args) => {
      try {
        const { query, sessionId, tenantId, topK, minScore } = args;
        const { events, sessionIds } = await fetchSessionEvents(sessionId, tenantId);

        const candidates = events.filter((e) => e.message && e.message.length > 5);

        if (candidates.length === 0) {
          return {
            content: [{
              type: 'text' as const,
              text: JSON.stringify({ query, resultCount: 0, results: [], note: 'No events with messages found.' }),
            }],
          };
        }

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

        const timeoutMs = 60_000;
        const provider = await createSearchProvider();
        const searchResults = await Promise.race([
          (async () => {
            await provider.index(docs);
            return provider.search(query, { topK, minScore });
          })(),
          new Promise<never>((_, reject) =>
            setTimeout(() => reject(new Error(`Semantic search timed out after ${timeoutMs / 1000}s`)), timeoutMs),
          ),
        ]);

        const results = searchResults.map((r) => {
          const idx = r.metadata.index as number;
          const e = candidates[idx];
          return {
            score: Math.round(r.score * 1000) / 1000,
            sessionId: e._sessionId ?? sessionId,
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
      } catch (error: unknown) {
        return toolError('search_events_semantic', args, error);
      }
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

  // Tool 23: deep_search_events
  server.tool(
    'deep_search_events',
    'TIER 3 — DEEP HYBRID SEARCH (thorough, use when accuracy is critical). ' +
    'Combines semantic search with a complementary keyword cross-check to ensure nothing is missed. ' +
    'First runs semantic search (same as search_events_semantic), then scans ALL events — including those ' +
    'with short or missing messages — for keyword matches in eventType, source, severity, message, and data fields. ' +
    'Results are merged, deduplicated, and tagged with their discovery method (semantic, keyword, or both). ' +
    'Use this when: (1) a previous semantic search may have missed events, (2) you need high confidence in completeness, ' +
    'or (3) the query involves specific technical terms that benefit from exact keyword matching alongside semantic meaning. ' +
    'Slower than search_events_semantic due to the double-pass approach — only use when accuracy matters more than speed. ' +
    'Provide sessionId to search within one session, or omit to search across recent failed sessions. ' +
    'Omit tenantId for cross-tenant search (Global Admin), or specify tenantId for single-tenant.',
    {
      query: z.string().describe('Natural language description of what to find (e.g. "app download stuck", "certificate error", "disk space low")'),
      sessionId: z.string().optional().describe('Search within a specific session. If omitted, searches across recent failed sessions.'),
      tenantId: z.string().optional().describe('Tenant ID. Required for non-Global Admin users; Global Admin can omit to search across tenants.'),
      topK: z.coerce.number().min(1).max(50).optional().default(15)
        .describe('Max results to return (1-50, default 15). Higher default than search_events_semantic for thoroughness.'),
      minScore: z.coerce.number().min(0).max(1).optional().default(0.3)
        .describe('Min semantic similarity score (0-1, default 0.3). Slightly lower than search_events_semantic to catch borderline matches.'),
      keywords: z.array(z.string()).optional()
        .describe('Additional exact keywords for the raw cross-check pass. Auto-extracted from query if omitted.'),
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
                query, resultCount: 0, semanticMatches: 0, keywordMatches: 0,
                results: [], note: 'No events found.',
              }),
            }],
          };
        }

        // ── Pass 1: Semantic search (events with meaningful messages) ──
        const semanticCandidates: EventEntry[] = [];
        const candidateOriginalIndices: number[] = [];
        for (let i = 0; i < events.length; i++) {
          if (events[i].message && events[i].message!.length > 5) {
            semanticCandidates.push(events[i]);
            candidateOriginalIndices.push(i);
          }
        }

        const semanticMap = new Map<number, { score: number }>();
        let searchBackend = 'none';

        if (semanticCandidates.length > 0) {
          const docs = semanticCandidates.map((e, i) => {
            const parts = [e.message];
            if (e.eventType) parts.push(`Event: ${e.eventType}`);
            if (e.severity) parts.push(`Severity: ${e.severity}`);
            if (e.source) parts.push(`Source: ${e.source}`);
            return { id: `event-${i}`, text: parts.join(' | '), metadata: { index: i } as Record<string, unknown> };
          });

          const timeoutMs = 60_000;
          const provider = await createSearchProvider();
          searchBackend = provider.name;
          const searchResults = await Promise.race([
            (async () => {
              await provider.index(docs);
              return provider.search(query, { topK, minScore });
            })(),
            new Promise<never>((_, reject) =>
              setTimeout(() => reject(new Error(`Deep search timed out after ${timeoutMs / 1000}s`)), timeoutMs),
            ),
          ]);

          for (const r of searchResults) {
            const candidateIdx = r.metadata.index as number;
            const originalIdx = candidateOriginalIndices[candidateIdx];
            semanticMap.set(originalIdx, { score: Math.round(r.score * 1000) / 1000 });
          }
        }

        // ── Pass 2: Keyword cross-check (ALL events, including short/empty messages) ──
        const queryKeywords = keywords ?? extractKeywords(query);
        const keywordMap = new Map<number, { matchedKeywords: string[] }>();

        for (let i = 0; i < events.length; i++) {
          const e = events[i];
          const searchText = [
            e.message ?? '', e.eventType ?? '', e.source ?? '',
            e.severity ?? '', e.phase ?? '',
            e.data ? JSON.stringify(e.data) : '',
          ].join(' ').toLowerCase();

          const matched = queryKeywords.filter((kw) => searchText.includes(kw.toLowerCase()));
          if (matched.length > 0) {
            keywordMap.set(i, { matchedKeywords: matched });
          }
        }

        // ── Merge & deduplicate ──
        const allIndices = new Set([...semanticMap.keys(), ...keywordMap.keys()]);
        type DiscoveryMethod = 'both' | 'semantic' | 'keyword';
        const merged = Array.from(allIndices).map((idx) => {
          const e = events[idx];
          const semantic = semanticMap.get(idx);
          const keyword = keywordMap.get(idx);
          const discoveryMethod: DiscoveryMethod = semantic && keyword ? 'both' : semantic ? 'semantic' : 'keyword';
          return {
            score: semantic?.score ?? 0,
            discoveryMethod,
            matchedKeywords: keyword?.matchedKeywords ?? [],
            sessionId: e._sessionId ?? sessionId,
            eventType: e.eventType,
            severity: e.severity,
            source: e.source,
            phase: e.phase,
            timestamp: e.timestamp,
            message: e.message,
          };
        });

        const methodRank: Record<DiscoveryMethod, number> = { both: 0, semantic: 1, keyword: 2 };
        merged.sort((a, b) => {
          const rankDiff = methodRank[a.discoveryMethod] - methodRank[b.discoveryMethod];
          if (rankDiff !== 0) return rankDiff;
          return b.score - a.score;
        });

        const results = merged.slice(0, topK);

        return {
          content: [{
            type: 'text' as const,
            text: JSON.stringify({
              query,
              searchBackend,
              sessionsSearched: sessionIds,
              totalEventsScanned: events.length,
              semanticCandidates: semanticCandidates.length,
              semanticMatches: semanticMap.size,
              keywordMatches: keywordMap.size,
              keywordsUsed: queryKeywords,
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
