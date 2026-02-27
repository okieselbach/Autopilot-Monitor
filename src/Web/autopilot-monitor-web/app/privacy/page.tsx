"use client";

import Link from "next/link";
import { PublicPageHeader } from "../../components/PublicPageHeader";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";

export default function PrivacyPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Privacy Policy" />
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-12 space-y-6">
        <div className="bg-white rounded-lg shadow p-6">
          <p className="mt-2 text-gray-600">How Autopilot Monitor handles data.</p>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Data Collection</h2>
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

          <h2 className="text-xl font-semibold text-gray-900 mt-6">Data Storage & Security</h2>
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

          <h2 className="text-xl font-semibold text-gray-900 mt-6">Data Retention</h2>
          <p className="text-gray-700">
            Data retention is configurable per tenant. The default retention period is 90 days.
          </p>

          <h2 className="text-xl font-semibold text-gray-900 mt-6">Data Sharing</h2>
          <p className="text-gray-700">
            Your data is not shared with third parties. Access is restricted to:
          </p>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li>Authenticated users within your tenant</li>
            <li>Galactic Administrators (for platform operations and support)</li>
          </ul>

          <h2 className="text-xl font-semibold text-gray-900 mt-6">Your Rights</h2>
          <p className="text-gray-700">
            As this is a lab environment operated under best-effort principles, formal data subject rights (access, deletion, portability)
            are not guaranteed. However, we will make reasonable efforts to accommodate such requests on a case-by-case basis.
          </p>
        </div>

        <div className="flex items-center gap-4 text-sm">
          <Link href="/docs" className="text-blue-700 hover:text-blue-800 font-medium">
            Documentation
          </Link>
        </div>
      </main>
    </div>
  );
}
