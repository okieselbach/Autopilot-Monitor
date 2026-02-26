"use client";

import { useCallback, useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAuth } from "../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";

interface SessionExportEvent {
  eventId: string;
  sessionId: string;
  tenantId: string;
  timestamp: string;
  eventType: string;
  severity: string;
  source: string;
  phase: number;
  phaseName?: string;
  message: string;
  sequence: number;
  data?: Record<string, unknown>;
}

const EXPORT_V1_PHASE_NAMES: Record<number, string> = {
  0: "Start", 1: "Device Preparation", 2: "Device Setup",
  3: "Apps (Device)", 4: "Account Setup", 5: "Apps (User)",
  6: "Finalizing Setup", 7: "Complete", 99: "Failed"
};
const EXPORT_V2_PHASE_NAMES: Record<number, string> = {
  0: "Start", 1: "Device Preparation", 2: "Device Setup",
  3: "App Installation", 4: "Account Setup", 5: "Apps (User)",
  6: "Finalizing Setup", 7: "Complete", 99: "Failed"
};
const EXPORT_V1_PHASE_ORDER = ["Start", "Device Preparation", "Device Setup",
  "Apps (Device)", "Account Setup", "Apps (User)", "Finalizing Setup", "Complete", "Failed"];
const EXPORT_V2_PHASE_ORDER = ["Start", "Device Preparation", "App Installation",
  "Finalizing Setup", "Complete", "Failed"];

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

interface BlockedDevice {
  tenantId: string;
  serialNumber: string;
  blockedAt: string;
  unblockAt: string;
  blockedByEmail: string;
  durationHours: number;
  reason?: string;
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
  const [maxCollectorDurationHours, setMaxCollectorDurationHours] = useState(4);
  const [maxSessionWindowHours, setMaxSessionWindowHours] = useState(24);
  const [maintenanceBlockDurationHours, setMaintenanceBlockDurationHours] = useState(12);

  // Session Event Export state
  const [exportSessionId, setExportSessionId] = useState("");
  const [exportTenantId, setExportTenantId] = useState("");
  const [exportLoading, setExportLoading] = useState(false);
  const [exportError, setExportError] = useState<string | null>(null);
  const [exportedEvents, setExportedEvents] = useState<SessionExportEvent[] | null>(null);

  // Device Block state
  const [blockSerialNumber, setBlockSerialNumber] = useState("");
  const [blockTenantId, setBlockTenantId] = useState("");
  const [blockDurationHours, setBlockDurationHours] = useState(12);
  const [blockReason, setBlockReason] = useState("");
  const [blockingDevice, setBlockingDevice] = useState(false);
  const [blockedDevices, setBlockedDevices] = useState<BlockedDevice[]>([]);
  const [loadingBlockedDevices, setLoadingBlockedDevices] = useState(false);
  const [unblockingDevice, setUnblockingDevice] = useState<string | null>(null);
  const [blockListTenantId, setBlockListTenantId] = useState("");

  // Tenant Management state
  const [tenants, setTenants] = useState<TenantConfiguration[]>([]);
  const [loadingTenants, setLoadingTenants] = useState(false);
  const [searchQuery, setSearchQuery] = useState("");
  const [showOnlyWaitlist, setShowOnlyWaitlist] = useState(false);
  const [tenantSectionExpanded, setTenantSectionExpanded] = useState(false);
  const [editingTenant, setEditingTenant] = useState<TenantConfiguration | null>(null);
  const [savingTenant, setSavingTenant] = useState(false);
  const [togglingSecurityBypassTenant, setTogglingSecurityBypassTenant] = useState<string | null>(null);
  const [currentPage, setCurrentPage] = useState(0);
  const tenantsPerPage = tenantSectionExpanded ? 7 : 3;

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
        setMaxCollectorDurationHours(data.maxCollectorDurationHours ?? 4);
        setMaxSessionWindowHours(data.maxSessionWindowHours ?? 24);
        setMaintenanceBlockDurationHours(data.maintenanceBlockDurationHours ?? 12);
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

  // Admin Pagination with Search
  const filteredAdmins = tenantAdmins.filter(admin =>
    admin.upn.toLowerCase().includes(adminSearchQuery.toLowerCase())
  );
  const totalAdminPages = Math.ceil(filteredAdmins.length / adminsPerPage);
  const startAdminIndex = currentAdminPage * adminsPerPage;
  const endAdminIndex = startAdminIndex + adminsPerPage;
  const paginatedAdmins = filteredAdmins.slice(startAdminIndex, endAdminIndex);

  const handleFetchExportEvents = async () => {
    const sid = exportSessionId.trim();
    const tid = exportTenantId.trim();
    if (!sid || !tid) {
      setExportError("Session ID and Tenant ID are required.");
      return;
    }
    try {
      setExportLoading(true);
      setExportError(null);
      setExportedEvents(null);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const res = await fetch(
        `${API_BASE_URL}/api/sessions/${sid}/events?tenantId=${encodeURIComponent(tid)}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      if (!res.ok) throw new Error(`HTTP ${res.status}: ${res.statusText}`);
      const data = await res.json();
      if (!data.success) throw new Error(data.message || "Backend returned error");
      setExportedEvents(data.events ?? []);
    } catch (err) {
      setExportError(err instanceof Error ? err.message : "Failed to fetch events");
    } finally {
      setExportLoading(false);
    }
  };

  const downloadFile = (content: string, filename: string, mimeType: string) => {
    const blob = new Blob([content], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = filename;
    a.click();
    URL.revokeObjectURL(url);
  };

  const generateCsvExport = (events: SessionExportEvent[]) => {
    const isV1 = events.some(e => e.phase === 2);
    const phaseNames = isV1 ? EXPORT_V1_PHASE_NAMES : EXPORT_V2_PHASE_NAMES;
    const esc = (v: string) => `"${v.replace(/"/g, '""')}"`;
    const header = "EventId,SessionId,TenantId,Timestamp,EventType,Severity,Source,Phase,PhaseName,Message,Sequence,Data";
    const rows = events.map(e => [
      esc(e.eventId ?? ""),
      esc(e.sessionId ?? ""),
      esc(e.tenantId ?? ""),
      esc(e.timestamp ?? ""),
      esc(e.eventType ?? ""),
      esc(e.severity ?? ""),
      esc(e.source ?? ""),
      String(e.phase ?? 0),
      esc(phaseNames[e.phase] ?? "Unknown"),
      esc(e.message ?? ""),
      String(e.sequence ?? 0),
      esc(e.data ? JSON.stringify(e.data) : ""),
    ].join(","));
    return "\uFEFF" + header + "\n" + rows.join("\n");
  };

  const generateUiExport = (events: SessionExportEvent[], sessionId: string, tenantId: string) => {
    const isV1 = events.some(e => e.phase === 2);
    const phaseNames = isV1 ? EXPORT_V1_PHASE_NAMES : EXPORT_V2_PHASE_NAMES;
    const phaseOrder = isV1 ? EXPORT_V1_PHASE_ORDER : EXPORT_V2_PHASE_ORDER;

    const sorted = [...events].sort((a, b) => a.sequence - b.sequence);

    // Replicate eventsByPhase useMemo logic: assign Unknown-phase events to active named phase
    const grouped: Record<string, SessionExportEvent[]> = {};
    phaseOrder.forEach(p => { grouped[p] = []; });

    let lastNamedPhase = phaseOrder[0];
    for (const ev of sorted) {
      const name = phaseNames[ev.phase];
      if (name && name !== "Unknown") {
        lastNamedPhase = name;
        if (grouped[name]) grouped[name].push({ ...ev, phaseName: name });
      } else {
        // Unknown phase — insert into currently active named phase
        if (grouped[lastNamedPhase]) grouped[lastNamedPhase].push({ ...ev, phaseName: "Unknown" });
      }
    }

    const pad = (s: string, len: number) => s.padEnd(len);
    const severityLabel = (s: string) => pad(s ?? "Unknown", 7);

    const lines: string[] = [];
    lines.push("AUTOPILOT MONITOR \u2014 SESSION EVENT EXPORT");
    lines.push("=========================================");
    lines.push(`Session ID   : ${sessionId}`);
    lines.push(`Tenant ID    : ${tenantId}`);
    lines.push(`Exported at  : ${new Date().toISOString()}`);
    lines.push(`Total events : ${events.length}`);
    lines.push(`Enrollment   : ${isV1 ? "V1" : "V2"}`);

    for (const phase of phaseOrder) {
      const phaseEvents = grouped[phase] ?? [];
      lines.push("");
      lines.push("\u2550".repeat(43));
      lines.push(`  ${phase}  (${phaseEvents.length} event${phaseEvents.length !== 1 ? "s" : ""})`);
      lines.push("\u2550".repeat(43));
      if (phaseEvents.length === 0) {
        lines.push("  (no events)");
      } else {
        for (const ev of phaseEvents) {
          const ts = ev.timestamp ? new Date(ev.timestamp).toISOString().replace("T", " ").substring(0, 23) : "?";
          lines.push(`[${ts}] [${severityLabel(ev.severity)}] ${ev.eventType} \u2014 ${ev.message}`);
          let detail = `  Source: ${ev.source ?? "?"} | Seq: ${ev.sequence ?? "?"} | EventId: ${ev.eventId ?? "?"}`;
          if (ev.phaseName === "Unknown") detail += ` | RawPhase: ${ev.phase}`;
          lines.push(detail);
          if (ev.data && Object.keys(ev.data).length > 0) {
            lines.push(`  Data: ${JSON.stringify(ev.data)}`);
          }
        }
      }
    }

    return lines.join("\n");
  };

  const fetchBlockedDevices = async (tenantId: string) => {
    if (!tenantId) return;
    try {
      setLoadingBlockedDevices(true);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const response = await fetch(`${API_BASE_URL}/api/devices/blocked?tenantId=${encodeURIComponent(tenantId)}`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      if (!response.ok) throw new Error(`Failed to load blocked devices: ${response.statusText}`);
      const data = await response.json();
      setBlockedDevices(data.blocked ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load blocked devices");
    } finally {
      setLoadingBlockedDevices(false);
    }
  };

  const handleBlockDevice = async () => {
    if (!blockSerialNumber.trim() || !blockTenantId.trim()) return;
    try {
      setBlockingDevice(true);
      setError(null);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const response = await fetch(`${API_BASE_URL}/api/devices/block`, {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
        body: JSON.stringify({
          tenantId: blockTenantId,
          serialNumber: blockSerialNumber.trim(),
          durationHours: blockDurationHours,
          reason: blockReason.trim() || undefined,
        }),
      });
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || response.statusText);
      setSuccessMessage(`Device ${blockSerialNumber.trim()} blocked for ${blockDurationHours}h.`);
      setTimeout(() => setSuccessMessage(null), 4000);
      setBlockSerialNumber("");
      setBlockReason("");
      if (blockListTenantId === blockTenantId) await fetchBlockedDevices(blockTenantId);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to block device");
    } finally {
      setBlockingDevice(false);
    }
  };

  const handleUnblockDevice = async (tenantId: string, serialNumber: string) => {
    try {
      setUnblockingDevice(serialNumber);
      setError(null);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");
      const response = await fetch(
        `${API_BASE_URL}/api/devices/block/${encodeURIComponent(serialNumber)}?tenantId=${encodeURIComponent(tenantId)}`,
        { method: "DELETE", headers: { Authorization: `Bearer ${token}` } }
      );
      const result = await response.json();
      if (!response.ok) throw new Error(result.message || response.statusText);
      setSuccessMessage(`Device ${serialNumber} unblocked.`);
      setTimeout(() => setSuccessMessage(null), 3000);
      setBlockedDevices((prev) => prev.filter((d) => d.serialNumber !== serialNumber || d.tenantId !== tenantId));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to unblock device");
    } finally {
      setUnblockingDevice(null);
    }
  };

  if (!galacticAdminMode) {
    return null;
  }

  return (
    <ProtectedRoute>
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
                                    {tenant.validateAutopilotDevice && (
                                      <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                                        Ready
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

            {/* Manual Maintenance Trigger */}
            <div className="bg-gradient-to-br from-purple-50 to-violet-50 dark:from-gray-800 dark:to-gray-800 border-2 border-purple-300 dark:border-purple-700 rounded-lg shadow-lg">
              <div className="p-6 border-b border-purple-200 dark:border-purple-700 bg-gradient-to-r from-purple-100 to-violet-100 dark:from-purple-900/40 dark:to-violet-900/40">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-purple-600 dark:text-purple-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-purple-900 dark:text-purple-100">Manual Maintenance Trigger</h2>
                    <p className="text-sm text-purple-600 dark:text-purple-300 mt-1">Execute platform-wide maintenance operations</p>
                  </div>
                </div>
              </div>
              <div className="p-6">
                <div className="flex items-start space-x-4">
                  <div className="flex-1">
                    <p className="text-sm text-purple-900 dark:text-gray-200 mb-4">
                      Manually trigger the daily maintenance job which includes:
                    </p>
                    <ul className="text-sm text-purple-900 dark:text-gray-200 space-y-1 mb-4 ml-4">
                      <li className="flex items-start">
                        <span className="text-purple-500 dark:text-purple-400 mr-2">•</span>
                        <span>Mark stalled sessions as timed out</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-purple-500 dark:text-purple-400 mr-2">•</span>
                        <span>Aggregate metrics into historical snapshots (with automatic catch-up for missed days)</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-purple-500 dark:text-purple-400 mr-2">•</span>
                        <span>Clean up old data based on retention policies</span>
                      </li>
                    </ul>
                    <div className="bg-purple-50 dark:bg-gray-700 border border-purple-200 dark:border-purple-600 rounded-lg p-4 mb-4">
                      <label className="block text-sm font-medium text-purple-800 dark:text-purple-200 mb-1">
                        Target Date (optional)
                      </label>
                      <input
                        type="date"
                        value={maintenanceDate}
                        onChange={(e) => setMaintenanceDate(e.target.value)}
                        max={new Date(Date.now() - 86400000).toISOString().split('T')[0]}
                        className="w-full max-w-xs px-3 py-2 border border-purple-300 dark:border-purple-600 rounded-lg text-sm bg-white dark:bg-gray-600 text-gray-900 dark:text-gray-100 focus:ring-2 focus:ring-purple-500 focus:border-purple-500"
                      />
                      <p className="text-xs text-gray-500 dark:text-gray-400 mt-2">
                        Leave empty to run the standard maintenance with automatic catch-up (aggregates any missed days within the last 7 days).
                        Select a specific date to manually aggregate metrics for that day, e.g. to backfill data older than 7 days.
                      </p>
                    </div>
                    <div className="bg-white dark:bg-gray-700 border border-purple-300 dark:border-purple-600 rounded-lg p-3 mb-4">
                      <div className="flex items-start space-x-2">
                        <svg className="w-5 h-5 text-purple-600 dark:text-purple-400 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                        </svg>
                        <p className="text-sm text-gray-800 dark:text-gray-200">
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
            <div className="bg-gradient-to-br from-amber-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border-2 border-amber-300 dark:border-amber-700 rounded-lg shadow-lg">
              <div className="p-6 border-b border-amber-200 dark:border-amber-700 bg-gradient-to-r from-amber-100 to-orange-100 dark:from-amber-900/40 dark:to-orange-900/40">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-amber-600 dark:text-amber-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-amber-900 dark:text-amber-100">Reseed Analyze Rules</h2>
                    <p className="text-sm text-amber-600 dark:text-amber-300 mt-1">Re-import all built-in analyze rules from code into Azure Table Storage</p>
                  </div>
                </div>
              </div>
              <div className="p-6">
                <div className="flex items-start space-x-4">
                  <div className="flex-1">
                    <p className="text-sm text-amber-900 dark:text-gray-200 mb-4">
                      This operation performs a full re-import of all built-in analyze rules:
                    </p>
                    <ul className="text-sm text-amber-900 dark:text-gray-200 space-y-1 mb-4 ml-4">
                      <li className="flex items-start">
                        <span className="text-amber-500 dark:text-amber-400 mr-2">•</span>
                        <span>Deletes all existing global built-in rules from the table</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-amber-500 dark:text-amber-400 mr-2">•</span>
                        <span>Writes all current code-defined rules as fresh entries</span>
                      </li>
                      <li className="flex items-start">
                        <span className="text-amber-500 dark:text-amber-400 mr-2">•</span>
                        <span>Tenant-specific custom rules and overrides are not affected</span>
                      </li>
                    </ul>
                    <div className="bg-white dark:bg-gray-700 border border-orange-300 dark:border-amber-600 rounded-lg p-3 mb-4">
                      <div className="flex items-start space-x-2">
                        <svg className="w-5 h-5 text-orange-600 dark:text-amber-400 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                        </svg>
                        <p className="text-sm text-gray-800 dark:text-gray-200">
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

            {/* Device Block Management */}
            <div className="bg-gradient-to-br from-red-50 to-orange-50 dark:from-gray-800 dark:to-gray-800 border-2 border-red-300 dark:border-red-700 rounded-lg shadow-lg">
              <div className="p-6 border-b border-red-200 dark:border-red-700 bg-gradient-to-r from-red-100 to-orange-100 dark:from-red-900/40 dark:to-orange-900/40">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-red-600 dark:text-red-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 18.364A9 9 0 005.636 5.636m12.728 12.728A9 9 0 015.636 5.636m12.728 12.728L5.636 5.636" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-red-900 dark:text-red-100">Device Block Management</h2>
                    <p className="text-sm text-red-600 dark:text-red-300 mt-1">Temporarily block a rogue device from sending data. The agent will stop uploading when it receives the block signal.</p>
                  </div>
                </div>
              </div>
              <div className="p-6 space-y-6">
                {/* Maintenance Auto-Block Settings */}
                <div className="border border-red-200 dark:border-red-700 rounded-lg p-4 bg-red-50/50 dark:bg-red-900/10">
                  <div className="flex items-center space-x-2 mb-3">
                    <svg className="w-4 h-4 text-red-500 dark:text-red-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                    <h3 className="text-sm font-semibold text-red-900 dark:text-red-100">Maintenance Auto-Block Settings</h3>
                  </div>
                  <p className="text-xs text-red-600 dark:text-red-400 mb-4">
                    The nightly maintenance function automatically blocks devices that are still actively sending data beyond the configured window. Sessions with a <code className="bg-red-100 dark:bg-red-900/40 px-1 rounded">LastEventAt</code> timestamp within the window <em>and</em> a <code className="bg-red-100 dark:bg-red-900/40 px-1 rounded">StartedAt</code> older than the window are flagged – regardless of session status.
                  </p>
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-4 mb-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                        Max Session Window (Hours)
                      </label>
                      <input
                        type="number"
                        min={0}
                        max={168}
                        value={maxSessionWindowHours}
                        onChange={(e) => setMaxSessionWindowHours(parseInt(e.target.value) || 0)}
                        className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                      />
                      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">0 = disabled</p>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
                        Maintenance Block Duration (Hours)
                      </label>
                      <input
                        type="number"
                        min={1}
                        max={720}
                        value={maintenanceBlockDurationHours}
                        onChange={(e) => setMaintenanceBlockDurationHours(parseInt(e.target.value) || 12)}
                        className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                      />
                      <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">Applied by the maintenance function</p>
                    </div>
                  </div>
                  <button
                    onClick={handleSaveAdminConfig}
                    disabled={savingConfig || !adminConfig}
                    className="px-4 py-2 bg-red-600 text-white rounded-lg text-sm font-medium hover:bg-red-700 disabled:opacity-50 flex items-center space-x-2"
                  >
                    {savingConfig ? (
                      <><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Saving...</span></>
                    ) : (
                      <span>Save Maintenance Settings</span>
                    )}
                  </button>
                </div>

                {/* Block a device form */}
                <div>
                  <h3 className="text-sm font-semibold text-red-900 dark:text-red-100 mb-3">Block a Device</h3>
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Serial Number</label>
                      <input
                        type="text"
                        placeholder="e.g. 1234ABCD"
                        value={blockSerialNumber}
                        onChange={(e) => setBlockSerialNumber(e.target.value)}
                        className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Tenant ID</label>
                      <select
                        value={blockTenantId}
                        onChange={(e) => setBlockTenantId(e.target.value)}
                        className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                      >
                        <option value="">— select tenant —</option>
                        {tenants.map((t) => (
                          <option key={t.tenantId} value={t.tenantId}>{t.domainName || t.tenantId}</option>
                        ))}
                      </select>
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Duration (Hours)</label>
                      <input
                        type="number"
                        min={1}
                        max={720}
                        value={blockDurationHours}
                        onChange={(e) => setBlockDurationHours(parseInt(e.target.value) || 12)}
                        className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Reason (optional)</label>
                      <input
                        type="text"
                        placeholder="e.g. Excessive data volume"
                        value={blockReason}
                        onChange={(e) => setBlockReason(e.target.value)}
                        className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                      />
                    </div>
                  </div>
                  <button
                    onClick={handleBlockDevice}
                    disabled={blockingDevice || !blockSerialNumber.trim() || !blockTenantId}
                    className="mt-4 px-4 py-2 bg-red-600 text-white rounded-lg text-sm font-medium hover:bg-red-700 disabled:opacity-50 flex items-center space-x-2"
                  >
                    {blockingDevice ? (
                      <><div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div><span>Blocking...</span></>
                    ) : (
                      <span>Block Device</span>
                    )}
                  </button>
                </div>

                {/* View active blocks for a tenant */}
                <div className="border-t border-red-200 dark:border-red-700 pt-6">
                  <h3 className="text-sm font-semibold text-red-900 dark:text-red-100 mb-3">Active Blocks</h3>
                  <div className="flex items-end gap-3 mb-4">
                    <div className="flex-1">
                      <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">Tenant</label>
                      <select
                        value={blockListTenantId}
                        onChange={(e) => setBlockListTenantId(e.target.value)}
                        className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-lg text-sm text-gray-900 dark:text-gray-100 bg-white dark:bg-gray-700 focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                      >
                        <option value="">— select tenant —</option>
                        {tenants.map((t) => (
                          <option key={t.tenantId} value={t.tenantId}>{t.domainName || t.tenantId}</option>
                        ))}
                      </select>
                    </div>
                    <button
                      onClick={() => fetchBlockedDevices(blockListTenantId)}
                      disabled={!blockListTenantId || loadingBlockedDevices}
                      className="px-4 py-2 bg-red-100 dark:bg-red-900/40 text-red-800 dark:text-red-200 border border-red-300 dark:border-red-600 rounded-lg text-sm font-medium hover:bg-red-200 disabled:opacity-50"
                    >
                      {loadingBlockedDevices ? "Loading..." : "Load"}
                    </button>
                  </div>
                  {blockedDevices.length > 0 ? (
                    <div className="overflow-x-auto">
                      <table className="w-full text-sm text-left">
                        <thead className="text-xs text-red-800 dark:text-red-200 uppercase bg-red-100 dark:bg-red-900/30">
                          <tr>
                            <th className="px-3 py-2">Serial Number</th>
                            <th className="px-3 py-2">Blocked Since</th>
                            <th className="px-3 py-2">Unblocks At</th>
                            <th className="px-3 py-2">Blocked By</th>
                            <th className="px-3 py-2">Reason</th>
                            <th className="px-3 py-2">Action</th>
                          </tr>
                        </thead>
                        <tbody className="divide-y divide-red-100 dark:divide-red-900/30">
                          {blockedDevices.map((d) => (
                            <tr key={d.serialNumber} className="bg-white dark:bg-gray-800">
                              <td className="px-3 py-2 font-mono font-medium text-gray-900 dark:text-gray-100">{d.serialNumber}</td>
                              <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{new Date(d.blockedAt).toLocaleString()}</td>
                              <td className="px-3 py-2 text-orange-600 dark:text-orange-400 font-medium">{new Date(d.unblockAt).toLocaleString()}</td>
                              <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{d.blockedByEmail}</td>
                              <td className="px-3 py-2 text-gray-600 dark:text-gray-400">{d.reason || "—"}</td>
                              <td className="px-3 py-2">
                                <button
                                  onClick={() => handleUnblockDevice(d.tenantId, d.serialNumber)}
                                  disabled={unblockingDevice === d.serialNumber}
                                  className="px-3 py-1 bg-green-600 text-white rounded text-xs font-medium hover:bg-green-700 disabled:opacity-50"
                                >
                                  {unblockingDevice === d.serialNumber ? "Unblocking..." : "Unblock"}
                                </button>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  ) : blockListTenantId && !loadingBlockedDevices ? (
                    <p className="text-sm text-gray-500 dark:text-gray-400 italic">No active blocks for this tenant.</p>
                  ) : null}
                </div>
              </div>
            </div>

            {/* Session Event Export Tool */}
            <div className="bg-gradient-to-br from-teal-50 to-cyan-50 dark:from-gray-800 dark:to-gray-800 border-2 border-teal-300 dark:border-teal-700 rounded-lg shadow-lg">
              <div className="p-6 border-b border-teal-200 dark:border-teal-700 bg-gradient-to-r from-teal-100 to-cyan-100 dark:from-teal-900/40 dark:to-cyan-900/40">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-teal-600 dark:text-teal-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-teal-900 dark:text-teal-100">Session Event Export</h2>
                    <p className="text-sm text-teal-600 dark:text-teal-300 mt-1">Fetch and export all events directly from storage — use to analyze timeline phase grouping and ordering</p>
                  </div>
                </div>
              </div>
              <div className="p-6">
                <div className="space-y-4">
                  <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                    <div>
                      <label className="block text-sm font-medium text-teal-900 dark:text-teal-100 mb-1">Session ID</label>
                      <input
                        type="text"
                        placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                        value={exportSessionId}
                        onChange={e => setExportSessionId(e.target.value)}
                        className="w-full px-3 py-2 border border-teal-300 dark:border-teal-600 rounded-lg text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
                      />
                    </div>
                    <div>
                      <label className="block text-sm font-medium text-teal-900 dark:text-teal-100 mb-1">Tenant ID</label>
                      <input
                        type="text"
                        placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                        value={exportTenantId}
                        onChange={e => setExportTenantId(e.target.value)}
                        className="w-full px-3 py-2 border border-teal-300 dark:border-teal-600 rounded-lg text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-teal-500 focus:border-teal-500"
                      />
                    </div>
                  </div>

                  <div className="flex justify-end">
                    <button
                      onClick={handleFetchExportEvents}
                      disabled={exportLoading}
                      className="px-5 py-2.5 bg-gradient-to-r from-teal-600 to-cyan-600 text-white rounded-lg hover:from-teal-700 hover:to-cyan-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2 text-sm font-medium"
                    >
                      {exportLoading ? (
                        <>
                          <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                          <span>Fetching...</span>
                        </>
                      ) : (
                        <>
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                          </svg>
                          <span>Fetch Events</span>
                        </>
                      )}
                    </button>
                  </div>

                  {exportError && (
                    <div className="bg-red-50 dark:bg-red-900/20 border border-red-200 dark:border-red-700 rounded-lg p-3 flex items-center space-x-2">
                      <svg className="w-4 h-4 text-red-600 dark:text-red-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                      </svg>
                      <span className="text-sm text-red-800 dark:text-red-300">{exportError}</span>
                    </div>
                  )}

                  {exportedEvents !== null && (
                    <div className="space-y-3">
                      <div className="flex items-center space-x-2 text-sm text-teal-800 dark:text-teal-200 bg-teal-50 dark:bg-teal-900/20 border border-teal-200 dark:border-teal-700 rounded-lg p-3">
                        <svg className="w-4 h-4 text-teal-600 dark:text-teal-400 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                        </svg>
                        <span>
                          <strong>{exportedEvents.length}</strong> events loaded
                          {" \u00B7 "}
                          {exportedEvents.some(e => e.phase === 2) ? "V1" : "V2"}
                          {" \u00B7 "}
                          session {exportSessionId.trim().slice(0, 8)}
                        </span>
                      </div>
                      <div className="flex flex-col sm:flex-row gap-3">
                        <button
                          onClick={() => {
                            const sid = exportSessionId.trim();
                            const tid = exportTenantId.trim();
                            downloadFile(
                              generateUiExport(exportedEvents, sid, tid),
                              `session-${sid.slice(0, 8)}-timeline.txt`,
                              "text/plain;charset=utf-8"
                            );
                          }}
                          className="flex items-center justify-center space-x-2 px-4 py-2.5 bg-white dark:bg-gray-700 border-2 border-teal-400 dark:border-teal-600 text-teal-800 dark:text-teal-200 rounded-lg hover:bg-teal-50 dark:hover:bg-teal-900/20 transition-colors text-sm font-medium"
                        >
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                          </svg>
                          <span>Download Timeline Export (.txt)</span>
                        </button>
                        <button
                          onClick={() => {
                            const sid = exportSessionId.trim();
                            downloadFile(
                              generateCsvExport(exportedEvents),
                              `session-${sid.slice(0, 8)}-events.csv`,
                              "text/csv;charset=utf-8"
                            );
                          }}
                          className="flex items-center justify-center space-x-2 px-4 py-2.5 bg-white dark:bg-gray-700 border-2 border-teal-400 dark:border-teal-600 text-teal-800 dark:text-teal-200 rounded-lg hover:bg-teal-50 dark:hover:bg-teal-900/20 transition-colors text-sm font-medium"
                        >
                          <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                          </svg>
                          <span>Download Raw CSV Export (.csv)</span>
                        </button>
                      </div>
                    </div>
                  )}
                </div>
              </div>
            </div>

            {/* Global Rate Limiting Configuration */}
            <div className="bg-gradient-to-br from-indigo-50 to-blue-50 dark:from-gray-800 dark:to-gray-800 border-2 border-indigo-300 dark:border-indigo-700 rounded-lg shadow-lg">
              <div className="p-6 border-b border-indigo-200 dark:border-indigo-700 bg-gradient-to-r from-indigo-100 to-blue-100 dark:from-indigo-900/40 dark:to-blue-900/40">
                <div className="flex items-center space-x-2">
                  <svg className="w-6 h-6 text-indigo-600 dark:text-indigo-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
                  </svg>
                  <div>
                    <h2 className="text-xl font-semibold text-indigo-900 dark:text-indigo-100">Global Settings</h2>
                    <p className="text-sm text-indigo-600 dark:text-indigo-300 mt-1">Configure global settings for all tenants</p>
                  </div>
                </div>
              </div>
              <div className="p-6">
                {loadingConfig ? (
                  <div className="text-center py-8">
                    <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600 dark:border-indigo-400 mx-auto"></div>
                    <p className="mt-3 text-indigo-800 dark:text-indigo-200 text-sm">Loading configuration...</p>
                  </div>
                ) : (
                  <div className="space-y-4">
                    <div>
                      <label className="block">
                        <span className="text-indigo-900 dark:text-indigo-100 font-medium">Global Rate Limit (Requests per Minute per Device)</span>
                        <p className="text-sm text-indigo-800 dark:text-gray-300 mb-2">
                          Configure default DoS protection limits for all tenants. Normal enrollment generates ~10-30 requests/min.
                          <br />
                          <strong className="text-indigo-900 dark:text-indigo-100">Note:</strong> Tenants cannot change this value. Only Galactic Admins can override per tenant in tenant management section.
                        </p>
                        <input
                          type="number"
                          min="1"
                          max="1000"
                          value={globalRateLimit}
                          onChange={(e) => setGlobalRateLimit(parseInt(e.target.value) || 100)}
                          className="mt-1 block w-full px-4 py-2 border border-indigo-300 dark:border-indigo-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                      </label>
                    </div>

                    <div>
                      <label className="block">
                        <span className="text-indigo-900 dark:text-indigo-100 font-medium">Platform Stats Blob Container SAS URL</span>
                        <p className="text-sm text-indigo-800 dark:text-gray-300 mb-2">
                          Maintenance publishes two files into this container:
                          <code className="ml-1 mr-1 text-xs bg-indigo-100 dark:bg-indigo-900 dark:text-indigo-200 px-1 rounded">platform-stats.json</code>
                          and
                          <code className="ml-1 text-xs bg-indigo-100 dark:bg-indigo-900 dark:text-indigo-200 px-1 rounded">platform-stats.YYYY-MM-DD.json</code>.
                          The upload is best-effort and does not fail the maintenance run.
                        </p>
                        <input
                          type="url"
                          value={platformStatsBlobSasUrl}
                          onChange={(e) => setPlatformStatsBlobSasUrl(e.target.value)}
                          placeholder="https://storageaccount.blob.core.windows.net/publicstats?sv=...&sig=..."
                          className="mt-1 block w-full px-4 py-2 border border-indigo-300 dark:border-indigo-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors font-mono text-sm"
                        />
                      </label>
                    </div>

                    <div>
                      <label className="block">
                        <span className="text-indigo-900 dark:text-indigo-100 font-medium">Max Collector Duration (Hours)</span>
                        <p className="text-sm text-indigo-800 dark:text-gray-300 mb-2">
                          Interval-based collectors (e.g. Performance Collector) stop automatically after this many hours per session.
                          Prevents excessive backend traffic when a device is stuck during enrollment.
                          Set to <strong>0</strong> to disable the limit (collectors run indefinitely).
                        </p>
                        <input
                          type="number"
                          min="0"
                          max="168"
                          value={maxCollectorDurationHours}
                          onChange={(e) => setMaxCollectorDurationHours(parseInt(e.target.value) || 0)}
                          className="mt-1 block w-full px-4 py-2 border border-indigo-300 dark:border-indigo-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                        />
                      </label>
                    </div>

                    {adminConfig && (
                      <div className="bg-blue-50 dark:bg-gray-700 border border-blue-200 dark:border-indigo-600 rounded-lg p-3">
                        <div className="flex items-start space-x-2">
                          <svg className="w-5 h-5 text-blue-600 dark:text-indigo-400 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                          </svg>
                          <div className="text-sm text-blue-800 dark:text-gray-200">
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
                        className="px-5 py-2 border border-indigo-300 dark:border-indigo-600 rounded-md text-indigo-800 dark:text-indigo-200 bg-white dark:bg-gray-700 hover:bg-indigo-50 dark:hover:bg-gray-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
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
