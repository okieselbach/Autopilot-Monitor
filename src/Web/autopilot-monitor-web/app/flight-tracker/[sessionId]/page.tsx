"use client";

import { useEffect, useState, useRef, useMemo } from "react";
import { useParams, useRouter } from "next/navigation";
import { useSignalR } from "../../../contexts/SignalRContext";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { ProtectedRoute } from "../../../components/ProtectedRoute";
import { API_BASE_URL } from "@/lib/config";

interface EnrollmentEvent {
  eventId: string;
  sessionId: string;
  timestamp: string;
  eventType: string;
  severity: string;
  source: string;
  phase: number;
  phaseName?: string;
  message: string;
  sequence: number;
  data?: Record<string, any>;
}

interface RuleResult {
  resultId: string;
  ruleId: string;
  ruleTitle: string;
  severity: string;
  category: string;
  confidenceScore: number;
  explanation: string;
  remediation: { title: string; steps: string[] }[];
  relatedDocs: { title: string; url: string }[];
  matchedConditions: Record<string, any>;
  detectedAt: string;
}

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

const phaseNames: Record<number, string> = {
  0: "PreFlight",
  1: "Network",
  2: "Identity",
  3: "MDM Enrollment",
  4: "ESP Device Setup",
  5: "App Installation",
  6: "ESP User Setup",
  7: "Complete",
  99: "Failed",
};

const phaseShortNames: Record<number, string> = {
  0: "OOBE",
  1: "Join",
  2: "Identity",
  3: "MDM",
  4: "ESP Device",
  5: "Apps",
  6: "ESP User",
  7: "Complete",
};

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const sizes = ["B", "KB", "MB", "GB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / Math.pow(k, i)).toFixed(1)} ${sizes[i]}`;
}

function formatSpeed(bps: number): string {
  if (bps === 0) return "0 B/s";
  const k = 1024;
  const sizes = ["B/s", "KB/s", "MB/s", "GB/s"];
  const i = Math.floor(Math.log(bps) / Math.log(k));
  return `${(bps / Math.pow(k, i)).toFixed(1)} ${sizes[i]}`;
}

export default function FlightTrackerPage() {
  const params = useParams();
  const router = useRouter();
  const sessionId = params?.sessionId as string;
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);
  const [session, setSession] = useState<Session | null>(null);
  const [sessionTenantId, setSessionTenantId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [analysisResults, setAnalysisResults] = useState<RuleResult[]>([]);

  const refreshTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const sessionIdRef = useRef(sessionId);
  const hasInitialFetch = useRef(false);
  const lastFetchedSessionId = useRef<string | null>(null);
  const hasJoinedGroups = useRef(false);

  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();

  const [galacticAdminMode] = useState(() => {
    if (typeof window !== "undefined") {
      return localStorage.getItem("galacticAdminMode") === "true";
    }
    return false;
  });

  // Initial data fetch
  useEffect(() => {
    if (!sessionId) return;
    sessionIdRef.current = sessionId;
    if (lastFetchedSessionId.current !== sessionId) {
      hasInitialFetch.current = false;
      lastFetchedSessionId.current = sessionId;
      setSessionTenantId(null);
    }
    if (hasInitialFetch.current) return;
    hasInitialFetch.current = true;
    fetchSessionDetails();
  }, [sessionId]);

  useEffect(() => {
    if (sessionTenantId && sessionId) {
      fetchEvents();
      fetchAnalysisResults();
    }
  }, [sessionTenantId, sessionId]);

  // SignalR groups
  useEffect(() => {
    const effectiveTenantId = sessionTenantId || tenantId;
    if (!sessionId || !isConnected || !effectiveTenantId) return;
    if (!hasJoinedGroups.current) {
      joinGroup(`tenant-${effectiveTenantId}`);
      joinGroup(`session-${effectiveTenantId}-${sessionId}`);
      hasJoinedGroups.current = true;
    }
    return () => {
      if (hasJoinedGroups.current && effectiveTenantId) {
        leaveGroup(`tenant-${effectiveTenantId}`);
        leaveGroup(`session-${effectiveTenantId}-${sessionId}`);
        hasJoinedGroups.current = false;
      }
    };
  }, [sessionId, isConnected, sessionTenantId, tenantId]);

  // SignalR listeners
  useEffect(() => {
    const handleEventStream = (data: {
      sessionId: string;
      tenantId: string;
      events: EnrollmentEvent[];
      session: Session;
      newRuleResults?: RuleResult[];
    }) => {
      if (data.sessionId === sessionIdRef.current && data.events?.length > 0) {
        const eventsWithPhaseNames = data.events.map((e) => ({
          ...e,
          phaseName: phaseNames[e.phase] || "Unknown",
        }));
        setEvents((prev) => {
          const existingIds = new Set(prev.map((e) => e.eventId));
          const newEvents = eventsWithPhaseNames.filter(
            (e) => !existingIds.has(e.eventId)
          );
          return [...prev, ...newEvents].sort(
            (a, b) => a.sequence - b.sequence
          );
        });
        if (data.session) setSession(data.session);
        if (data.newRuleResults?.length) {
          setAnalysisResults((prev) => {
            const existingIds = new Set(prev.map((r) => r.ruleId));
            const newResults = data.newRuleResults!.filter(
              (r) => !existingIds.has(r.ruleId)
            );
            return [...prev, ...newResults].sort(
              (a, b) => b.confidenceScore - a.confidenceScore
            );
          });
        }
      }
    };

    const handleNewEvents = (data: {
      sessionId: string;
      tenantId: string;
      eventCount: number;
      session: Session;
    }) => {
      if (data.sessionId === sessionIdRef.current && data.session) {
        setSession(data.session);
      }
    };

    on("eventstream", handleEventStream);
    on("newevents", handleNewEvents);
    return () => {
      off("eventstream", handleEventStream);
      off("newevents", handleNewEvents);
      if (refreshTimeoutRef.current) clearTimeout(refreshTimeoutRef.current);
    };
  }, [on, off]);

  const fetchSessionDetails = async () => {
    try {
      const endpoint = galacticAdminMode
        ? `${API_BASE_URL}/api/galactic/sessions`
        : `${API_BASE_URL}/api/sessions?tenantId=${tenantId}`;
      const token = await getAccessToken();
      if (!token) return;
      const response = await fetch(endpoint, {
        headers: { Authorization: `Bearer ${token}` },
      });
      if (response.ok) {
        const data = await response.json();
        const found = data.sessions?.find(
          (s: Session) => s.sessionId === sessionId
        );
        if (found) {
          setSession(found);
          setSessionTenantId(found.tenantId);
        }
      }
    } catch (error) {
      console.error("Failed to fetch session details:", error);
    }
  };

  const fetchEvents = async () => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      const token = await getAccessToken();
      if (!token) {
        setLoading(false);
        return;
      }
      const response = await fetch(
        `${API_BASE_URL}/api/sessions/${sessionId}/events?tenantId=${effectiveTenantId}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      if (response.ok) {
        const data = await response.json();
        setEvents(
          data.events.map((e: EnrollmentEvent) => ({
            ...e,
            phaseName: phaseNames[e.phase] || "Unknown",
          }))
        );
      }
    } catch (error) {
      console.error("Failed to fetch events:", error);
    } finally {
      setLoading(false);
    }
  };

  const fetchAnalysisResults = async () => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      const token = await getAccessToken();
      if (!token) return;
      const response = await fetch(
        `${API_BASE_URL}/api/sessions/${sessionId}/analysis?tenantId=${effectiveTenantId}`,
        { headers: { Authorization: `Bearer ${token}` } }
      );
      if (response.ok) {
        const data = await response.json();
        if (data.results) {
          setAnalysisResults(
            data.results.sort(
              (a: RuleResult, b: RuleResult) =>
                b.confidenceScore - a.confidenceScore
            )
          );
        }
      }
    } catch (error) {
      console.error("Failed to fetch analysis results:", error);
    }
  };

  // Derived data
  const currentDownload = useMemo(() => {
    const downloadEvents = events.filter(
      (e) => e.eventType === "download_progress"
    );
    if (downloadEvents.length === 0) return null;

    const latest = downloadEvents[downloadEvents.length - 1];
    const d = latest.data;
    if (!d) return null;

    const appName = d.app_name ?? d.appName ?? "Unknown App";
    const bytesDownloaded = parseInt(
      d.bytes_downloaded ?? d.bytesDownloaded ?? "0",
      10
    );
    const bytesTotal = parseInt(d.bytes_total ?? d.bytesTotal ?? "0", 10);
    const downloadRateBps = parseFloat(
      d.download_rate_bps ?? d.downloadRateBps ?? "0"
    );
    const status = d.status ?? "";
    const isComplete =
      status === "completed" || (bytesTotal > 0 && bytesDownloaded >= bytesTotal);

    if (isComplete) return null;

    const progressPercent =
      bytesTotal > 0 ? Math.min(100, (bytesDownloaded / bytesTotal) * 100) : 0;
    const bytesRemaining = bytesTotal > 0 ? bytesTotal - bytesDownloaded : 0;
    const etaSeconds =
      downloadRateBps > 0 && bytesRemaining > 0
        ? bytesRemaining / downloadRateBps
        : 0;

    return {
      appName,
      bytesDownloaded,
      bytesTotal,
      downloadRateBps,
      progressPercent,
      etaSeconds,
    };
  }, [events]);

  const appProgress = useMemo(() => {
    const espEvents = events.filter((e) => e.eventType === "esp_ui_state");
    if (espEvents.length === 0) return null;
    const latest = espEvents[espEvents.length - 1];
    const d = latest.data;
    if (!d) return null;

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

    return { completed, total, currentItem };
  }, [events]);

  const recentEvents = useMemo(() => {
    return [...events]
      .sort((a, b) => b.sequence - a.sequence)
      .slice(0, 8)
      .filter((e) => e.eventType !== "performance_snapshot" && e.eventType !== "download_progress");
  }, [events]);

  const estimatedCompletion = useMemo(() => {
    if (!session || session.status !== "InProgress") return null;
    // Simple estimation based on phase progress and duration so far
    const totalPhases = 7;
    const currentPhase = Math.min(session.currentPhase, 7);
    if (currentPhase === 0) return null;

    const elapsed = session.durationSeconds;
    const ratePerPhase = elapsed / currentPhase;
    const remainingPhases = totalPhases - currentPhase;
    const estimatedRemainingSeconds = remainingPhases * ratePerPhase;

    const completionTime = new Date(
      new Date(session.startedAt).getTime() +
        (elapsed + estimatedRemainingSeconds) * 1000
    );

    return {
      time: completionTime,
      remainingMinutes: Math.round(estimatedRemainingSeconds / 60),
    };
  }, [session]);

  // Determine failure phase
  const failurePhase = useMemo(() => {
    if (!session || session.status !== "Failed" || session.currentPhase !== 99)
      return null;
    const realPhases = events
      .filter((e) => e.phase >= 0 && e.phase <= 7)
      .map((e) => e.phase);
    return realPhases.length > 0 ? Math.max(...realPhases) : 0;
  }, [session, events]);

  const effectivePhase =
    failurePhase !== null ? failurePhase : session?.currentPhase ?? 0;

  const getPhaseStatus = (phaseId: number) => {
    if (!session) return "pending";
    if (session.status === "Succeeded" && phaseId <= 7) return "completed";
    if (session.status === "Failed" && failurePhase !== null) {
      if (phaseId === failurePhase) return "failed";
      if (phaseId < failurePhase) return "completed";
      return "pending";
    }
    if (phaseId === session.currentPhase) return "current";
    if (phaseId < session.currentPhase) return "completed";
    return "pending";
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-900 flex items-center justify-center">
        <div className="text-gray-400">Loading flight tracker...</div>
      </div>
    );
  }

  if (!session) {
    return (
      <div className="min-h-screen bg-gray-900 flex items-center justify-center">
        <div className="text-center">
          <div className="text-gray-400 text-lg mb-4">Session not found</div>
          <button
            onClick={() => router.push("/")}
            className="text-blue-400 hover:text-blue-300"
          >
            Back to Dashboard
          </button>
        </div>
      </div>
    );
  }

  const sessionShortId = session.sessionId.split("-")[0]?.toUpperCase() || session.sessionId.substring(0, 8).toUpperCase();

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-900 text-white">
        {/* Top Bar */}
        <div className="bg-gray-800 border-b border-gray-700">
          <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-3">
            <div className="flex items-center justify-between">
              <div className="flex items-center space-x-3">
                <button
                  onClick={() => router.push("/")}
                  className="text-gray-400 hover:text-white transition-colors"
                >
                  <svg
                    className="w-5 h-5"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M10 19l-7-7m0 0l7-7m-7 7h18"
                    />
                  </svg>
                </button>
                <div className="flex items-center space-x-2">
                  <span className="text-2xl">
                    {session.status === "Succeeded"
                      ? "\u2705"
                      : session.status === "Failed"
                      ? "\u274C"
                      : "\uD83D\uDEEB"}
                  </span>
                  <h1 className="text-xl font-bold text-white">
                    ENROLLMENT FLIGHT TRACKER
                  </h1>
                </div>
              </div>
              <div className="flex items-center space-x-4">
                <button
                  onClick={() =>
                    router.push(`/sessions/${session.sessionId}`)
                  }
                  className="text-sm text-gray-400 hover:text-white transition-colors"
                >
                  Full Details
                </button>
                {session.status === "Failed" && analysisResults.length > 0 && (
                  <button
                    onClick={() =>
                      router.push(`/diagnosis/${session.sessionId}`)
                    }
                    className="text-sm bg-red-600 hover:bg-red-700 px-3 py-1 rounded transition-colors"
                  >
                    View Diagnosis
                  </button>
                )}
                <span className="text-sm text-gray-400 font-mono">
                  Session: {sessionShortId}
                </span>
              </div>
            </div>
          </div>
        </div>

        {/* Device Info Bar */}
        <div className="bg-gray-800/50 border-b border-gray-700/50">
          <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-3">
            <div className="flex items-center justify-between text-sm">
              <div className="flex items-center space-x-6">
                <div className="flex items-center space-x-2">
                  <svg
                    className="w-4 h-4 text-gray-500"
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
                  <span className="text-white font-medium">
                    {session.deviceName || session.serialNumber}
                  </span>
                </div>
                <span className="text-gray-400">|</span>
                <span className="text-gray-300">
                  {session.manufacturer} {session.model}
                </span>
                <span className="text-gray-400">|</span>
                <span className="text-gray-300">{session.serialNumber}</span>
                <span className="text-gray-400">|</span>
                <span className="text-gray-300">
                  Started:{" "}
                  {new Date(session.startedAt).toLocaleTimeString([], {
                    hour: "2-digit",
                    minute: "2-digit",
                  })}
                </span>
              </div>
              <div>
                <StatusPill status={session.status} />
              </div>
            </div>
          </div>
        </div>

        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
          {/* Phase Progress Track */}
          <div className="bg-gray-800 rounded-xl p-6 mb-6 border border-gray-700">
            <div className="relative">
              {/* Progress Line */}
              <div className="absolute top-6 left-0 right-0 h-1 bg-gray-700 rounded-full">
                <div
                  className={`h-full rounded-full transition-all duration-1000 ${
                    session.status === "Failed"
                      ? "bg-red-500"
                      : session.status === "Succeeded"
                      ? "bg-green-500"
                      : "bg-blue-500"
                  }`}
                  style={{
                    width: `${
                      session.status === "Succeeded"
                        ? 100
                        : Math.min(
                            100,
                            (effectivePhase / 7) * 100
                          )
                    }%`,
                  }}
                />
              </div>

              {/* Phase Dots */}
              <div className="relative flex justify-between">
                {[0, 1, 2, 3, 4, 5, 6, 7].map((phaseId) => {
                  const status = getPhaseStatus(phaseId);
                  return (
                    <div
                      key={phaseId}
                      className="flex flex-col items-center"
                      style={{ width: "12.5%" }}
                    >
                      <div
                        className={`w-12 h-12 rounded-full flex items-center justify-center text-sm font-bold border-2 transition-all z-10 ${
                          status === "completed"
                            ? "bg-green-500 border-green-400 text-white"
                            : status === "current"
                            ? "bg-blue-500 border-blue-400 text-white ring-4 ring-blue-500/30 animate-pulse"
                            : status === "failed"
                            ? "bg-red-500 border-red-400 text-white ring-4 ring-red-500/30"
                            : "bg-gray-700 border-gray-600 text-gray-500"
                        }`}
                      >
                        {status === "completed" ? (
                          <svg
                            className="w-6 h-6"
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
                        ) : status === "failed" ? (
                          <svg
                            className="w-6 h-6"
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
                        ) : status === "current" ? (
                          <div className="w-3 h-3 bg-white rounded-full" />
                        ) : (
                          <span className="text-xs">{phaseId + 1}</span>
                        )}
                      </div>
                      <div className="mt-2 text-center">
                        <div
                          className={`text-xs font-medium ${
                            status === "completed"
                              ? "text-green-400"
                              : status === "current"
                              ? "text-blue-400"
                              : status === "failed"
                              ? "text-red-400"
                              : "text-gray-500"
                          }`}
                        >
                          {phaseShortNames[phaseId]}
                        </div>
                        {appProgress &&
                          phaseId === 5 &&
                          status === "current" && (
                            <div className="text-[10px] text-blue-300 mt-0.5">
                              ({appProgress.completed}/{appProgress.total})
                            </div>
                          )}
                      </div>
                    </div>
                  );
                })}
              </div>
            </div>
          </div>

          {/* Currently Installing + Recent Events side by side */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mb-6">
            {/* Currently Installing */}
            <div className="bg-gray-800 rounded-xl p-6 border border-gray-700">
              <div className="flex items-center space-x-2 mb-4">
                <svg
                  className="w-5 h-5 text-blue-400"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                >
                  <path
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    strokeWidth={2}
                    d="M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10M4 7v10l8 4"
                  />
                </svg>
                <h2 className="text-lg font-semibold">Current Activity</h2>
              </div>

              {currentDownload ? (
                <div>
                  <div className="text-sm text-gray-300 mb-2">
                    {currentDownload.appName}
                  </div>
                  {/* Progress bar */}
                  <div className="w-full h-3 bg-gray-700 rounded-full overflow-hidden mb-2">
                    <div
                      className="h-full bg-blue-500 rounded-full transition-all duration-500"
                      style={{
                        width: `${currentDownload.progressPercent}%`,
                      }}
                    />
                  </div>
                  <div className="flex items-center justify-between text-xs text-gray-400">
                    <span>
                      Download:{" "}
                      {formatBytes(currentDownload.bytesDownloaded)} /{" "}
                      {formatBytes(currentDownload.bytesTotal)}
                    </span>
                    <span className="text-blue-400 font-medium">
                      {currentDownload.progressPercent.toFixed(0)}%
                    </span>
                  </div>
                  <div className="flex items-center justify-between text-xs text-gray-500 mt-1">
                    <span>
                      Speed: {formatSpeed(currentDownload.downloadRateBps)}
                    </span>
                    <span>
                      ETA: ~
                      {currentDownload.etaSeconds < 60
                        ? `${Math.round(currentDownload.etaSeconds)} seconds`
                        : `${Math.round(currentDownload.etaSeconds / 60)} minutes`}
                    </span>
                  </div>
                </div>
              ) : appProgress && appProgress.currentItem ? (
                <div>
                  <div className="text-sm text-gray-300 mb-2">
                    {appProgress.currentItem}
                  </div>
                  <div className="w-full h-3 bg-gray-700 rounded-full overflow-hidden mb-2">
                    <div
                      className="h-full bg-blue-500 rounded-full transition-all duration-500"
                      style={{
                        width: `${
                          appProgress.total > 0
                            ? (appProgress.completed / appProgress.total) * 100
                            : 0
                        }%`,
                      }}
                    />
                  </div>
                  <div className="text-xs text-gray-400">
                    Apps: {appProgress.completed} / {appProgress.total}{" "}
                    completed
                  </div>
                </div>
              ) : session.status === "InProgress" ? (
                <div className="flex items-center space-x-3">
                  <div className="w-2 h-2 bg-blue-400 rounded-full animate-pulse" />
                  <span className="text-sm text-gray-400">
                    {phaseNames[session.currentPhase] || "Processing"}...
                  </span>
                </div>
              ) : session.status === "Succeeded" ? (
                <div className="flex items-center space-x-3">
                  <svg
                    className="w-8 h-8 text-green-400"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z"
                    />
                  </svg>
                  <div>
                    <div className="text-green-400 font-medium">
                      Enrollment Complete
                    </div>
                    <div className="text-xs text-gray-500">
                      Duration: {Math.round(session.durationSeconds / 60)} min
                    </div>
                  </div>
                </div>
              ) : (
                <div className="flex items-center space-x-3">
                  <svg
                    className="w-8 h-8 text-red-400"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M10 14l2-2m0 0l2-2m-2 2l-2-2m2 2l2 2m7-2a9 9 0 11-18 0 9 9 0 0118 0z"
                    />
                  </svg>
                  <div>
                    <div className="text-red-400 font-medium">
                      Enrollment Failed
                    </div>
                    {session.failureReason && (
                      <div className="text-xs text-gray-500 mt-1">
                        {session.failureReason}
                      </div>
                    )}
                  </div>
                </div>
              )}
            </div>

            {/* Recent Events */}
            <div className="bg-gray-800 rounded-xl p-6 border border-gray-700">
              <div className="flex items-center justify-between mb-4">
                <div className="flex items-center space-x-2">
                  <svg
                    className="w-5 h-5 text-gray-400"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      strokeWidth={2}
                      d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"
                    />
                  </svg>
                  <h2 className="text-lg font-semibold">Recent Events</h2>
                </div>
                <span className="text-xs text-gray-500">
                  {events.length} total
                </span>
              </div>

              <div className="space-y-1.5 max-h-[280px] overflow-y-auto">
                {recentEvents.length === 0 ? (
                  <div className="text-sm text-gray-500 text-center py-4">
                    Waiting for events...
                  </div>
                ) : (
                  recentEvents.map((event) => (
                    <div
                      key={event.eventId || `${event.sessionId}-${event.sequence}`}
                      className="flex items-start space-x-3 py-1.5 text-sm"
                    >
                      <span className="text-gray-500 font-mono text-xs whitespace-nowrap mt-0.5">
                        {new Date(event.timestamp).toLocaleTimeString([], {
                          hour: "2-digit",
                          minute: "2-digit",
                          second: "2-digit",
                        })}
                      </span>
                      <span className="flex-shrink-0 mt-0.5">
                        {event.severity === "Error" ||
                        event.severity === "Critical" ? (
                          <span className="text-red-400 text-xs">&#x2716;</span>
                        ) : event.severity === "Warning" ? (
                          <span className="text-yellow-400 text-xs">&#x26A1;</span>
                        ) : event.message
                            .toLowerCase()
                            .includes("success") ||
                          event.message
                            .toLowerCase()
                            .includes("completed") ? (
                          <span className="text-green-400 text-xs">&#x2714;</span>
                        ) : (
                          <span className="text-blue-400 text-xs">&#x25CF;</span>
                        )}
                      </span>
                      <span className="text-gray-300 text-sm leading-tight truncate">
                        {event.message}
                      </span>
                    </div>
                  ))
                )}
              </div>
            </div>
          </div>

          {/* Bottom: Estimated Completion + Quick Diagnosis if failed */}
          <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
            {/* Estimated Completion / Duration */}
            <div className="bg-gray-800 rounded-xl p-6 border border-gray-700">
              {session.status === "InProgress" && estimatedCompletion ? (
                <div className="flex items-center space-x-4">
                  <div className="w-12 h-12 rounded-full bg-blue-500/20 flex items-center justify-center">
                    <svg
                      className="w-6 h-6 text-blue-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z"
                      />
                    </svg>
                  </div>
                  <div>
                    <div className="text-sm text-gray-400">
                      Estimated completion
                    </div>
                    <div className="text-lg font-semibold text-white">
                      {estimatedCompletion.time.toLocaleTimeString([], {
                        hour: "2-digit",
                        minute: "2-digit",
                      })}
                    </div>
                    <div className="text-xs text-gray-500">
                      ~{estimatedCompletion.remainingMinutes} minutes remaining
                    </div>
                  </div>
                </div>
              ) : session.status === "Succeeded" ? (
                <div className="flex items-center space-x-4">
                  <div className="w-12 h-12 rounded-full bg-green-500/20 flex items-center justify-center">
                    <svg
                      className="w-6 h-6 text-green-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M5 13l4 4L19 7"
                      />
                    </svg>
                  </div>
                  <div>
                    <div className="text-sm text-gray-400">
                      Completed successfully
                    </div>
                    <div className="text-lg font-semibold text-green-400">
                      {Math.round(session.durationSeconds / 60)} min total
                    </div>
                    <div className="text-xs text-gray-500">
                      {new Date(session.startedAt).toLocaleTimeString([], {
                        hour: "2-digit",
                        minute: "2-digit",
                      })}{" "}
                      -{" "}
                      {new Date(
                        new Date(session.startedAt).getTime() +
                          session.durationSeconds * 1000
                      ).toLocaleTimeString([], {
                        hour: "2-digit",
                        minute: "2-digit",
                      })}
                    </div>
                  </div>
                </div>
              ) : (
                <div className="flex items-center space-x-4">
                  <div className="w-12 h-12 rounded-full bg-red-500/20 flex items-center justify-center">
                    <svg
                      className="w-6 h-6 text-red-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z"
                      />
                    </svg>
                  </div>
                  <div>
                    <div className="text-sm text-gray-400">
                      Failed after {Math.round(session.durationSeconds / 60)}{" "}
                      minutes
                    </div>
                    <div className="text-lg font-semibold text-red-400">
                      {session.failureReason || "Enrollment failed"}
                    </div>
                  </div>
                </div>
              )}
            </div>

            {/* Quick Analysis Summary */}
            {analysisResults.length > 0 && (
              <div className="bg-gray-800 rounded-xl p-6 border border-gray-700">
                <div className="flex items-center justify-between mb-3">
                  <div className="flex items-center space-x-2">
                    <svg
                      className="w-5 h-5 text-amber-400"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        strokeWidth={2}
                        d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z"
                      />
                    </svg>
                    <h2 className="text-lg font-semibold">Analysis</h2>
                  </div>
                  <button
                    onClick={() =>
                      router.push(`/diagnosis/${session.sessionId}`)
                    }
                    className="text-xs text-blue-400 hover:text-blue-300"
                  >
                    Full Diagnosis &rarr;
                  </button>
                </div>
                <div className="space-y-2">
                  {analysisResults.slice(0, 3).map((result) => (
                    <div
                      key={result.ruleId}
                      className="flex items-center space-x-3 py-1"
                    >
                      <span
                        className={`w-2 h-2 rounded-full flex-shrink-0 ${
                          result.severity === "critical"
                            ? "bg-red-500"
                            : result.severity === "high"
                            ? "bg-orange-500"
                            : result.severity === "warning"
                            ? "bg-yellow-500"
                            : "bg-blue-500"
                        }`}
                      />
                      <span className="text-sm text-gray-300 truncate">
                        {result.ruleTitle}
                      </span>
                      <span className="text-xs text-gray-500 flex-shrink-0">
                        {result.confidenceScore}%
                      </span>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      </div>
    </ProtectedRoute>
  );
}

function StatusPill({ status }: { status: string }) {
  const config = {
    InProgress: {
      bg: "bg-blue-500/20",
      text: "text-blue-400",
      label: "In Progress",
      dot: "bg-blue-400 animate-pulse",
    },
    Succeeded: {
      bg: "bg-green-500/20",
      text: "text-green-400",
      label: "Succeeded",
      dot: "bg-green-400",
    },
    Failed: {
      bg: "bg-red-500/20",
      text: "text-red-400",
      label: "Failed",
      dot: "bg-red-400",
    },
  };

  const c = config[status as keyof typeof config] || {
    bg: "bg-gray-500/20",
    text: "text-gray-400",
    label: status,
    dot: "bg-gray-400",
  };

  return (
    <span
      className={`inline-flex items-center space-x-1.5 px-3 py-1 rounded-full text-xs font-medium ${c.bg} ${c.text}`}
    >
      <span className={`w-2 h-2 rounded-full ${c.dot}`} />
      <span>{c.label}</span>
    </span>
  );
}
