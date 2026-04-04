"use client";

import React, { createContext, useContext, useEffect, useState, useCallback, useRef } from 'react';
import { PublicClientApplication, AccountInfo, InteractionStatus, InteractionRequiredAuthError, BrowserAuthError } from '@azure/msal-browser';
import { MsalProvider, useMsal, useIsAuthenticated } from '@azure/msal-react';
import { msalConfig, loginRequest, apiRequest } from '@/lib/msalConfig';
import { api } from '@/lib/api';

// Initialize MSAL instance
const msalInstance = new PublicClientApplication(msalConfig);

// Track MSAL initialization state so components can wait for it.
let msalReady = false;

// Prefetch: store auth/me result fetched during MSAL init so fetchUserInfo
// can use it immediately without an extra network round-trip.
// Runs as a fire-and-forget side-effect — MUST NOT block msalInitPromise,
// otherwise a cold backend would keep the UI on a white screen.
let prefetchedAuthMe: Record<string, unknown> | null = null;

const msalInitPromise = msalInstance
  .initialize()
  .then(() => msalInstance.handleRedirectPromise())
  .then(() => {
    msalReady = true;

    // Fire-and-forget: prefetch auth/me while React is still mounting.
    // Not awaited — if it finishes before fetchUserInfo runs, great;
    // if not, fetchUserInfo does its own fetch as before.
    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) {
      msalInstance.acquireTokenSilent({
        scopes: apiRequest.scopes,
        account: accounts[0],
      }).then(async (tokenResponse) => {
        const res = await fetch(api.auth.me(), {
          headers: { 'Authorization': `Bearer ${tokenResponse.accessToken}` },
          signal: AbortSignal.timeout(8000),
        });
        if (res.ok) {
          prefetchedAuthMe = await res.json();
        }
      }).catch(() => {
        // Best-effort; fetchUserInfo will retry normally.
      });
    }
  })
  .catch((error) => {
    console.error('[Auth] MSAL initialization/redirect error:', error);
    // Mark as ready even on error so the app doesn't hang forever.
    // Auth operations will fail individually and trigger appropriate recovery.
    msalReady = true;
  });

/**
 * Module-level guard: only one acquireTokenRedirect may be in-flight at a time.
 * Multiple call-sites (ProtectedRoute, getAccessToken, fetchUserInfo) can all
 * independently decide that a redirect is needed.  Without this gate the second
 * call throws BrowserAuthError: interaction_in_progress which is unrecoverable
 * and causes the "Application error" crash on mobile.
 */
let redirectInFlight = false;

async function safeAcquireTokenRedirect(
  instance: PublicClientApplication,
  account: AccountInfo | undefined,
): Promise<void> {
  if (redirectInFlight) {
    console.log('[Auth] Redirect already in-flight, skipping duplicate');
    return;
  }
  redirectInFlight = true;
  try {
    await instance.acquireTokenRedirect({
      scopes: apiRequest.scopes,
      account,
    });
  } catch (err) {
    // interaction_in_progress means another redirect beat us — not an error.
    if (err instanceof BrowserAuthError && err.errorCode === 'interaction_in_progress') {
      console.log('[Auth] Redirect already in progress (BrowserAuthError), ignoring');
    } else {
      console.error('[Auth] acquireTokenRedirect failed:', err);
    }
  } finally {
    redirectInFlight = false;
  }
}

interface UserInfo {
  displayName: string;
  upn: string;
  tenantId: string;
  objectId: string;
  isGlobalAdmin: boolean;
  isTenantAdmin: boolean;
  role: 'Admin' | 'Operator' | 'Viewer' | null;
  canManageBootstrapTokens: boolean;
  hasMcpAccess: boolean;
}

interface AuthContextType {
  isAuthenticated: boolean;
  user: UserInfo | null;
  isLoading: boolean;
  isPreviewBlocked: boolean;
  previewMessage: string;
  login: () => Promise<void>;
  logout: () => Promise<void>;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
  refreshUserInfo: () => Promise<void>;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

/**
 * Internal Auth Provider that uses MSAL hooks
 * This component must be inside MsalProvider
 */
function AuthProviderInternal({ children }: { children: React.ReactNode }) {
  const { instance, accounts, inProgress } = useMsal();
  const isAuthenticated = useIsAuthenticated();
  const [user, setUser] = useState<UserInfo | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const isLoadingRef = useRef(true);
  const [isPreviewBlocked, setPreviewBlocked] = useState(false);
  const [previewMessage, setPreviewMessage] = useState('');

  // Handle SSR - if we're on the server, MSAL won't initialize
  // Set loading to false immediately on mount in browser
  useEffect(() => {
    if (typeof window === 'undefined') {
      setIsLoading(false);
    }
  }, []);

  /**
   * Fetches user info from backend API
   */
  const fetchUserInfo = useCallback(async (account: AccountInfo): Promise<UserInfo | null> => {
    try {
      // Use prefetched auth/me result if available (fetched during MSAL init).
      // Consume it exactly once to avoid stale data on subsequent calls.
      if (prefetchedAuthMe) {
        const data = prefetchedAuthMe;
        prefetchedAuthMe = null;
        return {
          displayName: (data.displayName as string) || account.name || '',
          upn: (data.upn as string) || account.username || '',
          tenantId: (data.tenantId as string) || account.tenantId || '',
          objectId: (data.objectId as string) || account.homeAccountId || '',
          isGlobalAdmin: (data.isGlobalAdmin as boolean) || false,
          isTenantAdmin: (data.isTenantAdmin as boolean) || false,
          role: (data.role as 'Admin' | 'Operator' | 'Viewer' | null) || null,
          canManageBootstrapTokens: (data.canManageBootstrapTokens as boolean) || false,
          hasMcpAccess: (data.hasMcpAccess as boolean) || false,
        };
      }

      // Get access token for API
      const tokenResponse = await instance.acquireTokenSilent({
        scopes: apiRequest.scopes,
        account: account,
      });

      // Call backend API to get user info including global admin status.
      // 8-second timeout so a cold Azure Function start does not block the
      // landing page spinner indefinitely — the catch block falls back to
      // token claims so the user can still log in.
      const authMeController = new AbortController();
      const authMeTimeout = setTimeout(() => authMeController.abort(), 8000);
      let response: Response;
      try {
        response = await fetch(api.auth.me(), {
          headers: {
            'Authorization': `Bearer ${tokenResponse.accessToken}`,
          },
          signal: authMeController.signal,
        });
      } finally {
        clearTimeout(authMeTimeout);
      }

      if (!response.ok) {
        if (response.status === 403) {
          const errorData = await response.json();
          if (errorData.error === 'TenantSuspended') {
            console.error('[Auth] Tenant suspended:', errorData.message);
            alert(`Access Denied\n\n${errorData.message}`);
            await instance.logoutRedirect({ account });
            return null;
          }
          if (errorData.error === 'PrivatePreview') {
            console.log('[Auth] Tenant not yet approved for preview');
            setPreviewBlocked(true);
            setPreviewMessage(errorData.message || 'Your organization is on the waitlist.');
            // Return basic user info so the user stays logged in but sees the preview page
            return {
              displayName: account.name || '',
              upn: account.username || '',
              tenantId: account.tenantId || '',
              objectId: account.homeAccountId || '',
              isGlobalAdmin: false,
              isTenantAdmin: false,
              role: null,
              canManageBootstrapTokens: false,
              hasMcpAccess: false,
            };
          }
        }
        throw new Error(`Failed to fetch user info: ${response.statusText}`);
      }

      const data = await response.json();

      return {
        displayName: data.displayName || account.name || '',
        upn: data.upn || account.username || '',
        tenantId: data.tenantId || account.tenantId || '',
        objectId: data.objectId || account.homeAccountId || '',
        isGlobalAdmin: data.isGlobalAdmin || false,
        isTenantAdmin: data.isTenantAdmin || false,
        role: data.role || null,
        canManageBootstrapTokens: data.canManageBootstrapTokens || false,
        hasMcpAccess: data.hasMcpAccess || false,
      };
    } catch (error) {
      // If the refresh token is expired or consent is required, redirect to
      // interactive login immediately instead of falling back to stale claims.
      if (error instanceof InteractionRequiredAuthError) {
        console.warn('[Auth] Interactive login required — redirecting:', error.errorCode);
        await safeAcquireTokenRedirect(instance as PublicClientApplication, account);
        return null;
      }

      // interaction_in_progress — another redirect is already handling this.
      if (error instanceof BrowserAuthError && error.errorCode === 'interaction_in_progress') {
        console.log('[Auth] Interaction already in progress during fetchUserInfo, waiting');
        return null;
      }

      console.error('[Auth] Failed to fetch user info:', error);

      // Fallback to token claims only for non-auth errors (network issues,
      // backend cold starts, etc.) so the user can still see the app.
      return {
        displayName: account.name || '',
        upn: account.username || '',
        tenantId: account.tenantId || '',
        objectId: account.homeAccountId || '',
        isGlobalAdmin: false,
        isTenantAdmin: false,
        role: null,
        canManageBootstrapTokens: false,
        hasMcpAccess: false,
      };
    }
  }, [instance]);

  /**
   * Refreshes user information from backend
   */
  const refreshUserInfo = useCallback(async () => {
    if (accounts.length > 0) {
      const userInfo = await fetchUserInfo(accounts[0]);
      setUser(userInfo);
    }
  }, [accounts, fetchUserInfo]);

  /**
   * Load user info when authentication state changes.
   * Waits for MSAL to be fully initialized before proceeding.
   */
  useEffect(() => {
    const loadUserInfo = async () => {
      // Wait for MSAL initialization to complete before evaluating auth state.
      // This prevents the 3-second timeout from firing prematurely while MSAL
      // is still processing the redirect promise.
      if (!msalReady) {
        await msalInitPromise;
      }

      if (inProgress === InteractionStatus.None) {
        if (accounts.length > 0) {
          const userInfo = await fetchUserInfo(accounts[0]);
          setUser(userInfo);
        } else {
          setUser(null);
        }
        isLoadingRef.current = false;
        setIsLoading(false);
      }
    };

    loadUserInfo();

    // Fallback: if MSAL doesn't settle within 5 seconds, set loading to false
    // anyway so the user isn't stuck on a spinner forever.
    const timeout = setTimeout(() => {
      if (isLoadingRef.current) {
        console.warn('[Auth] MSAL initialization timeout - setting isLoading to false');
        isLoadingRef.current = false;
        setIsLoading(false);
      }
    }, 5000);

    return () => clearTimeout(timeout);
  }, [accounts, inProgress, fetchUserInfo]);

  /**
   * Initiates login flow
   */
  const login = useCallback(async () => {
    // Check if an interaction is already in progress
    if (inProgress !== InteractionStatus.None) {
      console.log('[Auth] Interaction already in progress, skipping login');
      return;
    }

    try {
      await instance.loginRedirect(loginRequest);
    } catch (error: unknown) {
      // Ignore interaction_in_progress errors - this can happen if user clicks button multiple times
      // or if another part of the app already triggered a redirect.
      if (error instanceof Error && 'errorCode' in error && error.errorCode === 'interaction_in_progress') {
        console.log('[Auth] Interaction already in progress, ignoring duplicate login attempt');
        return;
      }
      console.error('[Auth] Login error:', error);
      throw error;
    }
  }, [instance, inProgress]);

  /**
   * Initiates logout flow
   */
  const logout = useCallback(async () => {
    try {
      await instance.logoutRedirect({
        account: accounts[0],
      });
    } catch (error) {
      console.error('[Auth] Logout error:', error);
      throw error;
    }
  }, [instance, accounts]);

  /**
   * Gets access token for API calls
   * Automatically handles token refresh
   */
  const getAccessToken = useCallback(async (forceRefresh?: boolean): Promise<string | null> => {
    if (accounts.length === 0) {
      return null;
    }

    try {
      const response = await instance.acquireTokenSilent({
        scopes: apiRequest.scopes,
        account: accounts[0],
        forceRefresh: forceRefresh ?? false,
      });

      return response.accessToken;
    } catch (error) {
      // interaction_in_progress — another redirect is already in flight.
      // Return null and let the redirect complete; the page will reload.
      if (error instanceof BrowserAuthError && error.errorCode === 'interaction_in_progress') {
        console.log('[Auth] Interaction already in progress during getAccessToken, returning null');
        return null;
      }

      console.error('[Auth] Token acquisition error:', error);

      // If silent token acquisition fails, trigger interactive redirect
      // via the guarded helper to avoid duplicate redirects.
      await safeAcquireTokenRedirect(instance as PublicClientApplication, accounts[0]);
      // Browser will redirect; this line is only reached if the redirect was skipped.
      return null;
    }
  }, [instance, accounts]);

  const value: AuthContextType = {
    isAuthenticated,
    user,
    isLoading,
    isPreviewBlocked,
    previewMessage,
    login,
    logout,
    getAccessToken,
    refreshUserInfo,
  };

  return (
    <AuthContext.Provider value={value}>
      {children}
    </AuthContext.Provider>
  );
}

/**
 * Main Auth Provider that wraps MsalProvider
 */
export function AuthProvider({ children }: { children: React.ReactNode }) {
  return (
    <MsalProvider instance={msalInstance}>
      <AuthProviderInternal>
        {children}
      </AuthProviderInternal>
    </MsalProvider>
  );
}

/**
 * Hook to use auth context
 */
export function useAuth() {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}
