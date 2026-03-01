'use client';

import { useEffect, useState, useMemo, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { API_BASE_URL } from '@/lib/config';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { useAuth } from '../../contexts/AuthContext';
import { useNotifications } from '../../contexts/NotificationContext';

// ── Types ──────────────────────────────────────────────────────────────────────

interface AgentMetricsData {
  agent_cpu_percent?: number;
  agent_working_set_mb?: number;
  agent_private_bytes_mb?: number;
  agent_thread_count?: number;
  agent_handle_count?: number;
  net_requests?: number;
  net_failures?: number;
  net_bytes_up?: number;
  net_bytes_down?: number;
  net_avg_latency_ms?: number;
  net_total_bytes_up?: number;
  net_total_bytes_down?: number;
  net_total_requests?: number;
}

interface EnrollmentEvent {
  eventType: string;
  timestamp: string;
  data?: AgentMetricsData;
  sessionId?: string;
}

interface Session {
  sessionId: string;
  tenantId: string;
  serialNumber?: string;
  manufacturer?: string;
  model?: string;
  deviceName?: string;
  startedAt?: string;
  status?: string;
  agentVersion?: string;
}

interface SessionAgentMetrics {
  session: Session;
  snapshots: AgentMetricsData[];
  // Final cumulative values from the last snapshot
  totalBytesUp: number;
  totalBytesDown: number;
  totalRequests: number;
  avgCpu: number;
  maxCpu: number;
  avgWorkingSet: number;
  maxWorkingSet: number;
  avgPrivateBytes: number;
  avgLatency: number;
}

// ── Helpers ────────────────────────────────────────────────────────────────────

function formatBytes(bytes: number): string {
  if (bytes === 0) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  const i = Math.floor(Math.log(bytes) / Math.log(1024));
  const val = bytes / Math.pow(1024, i);
  return `${val.toFixed(i === 0 ? 0 : 1)} ${units[i]}`;
}

function avg(values: number[]): number {
  if (values.length === 0) return 0;
  return values.reduce((a, b) => a + b, 0) / values.length;
}

function max(values: number[]): number {
  if (values.length === 0) return 0;
  return Math.max(...values);
}

function pN(values: number[], percentile: number): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const idx = Math.ceil((percentile / 100) * sorted.length) - 1;
  return sorted[Math.max(0, idx)];
}

// ── Component ──────────────────────────────────────────────────────────────────

export default function PlatformMetricsPage() {
  const router = useRouter();
  const { getAccessToken, user } = useAuth();
  const { addNotification } = useNotifications();

  const [loading, setLoading] = useState(true);
  const [sessionMetrics, setSessionMetrics] = useState<SessionAgentMetrics[]>([]);
  const [sampleSize, setSampleSize] = useState(20);
  const [error, setError] = useState<string | null>(null);

  // ── Fetch sessions + their events ──────────────────────────────────────────

  const fetchMetrics = useCallback(async () => {
    setLoading(true);
    setError(null);

    try {
      const token = await getAccessToken();
      if (!token) {
        addNotification('error', 'Auth Error', 'No access token', 'pm-auth');
        return;
      }

      // 1. Fetch all sessions (galactic endpoint)
      const sessionsRes = await fetch(`${API_BASE_URL}/api/galactic/sessions`, {
        headers: { Authorization: `Bearer ${token}` },
      });
      if (!sessionsRes.ok) {
        if (sessionsRes.status === 403) {
          setError('Access denied. Galactic Admin privileges required.');
        } else {
          setError(`Failed to fetch sessions: ${sessionsRes.status}`);
        }
        return;
      }
      const sessionsData = await sessionsRes.json();
      const allSessions: Session[] = Array.isArray(sessionsData)
        ? sessionsData
        : sessionsData.sessions || [];

      // Sort by startedAt desc, take N most recent
      const sorted = allSessions
        .sort((a, b) => new Date(b.startedAt || 0).getTime() - new Date(a.startedAt || 0).getTime())
        .slice(0, sampleSize);

      if (sorted.length === 0) {
        setSessionMetrics([]);
        return;
      }

      // 2. Fetch events for each session in parallel
      const results = await Promise.allSettled(
        sorted.map(async (session) => {
          const eventsRes = await fetch(
            `${API_BASE_URL}/api/sessions/${session.sessionId}/events?tenantId=${session.tenantId}`,
            { headers: { Authorization: `Bearer ${token}` } }
          );
          if (!eventsRes.ok) return { session, events: [] as EnrollmentEvent[] };
          const eventsData = await eventsRes.json();
          const events: EnrollmentEvent[] = Array.isArray(eventsData.events) ? eventsData.events : [];
          return { session, events };
        })
      );

      // 3. Extract agent_metrics_snapshot events and compute per-session aggregates
      const metricsPerSession: SessionAgentMetrics[] = [];

      for (const result of results) {
        if (result.status !== 'fulfilled') continue;
        const { session, events } = result.value;
        const snapshots = events
          .filter((e) => e.eventType === 'agent_metrics_snapshot' && e.data)
          .map((e) => e.data!);

        if (snapshots.length === 0) continue;

        const cpuValues = snapshots.map((s) => s.agent_cpu_percent ?? 0);
        const wsValues = snapshots.map((s) => s.agent_working_set_mb ?? 0);
        const pbValues = snapshots.map((s) => s.agent_private_bytes_mb ?? 0);
        const latValues = snapshots.map((s) => s.net_avg_latency_ms ?? 0).filter((v) => v > 0);

        // Last snapshot has cumulative totals
        const last = snapshots[snapshots.length - 1];

        metricsPerSession.push({
          session,
          snapshots,
          totalBytesUp: last.net_total_bytes_up ?? 0,
          totalBytesDown: last.net_total_bytes_down ?? 0,
          totalRequests: last.net_total_requests ?? 0,
          avgCpu: avg(cpuValues),
          maxCpu: max(cpuValues),
          avgWorkingSet: avg(wsValues),
          maxWorkingSet: max(wsValues),
          avgPrivateBytes: avg(pbValues),
          avgLatency: avg(latValues),
        });
      }

      setSessionMetrics(metricsPerSession);
    } catch (err) {
      console.error('Platform metrics fetch error:', err);
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, sampleSize, addNotification]);

  useEffect(() => {
    fetchMetrics();
  }, [fetchMetrics]);

  // ── Aggregated stats across all sessions ───────────────────────────────────

  const globalStats = useMemo(() => {
    if (sessionMetrics.length === 0) return null;

    const allCpuAvgs = sessionMetrics.map((s) => s.avgCpu);
    const allCpuMaxes = sessionMetrics.map((s) => s.maxCpu);
    const allWsAvgs = sessionMetrics.map((s) => s.avgWorkingSet);
    const allWsMaxes = sessionMetrics.map((s) => s.maxWorkingSet);
    const allPbAvgs = sessionMetrics.map((s) => s.avgPrivateBytes);
    const allLatAvgs = sessionMetrics.filter((s) => s.avgLatency > 0).map((s) => s.avgLatency);
    const allBytesUp = sessionMetrics.map((s) => s.totalBytesUp);
    const allBytesDown = sessionMetrics.map((s) => s.totalBytesDown);
    const allRequests = sessionMetrics.map((s) => s.totalRequests);
    const totalSnapshots = sessionMetrics.reduce((sum, s) => sum + s.snapshots.length, 0);

    return {
      sessionsAnalyzed: sessionMetrics.length,
      totalSnapshots,
      cpu: {
        avg: avg(allCpuAvgs),
        max: max(allCpuMaxes),
        p95: pN(allCpuMaxes, 95),
      },
      memory: {
        avgWs: avg(allWsAvgs),
        maxWs: max(allWsMaxes),
        p95Ws: pN(allWsMaxes, 95),
        avgPb: avg(allPbAvgs),
      },
      network: {
        avgBytesUpPerSession: avg(allBytesUp),
        avgBytesDownPerSession: avg(allBytesDown),
        maxBytesUp: max(allBytesUp),
        avgRequestsPerSession: avg(allRequests),
        avgLatency: avg(allLatAvgs),
        p95Latency: pN(allLatAvgs, 95),
      },
    };
  }, [sessionMetrics]);

  // ── Agent version distribution ─────────────────────────────────────────────

  const versionDistribution = useMemo(() => {
    const counts: Record<string, number> = {};
    for (const sm of sessionMetrics) {
      const v = sm.session.agentVersion || 'unknown';
      counts[v] = (counts[v] || 0) + 1;
    }
    return Object.entries(counts)
      .sort((a, b) => b[1] - a[1])
      .map(([version, count]) => ({ version, count, pct: (count / sessionMetrics.length) * 100 }));
  }, [sessionMetrics]);

  // ── Render ─────────────────────────────────────────────────────────────────

  return (
    <ProtectedRoute requireGalacticAdmin>
      <div className="min-h-screen bg-gray-50">
        <header className="bg-white shadow">
          <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
            <div className="flex items-center justify-between">
              <div>
                <button
                  onClick={() => router.push('/dashboard')}
                  className="text-sm text-gray-600 hover:text-gray-900 mb-2 flex items-center"
                >
                  &larr; Back to Dashboard
                </button>
                <h1 className="text-3xl font-bold text-gray-900">Platform Metrics</h1>
              </div>
              <div className="flex items-center gap-3">
                <label className="text-sm text-gray-500">Sessions to analyze:</label>
                <select
                  value={sampleSize}
                  onChange={(e) => setSampleSize(Number(e.target.value))}
                  className="text-sm border border-gray-300 rounded-md px-2 py-1"
                >
                  <option value={10}>10</option>
                  <option value={20}>20</option>
                  <option value={50}>50</option>
                  <option value={100}>100</option>
                </select>
                <button
                  onClick={fetchMetrics}
                  disabled={loading}
                  className="px-3 py-1.5 text-sm bg-purple-600 text-white rounded-md hover:bg-purple-700 disabled:opacity-50 transition-colors"
                >
                  {loading ? 'Loading...' : 'Refresh'}
                </button>
              </div>
            </div>
          </div>
        </header>

        <main className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
          {error && (
            <div className="mb-6 p-4 bg-red-50 border border-red-200 rounded-lg text-red-700 text-sm">
              {error}
            </div>
          )}

          {loading && (
            <div className="flex items-center justify-center py-20">
              <div className="flex items-center gap-3 text-gray-500">
                <svg className="animate-spin h-5 w-5" viewBox="0 0 24 24" fill="none">
                  <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                  <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
                </svg>
                <span>Fetching agent metrics from {sampleSize} sessions...</span>
              </div>
            </div>
          )}

          {!loading && !error && globalStats && (
            <>
              {/* ── Overview Stats ──────────────────────────────────────── */}
              <div className="grid grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
                <StatCard
                  label="Sessions Analyzed"
                  value={globalStats.sessionsAnalyzed.toString()}
                  detail={`${globalStats.totalSnapshots} snapshots total`}
                  color="purple"
                />
                <StatCard
                  label="Avg Agent CPU"
                  value={`${globalStats.cpu.avg.toFixed(2)}%`}
                  detail={`p95 peak: ${globalStats.cpu.p95.toFixed(2)}%, max: ${globalStats.cpu.max.toFixed(2)}%`}
                  color={globalStats.cpu.avg < 2 ? 'green' : globalStats.cpu.avg < 5 ? 'yellow' : 'red'}
                />
                <StatCard
                  label="Avg Working Set"
                  value={`${globalStats.memory.avgWs.toFixed(1)} MB`}
                  detail={`p95 peak: ${globalStats.memory.p95Ws.toFixed(1)} MB, max: ${globalStats.memory.maxWs.toFixed(1)} MB`}
                  color={globalStats.memory.avgWs < 30 ? 'green' : globalStats.memory.avgWs < 60 ? 'yellow' : 'red'}
                />
                <StatCard
                  label="Avg Network / Session"
                  value={formatBytes(globalStats.network.avgBytesUpPerSession + globalStats.network.avgBytesDownPerSession)}
                  detail={`${formatBytes(globalStats.network.avgBytesUpPerSession)} up, ${formatBytes(globalStats.network.avgBytesDownPerSession)} down`}
                  color="blue"
                />
              </div>

              {/* ── Agent Footprint Assessment ─────────────────────────── */}
              <div className="bg-white shadow rounded-lg p-6 mb-6">
                <h2 className="text-lg font-semibold text-gray-900 mb-4">Agent Footprint Assessment</h2>
                <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
                  {/* CPU */}
                  <div>
                    <div className="flex items-center justify-between mb-2">
                      <span className="text-sm font-medium text-gray-700">CPU Impact</span>
                      <FootprintBadge value={globalStats.cpu.avg} thresholds={[1, 3, 5]} unit="%" />
                    </div>
                    <div className="h-2 bg-gray-100 rounded-full overflow-hidden">
                      <div
                        className={`h-full rounded-full transition-all ${globalStats.cpu.avg < 1 ? 'bg-green-500' : globalStats.cpu.avg < 3 ? 'bg-green-400' : globalStats.cpu.avg < 5 ? 'bg-yellow-400' : 'bg-red-500'}`}
                        style={{ width: `${Math.min(globalStats.cpu.avg * 10, 100)}%` }}
                      />
                    </div>
                    <p className="text-xs text-gray-500 mt-1">
                      Avg: {globalStats.cpu.avg.toFixed(2)}% | Peak p95: {globalStats.cpu.p95.toFixed(2)}%
                    </p>
                  </div>

                  {/* Memory */}
                  <div>
                    <div className="flex items-center justify-between mb-2">
                      <span className="text-sm font-medium text-gray-700">Memory Footprint</span>
                      <FootprintBadge value={globalStats.memory.avgWs} thresholds={[20, 40, 80]} unit=" MB" />
                    </div>
                    <div className="h-2 bg-gray-100 rounded-full overflow-hidden">
                      <div
                        className={`h-full rounded-full transition-all ${globalStats.memory.avgWs < 20 ? 'bg-green-500' : globalStats.memory.avgWs < 40 ? 'bg-green-400' : globalStats.memory.avgWs < 80 ? 'bg-yellow-400' : 'bg-red-500'}`}
                        style={{ width: `${Math.min((globalStats.memory.avgWs / 100) * 100, 100)}%` }}
                      />
                    </div>
                    <p className="text-xs text-gray-500 mt-1">
                      Working Set avg: {globalStats.memory.avgWs.toFixed(1)} MB | Private Bytes avg: {globalStats.memory.avgPb.toFixed(1)} MB
                    </p>
                  </div>

                  {/* Network */}
                  <div>
                    <div className="flex items-center justify-between mb-2">
                      <span className="text-sm font-medium text-gray-700">Network Usage</span>
                      <FootprintBadge
                        value={(globalStats.network.avgBytesUpPerSession + globalStats.network.avgBytesDownPerSession) / 1024}
                        thresholds={[100, 500, 2048]}
                        unit=" KB"
                        formatFn={(v) => formatBytes(v * 1024)}
                      />
                    </div>
                    <div className="h-2 bg-gray-100 rounded-full overflow-hidden">
                      <div
                        className="h-full bg-blue-500 rounded-full transition-all"
                        style={{ width: `${Math.min(((globalStats.network.avgBytesUpPerSession + globalStats.network.avgBytesDownPerSession) / (1024 * 1024)) * 100, 100)}%` }}
                      />
                    </div>
                    <p className="text-xs text-gray-500 mt-1">
                      Avg {globalStats.network.avgRequestsPerSession.toFixed(0)} requests/session | Latency avg: {globalStats.network.avgLatency.toFixed(0)} ms, p95: {globalStats.network.p95Latency.toFixed(0)} ms
                    </p>
                  </div>
                </div>
              </div>

              {/* ── Detailed Breakdown ─────────────────────────────────── */}
              <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mb-6">
                {/* Per-Session CPU Distribution */}
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-sm font-semibold text-gray-900 mb-4">CPU % per Session (avg)</h3>
                  <div className="space-y-2">
                    {sessionMetrics
                      .sort((a, b) => b.avgCpu - a.avgCpu)
                      .slice(0, 10)
                      .map((sm) => {
                        const barWidth = globalStats.cpu.max > 0 ? (sm.avgCpu / globalStats.cpu.max) * 100 : 0;
                        return (
                          <div key={sm.session.sessionId} className="flex items-center gap-3">
                            <span className="text-xs text-gray-500 w-24 truncate" title={sm.session.deviceName || sm.session.sessionId}>
                              {sm.session.deviceName || sm.session.sessionId.slice(0, 8)}
                            </span>
                            <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                              <div
                                className={`h-full rounded-full ${sm.avgCpu < 1 ? 'bg-green-400' : sm.avgCpu < 3 ? 'bg-green-500' : sm.avgCpu < 5 ? 'bg-yellow-400' : 'bg-red-500'}`}
                                style={{ width: `${Math.max(barWidth, 2)}%` }}
                              />
                            </div>
                            <span className="text-xs font-mono text-gray-600 w-14 text-right">
                              {sm.avgCpu.toFixed(2)}%
                            </span>
                          </div>
                        );
                      })}
                  </div>
                </div>

                {/* Per-Session Memory Distribution */}
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-sm font-semibold text-gray-900 mb-4">Working Set per Session (avg MB)</h3>
                  <div className="space-y-2">
                    {sessionMetrics
                      .sort((a, b) => b.avgWorkingSet - a.avgWorkingSet)
                      .slice(0, 10)
                      .map((sm) => {
                        const barWidth = globalStats.memory.maxWs > 0 ? (sm.avgWorkingSet / globalStats.memory.maxWs) * 100 : 0;
                        return (
                          <div key={sm.session.sessionId} className="flex items-center gap-3">
                            <span className="text-xs text-gray-500 w-24 truncate" title={sm.session.deviceName || sm.session.sessionId}>
                              {sm.session.deviceName || sm.session.sessionId.slice(0, 8)}
                            </span>
                            <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                              <div
                                className={`h-full rounded-full ${sm.avgWorkingSet < 20 ? 'bg-green-400' : sm.avgWorkingSet < 40 ? 'bg-yellow-400' : 'bg-red-500'}`}
                                style={{ width: `${Math.max(barWidth, 2)}%` }}
                              />
                            </div>
                            <span className="text-xs font-mono text-gray-600 w-16 text-right">
                              {sm.avgWorkingSet.toFixed(1)} MB
                            </span>
                          </div>
                        );
                      })}
                  </div>
                </div>

                {/* Per-Session Network Usage */}
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-sm font-semibold text-gray-900 mb-4">Network per Session (total bytes)</h3>
                  <div className="space-y-2">
                    {sessionMetrics
                      .sort((a, b) => (b.totalBytesUp + b.totalBytesDown) - (a.totalBytesUp + a.totalBytesDown))
                      .slice(0, 10)
                      .map((sm) => {
                        const total = sm.totalBytesUp + sm.totalBytesDown;
                        const maxTotal = globalStats.network.maxBytesUp + max(sessionMetrics.map(s => s.totalBytesDown));
                        const barWidth = maxTotal > 0 ? (total / maxTotal) * 100 : 0;
                        return (
                          <div key={sm.session.sessionId} className="flex items-center gap-3">
                            <span className="text-xs text-gray-500 w-24 truncate" title={sm.session.deviceName || sm.session.sessionId}>
                              {sm.session.deviceName || sm.session.sessionId.slice(0, 8)}
                            </span>
                            <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                              <div className="h-full bg-blue-500 rounded-full" style={{ width: `${Math.max(barWidth, 2)}%` }} />
                            </div>
                            <span className="text-xs font-mono text-gray-600 w-16 text-right">
                              {formatBytes(total)}
                            </span>
                          </div>
                        );
                      })}
                  </div>
                </div>

                {/* Agent Version Distribution */}
                <div className="bg-white shadow rounded-lg p-6">
                  <h3 className="text-sm font-semibold text-gray-900 mb-4">Agent Version Distribution</h3>
                  {versionDistribution.length === 0 ? (
                    <p className="text-sm text-gray-500">No version data available</p>
                  ) : (
                    <div className="space-y-2">
                      {versionDistribution.map((v) => (
                        <div key={v.version} className="flex items-center gap-3">
                          <span className="text-xs font-mono text-gray-600 w-24 truncate" title={v.version}>
                            {v.version}
                          </span>
                          <div className="flex-1 h-2 bg-gray-100 rounded-full overflow-hidden">
                            <div className="h-full bg-purple-500 rounded-full" style={{ width: `${v.pct}%` }} />
                          </div>
                          <span className="text-xs text-gray-500 w-16 text-right">
                            {v.count} ({v.pct.toFixed(0)}%)
                          </span>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              </div>

              {/* ── Ideas for future metrics ──────────────────────────── */}
              <div className="bg-white shadow rounded-lg p-6">
                <h3 className="text-sm font-semibold text-gray-900 mb-3">Future Platform Metrics Ideas</h3>
                <div className="grid grid-cols-1 md:grid-cols-3 gap-4 text-xs text-gray-500">
                  <div className="flex items-start gap-2">
                    <span className="text-gray-300 mt-0.5">&#9679;</span>
                    <span>Agent crash rate &amp; restart frequency</span>
                  </div>
                  <div className="flex items-start gap-2">
                    <span className="text-gray-300 mt-0.5">&#9679;</span>
                    <span>Event delivery latency (emit &rarr; backend)</span>
                  </div>
                  <div className="flex items-start gap-2">
                    <span className="text-gray-300 mt-0.5">&#9679;</span>
                    <span>Spool queue depth over time</span>
                  </div>
                  <div className="flex items-start gap-2">
                    <span className="text-gray-300 mt-0.5">&#9679;</span>
                    <span>Backend Function execution time &amp; cost</span>
                  </div>
                  <div className="flex items-start gap-2">
                    <span className="text-gray-300 mt-0.5">&#9679;</span>
                    <span>SignalR connection health &amp; message throughput</span>
                  </div>
                  <div className="flex items-start gap-2">
                    <span className="text-gray-300 mt-0.5">&#9679;</span>
                    <span>Agent footprint trend over releases</span>
                  </div>
                </div>
              </div>
            </>
          )}

          {!loading && !error && sessionMetrics.length === 0 && (
            <div className="text-center py-20 text-gray-500">
              <svg className="mx-auto h-12 w-12 text-gray-300 mb-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M3 13.125C3 12.504 3.504 12 4.125 12h2.25c.621 0 1.125.504 1.125 1.125v6.75C7.5 20.496 6.996 21 6.375 21h-2.25A1.125 1.125 0 013 19.875v-6.75zM9.75 8.625c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125v11.25c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V8.625zM16.5 4.125c0-.621.504-1.125 1.125-1.125h2.25C20.496 3 21 3.504 21 4.125v15.75c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 01-1.125-1.125V4.125z" />
              </svg>
              <p className="text-lg font-medium">No agent metrics data yet</p>
              <p className="text-sm mt-1">
                Deploy the agent with <code className="text-xs bg-gray-100 px-1.5 py-0.5 rounded">AgentSelfMetricsCollector</code> enabled.
                <br />
                Metrics will appear here after the first enrollment sessions complete.
              </p>
            </div>
          )}
        </main>
      </div>
    </ProtectedRoute>
  );
}

// ── Sub-components ─────────────────────────────────────────────────────────────

function StatCard({ label, value, detail, color }: { label: string; value: string; detail: string; color: string }) {
  const borderColor: Record<string, string> = {
    green: 'border-green-500',
    yellow: 'border-yellow-500',
    red: 'border-red-500',
    blue: 'border-blue-500',
    purple: 'border-purple-500',
  };
  const textColor: Record<string, string> = {
    green: 'text-green-700',
    yellow: 'text-yellow-700',
    red: 'text-red-700',
    blue: 'text-blue-700',
    purple: 'text-purple-700',
  };

  return (
    <div className={`bg-white shadow rounded-lg p-4 border-l-4 ${borderColor[color] || 'border-gray-300'}`}>
      <p className="text-xs text-gray-500 uppercase tracking-wide">{label}</p>
      <p className={`text-2xl font-bold mt-1 ${textColor[color] || 'text-gray-900'}`}>{value}</p>
      <p className="text-xs text-gray-400 mt-1">{detail}</p>
    </div>
  );
}

function FootprintBadge({
  value,
  thresholds,
  unit,
  formatFn,
}: {
  value: number;
  thresholds: [number, number, number];
  unit: string;
  formatFn?: (v: number) => string;
}) {
  let label: string;
  let className: string;

  if (value < thresholds[0]) {
    label = 'Minimal';
    className = 'bg-green-100 text-green-800';
  } else if (value < thresholds[1]) {
    label = 'Light';
    className = 'bg-green-50 text-green-700';
  } else if (value < thresholds[2]) {
    label = 'Moderate';
    className = 'bg-yellow-100 text-yellow-800';
  } else {
    label = 'Heavy';
    className = 'bg-red-100 text-red-800';
  }

  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${className}`}>
      {label}
    </span>
  );
}
