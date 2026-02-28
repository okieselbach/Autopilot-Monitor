"use client";

import { DiagnosticsLogPath } from "../page";

/** Parses the expiry date from the `se=` parameter of a SAS URL. Returns null if not found. */
export function parseSasExpiry(sasUrl: string): Date | null {
  try {
    const qIndex = sasUrl.indexOf('?');
    if (qIndex < 0) return null;
    const params = new URLSearchParams(sasUrl.substring(qIndex + 1));
    const se = params.get('se');
    if (!se) return null;
    const d = new Date(se);
    return isNaN(d.getTime()) ? null : d;
  } catch {
    return null;
  }
}

interface DiagnosticsSectionProps {
  diagnosticsBlobSasUrl: string;
  setDiagnosticsBlobSasUrl: (value: string) => void;
  diagnosticsUploadMode: string;
  setDiagnosticsUploadMode: (value: string) => void;
  tenantDiagPaths: DiagnosticsLogPath[];
  setTenantDiagPaths: (value: DiagnosticsLogPath[]) => void;
  globalDiagPaths: DiagnosticsLogPath[];
  newDiagPath: string;
  setNewDiagPath: (value: string) => void;
  newDiagDesc: string;
  setNewDiagDesc: (value: string) => void;
}

export default function DiagnosticsSection({
  diagnosticsBlobSasUrl,
  setDiagnosticsBlobSasUrl,
  diagnosticsUploadMode,
  setDiagnosticsUploadMode,
  tenantDiagPaths,
  setTenantDiagPaths,
  globalDiagPaths,
  newDiagPath,
  setNewDiagPath,
  newDiagDesc,
  setNewDiagDesc,
}: DiagnosticsSectionProps) {
  // Compute SAS expiry directly from the current URL value so feedback is instant
  const diagnosticsSasExpiry = parseSasExpiry(diagnosticsBlobSasUrl);

  return (
    <div id="diagnostics" className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-amber-50 to-orange-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 8h14M5 8a2 2 0 110-4h14a2 2 0 110 4M5 8v10a2 2 0 002 2h10a2 2 0 002-2V8m-9 4h4" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Diagnostics Package</h2>
            <p className="text-sm text-gray-500 mt-1">Upload diagnostic files as a ZIP package to your Azure Blob Storage after enrollment.</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">

        {/* Info */}
        <div className="bg-amber-50 border border-amber-200 rounded-lg p-3">
          <p className="text-sm text-amber-900">
            The agent requests a short-lived upload URL from the backend <strong>just before uploading</strong>. Your SAS URL is stored securely in the backend and never sent to devices in the agent configuration.
          </p>
        </div>

        {/* Blob Storage SAS URL */}
        <div>
          <label className="block">
            <span className="text-gray-700 font-medium">Blob Storage Container SAS URL</span>
            <p className="text-sm text-gray-500 mb-2">
              Create an Azure Blob Storage container and generate a Container-level SAS URL with Read, Write and Create permissions.
            </p>
            <div className="flex items-center gap-2">
              <input
                type="url"
                value={diagnosticsBlobSasUrl}
                onChange={(e) => setDiagnosticsBlobSasUrl(e.target.value)}
                placeholder="https://storageaccount.blob.core.windows.net/diagnostics?sv=...&sig=..."
                className="mt-1 block w-full px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500 transition-colors font-mono text-sm"
              />
              {diagnosticsBlobSasUrl && diagnosticsUploadMode !== "Off" && (
                <span className="mt-1 inline-flex items-center px-2.5 py-1 rounded-full text-xs font-medium bg-green-100 text-green-800 whitespace-nowrap">
                  Active
                </span>
              )}
            </div>
          </label>

          {/* SAS URL expiry indicator */}
          {diagnosticsBlobSasUrl && diagnosticsSasExpiry && (() => {
            const now = new Date();
            const daysRemaining = Math.ceil((diagnosticsSasExpiry.getTime() - now.getTime()) / (1000 * 60 * 60 * 24));
            const isExpired = daysRemaining <= 0;
            const isWarning = daysRemaining > 0 && daysRemaining <= 7;
            return (
              <div className={`mt-2 flex items-center gap-1.5 text-xs ${isExpired ? 'text-red-600' : isWarning ? 'text-amber-600' : 'text-green-600'}`}>
                {isExpired ? (
                  <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4m0 4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                ) : isWarning ? (
                  <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
                  </svg>
                ) : (
                  <svg className="w-3.5 h-3.5 flex-shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                  </svg>
                )}
                <span>
                  {isExpired
                    ? `Expired on ${diagnosticsSasExpiry.toLocaleDateString()}`
                    : `Expires on ${diagnosticsSasExpiry.toLocaleDateString()}${isWarning ? ` (${daysRemaining} day${daysRemaining === 1 ? '' : 's'} remaining)` : ''}`}
                </span>
              </div>
            );
          })()}
        </div>

        {/* Upload Mode */}
        <div className={`p-4 rounded-lg border transition-colors ${diagnosticsBlobSasUrl ? 'border-gray-200 hover:border-amber-200' : 'border-gray-100 opacity-50'}`}>
          <div className="flex items-center justify-between">
            <div>
              <p className="font-medium text-gray-900">Upload Mode</p>
              <p className="text-sm text-gray-500">Choose when diagnostics packages are uploaded</p>
            </div>
            <select
              value={diagnosticsUploadMode}
              onChange={(e) => setDiagnosticsUploadMode(e.target.value)}
              disabled={!diagnosticsBlobSasUrl}
              className="px-3 py-2 border border-gray-300 rounded-lg text-gray-900 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500 disabled:opacity-50 disabled:cursor-not-allowed text-sm"
            >
              <option value="Off">Off</option>
              <option value="Always">Always</option>
              <option value="OnFailure">On Failure Only</option>
            </select>
          </div>
        </div>

        {/* Additional Log Paths */}
        <div className="p-4 rounded-lg border border-gray-200">
          <p className="font-medium text-gray-900 mb-1">Additional Log Paths</p>
          <p className="text-sm text-gray-500 mb-3">
            Extra log files or wildcards included in the diagnostics ZIP. Global paths (set by your platform admin) are always included and shown below as read-only.
          </p>

          {/* Global paths (read-only) */}
          {globalDiagPaths.length > 0 && (
            <div className="mb-3">
              <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-2">Global (platform-wide)</p>
              <div className="space-y-1.5">
                {globalDiagPaths.map((entry, idx) => (
                  <div key={idx} className="flex items-start justify-between bg-gray-100 border border-gray-300 rounded-lg px-3 py-2">
                    <div className="min-w-0 flex-1">
                      <p className="font-mono text-xs text-gray-700 break-all">{entry.path}</p>
                      {entry.description && <p className="text-xs text-gray-500 mt-0.5">{entry.description}</p>}
                    </div>
                    <span className="ml-2 flex-shrink-0 text-gray-400 bg-gray-200 rounded-full px-1.5 py-0.5 text-xs">global</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Tenant paths */}
          {tenantDiagPaths.length > 0 && (
            <div className="mb-3">
              <p className="text-xs font-medium text-gray-400 uppercase tracking-wide mb-2">Your paths</p>
              <div className="space-y-1.5">
                {tenantDiagPaths.map((entry, idx) => (
                  <div key={idx} className="flex items-start justify-between bg-amber-50 border border-amber-200 rounded-lg px-3 py-2">
                    <div className="min-w-0 flex-1">
                      <p className="font-mono text-xs text-amber-900 break-all">{entry.path}</p>
                      {entry.description && <p className="text-xs text-amber-600 mt-0.5">{entry.description}</p>}
                    </div>
                    <button
                      onClick={() => setTenantDiagPaths(tenantDiagPaths.filter((_, i) => i !== idx))}
                      className="ml-2 flex-shrink-0 text-amber-400 hover:text-red-600 transition-colors"
                      title="Remove"
                    >
                      <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                      </svg>
                    </button>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Add new tenant path */}
          <div className="flex flex-col sm:flex-row gap-2 mt-2">
            <input
              type="text"
              placeholder="Path or wildcard (e.g. C:\Windows\Panther\*.log)"
              value={newDiagPath}
              onChange={(e) => setNewDiagPath(e.target.value)}
              className="flex-1 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500 font-mono"
            />
            <input
              type="text"
              placeholder="Description (optional)"
              value={newDiagDesc}
              onChange={(e) => setNewDiagDesc(e.target.value)}
              className="flex-1 px-3 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 bg-white placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-amber-500 focus:border-amber-500"
            />
            <button
              onClick={() => {
                const p = newDiagPath.trim();
                if (!p) return;
                setTenantDiagPaths([...tenantDiagPaths, { path: p, description: newDiagDesc.trim(), isBuiltIn: false }]);
                setNewDiagPath("");
                setNewDiagDesc("");
              }}
              disabled={!newDiagPath.trim()}
              className="px-4 py-1.5 bg-amber-600 text-white rounded-lg text-sm font-medium hover:bg-amber-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
            >
              Add
            </button>
          </div>
          <p className="text-xs text-gray-400 mt-2">
            Paths are validated on the agent against an allowlist of safe prefixes. Wildcards are only allowed in the last segment.
          </p>
        </div>

      </div>
    </div>
  );
}
