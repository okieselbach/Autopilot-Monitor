/**
 * Minimal JWT payload decoder — no signature verification.
 * Used to extract claims (upn, tid, exp) from bearer tokens.
 * Full cryptographic validation is handled by the backend API.
 */
export function createDecoder() {
  return function decode<T = Record<string, unknown>>(token: string): T {
    const parts = token.split('.');
    if (parts.length !== 3) {
      throw new Error('Invalid JWT: expected 3 parts');
    }
    const payload = parts[1];
    const json = Buffer.from(payload, 'base64url').toString('utf-8');
    return JSON.parse(json) as T;
  };
}
