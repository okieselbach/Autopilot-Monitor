import { PublicClientApplication, LogLevel } from '@azure/msal-node';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { homedir } from 'node:os';
import { exec } from 'node:child_process';

const CLIENT_ID = process.env.AUTOPILOT_ENTRA_CLIENT_ID ?? '1a400946-62c1-4ab4-aa37-f730ac89704d';
const AUTHORITY = process.env.AUTOPILOT_ENTRA_AUTHORITY ?? 'https://login.microsoftonline.com/organizations';
const SCOPES = [`api://${CLIENT_ID}/access_as_user`];
const CACHE_DIR = resolve(homedir(), '.autopilot-monitor');
const CACHE_FILE = resolve(CACHE_DIR, 'auth-cache.json');

let pca: PublicClientApplication | null = null;

async function loadCache(): Promise<string | undefined> {
  try {
    return await readFile(CACHE_FILE, 'utf-8');
  } catch {
    return undefined;
  }
}

async function saveCache(cache: string): Promise<void> {
  try {
    await mkdir(CACHE_DIR, { recursive: true });
    await writeFile(CACHE_FILE, cache, { mode: 0o600 });
  } catch (err) {
    console.error('[auth] Failed to persist token cache:', err);
  }
}

function getClient(): PublicClientApplication {
  if (!pca) {
    pca = new PublicClientApplication({
      auth: {
        clientId: CLIENT_ID,
        authority: AUTHORITY,
      },
      system: {
        loggerOptions: {
          loggerCallback: (_level, message) => {
            if (_level <= LogLevel.Warning) {
              console.error(`[msal] ${message}`);
            }
          },
          logLevel: LogLevel.Warning,
          piiLoggingEnabled: false,
        },
      },
    });
  }
  return pca;
}

/** Open a URL in the system default browser */
function openBrowser(url: string): Promise<void> {
  return new Promise((resolve) => {
    const cmd = process.platform === 'win32'
      ? `start "" "${url}"`
      : process.platform === 'darwin'
        ? `open "${url}"`
        : `xdg-open "${url}" 2>/dev/null`;
    exec(cmd, () => resolve());
  });
}

/**
 * Acquires an access token — tries silent refresh first, falls back to interactive browser login.
 * All log output goes to stderr (stdout is the MCP JSON-RPC transport).
 */
export async function getAccessToken(): Promise<string> {
  const client = getClient();

  // Load persisted cache
  const cacheData = await loadCache();
  if (cacheData) {
    client.getTokenCache().deserialize(cacheData);
  }

  // Try silent acquisition first (cached token or refresh)
  const accounts = await client.getTokenCache().getAllAccounts();
  if (accounts.length > 0) {
    try {
      const result = await client.acquireTokenSilent({
        account: accounts[0],
        scopes: SCOPES,
      });
      if (result?.accessToken) {
        await saveCache(client.getTokenCache().serialize());
        return result.accessToken;
      }
    } catch {
      // Silent failed — fall through to interactive login
    }
  }

  // Interactive browser login — opens system browser, listens on localhost for redirect
  console.error('');
  console.error('=== Authentication Required ===');
  console.error('Opening browser for sign-in…');
  console.error('');

  const result = await client.acquireTokenInteractive({
    scopes: SCOPES,
    openBrowser,
  });

  if (!result?.accessToken) {
    throw new Error('Interactive authentication failed — no access token received');
  }

  // Persist cache after successful auth
  await saveCache(client.getTokenCache().serialize());

  console.error(`[auth] Authenticated as ${result.account?.username ?? 'unknown'}`);
  return result.accessToken;
}

/**
 * Returns a summary of the current auth state.
 */
export async function getAuthStatus(): Promise<{
  mode: string;
  hasCachedToken: boolean;
  user?: string;
  tenantId?: string;
}> {
  const client = getClient();
  const cacheData = await loadCache();
  if (cacheData) {
    client.getTokenCache().deserialize(cacheData);
  }

  const accounts = await client.getTokenCache().getAllAccounts();
  if (accounts.length > 0) {
    return {
      mode: 'oauth',
      hasCachedToken: true,
      user: accounts[0].username,
      tenantId: accounts[0].tenantId,
    };
  }

  return { mode: 'oauth', hasCachedToken: false };
}
