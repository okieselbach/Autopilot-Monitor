"use client";

import { useEffect, useState, useRef } from "react";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

export function SettingsMenu({
  apiStatus,
  onCheckHealth,
  adminMode,
  onAdminModeChange,
  galacticAdminMode,
  onGalacticAdminModeChange,
  user,
}: {
  apiStatus: "unchecked" | "checking" | "healthy" | "error";
  onCheckHealth: () => void;
  adminMode: boolean;
  onAdminModeChange: (enabled: boolean) => void;
  galacticAdminMode: boolean;
  onGalacticAdminModeChange: (enabled: boolean) => void;
  user: { displayName: string; email: string; isGalacticAdmin?: boolean } | null;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const [showAuditLog, setShowAuditLog] = useState(false);
  const [auditLogs, setAuditLogs] = useState<any[]>([]);
  const [loadingLogs, setLoadingLogs] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();

  const fetchAuditLogs = async () => {
    setLoadingLogs(true);
    try {
      const response = await authenticatedFetch(`${API_BASE_URL}/api/audit/logs?tenantId=${tenantId}`, getAccessToken);

      if (response.ok) {
        const data = await response.json();
        setAuditLogs(data.logs || []);
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        console.error('Session expired:', error.message);
      } else {
        console.error("Failed to fetch audit logs:", error);
      }
    } finally {
      setLoadingLogs(false);
    }
  };

  const statusColors = {
    unchecked: "text-gray-500",
    checking: "text-yellow-600",
    healthy: "text-green-600",
    error: "text-red-600",
  };

  const statusIcons = {
    unchecked: "🔘",
    checking: "🔄",
    healthy: "✅",
    error: "❌",
  };

  const statusText = {
    unchecked: "Not checked",
    checking: "Checking...",
    healthy: "Connected",
    error: "Not connected",
  };

  // Close dropdown when clicking outside
  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        setIsOpen(false);
      }
    }

    if (isOpen) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => {
        document.removeEventListener("mousedown", handleClickOutside);
      };
    }
  }, [isOpen]);

  return (
    <div className="relative" ref={menuRef}>
      {/* Settings Button */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="p-2 rounded-full hover:bg-gray-100 transition-colors"
        aria-label="Settings"
        title="Settings"
      >
        <svg className="h-5 w-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
      </button>

      {/* Dropdown Menu */}
      {isOpen && (
        <div className="absolute right-0 mt-2 w-72 bg-white rounded-lg shadow-lg border border-gray-200 z-50">
          <div className="p-4">
            <h3 className="text-sm font-semibold text-gray-900 mb-3">Settings</h3>

            {/* Admin Mode Toggle */}
            <div className="mb-3">
              <div className="flex items-center justify-between p-3 rounded-lg bg-gray-50">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-gray-700">Admin Mode</span>
                  {adminMode && <span className="text-xs text-red-600 font-semibold">AKTIV</span>}
                </div>
                <button
                  onClick={() => onAdminModeChange(!adminMode)}
                  className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                    adminMode ? 'bg-red-600' : 'bg-gray-300'
                  }`}
                >
                  <span
                    className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                      adminMode ? 'translate-x-6' : 'translate-x-1'
                    }`}
                  />
                </button>
              </div>
              {adminMode && (
                <p className="mt-2 text-xs text-red-600 px-3 whitespace-nowrap">
                  ⚠️ Allows deleting sessions
                </p>
              )}
            </div>

            {/* Galactic Admin Toggle - Only visible to actual galactic admins */}
            {user?.isGalacticAdmin && (
              <div className="mb-3">
                <div className="flex items-center justify-between p-3 rounded-lg bg-purple-50">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium text-gray-700">Galactic Admin</span>
                    {galacticAdminMode && <span className="text-xs text-purple-700 font-semibold">ACTIVE</span>}
                  </div>
                  <button
                    onClick={() => onGalacticAdminModeChange(!galacticAdminMode)}
                    className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                      galacticAdminMode ? 'bg-purple-600' : 'bg-gray-300'
                    }`}
                  >
                    <span
                      className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                        galacticAdminMode ? 'translate-x-6' : 'translate-x-1'
                      }`}
                    />
                  </button>
                </div>
                {galacticAdminMode && (
                  <p className="mt-2 text-xs text-purple-700 px-3">
                    Shows ALL sessions across ALL tenants
                  </p>
                )}
              </div>
            )}

            {/* API Status Section - Clickable */}
            <div className="border-t border-gray-200 pt-3">
              <button
                onClick={onCheckHealth}
                disabled={apiStatus === "checking"}
                className="w-full p-3 text-left rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                title={apiStatus === "unchecked" ? "Click to check API status" : undefined}
              >
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-gray-700">API Status</span>
                  <span className={`text-sm font-medium ${statusColors[apiStatus]}`}>
                    {statusIcons[apiStatus]} {statusText[apiStatus]}
                  </span>
                </div>
              </button>
            </div>

            {/* Configuration Section */}
            <div className="border-t border-gray-200 pt-3 mt-3">
              <a
                href="/settings"
                className="block w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors"
              >
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-gray-700">Configuration</span>
                  <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
                  </svg>
                </div>
              </a>
            </div>

            {/* Usage Metrics Section - Always visible for tenant */}
            <div className="border-t border-gray-200 pt-3">
              <a
                href="/usage-metrics"
                className="block w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors"
              >
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-gray-700">Usage Metrics</span>
                  <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                  </svg>
                </div>
              </a>
            </div>

            {/* Platform Usage Metrics Section - Galactic Admin Only */}
            {galacticAdminMode && (
              <div className="border-t border-gray-200 pt-3">
                <a
                  href="/admin/metrics/usage"
                  className="block w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors"
                >
                  <div className="flex items-center justify-between">
                    <span className="text-sm font-medium text-gray-700">Platform Usage Metrics</span>
                    <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                  </div>
                </a>
              </div>
            )}

            {/* Audit Log Section */}
            <div className="border-t border-gray-200 pt-3">
              <button
                onClick={() => {
                  setShowAuditLog(true);
                  fetchAuditLogs();
                }}
                className="w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors"
              >
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-gray-700">Audit Log</span>
                  <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                  </svg>
                </div>
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Audit Log Modal */}
      {showAuditLog && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-[60]" onClick={() => setShowAuditLog(false)}>
          <div className="bg-white rounded-lg shadow-xl max-w-4xl w-full max-h-[80vh] overflow-hidden" onClick={(e) => e.stopPropagation()}>
            <div className="p-6 border-b border-gray-200">
              <div className="flex items-center justify-between">
                <h2 className="text-xl font-semibold text-gray-900">Audit Log</h2>
                <button
                  onClick={() => setShowAuditLog(false)}
                  className="text-gray-400 hover:text-gray-600"
                >
                  <svg className="h-6 w-6" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            </div>
            <div className="p-6 overflow-y-auto max-h-[60vh]">
              {loadingLogs ? (
                <div className="text-center py-8 text-gray-500">Lade Audit Logs...</div>
              ) : auditLogs.length === 0 ? (
                <div className="text-center py-8 text-gray-500">Keine Audit Logs vorhanden</div>
              ) : (
                <div className="space-y-4">
                  {auditLogs.map((log) => (
                    <div key={log.id} className="border border-gray-200 rounded-lg p-4 hover:bg-gray-50">
                      <div className="flex items-start justify-between">
                        <div className="flex-1">
                          <div className="flex items-center gap-3">
                            <span className={`px-2 py-1 text-xs font-semibold rounded ${
                              log.action === 'DELETE' ? 'bg-red-100 text-red-800' : 'bg-blue-100 text-blue-800'
                            }`}>
                              {log.action}
                            </span>
                            <span className="text-sm font-medium text-gray-900">{log.entityType}</span>
                            <span className="text-sm text-gray-500 font-mono">{log.entityId}</span>
                          </div>
                          <div className="mt-2 text-sm text-gray-600">
                            von <span className="font-medium">{log.performedBy}</span>
                          </div>
                          {log.details && log.details !== '{}' && (
                            <div className="mt-2 text-xs text-gray-500 bg-gray-50 p-2 rounded">
                              {log.details}
                            </div>
                          )}
                        </div>
                        <div className="text-xs text-gray-500 text-right">
                          {new Date(log.timestamp).toLocaleString('de-DE')}
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
