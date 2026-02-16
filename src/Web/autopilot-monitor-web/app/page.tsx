"use client";

import { useEffect, useState, useRef } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../components/ProtectedRoute";
import { useSignalR } from "../contexts/SignalRContext";
import { useTenant } from "../contexts/TenantContext";
import { useAuth } from "../contexts/AuthContext";
import { useNotifications } from "../contexts/NotificationContext";
import { API_BASE_URL } from "@/lib/config";

interface Session {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  status: string;
  currentPhase: number;
  eventCount: number;
  durationSeconds: number;
  failureReason?: string;
}

interface TenantConfigurationSummary {
  validateSerialNumber: boolean;
}

export default function Home() {
  const router = useRouter();
  const { user, logout, getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const [apiStatus, setApiStatus] = useState<"unchecked" | "checking" | "healthy" | "error">("unchecked");
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [serialValidationEnabled, setSerialValidationEnabled] = useState<boolean | null>(null);
  const [searchQuery, setSearchQuery] = useState("");
  const [sortColumn, setSortColumn] = useState<keyof Session | null>(null);
  const [sortDirection, setSortDirection] = useState<"asc" | "desc">("asc");
  const [currentPage, setCurrentPage] = useState(1);
  const sessionsPerPage = 10;
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const [sessionToDelete, setSessionToDelete] = useState<{ sessionId: string; tenantId: string; deviceName?: string } | null>(null);
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

  const fetchSessions = async () => {
    try {
      // Use different endpoint based on galactic admin mode
      const endpoint = galacticAdminMode
        ? `${API_BASE_URL}/api/galactic/sessions`
        : `${API_BASE_URL}/api/sessions?tenantId=${tenantId}`;

      // Get access token and include Authorization header
      const token = await getAccessToken();

      if (!token) {
        addNotification('error', 'Authentication Error', 'Failed to get access token. Please try logging in again.', 'auth-error');
        return;
      }

      const response = await fetch(endpoint, {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });

      if (response.ok) {
        const data = await response.json();
        const allSessions = data.sessions || [];
        setSessions(allSessions);
      } else {
        addNotification('error', 'Backend Error', `Failed to fetch sessions: ${response.statusText}`, 'backend-error');
      }
    } catch (error) {
      console.error("Failed to fetch sessions:", error);
      addNotification('error', 'Backend Not Reachable', 'Unable to connect to the backend API. Please ensure the backend server is running.', 'backend-unreachable');
    } finally {
      setLoading(false);
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
        const token = await getAccessToken();
        if (!token) return;

        const response = await fetch(`${API_BASE_URL}/api/config/${tenantId}`, {
          headers: {
            'Authorization': `Bearer ${token}`
          }
        });

        if (!response.ok) {
          setSerialValidationEnabled(null);
          return;
        }

        const data: TenantConfigurationSummary = await response.json();
        setSerialValidationEnabled(!!data.validateSerialNumber);
      } catch {
        setSerialValidationEnabled(null);
      }
    };

    fetchTenantSecurityConfig();
  }, [tenantId, user]);

  // Initial data fetch (only runs for admins)
  useEffect(() => {
    if (user && !user.isTenantAdmin && !user.isGalacticAdmin) {
      return; // regular users are being redirected, don't fetch
    }
    // Prevent duplicate fetches in React StrictMode (development double-mounting)
    if (hasInitialFetch.current) {
      return;
    }
    hasInitialFetch.current = true;

    // Only fetch sessions on load, no automatic health check
    fetchSessions();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [user]); // re-run once user is known

  // Join tenant group when SignalR is connected (for multi-tenancy)
  useEffect(() => {
    if (isConnected && !hasJoinedGroup.current) {
      const groupName = `tenant-${tenantId}`;
      joinGroup(groupName);
      hasJoinedGroup.current = true;
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
    // Listen for real-time updates via SignalR
    // - "newSession" events: Sent when a session is first registered (new session notification)
    // - "newevents" events: Sent when events are ingested (status updates for existing sessions)
    // Both are important for keeping the session list up-to-date

    const handleNewSession = (data: { sessionId: string; tenantId: string; session: Session }) => {
      console.log('New session registered', data);

      // Add new session to the list (at the beginning - most recent first)
      if (data.session) {
        setSessions(prevSessions => {
          // Check if session already exists (shouldn't happen, but be defensive)
          const sessionIndex = prevSessions.findIndex(s => s.sessionId === data.session.sessionId);
          if (sessionIndex >= 0) {
            // Update existing session (just in case)
            const updated = [...prevSessions];
            updated[sessionIndex] = data.session;
            return updated;
          } else {
            // Add new session at the beginning (most recent first)
            return [data.session, ...prevSessions];
          }
        });
      }
    };

    const handleNewEvents = (data: { sessionId: string; tenantId: string; eventCount: number; session: Session }) => {
      console.log('New events notification received on home page', data);

      // Update the session in the list directly from SignalR data (no HTTP request needed!)
      if (data.session) {
        setSessions(prevSessions => {
          const sessionIndex = prevSessions.findIndex(s => s.sessionId === data.session.sessionId);
          if (sessionIndex >= 0) {
            // Update existing session
            const updated = [...prevSessions];
            updated[sessionIndex] = data.session;
            return updated;
          } else {
            // Session not in list yet - this can happen if we missed the newSession event
            // Add it to the list anyway
            return [data.session, ...prevSessions];
          }
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
  }, [isConnected]); // Re-register when SignalR connection is established

  // Save admin mode to localStorage when it changes
  useEffect(() => {
    localStorage.setItem('adminMode', adminMode.toString());
  }, [adminMode]);

  // Save galactic admin mode to localStorage when it changes
  useEffect(() => {
    localStorage.setItem('galacticAdminMode', galacticAdminMode.toString());
  }, [galacticAdminMode]);

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

    // Also listen for custom storage events (for same-window changes)
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
    // Skip on initial mount - only refetch when user actually toggles the setting
    if (!hasGalacticModeInitialized.current) {
      hasGalacticModeInitialized.current = true;
      return;
    }

    setLoading(true);
    fetchSessions();
  }, [galacticAdminMode]);

  // Reset to page 1 when search query or sort changes
  useEffect(() => {
    setCurrentPage(1);
  }, [searchQuery, sortColumn, sortDirection]);

  const deleteSession = (sessionId: string, sessionTenantId: string, deviceName?: string) => {
    setSessionToDelete({ sessionId, tenantId: sessionTenantId, deviceName });
    setShowDeleteConfirm(true);
  };

  const confirmDelete = async () => {
    if (!sessionToDelete) return;

    try {
      // Get access token and include Authorization header
      const token = await getAccessToken();

      if (!token) {
        alert('Authentication error: Failed to get access token. Please try logging in again.');
        return;
      }

      const response = await fetch(`${API_BASE_URL}/api/sessions/${sessionToDelete.sessionId}?tenantId=${sessionToDelete.tenantId}`, {
        method: 'DELETE',
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });

      if (response.ok) {
        // Remove session from local state
        setSessions(prevSessions => prevSessions.filter(s => s.sessionId !== sessionToDelete.sessionId));
        console.log(`Session ${sessionToDelete.sessionId} deleted successfully`);
        setShowDeleteConfirm(false);
        setSessionToDelete(null);
      } else {
        const data = await response.json();
        alert(`Fehler beim Löschen: ${data.message || 'Unbekannter Fehler'}`);
      }
    } catch (error) {
      console.error('Failed to delete session:', error);
      alert('Fehler beim Löschen der Session');
    }
  };

  const cancelDelete = () => {
    setShowDeleteConfirm(false);
    setSessionToDelete(null);
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

  // Filter sessions based on search query
  const filteredSessions = sessions.filter(session => {
    if (!searchQuery.trim()) return true;

    const query = searchQuery.toLowerCase().trim();

    // Check for duration comparison (e.g., ">30" or "<10")
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

    // Search in multiple fields
    const searchableText = [
      session.deviceName,
      session.serialNumber,
      session.manufacturer,
      session.model,
      session.status,
      session.sessionId,
      new Date(session.startedAt).toLocaleString(),
      `${Math.round(session.durationSeconds / 60)} min`
    ].join(" ").toLowerCase();

    return searchableText.includes(query);
  });

  // Sort sessions
  const sortedSessions = [...filteredSessions].sort((a, b) => {
    if (!sortColumn) return 0;

    let aValue: any = a[sortColumn];
    let bValue: any = b[sortColumn];

    // Handle special cases
    if (sortColumn === "startedAt") {
      aValue = new Date(aValue).getTime();
      bValue = new Date(bValue).getTime();
    }

    if (aValue < bValue) return sortDirection === "asc" ? -1 : 1;
    if (aValue > bValue) return sortDirection === "asc" ? 1 : -1;
    return 0;
  });

  // Paginated sessions - show only current page
  const totalPages = Math.ceil(sortedSessions.length / sessionsPerPage);
  const startIndex = (currentPage - 1) * sessionsPerPage;
  const endIndex = startIndex + sessionsPerPage;
  const paginatedSessions = sortedSessions.slice(startIndex, endIndex);

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
          {serialValidationEnabled === false && (
            <div className="mb-6 bg-red-50 border border-red-200 rounded-lg p-4">
              <div className="flex items-start gap-3">
                <svg className="w-5 h-5 text-red-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <div>
                  <p className="text-sm font-semibold text-red-900">Serial Number Validation is disabled</p>
                  <p className="text-sm text-red-800">
                    Agent ingestion is blocked until this is enabled. Open Configuration and enable Serial Number Validation first.
                  </p>
                </div>
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
          {sessions.length === 0 && (
            <div className="bg-white shadow rounded-lg p-6">
              <h2 className="text-xl font-semibold text-gray-900 mb-2">
                Welcome to Autopilot Monitor
              </h2>
              <p className="text-gray-600 mb-6">
                No enrollment sessions yet. Sessions will appear below once devices start enrolling.
                <br />
                <br />
                If you're just getting started, check out the documentation for Intune bootstrapper setup instructions.
              </p>
              <div className="mt-6">
                <h3 className="text-lg font-medium text-gray-900 mb-3">Quick Links</h3>
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                  <QuickLink
                    title="Documentation"
                    description="Setup and configuration guides"
                    href="/docs"
                  />
                  <QuickLink
                    title="GitHub Repository"
                    description="Source code and issue tracking"
                    href="https://github.com/yourusername/autopilot-monitor"
                  />
                </div>
              </div>
            </div>
          )}

          {/* Sessions List */}
          {sessions.length > 0 && (
            <div className="mt-8 bg-white shadow rounded-lg p-6">
              <div className="flex items-center justify-between mb-4">
                <h2 className="text-xl font-semibold text-gray-900">
                  Sessions ({sessions.length})
                  {filteredSessions.length !== sessions.length && (
                    <span className="text-sm text-gray-500 ml-2">
                      ({filteredSessions.length} filtered)
                    </span>
                  )}
                </h2>
              </div>

              {/* Search Input */}
              <div className="mb-4 relative">
                <input
                  type="text"
                  placeholder="Search by device, serial, model, status, session ID, or duration (e.g., >30 for >30min)"
                  value={searchQuery}
                  onChange={(e) => setSearchQuery(e.target.value)}
                  className="w-full px-4 py-2 pr-10 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                />
                {searchQuery && (
                  <button
                    onClick={() => setSearchQuery("")}
                    className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
                    title="Clear search"
                  >
                    <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                    </svg>
                  </button>
                )}
              </div>

              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <SortableHeader column="deviceName" currentSort={sortColumn} direction={sortDirection} onSort={handleSort}>
                        Device
                      </SortableHeader>
                      {galacticAdminMode && (
                        <th scope="col" className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Tenant ID
                        </th>
                      )}
                      <SortableHeader column="model" currentSort={sortColumn} direction={sortDirection} onSort={handleSort}>
                        Model
                      </SortableHeader>
                      <SortableHeader column="status" currentSort={sortColumn} direction={sortDirection} onSort={handleSort}>
                        Status
                      </SortableHeader>
                      <SortableHeader column="eventCount" currentSort={sortColumn} direction={sortDirection} onSort={handleSort}>
                        Events
                      </SortableHeader>
                      <SortableHeader column="durationSeconds" currentSort={sortColumn} direction={sortDirection} onSort={handleSort}>
                        Duration
                      </SortableHeader>
                      <SortableHeader column="startedAt" currentSort={sortColumn} direction={sortDirection} onSort={handleSort}>
                        Started
                      </SortableHeader>
                      {adminMode && (
                        <th scope="col" className="px-6 py-3 text-right text-xs font-medium text-gray-500 uppercase tracking-wider">
                          Actions
                        </th>
                      )}
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {paginatedSessions.length === 0 ? (
                      <tr>
                        <td colSpan={(galacticAdminMode ? 1 : 0) + (adminMode ? 7 : 6)} className="px-6 py-8 text-center text-gray-500">
                          No sessions found matching your search.
                        </td>
                      </tr>
                    ) : (
                      paginatedSessions.map((session) => (
                      <tr
                        key={session.sessionId}
                        onClick={() => router.push(`/sessions/${session.sessionId}`)}
                        className="hover:bg-gray-50 cursor-pointer transition-colors"
                      >
                        <td className="px-6 py-4 whitespace-nowrap">
                          <div className="text-sm font-medium text-gray-900">
                            {session.deviceName || session.serialNumber}
                          </div>
                          <div className="text-sm text-gray-500">
                            {session.serialNumber}
                          </div>
                        </td>
                        {galacticAdminMode && (
                          <td className="px-6 py-4 whitespace-nowrap">
                            <div className="text-xs font-mono text-gray-600">
                              {session.tenantId}
                            </div>
                          </td>
                        )}
                        <td className="px-6 py-4 whitespace-nowrap">
                          <div className="text-sm text-gray-900">
                            {session.manufacturer} {session.model}
                          </div>
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap">
                          <StatusBadge status={session.status} failureReason={session.failureReason} />
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {session.eventCount}
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {Math.round(session.durationSeconds / 60)} min
                        </td>
                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-500">
                          {new Date(session.startedAt).toLocaleString()}
                        </td>
                        {adminMode && (
                          <td className="px-6 py-4 whitespace-nowrap text-right text-sm font-medium">
                            <button
                              onClick={(e) => {
                                e.stopPropagation(); // Prevent row click navigation
                                deleteSession(session.sessionId, session.tenantId, session.deviceName || session.serialNumber);
                              }}
                              className="text-red-600 hover:text-red-900 transition-colors"
                              title="Session löschen"
                            >
                              <svg className="h-5 w-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" />
                              </svg>
                            </button>
                          </td>
                        )}
                      </tr>
                      ))
                    )}
                  </tbody>
                </table>
              </div>

              {/* Pagination Controls */}
              {totalPages > 1 && (
                <div className="mt-4 flex items-center justify-between">
                  <div className="text-sm text-gray-700">
                    Page {currentPage} of {totalPages} ({sortedSessions.length} total sessions)
                  </div>
                  <div className="flex gap-2">
                    <button
                      onClick={handlePreviousPage}
                      disabled={currentPage === 1}
                      className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      ← Previous
                    </button>
                    <button
                      onClick={handleNextPage}
                      disabled={currentPage === totalPages}
                      className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                    >
                      Next →
                    </button>
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      </main>

      {/* Delete Confirmation Modal */}
      {showDeleteConfirm && sessionToDelete && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={cancelDelete}>
          <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4" onClick={(e) => e.stopPropagation()}>
            <div className="p-6">
              <div className="flex items-center mb-4">
                <div className="flex-shrink-0 w-12 h-12 bg-red-100 rounded-full flex items-center justify-center">
                  <svg className="w-6 h-6 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                  </svg>
                </div>
                <h3 className="ml-4 text-lg font-semibold text-gray-900">Delete Session</h3>
              </div>

              <div className="mb-6">
                <p className="text-sm text-gray-700 mb-2">
                  This action is <span className="font-semibold text-red-600">irreversible</span>!
                </p>
                <p className="text-sm text-gray-700 mb-2">
                  The session <span className="font-mono text-xs">{sessionToDelete.sessionId}</span> for device <span className="font-semibold">{sessionToDelete.deviceName || 'Unknown'}</span> and all associated events will be permanently deleted.
                </p>
                <p className="text-sm text-gray-600">
                  Do you want to continue?
                </p>
              </div>

              <div className="flex justify-end gap-3">
                <button
                  onClick={cancelDelete}
                  className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors"
                >
                  Cancel
                </button>
                <button
                  onClick={confirmDelete}
                  className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors"
                >
                  Delete
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
    </ProtectedRoute>
  );
}

function StatsCard({
  title,
  value,
  description,
  color,
}: {
  title: string;
  value: string;
  description: string;
  color: "blue" | "green" | "purple" | "red";
}) {
  const colorClasses = {
    blue: "bg-blue-500",
    green: "bg-green-500",
    purple: "bg-purple-500",
    red: "bg-red-500",
  };

  return (
    <div className="bg-white overflow-hidden shadow rounded-lg">
      <div className="p-5">
        <div className="flex items-center">
          <div className="flex-shrink-0">
            <div className={`${colorClasses[color]} rounded-md p-3`}>
              <svg className="h-6 w-6 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
              </svg>
            </div>
          </div>
          <div className="ml-5 w-0 flex-1">
            <dl>
              <dt className="text-sm font-medium text-gray-500 truncate">{title}</dt>
              <dd className="flex items-baseline">
                <div className="text-2xl font-semibold text-gray-900">{value}</div>
              </dd>
            </dl>
          </div>
        </div>
        <div className="mt-2">
          <div className="text-sm text-gray-500">{description}</div>
        </div>
      </div>
    </div>
  );
}

function QuickLink({
  title,
  description,
  href,
}: {
  title: string;
  description: string;
  href: string;
}) {
  return (
    <a
      href={href}
      className="block p-4 bg-gray-50 rounded-lg hover:bg-gray-100 transition-colors"
    >
      <h4 className="text-sm font-medium text-gray-900">{title}</h4>
      <p className="mt-1 text-sm text-gray-500">{description}</p>
    </a>
  );
}

function SortableHeader({
  column,
  currentSort,
  direction,
  onSort,
  children,
}: {
  column: keyof Session;
  currentSort: keyof Session | null;
  direction: "asc" | "desc";
  onSort: (column: keyof Session) => void;
  children: React.ReactNode;
}) {
  const isActive = currentSort === column;

  return (
    <th
      onClick={() => onSort(column)}
      className="px-6 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider cursor-pointer hover:bg-gray-100 transition-colors select-none"
    >
      <div className="flex items-center gap-2">
        {children}
        <span className="text-gray-400">
          {isActive ? (direction === "asc" ? "↑" : "↓") : "↕"}
        </span>
      </div>
    </th>
  );
}

function StatusBadge({ status, failureReason }: { status: string; failureReason?: string }) {
  const statusConfig = {
    InProgress: { color: "bg-blue-100 text-blue-800", text: "In Progress" },
    Succeeded: { color: "bg-green-100 text-green-800", text: "Succeeded" },
    Failed: { color: "bg-red-100 text-red-800", text: "Failed" },
    Unknown: { color: "bg-gray-100 text-gray-800", text: "Unknown" },
  };

  const config = statusConfig[status as keyof typeof statusConfig] || statusConfig.Unknown;

  // Check if this is a timeout failure
  const isTimeout = status === "Failed" && failureReason && failureReason.toLowerCase().includes("timed out");

  return (
    <span
      className={`px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full ${config.color}`}
      title={failureReason || undefined}
    >
      {config.text}
      {isTimeout && (
        <span title={failureReason} className="inline-flex items-center">
          ⏱️
        </span>
      )}
    </span>
  );
}

function SettingsMenu({
  apiStatus,
  onCheckHealth,
  adminMode,
  onAdminModeChange,
  galacticAdminMode,
  onGalacticAdminModeChange,
  user,
}: {
  apiStatus: "unchecked" | "checking" | "healthy" | "error";
  onCheckHealth: () => void;
  adminMode: boolean;
  onAdminModeChange: (enabled: boolean) => void;
  galacticAdminMode: boolean;
  onGalacticAdminModeChange: (enabled: boolean) => void;
  user: { displayName: string; email: string; isGalacticAdmin?: boolean } | null;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const [showAuditLog, setShowAuditLog] = useState(false);
  const [auditLogs, setAuditLogs] = useState<any[]>([]);
  const [loadingLogs, setLoadingLogs] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth(); // Add this line to get getAccessToken

  const fetchAuditLogs = async () => {
    setLoadingLogs(true);
    try {
      // Get access token and include Authorization header
      const token = await getAccessToken();

      if (!token) {
        console.error('Failed to get access token for audit logs');
        return;
      }

      const response = await fetch(`${API_BASE_URL}/api/audit/logs?tenantId=${tenantId}`, {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });

      if (response.ok) {
        const data = await response.json();
        setAuditLogs(data.logs || []);
      }
    } catch (error) {
      console.error("Failed to fetch audit logs:", error);
    } finally {
      setLoadingLogs(false);
    }
  };

  const statusColors = {
    unchecked: "text-gray-500",
    checking: "text-yellow-600",
    healthy: "text-green-600",
    error: "text-red-600",
  };

  const statusIcons = {
    unchecked: "🔘",
    checking: "🔄",
    healthy: "✅",
    error: "❌",
  };

  const statusText = {
    unchecked: "Not checked",
    checking: "Checking...",
    healthy: "Connected",
    error: "Not connected",
  };

  // Close dropdown when clicking outside
  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        setIsOpen(false);
      }
    }

    if (isOpen) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => {
        document.removeEventListener("mousedown", handleClickOutside);
      };
    }
  }, [isOpen]);

  return (
    <div className="relative" ref={menuRef}>
      {/* Settings Button */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="p-2 rounded-full hover:bg-gray-100 transition-colors"
        aria-label="Settings"
        title="Settings"
      >
        <svg className="h-5 w-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
      </button>

      {/* Dropdown Menu */}
      {isOpen && (
        <div className="absolute right-0 mt-2 w-72 bg-white rounded-lg shadow-lg border border-gray-200 z-50">
          <div className="p-4">
            <h3 className="text-sm font-semibold text-gray-900 mb-3">Settings</h3>

            {/* Admin Mode Toggle */}
            <div className="mb-3">
              <div className="flex items-center justify-between p-3 rounded-lg bg-gray-50">
                <div className="flex items-center gap-2">
                  <span className="text-sm font-medium text-gray-700">Admin Mode</span>
                  {adminMode && <span className="text-xs text-red-600 font-semibold">AKTIV</span>}
                </div>
                <button
                  onClick={() => onAdminModeChange(!adminMode)}
                  className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                    adminMode ? 'bg-red-600' : 'bg-gray-300'
                  }`}
                >
                  <span
                    className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                      adminMode ? 'translate-x-6' : 'translate-x-1'
                    }`}
                  />
                </button>
              </div>
              {adminMode && (
                <p className="mt-2 text-xs text-red-600 px-3 whitespace-nowrap">
                  ⚠️ Allows deleting sessions
                </p>
              )}
            </div>

            {/* Galactic Admin Toggle - Only visible to actual galactic admins */}
            {user?.isGalacticAdmin && (
              <div className="mb-3">
                <div className="flex items-center justify-between p-3 rounded-lg bg-purple-50">
                  <div className="flex items-center gap-2">
                    <span className="text-sm font-medium text-gray-700">Galactic Admin</span>
                    {galacticAdminMode && <span className="text-xs text-purple-700 font-semibold">ACTIVE</span>}
                  </div>
                  <button
                    onClick={() => onGalacticAdminModeChange(!galacticAdminMode)}
                    className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                      galacticAdminMode ? 'bg-purple-600' : 'bg-gray-300'
                    }`}
                  >
                    <span
                      className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                        galacticAdminMode ? 'translate-x-6' : 'translate-x-1'
                      }`}
                    />
                  </button>
                </div>
                {galacticAdminMode && (
                  <p className="mt-2 text-xs text-purple-700 px-3">
                    Shows ALL sessions across ALL tenants
                  </p>
                )}
              </div>
            )}

            {/* API Status Section - Clickable */}
            <div className="border-t border-gray-200 pt-3">
              <button
                onClick={onCheckHealth}
                disabled={apiStatus === "checking"}
                className="w-full p-3 text-left rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                title={apiStatus === "unchecked" ? "Click to check API status" : undefined}
              >
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-gray-700">API Status</span>
                  <span className={`text-sm font-medium ${statusColors[apiStatus]}`}>
                    {statusIcons[apiStatus]} {statusText[apiStatus]}
                  </span>
                </div>
              </button>
            </div>

            {/* Configuration Section */}
            <div className="border-t border-gray-200 pt-3 mt-3">
              <a
                href="/settings"
                className="block w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors"
              >
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-gray-700">Configuration</span>
                  <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
                  </svg>
                </div>
              </a>
            </div>

            {/* Usage Metrics Section - Always visible for tenant */}
            <div className="border-t border-gray-200 pt-3">
              <a
                href="/usage-metrics"
                className="block w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors"
              >
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-gray-700">Usage Metrics</span>
                  <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                  </svg>
                </div>
              </a>
            </div>

            {/* Platform Usage Metrics Section - Galactic Admin Only */}
            {galacticAdminMode && (
              <div className="border-t border-gray-200 pt-3">
                <a
                  href="/platform-usage-metrics"
                  className="block w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors"
                >
                  <div className="flex items-center justify-between">
                    <span className="text-sm font-medium text-gray-700">Platform Usage Metrics</span>
                    <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                  </div>
                </a>
              </div>
            )}

            {/* Audit Log Section */}
            <div className="border-t border-gray-200 pt-3">
              <button
                onClick={() => {
                  setShowAuditLog(true);
                  fetchAuditLogs();
                }}
                className="w-full p-3 text-left rounded-lg hover:bg-gray-50 transition-colors"
              >
                <div className="flex items-center justify-between">
                  <span className="text-sm font-medium text-gray-700">Audit Log</span>
                  <svg className="h-5 w-5 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                  </svg>
                </div>
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Audit Log Modal */}
      {showAuditLog && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-[60]" onClick={() => setShowAuditLog(false)}>
          <div className="bg-white rounded-lg shadow-xl max-w-4xl w-full max-h-[80vh] overflow-hidden" onClick={(e) => e.stopPropagation()}>
            <div className="p-6 border-b border-gray-200">
              <div className="flex items-center justify-between">
                <h2 className="text-xl font-semibold text-gray-900">Audit Log</h2>
                <button
                  onClick={() => setShowAuditLog(false)}
                  className="text-gray-400 hover:text-gray-600"
                >
                  <svg className="h-6 w-6" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              </div>
            </div>
            <div className="p-6 overflow-y-auto max-h-[60vh]">
              {loadingLogs ? (
                <div className="text-center py-8 text-gray-500">Lade Audit Logs...</div>
              ) : auditLogs.length === 0 ? (
                <div className="text-center py-8 text-gray-500">Keine Audit Logs vorhanden</div>
              ) : (
                <div className="space-y-4">
                  {auditLogs.map((log) => (
                    <div key={log.id} className="border border-gray-200 rounded-lg p-4 hover:bg-gray-50">
                      <div className="flex items-start justify-between">
                        <div className="flex-1">
                          <div className="flex items-center gap-3">
                            <span className={`px-2 py-1 text-xs font-semibold rounded ${
                              log.action === 'DELETE' ? 'bg-red-100 text-red-800' : 'bg-blue-100 text-blue-800'
                            }`}>
                              {log.action}
                            </span>
                            <span className="text-sm font-medium text-gray-900">{log.entityType}</span>
                            <span className="text-sm text-gray-500 font-mono">{log.entityId}</span>
                          </div>
                          <div className="mt-2 text-sm text-gray-600">
                            von <span className="font-medium">{log.performedBy}</span>
                          </div>
                          {log.details && log.details !== '{}' && (
                            <div className="mt-2 text-xs text-gray-500 bg-gray-50 p-2 rounded">
                              {log.details}
                            </div>
                          )}
                        </div>
                        <div className="text-xs text-gray-500 text-right">
                          {new Date(log.timestamp).toLocaleString('de-DE')}
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function UserMenu({
  username,
  email,
  onLogout,
}: {
  username: string;
  email: string;
  onLogout: () => void;
}) {
  const [isOpen, setIsOpen] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  // Get initials from username
  const getInitials = (name: string) => {
    return name
      .split(" ")
      .map(n => n[0])
      .join("")
      .toUpperCase()
      .slice(0, 2);
  };

  // Close dropdown when clicking outside
  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(event.target as Node)) {
        setIsOpen(false);
      }
    }

    if (isOpen) {
      document.addEventListener("mousedown", handleClickOutside);
      return () => {
        document.removeEventListener("mousedown", handleClickOutside);
      };
    }
  }, [isOpen]);

  return (
    <div className="relative" ref={menuRef}>
      {/* User Avatar Button */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="flex items-center gap-2 p-1 rounded-full hover:bg-gray-100 transition-colors"
        aria-label="User menu"
      >
        <div className="w-8 h-8 rounded-full bg-blue-600 flex items-center justify-center text-white text-sm font-medium">
          {getInitials(username)}
        </div>
      </button>

      {/* Dropdown Menu */}
      {isOpen && (
        <div className="absolute right-0 mt-2 w-80 bg-white rounded-lg shadow-lg border border-gray-200 z-50">
          <div className="p-4">
            {/* User Info */}
            <div className="flex items-center gap-3 pb-3 border-b border-gray-200">
              <div className="w-12 h-12 rounded-full bg-blue-600 flex items-center justify-center text-white text-lg font-medium">
                {getInitials(username)}
              </div>
              <div className="flex-1 min-w-0">
                <div className="text-sm font-semibold text-gray-900 truncate">
                  {username}
                </div>
                <div className="text-xs text-gray-500 truncate">
                  {email}
                </div>
                <button className="text-xs text-blue-600 hover:text-blue-800 mt-1">
                  View account
                </button>
              </div>
            </div>

            {/* Sign out */}
            <div className="pt-3">
              <button
                onClick={onLogout}
                className="w-full text-left px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 rounded-lg transition-colors"
              >
                Sign out
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
