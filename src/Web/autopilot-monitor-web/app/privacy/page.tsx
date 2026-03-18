import Link from "next/link";
import { PublicPageHeader } from "../../components/PublicPageHeader";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";

export default function PrivacyPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Privacy Policy" />
      <main className="px-4 sm:px-6 lg:px-8 py-8 space-y-6">
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

          <h2 className="text-xl font-semibold text-gray-900 mt-6">Data Processing Context</h2>
          <p className="text-gray-700">
            During an Autopilot enrollment, the user authenticates solely to verify their identity and initiate the process.
            After that, the user is not actively interacting with the device while provisioning runs. As a result, the data
            collected by the monitoring agent consists exclusively of <strong>technical enrollment events</strong> — no user
            activity, browsing data, or personal content is captured.
          </p>
          <p className="text-gray-700">
            Tenant administrators retain full control over collected data through the following options:
          </p>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li><strong>Data Retention</strong> — configurable retention period per tenant (default 90 days); expired sessions are automatically purged</li>
            <li><strong>Delete Session</strong> — delete individual monitoring sessions on demand</li>
            <li><strong>Offboard Tenant</strong> — remove all data and configurations for a tenant from the service</li>
          </ul>
          <p className="text-gray-700">
            These controls ensure that no personal information accumulates in the backend beyond what is necessary for
            enrollment monitoring. The service is designed for operational transparency, not user surveillance.
          </p>

          <h2 className="text-xl font-semibold text-gray-900 mt-6">Data Storage & Security</h2>
          <p className="text-gray-700">
            The platform is built with a layered security architecture designed to protect data at every level:
          </p>

          <h3 className="text-lg font-medium text-gray-800 mt-4">Authentication & Device Identity</h3>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li>Device agents authenticate via <strong>Intune MDM client certificates</strong>, validated against the embedded Intune CA chain</li>
            <li>Web users authenticate via <strong>Microsoft Entra ID (Azure AD)</strong> with multi-tenant JWT validation</li>
            <li><strong>Autopilot device validation</strong> via Microsoft Graph — only registered Autopilot devices are accepted</li>
            <li>Optional <strong>hardware whitelist</strong> for additional device verification</li>
            <li>Per-device <strong>rate limiting</strong> (sliding window) to prevent abuse</li>
          </ul>

          <h3 className="text-lg font-medium text-gray-800 mt-4">Tenant Isolation</h3>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li>Strict <strong>multi-tenant data isolation</strong> — all storage queries are partitioned by Tenant ID</li>
            <li>Real-time channels (SignalR) are scoped to <strong>tenant-specific groups</strong></li>
            <li>Independent configuration, audit logs, and device management per tenant</li>
          </ul>

          <h3 className="text-lg font-medium text-gray-800 mt-4">Transport & Data Protection</h3>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li>All communication encrypted via <strong>HTTPS/TLS</strong>; real-time updates via secure WebSocket</li>
            <li>Diagnostics upload URLs are issued on-demand, and never persisted on the device</li>
            <li>Azure Storage encryption at rest for all persisted data</li>
            <li>PII logging disabled in production environments</li>
          </ul>

          <h3 className="text-lg font-medium text-gray-800 mt-4">Access Control</h3>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-4">
            <li><strong>Role-based access</strong>: Tenant Admin (full tenant management), Operator, Users</li>
            <li>Device blocking capabilities for compromised or unauthorized devices</li>
            <li>Comprehensive <strong>audit logging</strong> of administrative actions</li>
          </ul>

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
            As this is an environment operated under best-effort principles, formal data subject rights (access, deletion, portability)
            are not guaranteed. However, we will make reasonable efforts to accommodate such requests on a case-by-case basis.
          </p>
        </div>

      </main>
    </div>
  );
}
