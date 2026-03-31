export function SectionAgentSetup() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 16a4 4 0 01-.88-7.903A5 5 0 1115.9 6L16 6a5 5 0 011 9.9M15 13l-3-3m0 0l-3 3m3-3v12" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Agent Setup via Intune</h2>
      </div>
      <p className="text-gray-700 mb-4">
        The Autopilot Monitor agent is deployed to devices using a PowerShell bootstrapper script distributed as an
        Intune <strong>Platform Script</strong>. The script downloads, installs, and registers the agent automatically —
        no manual steps on the device are required.
      </p>
      <div className="mb-6 p-4 bg-green-50 border border-green-200 rounded-lg text-sm text-green-900">
        <p className="font-semibold mb-2 flex items-center gap-2">
          <svg className="w-4 h-4 text-green-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
          </svg>
          Safe to assign broadly — already-enrolled devices are not affected
        </p>
        <p className="mb-3">
          Before installing anything, the bootstrapper runs a series of pre-requisite checks. The agent is only
          installed when <strong>all</strong> checks pass. Devices that do not meet the criteria are skipped silently.
        </p>
        <ul className="space-y-1.5 ml-1">
          <li className="flex items-start gap-2">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">✓</span>
            <span><strong>No previous deployment:</strong> A registry marker{" "}
              <span className="font-mono text-xs bg-green-100 px-1.5 py-0.5 rounded">HKLM:\SOFTWARE\AutopilotMonitor\Deployed</span>{" "}
              is the only artifact that survives agent self-destruct — everything else is removed. This marker acts as a critical security gate that permanently prevents repeated execution of the bootstrapper and agent on the same device.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">✓</span>
            <span><strong>No real user profiles:</strong> Combines WMI ({" "}
              <span className="font-mono text-xs bg-green-100 px-1.5 py-0.5 rounded">Win32_UserProfile.Special</span>{" "}
              ) and filesystem checks under{" "}
              <span className="font-mono text-xs bg-green-100 px-1.5 py-0.5 rounded">C:\Users</span>{" "}
              — system profiles (defaultuser*, Public, Default) are excluded. Real user profiles indicate the device is already in productive use.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">✓</span>
            <span><strong>No previous user logon:</strong> Checks{" "}
              <span className="font-mono text-xs bg-green-100 px-1.5 py-0.5 rounded">LastLoggedOnUser</span>{" "}
              in the LogonUI registry — during Device ESP no real user has logged on yet. A real username indicates the device has been used interactively.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">✓</span>
            <span><strong>No desktop shell:</strong> Checks if{" "}
              <span className="font-mono text-xs bg-green-100 px-1.5 py-0.5 rounded">explorer.exe</span>{" "}
              is running — during Device ESP the enrollment status page is shown instead of the Windows desktop. A running explorer process indicates the device is beyond initial enrollment.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">✓</span>
            <span><strong>Within bootstrap window:</strong> Device uptime must be under 12 hours. Prevents installation on devices that have been sitting powered on without completing enrollment. Sleep and standby do not reset this timer.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">✓</span>
            <span><strong>Agent not already installed:</strong> If the agent binary is already present at the expected path, the script skips re-installation.</span>
          </li>
        </ul>
        <p className="mt-3 text-green-800">
          The agent is <strong>temporary by design</strong>: once the Autopilot enrollment completes, the agent
          uninstalls itself and removes the scheduled task. It only exists on the device for the duration of the
          enrollment process.
        </p>
        <div className="mt-4 pt-4 border-t border-green-200">
          <p className="font-semibold mb-1 flex items-center gap-2">
            <svg className="w-4 h-4 text-green-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M2.458 12C3.732 7.943 7.523 5 12 5c4.478 0 8.268 2.943 9.542 7-1.274 4.057-5.064 7-9.542 7-4.477 0-8.268-2.943-9.542-7z" />
            </svg>
            Test it yourself: Dry-run before deployment
          </p>
          <p className="text-green-800 mb-2">
            Want to verify which devices would receive the agent? Run this read-only check in PowerShell on any
            machine — it evaluates all bootstrap guards and transparently reports the install decision.
            No changes are made, only read operations:
          </p>
          <div className="bg-green-100 rounded px-3 py-2 font-mono text-xs select-all mb-2">
            irm &apos;https://autopilotmonitor.blob.core.windows.net/agent/Test-ShouldBootstrapAgent.ps1&apos; | iex
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <a
              href="https://github.com/okieselbach/Autopilot-Monitor/blob/main/scripts/Bootstrap/Test-ShouldBootstrapAgent.ps1"
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-2 px-3 py-1.5 bg-green-700 hover:bg-green-800 text-white text-xs font-medium rounded-lg transition-colors"
            >
              <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24">
                <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
              </svg>
              Test-ShouldBootstrapAgent.ps1
            </a>
            <a
              href="https://raw.githubusercontent.com/okieselbach/Autopilot-Monitor/refs/heads/main/scripts/Bootstrap/Test-ShouldBootstrapAgent.ps1"
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-1.5 px-2.5 py-1.5 text-green-700 hover:text-green-900 text-xs font-medium rounded-md transition-colors"
            >
              <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
              </svg>
              Raw script
            </a>
          </div>
        </div>
      </div>

      <ol className="space-y-5">
        <li className="flex gap-4">
          <span className="flex-shrink-0 w-7 h-7 rounded-full bg-blue-600 text-white text-sm font-bold flex items-center justify-center">1</span>
          <div>
            <p className="font-semibold text-gray-900">Download the bootstrapper script</p>
            <p className="text-sm text-gray-600 mt-1 mb-2">
              Download the PowerShell script that installs and configures the Autopilot Monitor agent:
            </p>
            <div className="flex flex-wrap items-center gap-2">
              <a
                href="https://github.com/okieselbach/Autopilot-Monitor/blob/main/scripts/Bootstrap/Install-AutopilotMonitor.ps1"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-2 px-4 py-2 bg-blue-600 hover:bg-blue-700 text-white text-sm font-medium rounded-lg transition-colors"
              >
                <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                  <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
                </svg>
                Install-AutopilotMonitor.ps1
              </a>
              <a
                href="https://raw.githubusercontent.com/okieselbach/Autopilot-Monitor/refs/heads/main/scripts/Bootstrap/Install-AutopilotMonitor.ps1"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-1.5 px-2.5 py-1.5 text-gray-500 hover:text-gray-700 text-xs font-medium rounded-md transition-colors"
              >
                <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" />
                </svg>
                Raw script
              </a>
            </div>
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
              The &quot;All Autopilot devices&quot; dynamic group is preferred if you want to limit telemetry to Autopilot-enrolled hardware only.
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
  );
}
