"use client";

import { useEffect, useState, useRef, useMemo } from "react";
import { useParams, useRouter } from "next/navigation";
import { useSignalR } from "../../../contexts/SignalRContext";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { ProtectedRoute } from '../../../components/ProtectedRoute';
import PerformanceChart from '../../../components/PerformanceChart';
import DownloadProgress from '../../../components/DownloadProgress';
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
  enrollmentType?: string; // "v1" | "v2" — absent for sessions before this feature
}

// Phase definitions per enrollment type
const V1_PHASES = [
  { id: 0, name: "Start",               shortName: "Start" },
  { id: 1, name: "Device Preparation",  shortName: "Device Preparation" },
  { id: 2, name: "Device Setup",        shortName: "Device Setup" },
  { id: 3, name: "Apps (Device)",       shortName: "Apps (Device)" },
  { id: 4, name: "Account Setup",       shortName: "Account Setup" },
  { id: 5, name: "Apps (User)",         shortName: "Apps (User)" },
  { id: 6, name: "Finalizing Setup",    shortName: "Finalizing" },
  { id: 7, name: "Complete",            shortName: "Complete" },
];

const V2_PHASES = [
  { id: 0, name: "Start",               shortName: "Start" },
  { id: 1, name: "Device Preparation",  shortName: "Device Preparation" },
  { id: 3, name: "App Installation",    shortName: "Apps" },
  { id: 6, name: "Finalizing Setup",    shortName: "Finalizing" },
  { id: 7, name: "Complete",            shortName: "Complete" },
];

const V1_PHASE_ORDER = ["Start", "Device Preparation", "Device Setup", "Apps (Device)", "Account Setup", "Apps (User)", "Finalizing Setup", "Complete", "Failed"];
const V2_PHASE_ORDER = ["Start", "Device Preparation", "App Installation", "Finalizing Setup", "Complete", "Failed"];

// Lookup by phase number — Phase 3 has different names per enrollment type
const V1_PHASE_NAMES: Record<number, string> = { [-1]: "Unknown", 0: "Start", 1: "Device Preparation", 2: "Device Setup", 3: "Apps (Device)",    4: "Account Setup", 5: "Apps (User)", 6: "Finalizing Setup", 7: "Complete", 99: "Failed" };
const V2_PHASE_NAMES: Record<number, string> = { [-1]: "Unknown", 0: "Start", 1: "Device Preparation", 2: "Device Setup", 3: "App Installation", 4: "Account Setup", 5: "Apps (User)", 6: "Finalizing Setup", 7: "Complete", 99: "Failed" };

export default function SessionDetailPage() {
  const params = useParams();
  const router = useRouter();
  const sessionId = params?.sessionId as string;
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);
  const [session, setSession] = useState<Session | null>(null);
  const [sessionTenantId, setSessionTenantId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [severityFilters, setSeverityFilters] = useState<Set<string>>(new Set(["Info", "Warning", "Error", "Critical"]));
  const [showMarkFailedConfirm, setShowMarkFailedConfirm] = useState(false);
  const [analysisResults, setAnalysisResults] = useState<RuleResult[]>([]);
  const [loadingAnalysis, setLoadingAnalysis] = useState(false);
  const [analysisExpanded, setAnalysisExpanded] = useState(true);
  const [timelineExpanded, setTimelineExpanded] = useState(true);
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

  // Use ref for debounce timeout to persist across renders
  const refreshTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const sessionIdRef = useRef(sessionId);
  const hasInitialFetch = useRef(false);
  const lastFetchedSessionId = useRef<string | null>(null);

  // Track if we've joined groups to prevent duplicate joins
  const hasJoinedGroups = useRef(false);

  // Use global contexts
  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();
  const { getAccessToken, user } = useAuth();

  // Initial data fetch
  useEffect(() => {
    if (!sessionId) return;

    // Update sessionIdRef
    sessionIdRef.current = sessionId;

    // Reset fetch flag only if navigating to a different session
    if (lastFetchedSessionId.current !== sessionId) {
      hasInitialFetch.current = false;
      lastFetchedSessionId.current = sessionId;
      setSessionTenantId(null); // Reset session tenant ID for new session
    }

    // Prevent duplicate fetches in React StrictMode (development double-mounting)
    if (hasInitialFetch.current) return;
    hasInitialFetch.current = true;

    fetchSessionDetails();
    // fetchEvents will be called after sessionTenantId is set
  }, [sessionId]);

  // Fetch events and analysis when we have the session's tenant ID
  useEffect(() => {
    if (sessionTenantId && sessionId) {
      fetchEvents();
      fetchAnalysisResults();
    }
  }, [sessionTenantId, sessionId]);

  // Join SignalR groups when connected (for multi-tenancy and cost optimization)
  // Uses "subscribe-then-fetch" pattern: join groups first, then re-fetch events
  // to catch anything that arrived before the group join completed.
  useEffect(() => {
    // Use the session's tenant ID if available
    const effectiveTenantId = sessionTenantId || tenantId;
    if (!sessionId || !isConnected || !effectiveTenantId) return;

    if (!hasJoinedGroups.current) {
      // Join both tenant group (for newEvents) and session group (for eventStream)
      const tenantGroupName = `tenant-${effectiveTenantId}`;
      const sessionGroupName = `session-${effectiveTenantId}-${sessionId}`;

      const joinAndCatchUp = async () => {
        await joinGroup(tenantGroupName);
        await joinGroup(sessionGroupName);
        hasJoinedGroups.current = true;

        // Re-fetch events after group join to catch any SignalR messages
        // that were sent before the client joined the session group.
        // The frontend deduplicates by eventId, so no duplicates.
        fetchEvents();
      };
      joinAndCatchUp();
    }

    return () => {
      // Leave groups when component unmounts or sessionId changes
      if (hasJoinedGroups.current && effectiveTenantId) {
        const tenantGroupName = `tenant-${effectiveTenantId}`;
        const sessionGroupName = `session-${effectiveTenantId}-${sessionId}`;

        leaveGroup(tenantGroupName);
        leaveGroup(sessionGroupName);
        hasJoinedGroups.current = false;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, isConnected, sessionTenantId, tenantId]);

  // Setup SignalR listener - re-register when connection changes
  useEffect(() => {
    // Listen for real-time event stream (cost-efficient - no HTTP requests!)
    const handleEventStream = (data: { sessionId: string; tenantId: string; events: EnrollmentEvent[]; session: Session; newRuleResults?: RuleResult[] }) => {
      console.log('Event stream received via SignalR:', data);
      if (data.sessionId === sessionIdRef.current && data.events && data.events.length > 0) {
        // Add phase names to new events
        const phaseNamesMap = data.session?.enrollmentType === "v2" ? V2_PHASE_NAMES : V1_PHASE_NAMES;
        const eventsWithPhaseNames = data.events.map((e: EnrollmentEvent) => ({
          ...e,
          phaseName: phaseNamesMap[e.phase] || "Unknown"
        }));

        // Add new events to existing list (deduplication by eventId)
        setEvents(prevEvents => {
          const existingIds = new Set(prevEvents.map(e => e.eventId));
          const newEvents = eventsWithPhaseNames.filter(e => !existingIds.has(e.eventId));
          return [...prevEvents, ...newEvents].sort((a, b) => a.sequence - b.sequence);
        });

        // Update session details directly from SignalR (no HTTP request needed!)
        if (data.session) {
          setSession(data.session);
        }

        // Add new rule results in real-time
        if (data.newRuleResults && data.newRuleResults.length > 0) {
          setAnalysisResults(prev => {
            const existingIds = new Set(prev.map(r => r.ruleId));
            const newResults = data.newRuleResults!.filter(r => !existingIds.has(r.ruleId));
            return [...prev, ...newResults].sort((a, b) => b.confidenceScore - a.confidenceScore);
          });
        }
      }
    };

    // Also listen for summary updates to update session details
    const handleNewEvents = (data: { sessionId: string; tenantId: string; eventCount: number; session: Session }) => {
      if (data.sessionId === sessionIdRef.current) {
        // Update session details directly from SignalR (no HTTP request needed!)
        if (data.session) {
          setSession(data.session);
        }
      }
    };

    on('eventstream', handleEventStream);
    on('newevents', handleNewEvents);

    return () => {
      off('eventstream', handleEventStream);
      off('newevents', handleNewEvents);
      if (refreshTimeoutRef.current) {
        clearTimeout(refreshTimeoutRef.current);
      }
    };
  }, [on, off]); // Re-register when SignalR connection changes

  const fetchSessionDetails = async () => {
    try {
      // In galactic admin mode, fetch from the galactic endpoint (cross-tenant)
      // Otherwise, use the current tenant's sessions
      const endpoint = galacticAdminMode
        ? `${API_BASE_URL}/api/galactic/sessions`
        : `${API_BASE_URL}/api/sessions?tenantId=${tenantId}`;


      // Get access token and include Authorization header
      const token = await getAccessToken();

      if (!token) {
        console.error('Failed to get access token for session details');
        return;
      }
      const response = await fetch(endpoint, {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });
      if (response.ok) {
        const data = await response.json();
        const foundSession = data.sessions?.find((s: Session) => s.sessionId === sessionId);
        if (foundSession) {
          setSession(foundSession);
          // Store the session's tenant ID for subsequent requests
          setSessionTenantId(foundSession.tenantId);
        }
      }
    } catch (error) {
      console.error("Failed to fetch session details:", error);
    }
  };

  const fetchEvents = async () => {
    // Use the session's tenant ID if available, otherwise fall back to current user's tenant ID
    const effectiveTenantId = sessionTenantId || tenantId;

    try {
      // Get access token and include Authorization header
      const token = await getAccessToken();

      if (!token) {
        console.error('Failed to get access token for events');
        setLoading(false);
        return;
      }
      const response = await fetch(`${API_BASE_URL}/api/sessions/${sessionId}/events?tenantId=${effectiveTenantId}`, {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });
      if (response.ok) {
        const data = await response.json();
        const phaseNamesMap = session?.enrollmentType === "v2" ? V2_PHASE_NAMES : V1_PHASE_NAMES;
        const eventsWithPhaseNames = data.events.map((e: EnrollmentEvent) => ({
          ...e,
          phaseName: phaseNamesMap[e.phase] || "Unknown"
        }));
        // Merge with existing events (from SignalR) instead of replacing,
        // so we don't lose events that arrived via SignalR since the last fetch.
        // Deduplication by eventId, same as the SignalR handler.
        setEvents(prevEvents => {
          if (prevEvents.length === 0) return eventsWithPhaseNames;
          const existingIds = new Set(prevEvents.map(e => e.eventId));
          const newEvents = eventsWithPhaseNames.filter((e: EnrollmentEvent) => !existingIds.has(e.eventId));
          if (newEvents.length === 0) return prevEvents;
          return [...prevEvents, ...newEvents].sort((a: EnrollmentEvent, b: EnrollmentEvent) => a.sequence - b.sequence);
        });
      }
    } catch (error) {
      console.error("Failed to fetch events:", error);
    } finally {
      setLoading(false);
    }
  };

  const fetchAnalysisResults = async (reanalyze = false) => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      setLoadingAnalysis(true);
      const token = await getAccessToken();
      if (!token) return;

      const reanalyzeParam = reanalyze ? '&reanalyze=true' : '';
      const response = await fetch(
        `${API_BASE_URL}/api/sessions/${sessionId}/analysis?tenantId=${effectiveTenantId}${reanalyzeParam}`,
        { headers: { 'Authorization': `Bearer ${token}` } }
      );
      if (response.ok) {
        const data = await response.json();
        if (data.results) {
          setAnalysisResults(data.results.sort((a: RuleResult, b: RuleResult) => b.confidenceScore - a.confidenceScore));
        }
      }
    } catch (error) {
      console.error("Failed to fetch analysis results:", error);
    } finally {
      setLoadingAnalysis(false);
    }
  };

  const markAsFailed = () => {
    setShowMarkFailedConfirm(true);
  };

  const confirmMarkFailed = async () => {
    // Use the session's tenant ID if available
    const effectiveTenantId = sessionTenantId || tenantId;

    try {
      // Get access token and include Authorization header
      const token = await getAccessToken();

      if (!token) {
        console.error('Failed to get access token for mark failed');
        return;
      }
      const response = await fetch(`${API_BASE_URL}/api/sessions/${sessionId}/mark-failed?tenantId=${effectiveTenantId}`, {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });

      if (response.ok) {
        setShowMarkFailedConfirm(false);
        // Session will be updated via SignalR, but we can also update local state immediately
        if (session) {
          setSession({
            ...session,
            status: 'Failed'
          });
        }
      } else {
        console.error('Failed to mark session as failed');
      }
    } catch (error) {
      console.error('Error marking session as failed:', error);
    }
  };

  const cancelMarkFailed = () => {
    setShowMarkFailedConfirm(false);
  };

  const toggleSeverityFilter = (severity: string) => {
    setSeverityFilters(prev => {
      const next = new Set(prev);
      if (next.has(severity)) {
        next.delete(severity);
      } else {
        next.add(severity);
      }
      return next;
    });
  };

  // Filter events by severity for the timeline
  const filteredEvents = useMemo(() =>
    events.filter(e => severityFilters.has(e.severity)),
    [events, severityFilters]
  );

  // Group events by phase with memoization
  const { eventsByPhase, orderedPhases } = useMemo(() => {
    // Sort events by sequence (chronological order)
    const sortedEvents = [...filteredEvents].sort((a, b) => a.sequence - b.sequence);

    // Group events by phase, inserting "Unknown" events into their chronological position
    const eventsByPhase = {} as Record<string, EnrollmentEvent[]>;
    let currentActivePhaseName = "Start"; // Default to Start phase

    for (let i = 0; i < sortedEvents.length; i++) {
      const event = sortedEvents[i];
      let targetPhase = event.phaseName || "Unknown";

      // If event has explicit phase (not Unknown), update current active phase
      if (targetPhase !== "Unknown") {
        currentActivePhaseName = targetPhase;
      } else {
        // Unknown events go into the current active phase
        targetPhase = currentActivePhaseName;
      }

      if (!eventsByPhase[targetPhase]) {
        eventsByPhase[targetPhase] = [];
      }
      eventsByPhase[targetPhase].push(event);
    }

    const phaseOrder = session?.enrollmentType === "v2" ? V2_PHASE_ORDER : V1_PHASE_ORDER;
    const orderedPhases = phaseOrder.filter(phase => eventsByPhase[phase] && eventsByPhase[phase].length > 0);

    return { eventsByPhase, orderedPhases };
  }, [filteredEvents, events.length, session?.enrollmentType]);

  const [expandedPhases, setExpandedPhases] = useState<Set<string>>(new Set());

  // Auto-expand new phases as they appear (keeps existing expanded/collapsed state)
  useEffect(() => {
    setExpandedPhases(prev => {
      // Add any new phases that aren't already tracked
      const newExpanded = new Set(prev);
      let hasChanges = false;

      for (const phase of orderedPhases) {
        if (!prev.has(phase)) {
          newExpanded.add(phase);
          hasChanges = true;
        }
      }

      return hasChanges ? newExpanded : prev;
    });
  }, [orderedPhases]);

  const expandAll = () => {
    setExpandedPhases(new Set(orderedPhases));
  };

  const collapseAll = () => {
    setExpandedPhases(new Set());
  };

  const togglePhase = (phaseName: string) => {
    setExpandedPhases(prev => {
      const newExpanded = new Set(prev);
      if (newExpanded.has(phaseName)) {
        newExpanded.delete(phaseName);
      } else {
        newExpanded.add(phaseName);
      }
      return newExpanded;
    });
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center">
        <div className="text-gray-600">Loading session details...</div>
      </div>
    );
  }

  return (
<ProtectedRoute>
    <div className="min-h-screen bg-gray-50">
      {/* Header */}
      <header className="bg-white shadow">
        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8 flex items-center justify-between">
          <div>
            <button
              onClick={() => router.push('/')}
              className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
            >
              ← Back to Dashboard
            </button>
            <h1 className="text-3xl font-bold text-gray-900">
              Session Details
            </h1>
          </div>
          <div className="flex items-center gap-3">
            {session?.status === 'Failed' && (
              <button
                onClick={() => router.push(`/diagnosis/${sessionId}`)}
                className="px-4 py-2 bg-amber-500 text-white rounded-md hover:bg-amber-600 transition-colors flex items-center gap-2 text-sm"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                </svg>
                Diagnosis
              </button>
            )}
          {adminMode && session?.status === 'InProgress' && (
            <button
              onClick={markAsFailed}
              className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors flex items-center gap-2"
            >
              <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
              </svg>
              Mark as Failed
            </button>
          )}
          </div>
        </div>
      </header>

      <main className="max-w-7xl mx-auto py-6 sm:px-6 lg:px-8">
        <div className="px-4 py-6 sm:px-0">
          {/* Session Info Card */}
          {session && (
            <div className="bg-white shadow rounded-lg p-6 mb-6">
              <h2 className="text-xl font-semibold text-gray-900 mb-4">Device Information</h2>
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                <InfoItem label="Device" value={session.deviceName || session.serialNumber} />
                <InfoItem label="Model" value={`${session.manufacturer} ${session.model}`} />
                <InfoItem label="Serial Number" value={session.serialNumber} />
                <InfoItem label="Session ID" value={session.sessionId} />
                <InfoItem label="Status" value={<StatusBadge status={session.status} failureReason={session.failureReason} />} />
                <InfoItem label="Events" value={session.eventCount.toString()} />
                <InfoItem label="Duration" value={`${Math.round(session.durationSeconds / 60)} min`} />
              </div>
            </div>
          )}

          {/* Device Details Card (from enrollment tracker events) */}
          <DeviceDetailsCard events={events} />

          {/* Phase Timeline */}
          {session && (
            <div className="bg-white shadow rounded-lg p-6 mb-6">
              <h2 className="text-xl font-semibold text-gray-900 mb-6">Enrollment Progress</h2>
              <PhaseTimeline
                currentPhase={session.currentPhase}
                completedPhases={session.status === 'Succeeded' ? [7] : []}
                events={events}
                sessionStatus={session.status}
                enrollmentType={session.enrollmentType}
              />
            </div>
          )}

          {/* Analysis Results */}
          <div className="bg-white shadow rounded-lg p-6 mb-6">
            <div
              onClick={() => setAnalysisExpanded(!analysisExpanded)}
              className="flex items-center justify-between w-full text-left mb-4 cursor-pointer"
            >
              <div className="flex items-center space-x-2">
                <svg className="w-6 h-6 text-amber-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z" />
                </svg>
                <h2 className="text-xl font-semibold text-gray-900">Analysis Results</h2>
                {analysisResults.length > 0 && (
                  <>
                    <span className="text-xs text-gray-400">({analysisResults.length} {analysisResults.length === 1 ? 'issue' : 'issues'})</span>
                    <div className="flex items-center space-x-2 text-xs">
                      {analysisResults.filter(r => r.severity === 'critical').length > 0 && (
                        <span className="px-2 py-0.5 rounded-full bg-red-100 text-red-700 font-medium">
                          {analysisResults.filter(r => r.severity === 'critical').length} Critical
                        </span>
                      )}
                      {analysisResults.filter(r => r.severity === 'high').length > 0 && (
                        <span className="px-2 py-0.5 rounded-full bg-orange-100 text-orange-700 font-medium">
                          {analysisResults.filter(r => r.severity === 'high').length} High
                        </span>
                      )}
                      {analysisResults.filter(r => r.severity === 'warning').length > 0 && (
                        <span className="px-2 py-0.5 rounded-full bg-yellow-100 text-yellow-700 font-medium">
                          {analysisResults.filter(r => r.severity === 'warning').length} Warning
                        </span>
                      )}
                    </div>
                  </>
                )}
              </div>
              <div className="flex items-center space-x-3">
                <button
                  onClick={(e) => { e.stopPropagation(); fetchAnalysisResults(true); }}
                  disabled={loadingAnalysis}
                  title="Runs all analyze rules (single + correlation) against the current event data. Analysis also runs automatically when enrollment completes or fails."
                  className="px-3 py-1.5 text-sm font-medium bg-amber-50 text-amber-700 hover:bg-amber-100 rounded-lg transition-colors disabled:opacity-50 disabled:cursor-not-allowed flex items-center space-x-1.5"
                >
                  {loadingAnalysis ? (
                    <>
                      <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                      </svg>
                      <span>Analyzing...</span>
                    </>
                  ) : (
                    <>
                      <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
                      </svg>
                      <span>Analyze Now</span>
                    </>
                  )}
                </button>
                <span className="text-gray-400">{analysisExpanded ? '▼' : '▶'}</span>
              </div>
            </div>

            {analysisExpanded && (
              <>
                {loadingAnalysis && analysisResults.length === 0 ? (
                  <div className="text-center py-4 text-gray-500">Running analysis...</div>
                ) : analysisResults.length === 0 ? (
                  <div className="text-center py-4 text-gray-400 text-sm">
                    No issues detected yet. Click &quot;Analyze Now&quot; to run analysis on the current events, or wait for enrollment to complete for automatic analysis.
                  </div>
                ) : (
                  <div className="space-y-3">
                    {analysisResults.map((result) => (
                      <AnalysisResultCard key={result.ruleId} result={result} />
                    ))}
                  </div>
                )}
              </>
            )}
          </div>

          {/* Performance Metrics (from performance_snapshot events) */}
          <PerformanceChart
            events={events.filter(e => e.eventType === "performance_snapshot")}
          />

          {/* Download Progress (from download_progress or app_tracking_summary events) */}
          <DownloadProgress
            events={events.filter(e => e.eventType === "download_progress" || e.eventType === "app_tracking_summary")}
          />

          {/* Timeline */}
          <div className="bg-white shadow rounded-lg p-6">
            <button
              onClick={() => setTimelineExpanded(!timelineExpanded)}
              className="flex items-center justify-between w-full text-left mb-4"
            >
              <h2 className="text-xl font-semibold text-gray-900">Event Timeline</h2>
              <span className="text-gray-400">{timelineExpanded ? '▼' : '▶'}</span>
            </button>
            {timelineExpanded && (
              <>
                {/* Severity Filters + Expand/Collapse All */}
                <div className="flex items-center justify-between mb-6">
                  <div className="flex items-center gap-2">
                    <span className="text-xs font-medium text-gray-500">Filter:</span>
                    {(["Debug", "Info", "Warning", "Error", "Critical"] as const).map((sev) => {
                      const active = severityFilters.has(sev);
                      const colors: Record<string, { on: string; off: string }> = {
                        Debug: { on: "bg-gray-200 text-gray-800", off: "bg-gray-50 text-gray-400" },
                        Info: { on: "bg-blue-100 text-blue-800", off: "bg-gray-50 text-gray-400" },
                        Warning: { on: "bg-yellow-100 text-yellow-800", off: "bg-gray-50 text-gray-400" },
                        Error: { on: "bg-red-100 text-red-800", off: "bg-gray-50 text-gray-400" },
                        Critical: { on: "bg-red-200 text-red-900", off: "bg-gray-50 text-gray-400" },
                      };
                      return (
                        <button
                          key={sev}
                          onClick={() => toggleSeverityFilter(sev)}
                          className={`px-2.5 py-1 text-xs font-medium rounded-full transition-colors ${active ? colors[sev].on : colors[sev].off} hover:opacity-80`}
                        >
                          {sev}
                        </button>
                      );
                    })}
                    <span className="text-xs text-gray-400 ml-1">({filteredEvents.length}/{events.length})</span>
                  </div>
                  {orderedPhases.length > 0 && (
                    <div className="flex gap-2">
                      <button
                        onClick={expandAll}
                        className="px-3 py-1 text-sm bg-blue-50 text-blue-700 hover:bg-blue-100 rounded transition-colors"
                      >
                        Expand All
                      </button>
                      <button
                        onClick={collapseAll}
                        className="px-3 py-1 text-sm bg-gray-50 text-gray-700 hover:bg-gray-100 rounded transition-colors"
                      >
                        Collapse All
                      </button>
                    </div>
                  )}
                </div>

                {orderedPhases.length === 0 ? (
                  <div className="text-gray-500 text-center py-8">No events found for this session.</div>
                ) : (
                  <div className="space-y-8">
                    {orderedPhases.map((phaseName) => (
                      <PhaseSection
                        key={phaseName}
                        phaseName={phaseName}
                        events={eventsByPhase[phaseName]}
                        isExpanded={expandedPhases.has(phaseName)}
                        onToggle={() => togglePhase(phaseName)}
                        isGalacticAdmin={user?.isGalacticAdmin}
                      />
                    ))}
                  </div>
                )}
              </>
            )}
          </div>
        </div>

        {/* Mark as Failed Confirmation Modal */}
        {showMarkFailedConfirm && session && (
          <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50" onClick={cancelMarkFailed}>
            <div className="bg-white rounded-lg shadow-xl max-w-md w-full mx-4" onClick={(e) => e.stopPropagation()}>
              <div className="p-6">
                <div className="flex items-center mb-4">
                  <div className="flex-shrink-0 w-12 h-12 bg-red-100 rounded-full flex items-center justify-center">
                    <svg className="w-6 h-6 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                    </svg>
                  </div>
                  <h3 className="ml-4 text-lg font-semibold text-gray-900">Mark Session as Failed</h3>
                </div>
                <div className="mb-6">
                  <p className="text-sm text-gray-700 mb-2">
                    You are about to manually mark this session as <span className="font-semibold text-red-600">Failed</span>.
                  </p>
                  <p className="text-sm text-gray-700 mb-2">
                    Session <span className="font-mono text-xs">{session.sessionId}</span> for device <span className="font-semibold">{session.deviceName || session.serialNumber}</span> will be marked as failed with the reason "Manually marked as failed by administrator".
                  </p>
                  <p className="text-sm text-gray-600">
                    This action will update the session status and cannot be undone. Do you want to continue?
                  </p>
                </div>
                <div className="flex justify-end gap-3">
                  <button onClick={cancelMarkFailed} className="px-4 py-2 bg-gray-200 text-gray-700 rounded-md hover:bg-gray-300 transition-colors">
                    Cancel
                  </button>
                  <button onClick={confirmMarkFailed} className="px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors">
                    Mark as Failed
                  </button>
                </div>
              </div>
            </div>
          </div>
        )}
      </main>
    </div>
  </ProtectedRoute>
  );
}

function InfoItem({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <div className="text-sm font-medium text-gray-500">{label}</div>
      <div className="mt-1 text-sm text-gray-900">{value}</div>
    </div>
  );
}

function PhaseSection({
  phaseName,
  events,
  isExpanded,
  onToggle,
  isGalacticAdmin
}: {
  phaseName: string;
  events: EnrollmentEvent[];
  isExpanded: boolean;
  onToggle: () => void;
  isGalacticAdmin?: boolean;
}) {
  return (
    <div className="border-l-4 border-blue-500 pl-4">
      <button
        onClick={onToggle}
        className="flex items-center justify-between w-full text-left mb-3 group"
      >
        <h3 className="text-lg font-semibold text-gray-900 group-hover:text-blue-600">
          {phaseName} ({events.length} events)
        </h3>
        <span className="text-gray-400">{isExpanded ? '▼' : '▶'}</span>
      </button>

      {isExpanded && (
        <div className="space-y-3">
          {events.map((event, index) => (
            <EventRow
              key={event.eventId || `${event.sessionId}-${event.sequence}`}
              event={event}
              isGalacticAdmin={isGalacticAdmin}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function EventRow({ event, isGalacticAdmin }: { event: EnrollmentEvent; isGalacticAdmin?: boolean }) {
  const [showDetails, setShowDetails] = useState(false);
  const [copied, setCopied] = useState(false);

  const copyEventId = async () => {
    try {
      await navigator.clipboard.writeText(event.eventId);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch (err) {
      console.error('Failed to copy EventID:', err);
    }
  };

  return (
    <div className="bg-gray-50 rounded-lg p-3 hover:bg-gray-100 transition-colors">
      <div className="flex items-start justify-between">
        <div className="flex-1 min-w-0">
          <div className="flex items-center gap-3">
            <span className="text-xs text-gray-500 font-mono">
              {new Date(event.timestamp).toLocaleTimeString()}
            </span>
            <SeverityBadge severity={event.severity} />
            <span className="text-sm font-medium text-gray-900">{event.eventType}</span>
          </div>
          <p className="mt-1 text-sm text-gray-600">{event.message}</p>
          <div className="mt-1 flex items-center gap-3 text-xs text-gray-500">
            <span>Source: {event.source}</span>
            <span>Seq: {event.sequence}</span>
            {isGalacticAdmin && (
              <button
                onClick={copyEventId}
                className="font-mono hover:text-blue-600 cursor-pointer transition-colors"
                title={copied ? 'Copied!' : `Click to copy full EventId: ${event.eventId}`}
              >
                EventId: {event.eventId.substring(0, 8)}... {copied && '✓'}
              </button>
            )}
          </div>
        </div>
        {event.data && Object.keys(event.data).length > 0 && (
          <button
            onClick={() => setShowDetails(!showDetails)}
            className="text-xs text-blue-600 hover:text-blue-800 ml-4"
          >
            {showDetails ? 'Hide' : 'Details'}
          </button>
        )}
      </div>

      {showDetails && event.data && (
        <div className="mt-3 p-3 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
          <pre>{JSON.stringify(event.data, null, 2)}</pre>
        </div>
      )}
    </div>
  );
}

function PhaseTimeline({ currentPhase, completedPhases, events = [], sessionStatus, enrollmentType }: {
  currentPhase: number;
  completedPhases: number[];
  events?: EnrollmentEvent[];
  sessionStatus?: string;
  enrollmentType?: string;
}) {
  const phases = enrollmentType === "v2" ? V2_PHASES : V1_PHASES;

  // Derive current activity for the active phase from events
  const getCurrentActivity = (phaseId: number): string | null => {
    if (sessionStatus === 'Succeeded' || sessionStatus === 'Failed') return null;
    if (phaseId !== effectiveCurrentPhase) return null;

    // Event types that are not useful as activity status display
    const ignoredEventTypes = new Set([
      "performance_snapshot",
      "system_info",
      "network_info",
    ]);

    // Get events for this phase, sorted by sequence desc, excluding noise
    const phaseEvents = events
      .filter(e => e.phase === phaseId && !ignoredEventTypes.has(e.eventType))
      .sort((a, b) => b.sequence - a.sequence);

    if (phaseEvents.length === 0) return null;

    // Check for app_tracking_summary events (new strategic events)
    const trackingSummary = phaseEvents.find(e => e.eventType === "app_tracking_summary");
    if (trackingSummary?.data) {
      const d = trackingSummary.data;
      const completed = parseInt(d.appsCompleted ?? "0", 10);
      const total = parseInt(d.totalApps ?? "0", 10);
      if (total > 0) {
        return `Installing apps (${completed}/${total})`;
      }
    }

    // Check for esp_ui_state events (legacy) to show app install progress
    const espState = phaseEvents.find(e => e.eventType === "esp_ui_state");
    if (espState?.data) {
      const d = espState.data;
      const completed = parseInt(d.blocking_apps_completed ?? d.blockingAppsCompleted ?? "0", 10);
      const total = parseInt(d.blocking_apps_total ?? d.blockingAppsTotal ?? "0", 10);
      const currentItem = d.current_item ?? d.currentItem ?? d.status_text ?? d.statusText;
      if (total > 0 && currentItem) {
        return `${currentItem} (${completed}/${total})`;
      }
      if (total > 0) {
        return `Installing apps (${completed}/${total})`;
      }
    }

    // Check for app install events (new strategic events)
    const appInstallEvt = phaseEvents.find(e =>
      e.eventType === "app_download_started" || e.eventType === "app_install_started"
    );
    if (appInstallEvt?.data) {
      const appName = appInstallEvt.data.appName ?? appInstallEvt.data.appId ?? "app";
      if (appInstallEvt.eventType === "app_download_started") return `Downloading ${appName}`;
      return `Installing ${appName}`;
    }

    // Check for download_progress to show active download (legacy)
    const downloadEvt = phaseEvents.find(e => e.eventType === "download_progress");
    if (downloadEvt?.data) {
      const d = downloadEvt.data;
      const appName = d.app_name ?? d.appName ?? "content";
      const pct = d.bytes_total && d.bytes_downloaded
        ? Math.round((parseInt(d.bytes_downloaded) / parseInt(d.bytes_total)) * 100)
        : null;
      if (pct !== null) return `Downloading ${appName} - ${pct}%`;
      return `Downloading ${appName}`;
    }

    // Fall back to latest event message
    const latest = phaseEvents[0];
    if (latest && latest.message && latest.message.length < 80) {
      return latest.message;
    }

    return null;
  };

  // Calculate phase durations from events
  const getPhaseDuration = (phaseId: number): string | null => {
    const phaseEvents = events.filter(e => e.phase === phaseId);
    if (phaseEvents.length < 2) return null;

    const sorted = [...phaseEvents].sort((a, b) =>
      new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
    );
    const first = new Date(sorted[0].timestamp).getTime();
    const last = new Date(sorted[sorted.length - 1].timestamp).getTime();
    const durationSec = Math.round((last - first) / 1000);

    if (durationSec < 5) return null;
    if (durationSec < 60) return `${durationSec}s`;
    if (durationSec < 3600) return `${Math.floor(durationSec / 60)}m ${durationSec % 60}s`;
    return `${Math.floor(durationSec / 3600)}h ${Math.floor((durationSec % 3600) / 60)}m`;
  };

  // Derive the highest real phase (0-7) seen in events, excluding phase 99 (Failed)
  const maxEventPhase = (() => {
    const realPhases = events
      .filter(e => e.phase >= 0 && e.phase <= 7)
      .map(e => e.phase);
    if (realPhases.length === 0) return -1;
    return Math.max(...realPhases);
  })();

  // Determine the actual failure phase from events when session has failed
  // currentPhase=99 means "Failed" but we need to know WHICH phase it failed in
  const failurePhase = (() => {
    if (sessionStatus !== 'Failed' || currentPhase !== 99) return null;
    return maxEventPhase >= 0 ? maxEventPhase : 0;
  })();

  // Effective current phase: use the max of backend currentPhase and what events show.
  // This prevents the tracker from lagging behind the timeline when events from a new
  // phase arrive via SignalR before the backend updates session.currentPhase.
  const effectiveCurrentPhase = (() => {
    if (sessionStatus === 'Succeeded') return currentPhase;
    if (sessionStatus === 'Failed') return failurePhase !== null ? failurePhase : currentPhase;
    // In-progress: take the higher of backend phase and events phase
    if (maxEventPhase < 0) return currentPhase;
    return Math.max(currentPhase, maxEventPhase);
  })();

  const getPhaseStatus = (phaseId: number) => {
    // Agent starts at MDM phase (3) - Pre-Flight(0)/Network(1)/Identity(2) are inferred as completed
    // since the machine reached MDM enrollment
    if (phaseId >= 0 && phaseId <= 2) return 'completed'; // Pre-Flight(0), Network(1), Identity(2)

    // If phase is completed, show as completed (green)
    if (completedPhases.includes(phaseId)) return 'completed';

    // Handle failed sessions
    if (sessionStatus === 'Failed' && failurePhase !== null) {
      if (phaseId === failurePhase) return 'failed';
      if (phaseId < failurePhase) return 'completed';
      return 'pending';
    }

    // Normal in-progress logic (using effectiveCurrentPhase to stay in sync with events)
    if (phaseId === effectiveCurrentPhase) return 'current';
    if (phaseId < effectiveCurrentPhase) return 'completed';
    return 'pending';
  };

  const getPhaseColor = (status: string) => {
    switch (status) {
      case 'completed': return 'bg-green-500 text-white border-green-500';
      case 'current': return 'bg-blue-500 text-white border-blue-500 ring-4 ring-blue-200';
      case 'failed': return 'bg-red-500 text-white border-red-500 ring-4 ring-red-200';
      case 'pending': return 'bg-gray-200 text-gray-500 border-gray-300';
      default: return 'bg-gray-200 text-gray-500 border-gray-300';
    }
  };

  const getConnectorColor = (fromPhase: number) => {
    const fromStatus = getPhaseStatus(fromPhase);
    if (fromStatus === 'completed') return 'bg-green-500';
    if (fromStatus === 'failed') return 'bg-red-500';
    return 'bg-gray-300';
  };

  return (
    <div className="w-full py-4">
      <div className="flex w-full">
        {phases.map((phase, index) => {
          const status = getPhaseStatus(phase.id);
          const prevStatus = index > 0 ? getPhaseStatus(phases[index - 1].id) : null;
          const connColor = index > 0 ? getConnectorColor(phases[index - 1].id) : '';
          const showArrow = prevStatus === 'current' || prevStatus === 'failed';

          return (
            <div key={phase.id} className="flex-1 relative flex flex-col items-center min-w-0">
              {/* Connector line from previous phase center to this phase center */}
              {index > 0 && (
                <div
                  className={`absolute h-1 ${connColor}`}
                  style={{ top: '22px', left: '-50%', right: '50%' }}
                />
              )}
              {/* Arrow when previous phase is current or failed */}
              {index > 0 && showArrow && (
                <div
                  className="absolute z-20"
                  style={{ top: '16px', left: 'calc(50% - 36px)' }}
                >
                  <div
                    className={`w-0 h-0 border-t-[8px] border-t-transparent border-b-[8px] border-b-transparent border-l-[12px] ${
                      connColor === 'bg-green-500' ? 'border-l-green-500' :
                      connColor === 'bg-red-500' ? 'border-l-red-500' : 'border-l-gray-300'
                    }`}
                    style={{
                      filter: connColor === 'bg-green-500'
                        ? 'drop-shadow(0 0 3px rgba(34, 197, 94, 0.5))'
                        : connColor === 'bg-red-500'
                        ? 'drop-shadow(0 0 3px rgba(239, 68, 68, 0.5))'
                        : 'drop-shadow(0 0 3px rgba(209, 213, 219, 0.5))'
                    }}
                  />
                </div>
              )}
              {/* Circle - centered, on top of connector */}
              <div className={`relative z-10 w-12 h-12 rounded-full flex items-center justify-center border-2 transition-all font-semibold ${getPhaseColor(status)}`}>
                {status === 'completed' ? '✓' : status === 'failed' ? '✕' : phase.id + 1}
              </div>
              {/* Labels - centered below circle */}
              <div className="mt-3 text-center">
                <div className="text-xs font-medium text-gray-700 whitespace-nowrap">
                  {phase.shortName}
                </div>
                {(status === 'completed' || status === 'failed') && getPhaseDuration(phase.id) && (
                  <div className={`mt-0.5 text-[10px] ${status === 'failed' ? 'text-red-400' : 'text-gray-400'}`}>
                    {getPhaseDuration(phase.id)}
                  </div>
                )}
                {status === 'failed' && (
                  <div className="mt-0.5 text-[10px] text-red-500 font-semibold">Failed</div>
                )}
                {status === 'current' && getCurrentActivity(phase.id) && (
                  <div className="mt-1 max-w-[140px]">
                    <div className="text-[10px] text-blue-600 font-medium line-clamp-2 animate-pulse" title={getCurrentActivity(phase.id) || undefined}>
                      {getCurrentActivity(phase.id)}
                    </div>
                  </div>
                )}
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function SeverityBadge({ severity }: { severity: string }) {
  const colors = {
    Info: "bg-blue-100 text-blue-800",
    Warning: "bg-yellow-100 text-yellow-800",
    Error: "bg-red-100 text-red-800",
    Critical: "bg-red-200 text-red-900"
  };

  const color = colors[severity as keyof typeof colors] || colors.Info;

  return (
    <span className={`px-2 py-0.5 rounded text-xs font-medium ${color}`}>
      {severity}
    </span>
  );
}

function AnalysisResultCard({ result }: { result: RuleResult }) {
  const [expanded, setExpanded] = useState(false);

  const severityColors: Record<string, string> = {
    critical: "border-l-red-600 bg-red-50",
    high: "border-l-orange-500 bg-orange-50",
    warning: "border-l-yellow-500 bg-yellow-50",
    info: "border-l-blue-500 bg-blue-50",
  };

  const severityBadgeColors: Record<string, string> = {
    critical: "bg-red-100 text-red-800",
    high: "bg-orange-100 text-orange-800",
    warning: "bg-yellow-100 text-yellow-800",
    info: "bg-blue-100 text-blue-800",
  };

  const cardColor = severityColors[result.severity] || severityColors.info;
  const badgeColor = severityBadgeColors[result.severity] || severityBadgeColors.info;

  return (
    <div className={`border-l-4 rounded-lg p-4 ${cardColor}`}>
      <div className="flex items-start justify-between cursor-pointer" onClick={() => setExpanded(!expanded)}>
        <div className="flex-1">
          <div className="flex items-center space-x-2 mb-1">
            <span className={`px-2 py-0.5 rounded text-xs font-medium ${badgeColor}`}>
              {result.severity.toUpperCase()}
            </span>
            <span className="text-xs font-mono text-gray-500">{result.ruleId}</span>
            <span className="text-xs px-2 py-0.5 rounded-full bg-gray-200 text-gray-600">{result.category}</span>
          </div>
          <h3 className="font-medium text-gray-900">{result.ruleTitle}</h3>
          <div className="flex items-center mt-1 space-x-3">
            <div className="flex items-center space-x-1">
              <span className="text-xs text-gray-500">Confidence:</span>
              <div className="w-24 h-2 bg-gray-200 rounded-full overflow-hidden">
                <div
                  className={`h-full rounded-full ${
                    result.confidenceScore >= 80 ? 'bg-red-500' :
                    result.confidenceScore >= 60 ? 'bg-orange-500' :
                    result.confidenceScore >= 40 ? 'bg-yellow-500' : 'bg-blue-500'
                  }`}
                  style={{ width: `${result.confidenceScore}%` }}
                />
              </div>
              <span className="text-xs font-medium text-gray-700">{result.confidenceScore}%</span>
            </div>
          </div>
        </div>
        <span className="text-gray-400 ml-2">{expanded ? '▼' : '▶'}</span>
      </div>

      {expanded && (
        <div className="mt-4 space-y-3">
          {result.explanation && (
            <div>
              <h4 className="text-sm font-medium text-gray-700 mb-1">Explanation</h4>
              <p className="text-sm text-gray-600">{result.explanation}</p>
            </div>
          )}

          {result.remediation && result.remediation.length > 0 && (
            <div>
              <h4 className="text-sm font-medium text-gray-700 mb-1">Remediation</h4>
              {result.remediation.map((rem, i) => (
                <div key={i} className="mb-2">
                  <p className="text-sm font-medium text-gray-600">{rem.title}</p>
                  <ul className="list-disc list-inside text-sm text-gray-600 ml-2">
                    {rem.steps.map((step, j) => (
                      <li key={j}>{step}</li>
                    ))}
                  </ul>
                </div>
              ))}
            </div>
          )}

          {result.relatedDocs && result.relatedDocs.length > 0 && (
            <div>
              <h4 className="text-sm font-medium text-gray-700 mb-1">Related Documentation</h4>
              <div className="flex flex-wrap gap-2">
                {result.relatedDocs.map((doc, i) => (
                  <a
                    key={i}
                    href={doc.url}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="text-sm text-blue-600 hover:text-blue-800 underline"
                  >
                    {doc.title}
                  </a>
                ))}
              </div>
            </div>
          )}

          {result.matchedConditions && Object.keys(result.matchedConditions).length > 0 && (
            <div>
              <h4 className="text-sm font-medium text-gray-700 mb-1">Evidence</h4>
              <div className="p-2 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
                <pre>{JSON.stringify(result.matchedConditions, null, 2)}</pre>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function DeviceDetailsCard({ events }: { events: EnrollmentEvent[] }) {
  const [expanded, setExpanded] = useState(false);
  const [showIpv6, setShowIpv6] = useState<Record<number, boolean>>({});

  // Extract device detail events
  const getEventData = (eventType: string): Record<string, any> | null => {
    // Find the NEWEST event of this type (last occurrence) to get the most recent state
    const matchingEvents = events.filter(e => e.eventType === eventType);
    if (matchingEvents.length === 0) return null;
    const latestEvent = matchingEvents[matchingEvents.length - 1];
    return latestEvent?.data ?? null;
  };

  // Helper to check if IP is IPv6
  const isIpv6 = (ip: string): boolean => {
    // IPv6 contains colons and has multiple segments
    // IPv4 is like: 192.168.1.1
    // IPv6 is like: fe80::1234:5678:abcd:ef01 or 2001:db8::1
    if (!ip || typeof ip !== 'string') return false;

    // If it contains a colon and doesn't look like IPv4, it's IPv6
    // Simple check: IPv6 has at least 2 colons (even compressed form)
    const colonCount = (ip.match(/:/g) || []).length;
    return colonCount >= 2;
  };

  // Helper to split IPs into IPv4 and IPv6
  const splitIpAddresses = (ipAddresses: string | string[]): { ipv4: string[]; ipv6: string[] } => {
    // Handle both array and comma-separated string
    let ips: string[];
    if (Array.isArray(ipAddresses)) {
      ips = ipAddresses;
    } else if (typeof ipAddresses === 'string') {
      // Split by comma if it's a comma-separated string
      ips = ipAddresses.split(',').map(ip => ip.trim()).filter(ip => ip.length > 0);
    } else {
      ips = [];
    }

    const ipv4: string[] = [];
    const ipv6: string[] = [];

    for (const ip of ips) {
      if (typeof ip === 'string' && ip.trim()) {
        if (isIpv6(ip)) {
          ipv6.push(ip);
        } else {
          ipv4.push(ip);
        }
      }
    }

    return { ipv4, ipv6 };
  };

  const getBitLockerEncryptionMethodLabel = (value: unknown): string => {
    const method = value?.toString();
    const names: Record<string, string> = {
      "0": "None / Unknown",
      "1": "AES-128 mit Diffuser (legacy)",
      "2": "AES-256 mit Diffuser (legacy)",
      "3": "AES-128",
      "4": "AES-256",
      "5": "Hardware Encryption",
      "6": "XTS-AES 256",
      "7": "XTS-AES 128",
      "8": "Hardware Encryption (Full Disk)",
      "9": "Hardware Encryption (Data Only)",
    };

    if (!method) return "Unknown";
    return names[method] ?? `Unknown (${method})`;
  };

  const agentStarted = getEventData("agent_started");
  const bootTime = getEventData("boot_time");
  const osInfo = getEventData("os_info");
  const networkAdapters = getEventData("network_adapters");
  const dnsConfig = getEventData("dns_configuration");
  const proxyConfig = getEventData("proxy_configuration");
  const autopilotProfile = getEventData("autopilot_profile");
  const aadJoinStatus = getEventData("aad_join_status");
  const imeVersion = getEventData("ime_agent_version");
  const bitLockerStatus = getEventData("bitlocker_status");
  const secureBootStatus = getEventData("secureboot_status");
  const deviceLocation = getEventData("device_location");

  // Check if we have any device detail events at all
  const hasData = agentStarted || bootTime || osInfo || networkAdapters || dnsConfig || proxyConfig || autopilotProfile ||
                  aadJoinStatus || imeVersion || bitLockerStatus || secureBootStatus || deviceLocation;

  if (!hasData) return null;

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center justify-between w-full text-left"
      >
        <div className="flex items-center space-x-2">
          <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z" />
          </svg>
          <h2 className="text-xl font-semibold text-gray-900">Device Details</h2>
        </div>
        <span className="text-gray-400">{expanded ? '▼' : '▶'}</span>
      </button>

      {expanded && (
        <div className="mt-4 grid grid-cols-1 md:grid-cols-2 gap-6">
          {/* OS Information */}
          {osInfo && (
            <DetailSection title="Operating System">
              {osInfo.osVersion && <DetailRow label="Version" value={osInfo.osVersion} />}
              {osInfo.displayVersion && <DetailRow label="Display Version" value={osInfo.displayVersion} />}
              {osInfo.currentBuild && osInfo.buildRevision && (
                <DetailRow label="Build" value={`${osInfo.currentBuild}.${osInfo.buildRevision}`} />
              )}
              {osInfo.currentBuild && !osInfo.buildRevision && (
                <DetailRow label="Build" value={osInfo.currentBuild} />
              )}
              {osInfo.edition && <DetailRow label="Edition" value={osInfo.edition} />}
              {osInfo.compositionEdition && <DetailRow label="Composition Edition" value={osInfo.compositionEdition} />}
              {osInfo.buildBranch && <DetailRow label="Build Branch" value={osInfo.buildBranch} />}
            </DetailSection>
          )}

          {/* System */}
          {(bootTime?.bootTime || agentStarted?.bootTime || agentStarted?.agentVersion || imeVersion || aadJoinStatus?.joinType || deviceLocation?.country || deviceLocation?.Country || deviceLocation?.timezone || deviceLocation?.Timezone) && (
            <DetailSection title="System">
              {(bootTime?.bootTime || agentStarted?.bootTime) && (
                <DetailRow label="Boot Time" value={new Date(bootTime?.bootTime || agentStarted?.bootTime).toLocaleString()} />
              )}
              {bootTime?.uptimeMinutes && <DetailRow label="Uptime until enrollment starts" value={`${Math.floor(bootTime.uptimeMinutes / 60)}h ${bootTime.uptimeMinutes % 60}m`} />}
              {agentStarted?.agentVersion && <DetailRow label="Monitor Agent Version" value={agentStarted.agentVersion} />}
              {imeVersion && <DetailRow label="IME Agent Version" value={imeVersion.version ?? imeVersion.agentVersion ?? "Unknown"} />}
              {(deviceLocation?.country || deviceLocation?.Country) && (
                <DetailRow label="Country" value={deviceLocation.country ?? deviceLocation.Country} />
              )}
              {(deviceLocation?.timezone || deviceLocation?.Timezone) && (
                <DetailRow label="Timezone" value={deviceLocation.timezone ?? deviceLocation.Timezone} />
              )}
            </DetailSection>
          )}

          {/* Network */}
          {(networkAdapters || dnsConfig || proxyConfig) && (
            <DetailSection title="Network">
              {/* Network Adapters */}
              {networkAdapters && networkAdapters.adapters && (
                (networkAdapters.adapters as any[]).map((adapter: any, i: number) => {
                  const { ipv4, ipv6 } = adapter.ipAddresses ? splitIpAddresses(adapter.ipAddresses) : { ipv4: [], ipv6: [] };
                  const hasIpv6 = ipv6.length > 0;
                  const isIpv6Shown = showIpv6[i] ?? false;

                  return (
                    <div key={i} className="mb-3 pb-3 border-b border-gray-100 last:border-b-0 last:mb-0 last:pb-0">
                      <div className="text-sm font-medium text-gray-700 mb-1">{adapter.description || adapter.name || `Adapter ${i + 1}`}</div>

                      {/* DHCP */}
                      {adapter.dhcpEnabled !== undefined && <DetailRow label="DHCP" value={adapter.dhcpEnabled ? "Enabled" : "Disabled"} />}

                      {/* MAC */}
                      {adapter.macAddress && <DetailRow label="MAC" value={adapter.macAddress} />}

                      {/* IPv4 */}
                      {ipv4.length > 0 && <DetailRow label="IPv4" value={ipv4.join(", ")} />}

                      {/* IPv6 (collapsible) */}
                      {hasIpv6 && (
                        <div className="mt-1">
                          <button
                            onClick={() => setShowIpv6(prev => ({ ...prev, [i]: !isIpv6Shown }))}
                            className="text-xs text-blue-600 hover:text-blue-800 flex items-center space-x-1"
                          >
                            <span>{isIpv6Shown ? '▼' : '▶'}</span>
                            <span>IPv6 ({ipv6.length})</span>
                          </button>
                          {isIpv6Shown && (
                            <div className="mt-1 pl-4 text-xs text-gray-600 space-y-0.5">
                              {ipv6.map((ip, idx) => (
                                <div key={idx} className="font-mono">{ip}</div>
                              ))}
                            </div>
                          )}
                        </div>
                      )}

                      {/* DNS for this adapter */}
                      {dnsConfig?.dnsEntries && Array.isArray(dnsConfig.dnsEntries) && (
                        (dnsConfig.dnsEntries as any[])
                          .filter((entry: any) => entry.adapter === adapter.description || entry.adapter === adapter.name)
                          .map((entry: any, dnsIdx: number) => (
                            <DetailRow key={`dns-${dnsIdx}`} label="DNS" value={entry.servers || "N/A"} />
                          ))
                      )}
                    </div>
                  );
                })
              )}

              {/* Proxy Configuration */}
              {proxyConfig && (
                <div className="mt-3 pt-3 border-t border-gray-200">
                  <div className="text-sm font-medium text-gray-700 mb-1">Proxy</div>
                  <DetailRow label="Type" value={proxyConfig.proxyType ?? proxyConfig.type ?? "Direct"} />
                  {proxyConfig.proxyServer && <DetailRow label="Server" value={proxyConfig.proxyServer} />}
                  {proxyConfig.autoConfigUrl && <DetailRow label="PAC URL" value={proxyConfig.autoConfigUrl} />}
                  {proxyConfig.winHttpProxy && <DetailRow label="WinHTTP" value={proxyConfig.winHttpProxy} />}
                </div>
              )}
            </DetailSection>
          )}

          {/* Autopilot Profile */}
          {autopilotProfile && (
            <DetailSection title="Autopilot Profile">
              {autopilotProfile.CloudAssignedTenantDomain && <DetailRow label="Tenant Domain" value={autopilotProfile.CloudAssignedTenantDomain} />}
              {autopilotProfile.DeploymentProfileName && <DetailRow label="Profile Name" value={autopilotProfile.DeploymentProfileName} />}
              {autopilotProfile.CloudAssignedTenantId && <DetailRow label="Tenant ID" value={autopilotProfile.CloudAssignedTenantId} />}
              {autopilotProfile.PolicyDownloadDate && <DetailRow label="Policy Downloaded" value={new Date(autopilotProfile.PolicyDownloadDate).toLocaleString()} />}
              {autopilotProfile.CloudAssignedOobeConfig && <DetailRow label="OOBE Config" value={autopilotProfile.CloudAssignedOobeConfig} />}
              {autopilotProfile.ZtdRegistrationId && <DetailRow label="ZTD Registration ID" value={autopilotProfile.ZtdRegistrationId} />}
              {autopilotProfile.AadDeviceId && <DetailRow label="AAD Device ID" value={autopilotProfile.AadDeviceId} />}
              {autopilotProfile.CloudAssignedMdmId && <DetailRow label="MDM ID" value={autopilotProfile.CloudAssignedMdmId} />}
              {autopilotProfile.CloudAssignedDomainJoinMethod !== undefined && (
                <DetailRow label="Domain Join Method" value={autopilotProfile.CloudAssignedDomainJoinMethod === "0" ? "Entra Join" : autopilotProfile.CloudAssignedDomainJoinMethod} />
              )}
              {autopilotProfile.CloudAssignedForcedEnrollment !== undefined && (
                <DetailRow label="Forced Enrollment" value={autopilotProfile.CloudAssignedForcedEnrollment === "1" ? "Yes" : "No"} />
              )}
              {autopilotProfile.AutopilotCreationDate && <DetailRow label="Autopilot Created" value={new Date(autopilotProfile.AutopilotCreationDate).toLocaleString()} />}

              {/* Legacy fields (fallback for old data) */}
              {!autopilotProfile.CloudAssignedTenantDomain && autopilotProfile.tenantDomain && <DetailRow label="Tenant Domain" value={autopilotProfile.tenantDomain} />}
              {!autopilotProfile.DeploymentProfileName && autopilotProfile.deploymentProfileName && <DetailRow label="Profile Name" value={autopilotProfile.deploymentProfileName} />}
              {!autopilotProfile.CloudAssignedTenantId && autopilotProfile.cloudAssignedTenantId && <DetailRow label="Tenant ID" value={autopilotProfile.cloudAssignedTenantId} />}
              {!autopilotProfile.CloudAssignedOobeConfig && autopilotProfile.oobeConfig && <DetailRow label="OOBE Config" value={autopilotProfile.oobeConfig} />}
            </DetailSection>
          )}

          {/* Security */}
          {(bitLockerStatus || secureBootStatus) && (
            <DetailSection title="Security">
              {secureBootStatus && (
                <DetailRow label="SecureBoot" value={secureBootStatus.uefiSecureBootEnabled ? "Enabled" : "Disabled"} />
              )}
              {bitLockerStatus && (
                <>
                  <DetailRow label="BitLocker" value={bitLockerStatus.systemDriveProtected ? "Protected" : "Not Protected"} />
                  {bitLockerStatus.volumes && Array.isArray(bitLockerStatus.volumes) && bitLockerStatus.volumes.length > 0 && (
                    <div className="mt-1 text-xs text-gray-500">
                      {(bitLockerStatus.volumes as any[]).map((vol: any, i: number) => (
                        <div key={i}>
                          {vol.driveLetter} {vol.protectionStatus === "1" ? "Protected" : "Not Protected"}
                          {vol.encryptionMethod !== undefined && vol.encryptionMethod !== null && vol.encryptionMethod !== "" && (
                            ` (Method: ${getBitLockerEncryptionMethodLabel(vol.encryptionMethod)})`
                          )}
                        </div>
                      ))}
                    </div>
                  )}
                </>
              )}
            </DetailSection>
          )}
        </div>
      )}
    </div>
  );
}

function DetailSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="border border-gray-200 rounded-lg p-3">
      <h3 className="text-sm font-semibold text-gray-700 mb-2 border-b border-gray-100 pb-1">{title}</h3>
      <div>{children}</div>
    </div>
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex justify-between text-xs py-0.5">
      <span className="text-gray-500">{label}</span>
      <span className="text-gray-900 font-mono ml-2 text-right break-all" title={value}>{value}</span>
    </div>
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
