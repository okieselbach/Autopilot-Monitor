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
  const [refreshing, setRefreshing] = useState(false);
  const [months, setMonths] = useState(3);
  const [initialLoad, setInitialLoad] = useState(true);

  const fetchMetrics = useCallback(async (showRefreshing = false) => {
    if (!tenantId) return;
    try {
      if (showRefreshing) {
        setRefreshing(true);
      } else {
        setLoading(true);
      }

      // First load always bypasses cache (user may have just changed SLA config)
      const useFresh = initialLoad;
      if (initialLoad) setInitialLoad(false);

      const response = await authenticatedFetch(
        api.metrics.sla(tenantId, months, useFresh),
        getAccessToken
      );
      if (!response.ok) {
        addNotification('error', 'Error', `Failed to load SLA metrics: ${response.statusText}`, 'sla-fetch-error');
        return;
      }
      const data = await response.json();
      setMetrics(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error('Error loading SLA metrics:', err);
        addNotification('error', 'Error', 'Failed to load SLA metrics', 'sla-fetch-error');
      }
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [tenantId, months, getAccessToken, addNotification]);

  useEffect(() => {
    fetchMetrics();
  }, [fetchMetrics]);

  const hasTargets = metrics &&
    (metrics.targetSuccessRate != null || metrics.targetMaxDurationMinutes != null ||
     metrics.targetAppInstallSuccessRate != null);

  if (loading && !metrics) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 dark:from-gray-900 dark:to-gray-800 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600 dark:text-gray-400">Loading SLA metrics...</p>
        </div>
      </div>
    );
  }

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
        <header className="bg-white dark:bg-gray-800 shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <h1 className="text-2xl font-normal text-gray-900 dark:text-white">SLA Compliance</h1>
                <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">
                  Monitor enrollment performance against your SLA targets
                  {metrics && (
                    <>
                      {" · "}Computed at {new Date(metrics.computedAt).toLocaleString()} in {metrics.computeDurationMs}ms
                      {metrics.fromCache && (
                        <span className="ml-2 px-2 py-0.5 bg-blue-100 text-blue-700 text-xs rounded">
                          From Cache
                        </span>
                      )}
                    </>
                  )}
                </p>
              </div>
              <div className="flex items-center gap-3">
                <select
                  value={months}
                  onChange={(e) => setMonths(Number(e.target.value))}
                  className="text-sm border border-gray-300 dark:border-gray-600 rounded-md px-2 py-1.5 bg-white dark:bg-gray-700 text-gray-900 dark:text-white"
                >
                  <option value={1}>Last month</option>
                  <option value={3}>Last 3 months</option>
                  <option value={6}>Last 6 months</option>
                </select>
                <button
                  onClick={() => fetchMetrics(true)}
                  disabled={refreshing}
                  className="px-4 py-2 bg-white dark:bg-gray-700 border border-gray-200 dark:border-gray-600 text-gray-700 dark:text-gray-200 rounded-lg hover:bg-gray-50 dark:hover:bg-gray-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
                >
                  <svg className={`h-5 w-5 ${refreshing ? 'animate-spin' : ''}`} fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                  </svg>
                  <span>{refreshing ? 'Refreshing...' : 'Refresh'}</span>
                </button>
              </div>
            </div>
          </div>
        </header>

        <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          {!hasTargets && (
            <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-8 text-center">
              <h2 className="text-lg font-semibold text-gray-900 dark:text-white mb-2">No SLA Targets Configured</h2>
              <p className="text-gray-500 dark:text-gray-400 mb-4">
                Set up SLA targets to track enrollment success rate and duration compliance.
              </p>
              <Link
                href="/settings/tenant/sla-targets"
                className="inline-flex items-center px-4 py-2 bg-indigo-600 text-white rounded-md text-sm hover:bg-indigo-500"
              >
                Configure SLA Targets
              </Link>
            </div>
          )}

          {metrics && hasTargets && (
            <>
              {/* Gauge Cards */}
              <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-6 mb-6">
                {metrics.targetSuccessRate != null && (
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
                    <SlaGauge
                      value={metrics.currentMonth.successRate}
                      target={metrics.targetSuccessRate}
                      label="Success Rate"
                      unit="%"
                    />
                  </div>
                )}

                {metrics.targetMaxDurationMinutes != null && (
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
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
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
                    <SlaGauge
                      value={metrics.appInstallSla.successRate}
                      target={metrics.targetAppInstallSuccessRate}
                      label="App Install Rate"
                      unit="%"
                    />
                  </div>
                )}

                {/* Overall Status */}
                <div className="bg-white rounded-lg shadow p-6 flex flex-col items-center justify-center">
                  <OverallStatus metrics={metrics} />
                </div>
              </div>

              {/* Summary Stats */}
              <div className="mb-6">
                <h2 className="text-xl font-semibold text-gray-900 dark:text-white mb-4">Current Month</h2>
                <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
                    <div className="text-sm text-gray-500 dark:text-gray-400 mb-1">Total Sessions</div>
                    <div className="text-3xl font-bold text-gray-900 dark:text-white">{metrics.currentMonth.totalCompleted}</div>
                  </div>
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
                    <div className="text-sm text-gray-500 dark:text-gray-400 mb-1">Succeeded</div>
                    <div className="text-3xl font-bold text-green-600">{metrics.currentMonth.succeeded}</div>
                  </div>
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
                    <div className="text-sm text-gray-500 dark:text-gray-400 mb-1">Failed</div>
                    <div className="text-3xl font-bold text-red-600">{metrics.currentMonth.failed}</div>
                  </div>
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
                    <div className="text-sm text-gray-500 dark:text-gray-400 mb-1">Duration Violations</div>
                    <div className="text-3xl font-bold text-amber-600">{metrics.currentMonth.durationViolationCount}</div>
                  </div>
                </div>
              </div>

              {/* Trend Chart */}
              {metrics.monthlyTrend.length > 1 && (
                <div className="mb-6">
                  <h2 className="text-xl font-semibold text-gray-900 dark:text-white mb-4">Monthly Trend</h2>
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow p-6">
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
                </div>
              )}

              {/* Top Failing Apps */}
              {metrics.appInstallSla && metrics.appInstallSla.topFailingApps.length > 0 && (
                <div className="mb-6">
                  <h2 className="text-xl font-semibold text-gray-900 dark:text-white mb-4">Top Failing Apps</h2>
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
                    <table className="w-full text-sm">
                      <thead className="bg-gray-50 dark:bg-gray-750">
                        <tr className="text-gray-500 dark:text-gray-400 border-b dark:border-gray-700">
                          <th className="text-left py-3 px-4 font-medium">App Name</th>
                          <th className="text-right py-3 px-4 font-medium">Failures</th>
                          <th className="text-right py-3 px-4 font-medium">Total</th>
                          <th className="text-right py-3 px-4 font-medium">Success Rate</th>
                        </tr>
                      </thead>
                      <tbody>
                        {metrics.appInstallSla.topFailingApps.map((app) => (
                          <tr key={app.appName} className="border-b dark:border-gray-700 hover:bg-gray-50 dark:hover:bg-gray-750">
                            <td className="py-3 px-4 text-gray-900 dark:text-white">{app.appName}</td>
                            <td className="py-3 px-4 text-right text-red-600 font-medium">{app.failCount}</td>
                            <td className="py-3 px-4 text-right text-gray-500">{app.totalCount}</td>
                            <td className="py-3 px-4 text-right text-gray-700 dark:text-gray-300">{app.successRate.toFixed(1)}%</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}

              {/* Violators Table */}
              {metrics.violators.length > 0 && (
                <div className="mb-6">
                  <h2 className="text-xl font-semibold text-gray-900 dark:text-white mb-4">
                    SLA Violators ({metrics.violators.length})
                  </h2>
                  <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden">
                    <table className="w-full text-sm">
                      <thead className="bg-gray-50 dark:bg-gray-750">
                        <tr className="text-gray-500 dark:text-gray-400 border-b dark:border-gray-700">
                          <th className="text-left py-3 px-4 font-medium">Device</th>
                          <th className="text-left py-3 px-4 font-medium">Serial</th>
                          <th className="text-left py-3 px-4 font-medium">Started</th>
                          <th className="text-right py-3 px-4 font-medium">Duration</th>
                          <th className="text-left py-3 px-4 font-medium">Status</th>
                          <th className="text-left py-3 px-4 font-medium">Violation</th>
                          <th className="text-left py-3 px-4 font-medium">Failure Reason</th>
                        </tr>
                      </thead>
                      <tbody>
                        {metrics.violators.map((v) => (
                          <tr key={v.sessionId} className="border-b dark:border-gray-700 hover:bg-gray-50 dark:hover:bg-gray-750">
                            <td className="py-3 px-4">
                              <Link
                                href={`/session/${v.tenantId}/${v.sessionId}`}
                                className="text-indigo-600 hover:underline"
                              >
                                {v.deviceName || "Unknown"}
                              </Link>
                            </td>
                            <td className="py-3 px-4 text-gray-500 dark:text-gray-400">{v.serialNumber || "-"}</td>
                            <td className="py-3 px-4 text-gray-500 dark:text-gray-400">
                              {new Date(v.startedAt).toLocaleDateString()}
                            </td>
                            <td className="py-3 px-4 text-right text-gray-700 dark:text-gray-300">
                              {v.durationSeconds ? formatDuration(v.durationSeconds) : "-"}
                            </td>
                            <td className="py-3 px-4">
                              <span className={v.status === 4 ? "text-red-600 font-medium" : "text-gray-700"}>
                                {statusLabels[v.status] ?? "Unknown"}
                              </span>
                            </td>
                            <td className="py-3 px-4">
                              <span className={
                                v.violationType === "Both" ? "text-red-600 font-medium" :
                                v.violationType === "Failed" ? "text-red-600" : "text-amber-600"
                              }>
                                {v.violationType}
                              </span>
                            </td>
                            <td className="py-3 px-4 text-gray-500 dark:text-gray-400 max-w-xs truncate">
                              {v.failureReason || "-"}
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      </div>
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
      <span className="text-gray-500 dark:text-gray-400 text-sm">No targets configured</span>
    );
  }

  return (
    <div className="flex flex-col items-center gap-2">
      <div className={`text-4xl ${allMet ? "" : "animate-pulse"}`}>
        {allMet ? "\u2705" : "\u26a0\ufe0f"}
      </div>
      <span className={`text-lg font-bold ${allMet ? "text-green-600 dark:text-green-400" : anyBreached ? "text-red-600 dark:text-red-400" : "text-amber-600 dark:text-amber-400"}`}>
        {allMet ? "Compliant" : "Breached"}
      </span>
      <span className="text-xs text-gray-500">
        {checks.filter(Boolean).length}/{checks.length} targets met
      </span>
    </div>
  );
}
