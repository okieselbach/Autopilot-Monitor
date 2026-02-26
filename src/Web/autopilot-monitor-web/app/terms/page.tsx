"use client";

import Link from "next/link";
import { PublicPageHeader } from "../../components/PublicPageHeader";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";

export default function TermsPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Terms of Use" />
      <main className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-12 space-y-6">
        <div className="bg-white rounded-lg shadow p-6">
          <p className="mt-2 text-gray-600">Conditions and legal disclaimers for using Autopilot Monitor.</p>
        </div>

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
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

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Legal Disclaimer</h2>
          <div className="bg-amber-50 border-l-4 border-amber-500 p-4 rounded">
            <h3 className="font-semibold text-amber-900 mb-2">AS-IS, No Warranty</h3>
            <p className="text-amber-800 text-sm">
              This software is provided "AS-IS" without any warranty of any kind, either expressed or implied,
              including but not limited to the implied warranties of merchantability and fitness for a particular purpose.
            </p>
          </div>
          <div className="space-y-2 text-gray-700">
            <p><strong>No Liability:</strong> The service operator shall not be liable for any damages, including but not limited to direct, indirect, incidental, special, consequential, or punitive damages arising out of the use or inability to use this service.</p>
            <p><strong>No Service Level Agreement (SLA):</strong> This service is provided without any guaranteed uptime or availability. Service interruptions, maintenance windows, or complete shutdown may occur without prior notice.</p>
            <p><strong>No Data Backup Guarantee:</strong> While we implement data retention policies, we do not guarantee the preservation of your data. You are responsible for maintaining your own backups and records.</p>
            <p><strong>Best Effort Support:</strong> Support is provided on a best-effort basis with no guaranteed response times or resolution commitments.</p>
            <p><strong>Use at Your Own Risk:</strong> By using this service, you acknowledge that you do so at your own risk and that you are solely responsible for any consequences arising from your use of the service.</p>
            <p><strong>Active Development Notice:</strong> This project is under active development. Service interruptions or unexpected behavior can occur at any time, especially when updates are deployed to the backend, web application, or agent components.</p>
          </div>
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
