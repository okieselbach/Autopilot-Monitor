"use client";

import { TenantAdmin } from "../page";

interface AdminManagementSectionProps {
  admins: TenantAdmin[];
  loadingAdmins: boolean;
  newAdminEmail: string;
  setNewAdminEmail: (value: string) => void;
  addingAdmin: boolean;
  removingAdmin: string | null;
  togglingAdmin: string | null;
  adminSearchQuery: string;
  setAdminSearchQuery: (value: string) => void;
  currentAdminPage: number;
  setCurrentAdminPage: (value: number | ((prev: number) => number)) => void;
  user: { upn?: string } | null;
  onAddAdmin: () => void;
  onRemoveAdmin: (upn: string) => void;
  onToggleAdmin: (upn: string, isEnabled: boolean) => void;
}

export default function AdminManagementSection({
  admins,
  loadingAdmins,
  newAdminEmail,
  setNewAdminEmail,
  addingAdmin,
  removingAdmin,
  togglingAdmin,
  adminSearchQuery,
  setAdminSearchQuery,
  currentAdminPage,
  setCurrentAdminPage,
  user,
  onAddAdmin,
  onRemoveAdmin,
  onToggleAdmin,
}: AdminManagementSectionProps) {
  return (
    <div className="bg-white rounded-lg shadow">
      <div className="p-6 border-b border-gray-200 bg-gradient-to-r from-purple-50 to-indigo-50">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z" />
          </svg>
          <div>
            <h2 className="text-xl font-semibold text-gray-900">Admin Users Management</h2>
            <p className="text-sm text-gray-500 mt-1">Manage who has admin access to this tenant</p>
          </div>
        </div>
      </div>
      <div className="p-6 space-y-4">
        <div className="bg-blue-50 border border-blue-200 rounded-lg p-4">
          <div className="flex items-start space-x-3">
            <svg className="w-5 h-5 text-blue-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <div className="text-sm text-blue-800">
              <p className="font-medium">About Admin Users</p>
              <p className="mt-1">
                Admin users have full access to tenant configuration, all sessions, diagnostics, and settings.
                Non-admin users only have access to the simplified device tracking page.
              </p>
              <p className="mt-2">
                <strong>Your email:</strong> {user?.upn}
              </p>
            </div>
          </div>
        </div>

        {/* Current Admins List */}
        <div>
          <label className="block mb-2">
            <span className="text-gray-700 font-medium">Current Admin Users</span>
            {loadingAdmins && (
              <span className="ml-2 text-sm text-gray-500">(Loading...)</span>
            )}
          </label>

          {/* Search Field */}
          <div className="mb-3">
            <div className="relative">
              <input
                type="text"
                name="admin-search-field"
                value={adminSearchQuery}
                onChange={(e) => {
                  setAdminSearchQuery(e.target.value);
                  setCurrentAdminPage(0);
                }}
                placeholder="Search by email..."
                autoComplete="off"
                className="w-full px-4 py-2 pl-10 pr-10 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
              />
              <svg className="absolute left-3 top-2.5 w-5 h-5 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
              </svg>
              {adminSearchQuery && (
                <button
                  onClick={() => {
                    setAdminSearchQuery("");
                    setCurrentAdminPage(0);
                  }}
                  className="absolute right-3 top-2.5 text-gray-400 hover:text-gray-600 transition-colors"
                  title="Clear search"
                >
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              )}
            </div>
          </div>

          {admins.length === 0 && !loadingAdmins ? (
            <div className="text-sm text-gray-500 italic">No admins found</div>
          ) : (
            <>
              {/* Filtered and Paginated Admin List */}
              {(() => {
                const filteredAdmins = admins.filter(admin =>
                  admin.upn.toLowerCase().includes(adminSearchQuery.toLowerCase())
                );

                if (filteredAdmins.length === 0) {
                  return (
                    <div className="text-sm text-gray-500 italic p-4 text-center bg-gray-50 rounded-lg">
                      No admins match your search
                    </div>
                  );
                }

                const adminsPerPage = 3;
                const totalAdminPages = Math.ceil(filteredAdmins.length / adminsPerPage);
                const startAdminIndex = currentAdminPage * adminsPerPage;
                const endAdminIndex = startAdminIndex + adminsPerPage;
                const paginatedAdmins = filteredAdmins.slice(startAdminIndex, endAdminIndex);

                return (
                  <>
                    <div className="space-y-2">
                      {paginatedAdmins.map((admin) => (
                        <div
                          key={admin.upn}
                          className={`flex items-center justify-between p-3 border rounded-lg ${
                            admin.isEnabled
                              ? "bg-gray-50 border-gray-200"
                              : "bg-gray-100 border-gray-300"
                          }`}
                        >
                          <div className="flex-1 min-w-0">
                            <div className="flex items-center space-x-2">
                              <div className="font-medium text-gray-900 truncate">{admin.upn}</div>
                              {!admin.isEnabled && (
                                <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-200 text-gray-700">
                                  Disabled
                                </span>
                              )}
                            </div>
                            <div className="text-xs text-gray-500 mt-1">
                              Added {new Date(admin.addedDate).toLocaleDateString()} by {admin.addedBy}
                            </div>
                          </div>
                          <div className="flex items-center space-x-2 ml-4">
                            {admin.upn.toLowerCase() === user?.upn?.toLowerCase() ? (
                              <span className="text-sm text-blue-600 font-medium">(You)</span>
                            ) : (
                              <>
                                <button
                                  onClick={() => onToggleAdmin(admin.upn, admin.isEnabled)}
                                  disabled={togglingAdmin === admin.upn}
                                  className={`px-3 py-1 text-sm text-white rounded transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                                    admin.isEnabled
                                      ? "bg-yellow-600 hover:bg-yellow-700"
                                      : "bg-green-600 hover:bg-green-700"
                                  }`}
                                >
                                  {togglingAdmin === admin.upn
                                    ? "..."
                                    : admin.isEnabled
                                    ? "Disable"
                                    : "Enable"}
                                </button>
                                <button
                                  onClick={() => onRemoveAdmin(admin.upn)}
                                  disabled={removingAdmin === admin.upn}
                                  className="px-3 py-1 text-sm bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                                >
                                  {removingAdmin === admin.upn ? "Removing..." : "Remove"}
                                </button>
                              </>
                            )}
                          </div>
                        </div>
                      ))}
                    </div>

                    {/* Pagination Controls */}
                    {totalAdminPages > 1 && (
                      <div className="flex items-center justify-between mt-4 pt-4 border-t border-gray-200">
                        <button
                          onClick={() => setCurrentAdminPage(prev => Math.max(0, prev - 1))}
                          disabled={currentAdminPage === 0}
                          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Previous
                        </button>
                        <span className="text-sm text-gray-600">
                          Page {currentAdminPage + 1} of {totalAdminPages} ({filteredAdmins.length} admin{filteredAdmins.length !== 1 ? 's' : ''})
                        </span>
                        <button
                          onClick={() => setCurrentAdminPage(prev => Math.min(totalAdminPages - 1, prev + 1))}
                          disabled={currentAdminPage >= totalAdminPages - 1}
                          className="px-4 py-2 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                        >
                          Next
                        </button>
                      </div>
                    )}
                  </>
                );
              })()}
            </>
          )}
        </div>

        {/* Add New Admin */}
        <div>
          <label className="block mb-2">
            <span className="text-gray-700 font-medium">Add New Admin</span>
            <p className="text-sm text-gray-500 mb-2">
              Enter the user email (UPN) to grant admin access.
              Example: <code className="bg-gray-100 px-1 rounded">newadmin@company.com</code>
            </p>
            <div className="flex space-x-2">
              <input
                type="email"
                name="new-admin-email"
                id="add-new-admin-email-input"
                value={newAdminEmail}
                onChange={(e) => setNewAdminEmail(e.target.value)}
                placeholder="newadmin@tenant.com"
                autoComplete="off"
                className="flex-1 px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    e.preventDefault();
                    onAddAdmin();
                  }
                }}
              />
              <button
                onClick={onAddAdmin}
                disabled={addingAdmin || !newAdminEmail.trim()}
                className="px-6 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center space-x-2"
              >
                {addingAdmin ? (
                  <>
                    <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                    <span>Adding...</span>
                  </>
                ) : (
                  <>
                    <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
                    </svg>
                    <span>Add</span>
                  </>
                )}
              </button>
            </div>
          </label>
        </div>

        <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3">
          <div className="flex items-start space-x-2">
            <svg className="w-5 h-5 text-yellow-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <p className="text-sm text-yellow-800">
              <strong>Important:</strong> Make sure to include your own email in the list to maintain admin access!
              The first user to log in was automatically made an admin.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
