import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { registerTools } from './tools.js';
import { registerResources } from './resources.js';

const server = new McpServer({
  name: 'autopilot-monitor',
  version: '1.0.0',
});

registerTools(server);
registerResources(server);

const transport = new StdioServerTransport();
await server.connect(transport);

console.error('Autopilot-Monitor MCP Server running (stdio transport)');
console.error(`API URL: ${process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net'}`);
