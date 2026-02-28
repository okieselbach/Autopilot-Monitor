"use client";

interface OffboardingSectionProps {
  showOffboardDialog: boolean;
  setShowOffboardDialog: (value: boolean) => void;
  offboardConfirmText: string;
  setOffboardConfirmText: (value: string) => void;
  offboarding: boolean;
  offboardError: string | null;
  setOffboardError: (value: string | null) => void;
  onOffboard: () => void;
}

export default function OffboardingSection({
  showOffboardDialog,
  setShowOffboardDialog,
  offboardConfirmText,
  setOffboardConfirmText,
  offboarding,
  offboardError,
  setOffboardError,
  onOffboard,
}: OffboardingSectionProps) {
  return (
    <>
      {/* Danger Zone: Tenant Offboarding */}
      <div className="bg-white rounded-lg shadow border-2 border-red-200">
        <div className="p-6 border-b border-red-100 bg-red-50">
          <div className="flex items-center space-x-2">
            <svg className="w-6 h-6 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <div>
              <h2 className="text-xl font-semibold text-red-900">Danger Zone</h2>
              <p className="text-sm text-red-600 mt-1">Irreversible and destructive actions</p>
            </div>
          </div>
        </div>
        <div className="p-6">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-base font-semibold text-gray-900">Offboard this Tenant</h3>
              <p className="text-sm text-gray-500 mt-1">
                Permanently deletes <strong>all data</strong> for this tenant – sessions, events, rules, audit logs, configuration, and all admin accounts including yours. This cannot be undone.
              </p>
            </div>
            <button
              onClick={() => { setShowOffboardDialog(true); setOffboardConfirmText(""); setOffboardError(null); }}
              className="ml-6 flex-shrink-0 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 transition-colors text-sm font-medium"
            >
              Offboard Tenant
            </button>
          </div>
        </div>
      </div>

      {/* Offboard Confirmation Dialog */}
      {showOffboardDialog && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4">
          <div className="bg-white rounded-lg shadow-xl max-w-md w-full p-6">
            <div className="flex items-center space-x-3 mb-4">
              <div className="w-12 h-12 bg-red-100 rounded-full flex items-center justify-center flex-shrink-0">
                <svg className="w-6 h-6 text-red-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                </svg>
              </div>
              <div>
                <h3 className="text-lg font-bold text-gray-900">Tenant Offboarding</h3>
                <p className="text-sm text-red-600 font-medium">This action is permanent and cannot be undone.</p>
              </div>
            </div>

            <div className="bg-red-50 border border-red-200 rounded-lg p-4 mb-4 text-sm text-red-800 space-y-1">
              <p className="font-semibold">The following will be permanently deleted:</p>
              <ul className="list-disc list-inside mt-2 space-y-1">
                <li>All enrollment sessions and events</li>
                <li>All gather and analyze rules</li>
                <li>Audit logs and usage metrics</li>
                <li>Tenant configuration</li>
                <li>All admin accounts (including yours)</li>
              </ul>
              <p className="mt-3 font-semibold">After offboarding you will be signed out and lose all access.</p>
            </div>

            <div className="mb-4">
              <label className="block text-sm font-medium text-gray-700 mb-1">
                Type <span className="font-bold text-red-600">OFFBOARD</span> to confirm
              </label>
              <input
                type="text"
                value={offboardConfirmText}
                onChange={(e) => setOffboardConfirmText(e.target.value)}
                placeholder="OFFBOARD"
                className="w-full px-3 py-2 border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-red-500 focus:border-red-500"
                autoComplete="off"
              />
            </div>

            {offboardError && (
              <div className="mb-4 bg-red-50 border border-red-200 rounded p-3 text-sm text-red-800">
                {offboardError}
              </div>
            )}

            <div className="flex space-x-3">
              <button
                onClick={() => { setShowOffboardDialog(false); setOffboardConfirmText(""); setOffboardError(null); }}
                disabled={offboarding}
                className="flex-1 px-4 py-2 border border-gray-300 rounded-md text-gray-700 bg-white hover:bg-gray-50 disabled:opacity-50 transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={onOffboard}
                disabled={offboarding || offboardConfirmText !== "OFFBOARD"}
                className="flex-1 px-4 py-2 bg-red-600 text-white rounded-md hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center justify-center space-x-2"
              >
                {offboarding ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Offboarding...</span>
                  </>
                ) : (
                  <span>Permanently Delete All Data</span>
                )}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
