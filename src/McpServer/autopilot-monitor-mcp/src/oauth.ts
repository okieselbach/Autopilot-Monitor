/**
 * OAuth 2.0 proxy for the remote MCP server.
 *
 * Instead of requiring localhost redirect URIs, the MCP server acts as an
 * OAuth authorization server proxy to Entra ID. Claude Code talks to our
 * endpoints, which redirect to/from Entra ID.
 *
 * Flow:
 *   1. Claude Code → GET /oauth/authorize → 302 to Entra ID /authorize
 *   2. User authenticates at Entra ID
 *   3. Entra ID → GET /oauth/callback → 302 back to Claude Code with code
 *   4. Claude Code → POST /oauth/token → proxied to Entra ID /token
 */
import { Router } from 'express';

const CLIENT_ID = process.env.AUTOPILOT_ENTRA_CLIENT_ID ?? '1a400946-62c1-4ab4-aa37-f730ac89704d';
const AUTHORITY = process.env.AUTOPILOT_ENTRA_AUTHORITY ?? 'https://login.microsoftonline.com/organizations';
const SCOPES = `api://${CLIENT_ID}/access_as_user openid profile offline_access`;

/**
 * Derives the public base URL of this MCP server.
 * In production: from X-Forwarded-Host / X-Forwarded-Proto (set by Container Apps ingress).
 * Fallback: MCP_PUBLIC_URL env var or localhost.
 */
function getPublicBaseUrl(req: import('express').Request): string {
  if (process.env.MCP_PUBLIC_URL) return process.env.MCP_PUBLIC_URL;

  const proto = (req.headers['x-forwarded-proto'] as string) ?? req.protocol;
  const host = (req.headers['x-forwarded-host'] as string) ?? req.headers.host;
  return `${proto}://${host}`;
}

export function createOAuthRouter(): Router {
  const router = Router();

  // --- OAuth Authorization Server Metadata (RFC 8414) ---
  router.get('/.well-known/oauth-authorization-server', (req, res) => {
    const baseUrl = getPublicBaseUrl(req);
    res.json({
      issuer: baseUrl,
      authorization_endpoint: `${baseUrl}/oauth/authorize`,
      token_endpoint: `${baseUrl}/oauth/token`,
      response_types_supported: ['code'],
      grant_types_supported: ['authorization_code', 'refresh_token'],
      code_challenge_methods_supported: ['S256'],
      scopes_supported: ['openid', 'profile', 'offline_access', `api://${CLIENT_ID}/access_as_user`],
    });
  });

  // --- Authorize: redirect to Entra ID ---
  router.get('/oauth/authorize', (req, res) => {
    const baseUrl = getPublicBaseUrl(req);
    const {
      client_id,
      redirect_uri,
      state,
      code_challenge,
      code_challenge_method,
      scope,
    } = req.query as Record<string, string>;

    // Store Claude Code's redirect_uri in state so we can forward the code back
    const proxyState = Buffer.from(JSON.stringify({
      originalState: state,
      redirectUri: redirect_uri,
    })).toString('base64url');

    const entraParams = new URLSearchParams({
      client_id: CLIENT_ID,
      response_type: 'code',
      redirect_uri: `${baseUrl}/oauth/callback`,
      scope: scope || SCOPES,
      state: proxyState,
      ...(code_challenge ? { code_challenge } : {}),
      ...(code_challenge_method ? { code_challenge_method } : {}),
    });

    res.redirect(`${AUTHORITY}/oauth2/v2.0/authorize?${entraParams}`);
  });

  // --- Callback: receive code from Entra ID, forward to Claude Code ---
  router.get('/oauth/callback', (req, res) => {
    const { code, state, error, error_description } = req.query as Record<string, string>;

    if (error) {
      res.status(400).json({ error, error_description });
      return;
    }

    // Decode proxy state to get Claude Code's original redirect_uri
    let originalState = state;
    let redirectUri = '';
    try {
      const decoded = JSON.parse(Buffer.from(state, 'base64url').toString('utf-8'));
      originalState = decoded.originalState;
      redirectUri = decoded.redirectUri;
    } catch {
      res.status(400).json({ error: 'invalid_state', error_description: 'Could not decode state' });
      return;
    }

    // Redirect back to Claude Code with the authorization code
    const callbackParams = new URLSearchParams({
      code,
      ...(originalState ? { state: originalState } : {}),
    });

    res.redirect(`${redirectUri}?${callbackParams}`);
  });

  // --- Token: proxy token exchange to Entra ID ---
  router.post('/oauth/token', async (req, res) => {
    const baseUrl = getPublicBaseUrl(req);

    // Build form body for Entra ID token endpoint
    const body = new URLSearchParams();
    const params = req.body as Record<string, string>;

    body.set('client_id', CLIENT_ID);
    body.set('redirect_uri', `${baseUrl}/oauth/callback`);

    if (params.grant_type) body.set('grant_type', params.grant_type);
    if (params.code) body.set('code', params.code);
    if (params.refresh_token) body.set('refresh_token', params.refresh_token);
    if (params.code_verifier) body.set('code_verifier', params.code_verifier);
    if (params.scope) body.set('scope', params.scope);

    try {
      const tokenResponse = await fetch(`${AUTHORITY}/oauth2/v2.0/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: body.toString(),
      });

      const data = await tokenResponse.json();
      res.status(tokenResponse.status).json(data);
    } catch (err) {
      console.error('[oauth] Token exchange failed:', err);
      res.status(502).json({ error: 'token_exchange_failed', error_description: 'Failed to exchange token with identity provider' });
    }
  });

  return router;
}
