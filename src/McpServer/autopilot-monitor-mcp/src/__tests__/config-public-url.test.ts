/**
 * M3 — MCP_PUBLIC_URL pin path.
 *
 * config.ts captures MCP_PUBLIC_URL into a module-level const at import time, so
 * the pin-wins behaviour must be exercised in its own file with the env var set
 * BEFORE the dynamic import resolves. The forwarded-header fallback (pin unset)
 * is covered in security-guards.test.ts.
 */
import { describe, it, expect } from 'vitest';

process.env.MCP_PUBLIC_URL = 'https://pinned.example.net';
const { getPublicBaseUrl } = await import('../config.js');

describe('M3 — getPublicBaseUrl honors the MCP_PUBLIC_URL pin', () => {
  it('returns the pin verbatim and ignores spoofable forwarded headers', () => {
    const req = {
      headers: { 'x-forwarded-proto': 'https', 'x-forwarded-host': 'attacker.tld', host: 'attacker.tld' },
      protocol: 'http',
    } as unknown as Parameters<typeof getPublicBaseUrl>[0];
    expect(getPublicBaseUrl(req)).toBe('https://pinned.example.net');
  });
});
