import { runWithToolName } from './client.js';

const toolLoggingEnabled = process.env.MCP_TOOL_LOGGING === 'true';

/**
 * Wraps an MCP tool handler to:
 * 1. Always: propagate the tool name via AsyncLocalStorage so apiFetch sends
 *    the X-MCP-Tool-Name header to the backend (tracked in App Insights).
 * 2. Optionally (MCP_TOOL_LOGGING=true): emit structured JSON to stderr,
 *    queryable via Container App Logs in Azure Monitor.
 */
export async function withToolTelemetry<T>(toolName: string, fn: () => T | Promise<T>): Promise<T> {
  if (!toolLoggingEnabled) {
    return runWithToolName(toolName, fn) as Promise<T>;
  }

  const start = Date.now();
  let isError = false;
  try {
    return await runWithToolName(toolName, fn);
  } catch (err) {
    isError = true;
    throw err;
  } finally {
    console.error(JSON.stringify({
      type: 'tool_call',
      tool: toolName,
      durationMs: Date.now() - start,
      isError,
      timestamp: new Date().toISOString(),
    }));
  }
}
