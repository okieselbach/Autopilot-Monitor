"use client";

import React, { createContext, useContext, useEffect, useState, useCallback } from 'react';
import { PublicClientApplication, AccountInfo, InteractionStatus } from '@azure/msal-browser';
import { MsalProvider, useMsal, useIsAuthenticated } from '@azure/msal-react';
import { msalConfig, loginRequest, apiRequest } from '@/lib/msalConfig';
import { API_BASE_URL } from '@/lib/config';

// Initialize MSAL instance
const msalInstance = new PublicClientApplication(msalConfig);

// Initialize MSAL
msalInstance.initialize().then(() => {
  // Handle redirect promise
  msalInstance.handleRedirectPromise().catch((error) => {
    console.error('[Auth] Redirect error:', error);
  });
});

interface UserInfo {
  displayName: string;
  upn: string;
  tenantId: string;
  objectId: string;
  isGalacticAdmin: boolean;
  isTenantAdmin: boolean;
}

interface AuthContextType {
  isAuthenticated: boolean;
  user: UserInfo | null;
  isLoading: boolean;
  login: () => Promise<void>;
  logout: () => Promise<void>;
  getAccessToken: () => Promise<string | null>;
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
      // Get access token for API
      const tokenResponse = await instance.acquireTokenSilent({
        scopes: apiRequest.scopes,
        account: account,
      });

      // Call backend API to get user info including galactic admin status
      const response = await fetch(`${API_BASE_URL}/api/auth/me`, {
        headers: {
          'Authorization': `Bearer ${tokenResponse.accessToken}`,
        },
      });

      if (!response.ok) {
        // Check for tenant suspension
        if (response.status === 403) {
          const errorData = await response.json();
          if (errorData.error === 'TenantSuspended') {
            console.error('[Auth] Tenant suspended:', errorData.message);
            // Display error to user
            alert(`Access Denied\n\n${errorData.message}`);
            // Logout user
            await instance.logoutRedirect({ account });
            return null;
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
        isGalacticAdmin: data.isGalacticAdmin || false,
        isTenantAdmin: data.isTenantAdmin || false,
      };
    } catch (error) {
      console.error('[Auth] Failed to fetch user info:', error);

      // Fallback to token claims if API call fails
      return {
        displayName: account.name || '',
        upn: account.username || '',
        tenantId: account.tenantId || '',
        objectId: account.homeAccountId || '',
        isGalacticAdmin: false,
        isTenantAdmin: false,
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
   * Load user info when authentication state changes
   */
  useEffect(() => {
    const loadUserInfo = async () => {
      if (inProgress === InteractionStatus.None) {
        if (accounts.length > 0) {
          const userInfo = await fetchUserInfo(accounts[0]);
          setUser(userInfo);
        } else {
          setUser(null);
        }
        setIsLoading(false);
      }
    };

    loadUserInfo();

    // Fallback: if MSAL doesn't initialize within 3 seconds, set loading to false anyway
    const timeout = setTimeout(() => {
      if (isLoading) {
        console.warn('[Auth] MSAL initialization timeout - setting isLoading to false');
        setIsLoading(false);
      }
    }, 3000);

    return () => clearTimeout(timeout);
  }, [accounts, inProgress, fetchUserInfo, isLoading]);

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
    } catch (error: any) {
      // Ignore interaction_in_progress errors - this can happen if user clicks button multiple times
      if (error?.errorCode === 'interaction_in_progress') {
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
  const getAccessToken = useCallback(async (): Promise<string | null> => {
    if (accounts.length === 0) {
      return null;
    }

    try {
      const response = await instance.acquireTokenSilent({
        scopes: apiRequest.scopes,
        account: accounts[0],
      });

      return response.accessToken;
    } catch (error) {
      console.error('[Auth] Token acquisition error:', error);

      // If silent token acquisition fails, trigger interactive redirect
      // Note: acquireTokenRedirect doesn't return a token, it redirects the browser
      // The token will be available after the redirect via handleRedirectPromise
      try {
        await instance.acquireTokenRedirect({
          scopes: apiRequest.scopes,
          account: accounts[0],
        });
        // This line won't be reached as the browser will redirect
        return null;
      } catch (interactiveError) {
        console.error('[Auth] Interactive token acquisition error:', interactiveError);
        return null;
      }
    }
  }, [instance, accounts]);

  const value: AuthContextType = {
    isAuthenticated,
    user,
    isLoading,
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
