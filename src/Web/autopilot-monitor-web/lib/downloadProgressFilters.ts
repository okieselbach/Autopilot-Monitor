// Pure predicates extracted from `components/DownloadProgress.tsx` so the filter
// behaviour can be unit-tested without a DOM/JSX harness.

export interface DownloadFilterInput {
  bytesDownloaded: number;
  bytesTotal: number;
  status: string;
  isDownloadStartEvent: boolean;
  isSkippedEvent: boolean;
  progressPercent: number;
}

// Returns true if the event should be dropped because its declared total size is
// below 1 KB and the event isn't a terminal/skip/start signal.
//
// V2 emits bytesTotal=100 for WinGet/Store apps (e.g. Company Portal) which have
// no DO byte progress. Without exempting `app_download_started`, those apps would
// never appear in the UI — V1 had emitted bytesTotal=0 and slipped through the
// next filter instead.
export function shouldSkipLowBytesTotal(input: DownloadFilterInput): boolean {
  const { bytesTotal, status, isSkippedEvent, isDownloadStartEvent } = input;
  return (
    bytesTotal > 0 &&
    bytesTotal < 1024 &&
    status !== "completed" &&
    status !== "failed" &&
    !isSkippedEvent &&
    !isDownloadStartEvent
  );
}

// Returns true if the event has no activity (zero bytes, not started, not progressed)
// and should not yet appear as a download row.
export function shouldSkipNoActivity(input: DownloadFilterInput): boolean {
  const { bytesDownloaded, bytesTotal, status, isDownloadStartEvent, isSkippedEvent, progressPercent } = input;
  return (
    bytesDownloaded === 0 &&
    bytesTotal === 0 &&
    status !== "completed" &&
    status !== "failed" &&
    !isDownloadStartEvent &&
    !isSkippedEvent &&
    progressPercent < 100
  );
}
