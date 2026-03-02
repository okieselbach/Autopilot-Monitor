"use client";

import { useMemo, useState } from "react";

interface DownloadEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, any>;
}

interface SummaryStats {
  totalApps?: number;
  downloading?: number;
  installed?: number;
  installing?: number;
  skipped?: number;
  failed?: number;
  pending?: number;
}

interface DownloadProgressProps {
  events: DownloadEvent[];
  summaryStats?: SummaryStats | null;
}

interface DoStats {
  fileSize: number;
  bytesFromPeers: number;
  bytesFromHttp: number;
  percentPeerCaching: number;
  downloadMode: number;
  downloadDuration: string;
  bytesFromLanPeers: number;
  bytesFromGroupPeers: number;
  bytesFromInternetPeers: number;
}

interface DownloadItem {
  appName: string;
  bytesDownloaded: number;
  bytesTotal: number;
  downloadRateBps: number;
  lastUpdated: string;
  lastUpdatedMs: number;
  isComplete: boolean;
  isSkipped: boolean;
  firstSeenIndex: number;
  eventData?: Record<string, any>;
  doStats?: DoStats | null;
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

function formatDoMode(mode: number): string {
  switch (mode) {
    case 0: return "Background";
    case 1: return "Foreground";
    case 2: return "Bypass/LAN Only";
    case 99: return "Simple";
    case 100: return "Bypass";
    default: return `Mode ${mode}`;
  }
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

function parseDoDurationMs(duration: string): number {
  if (!duration) return 0;
  const match = duration.match(/^(\d+):(\d+):(\d+)\.?(\d+)?$/);
  if (!match) return 0;
  const hours = parseInt(match[1], 10);
  const mins = parseInt(match[2], 10);
  const secs = parseInt(match[3], 10);
  const frac = match[4] ? parseInt(match[4].padEnd(3, "0").substring(0, 3), 10) : 0;
  return ((hours * 3600) + (mins * 60) + secs) * 1000 + frac;
}

function formatDoDuration(duration: string): string {
  const ms = parseDoDurationMs(duration);
  if (ms <= 0) return duration || "N/A";
  return formatDuration(ms);
}

export default function DownloadProgress({ events, summaryStats }: DownloadProgressProps) {
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
      const isSkippedEvent = evt.eventType === "app_install_skipped";
      const progressPercent = parseInt(d.progress_percent ?? d.progressPercent ?? "0", 10);
      const eventTs = new Date(evt.timestamp).getTime();

      // Determine if complete: explicit status, bytes comparison, or 100% progress with no byte data
      const isComplete = status === "completed" || status === "failed"
        || (bytesTotal > 0 && bytesDownloaded >= bytesTotal)
        || (progressPercent >= 100 && bytesDownloaded === 0 && bytesTotal === 0);

      // Skip if bytesTotal is too small (< 1 KB) AND not explicitly completed/failed/skipped
      if (bytesTotal > 0 && bytesTotal < 1024 && status !== "completed" && status !== "failed" && !isSkippedEvent) {
        continue;
      }

      // Skip if no download activity yet (0 bytes downloaded, not completed/failed/skipped)
      // This makes downloads appear dynamically as they start
      if (
        bytesDownloaded === 0 &&
        bytesTotal === 0 &&
        status !== "completed" &&
        status !== "failed" &&
        !isDownloadStartEvent &&
        !isSkippedEvent &&
        progressPercent < 100
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

      // Extract DO telemetry if present in event data
      let doStats: DoStats | null = existing?.doStats ?? null;
      if (d.doFileSize !== undefined || d.doBytesFromPeers !== undefined || d.doDownloadMode !== undefined) {
        doStats = {
          fileSize: parseInt(d.doFileSize ?? "0", 10),
          bytesFromPeers: parseInt(d.doBytesFromPeers ?? "0", 10),
          bytesFromHttp: parseInt(d.doBytesFromHttp ?? "0", 10),
          percentPeerCaching: parseInt(d.doPercentPeerCaching ?? "0", 10),
          downloadMode: parseInt(d.doDownloadMode ?? "-1", 10),
          downloadDuration: d.doDownloadDuration ?? "",
          bytesFromLanPeers: parseInt(d.doBytesFromLanPeers ?? "0", 10),
          bytesFromGroupPeers: parseInt(d.doBytesFromGroupPeers ?? "0", 10),
          bytesFromInternetPeers: parseInt(d.doBytesFromInternetPeers ?? "0", 10),
        };
      }

      downloadMap.set(appName, {
        appName,
        bytesDownloaded: isNaN(bytesDownloaded) ? 0 : bytesDownloaded,
        bytesTotal: isNaN(bytesTotal) ? 0 : bytesTotal,
        downloadRateBps: effectiveRateBps,
        lastUpdated: evt.timestamp,
        lastUpdatedMs: Number.isFinite(eventTs) ? eventTs : (existing?.lastUpdatedMs ?? Date.now()),
        isComplete,
        isSkipped: isSkippedEvent || (existing?.isSkipped ?? false),
        firstSeenIndex: existing?.firstSeenIndex ?? insertionIndex++,
        eventData: d,
        doStats,
      });
    }

    return Array.from(downloadMap.values()).sort((a, b) => {
      // Keep stable visual order based on first appearance only.
      // This avoids active items jumping when completion status changes.
      return a.firstSeenIndex - b.firstSeenIndex;
    });
  }, [events]);

  const [expanded, setExpanded] = useState(true);

  // Sum of individual download durations (from DO telemetry per app)
  // Must be before the early return to keep hooks in stable order across renders.
  const totalDuration = useMemo(() => {
    let sum = 0;
    let hasAny = false;
    for (const dl of downloads) {
      if (dl.doStats?.downloadDuration) {
        const ms = parseDoDurationMs(dl.doStats.downloadDuration);
        if (ms > 0) {
          sum += ms;
          hasAny = true;
        }
      }
    }
    return hasAny ? sum : null;
  }, [downloads]);

  if (downloads.length === 0) return null;

  const activeCount = downloads.filter(d => !d.isComplete && !d.isSkipped).length;
  const completedCount = downloads.filter(d => d.isComplete && !d.isSkipped).length;
  const skippedCount = downloads.filter(d => d.isSkipped).length;

  // "X of Y downloaded" from summary stats: apps past download phase / total apps that actually download
  const totalDownloadable = summaryStats?.totalApps != null && summaryStats?.skipped != null && summaryStats?.pending != null
    ? summaryStats.totalApps - summaryStats.skipped
    : null;
  const downloadedCount = summaryStats?.installed != null && summaryStats?.installing != null && summaryStats?.failed != null
    ? summaryStats.installed + summaryStats.installing + summaryStats.failed
    : null;

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
          {totalDownloadable != null && downloadedCount != null ? (
            <span className="text-xs text-gray-400">({downloadedCount} of {totalDownloadable} downloaded)</span>
          ) : (
            <span className="text-xs text-gray-400">({downloads.length} {downloads.length === 1 ? 'download' : 'downloads'})</span>
          )}
          {totalDuration != null && (
            <span className="text-xs text-gray-400">
              — Total: {formatDuration(totalDuration)}
            </span>
          )}
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
        {downloads.map((dl) => {
          const progressPercent = dl.bytesTotal > 0
            ? Math.min(100, (dl.bytesDownloaded / dl.bytesTotal) * 100)
            : (dl.isComplete ? 100 : 0);

          return <DownloadItem key={dl.appName} download={dl} progressPercent={progressPercent} />;
        })}
      </div>}
    </div>
  );
}

function DownloadItem({ download: dl, progressPercent }: { download: DownloadItem; progressPercent: number }) {
  const [showDetails, setShowDetails] = useState(false);
  const [showDoStats, setShowDoStats] = useState(false);
  const hasKnownTotal = dl.bytesTotal > 0;
  const showProgressBar = !dl.isSkipped && (hasKnownTotal || dl.isComplete || !dl.isComplete);

  // Determine container styling
  const containerClass = dl.isSkipped
    ? "bg-gray-50 border border-gray-300"
    : dl.isComplete
      ? "bg-green-50 border border-green-200"
      : "bg-gray-50 border border-gray-200";

  return (
            <div className={`rounded-lg p-3 ${containerClass}`}>
              <div className="flex items-center justify-between mb-1">
                <div className="flex items-center space-x-2 min-w-0">
                  {dl.isSkipped ? (
                    <svg className="w-4 h-4 text-gray-400 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 5l7 7-7 7M5 5l7 7-7 7" />
                    </svg>
                  ) : dl.isComplete ? (
                    <svg className="w-4 h-4 text-green-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
                    </svg>
                  ) : (
                    <svg className="w-4 h-4 text-blue-500 flex-shrink-0 animate-pulse" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                    </svg>
                  )}
                  <span className={`text-sm font-medium truncate ${dl.isSkipped ? "text-gray-500" : "text-gray-900"}`}>{dl.appName}</span>
                  {dl.isSkipped && (
                    <span className="text-xs px-2 py-0.5 rounded-full bg-gray-200 text-gray-600 font-medium">Skipped</span>
                  )}
                </div>
                <div className="flex items-center space-x-3 text-xs text-gray-500 flex-shrink-0 ml-2">
                  {!dl.isComplete && !dl.isSkipped && dl.downloadRateBps > 0 && (
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

              {/* Progress bar - not shown for skipped apps */}
              {showProgressBar && !dl.isSkipped && (
                <div className="mt-1">
                  <div className="w-full h-2 bg-gray-200 rounded-full overflow-hidden">
                    <div
                      className={`h-full rounded-full transition-all duration-500 ${
                        dl.isComplete ? "bg-green-500" : "bg-blue-500"
                      }`}
                      style={{ width: `${progressPercent}%` }}
                    />
                  </div>
                  <div className="flex items-center justify-between mt-1 text-xs text-gray-500">
                    <span>
                      {hasKnownTotal
                        ? `${formatBytes(dl.bytesDownloaded)} / ${formatBytes(dl.bytesTotal)}`
                        : dl.isComplete
                          ? "Completed"
                          : `${formatBytes(dl.bytesDownloaded)} downloaded`}
                    </span>
                    <span>{progressPercent > 0 ? `${progressPercent.toFixed(0)}%` : "started"}</span>
                  </div>
                </div>
              )}

              {/* If no total size known, show downloaded amount (not for skipped or completed-with-no-bytes) */}
              {!dl.isSkipped && !dl.isComplete && dl.bytesTotal === 0 && dl.bytesDownloaded > 0 && (
                <div className="mt-1 text-xs text-gray-500">
                  Downloaded: {formatBytes(dl.bytesDownloaded)}
                  {dl.downloadRateBps > 0 && ` at ${formatSpeed(dl.downloadRateBps)}`}
                </div>
              )}

              {/* Delivery Optimization stats (collapsible) */}
              {dl.doStats && dl.doStats.fileSize > 0 && (
                <div className="mt-2">
                  <button
                    onClick={() => setShowDoStats(!showDoStats)}
                    className="flex items-center space-x-1 text-xs text-blue-600 hover:text-blue-800"
                  >
                    <svg className="w-3.5 h-3.5 text-blue-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                    </svg>
                    <span className="font-medium">Delivery Optimization</span>
                    <span className="text-blue-500">({formatDoMode(dl.doStats.downloadMode)})</span>
                    <span className="text-blue-400">{showDoStats ? '▼' : '▶'}</span>
                  </button>
                  {showDoStats && (
                    <div className="mt-1 p-2 bg-blue-50 rounded border border-blue-100">
                      <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-xs text-gray-600">
                        <div>
                          <span className="text-gray-500">Duration:</span>{" "}
                          <span className="font-medium">{formatDoDuration(dl.doStats.downloadDuration)}</span>
                        </div>
                        <div>
                          <span className="text-gray-500">File Size:</span>{" "}
                          <span className="font-medium">{formatBytes(dl.doStats.fileSize)}</span>
                        </div>
                        <div>
                          <span className="text-gray-500">From HTTP:</span>{" "}
                          <span className="font-medium">{formatBytes(dl.doStats.bytesFromHttp)}</span>
                        </div>
                        <div>
                          <span className="text-gray-500">From Peers:</span>{" "}
                          <span className="font-medium">
                            {formatBytes(dl.doStats.bytesFromPeers)}
                            {dl.doStats.percentPeerCaching > 0 && (
                              <span className="text-green-600 ml-1">({dl.doStats.percentPeerCaching}%)</span>
                            )}
                          </span>
                        </div>
                        {dl.doStats.bytesFromPeers > 0 && (
                          <div className="col-span-2 mt-1 text-xs text-gray-500">
                            Peer breakdown: LAN {formatBytes(dl.doStats.bytesFromLanPeers)}
                            {" | "}Group {formatBytes(dl.doStats.bytesFromGroupPeers)}
                            {" | "}Internet {formatBytes(dl.doStats.bytesFromInternetPeers)}
                          </div>
                        )}
                      </div>
                      {/* Peer vs HTTP visual bar */}
                      <div className="mt-1.5 w-full h-1.5 bg-gray-200 rounded-full overflow-hidden flex">
                        <div
                          className="h-full bg-green-400"
                          style={{ width: `${dl.doStats.percentPeerCaching}%` }}
                          title={`${dl.doStats.percentPeerCaching}% from peers`}
                        />
                        <div
                          className="h-full bg-blue-400"
                          style={{ width: `${100 - dl.doStats.percentPeerCaching}%` }}
                          title={`${100 - dl.doStats.percentPeerCaching}% from HTTP`}
                        />
                      </div>
                    </div>
                  )}
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
