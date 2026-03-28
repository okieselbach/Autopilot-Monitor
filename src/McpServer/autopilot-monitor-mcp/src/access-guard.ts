/**
 * MCP access guard: validates user token, checks backend whitelist, enforces rate limits.
 *
 * Flow per request:
 *   1. Extract Bearer token → decode JWT claims (upn, exp)
 *   2. Check access: call backend /api/global/mcp-users/check (cached 5min per UPN)
 *   3. Rate limit: sliding window per UPN (default 60 req/min)
 *   4. Set token for pass-through to backend API
 */
import type { Request, Response, NextFunction } from 'express';
import { extractTokenClaims, isTokenExpired } from './auth.js';
import { setCurrentToken } from './client.js';

const BASE_URL = process.env.AUTOPILOT_API_URL ?? 'https://autopilotmonitor-api.azurewebsites.net';
const RATE_LIMIT = parseInt(process.env.MCP_RATE_LIMIT_PER_MINUTE ?? '60', 10);
const ACCESS_CACHE_TTL_MS = 5 * 60 * 1000; // 5 minutes

// --- Access check cache ---

interface AccessCacheEntry {
  allowed: boolean;
  reason: string;
  expiresAt: number;
}

const accessCache = new Map<string, AccessCacheEntry>();

async function checkAccess(upn: string, token: string): Promise<{ allowed: boolean; reason: string }> {
  const cached = accessCache.get(upn);
  if (cached && Date.now() < cached.expiresAt) {
    return { allowed: cached.allowed, reason: cached.reason };
  }

  try {
    const res = await fetch(`${BASE_URL}/api/auth/mcp`, {
      headers: { Authorization: `Bearer ${token}` },
    });

    console.error(`[access-guard] Backend check for ${upn}: status=${res.status}`);

    const text = await res.text();
    if (!text) {
      console.error(`[access-guard] Backend returned empty body for ${upn}`);
      return { allowed: false, reason: `Backend returned ${res.status} with empty body` };
    }

    const data = JSON.parse(text) as { allowed: boolean; reason?: string; accessGrant?: string };
    const result = {
      allowed: data.allowed === true,
      reason: data.allowed ? (data.accessGrant ?? 'allowed') : (data.reason ?? 'denied'),
    };

    accessCache.set(upn, { ...result, expiresAt: Date.now() + ACCESS_CACHE_TTL_MS });
    return result;
  } catch (err) {
    console.error(`[access-guard] Backend check failed for ${upn}:`, err);
    // Fail-closed: deny on backend error
    return { allowed: false, reason: 'Backend access check unavailable' };
  }
}

// --- Rate limiting (sliding window) ---

interface RateEntry {
  timestamps: number[];
}

const rateBuckets = new Map<string, RateEntry>();

// Cleanup stale entries every 5 minutes
setInterval(() => {
  const cutoff = Date.now() - 60_000;
  for (const [key, entry] of rateBuckets) {
    entry.timestamps = entry.timestamps.filter((t) => t > cutoff);
    if (entry.timestamps.length === 0) rateBuckets.delete(key);
  }
}, 5 * 60_000);

function isRateLimited(upn: string): boolean {
  const now = Date.now();
  const windowStart = now - 60_000;

  let entry = rateBuckets.get(upn);
  if (!entry) {
    entry = { timestamps: [] };
    rateBuckets.set(upn, entry);
  }

  // Remove timestamps outside the window
  entry.timestamps = entry.timestamps.filter((t) => t > windowStart);

  if (entry.timestamps.length >= RATE_LIMIT) {
    return true;
  }

  entry.timestamps.push(now);
  return false;
}

// --- Express middleware ---

export function accessGuard(req: Request, res: Response, next: NextFunction): void {
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

  const upn = claims.upn.toLowerCase();

  // Rate limit check (sync — no backend call needed)
  if (isRateLimited(upn)) {
    res.status(429).json({ error: 'Rate limit exceeded', retryAfterSeconds: 60 });
    return;
  }

  // Access check (async — calls backend, cached)
  checkAccess(upn, token)
    .then((result) => {
      if (!result.allowed) {
        res.status(403).json({ error: 'MCP access denied', reason: result.reason });
        return;
      }

      // Set token for pass-through to backend API
      setCurrentToken(token);
      next();
    })
    .catch(() => {
      res.status(503).json({ error: 'Access check failed' });
    });
}
