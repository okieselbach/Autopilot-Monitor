/**
 * OAuth 2.0 proxy for the remote MCP server.
 *
 * Instead of requiring localhost redirect URIs, the MCP server acts as an
 * OAuth authorization server proxy to Entra ID. Claude Code talks to our
 * endpoints, which redirect to/from Entra ID.
 *
 * Flow:
 *   1. Claude Code → POST /mcp → 401 (with WWW-Authenticate header)
 *   2. Claude Code → GET /.well-known/oauth-protected-resource → discovers auth server (RFC 9728)
 *   3. Claude Code → GET /.well-known/oauth-authorization-server → discovers endpoints (RFC 8414)
 *   4. Claude Code → POST /oauth/register → dynamic client registration (RFC 7591)
 *   5. Claude Code → GET /oauth/authorize → 302 to Entra ID /authorize
 *   6. User authenticates at Entra ID
 *   7. Entra ID → GET /oauth/callback → 302 back to Claude Code with code
 *   8. Claude Code → POST /oauth/token → proxied to Entra ID /token
 *   9. Claude Code → POST /mcp (with Bearer token) → MCP session established
 */
import { Router } from 'express';
import crypto from 'node:crypto';

const CLIENT_ID = process.env.AUTOPILOT_ENTRA_CLIENT_ID ?? '1a400946-62c1-4ab4-aa37-f730ac89704d';
const CLIENT_SECRET = process.env.AUTOPILOT_ENTRA_CLIENT_SECRET ?? '';
const AUTHORITY = process.env.AUTOPILOT_ENTRA_AUTHORITY ?? 'https://login.microsoftonline.com/organizations';
const SCOPES = `api://${CLIENT_ID}/access_as_user openid profile offline_access`;

/**
 * In-memory store for dynamically registered OAuth clients (RFC 7591).
 * Clients re-register after a server restart — this is fine because the MCP
 * server acts as an OAuth proxy and the real credentials live in Entra ID.
 */
const registeredClients = new Map<string, { name: string; redirectUris: string[] }>();

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

  // --- Protected Resource Metadata (RFC 9728) ---
  // This is the FIRST endpoint Claude Code fetches after receiving a 401.
  // It tells the client where the authorization server lives.
  router.get('/.well-known/oauth-protected-resource', (req, res) => {
    const baseUrl = getPublicBaseUrl(req);
    res.json({
      resource: baseUrl,
      authorization_servers: [baseUrl],
      bearer_methods_supported: ['header'],
      scopes_supported: ['openid', 'profile', 'offline_access', `api://${CLIENT_ID}/access_as_user`],
    });
  });

  // --- OAuth Authorization Server Metadata (RFC 8414) ---
  router.get('/.well-known/oauth-authorization-server', (req, res) => {
    const baseUrl = getPublicBaseUrl(req);
    res.json({
      issuer: baseUrl,
      authorization_endpoint: `${baseUrl}/oauth/authorize`,
      token_endpoint: `${baseUrl}/oauth/token`,
      registration_endpoint: `${baseUrl}/oauth/register`,
      response_types_supported: ['code'],
      grant_types_supported: ['authorization_code', 'refresh_token'],
      code_challenge_methods_supported: ['S256'],
      scopes_supported: ['openid', 'profile', 'offline_access', `api://${CLIENT_ID}/access_as_user`],
    });
  });

  // --- Dynamic Client Registration (RFC 7591) ---
  router.post('/oauth/register', (req, res) => {
    const { client_name, redirect_uris, grant_types, response_types, token_endpoint_auth_method } = req.body ?? {};

    const clientId = crypto.randomUUID();
    registeredClients.set(clientId, {
      name: client_name ?? 'unknown',
      redirectUris: Array.isArray(redirect_uris) ? redirect_uris : [],
    });

    console.error(`[oauth/register] Registered dynamic client ${clientId} (${client_name ?? 'unknown'})`);

    res.status(201).json({
      client_id: clientId,
      client_name: client_name ?? 'unknown',
      redirect_uris: redirect_uris ?? [],
      grant_types: grant_types ?? ['authorization_code', 'refresh_token'],
      response_types: response_types ?? ['code'],
      token_endpoint_auth_method: token_endpoint_auth_method ?? 'none',
    });
  });

  // --- Authorize: redirect to Entra ID ---
  router.get('/oauth/authorize', (req, res) => {
    const baseUrl = getPublicBaseUrl(req);
    const {
      redirect_uri,
      state,
      code_challenge,
      code_challenge_method,
    } = req.query as Record<string, string>;

    // Store Claude Code's redirect_uri in state so we can forward the code back
    const proxyState = Buffer.from(JSON.stringify({
      originalState: state,
      redirectUri: redirect_uri,
    })).toString('base64url');

    // Always use server-defined SCOPES — Claude Code may send scope values
    // (e.g. after dynamic registration) that trigger AADSTS90009 when forwarded
    // to Entra ID, because the app would be requesting a token for itself with
    // an unsupported identifier format.
    const entraParams = new URLSearchParams({
      client_id: CLIENT_ID,
      response_type: 'code',
      redirect_uri: `${baseUrl}/oauth/callback`,
      scope: SCOPES,
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
    if (CLIENT_SECRET) body.set('client_secret', CLIENT_SECRET);
    body.set('redirect_uri', `${baseUrl}/oauth/callback`);

    if (params.grant_type) body.set('grant_type', params.grant_type);
    if (params.code) body.set('code', params.code);
    if (params.refresh_token) body.set('refresh_token', params.refresh_token);
    if (params.code_verifier) body.set('code_verifier', params.code_verifier);
    // Always use server-defined SCOPES (see authorize endpoint comment)
    body.set('scope', SCOPES);

    try {
      const tokenResponse = await fetch(`${AUTHORITY}/oauth2/v2.0/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: body.toString(),
      });

      const data = await tokenResponse.json();
      if (tokenResponse.status !== 200) {
        console.error(`[oauth/token] Entra error: status=${tokenResponse.status}`, JSON.stringify(data));
      }
      res.status(tokenResponse.status).json(data);
    } catch (err) {
      console.error('[oauth] Token exchange failed:', err);
      res.status(502).json({ error: 'token_exchange_failed', error_description: 'Failed to exchange token with identity provider' });
    }
  });

  return router;
}
