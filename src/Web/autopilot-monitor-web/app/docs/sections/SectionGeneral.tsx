export function SectionGeneral() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M12 2a10 10 0 100 20 10 10 0 000-20z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">General</h2>
      </div>

      <p className="text-gray-700 leading-relaxed mb-8">
        This section covers general concepts and UI features of the Autopilot Monitor portal that apply
        across different pages.
      </p>

      {/* Admin Mode */}
      <div className="mb-8">
        <h3 className="text-lg font-semibold text-gray-900 mb-3 flex items-center gap-2">
          <svg className="w-5 h-5 text-amber-500 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
          </svg>
          Admin Mode
        </h3>

        <p className="text-gray-700 leading-relaxed mb-4">
          Admin Mode is a safety toggle that gates access to destructive operations in the portal. It is only
          available to users with the <strong>Admin</strong> role and must be explicitly enabled before any
          destructive action can be performed. This prevents accidental deletions during normal day-to-day use.
        </p>

        <div className="bg-amber-50 border border-amber-200 rounded-lg px-4 py-3 mb-4 text-sm text-amber-900">
          <div className="flex items-start gap-2">
            <svg className="mt-0.5 h-5 w-5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M12 2a10 10 0 100 20 10 10 0 000-20z" />
            </svg>
            <span>
              Admin Mode is <strong>not persistent</strong> — it is stored in the browser&apos;s local storage and
              resets when you clear your browser data. It is recommended to keep Admin Mode disabled when not actively
              performing administrative actions.
            </span>
          </div>
        </div>

        <h4 className="text-sm font-semibold text-gray-800 mb-2">How to enable</h4>
        <p className="text-gray-700 text-sm leading-relaxed mb-4">
          Click the <strong>gear icon</strong> in the top navigation bar to open the settings menu. Under the{" "}
          <strong>Administration</strong> section, toggle <strong>Admin Mode</strong> on. The toggle turns amber
          when active and shows an <strong>ON</strong> label.
        </p>

        <h4 className="text-sm font-semibold text-gray-800 mb-2">What Admin Mode unlocks</h4>
        <div className="overflow-x-auto">
          <table className="w-full text-sm border border-gray-200 rounded-lg overflow-hidden">
            <thead>
              <tr className="bg-gray-50">
                <th className="text-left px-4 py-2.5 font-semibold text-gray-700 border-b border-gray-200">Action</th>
                <th className="text-left px-4 py-2.5 font-semibold text-gray-700 border-b border-gray-200">Location</th>
                <th className="text-left px-4 py-2.5 font-semibold text-gray-700 border-b border-gray-200">Description</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              <tr>
                <td className="px-4 py-2.5 font-medium text-gray-900">Delete Session</td>
                <td className="px-4 py-2.5 text-gray-600">Dashboard &rarr; Actions column</td>
                <td className="px-4 py-2.5 text-gray-600">
                  Permanently deletes an enrollment session and all associated event data. An additional{" "}
                  <strong>Actions</strong> column appears in the session table when Admin Mode is active.
                </td>
              </tr>
              <tr>
                <td className="px-4 py-2.5 font-medium text-gray-900">Mark as Failed</td>
                <td className="px-4 py-2.5 text-gray-600">Session Detail page</td>
                <td className="px-4 py-2.5 text-gray-600">
                  Manually marks a session that is currently <em>In Progress</em> or <em>Pending</em> as failed.
                  Useful when a session is stuck and will never complete on its own.
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      {/* Session Lifecycle */}
      <div className="mb-8">
        <h3 className="text-lg font-semibold text-gray-900 mb-3 flex items-center gap-2">
          <svg className="w-5 h-5 text-blue-500 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
          </svg>
          Session Statuses
        </h3>

        <p className="text-gray-700 leading-relaxed mb-4">
          Every enrollment session passes through a series of statuses that reflect the current state
          of the Autopilot enrollment process on the device.
        </p>

        <div className="overflow-x-auto">
          <table className="w-full text-sm border border-gray-200 rounded-lg overflow-hidden">
            <thead>
              <tr className="bg-gray-50">
                <th className="text-left px-4 py-2.5 font-semibold text-gray-700 border-b border-gray-200">Status</th>
                <th className="text-left px-4 py-2.5 font-semibold text-gray-700 border-b border-gray-200">Meaning</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              <tr>
                <td className="px-4 py-2.5"><span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">In Progress</span></td>
                <td className="px-4 py-2.5 text-gray-600">Enrollment events are actively being received from the device. The agent is monitoring the enrollment process.</td>
              </tr>
              <tr>
                <td className="px-4 py-2.5"><span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-yellow-100 text-yellow-800">Pending</span></td>
                <td className="px-4 py-2.5 text-gray-600">The session has been registered but is waiting for the user enrollment phase. This typically occurs after a White Glove (pre-provisioning) enrollment, where the device phase is complete and the device is waiting for the user to sign in and continue enrollment.</td>
              </tr>
              <tr>
                <td className="px-4 py-2.5"><span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-800">Complete</span></td>
                <td className="px-4 py-2.5 text-gray-600">The enrollment finished successfully. The device passed through all expected phases.</td>
              </tr>
              <tr>
                <td className="px-4 py-2.5"><span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-red-100 text-red-800">Failed</span></td>
                <td className="px-4 py-2.5 text-gray-600">The enrollment ended in failure — either detected automatically by the agent or marked manually via Admin Mode.</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      {/* Roles */}
      <div>
        <h3 className="text-lg font-semibold text-gray-900 mb-3 flex items-center gap-2">
          <svg className="w-5 h-5 text-gray-500 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0z" />
          </svg>
          User Roles
        </h3>

        <p className="text-gray-700 leading-relaxed mb-4">
          Access to the portal is controlled by role-based permissions. Each team member is assigned a role
          that determines what they can see and do.
        </p>

        <div className="overflow-x-auto">
          <table className="w-full text-sm border border-gray-200 rounded-lg overflow-hidden">
            <thead>
              <tr className="bg-gray-50">
                <th className="text-left px-4 py-2.5 font-semibold text-gray-700 border-b border-gray-200">Role</th>
                <th className="text-left px-4 py-2.5 font-semibold text-gray-700 border-b border-gray-200">Permissions</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              <tr>
                <td className="px-4 py-2.5 font-medium text-gray-900">Admin</td>
                <td className="px-4 py-2.5 text-gray-600">
                  Full access to all tenant configuration, sessions, diagnostics, and settings. Can manage team members,
                  enable Admin Mode for destructive operations, and configure all tenant-level settings.
                  The first user to sign in for a tenant is automatically granted the Admin role.
                </td>
              </tr>
              <tr>
                <td className="px-4 py-2.5 font-medium text-gray-900">Operator</td>
                <td className="px-4 py-2.5 text-gray-600">
                  Can view sessions, manage Bootstrap Tokens (if enabled), and execute actions on devices. Cannot enable Admin Mode
                  or perform destructive operations like deleting sessions.
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </section>
  );
}
