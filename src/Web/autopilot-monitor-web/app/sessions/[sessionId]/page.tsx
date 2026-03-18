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
import InstallProgress from '../../../components/InstallProgress';
import ScriptExecutions from '../../../components/ScriptExecutions';
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

import { V1_PHASE_NAMES, V2_PHASE_NAMES, V1_PHASE_ORDER, V2_PHASE_ORDER } from "./utils/phaseConstants";
import { groupEventsByPhase } from "./utils/eventHelpers";
import SessionInfoCard from "./components/SessionInfoCard";
import PhaseTimeline from "./components/PhaseTimeline";
import EventTimeline from "./components/EventTimeline";
import AnalysisResultsSection from "./components/AnalysisResultsSection";
import MarkFailedModal from "./components/MarkFailedModal";
import ReportSessionModal from "./components/ReportSessionModal";
import { UnifiedSidebar, SidebarItem } from "../../../components/UnifiedSidebar";
import { InformationCircleIcon, ComputerDesktopIcon, PlayCircleIcon, SparklesIcon, ChartBarIcon, CodeBracketIcon, ArrowDownTrayIcon, ListBulletIcon, ClockIcon } from "../../../lib/sidebarIcons";
import DeviceDetailsCard from "./components/DeviceDetailsCard";
import { generateUiExport, generateCsvExport, generateSessionCsvExport, generateRuleResultsCsvExport, SessionExportEvent } from "@/lib/sessionExportUtils";
import { Session, EnrollmentEvent, RuleResult } from "@/types";

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
  const [showReportModal, setShowReportModal] = useState(false);
  const [reportSubmitting, setReportSubmitting] = useState(false);
  const [showScriptOutput, setShowScriptOutput] = useState(true);
  const [analysisResults, setAnalysisResults] = useState<RuleResult[]>([]);
  const [loadingAnalysis, setLoadingAnalysis] = useState(false);
  const [analysisExpanded, setAnalysisExpanded] = useState(true);
  const [phaseTimelineExpanded, setPhaseTimelineExpanded] = useState(true);
  const [perfExpanded, setPerfExpanded] = useState(true);
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
      // Fetch showScriptOutput setting (best-effort)
      (async () => {
        try {
          const res = await authenticatedFetch(
            `${API_BASE_URL}/api/config/${sessionTenantId}`,
            getAccessToken
          );
          if (res.ok) {
            const cfg = await res.json();
            setShowScriptOutput(cfg.showScriptOutput ?? true);
          }
        } catch { /* non-fatal */ }
      })();
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
    const handleEventStream = (data: { sessionId: string; tenantId: string; newEventCount: number; newRuleResults?: RuleResult[] }) => {
      console.log('Event stream signal received via SignalR:', data);
      if (data.sessionId !== sessionIdRef.current) return;

      // Fetch full events from storage (single source of truth), but debounce bursts.
      // Session updates arrive via the "newevents" message (tenant group) — no session
      // object in this signal to keep payloads minimal.
      scheduleFetchEvents();

      if (data.tenantId) {
        setSessionTenantId(prev => prev || data.tenantId);
      }

      // Rule results from SignalR (only on enrollment completion)
      if (data.newRuleResults && data.newRuleResults.length > 0) {
        fetchAnalysisResults();
      }
    };

    // Listen for session delta updates via the tenant group ("newevents").
    // This replaces the full session object that was previously sent inside "eventStream".
    const handleNewEvents = (data: { sessionId: string; tenantId: string; sessionUpdate?: Partial<Session> }) => {
      if (data.sessionId !== sessionIdRef.current) return;

      if (data.sessionUpdate) {
        setSession(prev => prev ? { ...prev, ...data.sessionUpdate } : prev);
      }
      if (data.tenantId) {
        setSessionTenantId(prev => prev || data.tenantId);
      }
    };

    on('eventStream', handleEventStream);
    on('newevents', handleNewEvents);

    return () => {
      off('eventStream', handleEventStream);
      off('newevents', handleNewEvents);
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
      const knownTenantId = resolveEffectiveTenantId();
      const endpoint = knownTenantId
        ? `${API_BASE_URL}/api/sessions/${sessionId}?tenantId=${knownTenantId}`
        : galacticAdminMode
          ? `${API_BASE_URL}/api/galactic/sessions`
          : `${API_BASE_URL}/api/sessions/${sessionId}`;

      const response = await authenticatedFetch(endpoint, getAccessToken);
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
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
        return;
      }
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
      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/sessions/${sessionId}/events?tenantId=${effectiveTenantId}`,
        getAccessToken
      );
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
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error("Failed to fetch events:", error);
        addNotification('error', 'Backend Not Reachable', 'Unable to load session events. Please check your connection.', 'session-events-fetch-error');
      }
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

      const reanalyzeParam = reanalyze ? '&reanalyze=true' : '';
      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/sessions/${sessionId}/analysis?tenantId=${effectiveTenantId}${reanalyzeParam}`,
        getAccessToken
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
      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/sessions/${sessionId}/mark-failed?tenantId=${effectiveTenantId}`,
        getAccessToken,
        { method: 'POST' }
      );

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

  const handleSubmitReport = async (
    comment: string, email: string,
    screenshotBase64: string | null, screenshotFileName: string | null,
    agentLogBase64: string | null, agentLogFileName: string | null
  ) => {
    const effectiveTenantId = sessionTenantId || tenantId;
    try {
      setReportSubmitting(true);

      // Generate TXT and CSV exports from the events currently loaded
      const exportEvents: SessionExportEvent[] = events.map(e => ({
        ...e,
        tenantId: effectiveTenantId || '',
      }));
      const timelineExportTxt = generateUiExport(exportEvents, sessionId, effectiveTenantId || '', session?.status);
      const eventsCsv = generateCsvExport(exportEvents);
      const sessionCsv = session ? generateSessionCsvExport(session) : '';
      const ruleResultsCsv = generateRuleResultsCsvExport(analysisResults);

      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/sessions/${sessionId}/report?tenantId=${effectiveTenantId}`,
        getAccessToken,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            tenantId: effectiveTenantId,
            sessionId,
            comment,
            email,
            sessionCsv,
            eventsCsv,
            ruleResultsCsv,
            timelineExportTxt,
            screenshotBase64,
            screenshotFileName,
            agentLogBase64,
            agentLogFileName
          })
        }
      );

      if (response.ok) {
        addNotification('success', 'Report Submitted', 'Session report has been submitted for analysis.', 'report-success');
      } else {
        const data = await response.json().catch(() => null);
        const message = data?.message || 'Failed to submit report.';
        addNotification('error', 'Report Failed', message, 'report-error');
        throw new Error(message);
      }
    } catch (err: any) {
      // Re-throw so the modal can show inline error feedback.
      // Only log unexpected errors (not the ones we threw ourselves above).
      if (!err?.message?.includes('Failed to submit report') && !err?.message?.includes('Failed to get access token')) {
        console.error('Error submitting report:', err);
      }
      throw err;
    } finally {
      setReportSubmitting(false);
    }
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

  // Filter events by severity for the timeline.
  // Trace events are always excluded — they are for backend-side auditing only.
  const filteredEvents = useMemo(() =>
    events.filter(e => e.severity !== "Trace" && severityFilters.has(e.severity)),
    [events, severityFilters]
  );

  // Extract latest app_tracking_summary state-breakdown for progress headers
  const appSummaryStats = useMemo(() => {
    const summaryEvents = events.filter(e => e.eventType === "app_tracking_summary");
    if (summaryEvents.length === 0) return null;
    const latest = summaryEvents[summaryEvents.length - 1];
    const d = latest.data;
    if (!d) return null;
    return {
      totalApps: parseInt(d.totalApps ?? "0", 10),
      completedApps: parseInt(d.completedApps ?? "0", 10),
      downloading: parseInt(d.downloading ?? "0", 10),
      installing: parseInt(d.installing ?? "0", 10),
      installed: parseInt(d.installed ?? "0", 10),
      skipped: parseInt(d.skipped ?? "0", 10),
      failed: parseInt(d.failed ?? "0", 10),
      pending: parseInt(d.pending ?? "0", 10),
    };
  }, [events]);

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

  // Detect SkipUserStatusPage from esp_config_detected event
  const isSkipUserStatusPage = useMemo(() => {
    if (session?.enrollmentType === "v2") return false;
    const espConfigEvent = events.find(e => e.eventType === "esp_config_detected");
    if (!espConfigEvent?.data) return false;
    const val = espConfigEvent.data.skipUserStatusPage;
    return val === true || val === "True" || val === "true";
  }, [events, session?.enrollmentType]);

  // Detect WhiteGlove session and find the split point
  const isWhiteGloveSession = session?.isPreProvisioned === true ||
    events.some(e => e.eventType === "whiteglove_complete");

  const whiteGloveSplitSequence = useMemo(() => {
    if (!isWhiteGloveSession) return -1;
    const wgEvent = events.find(e => e.eventType === "whiteglove_complete");

    // Anchor the split relative to whiteglove_complete, robust against reboots
    // in both Part 1 (pre-prov) and Part 2 (user enrollment).
    const agentStarts = events
      .filter(e => e.eventType === "agent_started")
      .sort((a, b) => a.sequence - b.sequence);

    if (wgEvent && agentStarts.length >= 2) {
      // Normal: first agent_started AFTER whiteglove_complete = Part 2 start
      const nextStart = agentStarts.find(a => a.sequence > wgEvent.sequence);
      if (nextStart) return nextStart.sequence - 1;

      // Race condition: whiteglove_complete arrived after the Part 2 boot
      // (Windows writes Event 62407 after reboot). The LAST agent_started
      // before wgEvent is the Part 2 start.
      const preWgStarts = agentStarts.filter(a => a.sequence < wgEvent.sequence);
      if (preWgStarts.length >= 2) {
        return preWgStarts[preWgStarts.length - 1].sequence - 1;
      }
    }

    // Single boot (pre-prov only, user hasn't enrolled yet): use whiteglove_complete
    return wgEvent?.sequence ?? -1;
  }, [events, isWhiteGloveSession]);

  // For WhiteGlove sessions: split filtered events into pre-provisioning and user-enrollment parts.
  // Events are assigned purely by sequence number — no special-casing for whiteglove_complete.
  // In the race-condition case (Windows writes the WhiteGlove success event after the reboot),
  // whiteglove_complete naturally lands in the user-enrollment part, preserving chronological order.
  const preProvEvents = useMemo(() => {
    if (!isWhiteGloveSession || whiteGloveSplitSequence < 0) return [] as EnrollmentEvent[];
    return filteredEvents.filter(e => e.sequence <= whiteGloveSplitSequence);
  }, [filteredEvents, isWhiteGloveSession, whiteGloveSplitSequence]);

  const userEnrollEvents = useMemo(() => {
    if (!isWhiteGloveSession || whiteGloveSplitSequence < 0) return [] as EnrollmentEvent[];
    return filteredEvents.filter(e => e.sequence > whiteGloveSplitSequence);
  }, [filteredEvents, isWhiteGloveSession, whiteGloveSplitSequence]);

  // Compute per-block durations for WhiteGlove sessions (using unfiltered events for accuracy).
  // Duration 1 = pre-provisioning, Duration 2 = user enrollment, combined = D1 + D2 (pause excluded).
  const whiteGloveDurations = useMemo(() => {
    if (!isWhiteGloveSession || whiteGloveSplitSequence < 0) {
      return { preProvDuration: null as string | null, userEnrollDuration: null as string | null, combinedDuration: null as string | null };
    }

    const preProvEvts = events.filter(e => e.sequence <= whiteGloveSplitSequence);
    const userEnrollEvts = events.filter(e => e.sequence > whiteGloveSplitSequence);

    const calcMs = (evts: EnrollmentEvent[]): number => {
      if (evts.length === 0) return 0;
      const ts = evts.map(e => new Date(e.timestamp).getTime());
      return Math.max(...ts) - Math.min(...ts);
    };

    const fmt = (ms: number): string | null => {
      const sec = Math.round(ms / 1000);
      if (sec < 1) return null;
      if (sec < 60) return `${sec}s`;
      if (sec < 3600) return `${Math.floor(sec / 60)}m ${sec % 60}s`;
      return `${Math.floor(sec / 3600)}h ${Math.floor((sec % 3600) / 60)}m`;
    };

    const preProvMs = calcMs(preProvEvts);
    const userEnrollMs = calcMs(userEnrollEvts);

    return {
      preProvDuration: fmt(preProvMs),
      userEnrollDuration: fmt(userEnrollMs),
      combinedDuration: fmt(preProvMs + userEnrollMs),
    };
  }, [events, isWhiteGloveSession, whiteGloveSplitSequence]);

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
      ? groupEventsByPhase(userEnrollEvents, phaseNamesMap, phaseOrder, { preventPhaseRegression: true })
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

  const scrollToPhase = (phaseName: string) => {
    const id = `phase-${phaseName.replace(/[^a-zA-Z0-9]/g, '-')}`;
    // Try both WhiteGlove prefixed and plain ids
    const el = document.getElementById(id);
    if (el) {
      // Expand the phase section if collapsed
      setExpandedPhases(prev => {
        const newExpanded = new Set(prev);
        // For WhiteGlove sessions, the expandedPhases key may be prefixed
        if (isWhiteGloveSession) {
          newExpanded.add(`pre-${phaseName}`);
          newExpanded.add(`user-${phaseName}`);
        } else {
          newExpanded.add(phaseName);
        }
        return newExpanded;
      });
      // Scroll after a tick so the section is expanded
      setTimeout(() => {
        el.scrollIntoView({ behavior: 'smooth', block: 'center' });
      }, 50);
    }
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
      <UnifiedSidebar items={(() => {
        const s: SidebarItem[] = [];
        if (session) s.push({ id: "section-session-info", label: "Session Info", icon: <InformationCircleIcon /> });
        if (!isGatherRulesSession) s.push({ id: "section-device-details", label: "Device Details", icon: <ComputerDesktopIcon /> });
        if (!isGatherRulesSession && session) s.push({ id: "section-enrollment-progress", label: "Enrollment Progress", icon: <PlayCircleIcon /> });
        if (!isGatherRulesSession) s.push({ id: "section-analysis", label: "Analysis", icon: <SparklesIcon /> });
        if (!isGatherRulesSession) s.push({ id: "section-performance", label: "Performance", icon: <ChartBarIcon /> });
        if (!isGatherRulesSession) s.push({ id: "section-scripts", label: "Script Executions", icon: <CodeBracketIcon /> });
        if (!isGatherRulesSession) s.push({ id: "section-downloads", label: "Downloads", icon: <ArrowDownTrayIcon /> });
        if (!isGatherRulesSession) s.push({ id: "section-install-progress", label: "Install Progress", icon: <ListBulletIcon /> });
        s.push({ id: "section-event-timeline", label: "Event Timeline", icon: <ClockIcon /> });
        return s;
      })()} mode="scroll-spy" title="Sections">
      {/* Header */}
      <header className="bg-white shadow">
        <div className="py-6 px-4 sm:px-6 lg:px-8 flex items-center justify-between">
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
                    const res = await authenticatedFetch(
                      `${API_BASE_URL}/api/diagnostics/download-url?tenantId=${session.tenantId}&blobName=${encodeURIComponent(session.diagnosticsBlobName!)}`,
                      getAccessToken
                    );
                    if (!res.ok) throw new Error('Failed to get download URL');
                    const data = await res.json();
                    window.open(data.downloadUrl, '_blank');
                  } catch (err) {
                    if (err instanceof TokenExpiredError) {
                      addNotification('error', 'Session Expired', err.message, 'session-expired-error');
                    } else {
                      console.error('Diagnostics download failed:', err);
                    }
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
            <button
              onClick={() => setShowReportModal(true)}
              className="px-4 py-2 bg-white border border-blue-300 text-blue-700 rounded-md hover:bg-blue-50 transition-colors flex items-center gap-2 text-sm"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
              </svg>
              Report Session
            </button>
          </div>
        </div>
      </header>

      <main className="py-6 px-4 sm:px-6 lg:px-8">
        <div className="px-4 py-6 sm:px-0">
          {/* Session Info Card */}
          {session && (
            <div id="section-session-info">
            <SessionInfoCard
              session={session}
              enrollmentDuration={isWhiteGloveSession && whiteGloveDurations.combinedDuration ? whiteGloveDurations.combinedDuration : enrollmentDurationFromEvents}
              displayStatus={displayStatus}
              isGatherRulesSession={isGatherRulesSession}
            />
            </div>
          )}

          {/* Device Details Card (from enrollment tracker events) */}
          {!isGatherRulesSession && <div id="section-device-details"><DeviceDetailsCard events={events} /></div>}

          {/* Phase Timeline */}
          {!isGatherRulesSession && session && (
            <div id="section-enrollment-progress" className="bg-white shadow rounded-lg p-6 mb-6">
              <button
                onClick={() => setPhaseTimelineExpanded(!phaseTimelineExpanded)}
                className="flex items-center justify-between w-full text-left"
              >
                <h2 className="text-xl font-semibold text-gray-900">Enrollment Progress</h2>
                <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${phaseTimelineExpanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
                </svg>
              </button>
              {phaseTimelineExpanded && (
                <PhaseTimeline
                  currentPhase={session.currentPhase}
                  completedPhases={session.status === 'Succeeded' ? [7] : []}
                  events={events}
                  sessionStatus={session.status}
                  enrollmentType={session.enrollmentType}
                  isPreProvisioned={isWhiteGloveSession}
                  isSkipUserStatusPage={isSkipUserStatusPage}
                  onPhaseClick={scrollToPhase}
                />
              )}
            </div>
          )}

          {/* Analysis Results */}
          {!isGatherRulesSession && (
            <div id="section-analysis">
            <AnalysisResultsSection
              analysisResults={analysisResults}
              loadingAnalysis={loadingAnalysis}
              analysisExpanded={analysisExpanded}
              setAnalysisExpanded={setAnalysisExpanded}
              onReanalyze={() => fetchAnalysisResults(true)}
            />
            </div>
          )}

          {/* Performance Metrics (from performance_snapshot events) */}
          {!isGatherRulesSession && (
            <div id="section-performance">
            <PerformanceChart
              events={events.filter(e => e.eventType === "performance_snapshot")}
              expanded={perfExpanded}
              setExpanded={setPerfExpanded}
            />
            </div>
          )}

          {/* Script Executions (from script_completed, script_failed events) */}
          {!isGatherRulesSession && (
            <div id="section-scripts">
            <ScriptExecutions
              events={events.filter(
                e => e.eventType === "script_completed" || e.eventType === "script_failed"
              )}
              showScriptOutput={showScriptOutput}
            />
            </div>
          )}

          {/* Download Progress (from download_progress, app_download_started, app_install_skipped events) */}
          {!isGatherRulesSession && (
            <div id="section-downloads">
            <DownloadProgress
              events={events.filter(
                e => e.eventType === "download_progress" || e.eventType === "app_download_started" || e.eventType === "app_install_skipped"
              )}
              summaryStats={appSummaryStats}
            />
            </div>
          )}

          {/* Install Progress (from app_install_started, app_install_completed, app_install_failed, app_install_skipped events) */}
          {!isGatherRulesSession && (
            <div id="section-install-progress">
            <InstallProgress
              events={events.filter(
                e => e.eventType === "app_install_started" || e.eventType === "app_install_completed" || e.eventType === "app_install_failed" || e.eventType === "app_install_skipped"
              )}
              summaryStats={appSummaryStats}
            />
            </div>
          )}

          {/* Event Timeline (with severity filters, expand/collapse, WhiteGlove split) */}
          <div id="section-event-timeline">
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
            preProvDuration={whiteGloveDurations.preProvDuration}
            userEnrollDuration={whiteGloveDurations.userEnrollDuration}
            showScriptOutput={showScriptOutput}
          />
          </div>
        </div>

        {/* Mark as Failed Confirmation Modal */}
        <MarkFailedModal
          show={showMarkFailedConfirm}
          session={session}
          onConfirm={confirmMarkFailed}
          onCancel={cancelMarkFailed}
        />

        {/* Report Session Modal */}
        <ReportSessionModal
          show={showReportModal}
          session={session}
          events={events}
          analysisResults={analysisResults}
          onSubmit={handleSubmitReport}
          onCancel={() => setShowReportModal(false)}
          submitting={reportSubmitting}
        />

      </main>
      </UnifiedSidebar>
    </div>
  </ProtectedRoute>
  );
}

