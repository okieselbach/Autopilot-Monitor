import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { registerTools } from './tools.js';
import { registerResources } from './resources.js';
import { buildKnowledgeBase } from './knowledge-base.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

// Resolve the rules directory (relative to project root)
const RULES_DIR = process.env.RULES_DIR ?? resolve(__dirname, '..', '..', '..', '..', 'rules');

const server = new McpServer({
  name: 'autopilot-monitor',
  version: '1.1.0',
});

// Build the knowledge base (embeddings) before registering tools,
// so the search_knowledge tool has immediate access.
console.error('Initializing vector search knowledge base…');
const knowledgeBase = await buildKnowledgeBase(RULES_DIR);

registerTools(server, knowledgeBase);
registerResources(server);

const transport = new StdioServerTransport();
await server.connect(transport);

console.error('Autopilot-Monitor MCP Server running (stdio transport)');
console.error(`API URL: ${process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net'}`);
console.error(`Knowledge base: ${knowledgeBase.size} documents indexed from ${RULES_DIR}`);
