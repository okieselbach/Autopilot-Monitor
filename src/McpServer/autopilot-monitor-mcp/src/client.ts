const BASE_URL = process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net';

/**
 * Per-request token store. The MCP request handler sets the current user's
 * Bearer token before tool execution, and apiFetch reads it to pass through
 * to the backend API.
 */
let _currentToken: string | undefined;

export function setCurrentToken(token: string | undefined): void {
  _currentToken = token;
}

export function getCurrentToken(): string | undefined {
  return _currentToken;
}

async function apiFetch(path: string, options: RequestInit = {}): Promise<unknown> {
  const token = _currentToken;
  if (!token) {
    throw new Error('No authentication token available. Ensure the request includes a valid Bearer token.');
  }

  const url = `${BASE_URL}${path}`;
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${token}`,
    'X-Client-Source': 'mcp',
    ...((options.headers as Record<string, string>) ?? {}),
  };
  const res = await fetch(url, { ...options, headers });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`API error ${res.status}: ${text}`);
  }
  return res.json();
}

function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v != null && v !== '') p.set(k, String(v));
  }
  const s = p.toString();
  return s ? `?${s}` : '';
}

export { apiFetch, buildQuery };
