"use client";

import { useEffect, useState } from "react";
import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useTenant } from "../../contexts/TenantContext";
import { API_BASE_URL } from "@/lib/config";

interface TenantConfiguration {
  tenantId: string;
  lastUpdated: string;
  updatedBy: string;
  securityEnabled: boolean;
  rateLimitRequestsPerMinute: number;
  manufacturerWhitelist: string;
  modelWhitelist: string;
  validateSerialNumber: boolean;
  dataRetentionDays: number;
  sessionTimeoutHours: number;
  customSettings?: string;
}

export default function SettingsPage() {
  const router = useRouter();
  const { tenantId } = useTenant();
  const [config, setConfig] = useState<TenantConfiguration | null>(null);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);

  // Form state
  const [securityEnabled, setSecurityEnabled] = useState(true);
  const [rateLimitRequestsPerMinute, setRateLimitRequestsPerMinute] = useState(100);
  const [manufacturerWhitelist, setManufacturerWhitelist] = useState("Dell*,HP*,Lenovo*,Microsoft Corporation");
  const [modelWhitelist, setModelWhitelist] = useState("*");
  const [validateSerialNumber, setValidateSerialNumber] = useState(false);
  const [dataRetentionDays, setDataRetentionDays] = useState(90);
  const [sessionTimeoutHours, setSessionTimeoutHours] = useState(5);

  // Galactic Admin state
  const [galacticAdminMode, setGalacticAdminMode] = useState(false);
  const [triggeringMaintenance, setTriggeringMaintenance] = useState(false);

  // Load galactic admin mode from localStorage
  useEffect(() => {
    const galacticMode = localStorage.getItem("galacticAdminMode") === "true";
    setGalacticAdminMode(galacticMode);
  }, []);

  // Fetch configuration
  useEffect(() => {
    if (!tenantId) return;

    const fetchConfiguration = async () => {
      try {
        setLoading(true);
        setError(null);

        const response = await fetch(`${API_BASE_URL}/api/config/${tenantId}`);

        if (!response.ok) {
          throw new Error(`Failed to load configuration: ${response.statusText}`);
        }

        const data: TenantConfiguration = await response.json();
        setConfig(data);

        // Update form state
        setSecurityEnabled(data.securityEnabled);
        setRateLimitRequestsPerMinute(data.rateLimitRequestsPerMinute);
        setManufacturerWhitelist(data.manufacturerWhitelist);
        setModelWhitelist(data.modelWhitelist);
        setValidateSerialNumber(data.validateSerialNumber);
        setDataRetentionDays(data.dataRetentionDays ?? 90);
        setSessionTimeoutHours(data.sessionTimeoutHours ?? 5);
      } catch (err) {
        console.error("Error fetching configuration:", err);
        setError(err instanceof Error ? err.message : "Failed to load configuration");
      } finally {
        setLoading(false);
      }
    };

    fetchConfiguration();
  }, [tenantId]);

  const handleSave = async () => {
    if (!tenantId || !config) return;

    try {
      setSaving(true);
      setError(null);
      setSuccessMessage(null);

      const updatedConfig: TenantConfiguration = {
        ...config,
        securityEnabled,
        rateLimitRequestsPerMinute,
        manufacturerWhitelist,
        modelWhitelist,
        validateSerialNumber,
        dataRetentionDays,
        sessionTimeoutHours,
      };

      const response = await fetch(`${API_BASE_URL}/api/config/${tenantId}`, {
        method: "PUT",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify(updatedConfig),
      });

      if (!response.ok) {
        throw new Error(`Failed to save configuration: ${response.statusText}`);
      }

      const result = await response.json();
      setConfig(result.config);
      setSuccessMessage("Configuration saved successfully!");

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error saving configuration:", err);
      setError(err instanceof Error ? err.message : "Failed to save configuration");
    } finally {
      setSaving(false);
    }
  };

  const handleReset = () => {
    if (!config) return;

    setSecurityEnabled(config.securityEnabled);
    setRateLimitRequestsPerMinute(config.rateLimitRequestsPerMinute);
    setManufacturerWhitelist(config.manufacturerWhitelist);
    setModelWhitelist(config.modelWhitelist);
    setValidateSerialNumber(config.validateSerialNumber);
    setDataRetentionDays(config.dataRetentionDays ?? 90);
    setSessionTimeoutHours(config.sessionTimeoutHours ?? 5);
    setSuccessMessage(null);
    setError(null);
  };

  const handleTriggerMaintenance = async () => {
    try {
      setTriggeringMaintenance(true);
      setError(null);
      setSuccessMessage(null);

      const response = await fetch(`${API_BASE_URL}/api/maintenance/trigger`, {
        method: "POST",
      });

      if (!response.ok) {
        throw new Error(`Failed to trigger maintenance: ${response.statusText}`);
      }

      const result = await response.json();
      setSuccessMessage("Daily maintenance job completed successfully!");

      // Auto-hide success message after 5 seconds
      setTimeout(() => setSuccessMessage(null), 5000);
    } catch (err) {
      console.error("Error triggering maintenance:", err);
      setError(err instanceof Error ? err.message : "Failed to trigger maintenance job");
    } finally {
      setTriggeringMaintenance(false);
    }
  };

  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100">
        {/* Header */}
        <header className="bg-white shadow-sm border-b border-gray-200">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-4">
          <div className="flex items-center justify-between">
            <div className="flex items-center space-x-4">
              <button
                onClick={() => router.push("/")}
                className="flex items-center space-x-2 text-gray-600 hover:text-gray-900 transition-colors"
              >
                <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" />
                </svg>
                <span>Back to Dashboard</span>
              </button>
            </div>
            <div>
              <h1 className="text-2xl font-bold text-gray-900">Tenant Configuration</h1>
              <p className="text-sm text-gray-500">Tenant: {tenantId}</p>
            </div>
          </div>
        </div>
      </header>

      {/* Main Content */}
      <main className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        {loading ? (
          <div className="bg-white rounded-lg shadow p-8 text-center">
            <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-indigo-600 mx-auto"></div>
            <p className="mt-4 text-gray-600">Loading configuration...</p>
          </div>
        ) : (
          <div className="space-y-6">
            {/* Success Message */}
            {successMessage && (
              <div className="bg-green-50 border border-green-200 rounded-lg p-4 flex items-center space-x-3">
                <svg className="w-5 h-5 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span className="text-green-800 font-medium">{successMessage}</span>
              </div>
            )}

            {/* Error Message */}
            {error && (
              <div className="bg-red-50 border border-red-200 rounded-lg p-4 flex items-center space-x-3">
                <svg className="w-5 h-5 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span className="text-red-800">{error}</span>
              </div>
            )}

            {/* Security Toggle */}
            <div className="bg-white rounded-lg shadow">
              <div className="p-6 border-b border-gray-200">
                <h2 className="text-xl font-semibold text-gray-900">Security</h2>
                <p className="text-sm text-gray-500 mt-1">Enable or disable security validation for this tenant</p>
              </div>
              <div className="p-6">
                <label className="flex items-center justify-between cursor-pointer">
                  <div>
                    <p className="font-medium text-gray-900">Enable Security Validation</p>
                    <p className="text-sm text-gray-500">
                      When enabled, all API requests will be validated (certificate, rate limit, hardware whitelist)
                    </p>
                  </div>
                  <div className="relative">
                    <input
                      type="checkbox"
                      className="sr-only"
                      checked={securityEnabled}
                      onChange={(e) => setSecurityEnabled(e.target.checked)}
                    />
                    <div
                      onClick={() => setSecurityEnabled(!securityEnabled)}
                      className={`w-14 h-8 rounded-full transition-colors ${
                        securityEnabled ? "bg-indigo-600" : "bg-gray-300"
                      }`}
                    >
                      <div
                        className={`absolute top-1 left-1 w-6 h-6 bg-white rounded-full transition-transform ${
                          securityEnabled ? "transform translate-x-6" : ""
                        }`}
                      ></div>
                    </div>
                  </div>
                </label>
              </div>
            </div>

            {/* Rate Limiting */}
            <div className="bg-white rounded-lg shadow">
              <div className="p-6 border-b border-gray-200">
                <h2 className="text-xl font-semibold text-gray-900">Rate Limiting</h2>
                <p className="text-sm text-gray-500 mt-1">Configure DoS protection limits per device</p>
              </div>
              <div className="p-6">
                <label className="block">
                  <span className="text-gray-700 font-medium">Maximum Requests per Minute (per Device)</span>
                  <p className="text-sm text-gray-500 mb-2">
                    Default: 100. Normal enrollment generates ~2-3 requests/min. Lower values provide stricter protection.
                  </p>
                  <input
                    type="number"
                    min="1"
                    max="1000"
                    value={rateLimitRequestsPerMinute}
                    onChange={(e) => setRateLimitRequestsPerMinute(parseInt(e.target.value) || 100)}
                    className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                  />
                </label>
              </div>
            </div>

            {/* Hardware Whitelist */}
            <div className="bg-white rounded-lg shadow">
              <div className="p-6 border-b border-gray-200">
                <h2 className="text-xl font-semibold text-gray-900">Hardware Whitelist</h2>
                <p className="text-sm text-gray-500 mt-1">Restrict which device manufacturers and models can enroll</p>
              </div>
              <div className="p-6 space-y-6">
                {/* Manufacturer Whitelist */}
                <div>
                  <label className="block">
                    <span className="text-gray-700 font-medium">Allowed Manufacturers</span>
                    <p className="text-sm text-gray-500 mb-2">
                      Comma-separated list. Supports wildcards: <code className="bg-gray-100 px-1 rounded">*</code> = all,
                      <code className="bg-gray-100 px-1 rounded ml-1">Dell*</code> = starts with "Dell"
                    </p>
                    <input
                      type="text"
                      value={manufacturerWhitelist}
                      onChange={(e) => setManufacturerWhitelist(e.target.value)}
                      placeholder="Dell*,HP*,Lenovo*,Microsoft Corporation"
                      className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                    />
                  </label>
                </div>

                {/* Model Whitelist */}
                <div>
                  <label className="block">
                    <span className="text-gray-700 font-medium">Allowed Models</span>
                    <p className="text-sm text-gray-500 mb-2">
                      Comma-separated list. Use <code className="bg-gray-100 px-1 rounded">*</code> to allow all models.
                      Examples: <code className="bg-gray-100 px-1 rounded">Latitude*</code>, <code className="bg-gray-100 px-1 rounded">EliteBook*</code>
                    </p>
                    <input
                      type="text"
                      value={modelWhitelist}
                      onChange={(e) => setModelWhitelist(e.target.value)}
                      placeholder="*"
                      className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                    />
                  </label>
                </div>
              </div>
            </div>

            {/* Serial Number Validation (Prepared for future) */}
            <div className="bg-white rounded-lg shadow opacity-60">
              <div className="p-6 border-b border-gray-200">
                <div className="flex items-center justify-between">
                  <div>
                    <h2 className="text-xl font-semibold text-gray-900">Serial Number Validation</h2>
                    <p className="text-sm text-gray-500 mt-1">Validate devices against Intune Autopilot registration</p>
                  </div>
                  <span className="inline-flex items-center px-3 py-1 rounded-full text-xs font-medium bg-yellow-100 text-yellow-800">
                    Coming Soon
                  </span>
                </div>
              </div>
              <div className="p-6">
                <label className="flex items-center justify-between cursor-not-allowed">
                  <div>
                    <p className="font-medium text-gray-500">Enable Serial Number Validation</p>
                    <p className="text-sm text-gray-400">
                      Requires Graph API integration with Multi-Tenant support
                    </p>
                  </div>
                  <div className="relative">
                    <input
                      type="checkbox"
                      className="sr-only"
                      checked={validateSerialNumber}
                      disabled
                    />
                    <div className="w-14 h-8 bg-gray-200 rounded-full cursor-not-allowed">
                      <div className="absolute top-1 left-1 w-6 h-6 bg-white rounded-full"></div>
                    </div>
                  </div>
                </label>
              </div>
            </div>

            {/* Data Management Settings */}
            <div className="bg-white rounded-lg shadow">
              <div className="p-6 border-b border-gray-200">
                <h2 className="text-xl font-semibold text-gray-900">Data Management</h2>
                <p className="text-sm text-gray-500 mt-1">Configure data retention and session timeout policies</p>
              </div>
              <div className="p-6 space-y-6">
                {/* Data Retention Days */}
                <div>
                  <label className="block">
                    <span className="text-gray-700 font-medium">Data Retention Period (Days)</span>
                    <p className="text-sm text-gray-500 mb-2">
                      Sessions and events older than this will be automatically deleted by the daily maintenance job. Default: 90 days.
                    </p>
                    <input
                      type="number"
                      min="7"
                      max="3650"
                      value={dataRetentionDays}
                      onChange={(e) => setDataRetentionDays(parseInt(e.target.value) || 90)}
                      className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                    />
                    <p className="text-xs text-gray-400 mt-1">Minimum: 7 days, Maximum: 3650 days (10 years)</p>
                  </label>
                </div>

                {/* Session Timeout Hours */}
                <div>
                  <label className="block">
                    <span className="text-gray-700 font-medium">Session Timeout (Hours)</span>
                    <p className="text-sm text-gray-500 mb-2">
                      Sessions in "InProgress" status longer than this will be marked as "Failed - Timed Out".
                      This prevents stalled sessions from running indefinitely and skewing statistics.
                      <br />
                      <strong>Tip:</strong> Use the same value as your ESP (Enrollment Status Page) timeout for consistency.
                    </p>
                    <input
                      type="number"
                      min="1"
                      max="48"
                      value={sessionTimeoutHours}
                      onChange={(e) => setSessionTimeoutHours(parseInt(e.target.value) || 5)}
                      className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500 transition-colors"
                    />
                    <p className="text-xs text-gray-400 mt-1">Default: 5 hours (ESP default). Minimum: 1 hour, Maximum: 48 hours</p>
                  </label>
                </div>
              </div>
            </div>

            {/* Galactic Admin Functions - Only visible in Galactic Mode */}
            {galacticAdminMode && (
              <div className="bg-gradient-to-br from-purple-50 to-violet-50 border-2 border-purple-300 rounded-lg shadow-lg">
                <div className="p-6 border-b border-purple-200 bg-gradient-to-r from-purple-100 to-violet-100">
                  <div className="flex items-center space-x-2">
                    <svg className="w-6 h-6 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                    </svg>
                    <div>
                      <h2 className="text-xl font-semibold text-purple-900">Galactic Admin Functions</h2>
                      <p className="text-sm text-purple-600 mt-1">Cross-tenant administrative operations</p>
                    </div>
                  </div>
                </div>
                <div className="p-6">
                  <div className="flex items-start space-x-4">
                    <div className="flex-1">
                      <h3 className="text-lg font-medium text-gray-900 mb-2">Manual Maintenance Trigger</h3>
                      <p className="text-sm text-gray-600 mb-4">
                        Manually trigger the daily maintenance job which includes:
                      </p>
                      <ul className="text-sm text-gray-600 space-y-1 mb-4 ml-4">
                        <li className="flex items-start">
                          <span className="text-purple-500 mr-2">•</span>
                          <span>Mark stalled sessions as timed out</span>
                        </li>
                        <li className="flex items-start">
                          <span className="text-purple-500 mr-2">•</span>
                          <span>Aggregate yesterday's metrics into historical snapshots</span>
                        </li>
                        <li className="flex items-start">
                          <span className="text-purple-500 mr-2">•</span>
                          <span>Clean up old data based on retention policies</span>
                        </li>
                      </ul>
                      <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3 mb-4">
                        <div className="flex items-start space-x-2">
                          <svg className="w-5 h-5 text-yellow-600 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                          </svg>
                          <p className="text-sm text-yellow-800">
                            <strong>Warning:</strong> This operation runs across all tenants and may take several minutes to complete.
                            Use this only for testing or when immediate cleanup is needed.
                          </p>
                        </div>
                      </div>
                    </div>
                    <div className="flex-shrink-0">
                      <button
                        onClick={handleTriggerMaintenance}
                        disabled={triggeringMaintenance}
                        className="px-6 py-3 bg-gradient-to-r from-purple-600 to-violet-600 text-white rounded-lg hover:from-purple-700 hover:to-violet-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
                      >
                        {triggeringMaintenance ? (
                          <>
                            <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white"></div>
                            <span>Running...</span>
                          </>
                        ) : (
                          <>
                            <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z" />
                              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                            </svg>
                            <span>Run Now</span>
                          </>
                        )}
                      </button>
                    </div>
                  </div>
                </div>
              </div>
            )}

            {/* Configuration Info */}
            {config && (
              <div className="bg-blue-50 border border-blue-200 rounded-lg p-4">
                <div className="flex items-start space-x-3">
                  <svg className="w-5 h-5 text-blue-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <div className="text-sm text-blue-800">
                    <p className="font-medium">Configuration Info</p>
                    <p className="mt-1">Last updated: {new Date(config.lastUpdated).toLocaleString()}</p>
                    <p>Updated by: {config.updatedBy}</p>
                  </div>
                </div>
              </div>
            )}

            {/* Action Buttons */}
            <div className="flex items-center justify-end space-x-4 pt-4">
              <button
                onClick={handleReset}
                disabled={saving}
                className="px-6 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                Reset
              </button>
              <button
                onClick={handleSave}
                disabled={saving}
                className="px-6 py-2 bg-indigo-600 text-white rounded-md hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
              >
                {saving ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Saving...</span>
                  </>
                ) : (
                  <span>Save Configuration</span>
                )}
              </button>
            </div>
          </div>
        )}
      </main>
      </div>
    </ProtectedRoute>
  );
}
