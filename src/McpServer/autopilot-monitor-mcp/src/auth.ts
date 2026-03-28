/**
 * Auth module for the remote MCP server.
 *
 * In remote mode, the Claude Code client handles OAuth and sends a Bearer token
 * with each MCP request. This module provides helpers for token validation and
 * extracting user info from the JWT claims.
 *
 * The user's token is passed through to the backend API (access_as_user scope),
 * so no service principal or separate credentials are needed.
 */

import { createDecoder } from './jwt-decode.js';

export interface TokenClaims {
  /** User Principal Name (email) */
  upn?: string;
  /** Azure AD Object ID */
  oid?: string;
  /** Tenant ID */
  tid?: string;
  /** Token expiry (unix timestamp) */
  exp?: number;
  /** Audience */
  aud?: string;
}

const decode = createDecoder();

/**
 * Extracts claims from a JWT access token without cryptographic validation.
 * Full validation (signature, issuer, audience) is deferred to the backend API
 * which receives the same token. This avoids duplicating JWKS/OIDC config here.
 */
export function extractTokenClaims(token: string): TokenClaims | null {
  try {
    return decode(token);
  } catch {
    return null;
  }
}

/**
 * Checks if a token is expired (with 60s buffer).
 */
export function isTokenExpired(claims: TokenClaims): boolean {
  if (!claims.exp) return true;
  return Date.now() / 1000 > claims.exp - 60;
}
