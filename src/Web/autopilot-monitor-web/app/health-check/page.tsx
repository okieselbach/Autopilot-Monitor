"use client";

import { useAuth } from '@/contexts/AuthContext';
import { useNotifications } from '@/contexts/NotificationContext';
import { useSignalR } from '@/contexts/SignalRContext';
import { useTenant } from '@/contexts/TenantContext';
import { useState, useEffect, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';
import { API_BASE_URL } from '@/lib/config';
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface HealthCheck {
  name: string;
  description: string;
  status: string;
  message: string;
  details?: Record<string, any>;
}

interface HealthCheckResult {
  service: string;
  timestamp: string;
  overallStatus: string;
  checks: HealthCheck[];
}

export default function HealthCheckPage() {
  const { user, getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const { connectionState, isConnected, joinedGroups, joinGroup } = useSignalR();
  const { tenantId } = useTenant();
  const [healthResult, setHealthResult] = useState<HealthCheckResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [hasRun, setHasRun] = useState(false);
  const [groupJoinTest, setGroupJoinTest] = useState<'idle' | 'testing' | 'success' | 'failed'>('idle');

  const performHealthCheck = useCallback(async () => {
    setLoading(true);
    try {
      const response = await authenticatedFetch(`${API_BASE_URL}/api/health/detailed`, getAccessToken);

      if (!response.ok) {
        if (response.status === 403) {
          addNotification('error', 'Access Denied', 'You do not have permission to access health checks', 'health-check-forbidden');
        } else {
          addNotification('error', 'Health Check Failed', `Status: ${response.status}`, 'health-check-failed');
        }
        return;
      }

      const data = await response.json();
      setHealthResult(data);
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error('Health check error:', error);
        addNotification('error', 'Health Check Error', error instanceof Error ? error.message : 'Unknown error', 'health-check-error');
      }
    } finally {
      setLoading(false);
      setHasRun(true);
    }
  }, [getAccessToken, addNotification]);

  // Auto-run health check on page load
  useEffect(() => {
    if (user && !hasRun) {
      performHealthCheck();
    }
  }, [user, hasRun, performHealthCheck]);

  const testGroupJoin = useCallback(async () => {
    if (!tenantId || !isConnected) return;
    setGroupJoinTest('testing');
    try {
      await joinGroup(`tenant-${tenantId}`);
      // joinGroup updates joinedGroups state on success/failure
      // Small delay to let state propagate
      setTimeout(() => setGroupJoinTest('success'), 300);
    } catch {
      setGroupJoinTest('failed');
    }
  }, [tenantId, isConnected, joinGroup]);

  const getConnectionStateLabel = (state: signalR.HubConnectionState) => {
    switch (state) {
      case signalR.HubConnectionState.Connected: return 'Connected';
      case signalR.HubConnectionState.Connecting: return 'Connecting';
      case signalR.HubConnectionState.Reconnecting: return 'Reconnecting';
      case signalR.HubConnectionState.Disconnecting: return 'Disconnecting';
      case signalR.HubConnectionState.Disconnected: return 'Disconnected';
      default: return 'Unknown';
    }
  };

  const getConnectionStatus = (): 'healthy' | 'unhealthy' | 'warning' => {
    if (connectionState === signalR.HubConnectionState.Connected) return 'healthy';
    if (connectionState === signalR.HubConnectionState.Reconnecting || connectionState === signalR.HubConnectionState.Connecting) return 'warning';
    return 'unhealthy';
  };

  const getStatusColor = (status: string) => {
    switch (status) {
      case 'healthy': return { bg: 'bg-green-50 dark:bg-green-900/20', border: 'border-green-200 dark:border-green-800', text: 'text-green-700 dark:text-green-400', accent: 'border-green-500', badge: 'bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-400' };
      case 'unhealthy': return { bg: 'bg-red-50 dark:bg-red-900/20', border: 'border-red-200 dark:border-red-800', text: 'text-red-700 dark:text-red-400', accent: 'border-red-500', badge: 'bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-400' };
      case 'warning': return { bg: 'bg-yellow-50 dark:bg-yellow-900/20', border: 'border-yellow-200 dark:border-yellow-800', text: 'text-yellow-700 dark:text-yellow-400', accent: 'border-yellow-500', badge: 'bg-yellow-100 text-yellow-800 dark:bg-yellow-900/40 dark:text-yellow-400' };
      default: return { bg: 'bg-gray-50 dark:bg-gray-800', border: 'border-gray-200 dark:border-gray-700', text: 'text-gray-700 dark:text-gray-300', accent: 'border-gray-500', badge: 'bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300' };
    }
  };

  const connStatus = getConnectionStatus();
  const totalChecks = healthResult ? healthResult.checks.length + 1 : 0;
  const healthyChecks = healthResult ? healthResult.checks.filter(c => c.status === 'healthy').length + (connStatus === 'healthy' ? 1 : 0) : 0;
  const combinedOverallStatus = healthResult
    ? (connStatus === 'unhealthy' || healthResult.overallStatus === 'unhealthy')
      ? 'unhealthy'
      : (connStatus === 'warning' || healthResult.overallStatus === 'warning')
        ? 'warning'
        : 'healthy'
    : null;
  const overallColors = combinedOverallStatus ? getStatusColor(combinedOverallStatus) : null;

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <div className="max-w-7xl mx-auto py-6 px-4 sm:px-6 lg:px-8">
        {/* Header Section */}
        <div className="bg-white dark:bg-gray-800 rounded-lg shadow mb-6">
          <div className="p-6 border-b border-gray-200 dark:border-gray-700 bg-gradient-to-r from-blue-50 to-indigo-50 dark:from-blue-900/20 dark:to-indigo-900/20">
            <div className="flex items-center justify-between">
              <div className="flex items-center space-x-3">
                <div className="w-10 h-10 rounded-lg bg-blue-500 flex items-center justify-center">
                  <svg className="w-6 h-6 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                  </svg>
                </div>
                <div>
                  <h1 className="text-xl font-semibold text-gray-900 dark:text-white">System Health</h1>
                  <p className="text-sm text-gray-500 dark:text-gray-400">Infrastructure health monitoring</p>
                </div>
              </div>
              <button
                onClick={performHealthCheck}
                disabled={loading}
                className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors text-sm font-medium flex items-center gap-2"
              >
                {loading ? (
                  <>
                    <svg className="animate-spin h-4 w-4 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                    </svg>
                    Checking...
                  </>
                ) : (
                  <>
                    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                    Re-check
                  </>
                )}
              </button>
            </div>
          </div>

          {/* Overall Status Bar */}
          {healthResult && overallColors && (
            <div className={`p-4 flex items-center justify-between ${overallColors.bg}`}>
              <div className="flex items-center space-x-3">
                <div className={`w-3 h-3 rounded-full ${combinedOverallStatus === 'healthy' ? 'bg-green-500' : combinedOverallStatus === 'unhealthy' ? 'bg-red-500' : 'bg-yellow-500'}`}></div>
                <span className={`text-sm font-medium ${overallColors.text}`}>
                  {combinedOverallStatus === 'healthy' ? 'All systems operational' : combinedOverallStatus === 'unhealthy' ? 'System issues detected' : 'Warnings detected'}
                </span>
                <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${overallColors.badge}`}>
                  {healthyChecks}/{totalChecks} healthy
                </span>
              </div>
              <span className="text-xs text-gray-500 dark:text-gray-400 dark:text-gray-400">
                Last checked: {new Date(healthResult.timestamp).toLocaleString()}
              </span>
            </div>
          )}

          {/* Loading State */}
          {loading && !healthResult && (
            <div className="p-8 flex flex-col items-center justify-center">
              <svg className="animate-spin h-8 w-8 text-blue-500 mb-3" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
              </svg>
              <p className="text-sm text-gray-500 dark:text-gray-400">Running health checks...</p>
            </div>
          )}
        </div>

        {/* Real-Time Connection Status */}
        <div className="mb-6">
          <h2 className="text-sm font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-3">Real-Time Connection</h2>
          {(() => {
            const tenantGroup = `tenant-${tenantId}`;
            const hasTenantGroup = joinedGroups.includes(tenantGroup);
            const colors = getStatusColor(connStatus);
            return (
              <div className={`bg-white dark:bg-gray-800 rounded-lg shadow border-l-4 ${colors.accent}`}>
                <div className="p-6">
                  <div className="flex items-start justify-between mb-3">
                    <div className="flex items-center space-x-3">
                      <div className={`w-8 h-8 rounded-full ${colors.bg} flex items-center justify-center`}>
                        {connStatus === 'healthy' ? (
                          <svg className="w-5 h-5 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                          </svg>
                        ) : connStatus === 'unhealthy' ? (
                          <svg className="w-5 h-5 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        ) : (
                          <svg className="w-5 h-5 text-yellow-600 animate-spin" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                          </svg>
                        )}
                      </div>
                      <div>
                        <h3 className="text-lg font-semibold text-gray-900 dark:text-white">Live Updates</h3>
                        <p className="text-xs text-gray-500 dark:text-gray-400">Real-time event hub (Private Preview: 20 concurrent clients)</p>
                      </div>
                    </div>
                    <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors.badge}`}>
                      {getConnectionStateLabel(connectionState)}
                    </span>
                  </div>
                  <p className={`text-sm ${colors.text}`}>
                    {connStatus === 'healthy'
                      ? 'Connected to real-time event hub'
                      : connStatus === 'warning'
                      ? 'Attempting to establish connection...'
                      : 'Not connected — real-time updates unavailable'}
                  </p>
                  <div className="mt-4 bg-gray-50 dark:bg-gray-700/50 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
                    <h4 className="text-xs font-semibold text-gray-500 uppercase mb-2">Details</h4>
                    <dl className="space-y-1">
                      <div className="flex justify-between text-xs">
                        <dt className="font-medium text-gray-600 dark:text-gray-400">State</dt>
                        <dd className="text-gray-900 dark:text-gray-100 font-mono">{getConnectionStateLabel(connectionState)}</dd>
                      </div>
                      <div className="flex justify-between text-xs">
                        <dt className="font-medium text-gray-600 dark:text-gray-400">Tenant Group</dt>
                        <dd className={`font-mono ${hasTenantGroup ? 'text-green-700 dark:text-green-400' : 'text-gray-400'}`}>
                          {hasTenantGroup ? 'Joined' : 'Not joined'}
                        </dd>
                      </div>
                    </dl>
                    {isConnected && !hasTenantGroup && (
                      <button
                        onClick={testGroupJoin}
                        disabled={groupJoinTest === 'testing'}
                        className="mt-3 px-3 py-1.5 bg-blue-600 text-white rounded text-xs font-medium hover:bg-blue-700 disabled:opacity-50 transition-colors"
                      >
                        {groupJoinTest === 'testing' ? 'Testing...' : groupJoinTest === 'failed' ? 'Retry Join Test' : 'Test Group Join'}
                      </button>
                    )}
                    {groupJoinTest === 'failed' && (
                      <p className="mt-2 text-xs text-red-600">
                        Group join failed — likely hit the 20 concurrent client limit
                      </p>
                    )}
                  </div>
                </div>
              </div>
            );
          })()}
        </div>

        {/* Individual Check Cards */}
        {healthResult && (
          <div>
          <h2 className="text-sm font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wider mb-3">Backend Services</h2>
          <div className="grid gap-5 sm:grid-cols-2">
            {healthResult.checks.map((check, index) => {
              const colors = getStatusColor(check.status);
              return (
                <div key={index} className={`bg-white dark:bg-gray-800 rounded-lg shadow border-l-4 ${colors.accent}`}>
                  <div className="p-6">
                    <div className="flex items-start justify-between mb-3">
                      <div className="flex items-center space-x-3">
                        <div className={`w-8 h-8 rounded-full ${colors.bg} flex items-center justify-center`}>
                          {check.status === 'healthy' ? (
                            <svg className="w-5 h-5 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                            </svg>
                          ) : check.status === 'unhealthy' ? (
                            <svg className="w-5 h-5 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                            </svg>
                          ) : (
                            <svg className="w-5 h-5 text-yellow-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v2m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                            </svg>
                          )}
                        </div>
                        <div>
                          <h3 className="text-lg font-semibold text-gray-900 dark:text-white">{check.name}</h3>
                          <p className="text-xs text-gray-500 dark:text-gray-400">{check.description}</p>
                        </div>
                      </div>
                      <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-medium ${colors.badge}`}>
                        {check.status}
                      </span>
                    </div>

                    <p className={`text-sm ${colors.text}`}>
                      {check.message}
                    </p>

                    {check.details && Object.keys(check.details).length > 0 && (
                      <div className="mt-4 bg-gray-50 dark:bg-gray-700/50 rounded-lg p-3 border border-gray-200 dark:border-gray-600">
                        <h4 className="text-xs font-semibold text-gray-500 uppercase mb-2">Details</h4>
                        <dl className="space-y-1">
                          {Object.entries(check.details).map(([key, value]) => (
                            <div key={key} className="flex justify-between text-xs">
                              <dt className="font-medium text-gray-600 dark:text-gray-400">{key}</dt>
                              <dd className="text-gray-900 dark:text-gray-100 font-mono">
                                {Array.isArray(value) ? value.join(', ') : typeof value === 'object' ? JSON.stringify(value) : String(value)}
                              </dd>
                            </div>
                          ))}
                        </dl>
                      </div>
                    )}
                  </div>
                </div>
              );
            })}
          </div>
          </div>
        )}
      </div>
    </div>
  );
}
