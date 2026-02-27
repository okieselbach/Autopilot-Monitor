"use client";

import { useAuth } from "../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect, useRef } from "react";

interface ProtectedRouteProps {
  children: React.ReactNode;
  requireGalacticAdmin?: boolean;
}

/**
 * Protects routes by requiring authentication
 * Optionally requires Galactic Admin role
 */
export function ProtectedRoute({ children, requireGalacticAdmin = false }: ProtectedRouteProps) {
  const { isAuthenticated, user, isLoading } = useAuth();
  const router = useRouter();

  // Once authenticated, remember it so transient auth-state flips (e.g. MSAL
  // handleRedirectPromise re-settling) don't unmount/flash the children.
  const wasAuthenticated = useRef(false);
  if (isAuthenticated) {
    wasAuthenticated.current = true;
  }

  useEffect(() => {
    // Only redirect if MSAL has fully settled (!isLoading) and we were never
    // authenticated in this session. wasAuthenticated guards against transient
    // isAuthenticated=false flips during MSAL's double handleRedirectPromise cycle.
    if (!isLoading && !isAuthenticated && !wasAuthenticated.current) {
      router.push("/");
    }
  }, [isAuthenticated, isLoading, router]);

  // Show loading state only on the very first load (never been authenticated yet)
  if (isLoading && !wasAuthenticated.current) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  // Show nothing if never authenticated (will redirect)
  if (!isAuthenticated && !wasAuthenticated.current) {
    return null;
  }

  // Show nothing if requires galactic admin but user is not (will redirect)
  if (requireGalacticAdmin && user && !user.isGalacticAdmin) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 flex items-center justify-center p-4">
        <div className="bg-white rounded-lg shadow-xl p-8 max-w-md w-full text-center">
          <svg className="h-12 w-12 text-red-500 mx-auto mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900 mb-2">Access Denied</h2>
          <p className="text-gray-600 mb-6">You need Galactic Admin permissions to access this page.</p>
          <button
            onClick={() => router.push("/dashboard")}
            className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 transition-colors"
          >
            Back to Home
          </button>
        </div>
      </div>
    );
  }

  // Render children if authenticated (and galactic admin if required)
  return <>{children}</>;
}
