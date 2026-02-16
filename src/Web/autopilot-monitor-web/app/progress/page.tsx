"use client";

import { useState, useEffect, useRef, useMemo } from "react";
import { API_BASE_URL } from "@/lib/config";
import { useTenant } from "../../contexts/TenantContext";
import { useAuth } from "../../contexts/AuthContext";
import { useSignalR } from "../../contexts/SignalRContext";
import { ProtectedRoute } from "../../components/ProtectedRoute";

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

interface EnrollmentEvent {
  eventId: string;
  sessionId: string;
  timestamp: string;
  eventType: string;
  phase: number;
  message: string;
  sequence: number;
  data?: Record<string, any>;
}

const phaseSteps = [
  { id: 0, label: "Setup start", shortLabel: "Start" },
  { id: 1, label: "Device preparation", shortLabel: "Preparation" },
  { id: 2, label: "Device setup", shortLabel: "Device" },
  { id: 3, label: "Installing apps (device)", shortLabel: "Apps (D)" },
  { id: 4, label: "Account setup", shortLabel: "Account" },
  { id: 5, label: "Installing apps (user)", shortLabel: "Apps (U)" },
  { id: 6, label: "Finalizing setup", shortLabel: "Complete" },
];

export default function ProgressPortalPage() {
  const [serialInput, setSerialInput] = useState("");
  const [session, setSession] = useState<Session | null>(null);
  const [allSessions, setAllSessions] = useState<Session[]>([]);
  const [searching, setSearching] = useState(false);
  const [searched, setSearched] = useState(false);
  const [notFound, setNotFound] = useState(false);
  const [headerCollapsed, setHeaderCollapsed] = useState(false);
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);

  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();

  const hasJoinedTenantGroup = useRef(false);
  const hasJoinedSessionGroup = useRef(false);
  const sessionRef = useRef<Session | null>(null);

  // Keep ref in sync
  useEffect(() => {
    sessionRef.current = session;
  }, [session]);

  // Join tenant group for real-time updates
  useEffect(() => {
    if (isConnected && !hasJoinedTenantGroup.current) {
      joinGroup(`tenant-${tenantId}`);
      hasJoinedTenantGroup.current = true;
    }
    return () => {
      if (hasJoinedTenantGroup.current) {
        leaveGroup(`tenant-${tenantId}`);
        hasJoinedTenantGroup.current = false;
      }
    };
  }, [isConnected, tenantId]);

  // Join session-specific group when session is found
  useEffect(() => {
    if (!isConnected || !session) {
      return;
    }

    const sessionGroup = `session-${session.tenantId}-${session.sessionId}`;
    console.log('[Progress] Joining session group:', sessionGroup);
    joinGroup(sessionGroup);
    hasJoinedSessionGroup.current = true;

    return () => {
      console.log('[Progress] Leaving session group:', sessionGroup);
      leaveGroup(sessionGroup);
      hasJoinedSessionGroup.current = false;
    };
  }, [isConnected, session?.sessionId, session?.tenantId, joinGroup, leaveGroup]);

  // Listen for real-time session updates
  useEffect(() => {
    const handleNewEvents = (data: { session: Session; events?: EnrollmentEvent[] }) => {
      console.log('[Progress] Received newevents:', data);
      if (
        data.session &&
        sessionRef.current &&
        data.session.sessionId === sessionRef.current.sessionId
      ) {
        console.log('[Progress] Updating session from newevents');
        setSession(data.session);

        // Add new events to the events list
        if (data.events && data.events.length > 0) {
          console.log('[Progress] Adding', data.events.length, 'new events from newevents');
          setEvents((prev) => {
            const existingIds = new Set(prev.map((e) => e.eventId));
            const newEvents = data.events!.filter((e) => !existingIds.has(e.eventId));
            if (newEvents.length === 0) return prev;
            return [...prev, ...newEvents].sort((a, b) => a.sequence - b.sequence);
          });
        }
      }
    };

    const handleEventStream = (data: { session: Session; events?: EnrollmentEvent[] }) => {
      console.log('[Progress] Received eventStream:', data);
      if (
        data.session &&
        sessionRef.current &&
        data.session.sessionId === sessionRef.current.sessionId
      ) {
        console.log('[Progress] Updating session from eventStream');
        console.log('[Progress] Session currentPhase:', data.session.currentPhase, 'status:', data.session.status);
        setSession(data.session);

        // Add new events to the events list
        if (data.events && data.events.length > 0) {
          console.log('[Progress] Adding', data.events.length, 'new events from eventStream');
          console.log('[Progress] Event types:', data.events.map(e => e.eventType).join(', '));
          setEvents((prev) => {
            const existingIds = new Set(prev.map((e) => e.eventId));
            const newEvents = data.events!.filter((e) => !existingIds.has(e.eventId));
            if (newEvents.length === 0) return prev;
            console.log('[Progress] Actually adding', newEvents.length, 'new events');
            console.log('[Progress] Total events after update:', prev.length + newEvents.length);
            return [...prev, ...newEvents].sort((a, b) => a.sequence - b.sequence);
          });
        }
      }
    };

    on("newevents", handleNewEvents);
    on("newSession", handleNewEvents);
    on("eventStream", handleEventStream);
    return () => {
      off("newevents", handleNewEvents);
      off("newSession", handleNewEvents);
      off("eventStream", handleEventStream);
    };
  }, [on, off]);

  // Fetch events when a session is found
  const lastFetchedSessionId = useRef<string | null>(null);
  useEffect(() => {
    if (!session) return;
    if (lastFetchedSessionId.current === session.sessionId) return;
    lastFetchedSessionId.current = session.sessionId;
    const fetchEvents = async () => {
      try {
        const token = await getAccessToken();
        if (!token) return;
        const response = await fetch(
          `${API_BASE_URL}/api/sessions/${session.sessionId}/events?tenantId=${tenantId}`,
          { headers: { Authorization: `Bearer ${token}` } }
        );
        if (response.ok) {
          const data = await response.json();
          const fetched: EnrollmentEvent[] = data.events || [];
          setEvents((prev) => {
            if (prev.length === 0) return fetched;
            const existingIds = new Set(prev.map((e) => e.eventId));
            const newEvents = fetched.filter((e) => !existingIds.has(e.eventId));
            if (newEvents.length === 0) return prev;
            return [...prev, ...newEvents].sort(
              (a, b) => a.sequence - b.sequence
            );
          });
        }
      } catch (error) {
        console.error("Failed to fetch events:", error);
      }
    };
    fetchEvents();
  }, [session?.sessionId]);

  const searchBySerial = async () => {
    if (!serialInput.trim()) return;

    setSearching(true);
    setSearched(true);
    setNotFound(false);
    setSession(null);
    setEvents([]);
    lastFetchedSessionId.current = null;

    try {
      const token = await getAccessToken();
      if (!token) {
        setSearching(false);
        return;
      }

      const response = await fetch(
        `${API_BASE_URL}/api/sessions?tenantId=${tenantId}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );

      if (response.ok) {
        const data = await response.json();
        const sessions: Session[] = data.sessions || [];
        setAllSessions(sessions);

        // Find session by serial number (case-insensitive)
        const query = serialInput.trim().toLowerCase();
        const found = sessions
          .filter(
            (s) =>
              s.serialNumber.toLowerCase() === query ||
              s.serialNumber.toLowerCase().includes(query) ||
              s.deviceName?.toLowerCase().includes(query)
          )
          .sort(
            (a, b) =>
              new Date(b.startedAt).getTime() -
              new Date(a.startedAt).getTime()
          )[0];

        if (found) {
          setSession(found);
          setHeaderCollapsed(true);
        } else {
          setNotFound(true);
        }
      }
    } catch (error) {
      console.error("Search failed:", error);
      setNotFound(true);
    } finally {
      setSearching(false);
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") searchBySerial();
  };

  // Derive app installation progress from events
  const appProgress = useMemo(() => {
    // Check esp_ui_state events for app progress
    const espEvents = events.filter((e) => e.eventType === "esp_ui_state");
    if (espEvents.length > 0) {
      const latest = espEvents[espEvents.length - 1];
      const d = latest.data;
      if (d) {
        const completed = parseInt(
          d.blocking_apps_completed ?? d.blockingAppsCompleted ?? "0",
          10
        );
        const total = parseInt(
          d.blocking_apps_total ?? d.blockingAppsTotal ?? "0",
          10
        );
        const currentItem =
          d.current_item ?? d.currentItem ?? d.status_text ?? d.statusText ?? null;
        if (total > 0) return { completed, total, currentItem };
      }
    }

    // Check download_progress events for current app name
    const downloadEvents = events.filter((e) => e.eventType === "download_progress");
    if (downloadEvents.length > 0) {
      const latest = downloadEvents[downloadEvents.length - 1];
      const d = latest.data;
      if (d) {
        const appName = d.app_name ?? d.appName ?? null;
        const status = d.status ?? "";
        const isComplete = status === "completed" || status === "failed" || d.isCompleted === true || d.is_completed === true;
        if (appName && !isComplete) {
          return { completed: 0, total: 0, currentItem: appName };
        }
      }
    }

    return null;
  }, [events]);

  // Derive current active download for inline display
  const currentDownload = useMemo(() => {
    const downloadEvents = events.filter((e) => e.eventType === "download_progress");
    if (downloadEvents.length === 0) return null;

    // Build latest state per app (last event wins)
    const appLatest = new Map<string, { bytesDownloaded: number; bytesTotal: number; downloadRateBps: number; isComplete: boolean }>();
    for (const evt of downloadEvents) {
      const d = evt.data;
      if (!d) continue;
      const appName = d.app_name ?? d.appName ?? d.file_name ?? d.fileName ?? null;
      if (!appName) continue;
      const bytesDownloaded = Number(d.bytes_downloaded ?? d.bytesDownloaded ?? 0);
      const bytesTotal = Number(d.bytes_total ?? d.bytesTotal ?? 0);
      const downloadRateBps = Number(d.download_rate_bps ?? d.downloadRateBps ?? 0);
      const status = d.status ?? "";
      // Only mark complete via explicit status — don't infer from bytes (100% downloads are still active until status arrives)
      const isComplete = status === "completed" || status === "failed" || d.isCompleted === true || d.is_completed === true;
      appLatest.set(appName, {
        bytesDownloaded: isNaN(bytesDownloaded) ? 0 : bytesDownloaded,
        bytesTotal: isNaN(bytesTotal) ? 0 : bytesTotal,
        downloadRateBps: isNaN(downloadRateBps) ? 0 : downloadRateBps,
        isComplete,
      });
    }

    const completedCount = Array.from(appLatest.values()).filter((v) => v.isComplete).length;

    // Walk events in reverse to find the most recently seen active app
    for (let i = downloadEvents.length - 1; i >= 0; i--) {
      const d = downloadEvents[i].data;
      if (!d) continue;
      const appName = d.app_name ?? d.appName ?? d.file_name ?? d.fileName ?? null;
      if (!appName) continue;
      const latest = appLatest.get(appName);
      if (latest && !latest.isComplete) {
        return { appName, ...latest, completedCount, active: true };
      }
    }

    // No active download — return just the counter so it stays visible
    return { appName: null, bytesDownloaded: 0, bytesTotal: 0, downloadRateBps: 0, isComplete: true, completedCount, active: false };
  }, [events]);

  // Derive progress
  const overallProgress = session
    ? session.status === "Succeeded"
      ? 100
      : session.status === "Failed"
      ? Math.min(
          100,
          ((session.currentPhase === 99
            ? 3
            : session.currentPhase) /
            6) *
            100
        )
      : Math.min(100, (session.currentPhase / 6) * 100)
    : 0;

  const estimatedRemaining = session
    ? (() => {
        if (session.status !== "InProgress") return null;
        const currentPhase = Math.min(session.currentPhase, 6);
        if (currentPhase === 0) return null;
        const elapsed = session.durationSeconds;
        const rate = elapsed / currentPhase;
        const remaining = (6 - currentPhase) * rate;
        return Math.round(remaining / 60);
      })()
    : null;

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gradient-to-b from-blue-50 to-white">
        <div className="max-w-2xl mx-auto px-4 py-6 sm:py-12">
          {/* Collapsible Header + Search */}
          {headerCollapsed && session ? (
            <button
              onClick={() => setHeaderCollapsed(false)}
              className="w-full flex items-center justify-between bg-white rounded-xl shadow-sm border border-gray-200 px-4 py-2.5 mb-4 hover:bg-gray-50 transition-colors"
            >
              <div className="flex items-center space-x-2 min-w-0">
                <svg
                  className="w-4 h-4 text-blue-600 flex-shrink-0"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
                  />
                </svg>
                <span className="text-sm font-medium text-gray-700">
                  Device Setup Progress
                </span>
              </div>
              <div className="flex items-center space-x-1.5 flex-shrink-0 ml-3">
                <span className="text-xs text-blue-600 font-medium">Change device</span>
                <svg
                  className="w-3.5 h-3.5 text-blue-600"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M19 9l-7 7-7-7"
                  />
                </svg>
              </div>
            </button>
          ) : (
            <>
              {/* Full Header */}
              <div className="text-center mb-10">
                {session && (
                  <button
                    onClick={() => setHeaderCollapsed(true)}
                    className="mb-2 text-xs text-gray-400 hover:text-gray-600 transition-colors flex items-center justify-center mx-auto space-x-1"
                  >
                    <svg
                      className="w-3 h-3"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M5 15l7-7 7 7"
                      />
                    </svg>
                    <span>Collapse</span>
                  </button>
                )}
                <div className="inline-flex items-center justify-center w-16 h-16 bg-blue-100 rounded-full mb-4">
                  <svg
                    className="w-8 h-8 text-blue-600"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z"
                    />
                  </svg>
                </div>
                <h1 className="text-3xl font-bold text-gray-900 mb-2">
                  Device Setup Progress
                </h1>
                <p className="text-gray-500">
                  Enter your device serial number to check status
                </p>
              </div>

              {/* Search */}
              <div className="flex items-center space-x-3 mb-10">
                <div className="flex-1 relative">
                  <input
                    type="text"
                    value={serialInput}
                    onChange={(e) => setSerialInput(e.target.value)}
                    onKeyDown={handleKeyDown}
                    placeholder="Enter serial number or device name..."
                    className="w-full px-4 py-3 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 text-lg"
                  />
                </div>
                <button
                  onClick={searchBySerial}
                  disabled={searching || !serialInput.trim()}
                  className="px-6 py-3 bg-blue-600 text-white rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50 disabled:cursor-not-allowed font-medium"
                >
                  {searching ? "Searching..." : "Check Status"}
                </button>
              </div>
            </>
          )}

          {/* Not Found */}
          {notFound && (
            <div className="bg-white rounded-xl shadow-sm border border-gray-200 p-8 text-center">
              <svg
                className="w-12 h-12 mx-auto text-gray-300 mb-4"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeWidth={1.5}
                  d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z"
                />
              </svg>
              <h2 className="text-lg font-semibold text-gray-900 mb-2">
                Device Not Found
              </h2>
              <p className="text-gray-500 text-sm">
                No enrollment session found for &quot;{serialInput}&quot;.
                Please check the serial number and try again.
              </p>
            </div>
          )}

          {/* Session Found - Progress Display */}
          {session && (
            <div className="bg-white rounded-xl shadow-sm border border-gray-200 overflow-hidden">
              {/* Status Header */}
              <div
                className={`px-6 py-4 ${
                  session.status === "InProgress"
                    ? "bg-blue-50 border-b border-blue-100"
                    : session.status === "Succeeded"
                    ? "bg-green-50 border-b border-green-100"
                    : "bg-red-50 border-b border-red-100"
                }`}
              >
                <div className="text-center">
                  <h2
                    className={`text-xl font-semibold ${
                      session.status === "InProgress"
                        ? "text-blue-800"
                        : session.status === "Succeeded"
                        ? "text-green-800"
                        : "text-red-800"
                    }`}
                  >
                    {session.status === "InProgress"
                      ? "Setting up your device..."
                      : session.status === "Succeeded"
                      ? "Setup complete!"
                      : "Setup encountered an issue"}
                  </h2>
                  <p className="text-sm text-gray-500 mt-1">
                    {session.deviceName || session.serialNumber} |{" "}
                    {session.manufacturer} {session.model}
                  </p>
                </div>
              </div>

              <div className="p-6">
                {/* Overall Progress Bar */}
                <div className="mb-8">
                  <div className="flex items-center justify-between mb-2">
                    <span className="text-sm text-gray-500">
                      Overall Progress
                    </span>
                    <span
                      className={`text-sm font-semibold ${
                        session.status === "Failed"
                          ? "text-red-600"
                          : "text-blue-600"
                      }`}
                    >
                      {Math.round(overallProgress)}%
                    </span>
                  </div>
                  <div className="w-full h-4 bg-gray-100 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all duration-1000 ${
                        session.status === "Failed"
                          ? "bg-red-500"
                          : session.status === "Succeeded"
                          ? "bg-green-500"
                          : "bg-blue-500"
                      }`}
                      style={{ width: `${overallProgress}%` }}
                    />
                  </div>
                </div>

                {/* Phase Steps */}
                <div className="space-y-3 mb-8">
                  {phaseSteps.map((step) => {
                    const effectivePhase =
                      session.currentPhase === 99
                        ? 3
                        : session.currentPhase;
                    const isCompleted =
                      (session.status === "Succeeded" && step.id <= 6) ||
                      step.id < effectivePhase;
                    const isCurrent =
                      step.id === effectivePhase &&
                      session.status === "InProgress";
                    const isFailed =
                      step.id === effectivePhase &&
                      session.status === "Failed";

                    return (
                      <div key={step.id}>
                        <div className="flex items-center space-x-3">
                          {/* Icon */}
                          <div className="flex-shrink-0">
                            {isCompleted ? (
                              <div className="w-8 h-8 rounded-full bg-green-100 flex items-center justify-center">
                                <svg
                                  className="w-5 h-5 text-green-600"
                                  fill="none"
                                  viewBox="0 0 24 24"
                                  stroke="currentColor"
                                >
                                  <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    strokeWidth={3}
                                    d="M5 13l4 4L19 7"
                                  />
                                </svg>
                              </div>
                            ) : isCurrent ? (
                              <div className="w-8 h-8 rounded-full bg-blue-100 flex items-center justify-center">
                                <div className="w-3 h-3 bg-blue-500 rounded-full animate-pulse" />
                              </div>
                            ) : isFailed ? (
                              <div className="w-8 h-8 rounded-full bg-red-100 flex items-center justify-center">
                                <svg
                                  className="w-5 h-5 text-red-600"
                                  fill="none"
                                  viewBox="0 0 24 24"
                                  stroke="currentColor"
                                >
                                  <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    strokeWidth={3}
                                    d="M6 18L18 6M6 6l12 12"
                                  />
                                </svg>
                              </div>
                            ) : (
                              <div className="w-8 h-8 rounded-full bg-gray-100 flex items-center justify-center">
                                <div className="w-3 h-3 bg-gray-300 rounded-full" />
                              </div>
                            )}
                          </div>

                          {/* Label */}
                          <div className="min-w-0">
                            <span
                              className={`text-sm ${
                                isCompleted
                                  ? "text-green-700 font-medium"
                                  : isCurrent
                                  ? "text-blue-700 font-medium"
                                  : isFailed
                                  ? "text-red-700 font-medium"
                                  : "text-gray-400"
                              }`}
                            >
                              {step.label}
                              {isCurrent &&
                                (step.id === 3 || step.id === 5) &&
                                appProgress &&
                                appProgress.total > 0 &&
                                appProgress.completed > 0 &&
                                ` (${appProgress.completed}/${appProgress.total})`}
                            </span>
                            {/* App install detail below the "Installing apps" steps */}
                            {isCurrent && (step.id === 3 || step.id === 5) && (
                              <div className="flex items-center space-x-1.5 mt-0.5">
                                <div className="w-1.5 h-1.5 bg-blue-400 rounded-full animate-pulse flex-shrink-0" />
                                <span className="text-xs text-blue-500 truncate">
                                  {appProgress?.currentItem
                                    ? `${appProgress.currentItem} installing...`
                                    : "Installing applications..."}
                                </span>
                              </div>
                            )}
                          </div>
                        </div>
                      </div>
                    );
                  })}
                </div>

                {/* Estimated Time / Active Download / Status */}
                {session.status === "InProgress" && currentDownload && (
                  <div className="bg-blue-50 rounded-lg p-4">
                    {currentDownload.completedCount > 0 && (
                      <p className="text-xs text-blue-500 mb-2">
                        {currentDownload.completedCount} App installs completed
                      </p>
                    )}
                    {currentDownload.active && currentDownload.appName && (
                      <>
                        <div className="flex items-center justify-between mb-1">
                          <span className="text-sm text-blue-700 font-medium truncate pr-2">
                            {currentDownload.appName}
                          </span>
                          {currentDownload.downloadRateBps > 0 && (
                            <span className="text-xs text-blue-500 flex-shrink-0">
                              {currentDownload.downloadRateBps >= 1024 * 1024
                                ? `${(currentDownload.downloadRateBps / (1024 * 1024)).toFixed(1)} MB/s`
                                : currentDownload.downloadRateBps >= 1024
                                ? `${(currentDownload.downloadRateBps / 1024).toFixed(1)} KB/s`
                                : `${Math.round(currentDownload.downloadRateBps)} B/s`}
                            </span>
                          )}
                        </div>
                        {currentDownload.bytesTotal > 0 && (
                          <>
                            <div className="w-full h-1.5 bg-blue-200 rounded-full overflow-hidden">
                              <div
                                className="h-full bg-blue-500 rounded-full transition-all duration-500"
                                style={{ width: `${Math.min(100, (currentDownload.bytesDownloaded / currentDownload.bytesTotal) * 100)}%` }}
                              />
                            </div>
                            <div className="flex justify-between mt-1 text-xs text-blue-400">
                              <span>
                                {currentDownload.bytesDownloaded >= 1024 * 1024
                                  ? `${(currentDownload.bytesDownloaded / (1024 * 1024)).toFixed(1)} MB`
                                  : `${(currentDownload.bytesDownloaded / 1024).toFixed(0)} KB`}
                                {" / "}
                                {currentDownload.bytesTotal >= 1024 * 1024
                                  ? `${(currentDownload.bytesTotal / (1024 * 1024)).toFixed(1)} MB`
                                  : `${(currentDownload.bytesTotal / 1024).toFixed(0)} KB`}
                              </span>
                              <span>{Math.round((currentDownload.bytesDownloaded / currentDownload.bytesTotal) * 100)}%</span>
                            </div>
                          </>
                        )}
                      </>
                    )}
                  </div>
                )}

                {session.status === "InProgress" && !currentDownload && estimatedRemaining != null && estimatedRemaining > 0 && (
                  <div className="bg-blue-50 rounded-lg p-4 text-center">
                    <div className="text-sm text-blue-700">
                      Estimated time remaining:{" "}
                      <span className="font-semibold">
                        ~{estimatedRemaining} minutes
                      </span>
                    </div>
                  </div>
                )}

                {session.status === "Succeeded" && (
                  <div className="bg-green-50 rounded-lg p-4 text-center">
                    <p className="text-sm text-green-700 font-medium">
                      Your device is ready to use! Total setup time:{" "}
                      {Math.round(session.durationSeconds / 60)} minutes.
                    </p>
                    <p className="text-xs text-green-600 mt-1">
                      Completed at{" "}
                      {new Date(
                        new Date(session.startedAt).getTime() + session.durationSeconds * 1000
                      ).toLocaleString(undefined, {
                        dateStyle: "medium",
                        timeStyle: "short",
                      })}
                    </p>
                  </div>
                )}

                {session.status === "Failed" && (
                  <div className="bg-red-50 rounded-lg p-4 text-center">
                    <p className="text-sm text-red-700">
                      {session.failureReason ||
                        "Setup encountered an error. Please contact your IT department."}
                    </p>
                  </div>
                )}
              </div>
            </div>
          )}
        </div>
      </div>
    </ProtectedRoute>
  );
}
