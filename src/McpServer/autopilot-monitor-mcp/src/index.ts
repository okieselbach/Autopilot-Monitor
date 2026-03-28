import { resolve, dirname } from 'node:path';
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
  const s = new McpServer({ name: 'autopilot-monitor', version: '1.2.0' });
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
    version: '1.2.0',
    search: { backend: knowledgeBase.name, documents: knowledgeBase.size },
  });
});

// Track transports by session ID for reuse
const transports = new Map<string, StreamableHTTPServerTransport>();

// Access guard for /mcp — validates JWT, checks backend whitelist, enforces rate limits
app.use('/mcp', accessGuard);

// MCP Streamable HTTP endpoint
app.all('/mcp', async (req, res) => {
  const sessionId = req.headers['mcp-session-id'] as string | undefined;

  // GET (SSE stream) or DELETE (session termination) — need existing session
  if (req.method === 'GET' || req.method === 'DELETE') {
    const transport = sessionId ? transports.get(sessionId) : undefined;
    if (!transport) {
      res.status(400).json({ error: 'No valid session. Send an initialize request first.' });
      return;
    }
    await transport.handleRequest(req, res);
    return;
  }

  // POST — existing session: forward to its transport
  if (sessionId && transports.has(sessionId)) {
    const transport = transports.get(sessionId)!;
    await transport.handleRequest(req, res, req.body);
    return;
  }

  // POST — unknown/stale session ID: strip it so transport treats this as a fresh connection
  if (sessionId) {
    console.error(`[mcp] Stale session ${sessionId}, method=${req.body?.method} — creating new session`);
    delete req.headers['mcp-session-id'];
  } else {
    console.error(`[mcp] New session — method=${req.body?.method}`);
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

  const server = createMcpServer();
  await server.connect(transport);

  await transport.handleRequest(req, res, req.body);

  // Register AFTER handleRequest — the session ID is only assigned during
  // initialize processing inside handleRequest, not before.
  if (transport.sessionId) {
    transports.set(transport.sessionId, transport);
    console.error(`[mcp] Session ${transport.sessionId} registered`);
  }
});

app.listen(PORT, '0.0.0.0', () => {
  console.error(`Autopilot-Monitor MCP Server running (Streamable HTTP on port ${PORT})`);
  console.error(`API URL: ${process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net'}`);
  console.error(`Search backend: ${knowledgeBase.name} | Documents: ${knowledgeBase.size} | Rules dir: ${RULES_DIR}`);
  console.error(`Health: http://localhost:${PORT}/health`);
  console.error(`MCP endpoint: http://localhost:${PORT}/mcp`);
});
