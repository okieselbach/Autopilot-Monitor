"use client";

import { useEffect, useState, useRef } from "react";
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
  99: "Failed"
};

export default function SessionDetailPage() {
  const params = useParams();
  const router = useRouter();
  const sessionId = params?.sessionId as string;
  const [events, setEvents] = useState<EnrollmentEvent[]>([]);
  const [session, setSession] = useState<Session | null>(null);
  const [sessionTenantId, setSessionTenantId] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [expandedPhases, setExpandedPhases] = useState<Set<string>>(new Set());
  const [severityFilters, setSeverityFilters] = useState<Set<string>>(new Set(["Info", "Warning", "Error", "Critical", "Debug"]));
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
  const { getAccessToken } = useAuth();

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
        const eventsWithPhaseNames = data.events.map((e: EnrollmentEvent) => ({
          ...e,
          phaseName: phaseNames[e.phase] || "Unknown"
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
        const eventsWithPhaseNames = data.events.map((e: EnrollmentEvent) => ({
          ...e,
          phaseName: phaseNames[e.phase] || "Unknown"
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
  const filteredEvents = events.filter(e => severityFilters.has(e.severity));

  // Group filtered events by phase
  const eventsByPhase = filteredEvents.reduce((acc, event) => {
    const phaseName = event.phaseName || "Unknown";
    if (!acc[phaseName]) {
      acc[phaseName] = [];
    }
    acc[phaseName].push(event);
    return acc;
  }, {} as Record<string, EnrollmentEvent[]>);

  const phaseOrder = ["PreFlight", "Network", "Identity", "MDM Enrollment", "ESP Device Setup", "App Installation", "ESP User Setup", "Complete", "Failed"];
  const orderedPhases = phaseOrder.filter(phase => eventsByPhase[phase]);

  // Initialize all phases as expanded on first load
  useEffect(() => {
    if (orderedPhases.length > 0 && expandedPhases.size === 0) {
      setExpandedPhases(new Set(orderedPhases));
    }
  }, [orderedPhases.length]);

  const expandAll = () => {
    setExpandedPhases(new Set(orderedPhases));
  };

  const collapseAll = () => {
    setExpandedPhases(new Set());
  };

  const togglePhase = (phaseName: string) => {
    const newExpanded = new Set(expandedPhases);
    if (newExpanded.has(phaseName)) {
      newExpanded.delete(phaseName);
    } else {
      newExpanded.add(phaseName);
    }
    setExpandedPhases(newExpanded);
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
            <button
              onClick={() => router.push(`/flight-tracker/${sessionId}`)}
              className="px-4 py-2 bg-gray-800 text-white rounded-md hover:bg-gray-900 transition-colors flex items-center gap-2 text-sm"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 7l5 5m0 0l-5 5m5-5H6" />
              </svg>
              Flight Tracker
            </button>
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

          {/* Phase Timeline */}
          {session && (
            <div className="bg-white shadow rounded-lg p-6 mb-6">
              <h2 className="text-xl font-semibold text-gray-900 mb-6">Enrollment Progress</h2>
              <PhaseTimeline
                currentPhase={session.currentPhase}
                completedPhases={session.status === 'Succeeded' ? [7] : []}
                events={events}
                sessionStatus={session.status}
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

          {/* Download Progress (from download_progress events) */}
          <DownloadProgress
            events={events.filter(e => e.eventType === "download_progress")}
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
  onToggle
}: {
  phaseName: string;
  events: EnrollmentEvent[];
  isExpanded: boolean;
  onToggle: () => void;
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
            <EventRow key={event.eventId || `${event.sessionId}-${event.sequence}`} event={event} />
          ))}
        </div>
      )}
    </div>
  );
}

function EventRow({ event }: { event: EnrollmentEvent }) {
  const [showDetails, setShowDetails] = useState(false);

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

function PhaseTimeline({ currentPhase, completedPhases, events = [], sessionStatus }: {
  currentPhase: number;
  completedPhases: number[];
  events?: EnrollmentEvent[];
  sessionStatus?: string;
}) {
  const phases = [
    { id: 0, name: "PreFlight", shortName: "Pre-Flight" },
    { id: 1, name: "Network", shortName: "Network" },
    { id: 2, name: "Identity", shortName: "Identity" },
    { id: 3, name: "MDM Enrollment", shortName: "MDM" },
    { id: 4, name: "ESP Device Setup", shortName: "Device" },
    { id: 5, name: "App Installation", shortName: "Apps" },
    { id: 6, name: "ESP User Setup", shortName: "User" },
    { id: 7, name: "Complete", shortName: "Complete" },
  ];

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

    // Check for esp_ui_state events to show app install progress
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

    // Check for download_progress to show active download
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
