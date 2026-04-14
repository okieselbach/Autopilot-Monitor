"use client";

import { createContext, useCallback, useContext, useEffect, useState } from "react";
import { useAuth } from "../../contexts/AuthContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import type { AdminConfiguration, OpsAlertRule } from "@/types/adminConfig";

// Re-export so existing `import { AdminConfiguration } from "../AdminConfigContext"` consumers keep working
export type { AdminConfiguration, OpsAlertRule };

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
  opsEventRetentionDays: number;
  setOpsEventRetentionDays: (value: number) => void;
  allowAgentDowngrade: boolean;
  setAllowAgentDowngrade: (value: boolean) => void;

  // Diagnostics log paths
  globalDiagPaths: DiagnosticsLogPath[];
  setGlobalDiagPaths: React.Dispatch<React.SetStateAction<DiagnosticsLogPath[]>>;
  savingDiagPaths: boolean;

  // Ops Alert settings
  opsAlertRules: OpsAlertRule[];
  setOpsAlertRules: React.Dispatch<React.SetStateAction<OpsAlertRule[]>>;
  opsAlertTelegramEnabled: boolean;
  setOpsAlertTelegramEnabled: (value: boolean) => void;
  opsAlertTelegramChatId: string;
  setOpsAlertTelegramChatId: (value: string) => void;
  opsAlertTeamsEnabled: boolean;
  setOpsAlertTeamsEnabled: (value: boolean) => void;
  opsAlertTeamsWebhookUrl: string;
  setOpsAlertTeamsWebhookUrl: (value: string) => void;
  opsAlertSlackEnabled: boolean;
  setOpsAlertSlackEnabled: (value: boolean) => void;
  opsAlertSlackWebhookUrl: string;
  setOpsAlertSlackWebhookUrl: (value: string) => void;
  savingOpsAlerts: boolean;
  handleSaveOpsAlertConfig: (rules: OpsAlertRule[], telegramEnabled: boolean, telegramChatId: string, teamsEnabled: boolean, teamsWebhookUrl: string, slackEnabled: boolean, slackWebhookUrl: string) => Promise<void>;

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
  const [opsEventRetentionDays, setOpsEventRetentionDays] = useState(90);
  const [allowAgentDowngrade, setAllowAgentDowngrade] = useState(false);

  // Diagnostics Log Paths state
  const [globalDiagPaths, setGlobalDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [savingDiagPaths, setSavingDiagPaths] = useState(false);

  // Ops Alert state
  const [opsAlertRules, setOpsAlertRules] = useState<OpsAlertRule[]>([]);
  const [opsAlertTelegramEnabled, setOpsAlertTelegramEnabled] = useState(false);
  const [opsAlertTelegramChatId, setOpsAlertTelegramChatId] = useState("");
  const [opsAlertTeamsEnabled, setOpsAlertTeamsEnabled] = useState(false);
  const [opsAlertTeamsWebhookUrl, setOpsAlertTeamsWebhookUrl] = useState("");
  const [opsAlertSlackEnabled, setOpsAlertSlackEnabled] = useState(false);
  const [opsAlertSlackWebhookUrl, setOpsAlertSlackWebhookUrl] = useState("");
  const [savingOpsAlerts, setSavingOpsAlerts] = useState(false);

  // Tenant Management state
  const [tenants, setTenants] = useState<TenantConfiguration[]>([]);
  const [loadingTenants, setLoadingTenants] = useState(false);
  const [previewApproved, setPreviewApproved] = useState<Set<string>>(new Set());

  // Notifications
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  const isGlobalAdmin = user?.isGlobalAdmin === true;

  // Fetch admin configuration
  useEffect(() => {
    if (!isGlobalAdmin) return;

    const fetchAdminConfig = async () => {
      try {
        setLoadingConfig(true);
        setError(null);

        const response = await authenticatedFetch(api.globalConfig.get(), getAccessToken);

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
        setOpsEventRetentionDays(data.opsEventRetentionDays ?? 90);
        setAllowAgentDowngrade(data.allowAgentDowngrade ?? false);
        try {
          setGlobalDiagPaths(data.diagnosticsGlobalLogPathsJson ? JSON.parse(data.diagnosticsGlobalLogPathsJson) : []);
        } catch {
          setGlobalDiagPaths([]);
        }
        // Ops Alert state hydration
        try {
          setOpsAlertRules(data.opsAlertRulesJson ? JSON.parse(data.opsAlertRulesJson) : []);
        } catch {
          setOpsAlertRules([]);
        }
        setOpsAlertTelegramEnabled(data.opsAlertTelegramEnabled ?? false);
        setOpsAlertTelegramChatId(data.opsAlertTelegramChatId ?? "");
        setOpsAlertTeamsEnabled(data.opsAlertTeamsEnabled ?? false);
        setOpsAlertTeamsWebhookUrl(data.opsAlertTeamsWebhookUrl ?? "");
        setOpsAlertSlackEnabled(data.opsAlertSlackEnabled ?? false);
        setOpsAlertSlackWebhookUrl(data.opsAlertSlackWebhookUrl ?? "");
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
  }, [isGlobalAdmin, getAccessToken]);

  // Fetch tenants + preview whitelist
  const fetchTenants = useCallback(async () => {
    if (!isGlobalAdmin) return;
    try {
      setLoadingTenants(true);

      const [tenantsRes, previewRes] = await Promise.all([
        authenticatedFetch(api.config.all(), getAccessToken),
        authenticatedFetch(api.preview.whitelist(), getAccessToken)
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
  }, [isGlobalAdmin, getAccessToken]);

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
        opsEventRetentionDays,
        allowAgentDowngrade,
      };

      const response = await authenticatedFetch(api.globalConfig.get(), getAccessToken, {
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
  }, [adminConfig, globalRateLimit, platformStatsBlobSasUrl, collectorIdleTimeoutMinutes, maxSessionWindowHours, maintenanceBlockDurationHours, opsEventRetentionDays, allowAgentDowngrade, getAccessToken]);

  // Reset admin config
  const handleResetAdminConfig = useCallback(() => {
    if (!adminConfig) return;
    setGlobalRateLimit(adminConfig.globalRateLimitRequestsPerMinute);
    setPlatformStatsBlobSasUrl(adminConfig.platformStatsBlobSasUrl ?? "");
    setCollectorIdleTimeoutMinutes(adminConfig.collectorIdleTimeoutMinutes ?? 15);
    setMaxSessionWindowHours(adminConfig.maxSessionWindowHours ?? 24);
    setMaintenanceBlockDurationHours(adminConfig.maintenanceBlockDurationHours ?? 12);
    setOpsEventRetentionDays(adminConfig.opsEventRetentionDays ?? 90);
    setAllowAgentDowngrade(adminConfig.allowAgentDowngrade ?? false);
    try {
      setGlobalDiagPaths(adminConfig.diagnosticsGlobalLogPathsJson ? JSON.parse(adminConfig.diagnosticsGlobalLogPathsJson) : []);
    } catch {
      setGlobalDiagPaths([]);
    }
    try {
      setOpsAlertRules(adminConfig.opsAlertRulesJson ? JSON.parse(adminConfig.opsAlertRulesJson) : []);
    } catch {
      setOpsAlertRules([]);
    }
    setOpsAlertTelegramEnabled(adminConfig.opsAlertTelegramEnabled ?? false);
    setOpsAlertTelegramChatId(adminConfig.opsAlertTelegramChatId ?? "");
    setOpsAlertTeamsEnabled(adminConfig.opsAlertTeamsEnabled ?? false);
    setOpsAlertTeamsWebhookUrl(adminConfig.opsAlertTeamsWebhookUrl ?? "");
    setOpsAlertSlackEnabled(adminConfig.opsAlertSlackEnabled ?? false);
    setOpsAlertSlackWebhookUrl(adminConfig.opsAlertSlackWebhookUrl ?? "");
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

      const response = await authenticatedFetch(api.globalConfig.get(), getAccessToken, {
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

  // Save ops alert config (rules + providers)
  const handleSaveOpsAlertConfig = useCallback(async (
    rules: OpsAlertRule[],
    telegramEnabled: boolean, telegramChatId: string,
    teamsEnabled: boolean, teamsWebhookUrl: string,
    slackEnabled: boolean, slackWebhookUrl: string,
  ) => {
    if (!adminConfig) return;
    try {
      setSavingOpsAlerts(true);
      setError(null);
      setSuccessMessage(null);

      const updatedConfig: AdminConfiguration = {
        ...adminConfig,
        opsAlertRulesJson: JSON.stringify(rules),
        opsAlertTelegramEnabled: telegramEnabled,
        opsAlertTelegramChatId: telegramChatId.trim(),
        opsAlertTeamsEnabled: teamsEnabled,
        opsAlertTeamsWebhookUrl: teamsWebhookUrl.trim(),
        opsAlertSlackEnabled: slackEnabled,
        opsAlertSlackWebhookUrl: slackWebhookUrl.trim(),
      };

      const response = await authenticatedFetch(api.globalConfig.get(), getAccessToken, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) throw new Error(`Failed to save alert configuration: ${response.statusText}`);

      const result = await response.json();
      setAdminConfig(result.config);
      setOpsAlertRules(rules);
      setOpsAlertTelegramEnabled(telegramEnabled);
      setOpsAlertTelegramChatId(telegramChatId.trim());
      setOpsAlertTeamsEnabled(teamsEnabled);
      setOpsAlertTeamsWebhookUrl(teamsWebhookUrl.trim());
      setOpsAlertSlackEnabled(slackEnabled);
      setOpsAlertSlackWebhookUrl(slackWebhookUrl.trim());
      setSuccessMessage("Alert configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 4000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving alert configuration");
      }
      setError(err instanceof Error ? err.message : "Failed to save alert configuration");
    } finally {
      setSavingOpsAlerts(false);
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
      opsEventRetentionDays, setOpsEventRetentionDays,
      allowAgentDowngrade, setAllowAgentDowngrade,
      globalDiagPaths, setGlobalDiagPaths, savingDiagPaths,
      opsAlertRules, setOpsAlertRules,
      opsAlertTelegramEnabled, setOpsAlertTelegramEnabled,
      opsAlertTelegramChatId, setOpsAlertTelegramChatId,
      opsAlertTeamsEnabled, setOpsAlertTeamsEnabled,
      opsAlertTeamsWebhookUrl, setOpsAlertTeamsWebhookUrl,
      opsAlertSlackEnabled, setOpsAlertSlackEnabled,
      opsAlertSlackWebhookUrl, setOpsAlertSlackWebhookUrl,
      savingOpsAlerts,
      tenants, setTenants, loadingTenants, fetchTenants,
      previewApproved, setPreviewApproved,
      error, setError, successMessage, setSuccessMessage,
      getAccessToken,
      handleSaveAdminConfig, handleResetAdminConfig, handleSaveDiagPaths, handleSaveOpsAlertConfig,
    }}>
      {children}
    </AdminConfigContext.Provider>
  );
}
