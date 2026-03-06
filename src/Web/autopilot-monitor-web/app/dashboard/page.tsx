"use client";

import { useEffect, useState, useRef } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useSignalR } from "../../contexts/SignalRContext";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { Session } from "./types";
import { StatsCard } from "./components/StatsCards";
import { WelcomeMessage } from "./components/WelcomeMessage";
import { SessionTable } from "./components/SessionTable";
import { DeleteConfirmModal, BlockConfirmModal } from "./components/ConfirmationModals";

interface TenantConfigurationSummary {
  validateAutopilotDevice: boolean;
}

export default function Home() {
  const router = useRouter();
  const { user, logout, getAccessToken, isPreviewBlocked } = useAuth();
  const { addNotification } = useNotifications();
  const [apiStatus, setApiStatus] = useState<"unchecked" | "checking" | "healthy" | "error">("unchecked");
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [serialValidationEnabled, setSerialValidationEnabled] = useState<boolean | null>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [sortColumn, setSortColumn] = useState<keyof Session | null>(null);
  const [sortDirection, setSortDirection] = useState<"asc" | "desc">("asc");
  const [columnFilters, setColumnFilters] = useState<Record<string, Set<string>>>({});
  const [currentPage, setCurrentPage] = useState(1);
  const sessionsPerPage = 7;
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [sessionToDelete, setSessionToDelete] = useState<{ sessionId: string; tenantId: string; deviceName?: string } | null>(null);
  const [showBlockConfirm, setShowBlockConfirm] = useState(false);
  const [sessionToBlock, setSessionToBlock] = useState<{ serialNumber: string; tenantId: string; deviceName?: string } | null>(null);
  const [blockingDevice, setBlockingDevice] = useState(false);
  const [blockedDevicesSet, setBlockedDevicesSet] = useState<Set<string>>(new Set());
  const [adminMode, setAdminMode] = useState(() => {
    if (typeof window !== 'undefined') {
      return localStorage.getItem('adminMode') === 'true';
    }
    return false;
  });
  const [galacticAdminMode, setGalacticAdminMode] = useState(() => {
    if (typeof window !== 'undefined') {
      return localStorage.getItem('galacticAdminMode') === 'true';
    }
    return false;
  });

  // Use global contexts
  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();

  // Track if initial fetch has been done to prevent duplicate calls in React StrictMode
  const hasInitialFetch = useRef(false);

  // Track if galactic admin mode has been initialized (to skip initial mount in useEffect)
  const hasGalacticModeInitialized = useRef(false);

  // Track if we've joined the tenant group to prevent duplicate joins
  const hasJoinedGroup = useRef(false);

  // Track whether we've been connected at least once — used to detect reconnects
  const wasConnectedRef = useRef(false);

  const fetchSessions = async () => {
    try {
      // Use different endpoint based on galactic admin mode
      const endpoint = galacticAdminMode
        ? `${API_BASE_URL}/api/galactic/sessions`
        : `${API_BASE_URL}/api/sessions?tenantId=${tenantId}`;

      const response = await authenticatedFetch(endpoint, getAccessToken);

      if (response.ok) {
        const data = await response.json();
        const allSessions = data.sessions || [];
        setSessions(allSessions);
        fetchBlockedDevices(allSessions);
      } else {
        addNotification('error', 'Backend Error', `Failed to fetch sessions: ${response.statusText}`, 'backend-error');
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch sessions:", error);
        addNotification('error', 'Backend Not Reachable', 'Unable to connect to the backend API. Please ensure the backend server is running.', 'backend-unreachable');
      }
    } finally {
      setLoading(false);
    }
  };

  const fetchBlockedDevices = async (currentSessions: Session[]) => {
    if (!adminMode || !galacticAdminMode) {
      setBlockedDevicesSet(new Set());
      return;
    }

    try {
      const tenantIds = galacticAdminMode
        ? [...new Set(currentSessions.map(s => s.tenantId))]
        : tenantId ? [tenantId] : [];

      if (tenantIds.length === 0) {
        setBlockedDevicesSet(new Set());
        return;
      }

      const results = await Promise.allSettled(
        tenantIds.map(tid =>
          authenticatedFetch(`${API_BASE_URL}/api/devices/blocked?tenantId=${encodeURIComponent(tid)}`, getAccessToken)
            .then(res => res.ok ? res.json() : { blocked: [] })
        )
      );

      const newSet = new Set<string>();
      results.forEach(result => {
        if (result.status === "fulfilled" && result.value?.blocked) {
          for (const device of result.value.blocked) {
            newSet.add(`${device.tenantId}:${device.serialNumber}`);
          }
        }
      });

      setBlockedDevicesSet(newSet);
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch blocked devices:", error);
      }
    }
  };

  // Redirect regular users (non-admin) to progress portal – they must never see the session list
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGalacticAdmin) {
      router.replace("/progress");
    }
  }, [user, router]);

  useEffect(() => {
    const fetchTenantSecurityConfig = async () => {
      if (!tenantId) return;
      if (user && !user.isTenantAdmin && !user.isGalacticAdmin) return;

      try {
        const response = await authenticatedFetch(`${API_BASE_URL}/api/config/${tenantId}`, getAccessToken);

        if (!response.ok) {
          setSerialValidationEnabled(null);
          return;
        }

        const data: TenantConfigurationSummary = await response.json();
        setSerialValidationEnabled(!!data.validateAutopilotDevice);
      } catch (error) {
        if (error instanceof TokenExpiredError) {
          addNotification('error', 'Session Expired', error.message, 'session-expired-error');
        }
        setSerialValidationEnabled(null);
      }
    };

    fetchTenantSecurityConfig();
  }, [tenantId, user]);

  // Initial data fetch (only runs for admins).
  // Wait for a real tenantId before fetching — TenantContext initializes to '' and
  // updates asynchronously once AuthContext finishes loading the user token.
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGalacticAdmin) {
      return; // regular users are being redirected, don't fetch
    }
    if (!galacticAdminMode && !tenantId) return; // wait for real tenant ID
    // Prevent duplicate fetches in React StrictMode (development double-mounting)
    if (hasInitialFetch.current) {
      return;
    }
    hasInitialFetch.current = true;

    // Only fetch sessions on load, no automatic health check
    fetchSessions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user, tenantId, galacticAdminMode]); // re-run once user and tenantId are known

  // Join tenant group when SignalR is connected (for multi-tenancy)
  useEffect(() => {
    if (isConnected) {
      const isReconnect = wasConnectedRef.current;
      wasConnectedRef.current = true;

      if (!hasJoinedGroup.current) {
        const groupName = `tenant-${tenantId}`;
        hasJoinedGroup.current = true;
        joinGroup(groupName);
      }

      // After a reconnect, refresh the session list to catch any sessions that were
      // registered while the client was disconnected and not in the SignalR group.
      if (isReconnect && hasInitialFetch.current) {
        fetchSessions();
      }
    }

    return () => {
      // Leave group when component unmounts
      if (hasJoinedGroup.current) {
        const groupName = `tenant-${tenantId}`;
        leaveGroup(groupName);
        hasJoinedGroup.current = false;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected, tenantId]);

  // Join/leave galactic-admins group when Galactic Admin mode changes
  useEffect(() => {
    if (!isConnected) return;

    if (galacticAdminMode) {
      console.log('[Home] Galactic Admin mode enabled: joining galactic-admins group');
      joinGroup('galactic-admins');
    } else {
      console.log('[Home] Galactic Admin mode disabled: leaving galactic-admins group');
      leaveGroup('galactic-admins');
    }

    return () => {
      // Clean up galactic-admins group on unmount if currently in Galactic Admin mode
      if (galacticAdminMode) {
        console.log('[Home] Component unmounting: leaving galactic-admins group');
        leaveGroup('galactic-admins');
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected, galacticAdminMode]);

  // Setup SignalR listener - re-register when connection changes
  useEffect(() => {
    const handleNewSession = (data: { sessionId: string; tenantId: string; session: Session }) => {
      console.log('New session registered', data);

      if (data.session) {
        setSessions(prevSessions => {
          const sessionIndex = prevSessions.findIndex(s => s.sessionId === data.session.sessionId);
          if (sessionIndex >= 0) {
            const updated = [...prevSessions];
            updated[sessionIndex] = data.session;
            return updated;
          } else {
            return [data.session, ...prevSessions];
          }
        });
      } else {
        console.warn('newSession event received without session data, falling back to fetch');
        fetchSessions();
      }
    };

    const handleNewEvents = (data: { sessionId: string; tenantId: string; eventCount: number; sessionUpdate?: Partial<Session>; session?: Session }) => {
      console.log('New events notification received on home page', data);

      const update = data.sessionUpdate || data.session;
      if (update) {
        setSessions(prevSessions => {
          const sessionIndex = prevSessions.findIndex(s => s.sessionId === data.sessionId);
          if (sessionIndex >= 0) {
            const updated = [...prevSessions];
            updated[sessionIndex] = { ...prevSessions[sessionIndex], ...update };
            return updated;
          }
          return prevSessions;
        });
      }
    };

    on('newSession', handleNewSession);
    on('newevents', handleNewEvents);

    return () => {
      off('newSession', handleNewSession);
      off('newevents', handleNewEvents);
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected]);

  // Save admin mode to localStorage when it changes
  useEffect(() => {
    localStorage.setItem('adminMode', adminMode.toString());
  }, [adminMode]);

  // Save galactic admin mode to localStorage when it changes
  useEffect(() => {
    localStorage.setItem('galacticAdminMode', galacticAdminMode.toString());
  }, [galacticAdminMode]);

  // Clear blocked devices set when admin mode or galactic admin mode is turned off
  useEffect(() => {
    if (!adminMode || !galacticAdminMode) {
      setBlockedDevicesSet(new Set());
    }
  }, [adminMode, galacticAdminMode]);

  // Disable galactic admin mode if user is not a galactic admin
  useEffect(() => {
    if (user && !user.isGalacticAdmin && galacticAdminMode) {
      console.log('[Home] User is not a galactic admin, disabling galactic admin mode');
      setGalacticAdminMode(false);
    }
  }, [user, galacticAdminMode]);

  // Listen for localStorage changes from other components (e.g., Navbar)
  useEffect(() => {
    const handleStorageChange = (e: StorageEvent) => {
      if (e.key === 'adminMode' && e.newValue !== null) {
        setAdminMode(e.newValue === 'true');
      }
      if (e.key === 'galacticAdminMode' && e.newValue !== null) {
        setGalacticAdminMode(e.newValue === 'true');
      }
    };

    const handleCustomStorageChange = () => {
      const newAdminMode = localStorage.getItem('adminMode') === 'true';
      const newGalacticMode = localStorage.getItem('galacticAdminMode') === 'true';

      if (newAdminMode !== adminMode) {
        setAdminMode(newAdminMode);
      }
      if (newGalacticMode !== galacticAdminMode) {
        setGalacticAdminMode(newGalacticMode);
      }
    };

    window.addEventListener('storage', handleStorageChange);
    window.addEventListener('localStorageChange', handleCustomStorageChange);

    return () => {
      window.removeEventListener('storage', handleStorageChange);
      window.removeEventListener('localStorageChange', handleCustomStorageChange);
    };
  }, [adminMode, galacticAdminMode]);

  // Refetch sessions when galactic admin mode changes
  useEffect(() => {
    if (!hasGalacticModeInitialized.current) {
      hasGalacticModeInitialized.current = true;
      return;
    }

    setLoading(true);
    fetchSessions();
  }, [galacticAdminMode]);

  // Reset to page 1 when search query, status filter, or sort changes
  useEffect(() => {
    setCurrentPage(1);
  }, [searchQuery, statusFilter, sortColumn, sortDirection, columnFilters]);

  const deleteSession = (sessionId: string, sessionTenantId: string, deviceName?: string) => {
    setSessionToDelete({ sessionId, tenantId: sessionTenantId, deviceName });
    setShowDeleteConfirm(true);
  };

  const confirmDelete = async () => {
    if (!sessionToDelete) return;

    try {
      const response = await authenticatedFetch(`${API_BASE_URL}/api/sessions/${sessionToDelete.sessionId}?tenantId=${sessionToDelete.tenantId}`, getAccessToken, {
        method: 'DELETE',
      });

      if (response.ok) {
        setSessions(prevSessions => prevSessions.filter(s => s.sessionId !== sessionToDelete.sessionId));
        console.log(`Session ${sessionToDelete.sessionId} deleted successfully`);
        setShowDeleteConfirm(false);
        setSessionToDelete(null);
      } else {
        const data = await response.json();
        alert(`Fehler beim Löschen: ${data.message || 'Unbekannter Fehler'}`);
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error('Failed to delete session:', error);
        alert('Fehler beim Löschen der Session');
      }
    }
  };

  const cancelDelete = () => {
    setShowDeleteConfirm(false);
    setSessionToDelete(null);
  };

  const blockDevice = (serialNumber: string, sessionTenantId: string, deviceName?: string) => {
    setSessionToBlock({ serialNumber, tenantId: sessionTenantId, deviceName });
    setShowBlockConfirm(true);
  };

  const confirmBlock = async () => {
    if (!sessionToBlock) return;

    try {
      setBlockingDevice(true);

      const response = await authenticatedFetch(`${API_BASE_URL}/api/devices/block`, getAccessToken, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          tenantId: sessionToBlock.tenantId,
          serialNumber: sessionToBlock.serialNumber,
          durationHours: 24,
          reason: `Blocked from dashboard by Galactic Admin`
        })
      });

      if (response.ok) {
        console.log(`Device ${sessionToBlock.serialNumber} blocked successfully`);
        setShowBlockConfirm(false);
        setSessionToBlock(null);
        addNotification('success', 'Device Blocked', `Device ${sessionToBlock.deviceName || sessionToBlock.serialNumber} blocked for 24 hours.`);
        setBlockedDevicesSet(prev => {
          const next = new Set(prev);
          next.add(`${sessionToBlock.tenantId}:${sessionToBlock.serialNumber}`);
          return next;
        });
      } else {
        const data = await response.json();
        alert(`Fehler beim Blocken: ${data.message || 'Unbekannter Fehler'}`);
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error('Failed to block device:', error);
        alert('Fehler beim Blocken des Geräts');
      }
    } finally {
      setBlockingDevice(false);
    }
  };

  const cancelBlock = () => {
    setShowBlockConfirm(false);
    setSessionToBlock(null);
  };

  const activeSessions = sessions.filter(s => s.status === "InProgress");
  const successRate = sessions.length > 0
    ? Math.round((sessions.filter(s => s.status === "Succeeded").length / sessions.length) * 100)
    : 0;
  const avgDuration = sessions.length > 0
    ? Math.round(sessions.reduce((sum, s) => sum + s.durationSeconds, 0) / sessions.length / 60)
    : 0;
  const failedToday = sessions.filter(s =>
    s.status === "Failed" &&
    new Date(s.startedAt).toDateString() === new Date().toDateString()
  ).length;

  // Filter sessions based on status filter, column filters, and search query
  const filteredSessions = sessions.filter(session => {
    if (statusFilter && session.status !== statusFilter) return false;

    // Apply column filters
    for (const [field, allowedValues] of Object.entries(columnFilters)) {
      if (allowedValues.size === 0) continue;
      const value = String(session[field as keyof Session] ?? "");
      if (!allowedValues.has(value)) return false;
    }

    if (!searchQuery.trim()) return true;

    const query = searchQuery.toLowerCase().trim();

    const durationMatch = query.match(/^([><]=?)\s*(\d+)$/);
    if (durationMatch) {
      const operator = durationMatch[1];
      const value = parseInt(durationMatch[2]);
      const durationMinutes = Math.round(session.durationSeconds / 60);

      if (operator === ">") return durationMinutes > value;
      if (operator === ">=") return durationMinutes >= value;
      if (operator === "<") return durationMinutes < value;
      if (operator === "<=") return durationMinutes <= value;
    }

    const searchableText = [
      session.deviceName,
      session.serialNumber,
      session.manufacturer,
      session.model,
      session.status,
      session.sessionId,
      new Date(session.startedAt).toLocaleString(),
      `${Math.round(session.durationSeconds / 60)} min`,
      blockedDevicesSet.has(`${session.tenantId}:${session.serialNumber}`) ? "blocked" : "",
      session.geoCountry,
      session.geoRegion,
      session.geoCity,
      session.agentVersion,
      session.osBuild,
      session.osEdition,
      session.osLanguage,
    ].join(" ").toLowerCase();

    return searchableText.includes(query);
  });

  // Sort sessions
  const sortedSessions = [...filteredSessions].sort((a, b) => {
    if (!sortColumn) return 0;

    let aValue: any = a[sortColumn];
    let bValue: any = b[sortColumn];

    if (sortColumn === "startedAt") {
      aValue = new Date(aValue).getTime();
      bValue = new Date(bValue).getTime();
    }

    if (aValue < bValue) return sortDirection === "asc" ? -1 : 1;
    if (aValue > bValue) return sortDirection === "asc" ? 1 : -1;
    return 0;
  });

  // Paginated sessions
  const totalPages = Math.ceil(sortedSessions.length / sessionsPerPage);
  const startIndex = (currentPage - 1) * sessionsPerPage;
  const paginatedSessions = sortedSessions.slice(startIndex, startIndex + sessionsPerPage);

  const handleSort = (column: keyof Session) => {
    if (sortColumn === column) {
      setSortDirection(sortDirection === "asc" ? "desc" : "asc");
    } else {
      setSortColumn(column);
      setSortDirection("asc");
    }
  };

  const handlePreviousPage = () => {
    setCurrentPage(prev => Math.max(1, prev - 1));
  };

  const handleNextPage = () => {
    setCurrentPage(prev => Math.min(totalPages, prev + 1));
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
      {/* Main content */}
      <main className="max-w-7xl mx-auto py-6 sm:px-6 lg:px-8">
        <div className="px-4 py-6 sm:px-0">
          {/* Temporary banner: Analyze Rules are being actively refined */}
          <div className="mb-4 bg-amber-50 border border-amber-300 rounded-lg px-4 py-3 flex items-start gap-3 dark:bg-amber-950/30 dark:border-amber-700/50">
            <svg className="w-4 h-4 text-amber-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
            </svg>
            <p className="text-sm text-amber-800 dark:text-amber-300">
              <span className="font-semibold">Analyze Rules are actively being refined.</span>{" "}
              During this phase there may be occasional backend redeploys and analysis results might change or appear inconsistent. This is expected while rules are being fine-tuned.
            </p>
          </div>

          {/* Feedback & bug report banner */}
          <div className="mb-4 bg-blue-50 border border-blue-300 rounded-lg px-4 py-3 flex items-start gap-3 dark:bg-blue-950/30 dark:border-blue-700/50">
            <svg className="w-4 h-4 text-blue-500 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
            </svg>
            <p className="text-sm text-blue-800 dark:text-blue-300">
              <span className="font-semibold">Private Preview.</span>{" "}
              The platform is under active development.{" "}
              If something looks off, check the{" "}
              <Link
                href="/changelog"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Private Preview Changelog/Known Issues
              </Link>
              .{" "}
              Feedback or bug report?{" "}
              <a
                href="https://github.com/okieselbach/Autopilot-Monitor/issues"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Open a GitHub issue
              </a>
              {" "}or message me on{" "}
              <a
                href="https://www.linkedin.com/in/oliver-kieselbach/"
                target="_blank"
                rel="noopener noreferrer"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                LinkedIn
              </a>
              .
            </p>
          </div>

          {serialValidationEnabled === false && (
            <div className="mb-6 bg-red-600 border-2 border-red-700 rounded-xl p-5 shadow-lg">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div className="flex items-start gap-3">
                  <svg className="w-6 h-6 text-white mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
                  </svg>
                  <div>
                    <p className="text-base font-bold text-white">Action required: Autopilot Device Validation is disabled</p>
                    <p className="text-sm text-red-100 mt-0.5">
                      Agent ingestion is blocked. Enable Autopilot Device Validation in Settings to start monitoring devices.
                    </p>
                  </div>
                </div>
                <a
                  href="/settings"
                  className="shrink-0 inline-flex items-center gap-2 bg-white text-red-700 font-semibold text-sm px-4 py-2 rounded-lg hover:bg-red-50 transition-colors"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                  </svg>
                  Open Settings
                </a>
              </div>
            </div>
          )}

          {/* Stats cards */}
          <div className="grid grid-cols-1 gap-5 sm:grid-cols-2 lg:grid-cols-4 mb-8">
            <StatsCard
              title="Active Sessions"
              value={loading ? "..." : activeSessions.length.toString()}
              description="Currently enrolling"
              color="blue"
            />
            <StatsCard
              title="Success Rate"
              value={loading ? "..." : `${successRate}%`}
              description="Last 7 days"
              color="green"
            />
            <StatsCard
              title="Avg. Duration"
              value={loading ? "..." : `${avgDuration} min`}
              description="Last 7 days"
              color="purple"
            />
            <StatsCard
              title="Failed Today"
              value={loading ? "..." : failedToday.toString()}
              description="Needs attention"
              color="red"
            />
          </div>

          {/* Welcome message - only show when no sessions */}
          {sessions.length === 0 && <WelcomeMessage />}

          {/* Sessions List */}
          {sessions.length > 0 && (
            <SessionTable
              sessions={sessions}
              filteredSessions={filteredSessions}
              sortedSessions={sortedSessions}
              paginatedSessions={paginatedSessions}
              searchQuery={searchQuery}
              onSearchQueryChange={setSearchQuery}
              statusFilter={statusFilter}
              onStatusFilterChange={setStatusFilter}
              sortColumn={sortColumn}
              sortDirection={sortDirection}
              onSort={handleSort}
              currentPage={currentPage}
              totalPages={totalPages}
              onPreviousPage={handlePreviousPage}
              onNextPage={handleNextPage}
              adminMode={adminMode}
              galacticAdminMode={galacticAdminMode}
              blockedDevicesSet={blockedDevicesSet}
              isPreviewBlocked={isPreviewBlocked}
              user={user}
              columnFilters={columnFilters}
              onColumnFiltersChange={setColumnFilters}
              onDeleteSession={deleteSession}
              onBlockDevice={blockDevice}
            />
          )}
        </div>
      </main>

      {/* Delete Confirmation Modal */}
      {showDeleteConfirm && sessionToDelete && (
        <DeleteConfirmModal
          sessionToDelete={sessionToDelete}
          onConfirm={confirmDelete}
          onCancel={cancelDelete}
        />
      )}

      {/* Block Device Confirmation Modal */}
      {showBlockConfirm && sessionToBlock && (
        <BlockConfirmModal
          sessionToBlock={sessionToBlock}
          blockingDevice={blockingDevice}
          onConfirm={confirmBlock}
          onCancel={cancelBlock}
        />
      )}
    </div>
    </ProtectedRoute>
  );
}
