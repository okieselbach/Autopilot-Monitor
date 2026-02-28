"use client";

interface AdminConfiguration {
  partitionKey: string;
  rowKey: string;
  lastUpdated: string;
  updatedBy: string;
  globalRateLimitRequestsPerMinute: number;
  platformStatsBlobSasUrl?: string;
  maxCollectorDurationHours?: number;
  maxSessionWindowHours?: number;
  maintenanceBlockDurationHours?: number;
  diagnosticsGlobalLogPathsJson?: string;
  customSettings?: string;
}

interface AdminConfigSettingsSectionProps {
  loadingConfig: boolean;
  savingConfig: boolean;
  adminConfig: AdminConfiguration | null;
  globalRateLimit: number;
  setGlobalRateLimit: (value: number) => void;
  platformStatsBlobSasUrl: string;
  setPlatformStatsBlobSasUrl: (value: string) => void;
  maxCollectorDurationHours: number;
  setMaxCollectorDurationHours: (value: number) => void;
  onSave: () => Promise<void>;
  onReset: () => void;
}

export function AdminConfigSettingsSection({
  loadingConfig,
  savingConfig,
  adminConfig,
  globalRateLimit,
  setGlobalRateLimit,
  platformStatsBlobSasUrl,
  setPlatformStatsBlobSasUrl,
  maxCollectorDurationHours,
  setMaxCollectorDurationHours,
  onSave,
  onReset,
}: AdminConfigSettingsSectionProps) {
  return (
    <div className="bg-gradient-to-br from-indigo-50 to-blue-50 dark:from-gray-800 dark:to-gray-800 border-2 border-indigo-300 dark:border-indigo-700 rounded-lg shadow-lg">
      <div className="p-6 border-b border-indigo-200 dark:border-indigo-700 bg-gradient-to-r from-indigo-100 to-blue-100 dark:from-indigo-900/40 dark:to-blue-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-indigo-600 dark:text-indigo-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 15v2m-6 4h12a2 2 0 002-2v-6a2 2 0 00-2-2H6a2 2 0 00-2 2v6a2 2 0 002 2zm10-10V7a4 4 0 00-8 0v4h8z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-indigo-900 dark:text-indigo-100">Global Settings</h2>
            <p className="text-sm text-indigo-600 dark:text-indigo-300 mt-1">Configure global settings for all tenants</p>
          </div>
        </div>
      </div>
      <div className="p-6">
        {loadingConfig ? (
          <div className="text-center py-8">
            <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-indigo-600 dark:border-indigo-400 mx-auto"></div>
            <p className="mt-3 text-indigo-800 dark:text-indigo-200 text-sm">Loading configuration...</p>
          </div>
        ) : (
          <div className="space-y-4">
            <div>
              <label className="block">
                <span className="text-indigo-900 dark:text-indigo-100 font-medium">Global Rate Limit (Requests per Minute per Device)</span>
                <p className="text-sm text-indigo-800 dark:text-gray-300 mb-2">
                  Configure default DoS protection limits for all tenants. Normal enrollment generates ~10-30 requests/min.
                  <br />
                  <strong className="text-indigo-900 dark:text-indigo-100">Note:</strong> Tenants cannot change this value. Only Galactic Admins can override per tenant in tenant management section.
                </p>
                <input
                  type="number"
                  min="1"
                  max="1000"
                  value={globalRateLimit}
                  onChange={(e) => setGlobalRateLimit(parseInt(e.target.value) || 100)}
                  className="mt-1 block w-full px-4 py-2 border border-indigo-300 dark:border-indigo-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                />
              </label>
            </div>

            <div>
              <label className="block">
                <span className="text-indigo-900 dark:text-indigo-100 font-medium">Platform Stats Blob Container SAS URL</span>
                <p className="text-sm text-indigo-800 dark:text-gray-300 mb-2">
                  Maintenance publishes two files into this container:
                  <code className="ml-1 mr-1 text-xs bg-indigo-100 dark:bg-indigo-900 dark:text-indigo-200 px-1 rounded">platform-stats.json</code>
                  and
                  <code className="ml-1 text-xs bg-indigo-100 dark:bg-indigo-900 dark:text-indigo-200 px-1 rounded">platform-stats.YYYY-MM-DD.json</code>.
                  The upload is best-effort and does not fail the maintenance run.
                </p>
                <input
                  type="url"
                  value={platformStatsBlobSasUrl}
                  onChange={(e) => setPlatformStatsBlobSasUrl(e.target.value)}
                  placeholder="https://storageaccount.blob.core.windows.net/publicstats?sv=...&sig=..."
                  className="mt-1 block w-full px-4 py-2 border border-indigo-300 dark:border-indigo-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors font-mono text-sm"
                />
              </label>
            </div>

            <div>
              <label className="block">
                <span className="text-indigo-900 dark:text-indigo-100 font-medium">Max Collector Duration (Hours)</span>
                <p className="text-sm text-indigo-800 dark:text-gray-300 mb-2">
                  Interval-based collectors (e.g. Performance Collector) stop automatically after this many hours per session.
                  Prevents excessive backend traffic when a device is stuck during enrollment.
                  Set to <strong>0</strong> to disable the limit (collectors run indefinitely).
                </p>
                <input
                  type="number"
                  min="0"
                  max="168"
                  value={maxCollectorDurationHours}
                  onChange={(e) => setMaxCollectorDurationHours(parseInt(e.target.value) || 0)}
                  className="mt-1 block w-full px-4 py-2 border border-indigo-300 dark:border-indigo-600 rounded-lg bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:border-indigo-500 transition-colors"
                />
              </label>
            </div>

            {adminConfig && (
              <div className="bg-blue-50 dark:bg-gray-700 border border-blue-200 dark:border-indigo-600 rounded-lg p-3">
                <div className="flex items-start space-x-2">
                  <svg className="w-5 h-5 text-blue-600 dark:text-indigo-400 mt-0.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                  <div className="text-sm text-blue-800 dark:text-gray-200">
                    <p className="font-medium">Configuration Info</p>
                    <p className="mt-1">Last updated: {new Date(adminConfig.lastUpdated).toLocaleString()}</p>
                    <p>Updated by: {adminConfig.updatedBy}</p>
                  </div>
                </div>
              </div>
            )}

            <div className="flex items-center justify-end space-x-3 pt-2">
              <button
                onClick={onReset}
                disabled={savingConfig}
                className="px-5 py-2 border border-indigo-300 dark:border-indigo-600 rounded-md text-indigo-800 dark:text-indigo-200 bg-white dark:bg-gray-700 hover:bg-indigo-50 dark:hover:bg-gray-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                Reset
              </button>
              <button
                onClick={onSave}
                disabled={savingConfig}
                className="px-5 py-2 bg-gradient-to-r from-indigo-600 to-blue-600 text-white rounded-md hover:from-indigo-700 hover:to-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition-all shadow-md hover:shadow-lg flex items-center space-x-2"
              >
                {savingConfig ? (
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
      </div>
    </div>
  );
}
