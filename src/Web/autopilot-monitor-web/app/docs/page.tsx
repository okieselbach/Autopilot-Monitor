"use client";

import Link from "next/link";
import { useState } from "react";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";
import { PublicPageHeader } from "../../components/PublicPageHeader";

const NAV_SECTIONS = [
  { id: "overview",    label: "Overview" },
  { id: "setup",       label: "Setup" },
  { id: "agent-setup", label: "Agent Setup" },
];

function SectionOverview() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Overview</h2>
      </div>

      <p className="text-gray-700 leading-relaxed mb-6">
        <strong>Autopilot Monitor</strong> provides real-time visibility into Windows Autopilot enrollment sessions.
        A lightweight agent is deployed to devices via Intune and runs only during the enrollment process, streaming
        events to the portal as they happen. You can watch enrollments progress phase by phase, see exactly where
        failures occur, and review the full event history of any past session.
      </p>

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4 mb-8">
        <div className="bg-blue-50 rounded-lg p-4">
          <p className="font-semibold text-blue-900 mb-1">Live Sessions</p>
          <p className="text-sm text-blue-800">Watch Autopilot enrollments unfold phase by phase, in real time.</p>
        </div>
        <div className="bg-indigo-50 rounded-lg p-4">
          <p className="font-semibold text-indigo-900 mb-1">Diagnostics</p>
          <p className="text-sm text-indigo-800">Full event timeline, app install details, and failure reasons for every session.</p>
        </div>
        <div className="bg-violet-50 rounded-lg p-4">
          <p className="font-semibold text-violet-900 mb-1">Gather Rules</p>
          <p className="text-sm text-violet-800">Run custom diagnostic commands on demand and collect output centrally.</p>
        </div>
      </div>

      <div className="border-t border-gray-100 pt-6">
        <h3 className="text-lg font-semibold text-gray-900 mb-3 flex items-center gap-2">
          <svg className="w-5 h-5 text-amber-500 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17 20h5v-2a3 3 0 00-5.356-1.857M17 20H7m10 0v-2c0-.656-.126-1.283-.356-1.857M7 20H2v-2a3 3 0 015.356-1.857M7 20v-2c0-.656.126-1.283.356-1.857m0 0a5.002 5.002 0 019.288 0M15 7a3 3 0 11-6 0 3 3 0 016 0zm6 3a2 2 0 11-4 0 2 2 0 014 0zM7 10a2 2 0 11-4 0 2 2 0 014 0z" />
          </svg>
          The real value: community-driven Analyze Rules
        </h3>
        <p className="text-gray-700 leading-relaxed mb-4">
          Raw event data is useful — but the true power of Autopilot Monitor lies in its <strong>Analyze Rules</strong>.
          These are configurable rules that run automatically against every enrollment session and flag known failure
          patterns, misconfigurations, or anomalies with a clear description and suggested fix. The more rules exist,
          the more reliable and actionable the analysis becomes.
        </p>
        <p className="text-gray-700 leading-relaxed mb-4">
          No single person or team can anticipate every failure mode across all the different environments, hardware
          combinations, and Intune configurations out there. That&apos;s where <strong>collective intelligence</strong> comes in:
          every admin who has solved a tricky Autopilot failure has knowledge that could save hours of troubleshooting
          for someone else — if it were captured as a rule.
        </p>
        <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-900">
          <p className="font-semibold mb-2">You&apos;re invited to contribute</p>
          <p className="mb-2">
            If you&apos;ve encountered a failure pattern, found a reliable signal in the event data, or have ideas for
            what a useful analysis rule could look like — please share it. Rules can be built directly in the portal
            under <strong>Analyze Rules</strong>, and the more the community contributes, the more the tool grows in
            value for everyone over time.
          </p>
          <p>
            Even if you can&apos;t write a rule yourself, sharing hints, failure descriptions, or event patterns is
            enormously helpful. The goal is a library of rules that covers as much ground as possible — built by the
            people who work with Autopilot every day.
          </p>
        </div>
      </div>
    </section>
  );
}

function SectionSetup() {
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

function SectionAgentSetup() {
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
            <div>
              <span><strong>Fresh OS install:</strong> The OS installation date must be within the threshold (default: 120 minutes). Devices enrolled weeks or months ago fail this check immediately.</span>
              <p className="mt-1 text-green-800">
                If you have devices that were imaged earlier and sat in storage before deployment, adjust the threshold via the script parameter{" "}
                <span className="font-mono text-xs bg-green-100 px-1.5 py-0.5 rounded">MaxOsAgeMinutes</span>{" "}
                at the top of the script — e.g. set it to <span className="font-mono text-xs bg-green-100 px-1.5 py-0.5 rounded">2880</span> for 48 hours.
              </p>
            </div>
          </li>
          <li className="flex items-start gap-2">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">✓</span>
            <span><strong>MDM enrollment not yet complete:</strong> If the device is already fully MDM-enrolled, the script exits without installing anything.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">✓</span>
            <span><strong>No existing agent:</strong> If a previous agent installation is detected (leftover from a prior run), the script skips re-installation.</span>
          </li>
        </ul>
        <p className="mt-3 text-green-800">
          The agent is <strong>temporary by design</strong>: once the Autopilot enrollment completes, the agent
          uninstalls itself and removes the scheduled task. It only exists on the device for the duration of the
          enrollment process.
        </p>
      </div>

      <ol className="space-y-5">
        <li className="flex gap-4">
          <span className="flex-shrink-0 w-7 h-7 rounded-full bg-blue-600 text-white text-sm font-bold flex items-center justify-center">1</span>
          <div>
            <p className="font-semibold text-gray-900">Download the bootstrapper script</p>
            <p className="text-sm text-gray-600 mt-1 mb-2">
              Download the PowerShell script that installs and configures the Autopilot Monitor agent:
            </p>
            <a
              href="/api/download/bootstrapper"
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

const SECTION_COMPONENTS: Record<string, () => JSX.Element> = {
  "overview":    SectionOverview,
  "setup":       SectionSetup,
  "agent-setup": SectionAgentSetup,
};

export default function DocsPage() {
  const [activeSection, setActiveSection] = useState("overview");

  const ActiveContent = SECTION_COMPONENTS[activeSection] ?? SectionOverview;

  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Documentation" />

      {/* Two-column layout */}
      <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-8 flex gap-8 items-start">

        {/* Sidebar */}
        <aside className="w-52 shrink-0 hidden md:block">
          <nav className="sticky top-24 bg-white rounded-lg shadow-sm border border-gray-200 p-4">
            <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider mb-3 px-1">
              Contents
            </p>
            <ul className="space-y-0.5">
              {NAV_SECTIONS.map((s) => (
                <li key={s.id}>
                  <button
                    onClick={() => setActiveSection(s.id)}
                    className={`w-full text-left px-3 py-2 rounded-md text-sm transition-colors ${
                      activeSection === s.id
                        ? "bg-blue-50 text-blue-700 font-semibold"
                        : "text-gray-600 hover:bg-gray-100 hover:text-gray-900"
                    }`}
                  >
                    {s.label}
                  </button>
                </li>
              ))}
            </ul>
          </nav>
        </aside>

        {/* Content — only the active section is rendered */}
        <main className="flex-1 min-w-0 space-y-8">
          <ActiveContent />
          <div className="text-center text-sm text-gray-500 pb-4">
            <p>Autopilot Monitor v1.0.0</p>
            <p className="mt-1">Documentation last updated: {new Date().toLocaleDateString()}</p>
          </div>
        </main>

      </div>
    </div>
  );
}
