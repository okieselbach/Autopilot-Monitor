"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";
import { TenantManagementSection, TenantConfiguration } from "./components/TenantManagementSection";
import { MaintenanceSection } from "./components/MaintenanceSection";
import { DiagnosticsLogPathsSection, DiagnosticsLogPath } from "./components/DiagnosticsLogPathsSection";
import { DeviceBlockSection } from "./components/DeviceBlockSection";
import { SessionExportSection } from "./components/SessionExportSection";
import { AdminConfigSettingsSection } from "./components/AdminConfigSettingsSection";

interface AdminConfiguration {
  partitionKey: string;
  rowKey: string;
  lastUpdated: string;
  updatedBy: string;
  globalRateLimitRequestsPerMinute: number;
  platformStatsBlobSasUrl?: string;
  maxCollectorDurationHours?: number;
  maxSessionWindowHours?: number;
  maintenanceBlockDurationHours?: number;
  diagnosticsGlobalLogPathsJson?: string;
  customSettings?: string;
}

export default function AdminConfigurationPage() {
  const router = useRouter();
  const { getAccessToken, user } = useAuth();
  const [galacticAdminMode, setGalacticAdminMode] = useState(false);
  // Diagnostics Log Paths state
  const [globalDiagPaths, setGlobalDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [savingDiagPaths, setSavingDiagPaths] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Admin Configuration state
  const [adminConfig, setAdminConfig] = useState<AdminConfiguration | null>(null);
  const [loadingConfig, setLoadingConfig] = useState(false);
  const [savingConfig, setSavingConfig] = useState(false);
  const [globalRateLimit, setGlobalRateLimit] = useState(100);
  const [platformStatsBlobSasUrl, setPlatformStatsBlobSasUrl] = useState("");
  const [maxCollectorDurationHours, setMaxCollectorDurationHours] = useState(4);
  const [maxSessionWindowHours, setMaxSessionWindowHours] = useState(24);
  const [maintenanceBlockDurationHours, setMaintenanceBlockDurationHours] = useState(12);

  // Tenant Management state
  const [tenants, setTenants] = useState<TenantConfiguration[]>([]);
  const [loadingTenants, setLoadingTenants] = useState(false);

  // Preview Whitelist state
  const [previewApproved, setPreviewApproved] = useState<Set<string>>(new Set());

  // Load galactic admin mode - use server-derived isGalacticAdmin as authoritative source
  useEffect(() => {
    const galacticMode = user?.isGalacticAdmin === true;
    setGalacticAdminMode(galacticMode);

    // Redirect if not a galactic admin
    if (user && !galacticMode) {
      router.push("/dashboard");
    }
  }, [router, user]);

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
        setMaxCollectorDurationHours(data.maxCollectorDurationHours ?? 4);
        setMaxSessionWindowHours(data.maxSessionWindowHours ?? 24);
        setMaintenanceBlockDurationHours(data.maintenanceBlockDurationHours ?? 12);
        try {
          setGlobalDiagPaths(data.diagnosticsGlobalLogPathsJson ? JSON.parse(data.diagnosticsGlobalLogPathsJson) : []);
        } catch {
          setGlobalDiagPaths([]);
        }
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
  const fetchTenants = useCallback(async () => {
    if (!galacticAdminMode) return;
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
  }, [galacticAdminMode, getAccessToken]);

  useEffect(() => {
    fetchTenants();
  }, [fetchTenants]);

  const handleSaveDiagPaths = async (paths: DiagnosticsLogPath[]) => {
    if (!adminConfig) return;
    try {
      setSavingDiagPaths(true);
      setError(null);
      setSuccessMessage(null);

      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");

      const updatedConfig: AdminConfiguration = {
        ...adminConfig,
        diagnosticsGlobalLogPathsJson: JSON.stringify(paths),
      };

      const response = await fetch(`${API_BASE_URL}/api/global/config`, {
        method: "PUT",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) throw new Error(`Failed to save diagnostics paths: ${response.statusText}`);

      const result = await response.json();
      setAdminConfig(result.config);
      setGlobalDiagPaths(paths);
      setSuccessMessage("Global diagnostics log paths saved successfully!");
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save diagnostics paths");
    } finally {
      setSavingDiagPaths(false);
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
        maxCollectorDurationHours: maxCollectorDurationHours,
        maxSessionWindowHours: maxSessionWindowHours,
        maintenanceBlockDurationHours: maintenanceBlockDurationHours,
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
    setMaxCollectorDurationHours(adminConfig.maxCollectorDurationHours ?? 4);
    setMaxSessionWindowHours(adminConfig.maxSessionWindowHours ?? 24);
    setMaintenanceBlockDurationHours(adminConfig.maintenanceBlockDurationHours ?? 12);
    try {
      setGlobalDiagPaths(adminConfig.diagnosticsGlobalLogPathsJson ? JSON.parse(adminConfig.diagnosticsGlobalLogPathsJson) : []);
    } catch {
      setGlobalDiagPaths([]);
    }
    setSuccessMessage(null);
    setError(null);
  };

  if (!galacticAdminMode) {
    return null;
  }

  return (
    <ProtectedRoute requireGalacticAdmin>
      <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
        {/* Header */}
        <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <button
                  onClick={() => router.push("/dashboard")}
                  className="text-sm text-gray-600 dark:text-gray-400 hover:text-gray-900 dark:hover:text-gray-100 mb-2 flex items-center"
                >
                  &larr; Back to Dashboard
                </button>
                <div>
                  <h1 className="text-3xl font-bold text-gray-900 dark:text-white">Admin Configuration</h1>
                  <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">Galactic Admin Operations</p>
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
            <TenantManagementSection
              tenants={tenants}
              loadingTenants={loadingTenants}
              fetchTenants={fetchTenants}
              previewApproved={previewApproved}
              setPreviewApproved={setPreviewApproved}
              setTenants={setTenants}
              getAccessToken={getAccessToken}
              setError={setError}
              setSuccessMessage={setSuccessMessage}
            />

            {/* Maintenance + Reseed Sections */}
            <MaintenanceSection
              getAccessToken={getAccessToken}
              setError={setError}
              setSuccessMessage={setSuccessMessage}
            />

            {/* Diagnostics Log Paths */}
            <DiagnosticsLogPathsSection
              globalDiagPaths={globalDiagPaths}
              setGlobalDiagPaths={setGlobalDiagPaths}
              loadingConfig={loadingConfig}
              savingDiagPaths={savingDiagPaths}
              adminConfigExists={!!adminConfig}
              onSave={handleSaveDiagPaths}
            />

            {/* Device Block Management */}
            <DeviceBlockSection
              tenants={tenants}
              maxSessionWindowHours={maxSessionWindowHours}
              setMaxSessionWindowHours={setMaxSessionWindowHours}
              maintenanceBlockDurationHours={maintenanceBlockDurationHours}
              setMaintenanceBlockDurationHours={setMaintenanceBlockDurationHours}
              savingConfig={savingConfig}
              adminConfigExists={!!adminConfig}
              onSaveAdminConfig={handleSaveAdminConfig}
              getAccessToken={getAccessToken}
              setError={setError}
              setSuccessMessage={setSuccessMessage}
            />

            {/* Session Event Export */}
            <SessionExportSection
              tenants={tenants}
              getAccessToken={getAccessToken}
            />

            {/* Global Settings */}
            <AdminConfigSettingsSection
              loadingConfig={loadingConfig}
              savingConfig={savingConfig}
              adminConfig={adminConfig}
              globalRateLimit={globalRateLimit}
              setGlobalRateLimit={setGlobalRateLimit}
              platformStatsBlobSasUrl={platformStatsBlobSasUrl}
              setPlatformStatsBlobSasUrl={setPlatformStatsBlobSasUrl}
              maxCollectorDurationHours={maxCollectorDurationHours}
              setMaxCollectorDurationHours={setMaxCollectorDurationHours}
              onSave={handleSaveAdminConfig}
              onReset={handleResetAdminConfig}
            />
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
