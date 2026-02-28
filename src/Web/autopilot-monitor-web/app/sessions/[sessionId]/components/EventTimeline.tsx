"use client";

import { useState, useMemo } from "react";
import { EnrollmentEvent, Session } from "../page";
import { normalizeEventDataForDisplay } from "../utils/eventHelpers";

interface EventTimelineProps {
  filteredEvents: EnrollmentEvent[];
  events: EnrollmentEvent[];
  session: Session | null;
  severityFilters: Set<string>;
  toggleSeverityFilter: (severity: string) => void;
  expandedPhases: Set<string>;
  togglePhase: (phaseName: string) => void;
  timelineExpanded: boolean;
  setTimelineExpanded: (expanded: boolean) => void;
  expandAll: () => void;
  collapseAll: () => void;
  isWhiteGloveSession: boolean;
  whiteGloveSplitSequence: number;
  orderedPhases: string[];
  eventsByPhase: Record<string, EnrollmentEvent[]>;
  preProvGrouped: { eventsByPhase: Record<string, EnrollmentEvent[]>; orderedPhases: string[] };
  userEnrollGrouped: { eventsByPhase: Record<string, EnrollmentEvent[]>; orderedPhases: string[] };
  userEnrollEvents: EnrollmentEvent[];
  isGalacticAdmin?: boolean;
}

export default function EventTimeline({
  filteredEvents,
  events,
  session,
  severityFilters,
  toggleSeverityFilter,
  expandedPhases,
  togglePhase,
  timelineExpanded,
  setTimelineExpanded,
  expandAll,
  collapseAll,
  isWhiteGloveSession,
  whiteGloveSplitSequence,
  orderedPhases,
  eventsByPhase,
  preProvGrouped,
  userEnrollGrouped,
  userEnrollEvents,
  isGalacticAdmin,
}: EventTimelineProps) {
  return (
    <>
      {/* Severity filters + Expand/Collapse — shared controls above the timeline(s) */}
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-xs font-medium text-gray-500">Filter:</span>
        {(["Debug", "Info", "Warning", "Error", "Critical"] as const).map((sev) => {
          const active = severityFilters.has(sev);
          const colors: Record<string, { on: string; off: string }> = {
            Debug:    { on: "bg-gray-200 text-gray-800",  off: "bg-gray-50 text-gray-400" },
            Info:     { on: "bg-blue-100 text-blue-800",  off: "bg-gray-50 text-gray-400" },
            Warning:  { on: "bg-yellow-100 text-yellow-800", off: "bg-gray-50 text-gray-400" },
            Error:    { on: "bg-red-100 text-red-800",    off: "bg-gray-50 text-gray-400" },
            Critical: { on: "bg-red-200 text-red-900",    off: "bg-gray-50 text-gray-400" },
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
        <span className="text-xs text-gray-400">({filteredEvents.length}/{events.length})</span>
        <div className="flex gap-1.5 ml-auto">
          <button
            onClick={expandAll}
            title="Expand All"
            className="flex items-center gap-1 px-2 py-1 text-xs bg-blue-50 text-blue-700 hover:bg-blue-100 rounded transition-colors"
          >
            <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
            </svg>
            <span className="hidden sm:inline">Expand All</span>
          </button>
          <button
            onClick={collapseAll}
            title="Collapse All"
            className="flex items-center gap-1 px-2 py-1 text-xs bg-gray-50 text-gray-700 hover:bg-gray-100 rounded transition-colors"
          >
            <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 15l7-7 7 7" />
            </svg>
            <span className="hidden sm:inline">Collapse All</span>
          </button>
        </div>
      </div>

      {/* Timeline — split for WhiteGlove sessions, single card otherwise */}
      {isWhiteGloveSession ? (
        <>
          {/* Pre-Provisioning Part */}
          <div className="bg-white shadow rounded-lg p-6">
            <div className="flex items-center gap-3 mb-6">
              <h2 className="text-xl font-semibold text-gray-900">Pre-Provisioning Part</h2>
              <span className="px-2 py-0.5 text-xs font-semibold rounded-full bg-amber-100 text-amber-800">WhiteGlove</span>
            </div>
            {preProvGrouped.orderedPhases.length === 0 ? (
              <div className="text-gray-500 text-center py-8">No events found.</div>
            ) : (
              <div className="space-y-8">
                {preProvGrouped.orderedPhases.map((phaseName) => (
                  <PhaseSection
                    key={`pre-${phaseName}`}
                    phaseName={phaseName}
                    events={preProvGrouped.eventsByPhase[phaseName]}
                    isExpanded={expandedPhases.has(`pre-${phaseName}`)}
                    onToggle={() => togglePhase(`pre-${phaseName}`)}
                    isGalacticAdmin={isGalacticAdmin}
                  />
                ))}
              </div>
            )}
          </div>

          {/* User Enrollment Part */}
          {userEnrollEvents.length > 0 ? (
            <div className="bg-white shadow rounded-lg p-6">
              <div className="flex items-center gap-3 mb-6">
                <h2 className="text-xl font-semibold text-gray-900">User Enrollment Part</h2>
                <span className="px-2 py-0.5 text-xs font-semibold rounded-full bg-blue-100 text-blue-800">Resumed</span>
              </div>
              {userEnrollGrouped.orderedPhases.length === 0 ? (
                <div className="text-gray-500 text-center py-8">No events found.</div>
              ) : (
                <div className="space-y-8">
                  {userEnrollGrouped.orderedPhases.map((phaseName) => (
                    <PhaseSection
                      key={`user-${phaseName}`}
                      phaseName={phaseName}
                      events={userEnrollGrouped.eventsByPhase[phaseName]}
                      isExpanded={expandedPhases.has(`user-${phaseName}`)}
                      onToggle={() => togglePhase(`user-${phaseName}`)}
                      isGalacticAdmin={isGalacticAdmin}
                    />
                  ))}
                </div>
              )}
            </div>
          ) : session?.status === 'Pending' ? (
            <div className="bg-amber-50 border border-amber-200 rounded-lg p-6 text-center">
              <p className="text-amber-800 font-medium mb-1">Awaiting User Enrollment</p>
              <p className="text-amber-600 text-sm">
                Pre-provisioning is complete. The timeline will continue when the user powers on the device.
              </p>
            </div>
          ) : null}
        </>
      ) : (
        /* Original single-timeline card */
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
                      isGalacticAdmin={isGalacticAdmin}
                    />
                  ))}
                </div>
              )}
            </>
          )}
        </div>
      )}
    </>
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
  const [showRaw, setShowRaw] = useState(false);
  const [copied, setCopied] = useState(false);
  const detailData = useMemo(() => normalizeEventDataForDisplay(event.data), [event.data]);

  // Gather rule console output detection — use source, not eventType,
  // because users can name gather rule event types freely.
  const isGatherEvent = event.source === "GatherRuleExecutor";
  const gatherOutput = isGatherEvent
    ? ((detailData?.output ?? detailData?.Output) as string | null | undefined) ?? null
    : null;
  const gatherCommand = isGatherEvent
    ? ((detailData?.command ?? detailData?.Command) as string | null | undefined) ?? null
    : null;
  const gatherExitCode = isGatherEvent
    ? ((detailData?.exit_code ?? detailData?.exitCode) as number | null | undefined) ?? null
    : null;
  const hasGatherOutput = gatherOutput != null && gatherOutput !== "";
  const formattedOutput = hasGatherOutput
    ? gatherOutput.replace(/\r\n/g, "\n").replace(/\r/g, "\n")
    : null;

  const copyEventId = async () => {
    try {
      await navigator.clipboard.writeText(event.eventId);
      setCopied(true);
      setTimeout(() => setCopied(false), 2000);
    } catch (err) {
      console.error('Failed to copy EventID:', err);
    }
  };

  const hasDetails = detailData && Object.keys(detailData).length > 0;

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
        {hasDetails && (
          <button
            onClick={() => setShowDetails(!showDetails)}
            className="text-xs text-blue-600 hover:text-blue-800 ml-4 flex-shrink-0"
          >
            {showDetails ? 'Hide' : hasGatherOutput ? 'Output' : 'Details'}
          </button>
        )}
      </div>

      {/* Gather rule: terminal-style output block */}
      {showDetails && hasGatherOutput && (
        <div className="mt-3">
          {gatherCommand && (
            <div className="flex items-center gap-1.5 mb-1.5 text-xs font-mono text-gray-600">
              <span className="text-gray-400 select-none">$</span>
              <span>{gatherCommand}</span>
            </div>
          )}
          <div className="bg-gray-900 rounded-lg overflow-hidden">
            <div className="px-3 py-2 max-h-96 overflow-y-auto overflow-x-auto">
              <pre className="text-xs text-gray-100 font-mono whitespace-pre">{formattedOutput}</pre>
            </div>
          </div>
          <div className="mt-1.5 flex items-center justify-between">
            {gatherExitCode !== null ? (
              <span className={`text-xs font-mono ${gatherExitCode === 0 ? 'text-green-600' : 'text-red-600'}`}>
                exit {gatherExitCode}
              </span>
            ) : <span />}
            <button
              onClick={() => setShowRaw(!showRaw)}
              className="text-xs text-gray-400 hover:text-gray-600"
            >
              {showRaw ? 'hide raw' : 'raw JSON'}
            </button>
          </div>
          {showRaw && (
            <div className="mt-2 p-3 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
              <pre>{JSON.stringify(detailData, null, 2)}</pre>
            </div>
          )}
        </div>
      )}

      {/* Non-gather (or gather without output): raw JSON details */}
      {showDetails && !hasGatherOutput && detailData && (
        <div className="mt-3 p-3 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
          <pre>{JSON.stringify(detailData, null, 2)}</pre>
        </div>
      )}
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
