"use client";

import { useMemo, useState } from "react";

interface DownloadEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, any>;
}

interface DownloadProgressProps {
  events: DownloadEvent[];
}

interface DownloadItem {
  appName: string;
  bytesDownloaded: number;
  bytesTotal: number;
  downloadRateBps: number;
  lastUpdated: string;
  lastUpdatedMs: number;
  isComplete: boolean;
  firstSeenIndex: number;
  eventData?: Record<string, any>;
}

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

export default function DownloadProgress({ events }: DownloadProgressProps) {
  const downloads = useMemo(() => {
    if (events.length === 0) return [];

    const sortedEvents = [...events].sort(
      (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
    );

    const downloadMap = new Map<string, DownloadItem>();
    let insertionIndex = 0;

    for (const evt of sortedEvents) {
      const d = evt.data;
      if (!d) continue;

      const appName = d.app_name ?? d.appName ?? d.file_name ?? d.fileName ?? d.app_id ?? d.appId ?? "Unknown App";

      // Skip unknown apps
      if (appName === "Unknown App") continue;

      const bytesDownloaded = parseInt(d.bytes_downloaded ?? d.bytesDownloaded ?? "0", 10);
      const bytesTotal = parseInt(d.bytes_total ?? d.bytesTotal ?? "0", 10);
      const reportedRateBps = parseFloat(d.download_rate_bps ?? d.downloadRateBps ?? "0");
      const status = d.status ?? "";
      const isDownloadStartEvent = evt.eventType === "app_download_started";
      const eventTs = new Date(evt.timestamp).getTime();

      // Determine if complete: explicit status or bytes comparison
      const isComplete = status === "completed" || status === "failed" || (bytesTotal > 0 && bytesDownloaded >= bytesTotal);

      // Skip if bytesTotal is too small (< 1 KB) AND not explicitly completed/failed
      if (bytesTotal > 0 && bytesTotal < 1024 && status !== "completed" && status !== "failed") {
        continue;
      }

      // Skip if no download activity yet (0 bytes downloaded, not completed/failed)
      // This makes downloads appear dynamically as they start
      if (
        bytesDownloaded === 0 &&
        bytesTotal === 0 &&
        status !== "completed" &&
        status !== "failed" &&
        !isDownloadStartEvent
      ) {
        continue;
      }

      const existing = downloadMap.get(appName);
      // Max plausible rate: 250 MB/s (~2 Gbit/s, generous for any real client connection)
      const MAX_PLAUSIBLE_BPS = 250 * 1024 * 1024;

      let effectiveRateBps = isNaN(reportedRateBps) ? 0 : reportedRateBps;
      if (effectiveRateBps > MAX_PLAUSIBLE_BPS) effectiveRateBps = 0;

      if (effectiveRateBps <= 0 && existing && Number.isFinite(eventTs) && Number.isFinite(existing.lastUpdatedMs)) {
        const elapsedSeconds = (eventTs - existing.lastUpdatedMs) / 1000;
        const deltaBytes = (isNaN(bytesDownloaded) ? 0 : bytesDownloaded) - existing.bytesDownloaded;
        if (elapsedSeconds > 0 && deltaBytes > 0) {
          const calculatedRate = deltaBytes / elapsedSeconds;
          effectiveRateBps = calculatedRate <= MAX_PLAUSIBLE_BPS ? calculatedRate : 0;
        } else {
          effectiveRateBps = existing.downloadRateBps;
        }
      }

      downloadMap.set(appName, {
        appName,
        bytesDownloaded: isNaN(bytesDownloaded) ? 0 : bytesDownloaded,
        bytesTotal: isNaN(bytesTotal) ? 0 : bytesTotal,
        downloadRateBps: effectiveRateBps,
        lastUpdated: evt.timestamp,
        lastUpdatedMs: Number.isFinite(eventTs) ? eventTs : (existing?.lastUpdatedMs ?? Date.now()),
        isComplete,
        firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
        eventData: d,
      });
    }

    return Array.from(downloadMap.values()).sort((a, b) => {
      // Keep stable visual order based on first appearance only.
      // This avoids active items jumping when completion status changes.
      return a.firstSeenIndex - b.firstSeenIndex;
    });
  }, [events]);

  const [expanded, setExpanded] = useState(true);

  if (downloads.length === 0) return null;

  const activeCount = downloads.filter(d => !d.isComplete).length;
  const completedCount = downloads.filter(d => d.isComplete).length;

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center justify-between w-full text-left"
      >
        <div className="flex items-center space-x-2">
          <svg className="w-5 h-5 text-blue-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">Download Progress</h2>
          <span className="text-xs text-gray-400">({downloads.length} {downloads.length === 1 ? 'download' : 'downloads'})</span>
          <div className="flex items-center space-x-2 text-xs">
            {activeCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-blue-100 text-blue-700 font-medium">
                {activeCount} active
              </span>
            )}
            {completedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-green-100 text-green-700 font-medium">
                {completedCount} completed
              </span>
            )}
          </div>
        </div>
        <span className="text-gray-400">{expanded ? '▼' : '▶'}</span>
      </button>

      {expanded && <div className="space-y-3 mt-4">
        {downloads.map((dl) => {
          const progressPercent = dl.bytesTotal > 0
            ? Math.min(100, (dl.bytesDownloaded / dl.bytesTotal) * 100)
            : 0;

          return <DownloadItem key={dl.appName} download={dl} progressPercent={progressPercent} />;
        })}
      </div>}
    </div>
  );
}

function DownloadItem({ download: dl, progressPercent }: { download: DownloadItem; progressPercent: number }) {
  const [showDetails, setShowDetails] = useState(false);
  const hasKnownTotal = dl.bytesTotal > 0;
  const showProgressBar = hasKnownTotal || !dl.isComplete;

  return (
            <div
              className={`rounded-lg p-3 ${dl.isComplete ? "bg-green-50 border border-green-200" : "bg-gray-50 border border-gray-200"}`}
            >
              <div className="flex items-center justify-between mb-1">
                <div className="flex items-center space-x-2 min-w-0">
                  {dl.isComplete ? (
                    <svg className="w-4 h-4 text-green-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                    </svg>
                  ) : (
                    <svg className="w-4 h-4 text-blue-500 flex-shrink-0 animate-pulse" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                    </svg>
                  )}
                  <span className="text-sm font-medium text-gray-900 truncate">{dl.appName}</span>
                </div>
                <div className="flex items-center space-x-3 text-xs text-gray-500 flex-shrink-0 ml-2">
                  {!dl.isComplete && dl.downloadRateBps > 0 && (
                    <span className="font-medium text-blue-600">{formatSpeed(dl.downloadRateBps)}</span>
                  )}
                  {dl.eventData && Object.keys(dl.eventData).length > 0 && (
                    <button
                      onClick={() => setShowDetails(!showDetails)}
                      className="text-xs text-blue-600 hover:text-blue-800"
                    >
                      {showDetails ? 'Hide' : 'Details'}
                    </button>
                  )}
                </div>
              </div>

              {/* Progress bar */}
              {showProgressBar && (
                <div className="mt-1">
                  <div className="w-full h-2 bg-gray-200 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all duration-500 ${
                        dl.isComplete ? "bg-green-500" : "bg-blue-500"
                      }`}
                      style={{ width: `${hasKnownTotal ? progressPercent : 0}%` }}
                    />
                  </div>
                  <div className="flex items-center justify-between mt-1 text-xs text-gray-500">
                    <span>
                      {hasKnownTotal
                        ? `${formatBytes(dl.bytesDownloaded)} / ${formatBytes(dl.bytesTotal)}`
                        : `${formatBytes(dl.bytesDownloaded)} downloaded`}
                    </span>
                    <span>{hasKnownTotal ? `${progressPercent.toFixed(0)}%` : "started"}</span>
                  </div>
                </div>
              )}

              {/* If no total size known, show downloaded amount */}
              {dl.bytesTotal === 0 && dl.bytesDownloaded > 0 && (
                <div className="mt-1 text-xs text-gray-500">
                  Downloaded: {formatBytes(dl.bytesDownloaded)}
                  {dl.downloadRateBps > 0 && ` at ${formatSpeed(dl.downloadRateBps)}`}
                </div>
              )}

              {/* Event details (expandable) */}
              {showDetails && dl.eventData && (
                <div className="mt-3 p-2 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto">
                  <pre>{JSON.stringify(dl.eventData, null, 2)}</pre>
                </div>
              )}
            </div>
  );
}
