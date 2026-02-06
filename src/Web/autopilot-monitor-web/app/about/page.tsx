"use client";

import { useRouter } from "next/navigation";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import Link from "next/link";

const VERSION = "1.0.0";

export default function AboutPage() {
  const router = useRouter();

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
                <h1 className="text-2xl font-bold text-gray-900">About & Legal</h1>
                <p className="text-sm text-gray-500">Version {VERSION}</p>
              </div>
            </div>
          </div>
        </header>

        {/* Main Content */}
        <main className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-6">
          {/* About Section */}
          <div className="bg-white rounded-lg shadow">
            <div className="p-6 border-b border-gray-200">
              <h2 className="text-xl font-semibold text-gray-900 flex items-center space-x-2">
                <svg className="w-6 h-6 text-blue-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
                <span>About Autopilot Monitor</span>
              </h2>
            </div>
            <div className="p-6 space-y-4">
              <p className="text-gray-700">
                <strong>Autopilot Monitor</strong> is an advanced monitoring and troubleshooting tool for Windows Autopilot deployments.
                It provides real-time visibility into the Autopilot provisioning process, helping IT administrators identify and resolve issues quickly.
              </p>
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4 pt-4">
                <div className="bg-blue-50 p-4 rounded-lg">
                  <h3 className="font-semibold text-blue-900 mb-2">Version</h3>
                  <p className="text-blue-700">{VERSION}</p>
                </div>
                <div className="bg-blue-50 p-4 rounded-lg">
                  <h3 className="font-semibold text-blue-900 mb-2">Lab Tenant</h3>
                  <p className="text-blue-700">gktatooine.net</p>
                </div>
                <div className="bg-blue-50 p-4 rounded-lg">
                  <h3 className="font-semibold text-blue-900 mb-2">Architecture</h3>
                  <p className="text-blue-700">Multi-tenant SaaS</p>
                </div>
                <div className="bg-blue-50 p-4 rounded-lg">
                  <h3 className="font-semibold text-blue-900 mb-2">Technology Stack</h3>
                  <p className="text-blue-700">ASP.NET Core, React, Next.js, SignalR</p>
                </div>
              </div>
            </div>
          </div>

          {/* Terms of Use Section */}
          <div className="bg-white rounded-lg shadow">
            <div className="p-6 border-b border-gray-200">
              <h2 className="text-xl font-semibold text-gray-900 flex items-center space-x-2">
                <svg className="w-6 h-6 text-blue-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
                <span>Terms of Use</span>
              </h2>
            </div>
            <div className="p-6 space-y-4">
              <p className="text-gray-700">
                By using this service, you agree to the following terms:
              </p>
              <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
                <li>This service is provided for monitoring and troubleshooting Windows Autopilot deployments in your organization.</li>
                <li>You are responsible for ensuring that your use of this service complies with your organization's policies and applicable laws.</li>
                <li>Access to this service requires proper authentication via Azure AD/Entra ID.</li>
                <li>Each tenant's data is isolated and accessible only to authorized users of that tenant.</li>
                <li>You must not attempt to access data belonging to other tenants or circumvent security measures.</li>
                <li>The service operator reserves the right to suspend or terminate access at any time without prior notice.</li>
              </ul>
            </div>
          </div>

          {/* Legal Disclaimer Section */}
          <div className="bg-white rounded-lg shadow">
            <div className="p-6 border-b border-gray-200">
              <h2 className="text-xl font-semibold text-gray-900 flex items-center space-x-2">
                <svg className="w-6 h-6 text-amber-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
                </svg>
                <span>Legal Disclaimer</span>
              </h2>
            </div>
            <div className="p-6 space-y-4">
              <div className="bg-amber-50 border-l-4 border-amber-500 p-4 rounded">
                <h3 className="font-semibold text-amber-900 mb-2">AS-IS, No Warranty</h3>
                <p className="text-amber-800 text-sm">
                  This software is provided "AS-IS" without any warranty of any kind, either expressed or implied,
                  including but not limited to the implied warranties of merchantability and fitness for a particular purpose.
                </p>
              </div>
              <div className="space-y-2 text-gray-700">
                <p><strong>No Liability:</strong> The service operator shall not be liable for any damages, including but not limited to direct,
                indirect, incidental, special, consequential, or punitive damages arising out of the use or inability to use this service.</p>

                <p><strong>No Service Level Agreement (SLA):</strong> This service is provided without any guaranteed uptime or availability.
                Service interruptions, maintenance windows, or complete shutdown may occur without prior notice.</p>

                <p><strong>No Data Backup Guarantee:</strong> While we implement data retention policies, we do not guarantee the preservation
                of your data. You are responsible for maintaining your own backups and records.</p>

                <p><strong>Best Effort Support:</strong> Support is provided on a best-effort basis with no guaranteed response times or resolution commitments.</p>

                <p><strong>Use at Your Own Risk:</strong> By using this service, you acknowledge that you do so at your own risk and that
                you are solely responsible for any consequences arising from your use of the service.</p>
              </div>
            </div>
          </div>

          {/* Privacy & Data Section */}
          <div className="bg-white rounded-lg shadow">
            <div className="p-6 border-b border-gray-200">
              <h2 className="text-xl font-semibold text-gray-900 flex items-center space-x-2">
                <svg className="w-6 h-6 text-green-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                </svg>
                <span>Privacy & Data Handling</span>
              </h2>
            </div>
            <div className="p-6 space-y-4">
              <h3 className="font-semibold text-gray-900">Data Collection</h3>
              <p className="text-gray-700">
                This service collects and processes the following data to provide monitoring functionality:
              </p>
              <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
                <li>Device hardware information (manufacturer, model, serial number)</li>
                <li>Autopilot provisioning session data (status, events, timestamps)</li>
                <li>Azure AD/Entra ID tenant information</li>
                <li>User authentication information (UPN, display name, tenant ID)</li>
                <li>Operational telemetry and audit logs</li>
              </ul>

              <h3 className="font-semibold text-gray-900 mt-6">Data Storage & Security</h3>
              <p className="text-gray-700">
                We implement security measures on a best-effort basis:
              </p>
              <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
                <li>Multi-tenant architecture with tenant isolation</li>
                <li>JWT-based authentication and authorization</li>
                <li>Certificate validation for device agent connections</li>
                <li>Encrypted data transmission (HTTPS/WSS)</li>
                <li>PII logging disabled in production environments</li>
              </ul>

              <h3 className="font-semibold text-gray-900 mt-6">Data Retention</h3>
              <p className="text-gray-700">
                Data retention is configurable per tenant. The default retention period is 90 days.
              </p>
              <div className="bg-blue-50 p-4 rounded-lg mt-2">
                <p className="text-blue-900 text-sm">
                  <strong>Configure retention:</strong> Tenant administrators can adjust data retention settings in the{" "}
                  <Link href="/settings" className="underline hover:text-blue-700">Configuration</Link> page.
                  Sessions and events older than the configured retention period are automatically deleted by a daily maintenance job.
                </p>
              </div>

              <h3 className="font-semibold text-gray-900 mt-6">Data Sharing</h3>
              <p className="text-gray-700">
                Your data is not shared with third parties. Access is restricted to:
              </p>
              <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
                <li>Authenticated users within your tenant</li>
                <li>Galactic Administrators (for platform operations and support)</li>
              </ul>

              <h3 className="font-semibold text-gray-900 mt-6">Your Rights</h3>
              <p className="text-gray-700">
                As this is a lab environment operated under best-effort principles, formal data subject rights (access, deletion, portability)
                are not guaranteed. However, we will make reasonable efforts to accommodate such requests on a case-by-case basis.
              </p>
            </div>
          </div>

          {/* Support & Contact Section */}
          <div className="bg-white rounded-lg shadow">
            <div className="p-6 border-b border-gray-200">
              <h2 className="text-xl font-semibold text-gray-900 flex items-center space-x-2">
                <svg className="w-6 h-6 text-purple-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M18.364 5.636l-3.536 3.536m0 5.656l3.536 3.536M9.172 9.172L5.636 5.636m3.536 9.192l-3.536 3.536M21 12a9 9 0 11-18 0 9 9 0 0118 0zm-5 0a4 4 0 11-8 0 4 4 0 018 0z" />
                </svg>
                <span>Support & Contact</span>
              </h2>
            </div>
            <div className="p-6 space-y-4">
              <div className="bg-purple-50 border-l-4 border-purple-500 p-4 rounded">
                <h3 className="font-semibold text-purple-900 mb-2">Best Effort Support</h3>
                <p className="text-purple-800 text-sm">
                  Support is provided on a best-effort basis with no guaranteed response times or resolution commitments.
                </p>
              </div>

              <h3 className="font-semibold text-gray-900">How to Get Support</h3>
              <ul className="space-y-3 text-gray-700">
                <li className="flex items-start space-x-3">
                  <svg className="w-5 h-5 text-gray-500 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" />
                  </svg>
                  <div>
                    <strong>Documentation:</strong> Check the{" "}
                    <Link href="/docs" className="text-blue-600 hover:text-blue-800 underline">Documentation</Link> for guides and troubleshooting information.
                  </div>
                </li>
                <li className="flex items-start space-x-3">
                  <svg className="w-5 h-5 text-gray-500 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
                  </svg>
                  <div>
                    <strong>Feedback:</strong> Submit feedback or report issues through the feedback icon in the navigation bar.
                  </div>
                </li>
                <li className="flex items-start space-x-3">
                  <svg className="w-5 h-5 text-gray-500 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                    <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M3 8l7.89 5.26a2 2 0 002.22 0L21 8M5 19h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
                  </svg>
                  <div>
                    <strong>Direct Contact:</strong> For urgent issues affecting your production environment, contact your Galactic Administrator.
                  </div>
                </li>
              </ul>

              <div className="bg-gray-50 p-4 rounded-lg mt-4">
                <h3 className="font-semibold text-gray-900 mb-2">Multi-Tenant Lab Environment</h3>
                <p className="text-gray-700 text-sm">
                  This is a multi-tenant application operated as a lab environment under the tenant <strong>gktatooine.net</strong>.
                  Each tenant's data is isolated and accessible only to authorized users within that tenant.
                </p>
              </div>
            </div>
          </div>

          {/* Footer Info */}
          <div className="bg-gradient-to-r from-blue-600 to-indigo-600 rounded-lg shadow p-6 text-white">
            <div className="flex items-start space-x-4">
              <svg className="w-12 h-12 text-white opacity-80" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
              </svg>
              <div>
                <h3 className="text-xl font-semibold mb-2">Transparency & User Choice</h3>
                <p className="text-blue-100 text-sm">
                  We believe in transparency. This page provides all the information you need to make an informed decision about whether
                  to use this service and grant the necessary permissions. By using this service, you acknowledge that you have read
                  and understood these terms and agree to use the service at your own discretion and risk.
                </p>
                <p className="text-blue-100 text-sm mt-2">
                  Version {VERSION} | Last updated: {new Date().toLocaleDateString()}
                </p>
              </div>
            </div>
          </div>
        </main>
      </div>
    </ProtectedRoute>
  );
}
