"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { API_BASE_URL } from "@/lib/config";

export default function AdminConfigurationPage() {
  const router = useRouter();
  const [galacticAdminMode, setGalacticAdminMode] = useState(false);
  const [triggeringMaintenance, setTriggeringMaintenance] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Load galactic admin mode from localStorage
  useEffect(() => {
    const galacticMode = localStorage.getItem("galacticAdminMode") === "true";
    setGalacticAdminMode(galacticMode);

    // Redirect if not in galactic admin mode
    if (!galacticMode) {
      router.push("/");
    }
  }, [router]);

  const handleTriggerMaintenance = async () => {
    try {
      setTriggeringMaintenance(true);
      setError(null);
      setSuccessMessage(null);

      const response = await fetch(`${API_BASE_URL}/api/maintenance/trigger`, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(`Failed to trigger maintenance: ${response.statusText}`);
      }

      const result = await response.json();
      setSuccessMessage("Daily maintenance job completed successfully!");

      // Auto-hide success message after 5 seconds
      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      console.error("Error triggering maintenance:", err);
      setError(err instanceof Error ? err.message : "Failed to trigger maintenance job");
    } finally {
      setTriggeringMaintenance(false);
    }
  };

  if (!galacticAdminMode) {
    return null;
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gradient-to-br from-purple-50 to-violet-100">
        {/* Header */}
        <header className="bg-white shadow-sm border-b border-gray-200">
          <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center space-x-4">
                <button
                  onClick={() => router.push("/")}
                  className="flex items-center space-x-2 text-gray-600 hover:text-gray-900 transition-colors"
                >
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" />
                  </svg>
                  <span>Back to Dashboard</span>
                </button>
              </div>
              <div>
                <h1 className="text-2xl font-bold text-purple-900">Admin Configuration</h1>
                <p className="text-sm text-purple-600">Galactic Admin Operations</p>
              </div>
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          <div className="space-y-6">
            {/* Success Message */}
            {successMessage && (
              <div className="bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
                <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span className="text-green-800 font-medium">{successMessage}</span>
              </div>
            )}

            {/* Error Message */}
            {error && (
              <div className="bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
                <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span className="text-red-800">{error}</span>
              </div>
            )}

            {/* Manual Maintenance Trigger */}
            <div className="bg-gradient-to-br from-purple-50 to-violet-50 border-2 border-purple-300 rounded-lg shadow-lg">
              <div className="p-6 border-b border-purple-200 bg-gradient-to-r from-purple-100 to-violet-100">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-purple-900">Manual Maintenance Trigger</h2>
                    <p className="text-sm text-purple-600 mt-1">Execute platform-wide maintenance operations</p>
                  </div>
                </div>
              </div>
              <div className="p-6">
                <div className="flex items-start space-x-4">
                  <div className="flex-1">
                    <p className="text-sm text-gray-600 mb-4">
                      Manually trigger the daily maintenance job which includes:
                    </p>
                    <ul className="text-sm text-gray-600 space-y-1 mb-4 ml-4">
                      <li className="flex items-start">
                        <span className="text-purple-500 mr-2">•</span>
                        <span>Mark stalled sessions as timed out</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-purple-500 mr-2">•</span>
                        <span>Aggregate yesterday's metrics into historical snapshots</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-purple-500 mr-2">•</span>
                        <span>Clean up old data based on retention policies</span>
                      </li>
                    </ul>
                    <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3 mb-4">
                      <div className="flex items-start space-x-2">
                        <svg className="w-5 h-5 text-yellow-600 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                        </svg>
                        <p className="text-sm text-yellow-800">
                          <strong>Warning:</strong> This operation runs across all tenants and may take several minutes to complete.
                          Use this only for testing or when immediate cleanup is needed.
                        </p>
                      </div>
                    </div>
                  </div>
                  <div className="flex-shrink-0">
                    <button
                      onClick={handleTriggerMaintenance}
                      disabled={triggeringMaintenance}
                      className="px-6 py-3 bg-gradient-to-r from-purple-600 to-violet-600 text-white rounded-lg hover:from-purple-700 hover:to-violet-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
                    >
                      {triggeringMaintenance ? (
                        <>
                          <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white"></div>
                          <span>Running...</span>
                        </>
                      ) : (
                        <>
                          <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z" />
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                          </svg>
                          <span>Run Now</span>
                        </>
                      )}
                    </button>
                  </div>
                </div>
              </div>
            </div>

            {/* Future Admin Functions Placeholder */}
            <div className="bg-white border border-gray-200 rounded-lg shadow p-6">
              <div className="flex items-center space-x-2 mb-4">
                <svg className="w-6 h-6 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4" />
                </svg>
                <h3 className="text-lg font-semibold text-gray-900">Additional Admin Functions</h3>
              </div>
              <p className="text-sm text-gray-600">
                Additional cross-tenant administrative operations will be added here as needed.
              </p>
            </div>
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
