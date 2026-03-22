"use client";

import { createContext, useCallback, useContext, useEffect, useState } from "react";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

// Re-export for section components
export interface AdminConfiguration {
  partitionKey: string;
  rowKey: string;
  lastUpdated: string;
  updatedBy: string;
  globalRateLimitRequestsPerMinute: number;
  platformStatsBlobSasUrl?: string;
  collectorIdleTimeoutMinutes?: number;
  maxSessionWindowHours?: number;
  maintenanceBlockDurationHours?: number;
  diagnosticsGlobalLogPathsJson?: string;
  customSettings?: string;
  nvdApiKey?: string;
  vulnerabilityCorrelationEnabled?: boolean;
  vulnerabilityDataLastSyncUtc?: string;
}

interface AdminConfigContextValue {
  // Admin config
  adminConfig: AdminConfiguration | null;
  setAdminConfig: React.Dispatch<React.SetStateAction<AdminConfiguration | null>>;
  loadingConfig: boolean;
  savingConfig: boolean;
  setSavingConfig: React.Dispatch<React.SetStateAction<boolean>>;

  // Config field state
  globalRateLimit: number;
  setGlobalRateLimit: (value: number) => void;
  platformStatsBlobSasUrl: string;
  setPlatformStatsBlobSasUrl: (value: string) => void;
  collectorIdleTimeoutMinutes: number;
  setCollectorIdleTimeoutMinutes: (value: number) => void;
  maxSessionWindowHours: number;
  setMaxSessionWindowHours: (value: number) => void;
  maintenanceBlockDurationHours: number;
  setMaintenanceBlockDurationHours: (value: number) => void;

  // Diagnostics log paths
  globalDiagPaths: DiagnosticsLogPath[];
  setGlobalDiagPaths: React.Dispatch<React.SetStateAction<DiagnosticsLogPath[]>>;
  savingDiagPaths: boolean;

  // Tenants
  tenants: TenantConfiguration[];
  setTenants: React.Dispatch<React.SetStateAction<TenantConfiguration[]>>;
  loadingTenants: boolean;
  fetchTenants: () => void;
  previewApproved: Set<string>;
  setPreviewApproved: React.Dispatch<React.SetStateAction<Set<string>>>;

  // Notifications
  error: string | null;
  setError: (error: string | null) => void;
  successMessage: string | null;
  setSuccessMessage: (message: string | null) => void;

  // Auth
  getAccessToken: () => Promise<string | null>;

  // Actions
  handleSaveAdminConfig: () => Promise<void>;
  handleResetAdminConfig: () => void;
  handleSaveDiagPaths: (paths: DiagnosticsLogPath[]) => Promise<void>;
}

import { type DiagnosticsLogPath } from "./components/DiagnosticsLogPathsSection";
import { type TenantConfiguration } from "./components/TenantManagementSection";

const AdminConfigContext = createContext<AdminConfigContextValue | null>(null);

export function useAdminConfig() {
  const ctx = useContext(AdminConfigContext);
  if (!ctx) throw new Error("useAdminConfig must be used within AdminConfigProvider");
  return ctx;
}

export function AdminConfigProvider({ children }: { children: React.ReactNode }) {
  const { getAccessToken, user } = useAuth();

  // Admin Configuration state
  const [adminConfig, setAdminConfig] = useState<AdminConfiguration | null>(null);
  const [loadingConfig, setLoadingConfig] = useState(false);
  const [savingConfig, setSavingConfig] = useState(false);
  const [globalRateLimit, setGlobalRateLimit] = useState(100);
  const [platformStatsBlobSasUrl, setPlatformStatsBlobSasUrl] = useState("");
  const [collectorIdleTimeoutMinutes, setCollectorIdleTimeoutMinutes] = useState(15);
  const [maxSessionWindowHours, setMaxSessionWindowHours] = useState(24);
  const [maintenanceBlockDurationHours, setMaintenanceBlockDurationHours] = useState(12);

  // Diagnostics Log Paths state
  const [globalDiagPaths, setGlobalDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [savingDiagPaths, setSavingDiagPaths] = useState(false);

  // Tenant Management state
  const [tenants, setTenants] = useState<TenantConfiguration[]>([]);
  const [loadingTenants, setLoadingTenants] = useState(false);
  const [previewApproved, setPreviewApproved] = useState<Set<string>>(new Set());

  // Notifications
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const isGalacticAdmin = user?.isGalacticAdmin === true;

  // Fetch admin configuration
  useEffect(() => {
    if (!isGalacticAdmin) return;

    const fetchAdminConfig = async () => {
      try {
        setLoadingConfig(true);
        setError(null);

        const response = await authenticatedFetch(`${API_BASE_URL}/api/global/config`, getAccessToken);

        if (!response.ok) {
          throw new Error(`Failed to load admin configuration: ${response.statusText}`);
        }

        const data: AdminConfiguration = await response.json();
        setAdminConfig(data);
        setGlobalRateLimit(data.globalRateLimitRequestsPerMinute);
        setPlatformStatsBlobSasUrl(data.platformStatsBlobSasUrl ?? "");
        setCollectorIdleTimeoutMinutes(data.collectorIdleTimeoutMinutes ?? 15);
        setMaxSessionWindowHours(data.maxSessionWindowHours ?? 24);
        setMaintenanceBlockDurationHours(data.maintenanceBlockDurationHours ?? 12);
        try {
          setGlobalDiagPaths(data.diagnosticsGlobalLogPathsJson ? JSON.parse(data.diagnosticsGlobalLogPathsJson) : []);
        } catch {
          setGlobalDiagPaths([]);
        }
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          console.error("Session expired while fetching admin configuration");
        } else {
          console.error("Error fetching admin configuration:", err);
        }
        setError(err instanceof Error ? err.message : "Failed to load admin configuration");
      } finally {
        setLoadingConfig(false);
      }
    };

    fetchAdminConfig();
  }, [isGalacticAdmin, getAccessToken]);

  // Fetch tenants + preview whitelist
  const fetchTenants = useCallback(async () => {
    if (!isGalacticAdmin) return;
    try {
      setLoadingTenants(true);

      const [tenantsRes, previewRes] = await Promise.all([
        authenticatedFetch(`${API_BASE_URL}/api/config/all`, getAccessToken),
        authenticatedFetch(`${API_BASE_URL}/api/preview/whitelist`, getAccessToken)
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
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching tenants");
      } else {
        console.error("Error fetching tenants:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load tenants");
    } finally {
      setLoadingTenants(false);
    }
  }, [isGalacticAdmin, getAccessToken]);

  useEffect(() => {
    fetchTenants();
  }, [fetchTenants]);

  // Save admin config
  const handleSaveAdminConfig = useCallback(async () => {
    if (!adminConfig) return;

    try {
      setSavingConfig(true);
      setError(null);
      setSuccessMessage(null);

      const updatedConfig: AdminConfiguration = {
        ...adminConfig,
        globalRateLimitRequestsPerMinute: globalRateLimit,
        platformStatsBlobSasUrl: platformStatsBlobSasUrl.trim(),
        collectorIdleTimeoutMinutes,
        maxSessionWindowHours,
        maintenanceBlockDurationHours,
      };

      const response = await authenticatedFetch(`${API_BASE_URL}/api/global/config`, getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) {
        throw new Error(`Failed to save admin configuration: ${response.statusText}`);
      }

      const result = await response.json();
      setAdminConfig(result.config);
      setSuccessMessage("Admin configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving admin configuration");
      } else {
        console.error("Error saving admin configuration:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to save admin configuration");
    } finally {
      setSavingConfig(false);
    }
  }, [adminConfig, globalRateLimit, platformStatsBlobSasUrl, collectorIdleTimeoutMinutes, maxSessionWindowHours, maintenanceBlockDurationHours, getAccessToken]);

  // Reset admin config
  const handleResetAdminConfig = useCallback(() => {
    if (!adminConfig) return;
    setGlobalRateLimit(adminConfig.globalRateLimitRequestsPerMinute);
    setPlatformStatsBlobSasUrl(adminConfig.platformStatsBlobSasUrl ?? "");
    setCollectorIdleTimeoutMinutes(adminConfig.collectorIdleTimeoutMinutes ?? 15);
    setMaxSessionWindowHours(adminConfig.maxSessionWindowHours ?? 24);
    setMaintenanceBlockDurationHours(adminConfig.maintenanceBlockDurationHours ?? 12);
    try {
      setGlobalDiagPaths(adminConfig.diagnosticsGlobalLogPathsJson ? JSON.parse(adminConfig.diagnosticsGlobalLogPathsJson) : []);
    } catch {
      setGlobalDiagPaths([]);
    }
    setSuccessMessage(null);
    setError(null);
  }, [adminConfig]);

  // Save diagnostics paths
  const handleSaveDiagPaths = useCallback(async (paths: DiagnosticsLogPath[]) => {
    if (!adminConfig) return;
    try {
      setSavingDiagPaths(true);
      setError(null);
      setSuccessMessage(null);

      const updatedConfig: AdminConfiguration = {
        ...adminConfig,
        diagnosticsGlobalLogPathsJson: JSON.stringify(paths),
      };

      const response = await authenticatedFetch(`${API_BASE_URL}/api/global/config`, getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) throw new Error(`Failed to save diagnostics paths: ${response.statusText}`);

      const result = await response.json();
      setAdminConfig(result.config);
      setGlobalDiagPaths(paths);
      setSuccessMessage("Global diagnostics log paths saved successfully!");
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving diagnostics paths");
      }
      setError(err instanceof Error ? err.message : "Failed to save diagnostics paths");
    } finally {
      setSavingDiagPaths(false);
    }
  }, [adminConfig, getAccessToken]);

  return (
    <AdminConfigContext.Provider value={{
      adminConfig, setAdminConfig, loadingConfig, savingConfig, setSavingConfig,
      globalRateLimit, setGlobalRateLimit,
      platformStatsBlobSasUrl, setPlatformStatsBlobSasUrl,
      collectorIdleTimeoutMinutes, setCollectorIdleTimeoutMinutes,
      maxSessionWindowHours, setMaxSessionWindowHours,
      maintenanceBlockDurationHours, setMaintenanceBlockDurationHours,
      globalDiagPaths, setGlobalDiagPaths, savingDiagPaths,
      tenants, setTenants, loadingTenants, fetchTenants,
      previewApproved, setPreviewApproved,
      error, setError, successMessage, setSuccessMessage,
      getAccessToken,
      handleSaveAdminConfig, handleResetAdminConfig, handleSaveDiagPaths,
    }}>
      {children}
    </AdminConfigContext.Provider>
  );
}
