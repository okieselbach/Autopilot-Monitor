"use client";

import { Suspense, useEffect, useState, useRef, useCallback } from "react";
import { useRouter, useSearchParams } from "next/navigation";
import { ProtectedRoute } from "../../../components/ProtectedRoute";
import { useTenant } from "../../../contexts/TenantContext";
import { useAuth } from "../../../contexts/AuthContext";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface SessionSummary {
  sessionId: string;
  tenantId: string;
  serialNumber: string;
  deviceName: string;
  manufacturer: string;
  model: string;
  startedAt: string;
  completedAt: string | null;
  status: string;
  failureReason: string | null;
  durationSeconds: number | null;
  enrollmentType: string;
}

interface LocationSessionsResponse {
  success: boolean;
  sessions: SessionSummary[];
  totalCount: number;
}

const statusConfig: Record<string, { color: string; text: string }> = {
  InProgress: { color: "bg-blue-100 text-blue-800", text: "In Progress" },
  Pending: { color: "bg-amber-100 text-amber-800", text: "Pending" },
  Succeeded: { color: "bg-green-100 text-green-800", text: "Succeeded" },
  Failed: { color: "bg-red-100 text-red-800", text: "Failed" },
  Unknown: { color: "bg-gray-100 text-gray-800", text: "Unknown" },
};

function StatusBadge({ status, failureReason }: { status: string; failureReason?: string | null }) {
  const config = statusConfig[status] || statusConfig.Unknown;
  const isTimeout = status === "Failed" && failureReason?.toLowerCase().includes("timed out");

  return (
    <span
      className={`px-2 inline-flex items-center gap-1 text-xs leading-5 font-semibold rounded-full ${config.color}`}
      title={failureReason || undefined}
    >
      {config.text}
      {isTimeout && <span title={failureReason!}>&#9201;&#65039;</span>}
    </span>
  );
}

function formatDuration(seconds: number | null): string {
  if (!seconds || seconds <= 0) return "—";
  const mins = Math.round(seconds / 60);
  if (mins < 60) return `${mins} min`;
  const hrs = Math.floor(mins / 60);
  const remainMins = mins % 60;
  return `${hrs}h ${remainMins}m`;
}

function formatDate(dateStr: string): string {
  const d = new Date(dateStr);
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" }) +
    " " + d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}

export default function LocationSessionsPage() {
  return (
    <Suspense fallback={
      <div className="flex items-center justify-center h-screen">
        <div className="text-gray-600">Loading...</div>
      </div>
    }>
      <LocationSessionsContent />
    </Suspense>
  );
}

function LocationSessionsContent() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const locationKey = searchParams.get("locationKey") || "";
  const days = searchParams.get("days") || "30";
  const groupBy = searchParams.get("groupBy") || "city";

  const [data, setData] = useState<LocationSessionsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const hasInitialFetch = useRef(false);

  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();

  const [galacticAdminMode] = useState(() => {
    if (typeof window !== "undefined") {
      return localStorage.getItem("galacticAdminMode") === "true";
    }
    return false;
  });

  const fetchSessions = useCallback(async () => {
    if (!locationKey) return;
    try {
      const endpoint = galacticAdminMode
        ? `${API_BASE_URL}/api/galactic/metrics/geographic/sessions?days=${days}&groupBy=${groupBy}&locationKey=${encodeURIComponent(locationKey)}`
        : `${API_BASE_URL}/api/metrics/geographic/sessions?tenantId=${tenantId}&days=${days}&groupBy=${groupBy}&locationKey=${encodeURIComponent(locationKey)}`;
      const response = await authenticatedFetch(endpoint, getAccessToken);
      if (response.ok) {
        const result = await response.json();
        setData(result);
      }
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        console.error("Session expired:", error.message);
      } else {
        console.error("Failed to fetch location sessions:", error);
      }
    } finally {
      setLoading(false);
    }
  }, [galacticAdminMode, tenantId, getAccessToken, days, groupBy, locationKey]);

  useEffect(() => {
    if (!galacticAdminMode && !tenantId) return;
    if (hasInitialFetch.current) return;
    hasInitialFetch.current = true;
    fetchSessions();
  }, [tenantId, galacticAdminMode, fetchSessions]);

  const timeLabel = days === "7" ? "7 Days" : days === "30" ? "30 Days" : "90 Days";

  // Compute summary stats from loaded sessions
  const stats = data?.sessions
    ? {
        total: data.sessions.length,
        succeeded: data.sessions.filter((s) => s.status === "Succeeded").length,
        failed: data.sessions.filter((s) => s.status === "Failed").length,
        avgDuration: (() => {
          const durations = data.sessions
            .filter((s) => s.status === "Succeeded" && s.durationSeconds && s.durationSeconds > 0)
            .map((s) => s.durationSeconds!);
          return durations.length > 0 ? Math.round(durations.reduce((a, b) => a + b, 0) / durations.length / 60) : 0;
        })(),
      }
    : null;

  if (loading) {
    return (
      <div className="flex items-center justify-center h-screen">
        <div className="text-gray-600">Loading sessions for {locationKey}...</div>
      </div>
    );
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {galacticAdminMode && (
          <div className="bg-purple-700 text-white text-sm px-4 py-2 flex items-center justify-center space-x-2">
            <svg className="w-4 h-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <span className="font-medium">Galactic Admin View</span>
            <span className="text-purple-300">&mdash; aggregating data across all tenants</span>
          </div>
        )}
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <button
              onClick={() => {
                const tr = days === "7" ? "7d" : days === "30" ? "30d" : "90d";
                router.push(`/geographic-performance`);
              }}
              className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
            >
              &larr; Back to Geographic Performance
            </button>
            <div className="flex items-center space-x-3">
              <svg className="w-8 h-8 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17.657 16.657L13.414 20.9a1.998 1.998 0 01-2.827 0l-4.244-4.243a8 8 0 1111.314 0z" />
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 11a3 3 0 11-6 0 3 3 0 016 0z" />
              </svg>
              <div>
                <h1 className="text-3xl font-bold text-gray-900">{locationKey}</h1>
                <p className="text-sm text-gray-500 mt-0.5">
                  {timeLabel} &middot; grouped by {groupBy}
                </p>
              </div>
            </div>
          </div>
        </header>

        <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          {/* Summary Stats */}
          {stats && (
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-6">
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">Sessions</div>
                <div className="text-2xl font-bold text-gray-900">{stats.total}</div>
              </div>
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">Succeeded</div>
                <div className="text-2xl font-bold text-green-600">{stats.succeeded}</div>
                <div className="text-xs text-gray-400">
                  {stats.total > 0 ? `${Math.round((stats.succeeded / stats.total) * 100)}%` : "—"}
                </div>
              </div>
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">Failed</div>
                <div className={`text-2xl font-bold ${stats.failed > 0 ? "text-red-600" : "text-gray-400"}`}>
                  {stats.failed}
                </div>
                <div className="text-xs text-gray-400">
                  {stats.total > 0 && stats.failed > 0 ? `${Math.round((stats.failed / stats.total) * 100)}%` : "—"}
                </div>
              </div>
              <div className="bg-white rounded-lg shadow p-4">
                <div className="text-sm font-medium text-gray-500">Avg Duration</div>
                <div className="text-2xl font-bold text-gray-900">
                  {stats.avgDuration > 0 ? `${stats.avgDuration} min` : "—"}
                </div>
                <div className="text-xs text-gray-400">succeeded sessions</div>
              </div>
            </div>
          )}

          {/* Session Table */}
          <div className="bg-white rounded-lg shadow overflow-hidden">
            <div className="px-4 py-3 border-b border-gray-200">
              <h2 className="text-lg font-semibold text-gray-900">
                Sessions ({data?.totalCount ?? 0})
              </h2>
            </div>
            {!data?.sessions?.length ? (
              <div className="p-8 text-center text-gray-500">
                No sessions found for this location.
              </div>
            ) : (
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200">
                  <thead className="bg-gray-50">
                    <tr>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Device Name
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Serial Number
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Model
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Status
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Duration
                      </th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 uppercase tracking-wider">
                        Started At
                      </th>
                    </tr>
                  </thead>
                  <tbody className="bg-white divide-y divide-gray-200">
                    {data.sessions.map((session) => (
                      <tr
                        key={session.sessionId}
                        onClick={() => router.push(`/sessions/${session.sessionId}`)}
                        className="hover:bg-gray-50 cursor-pointer transition-colors"
                      >
                        <td className="px-4 py-3 text-sm text-gray-900 font-medium">
                          {session.deviceName || "—"}
                        </td>
                        <td className="px-4 py-3 text-sm text-gray-600">
                          {session.serialNumber || "—"}
                        </td>
                        <td className="px-4 py-3 text-sm text-gray-600">
                          {session.model || "—"}
                        </td>
                        <td className="px-4 py-3 text-sm">
                          <StatusBadge status={session.status} failureReason={session.failureReason} />
                        </td>
                        <td className="px-4 py-3 text-sm text-gray-600">
                          {formatDuration(session.durationSeconds)}
                        </td>
                        <td className="px-4 py-3 text-sm text-gray-500">
                          {formatDate(session.startedAt)}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
