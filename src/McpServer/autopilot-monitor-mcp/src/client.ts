import { AsyncLocalStorage } from 'node:async_hooks';

const BASE_URL = process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net';

/** Default timeout for backend API requests (30 seconds) */
const API_TIMEOUT_MS = 30_000;

/**
 * Per-request token store using AsyncLocalStorage.
 *
 * Each incoming MCP request runs inside its own async context (via
 * `runWithToken`), so concurrent sessions cannot overwrite each other's
 * tokens — even when async operations interleave on the event loop.
 */
const tokenStore = new AsyncLocalStorage<string>();

/**
 * Run a callback within an async context that carries the given Bearer token.
 * All calls to `apiFetch` inside the callback (and its async descendants)
 * will automatically use this token.
 */
export function runWithToken<T>(token: string, fn: () => T): T {
  return tokenStore.run(token, fn);
}

/**
 * @deprecated Use `runWithToken` instead. Kept for backward compatibility
 * during the transition — sets a fallback global token for code paths that
 * haven't migrated to `runWithToken` yet.
 */
let _fallbackToken: string | undefined;

export function setCurrentToken(token: string | undefined): void {
  _fallbackToken = token;
}

export function getCurrentToken(): string | undefined {
  return tokenStore.getStore() ?? _fallbackToken;
}

async function apiFetch(path: string, options: RequestInit = {}): Promise<unknown> {
  const token = getCurrentToken();
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

  // Apply timeout to prevent hanging on unresponsive backend
  const signal = options.signal ?? AbortSignal.timeout(API_TIMEOUT_MS);

  const res = await fetch(url, { ...options, headers, signal });
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
