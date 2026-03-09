'use client';

import { useEffect, useState, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { useTenant } from '../../contexts/TenantContext';
import { useAuth } from '../../contexts/AuthContext';
import { useNotifications } from '../../contexts/NotificationContext';
import { ProtectedRoute } from '../../components/ProtectedRoute';
import { API_BASE_URL } from '@/lib/config';
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface TenantInfo {
  tenantId: string;
  domainName: string;
}

interface SessionMetrics {
  total: number;
  today: number;
  last7Days: number;
  last30Days: number;
  succeeded: number;
  failed: number;
  inProgress: number;
  successRate: number;
}

interface TenantMetrics {
  total: number;
  active7Days: number;
  active30Days: number;
}

interface UserMetrics {
  total: number;
  dailyLogins: number;
  active7Days: number;
  active30Days: number;
  note: string;
}

interface PerformanceMetrics {
  avgDurationMinutes: number;
  medianDurationMinutes: number;
  p95DurationMinutes: number;
  p99DurationMinutes: number;
}

interface HardwareCount {
  name: string;
  count: number;
  percentage: number;
}

interface HardwareMetrics {
  topManufacturers: HardwareCount[];
  topModels: HardwareCount[];
}

interface DeploymentTypeMetrics {
  userDriven: number;
  whiteGlove: number;
  userDrivenPercentage: number;
  whiteGlovePercentage: number;
}

interface PlatformUsageMetrics {
  sessions: SessionMetrics;
  tenants: TenantMetrics;
  users: UserMetrics;
  performance: PerformanceMetrics;
  hardware: HardwareMetrics;
  deploymentTypes: DeploymentTypeMetrics;
  computedAt: string;
  computeDurationMs: number;
  fromCache: boolean;
}

export default function UsageMetricsPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const { getAccessToken, user } = useAuth();
  const { addNotification } = useNotifications();
  const [metrics, setMetrics] = useState<PlatformUsageMetrics | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  // Galactic admin mode
  const [galacticAdminMode] = useState(() => {
    if (typeof window !== 'undefined') {
      return localStorage.getItem('galacticAdminMode') === 'true';
    }
    return false;
  });
  const [tenants, setTenants] = useState<TenantInfo[]>([]);
  const [selectedTenantId, setSelectedTenantId] = useState<string>('');

  // Fetch tenant list for galactic admin
  useEffect(() => {
    if (!galacticAdminMode || !user?.isGalacticAdmin) return;
    const fetchTenants = async () => {
      try {
        const response = await authenticatedFetch(`${API_BASE_URL}/api/config/all`, getAccessToken);
        if (response.ok) {
          const data = await response.json();
          setTenants(data.map((t: { tenantId: string; domainName: string }) => ({
            tenantId: t.tenantId,
            domainName: t.domainName || '',
          })));
        }
      } catch (err) {
        console.error('Error fetching tenant list:', err);
      }
    };
    fetchTenants();
  }, [galacticAdminMode, user?.isGalacticAdmin]);

  // Set default selected tenant once tenantId is available
  useEffect(() => {
    if (tenantId && !selectedTenantId) {
      setSelectedTenantId(tenantId);
    }
  }, [tenantId]);

  const isGalacticOverride = galacticAdminMode && user?.isGalacticAdmin && selectedTenantId && selectedTenantId !== tenantId;
  const effectiveTenantId = (galacticAdminMode && user?.isGalacticAdmin && selectedTenantId) ? selectedTenantId : tenantId;

  const fetchMetrics = useCallback(async (showRefreshing = false) => {
    if (!effectiveTenantId) return;
    try {
      if (showRefreshing) {
        setRefreshing(true);
      } else {
        setLoading(true);
      }

      // Galactic admin viewing another tenant → use galactic endpoint
      const url = isGalacticOverride
        ? `${API_BASE_URL}/api/galactic/metrics/usage?tenantId=${effectiveTenantId}`
        : `${API_BASE_URL}/api/metrics/usage?tenantId=${effectiveTenantId}`;

      const response = await authenticatedFetch(url, getAccessToken);

      if (!response.ok) {
        addNotification('error', 'Backend Error', `Failed to load usage metrics: ${response.statusText}`, 'usage-metrics-fetch-error');
        return;
      }

      const data = await response.json();
      setMetrics(data);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', err.message, 'session-expired-error');
      } else {
        console.error('Error fetching usage metrics:', err);
        addNotification('error', 'Backend Not Reachable', 'Unable to load usage metrics. Please check your connection.', 'usage-metrics-fetch-error');
      }
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [effectiveTenantId, isGalacticOverride, getAccessToken, addNotification]);

  useEffect(() => {
    if (!effectiveTenantId) return;
    fetchMetrics();
  }, [effectiveTenantId]);

  // Re-fetch when tenant selection changes
  const handleTenantChange = (newTenantId: string) => {
    setSelectedTenantId(newTenantId);
  };

  const selectedTenantName = tenants.find(t => t.tenantId === selectedTenantId)?.domainName;

  const formatDuration = (minutes: number) => {
    if (minutes < 60) {
      return `${minutes.toFixed(1)}m`;
    }
    const hours = Math.floor(minutes / 60);
    const mins = Math.round(minutes % 60);
    return `${hours}h ${mins}m`;
  };

  const formatTimestamp = (timestamp: string) => {
    const date = new Date(timestamp);
    return date.toLocaleString();
  };

  if (loading) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading usage metrics...</p>
        </div>
      </div>
    );
  }

  if (!metrics) {
    return null;
  }

  return (
<ProtectedRoute>
    <div className="min-h-screen bg-gray-50">
      {galacticAdminMode && user?.isGalacticAdmin && (
        <div className="bg-purple-700 text-white text-sm px-4 py-2 flex items-center justify-center space-x-2">
          <svg className="w-4 h-4 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
          </svg>
          <span className="font-medium">Galactic Admin View</span>
          <span className="text-purple-300">&mdash; access to all tenants</span>
        </div>
      )}
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
              <div>
                <h1 className="text-3xl font-bold text-gray-900">Usage Metrics</h1>
                <p className="text-sm text-gray-600 mt-1">
                  {isGalacticOverride && selectedTenantName
                    ? `Tenant: ${selectedTenantName} · `
                    : ''}
                  Computed at {formatTimestamp(metrics.computedAt)} in {metrics.computeDurationMs}ms
                  {metrics.fromCache && (
                    <span className="ml-2 px-2 py-0.5 bg-blue-100 text-blue-700 text-xs rounded">
                      From Cache
                    </span>
                  )}
                </p>
              </div>
            </div>
            <div className="flex items-center gap-3">
              {galacticAdminMode && user?.isGalacticAdmin && tenants.length > 0 && (
                <>
                  <label className="text-sm text-gray-500 hidden sm:inline">Tenant:</label>
                  <select
                    value={selectedTenantId}
                    onChange={(e) => handleTenantChange(e.target.value)}
                    className="text-sm border border-gray-300 rounded-md px-2 py-1.5 max-w-[220px] sm:max-w-xs"
                  >
                    {tenants.map((t) => (
                      <option key={t.tenantId} value={t.tenantId}>
                        {t.domainName
                          ? `${t.domainName} (${t.tenantId.substring(0, 8)}...)`
                          : t.tenantId}
                      </option>
                    ))}
                  </select>
                </>
              )}
              <button
                onClick={() => fetchMetrics(true)}
                disabled={refreshing}
                className="px-4 py-2 bg-white border border-gray-200 text-gray-700 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
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

        {/* Session Statistics */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Sessions</h2>
          <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Total Sessions</div>
              <div className="text-3xl font-bold text-gray-900">{metrics.sessions.total.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Today</div>
              <div className="text-3xl font-bold text-blue-600">{metrics.sessions.today.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Last 7 Days</div>
              <div className="text-3xl font-bold text-indigo-600">{metrics.sessions.last7Days.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Last 30 Days</div>
              <div className="text-3xl font-bold text-purple-600">{metrics.sessions.last30Days.toLocaleString()}</div>
            </div>
          </div>

          <div className="grid grid-cols-1 md:grid-cols-4 gap-4 mt-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Succeeded</div>
              <div className="text-3xl font-bold text-green-600">{metrics.sessions.succeeded.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Failed</div>
              <div className="text-3xl font-bold text-red-600">{metrics.sessions.failed.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">In Progress</div>
              <div className="text-3xl font-bold text-yellow-600">{metrics.sessions.inProgress.toLocaleString()}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Success Rate</div>
              <div className="text-3xl font-bold text-gray-900">{metrics.sessions.successRate}%</div>
            </div>
          </div>
        </div>

        {/* Deployment Types */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Deployment Types</h2>
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="flex items-center justify-between mb-3">
                <div>
                  <div className="text-sm text-gray-500 mb-1">User Driven</div>
                  <div className="text-3xl font-bold text-blue-600">{metrics.deploymentTypes.userDriven.toLocaleString()}</div>
                </div>
                <div className="text-right">
                  <span className="text-2xl font-semibold text-blue-500">{metrics.deploymentTypes.userDrivenPercentage}%</span>
                </div>
              </div>
              <div className="w-full bg-gray-200 rounded-full h-2.5">
                <div
                  className="bg-blue-600 h-2.5 rounded-full transition-all duration-300"
                  style={{ width: `${metrics.deploymentTypes.userDrivenPercentage}%` }}
                ></div>
              </div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="flex items-center justify-between mb-3">
                <div>
                  <div className="text-sm text-gray-500 mb-1">Pre-Provisioned</div>
                  <div className="text-3xl font-bold text-purple-600">{metrics.deploymentTypes.whiteGlove.toLocaleString()}</div>
                </div>
                <div className="text-right">
                  <span className="text-2xl font-semibold text-purple-500">{metrics.deploymentTypes.whiteGlovePercentage}%</span>
                </div>
              </div>
              <div className="w-full bg-gray-200 rounded-full h-2.5">
                <div
                  className="bg-purple-600 h-2.5 rounded-full transition-all duration-300"
                  style={{ width: `${metrics.deploymentTypes.whiteGlovePercentage}%` }}
                ></div>
              </div>
            </div>
          </div>
        </div>

        {/* User Statistics */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Users</h2>
          {metrics.users.total > 0 || metrics.users.dailyLogins > 0 || metrics.users.active7Days > 0 || metrics.users.active30Days > 0 ? (
            <>
              <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
                <div className="bg-white rounded-lg shadow p-6">
                  <div className="text-sm text-gray-500 mb-1">Total Users</div>
                  <div className="text-3xl font-bold text-gray-900">{metrics.users.total.toLocaleString()}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-6">
                  <div className="text-sm text-gray-500 mb-1">Daily Logins</div>
                  <div className="text-3xl font-bold text-blue-600">{metrics.users.dailyLogins.toLocaleString()}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-6">
                  <div className="text-sm text-gray-500 mb-1">Active (Last 7 Days)</div>
                  <div className="text-3xl font-bold text-indigo-600">{metrics.users.active7Days.toLocaleString()}</div>
                </div>
                <div className="bg-white rounded-lg shadow p-6">
                  <div className="text-sm text-gray-500 mb-1">Active (Last 30 Days)</div>
                  <div className="text-3xl font-bold text-purple-600">{metrics.users.active30Days.toLocaleString()}</div>
                </div>
              </div>
              {metrics.users.note && (
                <p className="mt-3 text-xs text-gray-500">{metrics.users.note}</p>
              )}
            </>
          ) : (
            <div className="bg-white rounded-lg shadow p-6">
              <div className="grid grid-cols-1 md:grid-cols-4 gap-4 mb-4 opacity-40">
                <div>
                  <div className="text-sm text-gray-500 mb-1">Total Users</div>
                  <div className="text-3xl font-bold text-gray-400">--</div>
                </div>
                <div>
                  <div className="text-sm text-gray-500 mb-1">Daily Logins</div>
                  <div className="text-3xl font-bold text-gray-400">--</div>
                </div>
                <div>
                  <div className="text-sm text-gray-500 mb-1">Active (7 Days)</div>
                  <div className="text-3xl font-bold text-gray-400">--</div>
                </div>
                <div>
                  <div className="text-sm text-gray-500 mb-1">Active (30 Days)</div>
                  <div className="text-3xl font-bold text-gray-400">--</div>
                </div>
              </div>
              <div className="flex items-center space-x-3 pt-3 border-t border-gray-100">
                <svg className="h-5 w-5 text-amber-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <p className="text-sm text-gray-600">{metrics.users.note || 'User metrics will be available when Entra ID authentication tracking is enabled.'}</p>
              </div>
            </div>
          )}
        </div>

        {/* Performance Statistics */}
        <div className="mb-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Performance</h2>
          <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Average Duration</div>
              <div className="text-3xl font-bold text-gray-900">{formatDuration(metrics.performance.avgDurationMinutes)}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">Median Duration</div>
              <div className="text-3xl font-bold text-blue-600">{formatDuration(metrics.performance.medianDurationMinutes)}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">P95 Duration</div>
              <div className="text-3xl font-bold text-orange-600">{formatDuration(metrics.performance.p95DurationMinutes)}</div>
            </div>
            <div className="bg-white rounded-lg shadow p-6">
              <div className="text-sm text-gray-500 mb-1">P99 Duration</div>
              <div className="text-3xl font-bold text-red-600">{formatDuration(metrics.performance.p99DurationMinutes)}</div>
            </div>
          </div>
        </div>

        {/* Hardware Statistics */}
        <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
          {/* Top Manufacturers */}
          <div>
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Top Manufacturers</h2>
            <div className="bg-white rounded-lg shadow overflow-hidden">
              <div className="divide-y divide-gray-200">
                {metrics.hardware.topManufacturers.length > 0 ? (
                  metrics.hardware.topManufacturers.map((item, index) => (
                    <div key={index} className="p-4 hover:bg-gray-50 transition-colors">
                      <div className="flex items-center justify-between mb-2">
                        <span className="font-medium text-gray-900">{item.name || 'Unknown'}</span>
                        <div className="text-right">
                          <span className="text-sm font-semibold text-gray-900">{item.count.toLocaleString()}</span>
                          <span className="text-xs text-gray-500 ml-2">({item.percentage}%)</span>
                        </div>
                      </div>
                      <div className="w-full bg-gray-200 rounded-full h-2">
                        <div
                          className="bg-blue-600 h-2 rounded-full transition-all duration-300"
                          style={{ width: `${item.percentage}%` }}
                        ></div>
                      </div>
                    </div>
                  ))
                ) : (
                  <div className="p-8 text-center text-gray-500">No data available</div>
                )}
              </div>
            </div>
          </div>

          {/* Top Models */}
          <div>
            <h2 className="text-xl font-semibold text-gray-900 mb-4">Top Models</h2>
            <div className="bg-white rounded-lg shadow overflow-hidden">
              <div className="divide-y divide-gray-200">
                {metrics.hardware.topModels.length > 0 ? (
                  metrics.hardware.topModels.map((item, index) => (
                    <div key={index} className="p-4 hover:bg-gray-50 transition-colors">
                      <div className="flex items-center justify-between mb-2">
                        <span className="font-medium text-gray-900">{item.name || 'Unknown'}</span>
                        <div className="text-right">
                          <span className="text-sm font-semibold text-gray-900">{item.count.toLocaleString()}</span>
                          <span className="text-xs text-gray-500 ml-2">({item.percentage}%)</span>
                        </div>
                      </div>
                      <div className="w-full bg-gray-200 rounded-full h-2">
                        <div
                          className="bg-indigo-600 h-2 rounded-full transition-all duration-300"
                          style={{ width: `${item.percentage}%` }}
                        ></div>
                      </div>
                    </div>
                  ))
                ) : (
                  <div className="p-8 text-center text-gray-500">No data available</div>
                )}
              </div>
            </div>
          </div>
        </div>
      </div>
    </div>
  </ProtectedRoute>
  );
}
