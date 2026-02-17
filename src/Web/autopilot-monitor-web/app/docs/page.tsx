"use client";

import { ProtectedRoute } from "../../components/ProtectedRoute";
import Link from "next/link";

export default function DocsPage() {
  return (
    <ProtectedRoute>
      <div className="min-h-screen bg-gray-50">
        {/* Header */}
        <header className="bg-white shadow-sm border-b border-gray-200 sticky top-0 z-10">
          <div className="max-w-5xl mx-auto px-4 sm:px-6 lg:px-8 py-4">
            <div className="flex items-center justify-between">
              <div className="flex items-center space-x-4">
                <Link
                  href="/"
                  className="flex items-center space-x-2 text-gray-600 hover:text-gray-900 transition-colors"
                >
                  <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 19l-7-7m0 0l7-7m-7 7h18" />
                  </svg>
                  <span>Back to Dashboard</span>
                </Link>
              </div>
              <h1 className="text-2xl font-bold text-blue-600">Documentation</h1>
            </div>
          </div>
        </header>

        {/* Content */}
        <main className="max-w-5xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
          {/* Intune Deployment */}
          <section className="bg-white rounded-lg shadow-md p-8">
            <div className="flex items-center space-x-3 mb-4">
              <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
              </svg>
              <h2 className="text-2xl font-bold text-gray-900">Agent Deployment via Intune</h2>
            </div>
            <p className="text-gray-700 mb-6">
              The Autopilot Monitor agent is deployed to devices using a PowerShell bootstrapper script distributed as an
              Intune <strong>Platform Script</strong>. The script downloads, installs, and registers the agent automatically —
              no manual steps on the device are required.
            </p>

            <ol className="space-y-5">
              <li className="flex gap-4">
                <span className="flex-shrink-0 w-7 h-7 rounded-full bg-blue-600 text-white text-sm font-bold flex items-center justify-center">1</span>
                <div>
                  <p className="font-semibold text-gray-900">Download the bootstrapper script</p>
                  <p className="text-sm text-gray-600 mt-1 mb-2">
                    Download the PowerShell script that installs and configures the Autopilot Monitor agent:
                  </p>
                  <a
                    href="https://autopilotmonitor.blob.core.windows.net/agent/Install-AutopilotMonitor.ps1"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-2 px-4 py-2 bg-blue-600 hover:bg-blue-700 text-white text-sm font-medium rounded-lg transition-colors"
                  >
                    <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                      <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M4 16v1a3 3 0 003 3h10a3 3 0 003-3v-1m-4-4l-4 4m0 0l-4-4m4 4V4" />
                    </svg>
                    Install-AutopilotMonitor.ps1
                  </a>
                </div>
              </li>

              <li className="flex gap-4">
                <span className="flex-shrink-0 w-7 h-7 rounded-full bg-blue-600 text-white text-sm font-bold flex items-center justify-center">2</span>
                <div>
                  <p className="font-semibold text-gray-900">Create a Platform Script in Intune</p>
                  <p className="text-sm text-gray-600 mt-1">
                    In the <strong>Microsoft Intune admin center</strong>, navigate to{" "}
                    <span className="font-mono text-xs bg-gray-100 px-1.5 py-0.5 rounded">Devices → Scripts and remediations → Platform scripts</span>{" "}
                    and click <strong>+ Add → Windows 10 and later</strong>.
                  </p>
                  <div className="mt-3 bg-gray-50 rounded-lg p-4 text-sm space-y-1.5 text-gray-700">
                    <p className="font-medium text-gray-900 mb-2">Recommended script settings:</p>
                    <p><span className="font-medium w-48 inline-block">Name:</span> Install Autopilot Monitor</p>
                    <p><span className="font-medium w-48 inline-block">Script:</span> <em>Upload the downloaded .ps1 file</em></p>
                    <p><span className="font-medium w-48 inline-block">Run this script using logged on credentials:</span> No</p>
                    <p><span className="font-medium w-48 inline-block">Enforce script signature check:</span> No</p>
                    <p><span className="font-medium w-48 inline-block">Run script in 64-bit PowerShell:</span> Yes</p>
                  </div>
                </div>
              </li>

              <li className="flex gap-4">
                <span className="flex-shrink-0 w-7 h-7 rounded-full bg-blue-600 text-white text-sm font-bold flex items-center justify-center">3</span>
                <div>
                  <p className="font-semibold text-gray-900">Assign to a device group</p>
                  <p className="text-sm text-gray-600 mt-1">
                    Assign the script to the device group that covers your Autopilot-enrolled devices. The two most common choices are:
                  </p>
                  <ul className="mt-2 space-y-1.5 text-sm text-gray-700 ml-2">
                    <li className="flex items-start gap-2">
                      <span className="text-blue-600 font-bold mt-0.5">•</span>
                      <span><strong>All devices</strong> — built-in Intune group, covers every managed device</span>
                    </li>
                    <li className="flex items-start gap-2">
                      <span className="text-blue-600 font-bold mt-0.5">•</span>
                      <span>
                        A dynamic Azure AD group for Autopilot devices using the membership rule{" "}
                        <span className="font-mono text-xs bg-gray-100 px-1.5 py-0.5 rounded">(device.devicePhysicalIds -any _ -startsWith &quot;[ZTDId]&quot;)</span>
                        {" "}— targets only Autopilot-registered hardware
                      </span>
                    </li>
                  </ul>
                  <p className="mt-2 text-sm text-gray-500 italic">
                    The "All Autopilot devices" dynamic group is preferred if you want to limit telemetry to Autopilot-enrolled hardware only.
                  </p>
                </div>
              </li>

              <li className="flex gap-4">
                <span className="flex-shrink-0 w-7 h-7 rounded-full bg-green-600 text-white text-sm font-bold flex items-center justify-center">✓</span>
                <div>
                  <p className="font-semibold text-gray-900">Done</p>
                  <p className="text-sm text-gray-600 mt-1">
                    Once the script runs on a device, the agent installs itself, creates a scheduled task under
                    SYSTEM, and begins monitoring the Autopilot enrollment immediately. Sessions will appear in
                    your dashboard within seconds of the agent starting.
                  </p>
                </div>
              </li>
            </ol>
          </section>

          {/* Introduction */}
          <section className="bg-white rounded-lg shadow-md p-8">
            <div className="flex items-center space-x-3 mb-4">
              <svg className="w-8 h-8 text-blue-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
              </svg>
              <h2 className="text-3xl font-bold text-gray-900">Welcome to Autopilot Monitor</h2>
            </div>
            <p className="text-gray-700 text-lg leading-relaxed">
              This documentation provides comprehensive guidance on using the Autopilot Monitor platform.
              Whether you're a <strong>Tenant Admin</strong>, <strong>Regular User</strong>, or <strong>Galactic Admin</strong>,
              you'll find everything you need to know here.
            </p>
          </section>

          {/* User Roles */}
          <section className="bg-white rounded-lg shadow-md p-8">
            <h2 className="text-2xl font-bold text-gray-900 mb-4 flex items-center">
              <svg className="w-6 h-6 text-green-600 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0zm6 3a2 2 0 11-4 0 2 2 0 014 0zM7 10a2 2 0 11-4 0 2 2 0 014 0z" />
              </svg>
              User Roles & Permissions
            </h2>

            <div className="space-y-4">
              {/* Galactic Admin */}
              <div className="border-l-4 border-purple-500 pl-4 py-2 bg-purple-50">
                <h3 className="text-lg font-semibold text-purple-900">Galactic Admin</h3>
                <p className="text-gray-700 mt-1">
                  System-wide administrator with full access to all tenants and configuration.
                </p>
                <ul className="mt-2 space-y-1 text-sm text-gray-600 ml-4">
                  <li>• View and manage all tenant configurations</li>
                  <li>• Suspend/unsuspend tenants</li>
                  <li>• Manage tenant admins across all tenants</li>
                  <li>• Configure global rate limiting</li>
                  <li>• Trigger manual maintenance operations</li>
                  <li>• Access to all tenant data and sessions</li>
                </ul>
              </div>

              {/* Tenant Admin */}
              <div className="border-l-4 border-blue-500 pl-4 py-2 bg-blue-50">
                <h3 className="text-lg font-semibold text-blue-900">Tenant Admin</h3>
                <p className="text-gray-700 mt-1">
                  Administrator for a specific tenant with full access to that tenant's resources.
                </p>
                <ul className="mt-2 space-y-1 text-sm text-gray-600 ml-4">
                  <li>• View all Autopilot sessions for their tenant</li>
                  <li>• Access detailed diagnostics and logs</li>
                  <li>• Configure tenant-specific settings (data retention, timeouts, etc.)</li>
                  <li>• Manage other tenant admins (add/remove/disable)</li>
                  <li>• View usage metrics and statistics</li>
                </ul>
                <p className="mt-2 text-sm text-gray-600 italic">
                  Note: The first user to log in for a tenant automatically becomes a Tenant Admin.
                </p>
              </div>

              {/* Regular User */}
              <div className="border-l-4 border-green-500 pl-4 py-2 bg-green-50">
                <h3 className="text-lg font-semibold text-green-900">Regular User</h3>
                <p className="text-gray-700 mt-1">
                  Standard user with limited access focused on device tracking.
                </p>
                <ul className="mt-2 space-y-1 text-sm text-gray-600 ml-4">
                  <li>• Access simplified "Track" page</li>
                  <li>• Search devices by serial number</li>
                  <li>• View ESP-style progress for specific devices</li>
                  <li>• Limited to device-specific information</li>
                </ul>
              </div>
            </div>
          </section>

          {/* Getting Started */}
          <section className="bg-white rounded-lg shadow-md p-8">
            <h2 className="text-2xl font-bold text-gray-900 mb-4 flex items-center">
              <svg className="w-6 h-6 text-indigo-600 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
              </svg>
              Getting Started
            </h2>

            <div className="space-y-4">
              <div>
                <h3 className="text-lg font-semibold text-gray-900 mb-2">1. First Login</h3>
                <p className="text-gray-700">
                  When you first log in to Autopilot Monitor, you'll be authenticated via Azure AD/Entra ID.
                  The first user from your organization (tenant) will automatically be assigned as a <strong>Tenant Admin</strong>.
                </p>
              </div>

              <div>
                <h3 className="text-lg font-semibold text-gray-900 mb-2">2. Dashboard Overview</h3>
                <p className="text-gray-700 mb-2">
                  The main dashboard provides a real-time view of all active Autopilot sessions:
                </p>
                <ul className="space-y-1 text-gray-600 ml-4">
                  <li>• <strong>Active Sessions:</strong> Currently running Autopilot enrollments</li>
                  <li>• <strong>Recent Activity:</strong> Latest session updates and status changes</li>
                  <li>• <strong>Quick Stats:</strong> Success rates, average duration, active devices</li>
                  <li>• <strong>Session Table:</strong> Searchable and filterable list of all sessions</li>
                </ul>
              </div>

              <div>
                <h3 className="text-lg font-semibold text-gray-900 mb-2">3. Monitoring Sessions</h3>
                <p className="text-gray-700">
                  Click on any session to view detailed information including phase progress, diagnostic logs,
                  and real-time ESP (Enrollment Status Page) progression.
                </p>
              </div>
            </div>
          </section>

          {/* Feature Guides */}
          <section className="bg-white rounded-lg shadow-md p-8">
            <h2 className="text-2xl font-bold text-gray-900 mb-4 flex items-center">
              <svg className="w-6 h-6 text-orange-600 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z" />
              </svg>
              Feature Guides
            </h2>

            <div className="space-y-6">
              {/* Session Tracking */}
              <div>
                <h3 className="text-lg font-semibold text-gray-900 mb-2">Session Tracking</h3>
                <p className="text-gray-700 mb-2">
                  Monitor Windows Autopilot enrollment sessions in real-time with full diagnostic visibility.
                </p>
                <div className="bg-gray-50 rounded p-4 text-sm text-gray-700 space-y-2">
                  <p><strong>Session States:</strong></p>
                  <ul className="ml-4 space-y-1">
                    <li>• <span className="text-green-600 font-medium">Running:</span> Active enrollment in progress</li>
                    <li>• <span className="text-blue-600 font-medium">Completed:</span> Successfully finished enrollment</li>
                    <li>• <span className="text-red-600 font-medium">Failed:</span> Encountered an error</li>
                    <li>• <span className="text-yellow-600 font-medium">TimedOut:</span> Session exceeded timeout limit</li>
                  </ul>
                </div>
              </div>

              {/* Admin Management (for Tenant Admins) */}
              <div>
                <h3 className="text-lg font-semibold text-gray-900 mb-2">Admin Management</h3>
                <p className="text-gray-700 mb-2">
                  Tenant Admins can manage other administrators for their tenant via the <strong>Settings</strong> page.
                </p>
                <div className="bg-gray-50 rounded p-4 text-sm text-gray-700 space-y-2">
                  <p><strong>Admin Actions:</strong></p>
                  <ul className="ml-4 space-y-1">
                    <li>• <strong>Add Admin:</strong> Enter the UPN (email) of a user to grant admin access</li>
                    <li>• <strong>Remove Admin:</strong> Revoke admin privileges (cannot remove last admin)</li>
                    <li>• <strong>Disable Admin:</strong> Temporarily disable admin access without removal</li>
                    <li>• <strong>Enable Admin:</strong> Re-enable a disabled admin</li>
                  </ul>
                  <p className="text-yellow-700 mt-2">
                    ⚠️ <strong>Important:</strong> Always ensure you have at least one active admin to prevent lockout.
                  </p>
                </div>
              </div>

              {/* Configuration */}
              <div>
                <h3 className="text-lg font-semibold text-gray-900 mb-2">Tenant Configuration</h3>
                <p className="text-gray-700 mb-2">
                  Admins can customize various settings for their tenant including:
                </p>
                <div className="bg-gray-50 rounded p-4 text-sm text-gray-700">
                  <ul className="ml-4 space-y-1">
                    <li>• <strong>Data Retention:</strong> How long to keep session data (7-3650 days)</li>
                    <li>• <strong>Session Timeout:</strong> When to mark abandoned sessions as timed out (1-48 hours)</li>
                    <li>• <strong>Serial Number Validation:</strong> Enforce serial number format requirements</li>
                    <li>• <strong>Hardware Whitelists:</strong> Restrict allowed manufacturers and models</li>
                    <li>• <strong>Max Payload Size:</strong> Control NDJSON upload limits (1-50 MB)</li>
                  </ul>
                </div>
              </div>

              {/* Search & Filtering */}
              <div>
                <h3 className="text-lg font-semibold text-gray-900 mb-2">Search & Filtering</h3>
                <p className="text-gray-700">
                  Quickly find sessions using the search bar which supports:
                </p>
                <ul className="mt-2 ml-4 space-y-1 text-sm text-gray-600">
                  <li>• Serial numbers</li>
                  <li>• Device names</li>
                  <li>• Session IDs</li>
                  <li>• Status filtering</li>
                </ul>
              </div>
            </div>
          </section>

          {/* Best Practices */}
          <section className="bg-white rounded-lg shadow-md p-8">
            <h2 className="text-2xl font-bold text-gray-900 mb-4 flex items-center">
              <svg className="w-6 h-6 text-emerald-600 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              Best Practices
            </h2>

            <div className="space-y-4 text-gray-700">
              <div className="flex items-start space-x-3">
                <span className="text-emerald-600 font-bold text-lg">1.</span>
                <div>
                  <strong>Maintain Multiple Admins:</strong>
                  <p className="text-sm mt-1">
                    Always have at least 2-3 active tenant admins to prevent lockout scenarios.
                  </p>
                </div>
              </div>

              <div className="flex items-start space-x-3">
                <span className="text-emerald-600 font-bold text-lg">2.</span>
                <div>
                  <strong>Monitor Active Sessions:</strong>
                  <p className="text-sm mt-1">
                    Check the dashboard regularly during deployment periods to catch issues early.
                  </p>
                </div>
              </div>

              <div className="flex items-start space-x-3">
                <span className="text-emerald-600 font-bold text-lg">3.</span>
                <div>
                  <strong>Set Appropriate Retention:</strong>
                  <p className="text-sm mt-1">
                    Configure data retention based on your compliance and troubleshooting needs.
                    Longer retention helps with historical analysis but increases storage.
                  </p>
                </div>
              </div>

              <div className="flex items-start space-x-3">
                <span className="text-emerald-600 font-bold text-lg">4.</span>
                <div>
                  <strong>Use Hardware Whitelists:</strong>
                  <p className="text-sm mt-1">
                    Restrict enrollments to approved manufacturers and models to improve security.
                  </p>
                </div>
              </div>

              <div className="flex items-start space-x-3">
                <span className="text-emerald-600 font-bold text-lg">5.</span>
                <div>
                  <strong>Review Failed Sessions:</strong>
                  <p className="text-sm mt-1">
                    Investigate failed enrollments promptly to identify patterns and systemic issues.
                  </p>
                </div>
              </div>
            </div>
          </section>

          {/* Troubleshooting */}
          <section className="bg-white rounded-lg shadow-md p-8">
            <h2 className="text-2xl font-bold text-gray-900 mb-4 flex items-center">
              <svg className="w-6 h-6 text-red-600 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
              </svg>
              Troubleshooting
            </h2>

            <div className="space-y-4">
              <div>
                <h3 className="text-lg font-semibold text-gray-900">Cannot Access Admin Features</h3>
                <p className="text-gray-700 text-sm mt-1">
                  <strong>Solution:</strong> Ensure you've been added as a Tenant Admin. Contact your existing admins
                  or Galactic Admin to grant you admin permissions.
                </p>
              </div>

              <div>
                <h3 className="text-lg font-semibold text-gray-900">Session Not Appearing</h3>
                <p className="text-gray-700 text-sm mt-1">
                  <strong>Solution:</strong> Check that your device is configured with the correct Autopilot profile
                  and that telemetry is enabled. Verify network connectivity from the device.
                </p>
              </div>

              <div>
                <h3 className="text-lg font-semibold text-gray-900">Tenant Suspended Access</h3>
                <p className="text-gray-700 text-sm mt-1">
                  <strong>Solution:</strong> Your tenant has been suspended by a Galactic Admin. Contact system
                  administrators for assistance. The suspension message will include relevant details.
                </p>
              </div>

              <div>
                <h3 className="text-lg font-semibold text-gray-900">Data Not Loading</h3>
                <p className="text-gray-700 text-sm mt-1">
                  <strong>Solution:</strong> Refresh your browser. If the issue persists, check your authentication
                  status in the top-right user menu. You may need to re-login.
                </p>
              </div>
            </div>
          </section>

          {/* Support & Contact */}
          <section className="bg-gradient-to-r from-blue-600 to-indigo-600 rounded-lg shadow-md p-8 text-white">
            <h2 className="text-2xl font-bold mb-4 flex items-center">
              <svg className="w-6 h-6 mr-2" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
              </svg>
              Need Help?
            </h2>
            <p className="text-blue-100 mb-4">
              For additional support, questions, or feature requests, please refer to the <Link href="/about" className="underline font-medium">About & Legal</Link> page
              for contact information and support options.
            </p>
            <div className="bg-blue-700 bg-opacity-50 rounded-lg p-4">
              <p className="text-sm">
                <strong>Quick Links:</strong>
              </p>
              <ul className="mt-2 space-y-2 text-sm">
                <li>
                  <Link href="/about" className="hover:underline flex items-center space-x-2">
                    <span>→</span>
                    <span>About & Legal Information</span>
                  </Link>
                </li>
                <li>
                  <Link href="/settings" className="hover:underline flex items-center space-x-2">
                    <span>→</span>
                    <span>Tenant Settings</span>
                  </Link>
                </li>
                <li>
                  <Link href="/" className="hover:underline flex items-center space-x-2">
                    <span>→</span>
                    <span>Dashboard</span>
                  </Link>
                </li>
              </ul>
            </div>
          </section>

          {/* Version Info */}
          <div className="text-center text-sm text-gray-500">
            <p>Autopilot Monitor v1.0.0</p>
            <p className="mt-1">Documentation last updated: {new Date().toLocaleDateString()}</p>
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
