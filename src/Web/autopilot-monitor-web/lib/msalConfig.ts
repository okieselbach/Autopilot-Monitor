import { Configuration, LogLevel, RedirectRequest } from "@azure/msal-browser";

/**
 * MSAL Configuration for Multi-Tenant Azure AD Authentication
 *
 * Environment Variables:
 * - NEXT_PUBLIC_ENTRA_CLIENT_ID: Application (client) ID from App Registration
 * - NEXT_PUBLIC_ENTRA_REDIRECT_URI: Redirect URI configured in App Registration
 * - NEXT_PUBLIC_ENTRA_POST_LOGOUT_REDIRECT_URI: Post logout redirect URI
 */

// MSAL Configuration
export const msalConfig: Configuration = {
  auth: {
    clientId: process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID || "YOUR_CLIENT_ID_HERE",
    authority: "https://login.microsoftonline.com/organizations", // Multi-tenant
    redirectUri: process.env.NEXT_PUBLIC_ENTRA_REDIRECT_URI || "http://localhost:3000",
    postLogoutRedirectUri: process.env.NEXT_PUBLIC_ENTRA_POST_LOGOUT_REDIRECT_URI || "http://localhost:3000",
    navigateToLoginRequestUrl: false,
  },
  cache: {
    cacheLocation: "sessionStorage", // Using sessionStorage for better XSS protection
    storeAuthStateInCookie: false, // Set to true for IE11 or Edge legacy
  },
  system: {
    loggerOptions: {
      loggerCallback: (level, message, containsPii) => {
        if (containsPii) {
          return;
        }
        switch (level) {
          case LogLevel.Error:
            console.error(`[MSAL] ${message}`);
            return;
          case LogLevel.Info:
            console.info(`[MSAL] ${message}`);
            return;
          case LogLevel.Verbose:
            console.debug(`[MSAL] ${message}`);
            return;
          case LogLevel.Warning:
            console.warn(`[MSAL] ${message}`);
            return;
          default:
            return;
        }
      },
      logLevel: LogLevel.Info,
      piiLoggingEnabled: false,
    },
    allowNativeBroker: false, // Disables WAM Broker
  },
};

/**
 * Scopes you add here will be prompted for user consent during sign-in.
 * By default, MSAL.js will add OIDC scopes (openid, profile, email) to any login request.
 * For more information about OIDC scopes, visit:
 * https://docs.microsoft.com/en-us/azure/active-directory/develop/v2-permissions-and-consent#openid-connect-scopes
 */
export const loginRequest: RedirectRequest = {
  scopes: [
    "User.Read", // Microsoft Graph - read user profile
  ],
  prompt: "select_account", // Force account selection on login
};

/**
 * Scopes for accessing the backend API
 * IMPORTANT: Backend API must be exposed in Azure AD App Registration with this scope
 * Format: api://<backend-client-id>/access_as_user
 * For testing, you can also use User.Read but disable signature validation in backend
 */
export const apiRequest = {
  scopes: [
    // Option 1 (recommended): Use backend API scope after exposing API in Azure AD
    `api://${process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID}/access_as_user`
  ],
};

/**
 * Protected resource map for token acquisition
 * Maps API endpoints to their required scopes
 */
export const protectedResources = {
  api: {
    endpoint: process.env.NEXT_PUBLIC_API_BASE_URL || "http://localhost:7071",
    scopes: apiRequest.scopes,
  },
};
