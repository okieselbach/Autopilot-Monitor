export function SectionSetup() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Setup</h2>
      </div>
      <p className="text-gray-700 mb-6">
        Before the agent can send any data to the portal, a few one-time steps are required in the portal itself.
        Follow these steps when setting up Autopilot Monitor for a new tenant.
      </p>

      <ol className="space-y-6">
        <li className="flex gap-4">
          <span className="flex-shrink-0 w-7 h-7 rounded-full bg-blue-600 text-white text-sm font-bold flex items-center justify-center">1</span>
          <div className="flex-1">
            <p className="font-semibold text-gray-900">Sign in — first user becomes Tenant Admin</p>
            <p className="text-sm text-gray-600 mt-1">
              Open the portal and sign in with your Microsoft Entra ID (Azure AD) account. The very first user to log
              in for your organization is automatically granted <strong>Tenant Admin</strong> rights for your tenant.
            </p>
            <div className="mt-2 p-3 bg-blue-50 border border-blue-100 rounded-lg text-sm text-blue-800">
              The Tenant Admin can later promote other users to admin via the <strong>Settings</strong> page.
              Users who are not admins can only access the <strong>Progress</strong> portal — a simplified view
              for tracking a specific device by serial number — and have no access to session details, diagnostics,
              or configuration.
            </div>
          </div>
        </li>

        <li className="flex gap-4">
          <span className="flex-shrink-0 w-7 h-7 rounded-full bg-blue-600 text-white text-sm font-bold flex items-center justify-center">2</span>
          <div className="flex-1">
            <p className="font-semibold text-gray-900">Enable Autopilot Device Validation in Configuration</p>
            <p className="text-sm text-gray-600 mt-1 mb-2">
              Navigate to{" "}
              <span className="font-mono text-xs bg-gray-100 px-1.5 py-0.5 rounded">Settings → Configuration</span>{" "}
              and enable the <strong>Autopilot Device Validation</strong> setting. This is required before the agent
              is permitted to send any session data to the backend — without it, all agent uploads will be rejected.
            </p>
            <div className="p-3 bg-amber-50 border border-amber-200 rounded-lg text-sm text-amber-900">
              <strong>Why is this required?</strong> The Autopilot device check ensures that only devices registered
              in your Intune tenant can register sessions, preventing unintended data from reaching your tenant.
              Consenting to this setting is your confirmation that the agent may collect and transmit enrollment
              telemetry on behalf of your organization.
            </div>
          </div>
        </li>

        <li className="flex gap-4">
          <span className="flex-shrink-0 w-7 h-7 rounded-full bg-green-600 text-white text-sm font-bold flex items-center justify-center">✓</span>
          <div>
            <p className="font-semibold text-gray-900">Ready</p>
            <p className="text-sm text-gray-600 mt-1">
              Once Autopilot Device Validation is enabled, the portal is ready to receive data. Deploy the agent via
              Intune (see <strong>Agent Setup</strong>) and sessions will start appearing in the dashboard as soon
              as devices begin enrolling.
            </p>
          </div>
        </li>
      </ol>
    </section>
  );
}
