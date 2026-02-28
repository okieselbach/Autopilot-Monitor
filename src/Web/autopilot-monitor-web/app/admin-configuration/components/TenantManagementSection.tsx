"use client";

import { useCallback, useEffect, useState } from "react";
import { API_BASE_URL } from "@/lib/config";
import { TenantAdminSection } from "./TenantAdminSection";

export interface TenantConfiguration {
  tenantId: string;
  domainName: string;
  lastUpdated: string;
  updatedBy: string;
  disabled: boolean;
  disabledReason?: string;
  disabledUntil?: string;
  rateLimitRequestsPerMinute: number;
  manufacturerWhitelist: string;
  modelWhitelist: string;
  validateAutopilotDevice: boolean;
  allowInsecureAgentRequests?: boolean;
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  maxNdjsonPayloadSizeMB: number;
}

export interface TenantManagementSectionProps {
  tenants: TenantConfiguration[];
  loadingTenants: boolean;
  fetchTenants: () => void;
  previewApproved: Set<string>;
  setPreviewApproved: React.Dispatch<React.SetStateAction<Set<string>>>;
  setTenants: React.Dispatch<React.SetStateAction<TenantConfiguration[]>>;
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function TenantManagementSection({
  tenants,
  loadingTenants,
  fetchTenants,
  previewApproved,
  setPreviewApproved,
  setTenants,
  getAccessToken,
  setError,
  setSuccessMessage,
}: TenantManagementSectionProps) {
  const [searchQuery, setSearchQuery] = useState("");
  const [showOnlyWaitlist, setShowOnlyWaitlist] = useState(false);
  const [tenantSectionExpanded, setTenantSectionExpanded] = useState(false);
  const [editingTenant, setEditingTenant] = useState<TenantConfiguration | null>(null);
  const [savingTenant, setSavingTenant] = useState(false);
  const [togglingSecurityBypassTenant, setTogglingSecurityBypassTenant] = useState<string | null>(null);
  const [currentPage, setCurrentPage] = useState(0);
  const tenantsPerPage = tenantSectionExpanded ? 7 : 3;

  // Preview Whitelist state
  const [togglingPreviewTenant, setTogglingPreviewTenant] = useState<string | null>(null);

  // Filter and sort tenants
  const filteredTenants = tenants.filter(t => {
    const matchesSearch =
      t.tenantId.toLowerCase().includes(searchQuery.toLowerCase()) ||
      t.domainName.toLowerCase().includes(searchQuery.toLowerCase());
    const matchesWaitlist = !showOnlyWaitlist || !previewApproved.has(t.tenantId);
    return matchesSearch && matchesWaitlist;
  });

  // Statistics (always over all tenants, not filtered)
  const readyCount = tenants.filter(t => t.validateAutopilotDevice).length;
  const waitlistCount = tenants.filter(t => !previewApproved.has(t.tenantId)).length;
  const totalCount = tenants.length;

  // Pagination
  const totalPages = Math.ceil(filteredTenants.length / tenantsPerPage);
  const startIndex = currentPage * tenantsPerPage;
  const endIndex = startIndex + tenantsPerPage;
  const paginatedTenants = filteredTenants.slice(startIndex, endIndex);

  // Reset to first page when search changes
  useEffect(() => {
    setCurrentPage(0);
  }, [searchQuery, showOnlyWaitlist]);

  const handleSaveTenant = async (tenant: TenantConfiguration) => {
    try {
      setSavingTenant(true);
      setError(null);
      setSuccessMessage(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const response = await fetch(`${API_BASE_URL}/api/config/${tenant.tenantId}`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify(tenant),
      });

      if (!response.ok) {
        throw new Error(`Failed to save tenant configuration: ${response.statusText}`);
      }

      const result = await response.json();

      // Update tenant in list
      setTenants(prev => prev.map(t => t.tenantId === tenant.tenantId ? result.config : t));
      setEditingTenant(null);
      setSuccessMessage(`Tenant ${tenant.tenantId} configuration saved successfully!`);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error saving tenant configuration:", err);
      setError(err instanceof Error ? err.message : "Failed to save tenant configuration");
    } finally {
      setSavingTenant(false);
    }
  };

  const handleToggleSecurityBypass = async (tenant: TenantConfiguration) => {
    try {
      setTogglingSecurityBypassTenant(tenant.tenantId);
      setError(null);
      setSuccessMessage(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const newValue = !tenant.allowInsecureAgentRequests;
      const response = await fetch(`${API_BASE_URL}/api/config/${tenant.tenantId}/security-bypass`, {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify({ allowInsecureAgentRequests: newValue }),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to update security bypass: ${response.statusText}`);
      }

      setTenants(prev => prev.map(t =>
        t.tenantId === tenant.tenantId
          ? { ...t, allowInsecureAgentRequests: newValue }
          : t
      ));

      setEditingTenant(prev => prev && prev.tenantId === tenant.tenantId
        ? { ...prev, allowInsecureAgentRequests: newValue }
        : prev);

      setSuccessMessage(
        newValue
          ? `Security bypass enabled for tenant ${tenant.tenantId}.`
          : `Security bypass disabled for tenant ${tenant.tenantId}.`
      );
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update security bypass");
    } finally {
      setTogglingSecurityBypassTenant(null);
    }
  };

  const handleTogglePreview = async (tenantId: string) => {
    try {
      setTogglingPreviewTenant(tenantId);
      setError(null);
      setSuccessMessage(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const isCurrentlyApproved = previewApproved.has(tenantId);

      const response = await fetch(`${API_BASE_URL}/api/preview-whitelist/${tenantId}`, {
        method: isCurrentlyApproved ? "DELETE" : "POST",
        headers: { 'Authorization': `Bearer ${token}` }
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to update preview access: ${response.statusText}`);
      }

      setPreviewApproved(prev => {
        const next = new Set(prev);
        if (isCurrentlyApproved) {
          next.delete(tenantId);
        } else {
          next.add(tenantId);
        }
        return next;
      });

      setSuccessMessage(
        isCurrentlyApproved
          ? `Preview access revoked for tenant ${tenantId}`
          : `Preview access granted for tenant ${tenantId}`
      );
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to update preview access");
    } finally {
      setTogglingPreviewTenant(null);
    }
  };

  return (
    <>
      <div className="bg-gradient-to-br from-green-50 to-emerald-50 border-2 border-green-300 rounded-lg shadow-lg">
        <div
          className="p-6 border-b border-green-200 bg-gradient-to-r from-green-100 to-emerald-100 cursor-pointer select-none"
          onClick={() => { setTenantSectionExpanded(v => !v); setCurrentPage(0); }}
        >
          <div className="flex items-center justify-between">
            <div className="flex items-center space-x-2">
              <svg className="w-6 h-6 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 21V5a2 2 0 00-2-2H7a2 2 0 00-2 2v16m14 0h2m-2 0h-5m-9 0H3m2 0h5M9 7h1m-1 4h1m4-4h1m-1 4h1m-5 10v-5a1 1 0 011-1h2a1 1 0 011 1v5m-4 0h4" />
              </svg>
              <div>
                <h2 className="text-xl font-semibold text-green-900">Tenant Management</h2>
                <p className="text-sm text-green-600 mt-1">View and manage all tenant configurations</p>
              </div>
            </div>
            <div className="flex items-center space-x-2">
              <button
                onClick={(e) => { e.stopPropagation(); fetchTenants(); }}
                disabled={loadingTenants}
                className="p-1.5 rounded-lg text-green-700 hover:bg-green-200 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                title="Refresh tenants"
              >
                <svg className={`w-4 h-4 ${loadingTenants ? 'animate-spin' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                </svg>
              </button>
              <svg
                className={`w-5 h-5 text-green-700 transition-transform duration-200 ${tenantSectionExpanded ? 'rotate-180' : ''}`}
                fill="none" stroke="currentColor" viewBox="0 0 24 24"
              >
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
              </svg>
            </div>
          </div>
        </div>
        <div className="p-6">
          {loadingTenants ? (
            <div className="text-center py-8">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-green-600 mx-auto"></div>
              <p className="mt-3 text-gray-600 text-sm">Loading tenants...</p>
            </div>
          ) : (
            <div className="space-y-4">
              {/* Search */}
              <div className="flex items-center justify-between space-x-2 mb-4">
                <div className="relative flex-1">
                  <svg className="absolute left-3 top-1/2 transform -translate-y-1/2 w-5 h-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                  </svg>
                  <input
                    type="text"
                    placeholder="Search by domain or tenant ID..."
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    className="w-full pl-10 pr-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                  />
                </div>
                <button
                  onClick={() => { setShowOnlyWaitlist(v => !v); setCurrentPage(0); }}
                  className={`flex items-center space-x-1 px-3 py-2 text-sm rounded-lg border transition-colors whitespace-nowrap ${
                    showOnlyWaitlist
                      ? 'bg-amber-500 text-white border-amber-500'
                      : 'bg-white text-gray-700 border-gray-300 hover:bg-gray-50'
                  }`}
                >
                  {showOnlyWaitlist && (
                    <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2.5} d="M5 13l4 4L19 7" />
                    </svg>
                  )}
                  <span>Waitlist</span>
                </button>
                <span className="text-sm text-gray-600 whitespace-nowrap">{filteredTenants.length} tenant(s)</span>
              </div>

              {/* Tenant List */}
              <div className="space-y-3">
                {paginatedTenants.length === 0 ? (
                  <div className="text-center py-8 text-gray-500">
                    {showOnlyWaitlist ? "No waitlist tenants found" : searchQuery ? "No tenants found matching your search" : "No tenants registered yet"}
                  </div>
                ) : (
                  <>
                    {paginatedTenants.map((tenant) => (
                      <div
                        key={tenant.tenantId}
                        className={`border rounded-lg p-4 transition-all ${
                          tenant.disabled
                            ? 'bg-red-50 border-red-300'
                            : 'bg-white border-gray-200 hover:border-green-300'
                        }`}
                      >
                        <div className="flex flex-col md:flex-row md:items-center md:justify-between gap-2">
                          <div>
                            <h3 className="font-semibold text-gray-900 text-lg">
                              {tenant.domainName || tenant.tenantId}
                            </h3>
                            <p className="text-sm text-gray-500 mt-0.5">
                              Tenant ID: {tenant.tenantId}
                            </p>
                          </div>
                          <div className="flex flex-wrap items-center gap-2">
                            <div className="flex flex-wrap items-center gap-2">
                              {tenant.disabled && (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-red-100 text-red-800">
                                  Suspended
                                </span>
                              )}
                              {previewApproved.has(tenant.tenantId) ? (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-green-100 text-green-800">
                                  Preview
                                </span>
                              ) : (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-amber-100 text-amber-800">
                                  Waitlist
                                </span>
                              )}
                              {tenant.validateAutopilotDevice && (
                                <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                                  Ready
                                </span>
                              )}
                            </div>
                            <div className="flex items-center gap-2">
                              <button
                                onClick={() => handleTogglePreview(tenant.tenantId)}
                                disabled={togglingPreviewTenant === tenant.tenantId}
                                className={`px-3 py-2 text-sm rounded-lg transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                  previewApproved.has(tenant.tenantId)
                                    ? 'bg-amber-500 text-white hover:bg-amber-600'
                                    : 'bg-blue-600 text-white hover:bg-blue-700'
                                }`}
                              >
                                {togglingPreviewTenant === tenant.tenantId
                                  ? "..."
                                  : previewApproved.has(tenant.tenantId)
                                  ? "Revoke"
                                  : "Approve"}
                              </button>
                              <button
                                onClick={() => {
                                  setEditingTenant(tenant);
                                }}
                                className="px-4 py-2 text-sm bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors"
                              >
                                Edit
                              </button>
                            </div>
                          </div>
                        </div>
                      </div>
                    ))}

                    {/* Pagination */}
                    {totalPages > 1 && (
                      <div className="flex items-center justify-between pt-4 border-t border-gray-200">
                        <button
                          onClick={() => setCurrentPage(p => Math.max(0, p - 1))}
                          disabled={currentPage === 0}
                          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Previous
                        </button>
                        <span className="text-sm text-gray-600">
                          Page {currentPage + 1} of {totalPages}
                        </span>
                        <button
                          onClick={() => setCurrentPage(p => Math.min(totalPages - 1, p + 1))}
                          disabled={currentPage >= totalPages - 1}
                          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Next
                        </button>
                      </div>
                    )}
                  </>
                )}
              </div>

              {/* Statistics */}
              {totalCount > 0 && (
                <div className="pt-3 border-t border-green-200 flex items-center justify-between gap-4 text-sm text-gray-600 flex-wrap">
                  <span>
                    <span className="font-semibold text-blue-700">{readyCount}</span>
                    {' '}of{' '}
                    <span className="font-semibold">{totalCount}</span>
                    {' '}Tenant(s) are Ready
                  </span>
                  <span>
                    <span className="font-semibold text-amber-600">{waitlistCount}</span>
                    {' '}of{' '}
                    <span className="font-semibold">{totalCount}</span>
                    {' '}Tenant(s) are on the Waitlist
                  </span>
                </div>
              )}
            </div>
          )}
        </div>
      </div>

      {/* Edit Tenant Modal */}
      {editingTenant && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-2xl w-full max-h-[90vh] overflow-y-auto">
            <div className="sticky top-0 bg-green-600 text-white p-6 rounded-t-lg">
              <h2 className="text-2xl font-bold">Edit Tenant Configuration</h2>
              <p className="text-green-100 text-sm mt-1">{editingTenant.tenantId}</p>
            </div>

            <div className="p-6 space-y-6">
              {/* Tenant Suspension */}
              <div className="bg-red-50 border border-red-200 rounded-lg p-4">
                <h3 className="font-semibold text-red-900 mb-3">Tenant Suspension</h3>
                <div className="space-y-3">
                  <label className="flex items-center space-x-2 cursor-pointer">
                    <input
                      type="checkbox"
                      checked={editingTenant.disabled}
                      onChange={(e) => setEditingTenant({ ...editingTenant, disabled: e.target.checked })}
                      className="w-4 h-4 text-red-600 border-gray-300 rounded focus:ring-red-500"
                    />
                    <span className="text-sm font-medium text-gray-700">Suspend Tenant</span>
                  </label>

                  {editingTenant.disabled && (
                    <>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Reason</label>
                        <input
                          type="text"
                          value={editingTenant.disabledReason || ''}
                          onChange={(e) => setEditingTenant({ ...editingTenant, disabledReason: e.target.value })}
                          placeholder="Optional: Why is this tenant suspended?"
                          className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-red-500 focus:border-red-500"
                        />
                      </div>
                      <div>
                        <label className="block text-sm font-medium text-gray-700 mb-1">Disabled Until</label>
                        <input
                          type="datetime-local"
                          value={editingTenant.disabledUntil ? new Date(editingTenant.disabledUntil).toISOString().slice(0, 16) : ''}
                          onChange={(e) => setEditingTenant({ ...editingTenant, disabledUntil: e.target.value ? new Date(e.target.value).toISOString() : undefined })}
                          className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:ring-2 focus:ring-red-500 focus:border-red-500"
                        />
                        <p className="text-xs text-gray-500 mt-1">Optional: Auto-enable after this date/time</p>
                      </div>
                    </>
                  )}
                </div>
              </div>

              {/* Admin Users Info */}
              <TenantAdminSection
                tenantId={editingTenant.tenantId}
                getAccessToken={getAccessToken}
                setError={setError}
                setSuccessMessage={setSuccessMessage}
              />

              {/* Rate Limit */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Rate Limit (Requests/Min)</label>
                <input
                  type="number"
                  min="1"
                  max="1000"
                  value={editingTenant.rateLimitRequestsPerMinute}
                  onChange={(e) => setEditingTenant({ ...editingTenant, rateLimitRequestsPerMinute: parseInt(e.target.value) || 100 })}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                />
              </div>

              {/* Hardware Whitelist */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Manufacturer Whitelist</label>
                <input
                  type="text"
                  value={editingTenant.manufacturerWhitelist}
                  onChange={(e) => setEditingTenant({ ...editingTenant, manufacturerWhitelist: e.target.value })}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Model Whitelist</label>
                <input
                  type="text"
                  value={editingTenant.modelWhitelist}
                  onChange={(e) => setEditingTenant({ ...editingTenant, modelWhitelist: e.target.value })}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                />
              </div>

              <label className="flex items-center space-x-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={editingTenant.validateAutopilotDevice}
                  onChange={(e) => setEditingTenant({ ...editingTenant, validateAutopilotDevice: e.target.checked })}
                  className="w-4 h-4 text-blue-600 border-gray-300 rounded focus:ring-blue-500"
                />
                <span className="text-sm font-medium text-gray-700">Autopilot Device Validation</span>
              </label>

              <div className="border border-amber-300 bg-amber-50 rounded-lg p-3">
                <p className="text-sm font-semibold text-amber-900">Galactic Admin Test Bypass</p>
                <p className="text-xs text-amber-800 mt-1">
                  Allows agent requests even when Autopilot device validation is disabled. Use only for temporary test tenants.
                </p>
                <div className="mt-3 flex items-center justify-between">
                  <span className={`text-xs font-medium ${editingTenant.allowInsecureAgentRequests ? "text-red-700" : "text-green-700"}`}>
                    {editingTenant.allowInsecureAgentRequests ? "Bypass is ENABLED" : "Bypass is DISABLED"}
                  </span>
                  <button
                    onClick={() => handleToggleSecurityBypass(editingTenant)}
                    disabled={togglingSecurityBypassTenant === editingTenant.tenantId}
                    className={`px-3 py-1.5 text-xs font-medium rounded-md text-white transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                      editingTenant.allowInsecureAgentRequests
                        ? "bg-red-600 hover:bg-red-700"
                        : "bg-amber-600 hover:bg-amber-700"
                    }`}
                  >
                    {togglingSecurityBypassTenant === editingTenant.tenantId
                      ? "Updating..."
                      : editingTenant.allowInsecureAgentRequests
                      ? "Disable Bypass"
                      : "Enable Bypass"}
                  </button>
                </div>
              </div>

              {/* Data Management */}
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Data Retention (Days)</label>
                <input
                  type="number"
                  min="7"
                  max="3650"
                  value={editingTenant.dataRetentionDays}
                  onChange={(e) => setEditingTenant({ ...editingTenant, dataRetentionDays: parseInt(e.target.value) || 90 })}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Session Timeout (Hours)</label>
                <input
                  type="number"
                  min="1"
                  max="48"
                  value={editingTenant.sessionTimeoutHours}
                  onChange={(e) => setEditingTenant({ ...editingTenant, sessionTimeoutHours: parseInt(e.target.value) || 5 })}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                />
              </div>

              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Max NDJSON Payload (MB)</label>
                <input
                  type="number"
                  min="1"
                  max="50"
                  value={editingTenant.maxNdjsonPayloadSizeMB}
                  onChange={(e) => setEditingTenant({ ...editingTenant, maxNdjsonPayloadSizeMB: parseInt(e.target.value) || 5 })}
                  className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                />
              </div>
            </div>

            {/* Modal Actions */}
            <div className="sticky bottom-0 bg-gray-50 px-6 py-4 border-t border-gray-200 rounded-b-lg flex justify-end space-x-3">
              <button
                onClick={() => setEditingTenant(null)}
                disabled={savingTenant}
                className="px-4 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                onClick={() => handleSaveTenant(editingTenant)}
                disabled={savingTenant}
                className="px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 disabled:opacity-50 flex items-center space-x-2"
              >
                {savingTenant ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Saving...</span>
                  </>
                ) : (
                  <span>Save Changes</span>
                )}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
