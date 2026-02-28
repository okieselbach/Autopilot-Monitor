"use client";

import { useEffect, useState, useRef, useMemo } from "react";
import { useParams, useRouter } from "next/navigation";
import { useSignalR } from "../../../contexts/SignalRContext";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { useNotifications } from "../../../contexts/NotificationContext";
import { ProtectedRoute } from '../../../components/ProtectedRoute';
import PerformanceChart from '../../../components/PerformanceChart';
import DownloadProgress from '../../../components/DownloadProgress';
import { API_BASE_URL } from "@/lib/config";

import { V1_PHASE_NAMES, V2_PHASE_NAMES, V1_PHASE_ORDER, V2_PHASE_ORDER } from "./utils/phaseConstants";
import { groupEventsByPhase } from "./utils/eventHelpers";
import SessionInfoCard from "./components/SessionInfoCard";
import PhaseTimeline from "./components/PhaseTimeline";
import EventTimeline from "./components/EventTimeline";
import AnalysisResultsSection from "./components/AnalysisResultsSection";
import MarkFailedModal from "./components/MarkFailedModal";

export interface EnrollmentEvent {
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

export interface RuleResult {
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

export interface Session {
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
  diagnosticsBlobName?: string;
  isPreProvisioned?: boolean;
}

const GUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

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

  // Debounce real-time event refreshes to avoid burst reads in Table Storage.
  const eventRefreshTimeoutRef = useRef<NodeJS.Timeout | null>(null);
  const sessionIdRef = useRef(sessionId);
  const sessionRef = useRef<Session | null>(null);
  const hasInitialFetch = useRef(false);
  const lastFetchedSessionId = useRef<string | null>(null);
  const tenantIdRef = useRef<string>("");
  const sessionTenantIdRef = useRef<string | null>(sessionTenantId);
  const galacticAdminModeRef = useRef(galacticAdminMode);

  // Track if we've joined groups to prevent duplicate joins
  const hasJoinedGroups = useRef(false);

  // Deduplication: track in-flight fetchEvents to avoid concurrent calls
  const fetchEventsInFlight = useRef(false);
  const fetchEventsQueued = useRef(false);

  // Use global contexts
  const { on, off, isConnected, joinGroup, leaveGroup } = useSignalR();
  const { tenantId } = useTenant();
  const { getAccessToken, user } = useAuth();
  const { addNotification } = useNotifications();

  useEffect(() => {
    sessionRef.current = session;
  }, [session]);

  useEffect(() => {
    tenantIdRef.current = tenantId;
  }, [tenantId]);

  useEffect(() => {
    sessionTenantIdRef.current = sessionTenantId;
  }, [sessionTenantId]);

  useEffect(() => {
    galacticAdminModeRef.current = galacticAdminMode;
  }, [galacticAdminMode]);

  const resolveEffectiveTenantId = () => {
    const knownSessionTenant = sessionTenantIdRef.current || sessionRef.current?.tenantId || null;
    if (knownSessionTenant) return knownSessionTenant;
    if (galacticAdminModeRef.current) return null;
    return tenantIdRef.current || null;
  };

  const scheduleFetchEvents = (delayMs = 300) => {
    if (eventRefreshTimeoutRef.current) {
      clearTimeout(eventRefreshTimeoutRef.current);
    }
    eventRefreshTimeoutRef.current = setTimeout(() => {
      fetchEvents();
    }, delayMs);
  };

  // Initial data fetch — wait for auth to be ready and a real tenantId before calling the backend.
  // TenantContext initializes to '' and updates once AuthContext finishes loading.
  // `user` is included so that a retry fires once MSAL settles (token becomes available).
  useEffect(() => {
    if (!sessionId) return;
    if (!galacticAdminMode && !tenantId) return; // wait for real tenant ID

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
  }, [sessionId, tenantId, galacticAdminMode, user]);

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
    const effectiveTenantId = resolveEffectiveTenantId();
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
        scheduleFetchEvents(0);
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
  }, [sessionId, isConnected, sessionTenantId, tenantId, session?.tenantId, galacticAdminMode]);

  // Setup SignalR listener - re-register when connection changes
  useEffect(() => {
    // Listen for event stream signal — backend sends a lightweight signal (no event payload).
    // Frontend fetches fresh events from Table Storage on receipt: canonical truth, no gaps.
    const handleEventStream = (data: { sessionId: string; tenantId: string; newEventCount: number; session?: Session; newRuleResults?: RuleResult[] }) => {
      console.log('Event stream signal received via SignalR:', data);
      if (data.sessionId !== sessionIdRef.current) return;

      // Fetch full events from storage (single source of truth), but debounce bursts.
      scheduleFetchEvents();

      // Session update from SignalR (no extra fetch needed)
      if (data.session) {
        setSession(data.session);
        if (data.session.tenantId) {
          setSessionTenantId(prev => prev || data.session!.tenantId);
        }
      }

      // Rule results from SignalR (only on enrollment completion)
      if (data.newRuleResults && data.newRuleResults.length > 0) {
        fetchAnalysisResults();
      }
    };

    on('eventStream', handleEventStream);

    return () => {
      off('eventStream', handleEventStream);
      if (eventRefreshTimeoutRef.current) {
        clearTimeout(eventRefreshTimeoutRef.current);
      }
    };
  }, [on, off]); // Re-register when SignalR connection changes

  // Fallback polling only while SignalR is disconnected.
  useEffect(() => {
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!sessionId || !effectiveTenantId || isConnected) return;
    const interval = setInterval(() => {
      if (document.visibilityState === "visible") fetchEvents();
    }, 30_000);
    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sessionId, sessionTenantId, tenantId, session?.tenantId, galacticAdminMode, isConnected]);

  const fetchSessionDetails = async () => {
    try {
      // Get access token and include Authorization header
      const token = await getAccessToken();

      if (!token) {
        addNotification('error', 'Authentication Error', 'Failed to get access token. Please try logging in again.', 'session-detail-auth-error');
        // Allow retry — token may not be ready yet (MSAL still initializing)
        hasInitialFetch.current = false;
        return;
      }

      const knownTenantId = resolveEffectiveTenantId();
      const endpoint = knownTenantId
        ? `${API_BASE_URL}/api/sessions/${sessionId}?tenantId=${knownTenantId}`
        : galacticAdminMode
          ? `${API_BASE_URL}/api/galactic/sessions`
          : `${API_BASE_URL}/api/sessions/${sessionId}`;

      const response = await fetch(endpoint, {
        headers: {
          'Authorization': `Bearer ${token}`
        }
      });
      if (response.ok) {
        const data = await response.json();
        const foundSession = data.session ?? data.sessions?.find((s: Session) => s.sessionId === sessionId);
        if (foundSession) {
          setSession(foundSession);
          // Store the session's tenant ID for subsequent requests
          setSessionTenantId(foundSession.tenantId);
        }
      } else {
        addNotification('error', 'Backend Error', `Failed to load session details: ${response.statusText}`, 'session-detail-fetch-error');
      }
    } catch (error) {
      console.error("Failed to fetch session details:", error);
      addNotification('error', 'Backend Not Reachable', 'Unable to load session details. Please check your connection.', 'session-detail-fetch-error');
      // Allow retry on network errors
      hasInitialFetch.current = false;
    }
  };

  const fetchEvents = async () => {
    // Deduplication: if a fetch is already in flight, queue one follow-up instead of
    // stacking concurrent requests (SignalR signal + 30s timer + group-join can overlap).
    if (fetchEventsInFlight.current) {
      fetchEventsQueued.current = true;
      return;
    }
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!effectiveTenantId || !GUID_REGEX.test(effectiveTenantId)) {
      return;
    }
    fetchEventsInFlight.current = true;
    fetchEventsQueued.current = false;

    try {
      // Get access token and include Authorization header
      const token = await getAccessToken();

      if (!token) {
        addNotification('error', 'Authentication Error', 'Failed to get access token. Please try logging in again.', 'session-events-auth-error');
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
        // Replace entire event list with fresh data from Table Storage (canonical truth).
        // No merge needed — fetchEvents() is the single source; SignalR only signals.
        // phaseName is computed at render time in eventsByPhase useMemo (no race condition).
        const fetchedEvents = Array.isArray(data.events) ? data.events : [];
        setEvents(prevEvents => {
          // Keep the last known-good snapshot if backend transiently returns an empty list.
          if (fetchedEvents.length === 0 && prevEvents.length > 0) {
            console.warn(
              `[SessionDetail] Ignoring empty event refresh for session ${sessionIdRef.current} (tenant ${effectiveTenantId}); keeping ${prevEvents.length} cached events`
            );
            return prevEvents;
          }
          return fetchedEvents;
        });
      } else {
        addNotification('error', 'Backend Error', `Failed to load session events: ${response.statusText}`, 'session-events-fetch-error');
      }
    } catch (error) {
      console.error("Failed to fetch events:", error);
      addNotification('error', 'Backend Not Reachable', 'Unable to load session events. Please check your connection.', 'session-events-fetch-error');
    } finally {
      setLoading(false);
      fetchEventsInFlight.current = false;

      // If another fetch was requested while we were in flight, run it now
      if (fetchEventsQueued.current) {
        fetchEventsQueued.current = false;
        fetchEvents();
      }
    }
  };

  const fetchAnalysisResults = async (reanalyze = false) => {
    const effectiveTenantId = resolveEffectiveTenantId();
    if (!effectiveTenantId || !GUID_REGEX.test(effectiveTenantId)) return;
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

  const isGatherRulesSession = session?.enrollmentType === "gather_rules";
  // For gather_rules sessions: if the completed event is present, derive status as Succeeded
  // (the backend never sets this status automatically for one-shot gather runs).
  const gatherRulesSucceeded = isGatherRulesSession &&
    events.some(e => e.eventType === "gather_rules_collection_completed");
  const displayStatus = gatherRulesSucceeded ? "Succeeded" : (session?.status ?? "");

  // Calculate enrollment duration from events (first event → enrollment_complete or last event)
  // More accurate than session.durationSeconds which is based on registration StartedAt
  const enrollmentDurationFromEvents = useMemo(() => {
    if (events.length === 0) return null;
    const timestamps = events.map(e => new Date(e.timestamp).getTime());
    const firstEventTime = Math.min(...timestamps);
    const completeEvent = events.find(e => e.eventType === "enrollment_complete");
    const endTime = completeEvent
      ? new Date(completeEvent.timestamp).getTime()
      : Math.max(...timestamps);
    const durationSec = Math.round((endTime - firstEventTime) / 1000);
    if (durationSec < 60) return `${durationSec}s`;
    if (durationSec < 3600) return `${Math.floor(durationSec / 60)}m ${durationSec % 60}s`;
    return `${Math.floor(durationSec / 3600)}h ${Math.floor((durationSec % 3600) / 60)}m`;
  }, [events]);

  const phaseNamesMap = session?.enrollmentType === "v2" ? V2_PHASE_NAMES : V1_PHASE_NAMES;
  const phaseOrder = session?.enrollmentType === "v2" ? V2_PHASE_ORDER : V1_PHASE_ORDER;

  // Detect WhiteGlove session and find the split point
  const isWhiteGloveSession = session?.isPreProvisioned === true ||
    events.some(e => e.eventType === "whiteglove_complete");

  const whiteGloveSplitSequence = useMemo(() => {
    if (!isWhiteGloveSession) return -1;
    const wgEvent = events.find(e => e.eventType === "whiteglove_complete");
    return wgEvent?.sequence ?? -1;
  }, [events, isWhiteGloveSession]);

  // For WhiteGlove sessions: split filtered events into pre-provisioning and user-enrollment parts
  const preProvEvents = useMemo(() => {
    if (!isWhiteGloveSession || whiteGloveSplitSequence < 0) return [] as EnrollmentEvent[];
    return filteredEvents.filter(e => e.sequence <= whiteGloveSplitSequence);
  }, [filteredEvents, isWhiteGloveSession, whiteGloveSplitSequence]);

  const userEnrollEvents = useMemo(() => {
    if (!isWhiteGloveSession || whiteGloveSplitSequence < 0) return [] as EnrollmentEvent[];
    return filteredEvents.filter(e => e.sequence > whiteGloveSplitSequence);
  }, [filteredEvents, isWhiteGloveSession, whiteGloveSplitSequence]);

  // Group events by phase — single timeline for normal sessions, two groups for WhiteGlove
  const { eventsByPhase, orderedPhases } = useMemo(() => {
    if (isWhiteGloveSession) return { eventsByPhase: {} as Record<string, EnrollmentEvent[]>, orderedPhases: [] as string[] };
    return groupEventsByPhase(filteredEvents, phaseNamesMap, phaseOrder);
  }, [filteredEvents, isWhiteGloveSession, phaseNamesMap, phaseOrder]);

  const preProvGrouped = useMemo(() =>
    isWhiteGloveSession && preProvEvents.length > 0
      ? groupEventsByPhase(preProvEvents, phaseNamesMap, phaseOrder)
      : { eventsByPhase: {} as Record<string, EnrollmentEvent[]>, orderedPhases: [] as string[] },
    [preProvEvents, isWhiteGloveSession, phaseNamesMap, phaseOrder]
  );

  const userEnrollGrouped = useMemo(() =>
    isWhiteGloveSession && userEnrollEvents.length > 0
      ? groupEventsByPhase(userEnrollEvents, phaseNamesMap, phaseOrder)
      : { eventsByPhase: {} as Record<string, EnrollmentEvent[]>, orderedPhases: [] as string[] },
    [userEnrollEvents, isWhiteGloveSession, phaseNamesMap, phaseOrder]
  );

  const [expandedPhases, setExpandedPhases] = useState<Set<string>>(new Set());

  // Auto-expand new phases as they appear (keeps existing expanded/collapsed state).
  // For WhiteGlove sessions we use prefixed keys (pre-X, user-X) to avoid collisions.
  useEffect(() => {
    setExpandedPhases(prev => {
      const newExpanded = new Set(prev);
      let hasChanges = false;

      const allPhases = isWhiteGloveSession
        ? [
            ...preProvGrouped.orderedPhases.map(p => `pre-${p}`),
            ...userEnrollGrouped.orderedPhases.map(p => `user-${p}`),
          ]
        : orderedPhases;

      for (const phase of allPhases) {
        if (!prev.has(phase)) {
          newExpanded.add(phase);
          hasChanges = true;
        }
      }

      return hasChanges ? newExpanded : prev;
    });
  }, [orderedPhases, preProvGrouped.orderedPhases, userEnrollGrouped.orderedPhases, isWhiteGloveSession]);

  const expandAll = () => {
    if (isWhiteGloveSession) {
      setExpandedPhases(new Set([
        ...preProvGrouped.orderedPhases.map(p => `pre-${p}`),
        ...userEnrollGrouped.orderedPhases.map(p => `user-${p}`),
      ]));
    } else {
      setExpandedPhases(new Set(orderedPhases));
    }
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

  // Only show full-page loading spinner on the very first load (no data yet).
  // Subsequent refreshes (SignalR, 30s poll) keep the existing UI visible.
  if (loading && !session && events.length === 0) {
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
              onClick={() => router.push('/dashboard')}
              className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
            >
              ← Back to Dashboard
            </button>
            <h1 className="text-3xl font-bold text-gray-900">
              Session Details
            </h1>
          </div>
          <div className="flex items-center gap-3">
            {session?.diagnosticsBlobName && (
              <button
                onClick={async () => {
                  try {
                    const token = await getAccessToken();
                    if (!token) return;
                    const res = await fetch(
                      `${API_BASE_URL}/api/diagnostics/download-url?tenantId=${session.tenantId}&blobName=${encodeURIComponent(session.diagnosticsBlobName!)}`,
                      { headers: { Authorization: `Bearer ${token}` } }
                    );
                    if (!res.ok) throw new Error('Failed to get download URL');
                    const data = await res.json();
                    window.open(data.downloadUrl, '_blank');
                  } catch (err) {
                    console.error('Diagnostics download failed:', err);
                  }
                }}
                className="px-4 py-2 bg-white border border-gray-200 text-gray-700 rounded-md hover:bg-gray-50 transition-colors flex items-center gap-2 text-sm"
              >
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 10v6m0 0l-3-3m3 3l3-3m2 8H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
                Download Diagnostics
              </button>
            )}
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
            {adminMode && (session?.status === 'InProgress' || session?.status === 'Pending') && (
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
            <SessionInfoCard
              session={session}
              enrollmentDuration={enrollmentDurationFromEvents}
              displayStatus={displayStatus}
              isGatherRulesSession={isGatherRulesSession}
            />
          )}

          {/* Device Details Card (from enrollment tracker events) */}
          {!isGatherRulesSession && <DeviceDetailsCard events={events} />}

          {/* Phase Timeline */}
          {!isGatherRulesSession && session && (
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
          {!isGatherRulesSession && (
            <AnalysisResultsSection
              analysisResults={analysisResults}
              loadingAnalysis={loadingAnalysis}
              analysisExpanded={analysisExpanded}
              setAnalysisExpanded={setAnalysisExpanded}
              onReanalyze={() => fetchAnalysisResults(true)}
            />
          )}

          {/* Performance Metrics (from performance_snapshot events) */}
          {!isGatherRulesSession && (
            <PerformanceChart
              events={events.filter(e => e.eventType === "performance_snapshot")}
            />
          )}

          {/* Download Progress (from download_progress, app_download_started, or app_tracking_summary events) */}
          {!isGatherRulesSession && (
            <DownloadProgress
              events={events.filter(
                e => e.eventType === "download_progress" || e.eventType === "app_download_started" || e.eventType === "app_tracking_summary"
              )}
            />
          )}

          {/* Event Timeline (with severity filters, expand/collapse, WhiteGlove split) */}
          <EventTimeline
            filteredEvents={filteredEvents}
            events={events}
            session={session}
            severityFilters={severityFilters}
            toggleSeverityFilter={toggleSeverityFilter}
            expandedPhases={expandedPhases}
            togglePhase={togglePhase}
            timelineExpanded={timelineExpanded}
            setTimelineExpanded={setTimelineExpanded}
            expandAll={expandAll}
            collapseAll={collapseAll}
            isWhiteGloveSession={isWhiteGloveSession}
            whiteGloveSplitSequence={whiteGloveSplitSequence}
            orderedPhases={orderedPhases}
            eventsByPhase={eventsByPhase}
            preProvGrouped={preProvGrouped}
            userEnrollGrouped={userEnrollGrouped}
            userEnrollEvents={userEnrollEvents}
            isGalacticAdmin={user?.isGalacticAdmin}
          />
        </div>

        {/* Mark as Failed Confirmation Modal */}
        <MarkFailedModal
          show={showMarkFailedConfirm}
          session={session}
          onConfirm={confirmMarkFailed}
          onCancel={cancelMarkFailed}
        />

      </main>
    </div>
  </ProtectedRoute>
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

  const normalizeAutopilotProfile = (profile: Record<string, any> | null): Record<string, any> | null => {
    if (!profile) return null;

    const normalized = { ...profile };
    const policyJsonCache = normalized.PolicyJsonCache ?? normalized.policyJsonCache;

    if (typeof policyJsonCache === "string" && policyJsonCache.trim()) {
      try {
        const parsed = JSON.parse(policyJsonCache);
        if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) {
          Object.assign(normalized, parsed);
        }
      } catch {
        // Ignore malformed cache payload and keep original fields
      }
    }

    const aadServerData = normalized.CloudAssignedAadServerData;
    if (typeof aadServerData === "string" && aadServerData.trim()) {
      try {
        const parsed = JSON.parse(aadServerData);
        const zeroTouchConfig = parsed?.ZeroTouchConfig;
        if (!normalized.CloudAssignedTenantDomain && zeroTouchConfig?.CloudAssignedTenantDomain) {
          normalized.CloudAssignedTenantDomain = zeroTouchConfig.CloudAssignedTenantDomain;
        }
        if (normalized.CloudAssignedForcedEnrollment === undefined && zeroTouchConfig?.ForcedEnrollment !== undefined) {
          normalized.CloudAssignedForcedEnrollment = zeroTouchConfig.ForcedEnrollment;
        }
      } catch {
        // Ignore malformed nested JSON
      }
    }

    return normalized;
  };

  const hasValue = (value: unknown): boolean => value !== undefined && value !== null && `${value}` !== "";

  const agentStarted = getEventData("agent_started");
  const bootTime = getEventData("boot_time");

  // Boot time from bootTimeUtc (device-reported UTC with Z suffix, parsed correctly as UTC by browser).
  // Now that event timestamps are also serialized with Z suffix (Kind=Utc), both are consistent.
  const estimatedBootTime = (bootTime?.bootTimeUtc || bootTime?.bootTime)
    ? new Date(bootTime?.bootTimeUtc ?? bootTime?.bootTime)
    : null;

  // Uptime until enrollment starts: calculated from boot time to first event timestamp.
  // Falls back to agent-reported uptimeMinutes if the timestamp diff is negative
  // (e.g. clock not synced at start, or device rebooted mid-enrollment).
  const uptimeUntilEnrollment = useMemo(() => {
    if (!bootTime || events.length === 0) return null;
    const bootTimeStr = bootTime.bootTimeUtc ?? bootTime.bootTime;
    if (!bootTimeStr) return null;
    const bootTimeMs = new Date(bootTimeStr).getTime();
    if (isNaN(bootTimeMs)) return null;

    const firstEventMs = Math.min(...events.map(e => new Date(e.timestamp).getTime()));
    const diffMs = firstEventMs - bootTimeMs;

    if (diffMs >= 0) {
      const totalMinutes = Math.floor(diffMs / 60000);
      const hours = Math.floor(totalMinutes / 60);
      const minutes = totalMinutes % 60;
      return `${hours}h ${minutes}m`;
    }

    // Fallback: agent-calculated uptime (always positive)
    const uptimeMinutes = typeof bootTime.uptimeMinutes === 'number' ? bootTime.uptimeMinutes : null;
    if (uptimeMinutes !== null && uptimeMinutes >= 0) {
      const hours = Math.floor(uptimeMinutes / 60);
      const minutes = uptimeMinutes % 60;
      return `${hours}h ${minutes}m`;
    }

    return null;
  }, [bootTime, events]);
  const osInfo = getEventData("os_info");
  const networkAdapters = getEventData("network_adapters");
  const dnsConfig = getEventData("dns_configuration");
  const proxyConfig = getEventData("proxy_configuration");
  const autopilotProfile = normalizeAutopilotProfile(getEventData("autopilot_profile"));
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
          {(estimatedBootTime || agentStarted?.agentVersion || imeVersion || aadJoinStatus?.joinType || deviceLocation?.country || deviceLocation?.Country || deviceLocation?.timezone || deviceLocation?.Timezone) && (
            <DetailSection title="System">
              {estimatedBootTime && (
                <DetailRow label="Boot Time" value={estimatedBootTime.toLocaleString([], { dateStyle: "short", timeStyle: "medium" })} />
              )}
              {uptimeUntilEnrollment && <DetailRow label="Uptime until enrollment starts" value={uptimeUntilEnrollment} />}
              {agentStarted?.agentVersion && <DetailRow label="Monitor Agent Version" value={agentStarted.agentVersion.replace(/\+([0-9a-f]{7})[0-9a-f]+$/, '+$1')} />}
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
              {hasValue(autopilotProfile.CloudAssignedTenantDomain) && <DetailRow label="Tenant Domain" value={`${autopilotProfile.CloudAssignedTenantDomain}`} />}
              {hasValue(autopilotProfile.DeploymentProfileName) && <DetailRow label="Profile Name" value={`${autopilotProfile.DeploymentProfileName}`} />}
              {hasValue(autopilotProfile.CloudAssignedTenantId) && <DetailRow label="Tenant ID" value={`${autopilotProfile.CloudAssignedTenantId}`} />}
              {hasValue(autopilotProfile.PolicyDownloadDate) && <DetailRow label="Policy Downloaded" value={new Date(autopilotProfile.PolicyDownloadDate).toLocaleString()} />}
              {hasValue(autopilotProfile.CloudAssignedOobeConfig) && <DetailRow label="OOBE Config" value={`${autopilotProfile.CloudAssignedOobeConfig}`} />}
              {hasValue(autopilotProfile.ZtdRegistrationId) && <DetailRow label="ZTD Registration ID" value={`${autopilotProfile.ZtdRegistrationId}`} />}
              {hasValue(autopilotProfile.AadDeviceId) && <DetailRow label="AAD Device ID" value={`${autopilotProfile.AadDeviceId}`} />}
              {autopilotProfile.CloudAssignedDomainJoinMethod !== undefined && (
                <DetailRow label="Domain Join Method" value={`${autopilotProfile.CloudAssignedDomainJoinMethod}` === "0" ? "Entra Join" : `${autopilotProfile.CloudAssignedDomainJoinMethod}`} />
              )}
              {autopilotProfile.CloudAssignedForcedEnrollment !== undefined && (
                <DetailRow label="Forced Enrollment" value={`${autopilotProfile.CloudAssignedForcedEnrollment}` === "1" ? "Yes" : "No"} />
              )}
              {hasValue(autopilotProfile.AutopilotCreationDate) && <DetailRow label="Autopilot Created" value={new Date(autopilotProfile.AutopilotCreationDate).toLocaleString()} />}
              {hasValue(autopilotProfile.ProfileAvailable) && (
                <DetailRow label="Profile Available" value={`${autopilotProfile.ProfileAvailable}` === "1" ? "Yes" : `${autopilotProfile.ProfileAvailable}`} />
              )}
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

