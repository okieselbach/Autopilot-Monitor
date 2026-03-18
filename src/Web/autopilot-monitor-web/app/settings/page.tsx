"use client";

import { useEffect, useState, useMemo, useRef } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import UnsavedChangesModal from "../../components/UnsavedChangesModal";
import { usePageSections } from "../../hooks/usePageSections";
import { PageSectionItem } from "../../contexts/SidebarContext";
import { ShieldCheckIcon, UsersIcon, CpuChipIcon, GearIcon, MagnifyingGlassIcon, LockClosedIcon, BellIcon, CloudArrowUpIcon, KeyIcon, CircleStackIcon, ArrowRightOnRectangleIcon } from "../../lib/sidebarIcons";
import AutopilotValidationSection from "./components/AutopilotValidationSection";
import AdminManagementSection from "./components/AdminManagementSection";
import HardwareWhitelistSection from "./components/HardwareWhitelistSection";
import AgentSettingsSection from "./components/AgentSettingsSection";
import AgentAnalyzersSection from "./components/AgentAnalyzersSection";
import NotificationsSection from "./components/NotificationsSection";
import DiagnosticsSection, { parseSasExpiry } from "./components/DiagnosticsSection";
import DataManagementSection from "./components/DataManagementSection";
import OffboardingSection from "./components/OffboardingSection";
import BootstrapSessionsSection, { BootstrapSessionItem } from "./components/BootstrapSessionsSection";
import UnrestrictedModeSection from "./components/UnrestrictedModeSection";
import { TenantConfiguration, TenantAdmin, DiagnosticsLogPath } from "./types";

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
  const [newMemberRole, setNewMemberRole] = useState<string>("Admin");
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

  // Bootstrap sessions
  const [bootstrapSessions, setBootstrapSessions] = useState<BootstrapSessionItem[]>([]);
  const [bootstrapLoading, setBootstrapLoading] = useState(false);

  // Unsaved changes guard
  const [showUnsavedModal, setShowUnsavedModal] = useState(false);
  const pendingNavigationRef = useRef<string | null>(null);

  // Form state
  const [manufacturerWhitelist, setManufacturerWhitelist] = useState("Dell*,HP*,Lenovo*,Microsoft Corporation");
  const [modelWhitelist, setModelWhitelist] = useState("*");
  const [validateAutopilotDevice, setValidateAutopilotDevice] = useState(false);
  const [validateCorporateIdentifier, setValidateCorporateIdentifier] = useState(false);
  const [dataRetentionDays, setDataRetentionDays] = useState(90);
  const [sessionTimeoutHours, setSessionTimeoutHours] = useState(5);

  // Collector settings state
  const [enablePerformanceCollector, setEnablePerformanceCollector] = useState(true);
  const [performanceCollectorInterval, setPerformanceCollectorInterval] = useState(30);
  const [helloWaitTimeoutSeconds, setHelloWaitTimeoutSeconds] = useState(30);
  const [autopilotConsentInProgress, setAutopilotConsentInProgress] = useState(false);

  // Agent behavior state
  const [selfDestructOnComplete, setSelfDestructOnComplete] = useState(true);
  const [keepLogFile, setKeepLogFile] = useState(false);
  const [rebootOnComplete, setRebootOnComplete] = useState(false);
  const [rebootDelaySeconds, setRebootDelaySeconds] = useState(10);
  const [enableGeoLocation, setEnableGeoLocation] = useState(true);
  const [enableImeMatchLog, setEnableImeMatchLog] = useState(false);
  const [logLevel, setLogLevel] = useState("Info");
  const [showScriptOutput, setShowScriptOutput] = useState(true);
  const [showEnrollmentSummary, setShowEnrollmentSummary] = useState(false);
  const [enrollmentSummaryTimeoutSeconds, setEnrollmentSummaryTimeoutSeconds] = useState(60);
  const [enrollmentSummaryBrandingImageUrl, setEnrollmentSummaryBrandingImageUrl] = useState("");
  const [enrollmentSummaryLaunchRetrySeconds, setEnrollmentSummaryLaunchRetrySeconds] = useState(120);

  // Webhook notifications state
  const [webhookProviderType, setWebhookProviderType] = useState(0);
  const [webhookUrl, setWebhookUrl] = useState("");
  const [webhookNotifyOnSuccess, setWebhookNotifyOnSuccess] = useState(true);
  const [webhookNotifyOnFailure, setWebhookNotifyOnFailure] = useState(true);
  const [testingWebhook, setTestingWebhook] = useState(false);
  const [testWebhookResult, setTestWebhookResult] = useState<{ success: boolean; message: string } | null>(null);

  // Diagnostics package state
  const [diagnosticsBlobSasUrl, setDiagnosticsBlobSasUrl] = useState("");
  const [diagnosticsUploadMode, setDiagnosticsUploadMode] = useState("Off");

  const [tenantDiagPaths, setTenantDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [globalDiagPaths, setGlobalDiagPaths] = useState<DiagnosticsLogPath[]>([]);
  const [newDiagPath, setNewDiagPath] = useState("");
  const [newDiagDesc, setNewDiagDesc] = useState("");

  // Agent analyzer settings state
  const [enableLocalAdminAnalyzer, setEnableLocalAdminAnalyzer] = useState(true);
  const [localAdminAllowedAccounts, setLocalAdminAllowedAccounts] = useState<string[]>([]);
  const [newAllowedAccount, setNewAllowedAccount] = useState("");

  // Unrestricted mode state
  const [unrestrictedMode, setUnrestrictedMode] = useState(false);

  // Derived: true when form differs from last-saved config
  const hasUnsavedChanges = useMemo(() => {
    if (!config) return false;
    return (
      manufacturerWhitelist !== config.manufacturerWhitelist ||
      modelWhitelist !== config.modelWhitelist ||
      validateAutopilotDevice !== config.validateAutopilotDevice ||
      validateCorporateIdentifier !== (config.validateCorporateIdentifier ?? false) ||
      dataRetentionDays !== config.dataRetentionDays ||
      sessionTimeoutHours !== config.sessionTimeoutHours ||
      enablePerformanceCollector !== config.enablePerformanceCollector ||
      performanceCollectorInterval !== config.performanceCollectorIntervalSeconds ||
      helloWaitTimeoutSeconds !== (config.helloWaitTimeoutSeconds ?? 30) ||
      selfDestructOnComplete !== (config.selfDestructOnComplete ?? true) ||
      keepLogFile !== (config.keepLogFile ?? false) ||
      rebootOnComplete !== (config.rebootOnComplete ?? false) ||
      rebootDelaySeconds !== (config.rebootDelaySeconds ?? 10) ||
      enableGeoLocation !== (config.enableGeoLocation ?? true) ||
      enableImeMatchLog !== (config.enableImeMatchLog ?? false) ||
      logLevel !== (config.logLevel ?? "Info") ||
      showScriptOutput !== (config.showScriptOutput ?? true) ||
      showEnrollmentSummary !== (config.showEnrollmentSummary ?? false) ||
      enrollmentSummaryTimeoutSeconds !== (config.enrollmentSummaryTimeoutSeconds ?? 60) ||
      enrollmentSummaryBrandingImageUrl !== (config.enrollmentSummaryBrandingImageUrl ?? "") ||
      enrollmentSummaryLaunchRetrySeconds !== (config.enrollmentSummaryLaunchRetrySeconds ?? 120) ||
      webhookProviderType !== (config.webhookProviderType ?? (config.teamsWebhookUrl ? 1 : 0)) ||
      webhookUrl !== (config.webhookUrl ?? config.teamsWebhookUrl ?? "") ||
      webhookNotifyOnSuccess !== (config.webhookNotifyOnSuccess ?? config.teamsNotifyOnSuccess ?? true) ||
      webhookNotifyOnFailure !== (config.webhookNotifyOnFailure ?? config.teamsNotifyOnFailure ?? true) ||
      diagnosticsBlobSasUrl !== (config.diagnosticsBlobSasUrl ?? "") ||
      diagnosticsUploadMode !== (config.diagnosticsUploadMode ?? "Off") ||
      JSON.stringify(tenantDiagPaths) !== JSON.stringify(
        config.diagnosticsLogPathsJson ? (() => { try { return JSON.parse(config.diagnosticsLogPathsJson!); } catch { return []; } })() : []
      ) ||
      enableLocalAdminAnalyzer !== (config.enableLocalAdminAnalyzer ?? true) ||
      JSON.stringify(localAdminAllowedAccounts) !== JSON.stringify(
        config.localAdminAllowedAccountsJson ? (() => { try { return JSON.parse(config.localAdminAllowedAccountsJson!); } catch { return []; } })() : []
      ) ||
      unrestrictedMode !== (config.unrestrictedMode ?? false)
    );
  }, [
    config,
    manufacturerWhitelist, modelWhitelist, validateAutopilotDevice, validateCorporateIdentifier,
    dataRetentionDays, sessionTimeoutHours, enablePerformanceCollector,
    performanceCollectorInterval, helloWaitTimeoutSeconds, selfDestructOnComplete, keepLogFile,
    rebootOnComplete, rebootDelaySeconds, enableGeoLocation, enableImeMatchLog,
    logLevel, showScriptOutput, showEnrollmentSummary, enrollmentSummaryTimeoutSeconds,
    enrollmentSummaryBrandingImageUrl, enrollmentSummaryLaunchRetrySeconds,
    webhookProviderType, webhookUrl, webhookNotifyOnSuccess, webhookNotifyOnFailure,
    diagnosticsBlobSasUrl, diagnosticsUploadMode, tenantDiagPaths,
    enableLocalAdminAnalyzer, localAdminAllowedAccounts, unrestrictedMode,
  ]);

  // Fetch configuration
  useEffect(() => {
    if (!tenantId) return;

    const fetchConfiguration = async () => {
      try {
        setLoading(true);
        setError(null);

        
        const response = await authenticatedFetch(`${API_BASE_URL}/api/config/${tenantId}`, getAccessToken);

        if (!response.ok) {
          throw new Error(`Failed to load configuration: ${response.statusText}`);
        }

        const data: TenantConfiguration = await response.json();
        setConfig(data);

        // Update form state
        setManufacturerWhitelist(data.manufacturerWhitelist);
        setModelWhitelist(data.modelWhitelist);
        setValidateAutopilotDevice(data.validateAutopilotDevice);
        setValidateCorporateIdentifier(data.validateCorporateIdentifier ?? false);
        setDataRetentionDays(data.dataRetentionDays ?? 90);
        setSessionTimeoutHours(data.sessionTimeoutHours ?? 5);
        setEnablePerformanceCollector(data.enablePerformanceCollector ?? true);
        setPerformanceCollectorInterval(data.performanceCollectorIntervalSeconds ?? 30);
        setHelloWaitTimeoutSeconds(data.helloWaitTimeoutSeconds ?? 30);
        setSelfDestructOnComplete(data.selfDestructOnComplete ?? true);
        setKeepLogFile(data.keepLogFile ?? false);
        setRebootOnComplete(data.rebootOnComplete ?? false);
        setRebootDelaySeconds(data.rebootDelaySeconds ?? 10);
        setEnableGeoLocation(data.enableGeoLocation ?? true);
        setEnableImeMatchLog(data.enableImeMatchLog ?? false);
        setLogLevel(data.logLevel ?? "Info");
        setShowScriptOutput(data.showScriptOutput ?? true);
        setShowEnrollmentSummary(data.showEnrollmentSummary ?? false);
        setEnrollmentSummaryTimeoutSeconds(data.enrollmentSummaryTimeoutSeconds ?? 60);
        setEnrollmentSummaryBrandingImageUrl(data.enrollmentSummaryBrandingImageUrl ?? "");
        setEnrollmentSummaryLaunchRetrySeconds(data.enrollmentSummaryLaunchRetrySeconds ?? 120);
        // Webhook notifications: auto-migrate from legacy fields
        if (data.webhookUrl && data.webhookProviderType) {
          setWebhookProviderType(data.webhookProviderType);
          setWebhookUrl(data.webhookUrl);
          setWebhookNotifyOnSuccess(data.webhookNotifyOnSuccess ?? true);
          setWebhookNotifyOnFailure(data.webhookNotifyOnFailure ?? true);
        } else if (data.teamsWebhookUrl) {
          setWebhookProviderType(1); // TeamsLegacyConnector
          setWebhookUrl(data.teamsWebhookUrl);
          setWebhookNotifyOnSuccess(data.teamsNotifyOnSuccess ?? true);
          setWebhookNotifyOnFailure(data.teamsNotifyOnFailure ?? true);
        } else {
          setWebhookProviderType(0);
          setWebhookUrl("");
          setWebhookNotifyOnSuccess(true);
          setWebhookNotifyOnFailure(true);
        }
        const sasUrl = data.diagnosticsBlobSasUrl ?? "";
        setDiagnosticsBlobSasUrl(sasUrl);
        setDiagnosticsUploadMode(data.diagnosticsUploadMode ?? "Off");
        try {
          setTenantDiagPaths(data.diagnosticsLogPathsJson ? JSON.parse(data.diagnosticsLogPathsJson) : []);
        } catch {
          setTenantDiagPaths([]);
        }
        setEnableLocalAdminAnalyzer(data.enableLocalAdminAnalyzer ?? true);
        try {
          setLocalAdminAllowedAccounts(data.localAdminAllowedAccountsJson ? JSON.parse(data.localAdminAllowedAccountsJson) : []);
        } catch {
          setLocalAdminAllowedAccounts([]);
        }
        setUnrestrictedMode(data.unrestrictedMode ?? false);

        // Parse SAS expiry and fire notification to bell if needed
        if (sasUrl) {
          const expiry = parseSasExpiry(sasUrl);
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
        if (err instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', err.message, 'session-expired-error');
        } else {
          console.error("Error fetching configuration:", err);
          setError(err instanceof Error ? err.message : "Failed to load configuration");
        }
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

      const response = await authenticatedFetch(`${API_BASE_URL}/api/tenants/${tenantId}/admins`, getAccessToken);

      if (!response.ok) {
        throw new Error(`Failed to load admins: ${response.statusText}`);
      }

      const data: TenantAdmin[] = await response.json();
      setAdmins(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error fetching admins:", err);
        setError(err instanceof Error ? err.message : "Failed to load admins");
      }
    } finally {
      setLoadingAdmins(false);
    }
  };

  useEffect(() => {
    if (!tenantId) return;
    if (!user?.isTenantAdmin && !user?.isGalacticAdmin) return;
    fetchAdmins();
  }, [tenantId, user?.isTenantAdmin, user?.isGalacticAdmin]);

  // Fetch bootstrap sessions (only when feature is enabled)
  useEffect(() => {
    if (!tenantId || !config?.bootstrapTokenEnabled) return;
    fetchBootstrapSessions();
  }, [tenantId, config?.bootstrapTokenEnabled]);

  // Fetch global diagnostics paths (galactic-admin only)
  useEffect(() => {
    if (!user?.isGalacticAdmin) return;
    const fetchGlobalDiagPaths = async () => {
      try {
        const res = await authenticatedFetch(`${API_BASE_URL}/api/global/config`, getAccessToken);
        if (!res.ok) return;
        const data = await res.json();
        if (data.diagnosticsGlobalLogPathsJson) {
          setGlobalDiagPaths(JSON.parse(data.diagnosticsGlobalLogPathsJson));
        }
      } catch {
        // Non-fatal
      }
    };
    fetchGlobalDiagPaths();
  }, [user?.isGalacticAdmin]);

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

  const handleTestWebhook = async () => {
    if (!tenantId) return;
    setTestingWebhook(true);
    setTestWebhookResult(null);
    try {
      const response = await authenticatedFetch(`${API_BASE_URL}/api/config/${tenantId}/test-notification`, getAccessToken, {
        method: "POST",
      });
      const data = await response.json();
      setTestWebhookResult({ success: data.success, message: data.message });
    } catch (err) {
      setTestWebhookResult({ success: false, message: err instanceof Error ? err.message : "Failed to send test notification." });
    } finally {
      setTestingWebhook(false);
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

  const saveConfiguration = async (validateAutopilotDeviceOverride?: boolean, validateCorporateIdentifierOverride?: boolean) => {
    if (!tenantId || !config) return;

    try {
      setSaving(true);
      setError(null);
      setSuccessMessage(null);

      const autopilotDeviceValidationValue = validateAutopilotDeviceOverride ?? validateAutopilotDevice;
      const corporateIdentifierValidationValue = validateCorporateIdentifierOverride ?? validateCorporateIdentifier;

      const updatedConfig: TenantConfiguration = {
        ...config,
        manufacturerWhitelist,
        modelWhitelist,
        validateAutopilotDevice: autopilotDeviceValidationValue,
        validateCorporateIdentifier: corporateIdentifierValidationValue,
        dataRetentionDays,
        sessionTimeoutHours,
        enablePerformanceCollector,
        performanceCollectorIntervalSeconds: performanceCollectorInterval,
        helloWaitTimeoutSeconds,
        selfDestructOnComplete,
        keepLogFile,
        rebootOnComplete,
        rebootDelaySeconds,
        enableGeoLocation,
        enableImeMatchLog,
        logLevel,
        showScriptOutput,
        showEnrollmentSummary,
        enrollmentSummaryTimeoutSeconds,
        enrollmentSummaryBrandingImageUrl: enrollmentSummaryBrandingImageUrl || undefined,
        enrollmentSummaryLaunchRetrySeconds,
        // New webhook fields
        webhookProviderType,
        webhookUrl: webhookUrl || undefined,
        webhookNotifyOnSuccess,
        webhookNotifyOnFailure,
        // Legacy compat: mirror to old fields during transition
        teamsWebhookUrl: webhookProviderType === 1 ? (webhookUrl || undefined) : undefined,
        teamsNotifyOnSuccess: webhookNotifyOnSuccess,
        teamsNotifyOnFailure: webhookNotifyOnFailure,
        diagnosticsBlobSasUrl: diagnosticsBlobSasUrl || undefined,
        diagnosticsUploadMode,
        diagnosticsLogPathsJson: tenantDiagPaths.length > 0 ? JSON.stringify(tenantDiagPaths) : undefined,
        enableLocalAdminAnalyzer,
        localAdminAllowedAccountsJson: localAdminAllowedAccounts.length > 0 ? JSON.stringify(localAdminAllowedAccounts) : undefined,
        unrestrictedMode,
      };

      const response = await authenticatedFetch(`${API_BASE_URL}/api/config/${tenantId}`, getAccessToken, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.message || errorData.error || `Failed to save configuration: ${response.statusText}`);
      }

      const result = await response.json();
      setConfig(result.config);
      // Sync form state variables with the server response to keep UI consistent
      setValidateAutopilotDevice(result.config.validateAutopilotDevice);
      setValidateCorporateIdentifier(result.config.validateCorporateIdentifier ?? false);
      setUnrestrictedMode(result.config.unrestrictedMode ?? false);
      setSuccessMessage("Configuration saved successfully!");
      setTimeout(() => setSuccessMessage(null), 3000);
    } finally {
      setSaving(false);
    }
  };

  const beginDeviceValidationConsentFlow = async (trigger: 'autopilot' | 'corporate') => {
    if (!tenantId) return;

    try {
      setAutopilotConsentInProgress(true);
      setError(null);
      setSuccessMessage(null);

      const redirectUri = `${window.location.origin}/settings`;
      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-url?redirectUri=${encodeURIComponent(redirectUri)}`,
        getAccessToken,
      );

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        throw new Error(errorData.error || `Failed to start consent flow: ${response.statusText}`);
      }

      const data = await response.json();
      if (!data.consentUrl) {
        throw new Error("Backend did not return a consent URL.");
      }

      sessionStorage.setItem("deviceValidationConsentPending", "true");
      sessionStorage.setItem("consentTrigger", trigger);
      window.location.href = data.consentUrl;
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        setError(err instanceof Error ? err.message : "Failed to start admin consent flow");
      }
      setAutopilotConsentInProgress(false);
    }
  };

  useEffect(() => {
    const handleConsentCallback = async () => {
      if (!tenantId || !config) return;

      // Support both old and new sessionStorage keys for backward compatibility
      const wasPendingNew = sessionStorage.getItem("deviceValidationConsentPending") === "true";
      const wasPendingOld = sessionStorage.getItem("autopilotDeviceValidationPending") === "true";
      if (!wasPendingNew && !wasPendingOld) return;

      const queryParams = new URLSearchParams(window.location.search);
      const adminConsent = queryParams.get("admin_consent");
      const consentError = queryParams.get("error");
      const consentErrorDescription = queryParams.get("error_description");

      if (!adminConsent && !consentError) {
        return;
      }

      const trigger = sessionStorage.getItem("consentTrigger") ?? "autopilot";
      sessionStorage.removeItem("deviceValidationConsentPending");
      sessionStorage.removeItem("autopilotDeviceValidationPending");
      sessionStorage.removeItem("consentTrigger");

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

        const statusResponse = await authenticatedFetch(
          `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-status`,
          getAccessToken,
        );

        if (!statusResponse.ok) {
          const errorData = await statusResponse.json().catch(() => ({}));
          throw new Error(errorData.error || `Consent validation failed: ${statusResponse.statusText}`);
        }

        const statusData = await statusResponse.json();
        if (!statusData.isConsented) {
          throw new Error(statusData.message || "Consent is not active yet for this tenant.");
        }

        if (trigger === "corporate") {
          await saveConfiguration(undefined, true);
          setSuccessMessage("Corporate Identifier Validation enabled. Backend agent endpoints are now unlocked for this tenant.");
        } else {
          await saveConfiguration(true);
          setSuccessMessage("Autopilot Device Validation enabled. Backend agent endpoints are now unlocked for this tenant.");
        }
        router.replace("/settings");
      } catch (err) {
        if (err instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', err.message, 'session-expired-error');
        } else {
          setError(err instanceof Error ? err.message : "Failed to verify consent");
        }
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
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error saving configuration:", err);
        setError(err instanceof Error ? err.message : "Failed to save configuration");
      }
    }
  };


  const handleAddAdmin = async () => {
    if (!tenantId || !newAdminEmail.trim()) return;

    try {
      setAddingAdmin(true);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(`${API_BASE_URL}/api/tenants/${tenantId}/admins`, getAccessToken, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ upn: newAdminEmail.trim(), role: newMemberRole }),
      });

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to add member: ${response.statusText}`);
      }

      setSuccessMessage(`${newMemberRole} ${newAdminEmail} added successfully!`);
      setNewAdminEmail("");
      setNewMemberRole("Admin");

      // Refresh admin list
      await fetchAdmins();

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error adding admin:", err);
        setError(err instanceof Error ? err.message : "Failed to add admin");
      }
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

      const response = await authenticatedFetch(`${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}`, getAccessToken, {
        method: "DELETE",
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
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error removing admin:", err);
        setError(err instanceof Error ? err.message : "Failed to remove admin");
      }
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

      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}/${action}`,
        getAccessToken,
        {
          method: "PATCH",
        },
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
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error(`Error ${action}ing admin:`, err);
        setError(err instanceof Error ? err.message : `Failed to ${action} admin`);
      }
    } finally {
      setTogglingAdmin(null);
    }
  };

  const handleUpdatePermissions = async (adminUpn: string, role: string, canManageBootstrapTokens: boolean) => {
    if (!tenantId) return;

    try {
      setTogglingAdmin(adminUpn);
      setError(null);
      setSuccessMessage(null);

      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}/permissions`,
        getAccessToken,
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ role, canManageBootstrapTokens }),
        },
      );

      if (!response.ok) {
        const errorData = await response.json();
        throw new Error(errorData.error || `Failed to update permissions: ${response.statusText}`);
      }

      setSuccessMessage(`Permissions for ${adminUpn} updated successfully!`);
      await fetchAdmins();
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error("Error updating permissions:", err);
        setError(err instanceof Error ? err.message : "Failed to update permissions");
      }
    } finally {
      setTogglingAdmin(null);
    }
  };

  // Bootstrap sessions
  const fetchBootstrapSessions = async () => {
    if (!tenantId) return;
    try {
      setBootstrapLoading(true);
      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/bootstrap/sessions?tenantId=${tenantId}`,
        getAccessToken,
      );
      if (response.ok) {
        const data = await response.json();
        setBootstrapSessions(data.sessions || []);
      }
    } catch (err) {
      console.error("Failed to fetch bootstrap sessions:", err);
    } finally {
      setBootstrapLoading(false);
    }
  };

  const createBootstrapSession = async (validityHours: number, label: string): Promise<string | null> => {
    if (!tenantId) return null;
    try {
      const response = await authenticatedFetch(`${API_BASE_URL}/api/bootstrap/sessions`, getAccessToken, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ tenantId, validityHours, label }),
      });
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error((data as Record<string, string>).error || "Failed to create session");
      }
      const data = await response.json();
      await fetchBootstrapSessions();
      return data.bootstrapUrl || null;
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        setError(err instanceof Error ? err.message : "Failed to create bootstrap session");
      }
      return null;
    }
  };

  const revokeBootstrapSession = async (code: string) => {
    if (!tenantId) return;
    try {
      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/bootstrap/sessions/${code}?tenantId=${tenantId}`,
        getAccessToken,
        { method: "DELETE" },
      );
      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error((data as Record<string, string>).error || "Failed to revoke session");
      }
      await fetchBootstrapSessions();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        setError(err instanceof Error ? err.message : "Failed to revoke bootstrap session");
      }
    }
  };

  const handleOffboard = async () => {
    if (!tenantId) return;

    try {
      setOffboarding(true);
      setOffboardError(null);

      const response = await authenticatedFetch(`${API_BASE_URL}/api/tenants/${tenantId}/offboard`, getAccessToken, {
        method: 'DELETE',
      });

      if (!response.ok) {
        const data = await response.json().catch(() => ({}));
        throw new Error(data?.error || `Offboard failed: ${response.statusText}`);
      }

      // Offboard successful – sign out the user as their admin access is gone
      logout();
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        setOffboardError(err instanceof Error ? err.message : 'Offboard failed');
      }
      setOffboarding(false);
    }
  };

  // Build sidebar sections based on user role
  const settingsSections: PageSectionItem[] = useMemo(() => {
    const sections: PageSectionItem[] = [];
    if (user?.isTenantAdmin) {
      sections.push(
        { id: "autopilot-validation", label: "Autopilot Validation", icon: <ShieldCheckIcon /> },
        { id: "admin-management", label: "Admin Management", icon: <UsersIcon /> },
        { id: "hardware-whitelist", label: "Hardware Whitelist", icon: <CpuChipIcon /> },
        { id: "agent-settings", label: "Agent Settings", icon: <GearIcon /> },
        { id: "agent-analyzers", label: "Agent Analyzers", icon: <MagnifyingGlassIcon /> },
        { id: "unrestricted-mode", label: "Unrestricted Mode", icon: <LockClosedIcon /> },
        { id: "notifications", label: "Notifications", icon: <BellIcon /> },
        { id: "diagnostics", label: "Diagnostics", icon: <CloudArrowUpIcon /> },
      );
    }
    if (config?.bootstrapTokenEnabled && (user?.isTenantAdmin || user?.canManageBootstrapTokens)) {
      sections.push({ id: "bootstrap-sessions", label: "Bootstrap Sessions", icon: <KeyIcon /> });
    }
    if (user?.isTenantAdmin) {
      sections.push(
        { id: "data-management", label: "Data Management", icon: <CircleStackIcon /> },
        { id: "offboarding", label: "Offboarding", icon: <ArrowRightOnRectangleIcon /> },
      );
    }
    return sections;
  }, [user, config?.bootstrapTokenEnabled]);

  usePageSections(settingsSections, "Settings", "scroll-spy");

  // Access gate: Admin → full settings, Operator with bootstrap → bootstrap only, others → redirect
  if (user && !user.isTenantAdmin && !user.isGalacticAdmin) {
    if (user.role === 'Operator' && user.canManageBootstrapTokens) {
      // Operator with bootstrap permission — allowed, will see only bootstrap section
    } else if (user.role === 'Operator') {
      // Operator without bootstrap permission — no settings access
      router.replace('/dashboard');
      return null;
    } else {
      // Regular user — redirect to progress portal
      router.replace('/progress');
      return null;
    }
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {/* Header */}
        <header className="bg-white shadow">
          <div className="py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <div>
                  <h1 className="text-3xl font-bold text-gray-900">Tenant Configuration</h1>
                  <p className="text-sm text-gray-600 mt-1">Tenant: {tenantId}</p>
                </div>
              </div>
              {!loading && user?.isTenantAdmin && (
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
      <main className="px-4 sm:px-6 lg:px-8 py-8">
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


            {/* Admin-only sections — hidden for Operators */}
            {user?.isTenantAdmin && (
              <div id="autopilot-validation">
              <AutopilotValidationSection
                validateAutopilotDevice={validateAutopilotDevice}
                setValidateAutopilotDevice={setValidateAutopilotDevice}
                validateCorporateIdentifier={validateCorporateIdentifier}
                setValidateCorporateIdentifier={setValidateCorporateIdentifier}
                autopilotConsentInProgress={autopilotConsentInProgress}
                saving={saving}
                onBeginConsent={beginDeviceValidationConsentFlow}
              />
              </div>
            )}

            {user?.isTenantAdmin && (
              <div id="admin-management">
              <AdminManagementSection
                admins={admins}
                loadingAdmins={loadingAdmins}
                newAdminEmail={newAdminEmail}
                setNewAdminEmail={setNewAdminEmail}
                newMemberRole={newMemberRole}
                setNewMemberRole={setNewMemberRole}
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
                onUpdatePermissions={handleUpdatePermissions}
              />
              </div>
            )}

            {user?.isTenantAdmin && (
              <div id="hardware-whitelist">
              <HardwareWhitelistSection
                manufacturerWhitelist={manufacturerWhitelist}
                setManufacturerWhitelist={setManufacturerWhitelist}
                modelWhitelist={modelWhitelist}
                setModelWhitelist={setModelWhitelist}
              />
              </div>
            )}

            {user?.isTenantAdmin && (
            <div id="agent-settings" className="space-y-6">
            <AgentSettingsSection
              enablePerformanceCollector={enablePerformanceCollector}
              setEnablePerformanceCollector={setEnablePerformanceCollector}
              performanceCollectorInterval={performanceCollectorInterval}
              setPerformanceCollectorInterval={setPerformanceCollectorInterval}
              helloWaitTimeoutSeconds={helloWaitTimeoutSeconds}
              setHelloWaitTimeoutSeconds={setHelloWaitTimeoutSeconds}
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
              showScriptOutput={showScriptOutput}
              setShowScriptOutput={setShowScriptOutput}
              showEnrollmentSummary={showEnrollmentSummary}
              setShowEnrollmentSummary={setShowEnrollmentSummary}
              enrollmentSummaryTimeoutSeconds={enrollmentSummaryTimeoutSeconds}
              setEnrollmentSummaryTimeoutSeconds={setEnrollmentSummaryTimeoutSeconds}
              enrollmentSummaryBrandingImageUrl={enrollmentSummaryBrandingImageUrl}
              setEnrollmentSummaryBrandingImageUrl={setEnrollmentSummaryBrandingImageUrl}
              enrollmentSummaryLaunchRetrySeconds={enrollmentSummaryLaunchRetrySeconds}
              setEnrollmentSummaryLaunchRetrySeconds={setEnrollmentSummaryLaunchRetrySeconds}
            />
            </div>
            )}

            {user?.isTenantAdmin && (
            <div id="agent-analyzers">
            <AgentAnalyzersSection
              enableLocalAdminAnalyzer={enableLocalAdminAnalyzer}
              setEnableLocalAdminAnalyzer={setEnableLocalAdminAnalyzer}
              localAdminAllowedAccounts={localAdminAllowedAccounts}
              setLocalAdminAllowedAccounts={setLocalAdminAllowedAccounts}
              newAllowedAccount={newAllowedAccount}
              setNewAllowedAccount={setNewAllowedAccount}
            />
            </div>
            )}

            {user?.isTenantAdmin && (
            <div id="unrestricted-mode">
            <UnrestrictedModeSection
              unrestrictedMode={unrestrictedMode}
              setUnrestrictedMode={setUnrestrictedMode}
            />
            </div>
            )}

            {user?.isTenantAdmin && (
            <div id="notifications">
            <NotificationsSection
              webhookProviderType={webhookProviderType}
              setWebhookProviderType={setWebhookProviderType}
              webhookUrl={webhookUrl}
              setWebhookUrl={setWebhookUrl}
              webhookNotifyOnSuccess={webhookNotifyOnSuccess}
              setWebhookNotifyOnSuccess={setWebhookNotifyOnSuccess}
              webhookNotifyOnFailure={webhookNotifyOnFailure}
              setWebhookNotifyOnFailure={setWebhookNotifyOnFailure}
              onTestWebhook={handleTestWebhook}
              testingWebhook={testingWebhook}
              testWebhookResult={testWebhookResult}
            />
            </div>
            )}

            {user?.isTenantAdmin && (
            <div id="diagnostics">
            <DiagnosticsSection
              diagnosticsBlobSasUrl={diagnosticsBlobSasUrl}
              setDiagnosticsBlobSasUrl={setDiagnosticsBlobSasUrl}
              diagnosticsUploadMode={diagnosticsUploadMode}
              setDiagnosticsUploadMode={setDiagnosticsUploadMode}

              tenantDiagPaths={tenantDiagPaths}
              setTenantDiagPaths={setTenantDiagPaths}
              globalDiagPaths={globalDiagPaths}
              newDiagPath={newDiagPath}
              setNewDiagPath={setNewDiagPath}
              newDiagDesc={newDiagDesc}
              setNewDiagDesc={setNewDiagDesc}
              unrestrictedMode={unrestrictedMode}
            />
            </div>
            )}

            {/* Bootstrap section: visible to Admins always, Operators with bootstrap permission */}
            {config?.bootstrapTokenEnabled && (user?.isTenantAdmin || user?.canManageBootstrapTokens) && (
              <div id="bootstrap-sessions">
              <BootstrapSessionsSection
                sessions={bootstrapSessions}
                loading={bootstrapLoading}
                onRefresh={fetchBootstrapSessions}
                onRevoke={revokeBootstrapSession}
                onCreate={createBootstrapSession}
              />
              </div>
            )}

            {user?.isTenantAdmin && (
            <div id="data-management">
            <DataManagementSection
              dataRetentionDays={dataRetentionDays}
              setDataRetentionDays={setDataRetentionDays}
              sessionTimeoutHours={sessionTimeoutHours}
              setSessionTimeoutHours={setSessionTimeoutHours}
              isGalacticAdmin={user?.isGalacticAdmin}
            />
            </div>
            )}

            {user?.isTenantAdmin && (
              <div id="offboarding">
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
              </div>
            )}

            {/* Configuration Info — Admin only */}
            {config && user?.isTenantAdmin && (
              <div id="config-info" className="bg-blue-50 border border-blue-200 rounded-lg p-4">
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
