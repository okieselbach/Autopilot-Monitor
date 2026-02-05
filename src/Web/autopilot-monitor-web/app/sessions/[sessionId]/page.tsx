"use client";

import { useEffect, useState, useRef } from "react";
import { useParams, useRouter } from "next/navigation";
import { useSignalR } from "../../../contexts/SignalRContext";
import { useTenant } from "../../../contexts/TenantContext";
import { ProtectedRoute } from '../../../components/ProtectedRoute';
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
  const [showMarkFailedConfirm, setShowMarkFailedConfirm] = useState(false);
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

  // Fetch events when we have the session's tenant ID
  useEffect(() => {
    if (sessionTenantId && sessionId) {
      fetchEvents();
    }
  }, [sessionTenantId, sessionId]);

  // Join SignalR groups when connected (for multi-tenancy and cost optimization)
  useEffect(() => {
    // Use the session's tenant ID if available
    const effectiveTenantId = sessionTenantId || tenantId;
    if (!sessionId || !isConnected || !effectiveTenantId) return;

    if (!hasJoinedGroups.current) {
      // Join both tenant group (for newEvents) and session group (for eventStream)
      const tenantGroupName = `tenant-${effectiveTenantId}`;
      const sessionGroupName = `session-${effectiveTenantId}-${sessionId}`;

      joinGroup(tenantGroupName);
      joinGroup(sessionGroupName);
      hasJoinedGroups.current = true;
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
    const handleEventStream = (data: { sessionId: string; tenantId: string; events: EnrollmentEvent[]; session: Session }) => {
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

      const response = await fetch(endpoint);
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
      const response = await fetch(`${API_BASE_URL}/api/sessions/${sessionId}/events?tenantId=${effectiveTenantId}`);
      if (response.ok) {
        const data = await response.json();
        const eventsWithPhaseNames = data.events.map((e: EnrollmentEvent) => ({
          ...e,
          phaseName: phaseNames[e.phase] || "Unknown"
        }));
        setEvents(eventsWithPhaseNames);
      }
    } catch (error) {
      console.error("Failed to fetch events:", error);
    } finally {
      setLoading(false);
    }
  };

  const markAsFailed = () => {
    setShowMarkFailedConfirm(true);
  };

  const confirmMarkFailed = async () => {
    // Use the session's tenant ID if available
    const effectiveTenantId = sessionTenantId || tenantId;

    try {
      const response = await fetch(`${API_BASE_URL}/api/sessions/${sessionId}/mark-failed?tenantId=${effectiveTenantId}`, {
        method: 'POST',
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

  // Group events by phase
  const eventsByPhase = events.reduce((acc, event) => {
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
                <InfoItem label="Status" value={<StatusBadge status={session.status} />} />
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
              />
            </div>
          )}

          {/* Timeline */}
          <div className="bg-white shadow rounded-lg p-6">
            <div className="flex items-center justify-between mb-6">
              <h2 className="text-xl font-semibold text-gray-900">Event Timeline</h2>
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

function PhaseTimeline({ currentPhase, completedPhases }: { currentPhase: number; completedPhases: number[] }) {
  console.log('[PhaseTimeline] currentPhase:', currentPhase, 'completedPhases:', completedPhases);

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

  const getPhaseStatus = (phaseId: number) => {
    // If phase is completed, show as completed (green) even if it's the current phase
    if (completedPhases.includes(phaseId)) return 'completed';
    // If it's the current phase but not completed yet, show as current (blue)
    if (phaseId === currentPhase) return 'current';
    // If it's before the current phase, show as completed (for phases that were skipped or not explicitly tracked)
    if (phaseId < currentPhase) return 'completed';
    return 'pending';
  };

  const getPhaseColor = (status: string) => {
    switch (status) {
      case 'completed': return 'bg-green-500 text-white border-green-500';
      case 'current': return 'bg-blue-500 text-white border-blue-500 ring-4 ring-blue-200';
      case 'pending': return 'bg-gray-200 text-gray-500 border-gray-300';
      default: return 'bg-gray-200 text-gray-500 border-gray-300';
    }
  };

  const getConnectorColor = (fromPhase: number) => {
    const fromStatus = getPhaseStatus(fromPhase);
    const toStatus = getPhaseStatus(fromPhase + 1);
    return fromStatus === 'completed' ? 'bg-green-500' : 'bg-gray-300';
  };

  return (
    <div className="w-full overflow-x-auto py-4">
      <div className="flex items-center w-full">
        {phases.map((phase, index) => {
          const status = getPhaseStatus(phase.id);
          const nextStatus = index < phases.length - 1 ? getPhaseStatus(phases[index + 1].id) : null;
          const showArrow = status === 'current';

          return (
            <div key={phase.id} className="flex items-center" style={{ flex: index < phases.length - 1 ? '1 1 0%' : '0 0 auto' }}>
              <div className="flex flex-col items-center flex-shrink-0">
                <div className={`w-12 h-12 rounded-full flex items-center justify-center border-2 transition-all font-semibold ${getPhaseColor(status)}`}>
                  {status === 'completed' ? '✓' : phase.id + 1}
                </div>
                <div className="mt-3 text-xs font-medium text-center text-gray-700 whitespace-nowrap">
                  {phase.shortName}
                </div>
              </div>
              {index < phases.length - 1 && (
                <div className="flex items-center flex-1 mx-3 mb-8">
                  <div className={`h-1 flex-1 transition-all ${getConnectorColor(phase.id)}`} />
                  {showArrow && (
                    <div className="relative" style={{ marginLeft: '-6px', marginRight: '-6px' }}>
                      <div
                        className={`w-0 h-0 border-t-[8px] border-t-transparent border-b-[8px] border-b-transparent border-l-[12px] ${
                          getConnectorColor(phase.id) === 'bg-green-500' ? 'border-l-green-500' : 'border-l-gray-300'
                        }`}
                        style={{
                          filter: getConnectorColor(phase.id) === 'bg-green-500'
                            ? 'drop-shadow(0 0 3px rgba(34, 197, 94, 0.5))'
                            : 'drop-shadow(0 0 3px rgba(209, 213, 219, 0.5))'
                        }}
                      />
                    </div>
                  )}
                  {!showArrow && <div className={`h-1 flex-1 transition-all ${getConnectorColor(phase.id)}`} />}
                </div>
              )}
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

function StatusBadge({ status }: { status: string }) {
  const statusConfig = {
    InProgress: { color: "bg-blue-100 text-blue-800", text: "In Progress" },
    Succeeded: { color: "bg-green-100 text-green-800", text: "Succeeded" },
    Failed: { color: "bg-red-100 text-red-800", text: "Failed" },
    Unknown: { color: "bg-gray-100 text-gray-800", text: "Unknown" },
  };

  const config = statusConfig[status as keyof typeof statusConfig] || statusConfig.Unknown;

  return (
    <span className={`px-2 inline-block text-xs leading-5 font-semibold rounded-full ${config.color}`}>
      {config.text}
    </span>
  );
}
