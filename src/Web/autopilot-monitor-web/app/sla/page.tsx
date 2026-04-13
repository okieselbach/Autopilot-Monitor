'use client';

import { useEffect, useState, useCallback } from 'react';
import { useTenant } from '../../contexts/TenantContext';
import { useAuth } from '../../contexts/AuthContext';
import { useNotifications } from '../../contexts/NotificationContext';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { SlaGauge } from "@/components/charts/SlaGauge";
import AppLineChart from "@/components/charts/AppLineChart";
import { chartColors } from "@/components/charts/chartTheme";
import Link from "next/link";

interface SlaSnapshot {
  month: string;
  totalCompleted: number;
  succeeded: number;
  failed: number;
  successRate: number;
  avgDurationMinutes: number;
  p95DurationMinutes: number;
  durationViolationCount: number;
  successRateMet: boolean;
  durationTargetMet: boolean;
}

interface SlaMonthlyTrend {
  month: string;
  successRate: number;
  p95DurationMinutes: number;
  appInstallSuccessRate: number;
  totalCompleted: number;
  successRateMet: boolean;
  durationTargetMet: boolean;
  appInstallTargetMet: boolean;
}

interface SlaViolatorSession {
  sessionId: string;
  tenantId: string;
  deviceName: string;
  serialNumber: string;
  startedAt: string;
  completedAt: string | null;
  durationSeconds: number | null;
  status: number;
  failureReason: string | null;
  violationType: string;
}

interface TopFailingApp {
  appName: string;
  failCount: number;
  totalCount: number;
  successRate: number;
}

interface AppInstallSlaSnapshot {
  totalInstalls: number;
  succeeded: number;
  failed: number;
  successRate: number;
  targetMet: boolean;
  topFailingApps: TopFailingApp[];
}

interface SlaMetricsResponse {
  targetSuccessRate: number | null;
  targetMaxDurationMinutes: number | null;
  targetAppInstallSuccessRate: number | null;
  currentMonth: SlaSnapshot;
  monthlyTrend: SlaMonthlyTrend[];
  violators: SlaViolatorSession[];
  appInstallSla: AppInstallSlaSnapshot | null;
  computedAt: string;
  fromCache: boolean;
  computeDurationMs: number;
}

function formatDuration(seconds: number): string {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

const statusLabels: Record<number, string> = {
  0: "InProgress",
  1: "Pending",
  2: "Stalled",
  3: "Succeeded",
  4: "Failed",
  5: "Unknown",
};

export default function SlaPage() {
  const { tenantId } = useTenant();
  const { getAccessToken } = useAuth();
  const { addNotification } = useNotifications();

  const [metrics, setMetrics] = useState<SlaMetricsResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [months, setMonths] = useState(3);

  const fetchMetrics = useCallback(async () => {
    if (!tenantId) return;
    try {
      setLoading(true);
      const response = await authenticatedFetch(
        api.metrics.sla(tenantId, months),
        getAccessToken
      );
      if (!response.ok) {
        addNotification('error', 'Error', `Failed to load SLA metrics: ${response.statusText}`, 'sla-fetch-error');
        return;
      }
      const data = await response.json();
      setMetrics(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) return;
      console.error('Error loading SLA metrics:', err);
      addNotification('error', 'Error', 'Failed to load SLA metrics', 'sla-fetch-error');
    } finally {
      setLoading(false);
    }
  }, [tenantId, months, getAccessToken, addNotification]);

  useEffect(() => {
    fetchMetrics();
  }, [fetchMetrics]);

  const hasTargets = metrics &&
    (metrics.targetSuccessRate != null || metrics.targetMaxDurationMinutes != null ||
     metrics.targetAppInstallSuccessRate != null);

  return (
    <ProtectedRoute>
      <main className="flex-1 overflow-y-auto">
        <div className="max-w-7xl mx-auto px-6 py-8">
          {/* Header */}
          <div className="flex items-center justify-between mb-8">
            <div>
              <h1 className="text-2xl font-bold text-white">SLA Compliance</h1>
              <p className="text-sm text-gray-400 mt-1">
                Monitor enrollment performance against your SLA targets
              </p>
            </div>
            <div className="flex items-center gap-3">
              <select
                value={months}
                onChange={(e) => setMonths(Number(e.target.value))}
                className="bg-gray-800 text-gray-300 border border-gray-700 rounded-md px-3 py-1.5 text-sm"
              >
                <option value={1}>Last month</option>
                <option value={3}>Last 3 months</option>
                <option value={6}>Last 6 months</option>
              </select>
              <button
                onClick={fetchMetrics}
                disabled={loading}
                className="px-3 py-1.5 bg-gray-800 text-gray-300 border border-gray-700 rounded-md text-sm hover:bg-gray-700 disabled:opacity-50"
              >
                {loading ? "Loading..." : "Refresh"}
              </button>
            </div>
          </div>

          {loading && !metrics && (
            <div className="flex justify-center items-center h-64">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-blue-400" />
            </div>
          )}

          {!loading && !hasTargets && (
            <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-8 text-center">
              <h2 className="text-lg font-semibold text-white mb-2">No SLA Targets Configured</h2>
              <p className="text-gray-400 mb-4">
                Set up SLA targets to track enrollment success rate and duration compliance.
              </p>
              <Link
                href="/settings/tenant/sla-targets"
                className="inline-flex items-center px-4 py-2 bg-blue-600 text-white rounded-md text-sm hover:bg-blue-500"
              >
                Configure SLA Targets
              </Link>
            </div>
          )}

          {metrics && hasTargets && (
            <>
              {/* Gauge Cards */}
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6 mb-8">
                {metrics.targetSuccessRate != null && (
                  <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-4">
                    <SlaGauge
                      value={metrics.currentMonth.successRate}
                      target={metrics.targetSuccessRate}
                      label="Success Rate"
                      unit="%"
                    />
                  </div>
                )}

                {metrics.targetMaxDurationMinutes != null && (
                  <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-4">
                    <SlaGauge
                      value={metrics.currentMonth.p95DurationMinutes}
                      target={metrics.targetMaxDurationMinutes}
                      label="P95 Duration"
                      unit="min"
                      invert
                    />
                  </div>
                )}

                {metrics.targetAppInstallSuccessRate != null && metrics.appInstallSla && (
                  <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-4">
                    <SlaGauge
                      value={metrics.appInstallSla.successRate}
                      target={metrics.targetAppInstallSuccessRate}
                      label="App Install Rate"
                      unit="%"
                    />
                  </div>
                )}

                {/* Overall Status */}
                <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-4 flex flex-col items-center justify-center">
                  <OverallStatus metrics={metrics} />
                </div>
              </div>

              {/* Summary Stats */}
              <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-8">
                <StatCard label="Total Sessions" value={metrics.currentMonth.totalCompleted} />
                <StatCard label="Succeeded" value={metrics.currentMonth.succeeded} color="text-emerald-400" />
                <StatCard label="Failed" value={metrics.currentMonth.failed} color="text-red-400" />
                <StatCard label="Duration Violations" value={metrics.currentMonth.durationViolationCount} color="text-amber-400" />
              </div>

              {/* Trend Chart */}
              {metrics.monthlyTrend.length > 1 && (
                <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-6 mb-8">
                  <h2 className="text-lg font-semibold text-white mb-4">Monthly Trend</h2>
                  <AppLineChart
                    data={[...metrics.monthlyTrend].reverse() as unknown as Array<Record<string, unknown>>}
                    xKey="month"
                    series={[
                      { dataKey: "successRate", label: "Success Rate (%)", color: chartColors.primary },
                      ...(metrics.targetAppInstallSuccessRate != null
                        ? [{ dataKey: "appInstallSuccessRate", label: "App Install Rate (%)", color: chartColors.success }]
                        : []),
                    ]}
                    height={300}
                  />
                </div>
              )}

              {/* Top Failing Apps */}
              {metrics.appInstallSla && metrics.appInstallSla.topFailingApps.length > 0 && (
                <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-6 mb-8">
                  <h2 className="text-lg font-semibold text-white mb-4">Top Failing Apps</h2>
                  <div className="overflow-x-auto">
                    <table className="w-full text-sm">
                      <thead>
                        <tr className="text-gray-400 border-b border-gray-700">
                          <th className="text-left py-2 px-3">App Name</th>
                          <th className="text-right py-2 px-3">Failures</th>
                          <th className="text-right py-2 px-3">Total</th>
                          <th className="text-right py-2 px-3">Success Rate</th>
                        </tr>
                      </thead>
                      <tbody>
                        {metrics.appInstallSla.topFailingApps.map((app) => (
                          <tr key={app.appName} className="border-b border-gray-800 hover:bg-gray-800/50">
                            <td className="py-2 px-3 text-white">{app.appName}</td>
                            <td className="py-2 px-3 text-right text-red-400">{app.failCount}</td>
                            <td className="py-2 px-3 text-right text-gray-400">{app.totalCount}</td>
                            <td className="py-2 px-3 text-right text-gray-300">{app.successRate.toFixed(1)}%</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}

              {/* Violators Table */}
              {metrics.violators.length > 0 && (
                <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-6">
                  <h2 className="text-lg font-semibold text-white mb-4">
                    SLA Violators ({metrics.violators.length})
                  </h2>
                  <div className="overflow-x-auto">
                    <table className="w-full text-sm">
                      <thead>
                        <tr className="text-gray-400 border-b border-gray-700">
                          <th className="text-left py-2 px-3">Device</th>
                          <th className="text-left py-2 px-3">Serial</th>
                          <th className="text-left py-2 px-3">Started</th>
                          <th className="text-right py-2 px-3">Duration</th>
                          <th className="text-left py-2 px-3">Status</th>
                          <th className="text-left py-2 px-3">Violation</th>
                          <th className="text-left py-2 px-3">Failure Reason</th>
                        </tr>
                      </thead>
                      <tbody>
                        {metrics.violators.map((v) => (
                          <tr key={v.sessionId} className="border-b border-gray-800 hover:bg-gray-800/50">
                            <td className="py-2 px-3">
                              <Link
                                href={`/session/${v.tenantId}/${v.sessionId}`}
                                className="text-blue-400 hover:underline"
                              >
                                {v.deviceName || "Unknown"}
                              </Link>
                            </td>
                            <td className="py-2 px-3 text-gray-400">{v.serialNumber || "-"}</td>
                            <td className="py-2 px-3 text-gray-400">
                              {new Date(v.startedAt).toLocaleDateString()}
                            </td>
                            <td className="py-2 px-3 text-right text-gray-300">
                              {v.durationSeconds ? formatDuration(v.durationSeconds) : "-"}
                            </td>
                            <td className="py-2 px-3">
                              <span className={v.status === 4 ? "text-red-400" : "text-gray-300"}>
                                {statusLabels[v.status] ?? "Unknown"}
                              </span>
                            </td>
                            <td className="py-2 px-3">
                              <span className={
                                v.violationType === "Both" ? "text-red-400 font-medium" :
                                v.violationType === "Failed" ? "text-red-400" : "text-amber-400"
                              }>
                                {v.violationType}
                              </span>
                            </td>
                            <td className="py-2 px-3 text-gray-400 max-w-xs truncate">
                              {v.failureReason || "-"}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}

              {/* Cache / Timing info */}
              <div className="text-xs text-gray-500 mt-4 text-right">
                Computed at {new Date(metrics.computedAt).toLocaleTimeString()}
                {metrics.fromCache && " (cached)"}
                {" "}({metrics.computeDurationMs}ms)
              </div>
            </>
          )}
        </div>
      </main>
    </ProtectedRoute>
  );
}

function OverallStatus({ metrics }: { metrics: SlaMetricsResponse }) {
  const checks: boolean[] = [];
  if (metrics.targetSuccessRate != null)
    checks.push(metrics.currentMonth.successRateMet);
  if (metrics.targetMaxDurationMinutes != null)
    checks.push(metrics.currentMonth.durationTargetMet);
  if (metrics.targetAppInstallSuccessRate != null && metrics.appInstallSla)
    checks.push(metrics.appInstallSla.targetMet);

  const allMet = checks.every(Boolean);
  const anyBreached = checks.some(c => !c);

  if (checks.length === 0) {
    return (
      <span className="text-gray-400 text-sm">No targets configured</span>
    );
  }

  return (
    <div className="flex flex-col items-center gap-2">
      <div className={`text-4xl ${allMet ? "" : "animate-pulse"}`}>
        {allMet ? "\u2705" : "\u26a0\ufe0f"}
      </div>
      <span className={`text-lg font-bold ${allMet ? "text-emerald-400" : anyBreached ? "text-red-400" : "text-amber-400"}`}>
        {allMet ? "Compliant" : "Breached"}
      </span>
      <span className="text-xs text-gray-500">
        {checks.filter(Boolean).length}/{checks.length} targets met
      </span>
    </div>
  );
}

function StatCard({ label, value, color = "text-white" }: { label: string; value: number; color?: string }) {
  return (
    <div className="bg-gray-800/50 border border-gray-700 rounded-lg p-4">
      <div className={`text-2xl font-bold ${color}`}>{value}</div>
      <div className="text-sm text-gray-400 mt-1">{label}</div>
    </div>
  );
}
