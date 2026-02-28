"use client";

import { EnrollmentEvent } from "../page";
import { V1_PHASES, V2_PHASES } from "../utils/phaseConstants";

interface PhaseTimelineProps {
  currentPhase: number;
  completedPhases: number[];
  events?: EnrollmentEvent[];
  sessionStatus?: string;
  enrollmentType?: string;
}

export default function PhaseTimeline({ currentPhase, completedPhases, events = [], sessionStatus, enrollmentType }: PhaseTimelineProps) {
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

  // Helper: get first/last event timestamp for a phase
  const getFirstEventTime = (phaseId: number): number | null => {
    const ts = events
      .filter(e => e.phase === phaseId)
      .map(e => new Date(e.timestamp).getTime());
    return ts.length > 0 ? Math.min(...ts) : null;
  };
  const getLastEventTime = (phaseId: number): number | null => {
    const ts = events
      .filter(e => e.phase === phaseId)
      .map(e => new Date(e.timestamp).getTime());
    return ts.length > 0 ? Math.max(...ts) : null;
  };

  const formatDuration = (ms: number): string | null => {
    const durationSec = Math.round(ms / 1000);
    if (durationSec < 5) return null;
    if (durationSec < 60) return `${durationSec}s`;
    if (durationSec < 3600) return `${Math.floor(durationSec / 60)}m ${durationSec % 60}s`;
    return `${Math.floor(durationSec / 3600)}h ${Math.floor((durationSec % 3600) / 60)}m`;
  };

  // Calculate phase durations using phase-transition timestamps
  // Parent phases (Device Setup, Account Setup) span until the next major phase starts
  // Sub-phases (Apps Device, Apps User) show only their own event range
  const getPhaseDuration = (phaseId: number): string | null => {
    const isV2 = enrollmentType === "v2";

    // No timing for Start and Device Preparation (agent not active yet)
    if (phaseId === 0 || phaseId === 1) return null;
    // No timing for Complete
    if (phaseId === 7) return null;

    if (isV2) {
      // V2: simpler phases — no sub-phase nesting
      // Phase 3 (App Installation): first phase 3 event → first phase 6 event (or last phase 3)
      // Phase 6 (Finalizing): first phase 6 event → first phase 7 event (or last phase 6)
      const start = getFirstEventTime(phaseId);
      if (!start) return null;

      const nextPhaseMap: Record<number, number> = { 3: 6, 6: 7 };
      const nextPhase = nextPhaseMap[phaseId];
      const end = (nextPhase !== undefined ? getFirstEventTime(nextPhase) : null) ?? getLastEventTime(phaseId);
      if (!end || end <= start) return null;

      return formatDuration(end - start);
    }

    // V1 phase-transition-based durations:
    // Device Setup (2): first phase 2 → first phase 4 (includes Apps Device sub-phase)
    // Apps Device (3): first phase 3 → last phase 3 (sub-phase only)
    // Account Setup (4): first phase 4 → first phase 6 (includes Apps User sub-phase)
    // Apps User (5): first phase 5 → last phase 5 (sub-phase only)
    // Finalizing (6): first phase 6 → first phase 7 (or last phase 6)
    const start = getFirstEventTime(phaseId);
    if (!start) return null;

    let end: number | null = null;
    switch (phaseId) {
      case 2: // Device Setup → ends when Account Setup starts
        end = getFirstEventTime(4) ?? getLastEventTime(3) ?? getLastEventTime(2);
        break;
      case 3: // Apps (Device) — sub-phase, own event range
        end = getLastEventTime(3);
        break;
      case 4: // Account Setup → ends when Finalizing starts
        end = getFirstEventTime(6) ?? getLastEventTime(5) ?? getLastEventTime(4);
        break;
      case 5: // Apps (User) — sub-phase, own event range
        end = getLastEventTime(5);
        break;
      case 6: // Finalizing → ends when Complete starts
        end = getFirstEventTime(7) ?? getLastEventTime(6);
        break;
      default:
        end = getLastEventTime(phaseId);
        break;
    }

    if (!end || end <= start) return null;
    return formatDuration(end - start);
  };

  // Check if a phase is a sub-phase (Apps Device / Apps User in V1)
  const isSubPhase = (phaseId: number): boolean => {
    if (enrollmentType === "v2") return false;
    return phaseId === 3 || phaseId === 5;
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
                  <div className={`mt-0.5 text-[10px] ${
                    status === 'failed' ? 'text-red-400' :
                    isSubPhase(phase.id) ? 'text-gray-400 italic' : 'text-gray-500 font-medium'
                  }`}>
                    {isSubPhase(phase.id) ? `(${getPhaseDuration(phase.id)})` : getPhaseDuration(phase.id)}
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
