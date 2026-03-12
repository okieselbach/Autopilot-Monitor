"use client";

import { useEffect, useState } from "react";

interface PlatformStatsManifest {
  latest: string;
  generatedAtUtc: string;
}

interface PlatformStatsPayload {
  totalEnrollments?: number;
  totalUsers?: number;
  totalTenants?: number;
  totalSignedUpTenants?: number;
  uniqueDeviceModels?: number;
  totalEventsProcessed?: number;
  successfulEnrollments?: number;
  issuesDetected?: number;
  lastFullCompute?: string;
  lastUpdated?: string;
}

function resolvePlatformStatsManifestUrl(rawUrl?: string): string {
  const trimmed = rawUrl?.trim();
  if (!trimmed) {
    return "/platform-stats.json";
  }

  const hashIndex = trimmed.indexOf("#");
  const withoutHash = hashIndex >= 0 ? trimmed.slice(0, hashIndex) : trimmed;
  const hash = hashIndex >= 0 ? trimmed.slice(hashIndex) : "";

  const queryIndex = withoutHash.indexOf("?");
  const basePath = queryIndex >= 0 ? withoutHash.slice(0, queryIndex) : withoutHash;
  const query = queryIndex >= 0 ? withoutHash.slice(queryIndex) : "";

  if (/\.json$/i.test(basePath)) {
    return trimmed;
  }

  const normalizedBasePath = basePath.endsWith("/") ? basePath.slice(0, -1) : basePath;
  const manifestPath = `${normalizedBasePath}/platform-stats.json`;
  return `${manifestPath}${query}${hash}`;
}

const PLATFORM_STATS_MANIFEST_URL =
  resolvePlatformStatsManifestUrl(process.env.NEXT_PUBLIC_PLATFORM_STATS_MANIFEST_URL);

const DEFAULT_PLATFORM_STATS = {
  totalEnrollments: 123,
  totalTenants: 2,
  totalSignedUpTenants: 2,
  uniqueDeviceModels: 1,
  totalEventsProcessed: 0,
  issuesDetected: 0,
  lastUpdated: null as string | null,
};

export function PlatformStats() {
  const [stats, setStats] = useState(DEFAULT_PLATFORM_STATS);

  useEffect(() => {
    let cancelled = false;

    const loadPlatformStats = async () => {
      try {
        const manifestResponse = await fetch(PLATFORM_STATS_MANIFEST_URL, { cache: "no-store" });
        if (!manifestResponse.ok) {
          return;
        }

        const manifest = (await manifestResponse.json()) as PlatformStatsManifest;
        if (!manifest?.latest) {
          return;
        }

        const versionedUrl = new URL(manifest.latest, manifestResponse.url).toString();
        const statsResponse = await fetch(versionedUrl, { cache: "force-cache" });
        if (!statsResponse.ok) {
          return;
        }

        const payload = (await statsResponse.json()) as PlatformStatsPayload;
        if (cancelled) {
          return;
        }

        setStats({
          totalEnrollments: payload.totalEnrollments ?? DEFAULT_PLATFORM_STATS.totalEnrollments,
          totalTenants: payload.totalTenants ?? DEFAULT_PLATFORM_STATS.totalTenants,
          totalSignedUpTenants: payload.totalSignedUpTenants ?? DEFAULT_PLATFORM_STATS.totalSignedUpTenants,
          uniqueDeviceModels: payload.uniqueDeviceModels ?? DEFAULT_PLATFORM_STATS.uniqueDeviceModels,
          totalEventsProcessed: payload.totalEventsProcessed ?? DEFAULT_PLATFORM_STATS.totalEventsProcessed,
          issuesDetected: payload.issuesDetected ?? DEFAULT_PLATFORM_STATS.issuesDetected,
          lastUpdated: payload.lastUpdated ?? null,
        });
      } catch {
        // Keep defaults if manifest/versioned files are not reachable.
      }
    };

    loadPlatformStats();
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <div className="mt-10 text-xs text-gray-400">
      <div className="flex flex-wrap items-center justify-center gap-x-3 gap-y-2 sm:gap-x-4">
        <span><span className="font-semibold text-gray-600">{stats.totalSignedUpTenants.toLocaleString()}</span> organisations</span>
        <span className="w-px h-2.5 bg-gray-300 hidden sm:inline-block" />
        <span><span className="font-semibold text-gray-600">{stats.uniqueDeviceModels.toLocaleString()}</span> device models</span>
        <span className="w-px h-2.5 bg-gray-300 hidden sm:inline-block" />
        <span><span className="font-semibold text-gray-600">{stats.totalEnrollments.toLocaleString()}</span> enrollments monitored</span>
        <span className="w-px h-2.5 bg-gray-300 hidden sm:inline-block" />
        <span><span className="font-semibold text-gray-600">{stats.issuesDetected.toLocaleString()}</span> detected issues</span>
        <span className="w-px h-2.5 bg-gray-300 hidden sm:inline-block" />
        <span><span className="font-semibold text-gray-600">{stats.totalEventsProcessed.toLocaleString()}</span> events processed</span>
      </div>
      {stats.lastUpdated && (
        <p className="mt-1.5 text-[10px] text-gray-300">
          last updated: {new Date(stats.lastUpdated).toLocaleString()}
        </p>
      )}
    </div>
  );
}
