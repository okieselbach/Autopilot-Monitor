"use client";

import { useEffect, useState, useMemo, useRef } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { API_BASE_URL } from "@/lib/config";
import UnsavedChangesModal from "../../components/UnsavedChangesModal";
import AutopilotValidationSection from "./components/AutopilotValidationSection";
import AdminManagementSection from "./components/AdminManagementSection";
import HardwareWhitelistSection from "./components/HardwareWhitelistSection";
import AgentSettingsSection from "./components/AgentSettingsSection";
import TeamsNotificationsSection from "./components/TeamsNotificationsSection";
import DiagnosticsSection, { parseSasExpiry } from "./components/DiagnosticsSection";
import DataManagementSection from "./components/DataManagementSection";
import OffboardingSection from "./components/OffboardingSection";

export interface DiagnosticsLogPath {
  path: string;
  description: string;
  isBuiltIn: boolean;
}

export interface TenantConfiguration {
  tenantId: string;
  lastUpdated: string;
  updatedBy: string;
  manufacturerWhitelist: string;
  modelWhitelist: string;
  validateAutopilotDevice: boolean;
  allowInsecureAgentRequests?: boolean;
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  customSettings?: string;
  // Agent collector settings
  enablePerformanceCollector: boolean;
  performanceCollectorIntervalSeconds: number;
  // Agent behavior
  selfDestructOnComplete?: boolean;
  keepLogFile?: boolean;
  rebootOnComplete?: boolean;
  rebootDelaySeconds?: number;
  enableGeoLocation?: boolean;
  enableImeMatchLog?: boolean;
  logLevel?: string;
  // Teams notifications
  teamsWebhookUrl?: string;
  teamsNotifyOnSuccess?: boolean;
  teamsNotifyOnFailure?: boolean;
  // Diagnostics package
  diagnosticsBlobSasUrl?: string;
  diagnosticsUploadMode?: string;
  diagnosticsLogPathsJson?: string;
}

export interface TenantAdmin {
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
  const { addNotification } = useNotifications();
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

  // Unsaved changes guard
  const [showUnsavedModal, setShowUnsavedModal] = useState(false);
  const pendingNavigationRef = useRef<string | null>(null);

  // Form state
  const [manufacturerWhitelist, setManufacturerWhitelist] = useState("Dell*,HP*,Lenovo*,Microsoft Corporation");
  const [modelWhitelist, setModelWhitelist] = useState("*");
  const [validateAutopilotDevice, setValidateAutopilotDevice] = useState(false);
  const [dataRetentionDays, setDataRetentionDays] = useState(90);
  const [sessionTimeoutHours, setSessionTimeoutHours] = useState(5);

  // Collector settings state
  const [enablePerformanceCollector, setEnablePerformanceCollector] = useState(true);
  const [performanceCollectorInterval, setPerformanceCollectorInterval] = useState(30);
  const [autopilotConsentInProgress, setAutopilotConsentInProgress] = useState(false);

  // Agent behavior state
  const [selfDestructOnComplete, setSelfDestructOnComplete] = useState(true);
  const [keepLogFile, setKeepLogFile] = useState(false);
  const [rebootOnComplete, setRebootOnComplete] = useState(false);
  const [rebootDelaySeconds, setRebootDelaySeconds] = useState(10);
  const [enableGeoLocation, setEnableGeoLocation] = useState(true);
  const [enableImeMatchLog, setEnableImeMatchLog] = useState(false);
  const [logLevel, setLogLevel] = useState("Info");

  // Teams notifications state
  const [teamsWebhookUrl, setTeamsWebhookUrl] = useState("");
  const [teamsNotifyOnSuccess, setTeamsNotifyOnSuccess] = useState(true);
  const [teamsNotifyOnFailure, setTeamsNotifyOnFailure] = useState(true);

  // Diagnostics package state
  const [diagnosticsBlobSasUrl, setDiagnosticsBlobSasUrl] = useState("");
  const [diagnosticsUploadMode, setDiagnosticsUploadMode] = useState("Off");
  const [diagnosticsSasExpiry, setDiagnosticsSasExpiry] = useState<Date | null>(null);
  const [tenantDiagPaths, setTenantDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [globalDiagPaths, setGlobalDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [newDiagPath, setNewDiagPath] = useState("");
  const [newDiagDesc, setNewDiagDesc] = useState("");

  // Derived: true when form differs from last-saved config
  const hasUnsavedChanges = useMemo(() => {
    if (!config) return false;
    return (
      manufacturerWhitelist !== config.manufacturerWhitelist ||
      modelWhitelist !== config.modelWhitelist ||
      validateAutopilotDevice !== config.validateAutopilotDevice ||
      dataRetentionDays !== config.dataRetentionDays ||
      sessionTimeoutHours !== config.sessionTimeoutHours ||
      enablePerformanceCollector !== config.enablePerformanceCollector ||
      performanceCollectorInterval !== config.performanceCollectorIntervalSeconds ||
      selfDestructOnComplete !== (config.selfDestructOnComplete ?? true) ||
      keepLogFile !== (config.keepLogFile ?? false) ||
      rebootOnComplete !== (config.rebootOnComplete ?? false) ||
      rebootDelaySeconds !== (config.rebootDelaySeconds ?? 10) ||
      enableGeoLocation !== (config.enableGeoLocation ?? true) ||
      enableImeMatchLog !== (config.enableImeMatchLog ?? false) ||
      logLevel !== (config.logLevel ?? "Info") ||
      teamsWebhookUrl !== (config.teamsWebhookUrl ?? "") ||
      teamsNotifyOnSuccess !== (config.teamsNotifyOnSuccess ?? true) ||
      teamsNotifyOnFailure !== (config.teamsNotifyOnFailure ?? true) ||
      diagnosticsBlobSasUrl !== (config.diagnosticsBlobSasUrl ?? "") ||
      diagnosticsUploadMode !== (config.diagnosticsUploadMode ?? "Off") ||
      JSON.stringify(tenantDiagPaths) !== JSON.stringify(
        config.diagnosticsLogPathsJson ? (() => { try { return JSON.parse(config.diagnosticsLogPathsJson!); } catch { return []; } })() : []
      )
    );
  }, [
    config,
    manufacturerWhitelist, modelWhitelist, validateAutopilotDevice,
    dataRetentionDays, sessionTimeoutHours, enablePerformanceCollector,
    performanceCollectorInterval, selfDestructOnComplete, keepLogFile,
    rebootOnComplete, rebootDelaySeconds, enableGeoLocation, enableImeMatchLog,
    logLevel, teamsWebhookUrl, teamsNotifyOnSuccess, teamsNotifyOnFailure,
    diagnosticsBlobSasUrl, diagnosticsUploadMode, tenantDiagPaths,
  ]);

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
        setValidateAutopilotDevice(data.validateAutopilotDevice);
        setDataRetentionDays(data.dataRetentionDays ?? 90);
        setSessionTimeoutHours(data.sessionTimeoutHours ?? 5);
        setEnablePerformanceCollector(data.enablePerformanceCollector ?? true);
        setPerformanceCollectorInterval(data.performanceCollectorIntervalSeconds ?? 30);
        setSelfDestructOnComplete(data.selfDestructOnComplete ?? true);
        setKeepLogFile(data.keepLogFile ?? false);
        setRebootOnComplete(data.rebootOnComplete ?? false);
        setRebootDelaySeconds(data.rebootDelaySeconds ?? 10);
        setEnableGeoLocation(data.enableGeoLocation ?? true);
        setEnableImeMatchLog(data.enableImeMatchLog ?? false);
        setLogLevel(data.logLevel ?? "Info");
        setTeamsWebhookUrl(data.teamsWebhookUrl ?? "");
        setTeamsNotifyOnSuccess(data.teamsNotifyOnSuccess ?? true);
        setTeamsNotifyOnFailure(data.teamsNotifyOnFailure ?? true);
        const sasUrl = data.diagnosticsBlobSasUrl ?? "";
        setDiagnosticsBlobSasUrl(sasUrl);
        setDiagnosticsUploadMode(data.diagnosticsUploadMode ?? "Off");
        try {
          setTenantDiagPaths(data.diagnosticsLogPathsJson ? JSON.parse(data.diagnosticsLogPathsJson) : []);
        } catch {
          setTenantDiagPaths([]);
        }

        // Parse SAS expiry and fire notification to bell if needed
        if (sasUrl) {
          const expiry = parseSasExpiry(sasUrl);
          setDiagnosticsSasExpiry(expiry);
          if (expiry) {
            const now = new Date();
            const daysRemaining = Math.ceil((expiry.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
            if (daysRemaining <= 0) {
              addNotification(
                'error',
                'Diagnostics SAS URL Expired',
                `The Diagnostics SAS URL expired on ${expiry.toLocaleDateString()}. Diagnostics upload is non-functional.`,
                'diagnostics-sas-expiry',
                '/settings#diagnostics'
              );
            } else if (daysRemaining <= 7) {
              addNotification(
                'warning',
                'Diagnostics SAS URL Expiring Soon',
                `The Diagnostics SAS URL expires on ${expiry.toLocaleDateString()} (${daysRemaining} day${daysRemaining === 1 ? '' : 's'} remaining). Please update it soon.`,
                'diagnostics-sas-expiry',
                '/settings#diagnostics'
              );
            }
          }
        }
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

  // Fetch global diagnostics paths (best-effort, may return 403 for non-galactic-admins)
  useEffect(() => {
    const fetchGlobalDiagPaths = async () => {
      try {
        const token = await getAccessToken();
        if (!token) return;
        const res = await fetch(`${API_BASE_URL}/api/global/config`, {
          headers: { Authorization: `Bearer ${token}` },
        });
        if (!res.ok) return;
        const data = await res.json();
        if (data.diagnosticsGlobalLogPathsJson) {
          setGlobalDiagPaths(JSON.parse(data.diagnosticsGlobalLogPathsJson));
        }
      } catch {
        // Non-fatal: galactic-admin endpoint may be unreachable for regular admins
      }
    };
    fetchGlobalDiagPaths();
  }, []);

  // Intercept <Link> clicks from Navbar and any other anchor navigations
  useEffect(() => {
    const handleLinkClick = (e: MouseEvent) => {
      if (!hasUnsavedChanges) return;
      if (e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) return;
      const target = (e.target as Element).closest('a[href]');
      if (!target) return;
      const href = target.getAttribute('href');
      if (!href || href.startsWith('#') || href.startsWith('http://') || href.startsWith('https://')) return;
      if (href === '/settings' || href.startsWith('/settings?') || href.startsWith('/settings#')) return;
      e.preventDefault();
      e.stopPropagation();
      pendingNavigationRef.current = href;
      setShowUnsavedModal(true);
    };
    document.addEventListener('click', handleLinkClick, true);
    return () => document.removeEventListener('click', handleLinkClick, true);
  }, [hasUnsavedChanges]);

  // Intercept browser back/forward button
  useEffect(() => {
    const handlePopState = () => {
      if (!hasUnsavedChanges) return;
      window.history.pushState(null, '', window.location.href);
      pendingNavigationRef.current = null;
      setShowUnsavedModal(true);
    };
    window.history.pushState(null, '', window.location.href);
    window.addEventListener('popstate', handlePopState);
    return () => window.removeEventListener('popstate', handlePopState);
  }, [hasUnsavedChanges]);

  // Intercept tab close / page refresh
  useEffect(() => {
    const handleBeforeUnload = (e: BeforeUnloadEvent) => {
      if (!hasUnsavedChanges) return;
      e.preventDefault();
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', handleBeforeUnload);
    return () => window.removeEventListener('beforeunload', handleBeforeUnload);
  }, [hasUnsavedChanges]);

  const handleNavigate = (url: string) => {
    if (hasUnsavedChanges) {
      pendingNavigationRef.current = url;
      setShowUnsavedModal(true);
    } else {
      router.push(url);
    }
  };

  const handleSaveAndNavigate = async () => {
    setShowUnsavedModal(false);
    const destination = pendingNavigationRef.current;
    pendingNavigationRef.current = null;
    try {
      await saveConfiguration();
      if (destination) router.push(destination);
      else router.back();
    } catch {
      // saveConfiguration already calls setError; stay on page
    }
  };

  const handleDiscardAndNavigate = () => {
    setShowUnsavedModal(false);
    const destination = pendingNavigationRef.current;
    pendingNavigationRef.current = null;
    if (destination) router.push(destination);
    else router.back();
  };

  const handleCancelNavigation = () => {
    setShowUnsavedModal(false);
    pendingNavigationRef.current = null;
  };

  const saveConfiguration = async (validateAutopilotDeviceOverride?: boolean) => {
    if (!tenantId || !config) return;

    try {
      setSaving(true);
      setError(null);
      setSuccessMessage(null);

      const autopilotDeviceValidationValue = validateAutopilotDeviceOverride ?? validateAutopilotDevice;

      const updatedConfig: TenantConfiguration = {
        ...config,
        manufacturerWhitelist,
        modelWhitelist,
        validateAutopilotDevice: autopilotDeviceValidationValue,
        dataRetentionDays,
        sessionTimeoutHours,
        enablePerformanceCollector,
        performanceCollectorIntervalSeconds: performanceCollectorInterval,
        selfDestructOnComplete,
        keepLogFile,
        rebootOnComplete,
        rebootDelaySeconds,
        enableGeoLocation,
        enableImeMatchLog,
        logLevel,
        teamsWebhookUrl: teamsWebhookUrl || undefined,
        teamsNotifyOnSuccess,
        teamsNotifyOnFailure,
        diagnosticsBlobSasUrl: diagnosticsBlobSasUrl || undefined,
        diagnosticsUploadMode,
        diagnosticsLogPathsJson: tenantDiagPaths.length > 0 ? JSON.stringify(tenantDiagPaths) : undefined,
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
      setValidateAutopilotDevice(result.config.validateAutopilotDevice);
      setSuccessMessage("Configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 3000);
    } finally {
      setSaving(false);
    }
  };

  const beginAutopilotDeviceValidationEnableFlow = async () => {
    if (!tenantId) return;

    try {
      setAutopilotConsentInProgress(true);
      setError(null);
      setSuccessMessage(null);

      const token = await getAccessToken();
      if (!token) {
        throw new Error("Failed to get access token");
      }

      const redirectUri = `${window.location.origin}/settings`;
      const response = await fetch(
        `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-url?redirectUri=${encodeURIComponent(redirectUri)}`,
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

      sessionStorage.setItem("autopilotDeviceValidationPending", "true");
      window.location.href = data.consentUrl;
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to start admin consent flow");
      setAutopilotConsentInProgress(false);
    }
  };

  useEffect(() => {
    const handleConsentCallback = async () => {
      if (!tenantId || !config) return;

      const wasPending = sessionStorage.getItem("autopilotDeviceValidationPending") === "true";
      if (!wasPending) return;

      const queryParams = new URLSearchParams(window.location.search);
      const adminConsent = queryParams.get("admin_consent");
      const consentError = queryParams.get("error");
      const consentErrorDescription = queryParams.get("error_description");

      if (!adminConsent && !consentError) {
        return;
      }

      sessionStorage.removeItem("autopilotDeviceValidationPending");

      if (consentError) {
        const errorText = consentErrorDescription
          ? `${consentError}: ${decodeURIComponent(consentErrorDescription)}`
          : consentError;
        setError(`Admin consent failed: ${errorText}`);
        setAutopilotConsentInProgress(false);
        router.replace("/settings");
        return;
      }

      try {
        setAutopilotConsentInProgress(true);
        const token = await getAccessToken();
        if (!token) {
          throw new Error("Failed to get access token");
        }

        const statusResponse = await fetch(
          `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-status`,
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
        setSuccessMessage("Autopilot Device Validation enabled. Backend agent endpoints are now unlocked for this tenant.");
        router.replace("/settings");
      } catch (err) {
        setError(err instanceof Error ? err.message : "Failed to verify consent");
      } finally {
        setAutopilotConsentInProgress(false);
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
      <div className="min-h-screen bg-gray-50">
        {/* Header */}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <button
                  onClick={() => handleNavigate("/dashboard")}
                  className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
                >
                  &larr; Back to Dashboard
                </button>
                <div>
                  <h1 className="text-3xl font-bold text-gray-900">Tenant Configuration</h1>
                  <p className="text-sm text-gray-600 mt-1">Tenant: {tenantId}</p>
                </div>
              </div>
              {!loading && (
                <div className="flex items-center space-x-3">
                  <button
                    onClick={handleSave}
                    disabled={saving}
                    className="px-4 py-2 bg-indigo-600 text-white rounded-md text-sm hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
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
              )}
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


            <AutopilotValidationSection
              validateAutopilotDevice={validateAutopilotDevice}
              setValidateAutopilotDevice={setValidateAutopilotDevice}
              autopilotConsentInProgress={autopilotConsentInProgress}
              saving={saving}
              onBeginConsent={beginAutopilotDeviceValidationEnableFlow}
            />

            {user?.isTenantAdmin && (
              <AdminManagementSection
                admins={admins}
                loadingAdmins={loadingAdmins}
                newAdminEmail={newAdminEmail}
                setNewAdminEmail={setNewAdminEmail}
                addingAdmin={addingAdmin}
                removingAdmin={removingAdmin}
                togglingAdmin={togglingAdmin}
                adminSearchQuery={adminSearchQuery}
                setAdminSearchQuery={setAdminSearchQuery}
                currentAdminPage={currentAdminPage}
                setCurrentAdminPage={setCurrentAdminPage}
                user={user}
                onAddAdmin={handleAddAdmin}
                onRemoveAdmin={handleRemoveAdmin}
                onToggleAdmin={handleToggleTenantAdmin}
              />
            )}

            <HardwareWhitelistSection
              manufacturerWhitelist={manufacturerWhitelist}
              setManufacturerWhitelist={setManufacturerWhitelist}
              modelWhitelist={modelWhitelist}
              setModelWhitelist={setModelWhitelist}
            />

            <AgentSettingsSection
              enablePerformanceCollector={enablePerformanceCollector}
              setEnablePerformanceCollector={setEnablePerformanceCollector}
              performanceCollectorInterval={performanceCollectorInterval}
              setPerformanceCollectorInterval={setPerformanceCollectorInterval}
              selfDestructOnComplete={selfDestructOnComplete}
              setSelfDestructOnComplete={setSelfDestructOnComplete}
              keepLogFile={keepLogFile}
              setKeepLogFile={setKeepLogFile}
              rebootOnComplete={rebootOnComplete}
              setRebootOnComplete={setRebootOnComplete}
              rebootDelaySeconds={rebootDelaySeconds}
              setRebootDelaySeconds={setRebootDelaySeconds}
              enableGeoLocation={enableGeoLocation}
              setEnableGeoLocation={setEnableGeoLocation}
              enableImeMatchLog={enableImeMatchLog}
              setEnableImeMatchLog={setEnableImeMatchLog}
              logLevel={logLevel}
              setLogLevel={setLogLevel}
            />

            <TeamsNotificationsSection
              teamsWebhookUrl={teamsWebhookUrl}
              setTeamsWebhookUrl={setTeamsWebhookUrl}
              teamsNotifyOnSuccess={teamsNotifyOnSuccess}
              setTeamsNotifyOnSuccess={setTeamsNotifyOnSuccess}
              teamsNotifyOnFailure={teamsNotifyOnFailure}
              setTeamsNotifyOnFailure={setTeamsNotifyOnFailure}
            />

            <DiagnosticsSection
              diagnosticsBlobSasUrl={diagnosticsBlobSasUrl}
              setDiagnosticsBlobSasUrl={setDiagnosticsBlobSasUrl}
              diagnosticsUploadMode={diagnosticsUploadMode}
              setDiagnosticsUploadMode={setDiagnosticsUploadMode}
              diagnosticsSasExpiry={diagnosticsSasExpiry}
              tenantDiagPaths={tenantDiagPaths}
              setTenantDiagPaths={setTenantDiagPaths}
              globalDiagPaths={globalDiagPaths}
              newDiagPath={newDiagPath}
              setNewDiagPath={setNewDiagPath}
              newDiagDesc={newDiagDesc}
              setNewDiagDesc={setNewDiagDesc}
            />

            <DataManagementSection
              dataRetentionDays={dataRetentionDays}
              setDataRetentionDays={setDataRetentionDays}
              sessionTimeoutHours={sessionTimeoutHours}
              setSessionTimeoutHours={setSessionTimeoutHours}
            />

            {user?.isTenantAdmin && (
              <OffboardingSection
                showOffboardDialog={showOffboardDialog}
                setShowOffboardDialog={setShowOffboardDialog}
                offboardConfirmText={offboardConfirmText}
                setOffboardConfirmText={setOffboardConfirmText}
                offboarding={offboarding}
                offboardError={offboardError}
                setOffboardError={setOffboardError}
                onOffboard={handleOffboard}
              />
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

          </div>
        )}
      </main>
      </div>

      <UnsavedChangesModal
        isOpen={showUnsavedModal}
        isSaving={saving}
        onSaveAndNavigate={handleSaveAndNavigate}
        onDiscardAndNavigate={handleDiscardAndNavigate}
        onCancel={handleCancelNavigation}
      />
    </ProtectedRoute>
  );
}
