"use client";

import { useAuth } from '@/contexts/AuthContext';
import { useNotifications } from '@/contexts/NotificationContext';
import { useState, useEffect } from 'react';
import { API_BASE_URL } from '@/lib/config';

interface HealthCheck {
  name: string;
  description: string;
  status: string;
  message: string;
  details?: Record<string, any>;
}

interface HealthCheckResult {
  service: string;
  version: string;
  timestamp: string;
  overallStatus: string;
  checks: HealthCheck[];
}

export default function HealthCheckPage() {
  const { user, getAccessToken } = useAuth();
  const { addNotification } = useNotifications();
  const [healthResult, setHealthResult] = useState<HealthCheckResult | null>(null);
  const [loading, setLoading] = useState(false);

  const performHealthCheck = async () => {
    setLoading(true);
    try {
      const token = await getAccessToken();
      if (!token) {
        addNotification('error', 'Authentication Error', 'No access token available', 'health-check-auth-error');
        return;
      }

      const response = await fetch(`${API_BASE_URL}/api/health/detailed`, {
        headers: {
          'Authorization': `Bearer ${token}`,
        },
      });

      if (!response.ok) {
        if (response.status === 403) {
          addNotification('error', 'Access Denied', 'Only Galactic Admins can access health checks', 'health-check-forbidden');
        } else {
          addNotification('error', 'Health Check Failed', `Status: ${response.status}`, 'health-check-failed');
        }
        return;
      }

      const data = await response.json();
      setHealthResult(data);

      if (data.overallStatus === 'healthy') {
        addNotification('success', 'Health Check Complete', 'All systems are healthy');
      } else {
        addNotification('warning', 'Health Check Complete', 'Some systems are unhealthy');
      }
    } catch (error) {
      console.error('Health check error:', error);
      addNotification('error', 'Health Check Error', error instanceof Error ? error.message : 'Unknown error', 'health-check-error');
    } finally {
      setLoading(false);
    }
  };

  const getStatusColor = (status: string) => {
    switch (status) {
      case 'healthy':
        return 'text-green-600 bg-green-50 border-green-200';
      case 'unhealthy':
        return 'text-red-600 bg-red-50 border-red-200';
      case 'warning':
        return 'text-yellow-600 bg-yellow-50 border-yellow-200';
      default:
        return 'text-gray-600 bg-gray-50 border-gray-200';
    }
  };

  const getStatusIcon = (status: string) => {
    switch (status) {
      case 'healthy':
        return '✅';
      case 'unhealthy':
        return '❌';
      case 'warning':
        return '⚠️';
      default:
        return '❓';
    }
  };

  // Check if user is galactic admin
  if (!user?.isGalacticAdmin) {
    return (
      <div className="min-h-screen bg-gray-50 flex items-center justify-center p-4">
        <div className="max-w-md w-full bg-white rounded-lg shadow-lg p-8 text-center">
          <div className="text-6xl mb-4">🔒</div>
          <h1 className="text-2xl font-bold text-gray-900 mb-2">Access Denied</h1>
          <p className="text-gray-600">
            Only Galactic Admins can access the health check dashboard.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-50 p-4 sm:p-6 lg:p-8">
      <div className="max-w-6xl mx-auto">
        {/* Header */}
        <div className="bg-white rounded-lg shadow-lg p-6 mb-6">
          <div className="flex items-center justify-between">
            <div>
              <h1 className="text-3xl font-bold text-gray-900 mb-2">System Health Check</h1>
              <p className="text-gray-600">
                Health monitoring for Autopilot Monitor infrastructure
              </p>
            </div>
            <button
              onClick={performHealthCheck}
              disabled={loading}
              className="px-6 py-3 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors font-medium flex items-center gap-2"
            >
              {loading ? (
                <>
                  <svg className="animate-spin h-5 w-5 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                    <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                    <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
                  </svg>
                  Running...
                </>
              ) : (
                <>
                  <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-6 9l2 2 4-4" />
                  </svg>
                  Run Health Check
                </>
              )}
            </button>
          </div>
        </div>

        {/* Health Check Results */}
        {healthResult && (
          <>
            {/* Overall Status */}
            <div className={`rounded-lg shadow-lg p-6 mb-6 border-2 ${getStatusColor(healthResult.overallStatus)}`}>
              <div className="flex items-center justify-between">
                <div className="flex items-center gap-4">
                  <div className="text-5xl">{getStatusIcon(healthResult.overallStatus)}</div>
                  <div>
                    <h2 className="text-2xl font-bold">Overall Status: {healthResult.overallStatus.toUpperCase()}</h2>
                    <p className="text-sm mt-1">
                      {healthResult.service} v{healthResult.version}
                    </p>
                    <p className="text-xs mt-1 opacity-75">
                      Last checked: {new Date(healthResult.timestamp).toLocaleString()}
                    </p>
                  </div>
                </div>
                <div className="text-right">
                  <div className="text-sm font-medium">
                    {healthResult.checks.filter(c => c.status === 'healthy').length} / {healthResult.checks.length} Healthy
                  </div>
                </div>
              </div>
            </div>

            {/* Individual Checks */}
            <div className="grid gap-6 md:grid-cols-2">
              {healthResult.checks.map((check, index) => (
                <div key={index} className={`bg-white rounded-lg shadow-lg p-6 border-l-4 ${getStatusColor(check.status)}`}>
                  <div className="flex items-start justify-between mb-4">
                    <div className="flex items-center gap-3">
                      <span className="text-3xl">{getStatusIcon(check.status)}</span>
                      <div>
                        <h3 className="text-xl font-bold text-gray-900">{check.name}</h3>
                        <p className="text-sm text-gray-600">{check.description}</p>
                      </div>
                    </div>
                  </div>

                  <div className="mb-4">
                    <p className={`text-sm font-medium ${
                      check.status === 'healthy' ? 'text-green-700' :
                      check.status === 'warning' ? 'text-yellow-700' :
                      'text-red-700'
                    }`}>
                      {check.message}
                    </p>
                  </div>

                  {check.details && Object.keys(check.details).length > 0 && (
                    <div className="bg-gray-50 rounded-lg p-4 border border-gray-200">
                      <h4 className="text-xs font-semibold text-gray-700 uppercase mb-2">Details</h4>
                      <dl className="space-y-2">
                        {Object.entries(check.details).map(([key, value]) => (
                          <div key={key} className="flex justify-between text-sm">
                            <dt className="font-medium text-gray-600">{key}:</dt>
                            <dd className="text-gray-900 font-mono">
                              {Array.isArray(value) ? (
                                <div className="text-right">
                                  {value.map((item, i) => (
                                    <div key={i}>{item}</div>
                                  ))}
                                </div>
                              ) : typeof value === 'object' ? (
                                JSON.stringify(value)
                              ) : (
                                String(value)
                              )}
                            </dd>
                          </div>
                        ))}
                      </dl>
                    </div>
                  )}
                </div>
              ))}
            </div>
          </>
        )}

        {/* Initial State - No Results */}
        {!healthResult && !loading && (
          <div className="bg-white rounded-lg shadow-lg p-12 text-center">
            <div className="text-6xl mb-4">🏥</div>
            <h2 className="text-2xl font-bold text-gray-900 mb-2">No Health Check Results</h2>
            <p className="text-gray-600 mb-6">
              Click &quot;Run Health Check&quot; to perform a comprehensive system health check
            </p>
            <div className="max-w-2xl mx-auto text-left bg-gray-50 rounded-lg p-6">
              <h3 className="font-semibold text-gray-900 mb-3">What will be checked:</h3>
              <ul className="space-y-2 text-sm text-gray-700">
                <li className="flex items-start gap-2">
                  <span className="text-blue-600 mt-0.5">•</span>
                  <span><strong>Table Storage:</strong> Azure Table Storage connectivity and response time</span>
                </li>
                <li className="flex items-start gap-2">
                  <span className="text-blue-600 mt-0.5">•</span>
                  <span><strong>Functions Host:</strong> Azure Functions host process status and uptime</span>
                </li>
              </ul>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
