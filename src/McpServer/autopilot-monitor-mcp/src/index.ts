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
import { setCurrentToken } from './client.js';
import { extractTokenClaims, isTokenExpired } from './auth.js';
import { createOAuthRouter } from './oauth.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

const PORT = parseInt(process.env.PORT ?? '8080', 10);
const RULES_DIR = process.env.RULES_DIR ?? resolve(__dirname, '..', '..', '..', '..', 'rules');

// --- Initialize MCP Server ---

const server = new McpServer({
  name: 'autopilot-monitor',
  version: '1.2.0',
});

console.error('Loading knowledge base documents…');
const docs = await loadKnowledgeDocs(RULES_DIR);

console.error(`Initializing search provider (${docs.length} documents)…`);
const knowledgeBase = await createSearchProvider();
await knowledgeBase.index(docs);
console.error(`Search provider ready: ${knowledgeBase.name} — ${knowledgeBase.size} documents indexed.`);

registerTools(server, knowledgeBase);
registerResources(server);

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

// Auth middleware for /mcp — extract Bearer token, validate basic claims, set for pass-through
app.use('/mcp', (req, res, next) => {
  const authHeader = req.headers.authorization;
  if (!authHeader?.startsWith('Bearer ')) {
    res.status(401).json({ error: 'Missing or invalid Authorization header' });
    return;
  }

  const token = authHeader.slice(7);
  const claims = extractTokenClaims(token);
  if (!claims || !claims.upn) {
    res.status(401).json({ error: 'Invalid token: missing required claims' });
    return;
  }

  if (isTokenExpired(claims)) {
    res.status(401).json({ error: 'Token expired' });
    return;
  }

  // Set token for pass-through to backend API
  setCurrentToken(token);
  next();
});

// MCP Streamable HTTP endpoint
app.all('/mcp', async (req, res) => {
  // Handle GET for SSE streams and DELETE for session termination
  const sessionId = req.headers['mcp-session-id'] as string | undefined;

  if (req.method === 'GET' || req.method === 'DELETE') {
    const transport = sessionId ? transports.get(sessionId) : undefined;
    if (!transport) {
      res.status(400).json({ error: 'No valid session. Send an initialize request first.' });
      return;
    }
    await transport.handleRequest(req, res);
    return;
  }

  // POST — either new session (initialize) or existing session
  if (sessionId && transports.has(sessionId)) {
    const transport = transports.get(sessionId)!;
    await transport.handleRequest(req, res, req.body);
    return;
  }

  // New session — create transport and connect
  const transport = new StreamableHTTPServerTransport({
    sessionIdGenerator: () => randomUUID(),
  });

  transport.onclose = () => {
    if (transport.sessionId) {
      transports.delete(transport.sessionId);
    }
  };

  await server.connect(transport);

  // Store transport for session reuse
  if (transport.sessionId) {
    transports.set(transport.sessionId, transport);
  }

  await transport.handleRequest(req, res, req.body);
});

app.listen(PORT, '0.0.0.0', () => {
  console.error(`Autopilot-Monitor MCP Server running (Streamable HTTP on port ${PORT})`);
  console.error(`API URL: ${process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net'}`);
  console.error(`Search backend: ${knowledgeBase.name} | Documents: ${knowledgeBase.size} | Rules dir: ${RULES_DIR}`);
  console.error(`Health: http://localhost:${PORT}/health`);
  console.error(`MCP endpoint: http://localhost:${PORT}/mcp`);
});
