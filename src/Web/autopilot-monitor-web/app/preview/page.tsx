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
        router.push("/dashboard");
      } else {
        router.push("/progress");
      }
    }
    if (!isLoading && !isAuthenticated) {
      router.push("/");
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
            {previewMessage || "Autopilot Monitor is currently in private preview. Your organization has been added to the waitlist."}
          </p>

          <div className="bg-blue-50 rounded-lg p-4 mb-6">
            <p className="text-sm text-blue-700">
              Signed in as <span className="font-semibold">{user?.upn}</span>
            </p>
            <p className="text-xs text-blue-500 mt-1">
              Tenant: {user?.tenantId}
            </p>
          </div>

          <div className="text-left bg-amber-50 border border-amber-200 rounded-lg p-4 mb-6">
            <p className="text-sm font-semibold text-amber-900 mb-1">Next steps</p>
            <p className="text-sm text-amber-800">
              Please sign out and contact me on LinkedIn or open a GitHub issue to request access to the Private Preview.
            </p>
            <div className="flex flex-wrap gap-2 mt-3">
              <a
                href="https://www.linkedin.com/in/oliver-kieselbach/"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium text-blue-700 bg-blue-100 rounded-md hover:bg-blue-200 transition-colors"
              >
                LinkedIn
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                </svg>
              </a>
              <a
                href="https://github.com/okieselbach/Autopilot-Monitor/issues"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm font-medium text-blue-700 bg-blue-100 rounded-md hover:bg-blue-200 transition-colors"
              >
                GitHub Issues
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" strokeWidth={2} stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                </svg>
              </a>
            </div>
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
