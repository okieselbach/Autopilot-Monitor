import { resolve, dirname } from 'node:path';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
import express from 'express';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { registerTools } from './tools.js';
import { registerResources } from './resources.js';
import { loadKnowledgeDocs } from './knowledge-base.js';
import { createSearchProvider } from './search-factory.js';
import { createOAuthRouter } from './oauth.js';
import { accessGuard } from './access-guard.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

const pkg = JSON.parse(readFileSync(resolve(__dirname, '..', 'package.json'), 'utf-8')) as { version: string };
const SERVER_VERSION: string = pkg.version;

const PORT = parseInt(process.env.PORT ?? '8080', 10);
const RULES_DIR = process.env.RULES_DIR ?? resolve(__dirname, '..', '..', '..', '..', 'rules');

// --- Load shared knowledge base (reused across all sessions) ---

console.error('Loading knowledge base documents…');
const docs = await loadKnowledgeDocs(RULES_DIR);

console.error(`Initializing search provider (${docs.length} documents)…`);
const knowledgeBase = await createSearchProvider();
await knowledgeBase.index(docs);
console.error(`Search provider ready: ${knowledgeBase.name} — ${knowledgeBase.size} documents indexed.`);

/** Creates a fresh McpServer instance per session (each needs its own protocol). */
function createMcpServer(): McpServer {
  const s = new McpServer({ name: 'Autopilot-Monitor', version: SERVER_VERSION });
  registerTools(s, knowledgeBase);
  registerResources(s);
  return s;
}

// --- HTTP Server with Streamable HTTP Transport ---

const app = express();
app.use(express.json());
app.use(express.urlencoded({ extended: true }));

// OAuth proxy (must be before auth middleware)
app.use(createOAuthRouter());

// Health check
app.get('/health', (_req, res) => {
  res.json({
    status: 'healthy',
    server: 'autopilot-monitor-mcp',
    version: SERVER_VERSION,
    search: { backend: knowledgeBase.name, documents: knowledgeBase.size },
  });
});

// Track transports by session ID for reuse
const transports = new Map<string, { transport: StreamableHTTPServerTransport; createdAt: number; lastActivity: number }>();

// Session TTL cleanup — remove abandoned sessions that never sent a DELETE.
// Currently runs every 12h (cost-effective for single-user on a sleeping container app).
// TODO: Reduce interval (e.g. 30min) when scaling to multiple concurrent users.
const SESSION_MAX_AGE_MS = 2 * 60 * 60 * 1000; // 2 hours
setInterval(() => {
  const now = Date.now();
  for (const [id, entry] of transports) {
    if (now - entry.lastActivity > SESSION_MAX_AGE_MS) {
      entry.transport.close().catch(() => {});
      transports.delete(id);
      console.error(`[mcp] Reaped idle session ${id} (idle > ${SESSION_MAX_AGE_MS / 1000 / 60}min)`);
    }
  }
}, 12 * 60 * 60 * 1000); // 12h interval

// Access guard for /mcp — validates JWT, checks backend whitelist, enforces rate limits
app.use('/mcp', accessGuard);

// MCP Streamable HTTP endpoint
app.all('/mcp', async (req, res) => {
  const sessionId = req.headers['mcp-session-id'] as string | undefined;

  // GET (SSE stream) or DELETE (session termination) — need existing session
  if (req.method === 'GET' || req.method === 'DELETE') {
    const entry = sessionId ? transports.get(sessionId) : undefined;
    if (!entry) {
      res.status(400).json({ error: 'No valid session. Send an initialize request first.' });
      return;
    }
    await entry.transport.handleRequest(req, res);
    return;
  }

  // POST — existing session: forward to its transport
  if (sessionId && transports.has(sessionId)) {
    const entry = transports.get(sessionId)!;
    entry.lastActivity = Date.now();
    await entry.transport.handleRequest(req, res, req.body);
    return;
  }

  // POST — unknown/stale session ID: strip it so transport treats this as a fresh connection
  if (sessionId) {
    console.error(`[mcp] Stale session ${sessionId} — creating new session`);
    delete req.headers['mcp-session-id'];
  }
  const transport = new StreamableHTTPServerTransport({
    sessionIdGenerator: () => randomUUID(),
  });

  transport.onclose = () => {
    if (transport.sessionId) {
      transports.delete(transport.sessionId);
      console.error(`[mcp] Session ${transport.sessionId} closed`);
    }
  };

  transport.onerror = (error: Error) => {
    console.error(`[mcp] Session ${transport.sessionId ?? 'unknown'} error: ${error.message}`);
  };

  const server = createMcpServer();
  await server.connect(transport);

  await transport.handleRequest(req, res, req.body);

  // Register AFTER handleRequest — the session ID is only assigned during
  // initialize processing inside handleRequest, not before.
  if (transport.sessionId) {
    const now = Date.now();
    transports.set(transport.sessionId, { transport, createdAt: now, lastActivity: now });
    console.error(`[mcp] Session ${transport.sessionId} registered`);
  }
});

const server = app.listen(PORT, '0.0.0.0', () => {
  console.error(`Autopilot-Monitor MCP Server running (Streamable HTTP on port ${PORT})`);
  console.error(`API URL: ${process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net'}`);
  console.error(`Search backend: ${knowledgeBase.name} | Documents: ${knowledgeBase.size} | Rules dir: ${RULES_DIR}`);
  console.error(`Health: http://localhost:${PORT}/health`);
  console.error(`MCP endpoint: http://localhost:${PORT}/mcp`);
});

// --- Graceful shutdown ---

async function gracefulShutdown(signal: string) {
  console.error(`[mcp] Received ${signal}, shutting down gracefully…`);
  for (const [id, entry] of transports) {
    try {
      await entry.transport.close();
      console.error(`[mcp] Closed session ${id}`);
    } catch (e) {
      console.error(`[mcp] Error closing session ${id}:`, e);
    }
  }
  transports.clear();
  server.close(() => {
    console.error('[mcp] HTTP server closed');
    process.exit(0);
  });
}

process.on('SIGTERM', () => gracefulShutdown('SIGTERM'));
process.on('SIGINT', () => gracefulShutdown('SIGINT'));
