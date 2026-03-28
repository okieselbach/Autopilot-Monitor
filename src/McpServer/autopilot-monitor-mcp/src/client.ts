import { getAccessToken } from './auth.js';

const BASE_URL = process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net';

async function apiFetch(path: string, options: RequestInit = {}): Promise<unknown> {
  const url = `${BASE_URL}${path}`;
  const token = await getAccessToken();
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${token}`,
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
