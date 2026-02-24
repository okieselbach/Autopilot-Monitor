"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";

interface AdminConfiguration {
  partitionKey: string;
  rowKey: string;
  lastUpdated: string;
  updatedBy: string;
  globalRateLimitRequestsPerMinute: number;
  platformStatsBlobSasUrl?: string;
  customSettings?: string;
}

interface TenantConfiguration {
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

interface TenantAdmin {
  tenantId: string;
  upn: string;
  isEnabled: boolean;
  addedDate: string;
  addedBy: string;
}

export default function AdminConfigurationPage() {
  const router = useRouter();
  const { getAccessToken } = useAuth();
  const [galacticAdminMode, setGalacticAdminMode] = useState(false);
  const [triggeringMaintenance, setTriggeringMaintenance] = useState(false);
  const [maintenanceDate, setMaintenanceDate] = useState<string>("");
  const [reseedingRules, setReseedingRules] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Admin Configuration state
  const [adminConfig, setAdminConfig] = useState<AdminConfiguration | null>(null);
  const [loadingConfig, setLoadingConfig] = useState(false);
  const [savingConfig, setSavingConfig] = useState(false);
  const [globalRateLimit, setGlobalRateLimit] = useState(100);
  const [platformStatsBlobSasUrl, setPlatformStatsBlobSasUrl] = useState("");

  // Tenant Management state
  const [tenants, setTenants] = useState<TenantConfiguration[]>([]);
  const [loadingTenants, setLoadingTenants] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");
  const [editingTenant, setEditingTenant] = useState<TenantConfiguration | null>(null);
  const [savingTenant, setSavingTenant] = useState(false);
  const [togglingSecurityBypassTenant, setTogglingSecurityBypassTenant] = useState<string | null>(null);
  const [currentPage, setCurrentPage] = useState(0);
  const tenantsPerPage = 3;

  // Preview Whitelist state
  const [previewApproved, setPreviewApproved] = useState<Set<string>>(new Set());
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [togglingPreviewTenant, setTogglingPreviewTenant] = useState<string | null>(null);

  // Admin Management state (for Edit Modal)
  const [tenantAdmins, setTenantAdmins] = useState<TenantAdmin[]>([]);
  const [loadingAdmins, setLoadingAdmins] = useState(false);
  const [newAdminEmail, setNewAdminEmail] = useState("");
  const [addingAdmin, setAddingAdmin] = useState(false);
  const [removingAdmin, setRemovingAdmin] = useState<string | null>(null);
  const [togglingAdmin, setTogglingAdmin] = useState<string | null>(null);
  const [currentAdminPage, setCurrentAdminPage] = useState(0);
  const [adminSearchQuery, setAdminSearchQuery] = useState("");
  const adminsPerPage = 5;

  // Load galactic admin mode from localStorage
  useEffect(() => {
    const galacticMode = localStorage.getItem("galacticAdminMode") === "true";
    setGalacticAdminMode(galacticMode);

    // Redirect if not in galactic admin mode
    if (!galacticMode) {
      router.push("/dashboard");
    }
  }, [router]);

  // Fetch admin configuration
  useEffect(() => {
    if (!galacticAdminMode) return;

    const fetchAdminConfig = async () => {
      try {
        setLoadingConfig(true);
        setError(null);

        
        const token = await getAccessToken();
        if (!token) {
          throw new Error('Failed to get access token');
        }

        const response = await fetch(`${API_BASE_URL}/api/global/config`, {
          headers: {
            'Authorization': `Bearer ${token}`
          }
        });

        if (!response.ok) {
          throw new Error(`Failed to load admin configuration: ${response.statusText}`);
        }

        const data: AdminConfiguration = await response.json();
        setAdminConfig(data);
        setGlobalRateLimit(data.globalRateLimitRequestsPerMinute);
        setPlatformStatsBlobSasUrl(data.platformStatsBlobSasUrl ?? "");
      } catch (err) {
        console.error("Error fetching admin configuration:", err);
        setError(err instanceof Error ? err.message : "Failed to load admin configuration");
      } finally {
        setLoadingConfig(false);
      }
    };

    fetchAdminConfig();
  }, [galacticAdminMode]);

  // Fetch all tenant configurations + preview whitelist
  useEffect(() => {
    if (!galacticAdminMode) return;

    const fetchTenants = async () => {
      try {
        setLoadingTenants(true);

        const token = await getAccessToken();
        if (!token) {
          throw new Error('Failed to get access token');
        }

        const [tenantsRes, previewRes] = await Promise.all([
          fetch(`${API_BASE_URL}/api/config/all`, {
            headers: { 'Authorization': `Bearer ${token}` }
          }),
          fetch(`${API_BASE_URL}/api/preview-whitelist`, {
            headers: { 'Authorization': `Bearer ${token}` }
          })
        ]);

        if (!tenantsRes.ok) {
          throw new Error(`Failed to load tenants: ${tenantsRes.statusText}`);
        }

        const data: TenantConfiguration[] = await tenantsRes.json();
        setTenants(data);

        if (previewRes.ok) {
          const previewData = await previewRes.json();
          const approvedIds = new Set<string>(
            (previewData.tenants || []).map((t: { partitionKey: string }) => t.partitionKey)
          );
          setPreviewApproved(approvedIds);
        }
      } catch (err) {
        console.error("Error fetching tenants:", err);
        setError(err instanceof Error ? err.message : "Failed to load tenants");
      } finally {
        setLoadingTenants(false);
      }
    };

    fetchTenants();
  }, [galacticAdminMode, getAccessToken]);

  const handleTriggerMaintenance = async () => {
    try {
      setTriggeringMaintenance(true);
      setError(null);
      setSuccessMessage(null);

      
      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const queryParams = maintenanceDate ? `?date=${maintenanceDate}` : '';
      const response = await fetch(`${API_BASE_URL}/api/maintenance/trigger${queryParams}`, {
        method: "POST",
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });

      if (!response.ok) {
        throw new Error(`Failed to trigger maintenance: ${response.statusText}`);
      }

      const result = await response.json();
      const dateInfo = maintenanceDate ? ` for ${maintenanceDate}` : '';
      setSuccessMessage(`Maintenance job completed successfully${dateInfo}!`);

      // Auto-hide success message after 5 seconds
      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      console.error("Error triggering maintenance:", err);
      setError(err instanceof Error ? err.message : "Failed to trigger maintenance job");
    } finally {
      setTriggeringMaintenance(false);
    }
  };

  const handleReseedAnalyzeRules = async () => {
    try {
      setReseedingRules(true);
      setError(null);
      setSuccessMessage(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const response = await fetch(`${API_BASE_URL}/api/analyze-rules/reseed`, {
        method: "POST",
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });

      if (!response.ok) {
        throw new Error(`Failed to reseed analyze rules: ${response.statusText}`);
      }

      const result = await response.json();
      setSuccessMessage(result.message || "Analyze rules reseeded successfully!");
      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      console.error("Error reseeding analyze rules:", err);
      setError(err instanceof Error ? err.message : "Failed to reseed analyze rules");
    } finally {
      setReseedingRules(false);
    }
  };

  const handleSaveAdminConfig = async () => {
    if (!adminConfig) return;

    try {
      setSavingConfig(true);
      setError(null);
      setSuccessMessage(null);

      const updatedConfig: AdminConfiguration = {
        ...adminConfig,
        globalRateLimitRequestsPerMinute: globalRateLimit,
        platformStatsBlobSasUrl: platformStatsBlobSasUrl.trim(),
      };

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const response = await fetch(`${API_BASE_URL}/api/global/config`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) {
        throw new Error(`Failed to save admin configuration: ${response.statusText}`);
      }

      const result = await response.json();
      setAdminConfig(result.config);
      setSuccessMessage("Admin configuration saved successfully!");

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error saving admin configuration:", err);
      setError(err instanceof Error ? err.message : "Failed to save admin configuration");
    } finally {
      setSavingConfig(false);
    }
  };

  const handleResetAdminConfig = () => {
    if (!adminConfig) return;
    setGlobalRateLimit(adminConfig.globalRateLimitRequestsPerMinute);
    setPlatformStatsBlobSasUrl(adminConfig.platformStatsBlobSasUrl ?? "");
    setSuccessMessage(null);
    setError(null);
  };

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
      setTenants(tenants.map(t => t.tenantId === tenant.tenantId ? result.config : t));
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

  // Fetch tenant admins for editing
  const fetchTenantAdmins = async (tenantId: string) => {
    try {
      setLoadingAdmins(true);
      setCurrentAdminPage(0); // Reset to first page when loading new tenant

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const response = await fetch(`${API_BASE_URL}/api/tenants/${tenantId}/admins`, {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });

      if (!response.ok) {
        throw new Error(`Failed to load admins: ${response.statusText}`);
      }

      const data: TenantAdmin[] = await response.json();
      setTenantAdmins(data);
    } catch (err) {
      console.error("Error fetching tenant admins:", err);
      setError(err instanceof Error ? err.message : "Failed to load tenant admins");
    } finally {
      setLoadingAdmins(false);
    }
  };

  const handleAddTenantAdmin = async (tenantId: string) => {
    if (!newAdminEmail.trim()) return;

    try {
      setAddingAdmin(true);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const response = await fetch(`${API_BASE_URL}/api/tenants/${tenantId}/admins`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify({ upn: newAdminEmail.trim() }),
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to add admin: ${response.statusText}`);
      }

      setSuccessMessage(`Admin ${newAdminEmail} added successfully!`);
      setNewAdminEmail("");

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error adding tenant admin:", err);
      setError(err instanceof Error ? err.message : "Failed to add admin");
    } finally {
      setAddingAdmin(false);
    }
  };

  const handleRemoveTenantAdmin = async (tenantId: string, adminUpn: string) => {
    if (!confirm(`Are you sure you want to remove ${adminUpn} as an admin?`)) {
      return;
    }

    try {
      setRemovingAdmin(adminUpn);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const response = await fetch(`${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}`, {
        method: "DELETE",
        headers: {
          'Authorization': `Bearer ${token}`
        },
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to remove admin: ${response.statusText}`);
      }

      setSuccessMessage(`Admin ${adminUpn} removed successfully!`);

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error removing tenant admin:", err);
      setError(err instanceof Error ? err.message : "Failed to remove admin");
    } finally {
      setRemovingAdmin(null);
    }
  };

  const handleToggleTenantAdmin = async (tenantId: string, adminUpn: string, isEnabled: boolean) => {
    try {
      setTogglingAdmin(adminUpn);
      setError(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const action = isEnabled ? 'disable' : 'enable';
      const response = await fetch(`${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}/${action}`, {
        method: "PATCH",
        headers: {
          'Authorization': `Bearer ${token}`
        },
      });

      if (!response.ok) {
        let errorData;
        try {
          errorData = await response.json();
        } catch {
          errorData = { error: `Failed to ${action} admin: ${response.statusText}` };
        }
        throw new Error(errorData.error || `Failed to ${action} admin: ${response.statusText}`);
      }

      setSuccessMessage(`Admin ${adminUpn} ${isEnabled ? 'disabled' : 'enabled'} successfully!`);

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error toggling tenant admin:", err);
      setError(err instanceof Error ? err.message : "Failed to toggle admin");
    } finally {
      setTogglingAdmin(null);
    }
  };

  // Filter and sort tenants
  const filteredTenants = tenants.filter(t =>
    t.tenantId.toLowerCase().includes(searchQuery.toLowerCase()) ||
    t.domainName.toLowerCase().includes(searchQuery.toLowerCase())
  );

  // Pagination
  const totalPages = Math.ceil(filteredTenants.length / tenantsPerPage);
  const startIndex = currentPage * tenantsPerPage;
  const endIndex = startIndex + tenantsPerPage;
  const paginatedTenants = filteredTenants.slice(startIndex, endIndex);

  // Reset to first page when search changes
  useEffect(() => {
    setCurrentPage(0);
  }, [searchQuery]);

  // Admin Pagination with Search
  const filteredAdmins = tenantAdmins.filter(admin =>
    admin.upn.toLowerCase().includes(adminSearchQuery.toLowerCase())
  );
  const totalAdminPages = Math.ceil(filteredAdmins.length / adminsPerPage);
  const startAdminIndex = currentAdminPage * adminsPerPage;
  const endAdminIndex = startAdminIndex + adminsPerPage;
  const paginatedAdmins = filteredAdmins.slice(startAdminIndex, endAdminIndex);

  if (!galacticAdminMode) {
    return null;
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {/* Header */}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <button
                  onClick={() => router.push("/dashboard")}
                  className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
                >
                  &larr; Back to Dashboard
                </button>
                <div>
                  <h1 className="text-3xl font-bold text-gray-900">Admin Configuration</h1>
                  <p className="text-sm text-gray-600 mt-1">Galactic Admin Operations</p>
                </div>
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

            {/* Tenant Management */}
            <div className="bg-gradient-to-br from-green-50 to-emerald-50 border-2 border-green-300 rounded-lg shadow-lg">
              <div className="p-6 border-b border-green-200 bg-gradient-to-r from-green-100 to-emerald-100">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 21V5a2 2 0 00-2-2H7a2 2 0 00-2 2v16m14 0h2m-2 0h-5m-9 0H3m2 0h5M9 7h1m-1 4h1m4-4h1m-1 4h1m-5 10v-5a1 1 0 011-1h2a1 1 0 011 1v5m-4 0h4" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-green-900">Tenant Management</h2>
                    <p className="text-sm text-green-600 mt-1">View and manage all tenant configurations</p>
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
                      <span className="text-sm text-gray-600 whitespace-nowrap">{filteredTenants.length} tenant(s)</span>
                    </div>

                    {/* Tenant List */}
                    <div className="space-y-3">
                      {paginatedTenants.length === 0 ? (
                        <div className="text-center py-8 text-gray-500">
                          {searchQuery ? "No tenants found matching your search" : "No tenants registered yet"}
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
                              <div className="flex items-center justify-between">
                                <div className="flex-1">
                                  <div className="flex items-center space-x-2">
                                    <h3 className="font-semibold text-gray-900 text-lg">
                                      {tenant.domainName || tenant.tenantId}
                                    </h3>
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
                                  </div>
                                  <p className="text-sm text-gray-500 mt-1">
                                    Tenant ID: {tenant.tenantId}
                                  </p>
                                </div>
                                <div className="flex items-center space-x-2 ml-4">
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
                                      fetchTenantAdmins(tenant.tenantId);
                                    }}
                                    className="px-4 py-2 text-sm bg-green-600 text-white rounded-lg hover:bg-green-700 transition-colors"
                                  >
                                    Edit
                                  </button>
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
                  </div>
                )}
              </div>
            </div>

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
                        <span>Aggregate metrics into historical snapshots (with automatic catch-up for missed days)</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-purple-500 mr-2">•</span>
                        <span>Clean up old data based on retention policies</span>
                      </li>
                    </ul>
                    <div className="bg-purple-50 border border-purple-200 rounded-lg p-4 mb-4">
                      <label className="block text-sm font-medium text-purple-800 mb-1">
                        Target Date (optional)
                      </label>
                      <input
                        type="date"
                        value={maintenanceDate}
                        onChange={(e) => setMaintenanceDate(e.target.value)}
                        max={new Date(Date.now() - 86400000).toISOString().split('T')[0]}
                        className="w-full max-w-xs px-3 py-2 border border-purple-300 rounded-lg text-sm focus:ring-2 focus:ring-purple-500 focus:border-purple-500"
                      />
                      <p className="text-xs text-gray-500 mt-2">
                        Leave empty to run the standard maintenance with automatic catch-up (aggregates any missed days within the last 7 days).
                        Select a specific date to manually aggregate metrics for that day, e.g. to backfill data older than 7 days.
                      </p>
                    </div>
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

            {/* Reseed Analyze Rules */}
            <div className="bg-gradient-to-br from-amber-50 to-orange-50 border-2 border-amber-300 rounded-lg shadow-lg">
              <div className="p-6 border-b border-amber-200 bg-gradient-to-r from-amber-100 to-orange-100">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-amber-900">Reseed Analyze Rules</h2>
                    <p className="text-sm text-amber-600 mt-1">Re-import all built-in analyze rules from code into Azure Table Storage</p>
                  </div>
                </div>
              </div>
              <div className="p-6">
                <div className="flex items-start space-x-4">
                  <div className="flex-1">
                    <p className="text-sm text-gray-600 mb-4">
                      This operation performs a full re-import of all built-in analyze rules:
                    </p>
                    <ul className="text-sm text-gray-600 space-y-1 mb-4 ml-4">
                      <li className="flex items-start">
                        <span className="text-amber-500 mr-2">•</span>
                        <span>Deletes all existing global built-in rules from the table</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-amber-500 mr-2">•</span>
                        <span>Writes all current code-defined rules as fresh entries</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-amber-500 mr-2">•</span>
                        <span>Tenant-specific custom rules and overrides are not affected</span>
                      </li>
                    </ul>
                    <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3 mb-4">
                      <div className="flex items-start space-x-2">
                        <svg className="w-5 h-5 text-yellow-600 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                        </svg>
                        <p className="text-sm text-yellow-800">
                          <strong>Use after deployments</strong> that add, remove, or modify built-in analyze rules to ensure Azure Table Storage reflects the latest code definitions.
                        </p>
                      </div>
                    </div>
                  </div>
                  <div className="flex-shrink-0">
                    <button
                      onClick={handleReseedAnalyzeRules}
                      disabled={reseedingRules}
                      className="px-6 py-3 bg-gradient-to-r from-amber-600 to-orange-600 text-white rounded-lg hover:from-amber-700 hover:to-orange-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
                    >
                      {reseedingRules ? (
                        <>
                          <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white"></div>
                          <span>Reseeding...</span>
                        </>
                      ) : (
                        <>
                          <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                          </svg>
                          <span>Reseed Now</span>
                        </>
                      )}
                    </button>
                  </div>
                </div>
              </div>
            </div>

            {/* Global Rate Limiting Configuration */}
            <div className="bg-gradient-to-br from-indigo-50 to-blue-50 border-2 border-indigo-300 rounded-lg shadow-lg">
              <div className="p-6 border-b border-indigo-200 bg-gradient-to-r from-indigo-100 to-blue-100">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-indigo-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-indigo-900">Global Settings</h2>
                    <p className="text-sm text-indigo-600 mt-1">Configure global settings for all tenants</p>
                  </div>
                </div>
              </div>
              <div className="p-6">
                {loadingConfig ? (
                  <div className="text-center py-8">
                    <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600 mx-auto"></div>
                    <p className="mt-3 text-gray-600 text-sm">Loading configuration...</p>
                  </div>
                ) : (
                  <div className="space-y-4">
                    <div>
                      <label className="block">
                        <span className="text-gray-700 font-medium">Global Rate Limit (Requests per Minute per Device)</span>
                        <p className="text-sm text-gray-600 mb-2">
                          Configure default DoS protection limits for all tenants. Normal enrollment generates ~10-30 requests/min.
                          <br />
                          <strong className="text-indigo-700">Note:</strong> Tenants cannot change this value. Only Galactic Admins can override per tenant in tenant management section.
                        </p>
                        <input
                          type="number"
                          min="1"
                          max="1000"
                          value={globalRateLimit}
                          onChange={(e) => setGlobalRateLimit(parseInt(e.target.value) || 100)}
                          className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                      </label>
                    </div>

                    <div>
                      <label className="block">
                        <span className="text-gray-700 font-medium">Platform Stats Blob Container SAS URL</span>
                        <p className="text-sm text-gray-600 mb-2">
                          Maintenance publishes two files into this container:
                          <code className="ml-1 mr-1 text-xs">platform-stats.json</code>
                          and
                          <code className="ml-1 text-xs">platform-stats.YYYY-MM-DD.json</code>.
                          The upload is best-effort and does not fail the maintenance run.
                        </p>
                        <input
                          type="url"
                          value={platformStatsBlobSasUrl}
                          onChange={(e) => setPlatformStatsBlobSasUrl(e.target.value)}
                          placeholder="https://storageaccount.blob.core.windows.net/publicstats?sv=...&sig=..."
                          className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors font-mono text-sm"
                        />
                      </label>
                    </div>

                    {adminConfig && (
                      <div className="bg-blue-50 border border-blue-200 rounded-lg p-3">
                        <div className="flex items-start space-x-2">
                          <svg className="w-5 h-5 text-blue-600 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                          </svg>
                          <div className="text-sm text-blue-800">
                            <p className="font-medium">Configuration Info</p>
                            <p className="mt-1">Last updated: {new Date(adminConfig.lastUpdated).toLocaleString()}</p>
                            <p>Updated by: {adminConfig.updatedBy}</p>
                          </div>
                        </div>
                      </div>
                    )}

                    <div className="flex items-center justify-end space-x-3 pt-2">
                      <button
                        onClick={handleResetAdminConfig}
                        disabled={savingConfig}
                        className="px-5 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                      >
                        Reset
                      </button>
                      <button
                        onClick={handleSaveAdminConfig}
                        disabled={savingConfig}
                        className="px-5 py-2 bg-gradient-to-r from-indigo-600 to-blue-600 text-white rounded-md hover:from-indigo-700 hover:to-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
                      >
                        {savingConfig ? (
                          <>
                            <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                            <span>Saving...</span>
                          </>
                        ) : (
                          <span>Save Configuration</span>
                        )}
                      </button>
                    </div>
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
                    <div className="bg-purple-50 border border-purple-200 rounded-lg p-4">
                      <div className="flex items-start space-x-3">
                        <svg className="w-5 h-5 text-purple-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z" />
                        </svg>
                        <div className="flex-1">
                          <p className="font-semibold text-purple-900 mb-2">Admin Users Management</p>

                          {/* Current Admins List */}
                          <div className="mb-3">
                            <p className="text-sm text-purple-800 font-medium mb-2">
                              Current Admins ({tenantAdmins.length}):
                            </p>

                            {/* Search Field */}
                            <div className="mb-3">
                              <div className="relative">
                                <input
                                  type="text"
                                  name="admin-search-modal"
                                  value={adminSearchQuery}
                                  onChange={(e) => {
                                    setAdminSearchQuery(e.target.value);
                                    setCurrentAdminPage(0);
                                  }}
                                  placeholder="Search by email..."
                                  autoComplete="off"
                                  className="w-full px-3 py-2 pl-9 pr-9 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
                                />
                                <svg className="absolute left-2.5 top-2.5 w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                                </svg>
                                {adminSearchQuery && (
                                  <button
                                    onClick={() => {
                                      setAdminSearchQuery("");
                                      setCurrentAdminPage(0);
                                    }}
                                    className="absolute right-2.5 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
                                    title="Clear search"
                                  >
                                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                                    </svg>
                                  </button>
                                )}
                              </div>
                            </div>

                            {loadingAdmins ? (
                              <div className="text-sm text-purple-700">Loading admins...</div>
                            ) : tenantAdmins.length === 0 ? (
                              <div className="text-sm text-purple-700 italic">No admins configured yet</div>
                            ) : filteredAdmins.length === 0 ? (
                              <div className="text-sm text-purple-700 italic p-3 text-center bg-purple-50 rounded-lg">
                                No admins match your search
                              </div>
                            ) : (
                              <>
                                <div className="space-y-2">
                                  {paginatedAdmins.map((admin) => (
                                    <div
                                      key={admin.upn}
                                      className={`flex items-center justify-between p-2 bg-white border rounded text-sm ${
                                        admin.isEnabled ? 'border-purple-200' : 'border-gray-300 bg-gray-50'
                                      }`}
                                    >
                                      <div className="flex-1">
                                        <div className="flex items-center space-x-2">
                                          <div className="font-medium text-gray-900">{admin.upn}</div>
                                          {!admin.isEnabled && (
                                            <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-200 text-gray-700">
                                              Disabled
                                            </span>
                                          )}
                                        </div>
                                        <div className="text-xs text-gray-500">
                                          Added {new Date(admin.addedDate).toLocaleDateString()} by {admin.addedBy}
                                        </div>
                                      </div>
                                      <div className="flex items-center space-x-2">
                                        <button
                                          onClick={() => handleToggleTenantAdmin(editingTenant.tenantId, admin.upn, admin.isEnabled)}
                                          disabled={togglingAdmin === admin.upn}
                                          className={`px-2 py-1 text-xs rounded hover:opacity-80 disabled:opacity-50 disabled:cursor-not-allowed ${
                                            admin.isEnabled
                                              ? 'bg-yellow-600 text-white hover:bg-yellow-700'
                                              : 'bg-green-600 text-white hover:bg-green-700'
                                          }`}
                                        >
                                          {togglingAdmin === admin.upn
                                            ? "..."
                                            : admin.isEnabled ? "Disable" : "Enable"}
                                        </button>
                                        <button
                                          onClick={() => handleRemoveTenantAdmin(editingTenant.tenantId, admin.upn)}
                                          disabled={removingAdmin === admin.upn}
                                          className="px-2 py-1 text-xs bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed"
                                        >
                                          {removingAdmin === admin.upn ? "..." : "Remove"}
                                        </button>
                                      </div>
                                    </div>
                                  ))}
                                </div>

                                {/* Pagination */}
                                {totalAdminPages > 1 && (
                                  <div className="flex items-center justify-between pt-3 mt-3 border-t border-purple-200">
                                    <button
                                      onClick={() => setCurrentAdminPage(p => Math.max(0, p - 1))}
                                      disabled={currentAdminPage === 0}
                                      className="px-3 py-1 text-xs font-medium text-purple-700 bg-white border border-purple-300 rounded hover:bg-purple-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                    >
                                      Previous
                                    </button>
                                    <span className="text-xs text-purple-700">
                                      Page {currentAdminPage + 1} of {totalAdminPages} ({filteredAdmins.length} admin{filteredAdmins.length !== 1 ? 's' : ''})
                                    </span>
                                    <button
                                      onClick={() => setCurrentAdminPage(p => Math.min(totalAdminPages - 1, p + 1))}
                                      disabled={currentAdminPage >= totalAdminPages - 1}
                                      className="px-3 py-1 text-xs font-medium text-purple-700 bg-white border border-purple-300 rounded hover:bg-purple-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                    >
                                      Next
                                    </button>
                                  </div>
                                )}
                              </>
                            )}
                          </div>

                          {/* Add New Admin */}
                          <div>
                            <p className="text-sm text-purple-800 font-medium mb-2">Add New Admin:</p>
                            <div className="flex space-x-2">
                              <input
                                type="email"
                                name="modal-new-admin-email"
                                id="modal-add-admin-email-input"
                                value={newAdminEmail}
                                onChange={(e) => setNewAdminEmail(e.target.value)}
                                placeholder="admin@tenant.com"
                                autoComplete="off"
                                className="flex-1 px-3 py-2 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                                onKeyDown={(e) => {
                                  if (e.key === 'Enter') {
                                    e.preventDefault();
                                    handleAddTenantAdmin(editingTenant.tenantId);
                                  }
                                }}
                              />
                              <button
                                onClick={() => handleAddTenantAdmin(editingTenant.tenantId)}
                                disabled={addingAdmin || !newAdminEmail.trim()}
                                className="px-4 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm"
                              >
                                {addingAdmin ? "Adding..." : "Add"}
                              </button>
                            </div>
                          </div>
                        </div>
                      </div>
                    </div>

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
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
