"use client";

import { useMemo, useState } from "react";

interface InstallEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, any>;
}

interface SummaryStats {
  totalApps?: number;
  installing?: number;
  installed?: number;
  failed?: number;
}

interface InstallProgressProps {
  events: InstallEvent[];
  summaryStats?: SummaryStats | null;
}

interface InstallItem {
  appName: string;
  appId: string;
  state: "Installing" | "Installed" | "Failed" | "Skipped";
  startedAt?: string;
  completedAt?: string;
  durationMs?: number;
  isCompleted: boolean;
  isError: boolean;
  errorDetail?: string;
  errorPatternId?: string;
  firstSeenIndex: number;
  eventData?: Record<string, any>;
}

function formatDuration(ms: number): string {
  const seconds = Math.floor(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  if (minutes < 60) return `${minutes}m ${remainingSeconds}s`;
  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  return `${hours}h ${remainingMinutes}m`;
}

export default function InstallProgress({ events, summaryStats }: InstallProgressProps) {
  const installs = useMemo(() => {
    if (events.length === 0) return [];

    const sortedEvents = [...events].sort(
      (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
    );

    const installMap = new Map<string, InstallItem>();
    let insertionIndex = 0;

    for (const evt of sortedEvents) {
      const d = evt.data;
      if (!d) continue;

      const appName = d.appName ?? d.app_name ?? d.appId ?? d.app_id ?? "Unknown App";
      if (appName === "Unknown App") continue;

      const appId = d.appId ?? d.app_id ?? appName;
      const existing = installMap.get(appName);
      const eventTs = evt.timestamp;

      if (evt.eventType === "app_install_started") {
        installMap.set(appName, {
          appName,
          appId,
          state: "Installing",
          startedAt: existing?.startedAt ?? eventTs,
          isCompleted: false,
          isError: false,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      } else if (evt.eventType === "app_install_completed") {
        const startTime = existing?.startedAt ? new Date(existing.startedAt).getTime() : null;
        const endTime = new Date(eventTs).getTime();
        const duration = startTime ? endTime - startTime : undefined;

        installMap.set(appName, {
          appName,
          appId,
          state: "Installed",
          startedAt: existing?.startedAt,
          completedAt: eventTs,
          durationMs: duration,
          isCompleted: true,
          isError: false,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      } else if (evt.eventType === "app_install_failed") {
        const startTime = existing?.startedAt ? new Date(existing.startedAt).getTime() : null;
        const endTime = new Date(eventTs).getTime();
        const duration = startTime ? endTime - startTime : undefined;

        installMap.set(appName, {
          appName,
          appId,
          state: "Failed",
          startedAt: existing?.startedAt,
          completedAt: eventTs,
          durationMs: duration,
          isCompleted: true,
          isError: true,
          errorDetail: d.errorDetail ?? d.error_detail,
          errorPatternId: d.errorPatternId ?? d.error_pattern_id,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      } else if (evt.eventType === "app_install_skipped") {
        installMap.set(appName, {
          appName,
          appId,
          state: "Skipped",
          isCompleted: true,
          isError: false,
          firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
          eventData: d,
        });
      }
    }

    return Array.from(installMap.values()).sort((a, b) => a.firstSeenIndex - b.firstSeenIndex);
  }, [events]);

  const [expanded, setExpanded] = useState(true);

  // Calculate total wall-clock duration (earliest start → latest completion)
  // Must be before the early return to keep hooks in stable order across renders.
  const totalDuration = useMemo(() => {
    let earliest = Infinity;
    let latest = -Infinity;
    for (const item of installs) {
      if (item.startedAt) {
        const t = new Date(item.startedAt).getTime();
        if (t < earliest) earliest = t;
      }
      if (item.completedAt) {
        const t = new Date(item.completedAt).getTime();
        if (t > latest) latest = t;
      }
    }
    if (earliest !== Infinity && latest !== -Infinity && latest > earliest) {
      return latest - earliest;
    }
    return null;
  }, [installs]);

  if (installs.length === 0) return null;

  const activeCount = installs.filter(d => d.state === "Installing").length;
  const completedCount = installs.filter(d => d.state === "Installed").length;
  const failedCount = installs.filter(d => d.state === "Failed").length;
  const skippedCount = installs.filter(d => d.state === "Skipped").length;

  // Use summary stats for "X of Y" if available, fall back to local event counts
  const totalFromSummary = summaryStats?.totalApps;
  const installedFromSummary = summaryStats?.installed;

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center justify-between w-full text-left"
      >
        <div className="flex items-center space-x-2">
          <svg className="w-5 h-5 text-indigo-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">Install Progress</h2>
          {totalFromSummary != null && installedFromSummary != null && (
            <span className="text-xs text-gray-400">
              ({installedFromSummary} of {totalFromSummary} installed)
            </span>
          )}
          {totalDuration != null && (
            <span className="text-xs text-gray-400">
              — Total: {formatDuration(totalDuration)}
            </span>
          )}
          <div className="flex items-center space-x-2 text-xs">
            {activeCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-indigo-100 text-indigo-700 font-medium">
                {activeCount} active
              </span>
            )}
            {completedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-green-100 text-green-700 font-medium">
                {completedCount} completed
              </span>
            )}
            {failedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-red-100 text-red-700 font-medium">
                {failedCount} failed
              </span>
            )}
            {skippedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-gray-100 text-gray-600 font-medium">
                {skippedCount} skipped
              </span>
            )}
          </div>
        </div>
        <span className="text-gray-400">{expanded ? '▼' : '▶'}</span>
      </button>

      {expanded && <div className="space-y-3 mt-4">
        {installs.map((item) => (
          <InstallItemRow key={item.appName} item={item} />
        ))}
      </div>}
    </div>
  );
}

function InstallItemRow({ item }: { item: InstallItem }) {
  const [showDetails, setShowDetails] = useState(false);

  const containerClass = item.state === "Skipped"
    ? "bg-gray-50 border border-gray-300"
    : item.isError
      ? "bg-red-50 border border-red-200"
      : item.isCompleted
        ? "bg-green-50 border border-green-200"
        : "bg-gray-50 border border-gray-200";

  return (
    <div className={`rounded-lg p-3 ${containerClass}`}>
      <div className="flex items-center justify-between mb-1">
        <div className="flex items-center space-x-2 min-w-0">
          {item.state === "Skipped" ? (
            <svg className="w-4 h-4 text-gray-400 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 5l7 7-7 7M5 5l7 7-7 7" />
            </svg>
          ) : item.isError ? (
            <svg className="w-4 h-4 text-red-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          ) : item.isCompleted ? (
            <svg className="w-4 h-4 text-green-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
            </svg>
          ) : (
            <svg className="w-4 h-4 text-indigo-500 flex-shrink-0 animate-pulse" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z" />
            </svg>
          )}
          <span className={`text-sm font-medium truncate ${item.state === "Skipped" ? "text-gray-500" : "text-gray-900"}`}>
            {item.appName}
          </span>
          {item.state === "Skipped" && (
            <span className="text-xs px-2 py-0.5 rounded-full bg-gray-200 text-gray-600 font-medium">Skipped</span>
          )}
          {item.state === "Failed" && (
            <span className="text-xs px-2 py-0.5 rounded-full bg-red-200 text-red-700 font-medium">Failed</span>
          )}
        </div>
        <div className="flex items-center space-x-3 text-xs text-gray-500 flex-shrink-0 ml-2">
          {item.durationMs != null && item.durationMs > 0 && (
            <span className="font-medium">{formatDuration(item.durationMs)}</span>
          )}
          {item.eventData && Object.keys(item.eventData).length > 0 && (
            <button
              onClick={() => setShowDetails(!showDetails)}
              className="text-xs text-blue-600 hover:text-blue-800"
            >
              {showDetails ? 'Hide' : 'Details'}
            </button>
          )}
        </div>
      </div>

      {/* Error detail */}
      {item.isError && item.errorDetail && (
        <div className="mt-1 text-xs text-red-600">
          {item.errorDetail}
        </div>
      )}

      {/* Event details (expandable) */}
      {showDetails && item.eventData && (
        <div className="mt-3 p-2 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
          <pre>{JSON.stringify(item.eventData, null, 2)}</pre>
        </div>
      )}
    </div>
  );
}
