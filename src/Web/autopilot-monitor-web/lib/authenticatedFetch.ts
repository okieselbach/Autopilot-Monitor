/**
 * Authenticated fetch wrapper with automatic 401 retry.
 *
 * On a 401 response the wrapper forces an MSAL token refresh and retries the
 * request once.  If the retry also fails with 401, a `TokenExpiredError` is
 * thrown so callers can show a clear "session expired" message instead of a
 * generic backend error.
 */

export class TokenExpiredError extends Error {
  constructor() {
    super('Your session has expired. Please reload the page to sign in again.');
    this.name = 'TokenExpiredError';
  }
}

type GetAccessToken = (forceRefresh?: boolean) => Promise<string | null>;

export async function authenticatedFetch(
  url: string,
  getAccessToken: GetAccessToken,
  init?: RequestInit,
): Promise<Response> {
  const token = await getAccessToken();
  if (!token) {
    throw new TokenExpiredError();
  }

  const headers = new Headers(init?.headers);
  headers.set('Authorization', `Bearer ${token}`);

  const response = await fetch(url, { ...init, headers });

  if (response.status === 401) {
    // Force MSAL to bypass its cache and obtain a fresh token.
    const freshToken = await getAccessToken(true);
    if (!freshToken) {
      throw new TokenExpiredError();
    }

    const retryHeaders = new Headers(init?.headers);
    retryHeaders.set('Authorization', `Bearer ${freshToken}`);

    const retryResponse = await fetch(url, { ...init, headers: retryHeaders });
    if (retryResponse.status === 401) {
      throw new TokenExpiredError();
    }
    return retryResponse;
  }

  return response;
}
