"use client";

import { useAuth } from "../../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect } from "react";

export default function PreviewPage() {
  const { isAuthenticated, isLoading, user, isPreviewBlocked, previewMessage, logout } = useAuth();
  const router = useRouter();

  // If not preview-blocked (e.g. approved tenant navigates here), redirect away
  useEffect(() => {
    if (!isLoading && isAuthenticated && user && !isPreviewBlocked) {
      if (user.isTenantAdmin || user.isGalacticAdmin) {
        router.push("/");
      } else {
        router.push("/progress");
      }
    }
    if (!isLoading && !isAuthenticated) {
      router.push("/landing");
    }
  }, [isAuthenticated, isLoading, user, isPreviewBlocked, router]);

  if (isLoading) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50 flex items-center justify-center px-6">
      <div className="max-w-lg w-full text-center">
        {/* Logo */}
        <div className="flex items-center justify-center space-x-3 mb-8">
          <div className="w-12 h-12 bg-gradient-to-br from-blue-600 to-indigo-600 rounded-lg flex items-center justify-center">
            <svg className="w-7 h-7 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
          </div>
          <span className="text-2xl font-bold bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent">
            Autopilot Monitor
          </span>
        </div>

        {/* Card */}
        <div className="bg-white rounded-2xl shadow-xl p-10">
          {/* Clock icon */}
          <div className="w-16 h-16 bg-amber-100 rounded-full flex items-center justify-center mx-auto mb-6">
            <svg className="w-8 h-8 text-amber-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
          </div>

          <h1 className="text-2xl font-bold text-gray-900 mb-3">
            Private Preview
          </h1>

          <p className="text-gray-600 mb-6 leading-relaxed">
            {previewMessage || "Autopilot Monitor is currently in Private Preview. Your organization is on the waitlist \u2014 we'll notify you when access is granted."}
          </p>

          <div className="bg-blue-50 rounded-lg p-4 mb-6">
            <p className="text-sm text-blue-700">
              Signed in as <span className="font-semibold">{user?.upn}</span>
            </p>
            <p className="text-xs text-blue-500 mt-1">
              Tenant: {user?.tenantId}
            </p>
          </div>

          <button
            onClick={logout}
            className="px-6 py-3 bg-gray-100 text-gray-700 rounded-lg font-semibold hover:bg-gray-200 transition-colors"
          >
            Sign Out
          </button>
        </div>

        <p className="mt-6 text-sm text-gray-400">
          &copy; 2026 Autopilot Monitor. Powered by Azure and Microsoft Identity.
        </p>
      </div>
    </div>
  );
}
