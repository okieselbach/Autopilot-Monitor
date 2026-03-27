import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { registerTools } from './tools.js';
import { registerResources } from './resources.js';
import { loadKnowledgeDocs } from './knowledge-base.js';
import { createSearchProvider } from './search-factory.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

const RULES_DIR = process.env.RULES_DIR ?? resolve(__dirname, '..', '..', '..', '..', 'rules');

const server = new McpServer({
  name: 'autopilot-monitor',
  version: '1.2.0',
});

// Initialize the knowledge base search provider
console.error('Loading knowledge base documents…');
const docs = await loadKnowledgeDocs(RULES_DIR);

console.error(`Initializing search provider (${docs.length} documents)…`);
const knowledgeBase = await createSearchProvider();
await knowledgeBase.index(docs);

console.error(`Search provider ready: ${knowledgeBase.name} — ${knowledgeBase.size} documents indexed.`);

registerTools(server, knowledgeBase);
registerResources(server);

const transport = new StdioServerTransport();
await server.connect(transport);

console.error('Autopilot-Monitor MCP Server running (stdio transport)');
console.error(`API URL: ${process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net'}`);
console.error(`Search backend: ${knowledgeBase.name} | Documents: ${knowledgeBase.size} | Rules dir: ${RULES_DIR}`);
