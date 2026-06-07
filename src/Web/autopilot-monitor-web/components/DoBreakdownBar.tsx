"use client";

import { formatBytes } from "@/lib/formatting";

interface DoBreakdownBarProps {
  /** Bytes served from DO peers. */
  peers: number;
  /** Bytes served from a Microsoft Connected Cache (MCC) node. */
  cacheServer: number;
  /** Bytes served from HTTP/CDN (the "pure CDN" remainder; net of MCC). */
  http: number;
  /**
   * Denominator for the segment widths. Defaults to peers + cacheServer + http.
   * DownloadProgress passes the DO-reported file size so any unaccounted bytes
   * fall into the HTTP remainder — preserving its original per-app bar math.
   */
  total?: number;
  /** MCC node URI/IP, surfaced in the segment tooltip when present. */
  cacheHost?: string;
  /** Render a compact "Peers · MCC · CDN" byte legend under the bar. */
  showLegend?: boolean;
  className?: string;
}

/**
 * Three-segment bar showing how a download was sourced: peers (green) | MCC
 * (purple) | HTTP/CDN (blue). HTTP is the remainder of the bar so any total not
 * covered by peers/MCC reads as CDN. Shared by the per-app session view
 * (DownloadProgress) and the aggregated Delivery Optimization card.
 */
export default function DoBreakdownBar({
  peers,
  cacheServer,
  http,
  total,
  cacheHost,
  showLegend = false,
  className,
}: DoBreakdownBarProps) {
  const denom = total != null && total > 0 ? total : Math.max(1, peers + cacheServer + http);
  const peerPct = (peers / denom) * 100;
  const cachePct = (cacheServer / denom) * 100;
  const httpPct = Math.max(0, 100 - peerPct - cachePct);

  return (
    <div className={className}>
      <div className="w-full h-1.5 bg-gray-200 rounded-full overflow-hidden flex">
        <div
          className="h-full bg-green-400"
          style={{ width: `${peerPct}%` }}
          title={`${peerPct.toFixed(1)}% from peers`}
        />
        <div
          className="h-full bg-purple-400"
          style={{ width: `${cachePct}%` }}
          title={`${cachePct.toFixed(1)}% from Connected Cache${cacheHost ? ` (${cacheHost})` : ""}`}
        />
        <div
          className="h-full bg-blue-400"
          style={{ width: `${httpPct}%` }}
          title={`${httpPct.toFixed(1)}% from HTTP/CDN`}
        />
      </div>
      {showLegend && (
        <div className="mt-1.5 flex flex-wrap gap-x-4 gap-y-0.5 text-xs text-gray-600">
          <span><span className="inline-block w-2 h-2 rounded-sm bg-green-400 mr-1 align-middle" />Peers {formatBytes(peers)}</span>
          <span><span className="inline-block w-2 h-2 rounded-sm bg-purple-400 mr-1 align-middle" />MCC {formatBytes(cacheServer)}</span>
          <span><span className="inline-block w-2 h-2 rounded-sm bg-blue-400 mr-1 align-middle" />CDN {formatBytes(http)}</span>
        </div>
      )}
    </div>
  );
}
