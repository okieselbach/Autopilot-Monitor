"use client";

import { useEffect, useState, useRef } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useSignalR } from "../../contexts/SignalRContext";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useNotifications } from "../../contexts/NotificationContext";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { trackEvent } from "@/lib/appInsights";
import { asGuidOrUndefined } from "@/lib/inputValidation";
import { Session } from "./types";
import { StatsCard } from "./components/StatsCards";
import { WelcomeMessage } from "./components/WelcomeMessage";
import { SessionTable } from "./components/SessionTable";
import { DeleteConfirmModal, BlockConfirmModal } from "./components/ConfirmationModals";
import TipOfTheDay from "./components/TipOfTheDay";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useDeleteSession } from "./hooks/useDeleteSession";
import { useBlockDevice } from "./hooks/useBlockDevice";

interface TenantConfigurationSummary {
  validateAutopilotDevice: boolean;
}

interface TenantListItem {
  tenantId: string;
  domainName: string;
}

export default function Home() {
  const router = useRouter();
  const { user, logout, getAccessToken, isPreviewBlocked } = useAuth();
  const { addNotification } = useNotifications();
  const [apiStatus, setApiStatus] = useState<"unchecked" | "checking" | "healthy" | "error">("unchecked");
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [hasMore, setHasMore] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [cursor, setCursor] = useState<string | null>(null);
  const [serialValidationEnabled, setSerialValidationEnabled] = useState<boolean | null>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [tenantIdFilter, setTenantIdFilter] = useState("");
  const [tenantList, setTenantList] = useState<TenantListItem[]>([]);
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [sortColumn, setSortColumn] = useState<keyof Session | null>(null);
  const [sortDirection, setSortDirection] = useState<"asc" | "desc">("asc");
  const [columnFilters, setColumnFilters] = useState<Record<string, Set<string>>>({});
  const [currentPage, setCurrentPage] = useState(1);
  const [sessionsPerPage, setSessionsPerPage] = useState(() => {
    if (typeof window !== 'undefined') {
      const stored = localStorage.getItem('sessionsPerPage');
      if (stored) return parseInt(stored, 10);
    }
    return 10;
  });
  const { adminMode, setAdminMode, globalAdminMode, setGlobalAdminMode } = useAdminMode();

  const {
    showDeleteConfirm, sessionToDelete,
    deleteSession, confirmDelete, cancelDelete,
  } = useDeleteSession(
    getAccessToken,
    addNotification,
    adminMode,
    (deletedId) => setSessions(prev => prev.filter(s => s.sessionId !== deletedId))
  );

  const {
    showBlockConfirm, sessionToBlock, blockingDevice, blockedDevicesSet, setBlockedDevicesSet,
    blockDevice, confirmBlock, cancelBlock,
  } = useBlockDevice(getAccessToken, addNotification, adminMode, globalAdminMode);

  // Use global contexts
  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();

  // Track if initial fetch has been done to prevent duplicate calls in React StrictMode
  const hasInitialFetch = useRef(false);

  // Track if global admin mode has been initialized (to skip initial mount in useEffect)
  const hasGlobalModeInitialized = useRef(false);

  // Refs for SignalR handlers to access current filter state (avoids stale closures)
  const tenantIdFilterRef = useRef(tenantIdFilter);
  tenantIdFilterRef.current = tenantIdFilter;
  const globalAdminModeRef = useRef(globalAdminMode);
  globalAdminModeRef.current = globalAdminMode;
  const tenantIdRef = useRef(tenantId);
  tenantIdRef.current = tenantId;

  // Track if we've joined the tenant group to prevent duplicate joins
  const hasJoinedGroup = useRef(false);

  // Track whether we've been connected at least once — used to detect reconnects
  const wasConnectedRef = useRef(false);

  const fetchSessions = async (loadMoreCursor?: string, globalTenantIdOverride?: string) => {
    try {
      // Use different endpoint based on global admin mode
      // Only pass tenantIdFilter as query param if it's a valid GUID (fuzzy search sets the GUID on selection)
      const rawFilter = globalTenantIdOverride !== undefined ? globalTenantIdOverride : tenantIdFilter.trim();
      const effectiveTenantFilter = asGuidOrUndefined(rawFilter);
      let endpoint = globalAdminMode
        ? api.globalSessions.list(effectiveTenantFilter)
        : api.sessions.list(tenantId);

      // Append cursor for "Load More" requests
      if (loadMoreCursor) {
        endpoint += endpoint.includes('?') ? '&' : '?';
        endpoint += `cursor=${encodeURIComponent(loadMoreCursor)}`;
      }

      const response = await authenticatedFetch(endpoint, getAccessToken);

      if (response.ok) {
        const data = await response.json();
        const newSessions = data.sessions || [];

        if (loadMoreCursor) {
          // Append to existing sessions (Load More)
          setSessions(prev => [...prev, ...newSessions]);
        } else {
          // Initial load — replace all sessions
          setSessions(newSessions);
          fetchBlockedDevices(newSessions);
        }

        setHasMore(data.hasMore || false);
        setCursor(data.cursor || null);
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
      setLoadingMore(false);
    }
  };

  const loadMore = () => {
    if (!cursor || loadingMore) return;
    setLoadingMore(true);
    fetchSessions(cursor);
  };

  const fetchBlockedDevices = async (currentSessions: Session[]) => {
    if (!adminMode || !globalAdminMode) {
      setBlockedDevicesSet(new Set());
      return;
    }

    try {
      const tenantIds = globalAdminMode
        ? [...new Set(currentSessions.map(s => s.tenantId))]
        : tenantId ? [tenantId] : [];

      if (tenantIds.length === 0) {
        setBlockedDevicesSet(new Set());
        return;
      }

      const results = await Promise.allSettled(
        tenantIds.map(tid =>
          authenticatedFetch(api.devices.blocked(tid), getAccessToken)
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

  // Redirect regular users (non-admin, non-operator) to progress portal – they must never see the session list
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGlobalAdmin && user.role !== 'Operator') {
      router.replace("/progress");
    }
  }, [user, router]);

  useEffect(() => {
    const fetchTenantSecurityConfig = async () => {
      if (!tenantId) return;
      if (user && !user.isTenantAdmin && !user.isGlobalAdmin && user.role !== 'Operator') return;

      try {
        const response = await authenticatedFetch(api.config.tenant(tenantId), getAccessToken);

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
    if (user && !user.isTenantAdmin && !user.isGlobalAdmin && user.role !== 'Operator') {
      return; // regular users are being redirected, don't fetch
    }
    if (!globalAdminMode && !tenantId) return; // wait for real tenant ID
    // Prevent duplicate fetches in React StrictMode (development double-mounting)
    if (hasInitialFetch.current) {
      return;
    }
    hasInitialFetch.current = true;

    // Only fetch sessions on load, no automatic health check
    fetchSessions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user, tenantId, globalAdminMode]); // re-run once user and tenantId are known

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

  // Join/leave global-admins group when Global Admin mode changes
  useEffect(() => {
    if (!isConnected) return;

    if (globalAdminMode) {
      console.log('[Home] Global Admin mode enabled: joining global-admins group');
      joinGroup('global-admins');
    } else {
      console.log('[Home] Global Admin mode disabled: leaving global-admins group');
      leaveGroup('global-admins');
    }

    return () => {
      // Clean up global-admins group on unmount if currently in Global Admin mode
      if (globalAdminMode) {
        console.log('[Home] Component unmounting: leaving global-admins group');
        leaveGroup('global-admins');
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isConnected, globalAdminMode]);

  // Setup SignalR listener - re-register when connection changes
  useEffect(() => {
    const handleNewSession = (data: { sessionId: string; tenantId: string; session: Session }) => {
      console.log('New session registered', data);

      // In non-global mode, only accept sessions from the user's own tenant
      if (!globalAdminModeRef.current && tenantIdRef.current && data.tenantId !== tenantIdRef.current) {
        console.log(`Ignoring newSession from tenant ${data.tenantId} (not in global mode, own tenant: ${tenantIdRef.current})`);
        return;
      }

      // In global admin mode with an active tenant filter, ignore sessions from other tenants
      const activeFilter = tenantIdFilterRef.current.trim();
      if (globalAdminModeRef.current && activeFilter && data.tenantId !== activeFilter) {
        console.log(`Ignoring newSession from tenant ${data.tenantId} (filtered to ${activeFilter})`);
        return;
      }

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

  // Track search usage (debounced — fires 1s after user stops typing)
  useEffect(() => {
    const timer = setTimeout(() => {
      trackEvent("session_searched", { hasQuery: searchQuery.length > 0 });
    }, 1000);
    return () => clearTimeout(timer);
  }, [searchQuery]);


  // Disable global admin mode if user is not a global admin
  useEffect(() => {
    if (user && !user.isGlobalAdmin && globalAdminMode) {
      console.log('[Home] User is not a global admin, disabling global admin mode');
      setGlobalAdminMode(false);
    }
  }, [user, globalAdminMode]);


  // Refetch sessions when global admin mode changes
  useEffect(() => {
    if (!hasGlobalModeInitialized.current) {
      hasGlobalModeInitialized.current = true;
      return;
    }

    // Clear tenant filter when leaving global mode
    if (!globalAdminMode) {
      setTenantIdFilter("");
    }

    // Clear sessions immediately to prevent showing stale cross-tenant data
    setSessions([]);
    setCursor(null);
    setCurrentPage(1);
    setLoading(true);
    fetchSessions();
  }, [globalAdminMode]);

  // Fetch tenant list for fuzzy search when entering global admin mode
  useEffect(() => {
    if (!globalAdminMode) {
      setTenantList([]);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const res = await authenticatedFetch(api.config.all(), getAccessToken);
        if (!res.ok || cancelled) return;
        const configs: { tenantId?: string; domainName?: string }[] = await res.json();
        if (cancelled) return;
        setTenantList(
          configs
            .filter((c): c is { tenantId: string; domainName: string } => !!c.tenantId && !!c.domainName)
            .map((c) => ({ tenantId: c.tenantId, domainName: c.domainName }))
        );
      } catch {
        // Non-critical — tenant autocomplete just won't work
      }
    })();
    return () => { cancelled = true; };
  }, [globalAdminMode, getAccessToken]);

  // Reset to page 1 when search query, status filter, sort, or page size changes
  useEffect(() => {
    setCurrentPage(1);
  }, [searchQuery, statusFilter, sortColumn, sortDirection, columnFilters, sessionsPerPage]);

  const handleSessionsPerPageChange = (value: number) => {
    setSessionsPerPage(value);
    localStorage.setItem('sessionsPerPage', String(value));
  };

  const applyTenantIdFilter = (value: string) => {
    setTenantIdFilter(value);
  };

  const submitTenantIdFilter = () => {
    setCursor(null);
    setCurrentPage(1);
    setLoading(true);
    fetchSessions();
  };

  const clearTenantIdFilter = () => {
    setTenantIdFilter("");
    setCursor(null);
    setCurrentPage(1);
    setLoading(true);
    fetchSessions(undefined, "");
  };

  // Client-side tenant filter: ensures sessions from other tenants are never displayed,
  // regardless of how they entered the sessions state (SignalR, race conditions, etc.)
  const effectiveSessions = globalAdminMode
    ? (tenantIdFilter.trim()
        ? sessions.filter(s => s.tenantId === tenantIdFilter.trim())
        : sessions)
    : (tenantId
        ? sessions.filter(s => s.tenantId === tenantId)
        : sessions);

  const activeSessions = effectiveSessions.filter(s => s.status === "InProgress");
  const successRate = effectiveSessions.length > 0
    ? Math.round((effectiveSessions.filter(s => s.status === "Succeeded").length / effectiveSessions.length) * 100)
    : 0;
  const avgDuration = effectiveSessions.length > 0
    ? Math.round(effectiveSessions.reduce((sum, s) => sum + s.durationSeconds, 0) / effectiveSessions.length / 60)
    : 0;
  const sessionsToday = effectiveSessions.filter(s =>
    new Date(s.startedAt).toDateString() === new Date().toDateString()
  );
  const totalToday = sessionsToday.length;
  const failedToday = sessionsToday.filter(s => s.status === "Failed").length;

  // Filter sessions based on status filter, column filters, and search query
  const filteredSessions = effectiveSessions.filter(session => {
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
      session.osName,
      session.osBuild,
      session.osDisplayVersion,
      session.osEdition,
      session.osLanguage,
    ].join(" ").toLowerCase();

    return searchableText.includes(query);
  });

  // Sort sessions
  const sortedSessions = [...filteredSessions].sort((a, b) => {
    if (!sortColumn) return 0;

    const rawA: Session[typeof sortColumn] = a[sortColumn];
    const rawB: Session[typeof sortColumn] = b[sortColumn];

    let aValue: number | string | boolean | null | undefined;
    let bValue: number | string | boolean | null | undefined;

    if (sortColumn === "startedAt") {
      aValue = new Date(rawA as string).getTime();
      bValue = new Date(rawB as string).getTime();
    } else {
      aValue = rawA;
      bValue = rawB;
    }

    if (aValue == null || bValue == null) return 0;
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
      <main className="max-w-7xl mx-auto py-4 sm:px-6 lg:px-8">
        <div className="px-4 sm:px-0">
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
                Private Preview Changelog
              </Link>{" "}
              or{" "}
              <Link
                href="/docs/known-issues"
                className="underline font-medium hover:text-blue-600 dark:hover:text-blue-200"
              >
                Known Issues
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
          <div className="grid grid-cols-1 gap-5 sm:grid-cols-2 lg:grid-cols-5 mb-2">
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
              title="Total Today"
              value={loading ? "..." : totalToday.toString()}
              description="Started today"
              color="indigo"
            />
            <StatsCard
              title="Failed Today"
              value={loading ? "..." : failedToday.toString()}
              description="Needs attention"
              color="red"
            />
          </div>

          <TipOfTheDay />

          {/* Welcome message - only show when no sessions */}
          {sessions.length === 0 && <WelcomeMessage />}

          {/* Sessions List */}
          {sessions.length > 0 && (
            <SessionTable
              sessions={effectiveSessions}
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
              sessionsPerPage={sessionsPerPage}
              onSessionsPerPageChange={handleSessionsPerPageChange}
              hasMore={hasMore}
              loadingMore={loadingMore}
              onLoadMore={loadMore}
              adminMode={adminMode}
              globalAdminMode={globalAdminMode}
              tenantIdFilter={tenantIdFilter}
              onTenantIdFilterChange={applyTenantIdFilter}
              onTenantIdFilterSubmit={submitTenantIdFilter}
              onTenantIdFilterClear={clearTenantIdFilter}
              tenantList={tenantList}
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
