"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";

interface TenantConfiguration {
  tenantId: string;
  lastUpdated: string;
  updatedBy: string;
  manufacturerWhitelist: string;
  modelWhitelist: string;
  validateSerialNumber: boolean;
  allowInsecureAgentRequests?: boolean;
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  customSettings?: string;
  // Agent collector settings
  enablePerformanceCollector: boolean;
  performanceCollectorIntervalSeconds: number;
}

interface TenantAdmin {
  tenantId: string;
  upn: string;
  isEnabled: boolean;
  addedDate: string;
  addedBy: string;
}

export default function SettingsPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken, user, logout } = useAuth();
  const [config, setConfig] = useState<TenantConfiguration | null>(null);
  const [admins, setAdmins] = useState<TenantAdmin[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadingAdmins, setLoadingAdmins] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [newAdminEmail, setNewAdminEmail] = useState("");
  const [addingAdmin, setAddingAdmin] = useState(false);
  const [removingAdmin, setRemovingAdmin] = useState<string | null>(null);
  const [togglingAdmin, setTogglingAdmin] = useState<string | null>(null);
  const [adminSearchQuery, setAdminSearchQuery] = useState("");
  const [currentAdminPage, setCurrentAdminPage] = useState(0);

  // Offboard state
  const [showOffboardDialog, setShowOffboardDialog] = useState(false);
  const [offboardConfirmText, setOffboardConfirmText] = useState("");
  const [offboarding, setOffboarding] = useState(false);
  const [offboardError, setOffboardError] = useState<string | null>(null);

  // Form state
  const [manufacturerWhitelist, setManufacturerWhitelist] = useState("Dell*,HP*,Lenovo*,Microsoft Corporation");
  const [modelWhitelist, setModelWhitelist] = useState("*");
  const [validateSerialNumber, setValidateSerialNumber] = useState(false);
  const [dataRetentionDays, setDataRetentionDays] = useState(90);
  const [sessionTimeoutHours, setSessionTimeoutHours] = useState(5);

  // Collector settings state
  const [enablePerformanceCollector, setEnablePerformanceCollector] = useState(false);
  const [performanceCollectorInterval, setPerformanceCollectorInterval] = useState(60);
  const [serialConsentInProgress, setSerialConsentInProgress] = useState(false);


  // Fetch configuration
  useEffect(() => {
    if (!tenantId) return;

    const fetchConfiguration = async () => {
      try {
        setLoading(true);
        setError(null);

        
        const token = await getAccessToken();
        if (!token) {
          throw new Error('Failed to get access token');
        }

        const response = await fetch(`${API_BASE_URL}/api/config/${tenantId}`, {
          headers: {
            'Authorization': `Bearer ${token}`
          }
        });

        if (!response.ok) {
          throw new Error(`Failed to load configuration: ${response.statusText}`);
        }

        const data: TenantConfiguration = await response.json();
        setConfig(data);

        // Update form state
        setManufacturerWhitelist(data.manufacturerWhitelist);
        setModelWhitelist(data.modelWhitelist);
        setValidateSerialNumber(data.validateSerialNumber);
        setDataRetentionDays(data.dataRetentionDays ?? 90);
        setSessionTimeoutHours(data.sessionTimeoutHours ?? 5);
        setEnablePerformanceCollector(data.enablePerformanceCollector ?? false);
        setPerformanceCollectorInterval(data.performanceCollectorIntervalSeconds ?? 60);
      } catch (err) {
        console.error("Error fetching configuration:", err);
        setError(err instanceof Error ? err.message : "Failed to load configuration");
      } finally {
        setLoading(false);
      }
    };

    fetchConfiguration();
  }, [tenantId]);

  // Fetch admins
  const fetchAdmins = async () => {
    if (!tenantId) return;

    try {
      setLoadingAdmins(true);

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
      setAdmins(data);
    } catch (err) {
      console.error("Error fetching admins:", err);
      setError(err instanceof Error ? err.message : "Failed to load admins");
    } finally {
      setLoadingAdmins(false);
    }
  };

  useEffect(() => {
    if (!tenantId) return;
    fetchAdmins();
  }, [tenantId]);

  const saveConfiguration = async (validateSerialNumberOverride?: boolean) => {
    if (!tenantId || !config) return;

    try {
      setSaving(true);
      setError(null);
      setSuccessMessage(null);

      const serialValidationValue = validateSerialNumberOverride ?? validateSerialNumber;

      const updatedConfig: TenantConfiguration = {
        ...config,
        manufacturerWhitelist,
        modelWhitelist,
        validateSerialNumber: serialValidationValue,
        dataRetentionDays,
        sessionTimeoutHours,
        enablePerformanceCollector,
        performanceCollectorIntervalSeconds: performanceCollectorInterval,
      };

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const response = await fetch(`${API_BASE_URL}/api/config/${tenantId}`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
          'Authorization': `Bearer ${token}`
        },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || errorData.error || `Failed to save configuration: ${response.statusText}`);
      }

      const result = await response.json();
      setConfig(result.config);
      setValidateSerialNumber(result.config.validateSerialNumber);
      setSuccessMessage("Configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 3000);
    } finally {
      setSaving(false);
    }
  };

  const beginSerialValidationEnableFlow = async () => {
    if (!tenantId) return;

    try {
      setSerialConsentInProgress(true);
      setError(null);
      setSuccessMessage(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const redirectUri = `${window.location.origin}/settings`;
      const response = await fetch(
        `${API_BASE_URL}/api/config/${tenantId}/serial-validation/consent-url?redirectUri=${encodeURIComponent(redirectUri)}`,
        {
          headers: {
            'Authorization': `Bearer ${token}`
          }
        }
      );

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to start consent flow: ${response.statusText}`);
      }

      const data = await response.json();
      if (!data.consentUrl) {
        throw new Error("Backend did not return a consent URL.");
      }

      sessionStorage.setItem("serialValidationPending", "true");
      window.location.href = data.consentUrl;
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to start admin consent flow");
      setSerialConsentInProgress(false);
    }
  };

  useEffect(() => {
    const handleConsentCallback = async () => {
      if (!tenantId || !config) return;

      const wasPending = sessionStorage.getItem("serialValidationPending") === "true";
      if (!wasPending) return;

      const queryParams = new URLSearchParams(window.location.search);
      const adminConsent = queryParams.get("admin_consent");
      const consentError = queryParams.get("error");
      const consentErrorDescription = queryParams.get("error_description");

      if (!adminConsent && !consentError) {
        return;
      }

      sessionStorage.removeItem("serialValidationPending");

      if (consentError) {
        const errorText = consentErrorDescription
          ? `${consentError}: ${decodeURIComponent(consentErrorDescription)}`
          : consentError;
        setError(`Admin consent failed: ${errorText}`);
        setSerialConsentInProgress(false);
        router.replace("/settings");
        return;
      }

      try {
        setSerialConsentInProgress(true);
        const token = await getAccessToken();
        if (!token) {
          throw new Error("Failed to get access token");
        }

        const statusResponse = await fetch(
          `${API_BASE_URL}/api/config/${tenantId}/serial-validation/consent-status`,
          {
            headers: {
              'Authorization': `Bearer ${token}`
            }
          }
        );

        if (!statusResponse.ok) {
          const errorData = await statusResponse.json().catch(() => ({}));
          throw new Error(errorData.error || `Consent validation failed: ${statusResponse.statusText}`);
        }

        const statusData = await statusResponse.json();
        if (!statusData.isConsented) {
          throw new Error(statusData.message || "Consent is not active yet for this tenant.");
        }

        await saveConfiguration(true);
        setSuccessMessage("Serial Number Validation enabled. Backend agent endpoints are now unlocked for this tenant.");
        router.replace("/settings");
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to verify consent");
      } finally {
        setSerialConsentInProgress(false);
      }
    };

    handleConsentCallback();
  }, [tenantId, config, router]);

  const handleSave = async () => {
    if (!tenantId || !config) return;

    try {
      await saveConfiguration();
    } catch (err) {
      console.error("Error saving configuration:", err);
      setError(err instanceof Error ? err.message : "Failed to save configuration");
    }
  };

  const handleReset = () => {
    if (!config) return;

    setManufacturerWhitelist(config.manufacturerWhitelist);
    setModelWhitelist(config.modelWhitelist);
    setValidateSerialNumber(config.validateSerialNumber);
    setDataRetentionDays(config.dataRetentionDays ?? 90);
    setSessionTimeoutHours(config.sessionTimeoutHours ?? 5);
    setEnablePerformanceCollector(config.enablePerformanceCollector ?? false);
    setPerformanceCollectorInterval(config.performanceCollectorIntervalSeconds ?? 60);
    setSuccessMessage(null);
    setError(null);
  };

  const handleAddAdmin = async () => {
    if (!tenantId || !newAdminEmail.trim()) return;

    try {
      setAddingAdmin(true);
      setError(null);
      setSuccessMessage(null);

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
      await fetchAdmins();

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error adding admin:", err);
      setError(err instanceof Error ? err.message : "Failed to add admin");
    } finally {
      setAddingAdmin(false);
    }
  };

  const handleRemoveAdmin = async (adminUpn: string) => {
    if (!tenantId) return;

    // Confirm removal
    if (!confirm(`Are you sure you want to remove ${adminUpn} as an admin?`)) {
      return;
    }

    try {
      setRemovingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

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
      await fetchAdmins();

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error removing admin:", err);
      setError(err instanceof Error ? err.message : "Failed to remove admin");
    } finally {
      setRemovingAdmin(null);
    }
  };

  const handleToggleTenantAdmin = async (adminUpn: string, isEnabled: boolean) => {
    if (!tenantId) return;

    const action = isEnabled ? "disable" : "enable";

    try {
      setTogglingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error('Failed to get access token');
      }

      const response = await fetch(
        `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}/${action}`,
        {
          method: "PATCH",
          headers: {
            'Authorization': `Bearer ${token}`
          },
        }
      );

      if (!response.ok) {
        let errorData;
        try {
          errorData = await response.json();
        } catch {
          errorData = { error: `Failed to ${action} admin: ${response.statusText}` };
        }
        throw new Error(errorData.error || `Failed to ${action} admin: ${response.statusText}`);
      }

      setSuccessMessage(`Admin ${adminUpn} ${action}d successfully!`);

      // Refresh admin list
      await fetchAdmins();

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error(`Error ${action}ing admin:`, err);
      setError(err instanceof Error ? err.message : `Failed to ${action} admin`);
    } finally {
      setTogglingAdmin(null);
    }
  };


  const handleOffboard = async () => {
    if (!tenantId) return;

    try {
      setOffboarding(true);
      setOffboardError(null);

      const token = await getAccessToken();
      if (!token) throw new Error('Failed to get access token');

      const response = await fetch(`${API_BASE_URL}/api/tenants/${tenantId}/offboard`, {
        method: 'DELETE',
        headers: { 'Authorization': `Bearer ${token}` },
      });

      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data?.error || `Offboard failed: ${response.statusText}`);
      }

      // Offboard successful – sign out the user as their admin access is gone
      logout();
    } catch (err) {
      setOffboardError(err instanceof Error ? err.message : 'Offboard failed');
      setOffboarding(false);
    }
  };

  // Redirect regular users (non-TenantAdmin) to progress portal
  if (user && !user.isTenantAdmin && !user.isGalacticAdmin) {
    router.replace('/progress');
    return null;
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100">
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
              <h1 className="text-2xl font-bold text-gray-900">Tenant Configuration</h1>
              <p className="text-sm text-gray-500">Tenant: {tenantId}</p>
            </div>
          </div>
        </div>
      </header>

      {/* Main Content */}
      <main className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        {loading ? (
          <div className="bg-white rounded-lg shadow p-8 text-center">
            <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-indigo-600 mx-auto"></div>
            <p className="mt-4 text-gray-600">Loading configuration...</p>
          </div>
        ) : (
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

            {/* Admin Users Management - Only for Tenant Admins */}
            {user?.isTenantAdmin && (
              <div className="bg-white rounded-lg shadow">
                <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-purple-50 to-indigo-50">
                  <div className="flex items-center space-x-2">
                    <svg className="w-6 h-6 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z" />
                    </svg>
                    <div>
                      <h2 className="text-xl font-semibold text-gray-900">Admin Users Management</h2>
                      <p className="text-sm text-gray-500 mt-1">Manage who has admin access to this tenant</p>
                    </div>
                  </div>
                </div>
                <div className="p-6 space-y-4">
                  <div className="bg-blue-50 border border-blue-200 rounded-lg p-4">
                    <div className="flex items-start space-x-3">
                      <svg className="w-5 h-5 text-blue-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                      </svg>
                      <div className="text-sm text-blue-800">
                        <p className="font-medium">About Admin Users</p>
                        <p className="mt-1">
                          Admin users have full access to tenant configuration, all sessions, diagnostics, and settings.
                          Non-admin users only have access to the simplified device tracking page.
                        </p>
                        <p className="mt-2">
                          <strong>Your email:</strong> {user?.upn}
                        </p>
                      </div>
                    </div>
                  </div>

                  {/* Current Admins List */}
                  <div>
                    <label className="block mb-2">
                      <span className="text-gray-700 font-medium">Current Admin Users</span>
                      {loadingAdmins && (
                        <span className="ml-2 text-sm text-gray-500">(Loading...)</span>
                      )}
                    </label>

                    {/* Search Field */}
                    <div className="mb-3">
                      <div className="relative">
                        <input
                          type="text"
                          name="admin-search-field"
                          value={adminSearchQuery}
                          onChange={(e) => {
                            setAdminSearchQuery(e.target.value);
                            setCurrentAdminPage(0);
                          }}
                          placeholder="Search by email..."
                          autoComplete="off"
                          className="w-full px-4 py-2 pl-10 pr-10 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
                        />
                        <svg className="absolute left-3 top-2.5 w-5 h-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                        </svg>
                        {adminSearchQuery && (
                          <button
                            onClick={() => {
                              setAdminSearchQuery("");
                              setCurrentAdminPage(0);
                            }}
                            className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
                            title="Clear search"
                          >
                            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                            </svg>
                          </button>
                        )}
                      </div>
                    </div>

                    {admins.length === 0 && !loadingAdmins ? (
                      <div className="text-sm text-gray-500 italic">No admins found</div>
                    ) : (
                      <>
                        {/* Filtered and Paginated Admin List */}
                        {(() => {
                          const filteredAdmins = admins.filter(admin =>
                            admin.upn.toLowerCase().includes(adminSearchQuery.toLowerCase())
                          );

                          if (filteredAdmins.length === 0) {
                            return (
                              <div className="text-sm text-gray-500 italic p-4 text-center bg-gray-50 rounded-lg">
                                No admins match your search
                              </div>
                            );
                          }

                          const adminsPerPage = 3;
                          const totalAdminPages = Math.ceil(filteredAdmins.length / adminsPerPage);
                          const startAdminIndex = currentAdminPage * adminsPerPage;
                          const endAdminIndex = startAdminIndex + adminsPerPage;
                          const paginatedAdmins = filteredAdmins.slice(startAdminIndex, endAdminIndex);

                          return (
                            <>
                              <div className="space-y-2">
                                {paginatedAdmins.map((admin) => (
                                  <div
                                    key={admin.upn}
                                    className={`flex items-center justify-between p-3 border rounded-lg ${
                                      admin.isEnabled
                                        ? "bg-gray-50 border-gray-200"
                                        : "bg-gray-100 border-gray-300"
                                    }`}
                                  >
                                    <div className="flex-1 min-w-0">
                                      <div className="flex items-center space-x-2">
                                        <div className="font-medium text-gray-900 truncate">{admin.upn}</div>
                                        {!admin.isEnabled && (
                                          <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-200 text-gray-700">
                                            Disabled
                                          </span>
                                        )}
                                      </div>
                                      <div className="text-xs text-gray-500 mt-1">
                                        Added {new Date(admin.addedDate).toLocaleDateString()} by {admin.addedBy}
                                      </div>
                                    </div>
                                    <div className="flex items-center space-x-2 ml-4">
                                      {admin.upn.toLowerCase() === user?.upn?.toLowerCase() ? (
                                        <span className="text-sm text-blue-600 font-medium">(You)</span>
                                      ) : (
                                        <>
                                          <button
                                            onClick={() => handleToggleTenantAdmin(admin.upn, admin.isEnabled)}
                                            disabled={togglingAdmin === admin.upn}
                                            className={`px-3 py-1 text-sm text-white rounded transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                              admin.isEnabled
                                                ? "bg-yellow-600 hover:bg-yellow-700"
                                                : "bg-green-600 hover:bg-green-700"
                                            }`}
                                          >
                                            {togglingAdmin === admin.upn
                                              ? "..."
                                              : admin.isEnabled
                                              ? "Disable"
                                              : "Enable"}
                                          </button>
                                          <button
                                            onClick={() => handleRemoveAdmin(admin.upn)}
                                            disabled={removingAdmin === admin.upn}
                                            className="px-3 py-1 text-sm bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                          >
                                            {removingAdmin === admin.upn ? "Removing..." : "Remove"}
                                          </button>
                                        </>
                                      )}
                                    </div>
                                  </div>
                                ))}
                              </div>

                              {/* Pagination Controls */}
                              {totalAdminPages > 1 && (
                                <div className="flex items-center justify-between mt-4 pt-4 border-t border-gray-200">
                                  <button
                                    onClick={() => setCurrentAdminPage(prev => Math.max(0, prev - 1))}
                                    disabled={currentAdminPage === 0}
                                    className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                  >
                                    Previous
                                  </button>
                                  <span className="text-sm text-gray-600">
                                    Page {currentAdminPage + 1} of {totalAdminPages} ({filteredAdmins.length} admin{filteredAdmins.length !== 1 ? 's' : ''})
                                  </span>
                                  <button
                                    onClick={() => setCurrentAdminPage(prev => Math.min(totalAdminPages - 1, prev + 1))}
                                    disabled={currentAdminPage >= totalAdminPages - 1}
                                    className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                  >
                                    Next
                                  </button>
                                </div>
                              )}
                            </>
                          );
                        })()}
                      </>
                    )}
                  </div>

                  {/* Add New Admin */}
                  <div>
                    <label className="block mb-2">
                      <span className="text-gray-700 font-medium">Add New Admin</span>
                      <p className="text-sm text-gray-500 mb-2">
                        Enter the user email (UPN) to grant admin access.
                        Example: <code className="bg-gray-100 px-1 rounded">newadmin@company.com</code>
                      </p>
                      <div className="flex space-x-2">
                        <input
                          type="email"
                          name="new-admin-email"
                          id="add-new-admin-email-input"
                          value={newAdminEmail}
                          onChange={(e) => setNewAdminEmail(e.target.value)}
                          placeholder="newadmin@tenant.com"
                          autoComplete="off"
                          className="flex-1 px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
                          onKeyDown={(e) => {
                            if (e.key === 'Enter') {
                              e.preventDefault();
                              handleAddAdmin();
                            }
                          }}
                        />
                        <button
                          onClick={handleAddAdmin}
                          disabled={addingAdmin || !newAdminEmail.trim()}
                          className="px-6 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
                        >
                          {addingAdmin ? (
                            <>
                              <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                              <span>Adding...</span>
                            </>
                          ) : (
                            <>
                              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                              </svg>
                              <span>Add</span>
                            </>
                          )}
                        </button>
                      </div>
                    </label>
                  </div>

                  <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3">
                    <div className="flex items-start space-x-2">
                      <svg className="w-5 h-5 text-yellow-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                      </svg>
                      <p className="text-sm text-yellow-800">
                        <strong>Important:</strong> Make sure to include your own email in the list to maintain admin access!
                        The first user to log in was automatically made an admin.
                      </p>
                    </div>
                  </div>
                </div>
              </div>
            )}

            {/* Hardware Whitelist */}
            <div className="bg-white rounded-lg shadow">
              <div className="p-6 border-b border-gray-200">
                <h2 className="text-xl font-semibold text-gray-900">Hardware Whitelist</h2>
                <p className="text-sm text-gray-500 mt-1">Restrict which device manufacturers and models can enroll</p>
              </div>
              <div className="p-6 space-y-6">
                {/* Manufacturer Whitelist */}
                <div>
                  <label className="block">
                    <span className="text-gray-700 font-medium">Allowed Manufacturers</span>
                    <p className="text-sm text-gray-500 mb-2">
                      Comma-separated list. Supports wildcards: <code className="bg-gray-100 px-1 rounded">*</code> = all,
                      <code className="bg-gray-100 px-1 rounded ml-1">Dell*</code> = starts with "Dell"
                    </p>
                    <input
                      type="text"
                      value={manufacturerWhitelist}
                      onChange={(e) => setManufacturerWhitelist(e.target.value)}
                      placeholder="Dell*,HP*,Lenovo*,Microsoft Corporation"
                      className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                    />
                  </label>
                </div>

                {/* Model Whitelist */}
                <div>
                  <label className="block">
                    <span className="text-gray-700 font-medium">Allowed Models</span>
                    <p className="text-sm text-gray-500 mb-2">
                      Comma-separated list. Use <code className="bg-gray-100 px-1 rounded">*</code> to allow all models.
                      Examples: <code className="bg-gray-100 px-1 rounded">Latitude*</code>, <code className="bg-gray-100 px-1 rounded">EliteBook*</code>
                    </p>
                    <input
                      type="text"
                      value={modelWhitelist}
                      onChange={(e) => setModelWhitelist(e.target.value)}
                      placeholder="*"
                      className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                    />
                  </label>
                </div>
              </div>
            </div>

            {/* Agent Collectors */}
            <div className="bg-white rounded-lg shadow">
              <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-emerald-50 to-teal-50">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-emerald-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-gray-900">Agent Collectors</h2>
                    <p className="text-sm text-gray-500 mt-1">Enable optional data collectors on enrolled devices. These generate additional telemetry traffic.</p>
                  </div>
                </div>
              </div>
              <div className="p-6 space-y-5">
                {/* Performance Collector */}
                <div className="flex items-center justify-between p-4 rounded-lg border border-gray-200 hover:border-emerald-200 transition-colors">
                  <div className="flex-1">
                    <div className="flex items-center space-x-2">
                      <p className="font-medium text-gray-900">Performance Collector</p>
                      <span className="text-xs px-2 py-0.5 rounded-full bg-yellow-100 text-yellow-700">Medium Traffic</span>
                    </div>
                    <p className="text-sm text-gray-500 mt-1">CPU, memory, disk metrics at configurable intervals</p>
                    {enablePerformanceCollector && (
                      <div className="mt-2 flex items-center space-x-2">
                        <span className="text-sm text-gray-600">Interval:</span>
                        <input
                          type="number"
                          min="10"
                          max="300"
                          value={performanceCollectorInterval}
                          onChange={(e) => setPerformanceCollectorInterval(parseInt(e.target.value) || 60)}
                          className="w-20 px-2 py-1 border border-gray-300 rounded text-sm text-gray-900 focus:ring-2 focus:ring-emerald-500 focus:border-emerald-500"
                        />
                        <span className="text-sm text-gray-500">seconds</span>
                      </div>
                    )}
                  </div>
                  <button
                    onClick={() => setEnablePerformanceCollector(!enablePerformanceCollector)}
                    className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${enablePerformanceCollector ? 'bg-emerald-500' : 'bg-gray-300'}`}
                  >
                    <span className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${enablePerformanceCollector ? 'translate-x-6' : 'translate-x-1'}`} />
                  </button>
                </div>

                <div className="bg-blue-50 border border-blue-200 rounded-lg p-3">
                  <div className="flex items-start space-x-2">
                    <svg className="w-5 h-5 text-blue-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                    <p className="text-sm text-blue-800">
                      Changes take effect on the next agent config refresh (default: every 5 minutes). Core collectors (Hello Detector) and enrollment tracking are always active.
                    </p>
                  </div>
                </div>
              </div>
            </div>

            {/* Serial Number Validation */}
            <div className="bg-white rounded-lg shadow">
              <div className="p-6 border-b border-gray-200">
                <div className="flex items-center justify-between">
                  <div>
                    <h2 className="text-xl font-semibold text-gray-900">Serial Number Validation</h2>
                    <p className="text-sm text-gray-500 mt-1">Validate devices against Intune Autopilot registration (mandatory for agent ingestion)</p>
                  </div>
                  <span className={`inline-flex items-center px-3 py-1 rounded-full text-xs font-medium ${validateSerialNumber ? "bg-green-100 text-green-800" : "bg-red-100 text-red-800"}`}>
                    {validateSerialNumber ? "Enabled" : "Disabled"}
                  </span>
                </div>
              </div>
              <div className="p-6 space-y-4">
                <label className="flex items-center justify-between">
                  <div>
                    <p className="font-medium text-gray-900">Enable Serial Number Validation</p>
                    <p className="text-sm text-gray-500">
                      Enabling starts Microsoft Entra admin consent for the validator app. After consent, the setting is saved automatically.
                    </p>
                  </div>
                  <button
                    onClick={() => {
                      if (validateSerialNumber) {
                        setValidateSerialNumber(false);
                      } else {
                        beginSerialValidationEnableFlow();
                      }
                    }}
                    disabled={saving || serialConsentInProgress}
                    className={`relative inline-flex h-8 w-14 items-center rounded-full transition-colors disabled:opacity-60 disabled:cursor-not-allowed ${validateSerialNumber ? 'bg-green-600' : 'bg-gray-300'}`}
                  >
                    <span className={`inline-block h-6 w-6 transform rounded-full bg-white transition-transform ${validateSerialNumber ? 'translate-x-7' : 'translate-x-1'}`} />
                  </button>
                </label>

                <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
                  <p className="text-sm text-amber-900">
                    <strong>Important:</strong> If this is disabled, backend agent endpoints reject requests for this tenant.
                    Use this toggle and complete admin consent first.
                  </p>
                </div>

                {serialConsentInProgress && (
                  <div className="bg-blue-50 border border-blue-200 rounded-lg p-3 text-sm text-blue-800">
                    Checking or applying admin consent...
                  </div>
                )}
              </div>
            </div>

            {/* Data Management Settings */}
            <div className="bg-white rounded-lg shadow">
              <div className="p-6 border-b border-gray-200">
                <h2 className="text-xl font-semibold text-gray-900">Data Management</h2>
                <p className="text-sm text-gray-500 mt-1">Configure data retention and session timeout policies</p>
              </div>
              <div className="p-6 space-y-6">
                {/* Data Retention Days */}
                <div>
                  <label className="block">
                    <span className="text-gray-700 font-medium">Data Retention Period (Days)</span>
                    <p className="text-sm text-gray-500 mb-2">
                      Sessions and events older than this will be automatically deleted by the daily maintenance job. Default: 90 days.
                    </p>
                    <input
                      type="number"
                      min="7"
                      max="3650"
                      value={dataRetentionDays}
                      onChange={(e) => setDataRetentionDays(parseInt(e.target.value) || 90)}
                      className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                    />
                    <p className="text-xs text-gray-400 mt-1">Minimum: 7 days, Maximum: 3650 days (10 years)</p>
                  </label>
                </div>

                {/* Session Timeout Hours */}
                <div>
                  <label className="block">
                    <span className="text-gray-700 font-medium">Session Timeout (Hours)</span>
                    <p className="text-sm text-gray-500 mb-2">
                      Sessions in "InProgress" status longer than this will be marked as "Failed - Timed Out".
                      This prevents stalled sessions from running indefinitely and skewing statistics.
                      <br />
                      <strong>Tip:</strong> Use the same value as your ESP (Enrollment Status Page) timeout for consistency.
                    </p>
                    <input
                      type="number"
                      min="1"
                      max="48"
                      value={sessionTimeoutHours}
                      onChange={(e) => setSessionTimeoutHours(parseInt(e.target.value) || 5)}
                      className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                    />
                    <p className="text-xs text-gray-400 mt-1">Default: 5 hours (ESP default). Minimum: 1 hour, Maximum: 48 hours</p>
                  </label>
                </div>
              </div>
            </div>


            {/* Danger Zone: Tenant Offboarding */}
            {user?.isTenantAdmin && (
              <div className="bg-white rounded-lg shadow border-2 border-red-200">
                <div className="p-6 border-b border-red-100 bg-red-50">
                  <div className="flex items-center space-x-2">
                    <svg className="w-6 h-6 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                    </svg>
                    <div>
                      <h2 className="text-xl font-semibold text-red-900">Danger Zone</h2>
                      <p className="text-sm text-red-600 mt-1">Irreversible and destructive actions</p>
                    </div>
                  </div>
                </div>
                <div className="p-6">
                  <div className="flex items-center justify-between">
                    <div>
                      <h3 className="text-base font-semibold text-gray-900">Offboard this Tenant</h3>
                      <p className="text-sm text-gray-500 mt-1">
                        Permanently deletes <strong>all data</strong> for this tenant – sessions, events, rules, audit logs, configuration, and all admin accounts including yours. This cannot be undone.
                      </p>
                    </div>
                    <button
                      onClick={() => { setShowOffboardDialog(true); setOffboardConfirmText(""); setOffboardError(null); }}
                      className="ml-6 flex-shrink-0 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors text-sm font-medium"
                    >
                      Offboard Tenant
                    </button>
                  </div>
                </div>
              </div>
            )}

            {/* Offboard Confirmation Dialog */}
            {showOffboardDialog && (
              <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
                <div className="bg-white rounded-lg shadow-xl max-w-md w-full p-6">
                  <div className="flex items-center space-x-3 mb-4">
                    <div className="w-12 h-12 bg-red-100 rounded-full flex items-center justify-center flex-shrink-0">
                      <svg className="w-6 h-6 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                      </svg>
                    </div>
                    <div>
                      <h3 className="text-lg font-bold text-gray-900">Tenant Offboarding</h3>
                      <p className="text-sm text-red-600 font-medium">This action is permanent and cannot be undone.</p>
                    </div>
                  </div>

                  <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4 text-sm text-red-800 space-y-1">
                    <p className="font-semibold">The following will be permanently deleted:</p>
                    <ul className="list-disc list-inside mt-2 space-y-1">
                      <li>All enrollment sessions and events</li>
                      <li>All gather and analyze rules</li>
                      <li>Audit logs and usage metrics</li>
                      <li>Tenant configuration</li>
                      <li>All admin accounts (including yours)</li>
                    </ul>
                    <p className="mt-3 font-semibold">After offboarding you will be signed out and lose all access.</p>
                  </div>

                  <div className="mb-4">
                    <label className="block text-sm font-medium text-gray-700 mb-1">
                      Type <span className="font-bold text-red-600">OFFBOARD</span> to confirm
                    </label>
                    <input
                      type="text"
                      value={offboardConfirmText}
                      onChange={(e) => setOffboardConfirmText(e.target.value)}
                      placeholder="OFFBOARD"
                      className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                      autoComplete="off"
                    />
                  </div>

                  {offboardError && (
                    <div className="mb-4 bg-red-50 border border-red-200 rounded p-3 text-sm text-red-800">
                      {offboardError}
                    </div>
                  )}

                  <div className="flex space-x-3">
                    <button
                      onClick={() => { setShowOffboardDialog(false); setOffboardConfirmText(""); setOffboardError(null); }}
                      disabled={offboarding}
                      className="flex-1 px-4 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 transition-colors"
                    >
                      Cancel
                    </button>
                    <button
                      onClick={handleOffboard}
                      disabled={offboarding || offboardConfirmText !== "OFFBOARD"}
                      className="flex-1 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center justify-center space-x-2"
                    >
                      {offboarding ? (
                        <>
                          <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                          <span>Offboarding...</span>
                        </>
                      ) : (
                        <span>Permanently Delete All Data</span>
                      )}
                    </button>
                  </div>
                </div>
              </div>
            )}

            {/* Configuration Info */}
            {config && (
              <div className="bg-blue-50 border border-blue-200 rounded-lg p-4">
                <div className="flex items-start space-x-3">
                  <svg className="w-5 h-5 text-blue-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <div className="text-sm text-blue-800">
                    <p className="font-medium">Configuration Info</p>
                    <p className="mt-1">Last updated: {new Date(config.lastUpdated).toLocaleString()}</p>
                    <p>Updated by: {config.updatedBy}</p>
                  </div>
                </div>
              </div>
            )}

            {/* Action Buttons */}
            <div className="flex items-center justify-end space-x-4 pt-4">
              <button
                onClick={handleReset}
                disabled={saving}
                className="px-6 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                Reset
              </button>
              <button
                onClick={handleSave}
                disabled={saving}
                className="px-6 py-2 bg-indigo-600 text-white rounded-md hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
              >
                {saving ? (
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
      </main>
      </div>
    </ProtectedRoute>
  );
}
