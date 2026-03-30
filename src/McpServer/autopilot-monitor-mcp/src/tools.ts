import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import type { SearchProvider } from './search-provider.js';
import { registerSessionTools } from './tools/sessions.js';
import { registerSearchTools } from './tools/search.js';
import { registerAdminTools } from './tools/admin.js';

export function registerTools(server: McpServer, knowledgeBase?: SearchProvider): void {
  registerSessionTools(server);
  registerSearchTools(server, knowledgeBase);
  registerAdminTools(server);
}
