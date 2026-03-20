import { PublicPageHeader } from "../../components/PublicPageHeader";

export default function TermsPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicPageHeader title="Terms of Use" />
      <main className="px-4 sm:px-6 lg:px-8 py-8 space-y-6">
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

        <div className="bg-white rounded-lg shadow p-6 space-y-4">
          <h2 className="text-xl font-semibold text-gray-900">Third-Party Data Sources & Attributions</h2>
          <p className="text-gray-700">
            The vulnerability correlation feature uses the following external data sources to identify known vulnerabilities in installed software:
          </p>
          <div className="space-y-3">
            <div className="p-4 border border-gray-200 rounded-lg">
              <h3 className="font-semibold text-gray-900 mb-1">National Vulnerability Database (NVD)</h3>
              <p className="text-sm text-gray-700 mb-2">
                This product uses the NVD API but is not endorsed or certified by the NVD.
              </p>
              <p className="text-sm text-gray-500">
                The NVD is maintained by the National Institute of Standards and Technology (NIST). CVE and CPE data is sourced from the NVD API 2.0. For more information, visit{" "}
                <a href="https://nvd.nist.gov/" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">nvd.nist.gov</a>.
              </p>
            </div>
            <div className="p-4 border border-gray-200 rounded-lg">
              <h3 className="font-semibold text-gray-900 mb-1">CISA Known Exploited Vulnerabilities (KEV) Catalog</h3>
              <p className="text-sm text-gray-500">
                Actively exploited vulnerability data is sourced from the CISA KEV Catalog maintained by the Cybersecurity and Infrastructure Security Agency. For more information, visit{" "}
                <a href="https://www.cisa.gov/known-exploited-vulnerabilities-catalog" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">cisa.gov</a>.
              </p>
            </div>
            <div className="p-4 border border-gray-200 rounded-lg">
              <h3 className="font-semibold text-gray-900 mb-1">Microsoft Security Response Center (MSRC)</h3>
              <p className="text-sm text-gray-500">
                Microsoft-specific vulnerability data is sourced from the MSRC Security Update Guide API. For more information, visit{" "}
                <a href="https://msrc.microsoft.com/" target="_blank" rel="noopener noreferrer" className="text-indigo-600 hover:text-indigo-800 underline">msrc.microsoft.com</a>.
              </p>
            </div>
          </div>
        </div>

      </main>
    </div>
  );
}
