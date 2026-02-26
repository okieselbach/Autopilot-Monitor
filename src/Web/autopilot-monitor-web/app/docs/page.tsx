"use client";

import Link from "next/link";
import { useState } from "react";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";
import { PublicPageHeader } from "../../components/PublicPageHeader";

const NAV_SECTIONS = [
  { id: "private-preview", label: "Private Preview" },
  { id: "overview",    label: "Overview" },
  { id: "setup",       label: "Setup" },
  { id: "agent-setup", label: "Agent Setup" },
  { id: "settings",    label: "Settings" },
];

function SectionPrivatePreview() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-amber-500 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Private Preview</h2>
      </div>

      <p className="text-gray-700 leading-relaxed mb-6">
        Autopilot Monitor is currently in <strong>Private Preview</strong>. The service is available to a limited number
        of organizations while the core functionality is being refined and stabilized. Access is invite-only and managed
        manually on a per-tenant basis.
      </p>

      {/* Warning banner */}
      <div className="mb-6 p-4 bg-red-50 border border-red-200 rounded-lg text-sm text-red-900">
        <p className="font-semibold mb-1 flex items-center gap-2">
          <svg className="w-4 h-4 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
          </svg>
          Expect instability during Private Preview
        </p>
        <ul className="space-y-1 ml-1">
          <li className="flex items-start gap-2">
            <span className="shrink-0 mt-0.5">•</span>
            <span>Things <strong>can and will break</strong>. The backend, web frontend, and agent are all under active development and receive frequent updates.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="shrink-0 mt-0.5">•</span>
            <span><strong>Availability is not guaranteed.</strong> Deployments and maintenance may cause downtime without prior notice.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="shrink-0 mt-0.5">•</span>
            <span>Features, APIs, and data structures may change at any time. Existing session data may be cleared between major updates.</span>
          </li>
        </ul>
        <p className="mt-2 text-red-800">
          This preview is intended for testing and feedback — not for production use in critical environments.
        </p>
      </div>

      {/* How to get access */}
      <div className="mb-8">
        <h3 className="text-lg font-semibold text-gray-900 mb-3">How to request access</h3>
        <p className="text-gray-700 leading-relaxed mb-4">
          To get started, sign in with your Microsoft Entra ID account on the Autopilot Monitor portal. If your
          organization has not yet been granted access, you will see a <strong>Private Preview</strong> screen
          (see below) confirming that your tenant has been placed on the waitlist. Once you are on the waitlist,
          reach out via LinkedIn or open a GitHub issue to request activation — your tenant is then enabled manually
          in the backend. I'll check incoming requests regularly and approve them as quickly as possible, depending 
          on my capacity. When you signed-up, sign in again later to view the updated approval status on your dashboard.
        </p>
        <div className="flex flex-wrap gap-3 mb-6">
          <a
            href="https://www.linkedin.com/in/nicobostelmann"
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-2 px-4 py-2 bg-blue-700 hover:bg-blue-800 text-white text-sm font-medium rounded-lg transition-colors"
          >
            <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
              <path d="M19 0h-14c-2.761 0-5 2.239-5 5v14c0 2.761 2.239 5 5 5h14c2.762 0 5-2.239 5-5v-14c0-2.761-2.238-5-5-5zm-11 19h-3v-11h3v11zm-1.5-12.268c-.966 0-1.75-.79-1.75-1.764s.784-1.764 1.75-1.764 1.75.79 1.75 1.764-.783 1.764-1.75 1.764zm13.5 12.268h-3v-5.604c0-3.368-4-3.113-4 0v5.604h-3v-11h3v1.765c1.396-2.586 7-2.777 7 2.476v6.759z" />
            </svg>
            Contact on LinkedIn
          </a>
          <a
            href="https://github.com/nicobostelmann/autopilot-monitor/issues"
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-900 text-white text-sm font-medium rounded-lg transition-colors"
          >
            <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
              <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z" />
            </svg>
            Open a GitHub Issue
          </a>
        </div>

        {/* Screenshot */}
        <div className="rounded-lg overflow-hidden border border-gray-200 shadow-sm max-w-sm">
          <img
            src="/private-preview.png"
            alt="Private Preview waitlist screen"
            className="w-full"
          />
        </div>
        <p className="text-xs text-gray-500 mt-2">
          The Private Preview screen you will see when your tenant is on the waitlist.
        </p>
      </div>

      {/* After approval */}
      <div className="p-4 bg-green-50 border border-green-200 rounded-lg text-sm text-green-900">
        <p className="font-semibold mb-1">After your tenant is approved</p>
        <p>
          Once access is granted, you can sign back in and will be taken directly to the portal. Continue with
          the <strong>Setup</strong> and <strong>Agent Setup</strong> sections of this documentation to get
          your first Autopilot enrollment sessions flowing in.
        </p>
      </div>
    </section>
  );
}

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
            <div className="flex flex-wrap items-center gap-2">
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
              <a
                href="https://raw.githubusercontent.com/okieselbach/Autopilot-Monitor/refs/heads/main/scripts/Bootstrap/Install-AutopilotMonitor.ps1"
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex items-center gap-2 px-3 py-2 bg-gray-100 hover:bg-gray-200 text-gray-700 text-sm font-medium rounded-lg transition-colors border border-gray-300"
              >
                <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
                  <path d="M12 0C5.37 0 0 5.37 0 12c0 5.31 3.435 9.795 8.205 11.385.6.105.825-.255.825-.57 0-.285-.015-1.23-.015-2.235-3.015.555-3.795-.735-4.035-1.41-.135-.345-.72-1.41-1.23-1.695-.42-.225-1.02-.78-.015-.795.945-.015 1.62.87 1.845 1.23 1.08 1.815 2.805 1.305 3.495.99.105-.78.42-1.305.765-1.605-2.67-.3-5.46-1.335-5.46-5.925 0-1.305.465-2.385 1.23-3.225-.12-.3-.54-1.53.12-3.18 0 0 1.005-.315 3.3 1.23.96-.27 1.98-.405 3-.405s2.04.135 3 .405c2.295-1.56 3.3-1.23 3.3-1.23.66 1.65.24 2.88.12 3.18.765.84 1.23 1.905 1.23 3.225 0 4.605-2.805 5.625-5.475 5.925.435.375.81 1.095.81 2.22 0 1.605-.015 2.895-.015 3.3 0 .315.225.69.825.57A12.02 12.02 0 0024 12c0-6.63-5.37-12-12-12z" />
                </svg>
                Alternate download via GitHub
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

function SettingsGroup({ title, color, borderColor, children }: { title: string; color: string; borderColor: string; children: React.ReactNode }) {
  return (
    <div className={`border ${borderColor} rounded-lg overflow-hidden mb-6`}>
      <div className={`px-4 py-2.5 ${color}`}>
        <p className="font-semibold text-sm">{title}</p>
      </div>
      <div className="divide-y divide-gray-100 bg-white">
        {children}
      </div>
    </div>
  );
}

function SettingsRow({ name, defaultVal, children }: { name: string; defaultVal?: string; children: React.ReactNode }) {
  return (
    <div className="px-4 py-3 grid grid-cols-1 sm:grid-cols-3 gap-1 sm:gap-4 text-sm bg-white">
      <div className="sm:col-span-1">
        <p className="font-medium text-gray-900">{name}</p>
        {defaultVal && <p className="text-xs text-gray-500 mt-0.5">Default: {defaultVal}</p>}
      </div>
      <div className="sm:col-span-2 text-gray-600 leading-relaxed">{children}</div>
    </div>
  );
}

function SectionSettings() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Settings Reference</h2>
      </div>

      <p className="text-gray-700 leading-relaxed mb-8">
        The <strong>Settings</strong> page (accessible to Tenant Admins) controls all aspects of how Autopilot Monitor
        behaves for your tenant — from security and device filtering, to agent behavior, notifications, and data retention.
        Below is a reference for every available option.
      </p>

      {/* Security */}
      <SettingsGroup title="Security" color="bg-purple-50 text-purple-900" borderColor="border-purple-200">
        <SettingsRow name="Autopilot Device Validation" defaultVal="Disabled">
          Validates that only devices registered in your Intune tenant as Windows Autopilot devices can register sessions.
          When enabled, the backend checks each incoming agent request against the Intune Autopilot device list — unauthorized
          devices are rejected. Enabling this setting requires granting admin consent for the{" "}
          <span className="font-mono text-xs bg-gray-100 px-1.5 py-0.5 rounded">DeviceManagementServiceConfig.Read.All</span>{" "}
          Microsoft Graph permission. This is <strong>required</strong> before the agent can send any data (see{" "}
          <strong>Setup</strong>).
        </SettingsRow>
      </SettingsGroup>

      {/* Hardware Whitelist */}
      <SettingsGroup title="Hardware Whitelist" color="bg-sky-50 text-sky-900" borderColor="border-sky-200">
        <SettingsRow name="Allowed Manufacturers" defaultVal="Dell*, HP*, Lenovo*, Microsoft Corporation">
          Comma-separated list of manufacturer names that are permitted to register sessions. Wildcards (<span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">*</span>) are
          supported — e.g. <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">Dell*</span> matches any string starting with &quot;Dell&quot;.
          Devices with a manufacturer not on this list will have their session registration rejected by the backend.
          Set to <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">*</span> to allow all manufacturers.
        </SettingsRow>
        <SettingsRow name="Allowed Models" defaultVal="* (all models)">
          Comma-separated list of device model names to allow. Works the same as the manufacturer filter with wildcard support.
          Use this to restrict telemetry to specific hardware lines, e.g. <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">Latitude*,EliteBook*</span>.
        </SettingsRow>
      </SettingsGroup>

      {/* Agent Collectors */}
      <SettingsGroup title="Agent Collectors" color="bg-emerald-50 text-emerald-900" borderColor="border-emerald-200">
        <SettingsRow name="Performance Collector" defaultVal="Enabled, 30 s interval">
          When enabled, the agent periodically collects CPU, memory, disk, and network metrics during enrollment and
          streams them to the portal. The interval (30–300 seconds) controls how often samples are taken. Disabling
          this reduces network traffic but removes the performance timeline from session diagnostics.
          Core collectors (enrollment event tracking, Windows Hello detector) are always active and cannot be disabled.
        </SettingsRow>
      </SettingsGroup>

      {/* Agent Parameters */}
      <SettingsGroup title="Agent Parameters" color="bg-violet-50 text-violet-900" borderColor="border-violet-200">
        <SettingsRow name="Self-Destruct on Complete" defaultVal="Enabled">
          After enrollment finishes, the agent removes its scheduled task and all agent files from the device.
          This is the recommended mode — the agent is temporary by design and should not remain on enrolled devices.
        </SettingsRow>
        <SettingsRow name="Keep Log File" defaultVal="Disabled">
          Only available when Self-Destruct is enabled. If turned on, the agent&apos;s local log file is preserved on
          the device even after self-destruct. Useful for troubleshooting when you need a local record of what happened.
        </SettingsRow>
        <SettingsRow name="Reboot on Complete" defaultVal="Disabled">
          Triggers an automatic reboot of the device once the Autopilot enrollment is detected as complete.
          When enabled, a configurable <strong>Reboot Delay</strong> (0–3600 seconds, default 10 s) gives the user
          a brief window to see the result before the reboot happens.
        </SettingsRow>
        <SettingsRow name="Geo-Location Detection" defaultVal="Enabled">
          The agent queries an external IP geolocation service to capture the device&apos;s public IP address,
          approximate location, and ISP at enrollment time. This data appears in the session detail view and can
          help identify where devices are being enrolled. Disable this if your security policy prohibits outbound
          requests to third-party services.
        </SettingsRow>
        <SettingsRow name="IME Pattern Match Log" defaultVal="Disabled">
          When enabled, the agent writes matched Intune Management Extension (IME) log lines to a local file at{" "}
          <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">%ProgramData%\AutopilotMonitor\Logs\ime_pattern_matches.log</span>.
          Useful for offline analysis of app deployment patterns captured during enrollment.
        </SettingsRow>
        <SettingsRow name="Log Level" defaultVal="Info">
          Controls the verbosity of the agent&apos;s own log file.{" "}
          <strong>Info</strong> covers normal operation, <strong>Debug</strong> adds detailed step tracing for
          troubleshooting, and <strong>Verbose</strong> produces a full trace of every internal operation.
          Only change this if actively diagnosing an agent issue — Verbose can produce large log files.
        </SettingsRow>
      </SettingsGroup>

      {/* Teams Notifications */}
      <SettingsGroup title="Teams Notifications" color="bg-indigo-50 text-indigo-900" borderColor="border-indigo-200">
        <SettingsRow name="Incoming Webhook URL">
          A Microsoft Teams Incoming Webhook URL. Once configured, the portal can send enrollment outcome notifications
          directly to a Teams channel. To create one: open the target channel in Teams → <em>Connectors</em> → search
          for <em>Incoming Webhook</em> → configure and copy the URL.
        </SettingsRow>
        <SettingsRow name="Notify on Success" defaultVal="Enabled (if webhook configured)">
          Send a Teams notification when an enrollment session completes successfully.
        </SettingsRow>
        <SettingsRow name="Notify on Failure" defaultVal="Enabled (if webhook configured)">
          Send a Teams notification when an enrollment session ends in failure. Recommended to keep enabled so
          failed enrollments are surfaced immediately without having to check the portal manually.
        </SettingsRow>
      </SettingsGroup>

      {/* Diagnostics Package */}
      <SettingsGroup title="Diagnostics Package" color="bg-amber-50 text-amber-900" borderColor="border-amber-200">
        <SettingsRow name="Blob Storage Container SAS URL">
          An Azure Blob Storage Container SAS URL where the agent uploads diagnostics packages. The SAS URL must grant
          at minimum <strong>Read</strong>, <strong>Write</strong>, and <strong>Create</strong> permissions at the
          container level. Diagnostic data is uploaded <em>directly</em> from the device to Blob Storage — it does
          not pass through the Autopilot Monitor backend.
        </SettingsRow>
        <SettingsRow name="Upload Mode" defaultVal="Off">
          Controls when diagnostics packages are uploaded:{" "}
          <strong>Off</strong> — never upload,{" "}
          <strong>Always</strong> — upload after every session,{" "}
          <strong>OnFailure</strong> — upload only when the session ends in failure (recommended if storage costs
          are a concern). Only available when a Blob Storage URL is configured.
        </SettingsRow>
      </SettingsGroup>

      {/* Data Management */}
      <SettingsGroup title="Data Management" color="bg-rose-50 text-rose-900" borderColor="border-rose-200">
        <SettingsRow name="Data Retention Period" defaultVal="90 days (range: 7–180)">
          Sessions and their associated events are automatically deleted after this many days. Deletion runs as part
          of a daily maintenance job. Reduce this value to limit storage usage; increase it if you need a longer
          history for auditing or trend analysis.
        </SettingsRow>
        <SettingsRow name="Session Timeout" defaultVal="5 hours (range: 1–12)">
          Sessions that remain in <em>In Progress</em> state beyond this threshold are automatically marked as{" "}
          <em>Failed – Timed Out</em> by the daily maintenance job. Set this to match (or slightly exceed) your
          Enrollment Status Page (ESP) timeout so stalled sessions don&apos;t permanently inflate your in-progress count.
        </SettingsRow>
      </SettingsGroup>

      {/* Admin Users */}
      <SettingsGroup title="Admin Users" color="bg-gray-100 text-gray-800" borderColor="border-gray-200">
        <SettingsRow name="Tenant Admins">
          The list of users with full admin access to the portal. Admins can view all sessions, access diagnostics,
          manage settings, and add or remove other admins. The first user to sign in for a tenant is automatically
          granted Tenant Admin rights. Additional admins can be added by entering their UPN (e.g.{" "}
          <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">user@contoso.com</span>).
          Users without admin access can only use the <strong>Progress</strong> portal to track a specific device
          by serial number.
        </SettingsRow>
      </SettingsGroup>

      {/* Danger Zone */}
      <div className="border border-red-200 rounded-lg overflow-hidden">
        <div className="px-4 py-2.5 bg-red-50 text-red-900">
          <p className="font-semibold text-sm">Danger Zone</p>
        </div>
        <div className="px-4 py-3 text-sm text-gray-700 bg-white">
          <p className="font-medium text-gray-900 mb-1">Offboard Tenant</p>
          <p>
            Permanently and irreversibly deletes <strong>all tenant data</strong>: sessions, events, analyze rules,
            audit logs, configuration, and all admin accounts. You will be signed out immediately. This action
            requires typing <span className="font-mono text-xs bg-red-50 text-red-700 px-1.5 py-0.5 rounded border border-red-200">OFFBOARD</span>{" "}
            as a confirmation step and <strong>cannot be undone</strong>.
          </p>
        </div>
      </div>
    </section>
  );
}

const SECTION_COMPONENTS: Record<string, () => JSX.Element> = {
  "private-preview": SectionPrivatePreview,
  "overview":    SectionOverview,
  "setup":       SectionSetup,
  "agent-setup": SectionAgentSetup,
  "settings":    SectionSettings,
};

export default function DocsPage() {
  const [activeSection, setActiveSection] = useState("overview");
  const [mobileSidebarOpen, setMobileSidebarOpen] = useState(false);

  const ActiveContent = SECTION_COMPONENTS[activeSection] ?? SectionOverview;

  const activeLabel = NAV_SECTIONS.find((s) => s.id === activeSection)?.label ?? "Contents";

  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Documentation" />

      {/* Mobile sidebar overlay */}
      {mobileSidebarOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/40 md:hidden"
          onClick={() => setMobileSidebarOpen(false)}
        />
      )}

      {/* Mobile sidebar drawer */}
      <div
        className={`fixed top-0 left-0 z-50 h-full w-56 bg-white shadow-xl transition-transform duration-200 md:hidden ${
          mobileSidebarOpen ? "translate-x-0" : "-translate-x-full"
        }`}
      >
        <div className="flex items-center justify-between px-4 pt-5 pb-3 border-b border-gray-100">
          <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider">Contents</p>
          <button
            onClick={() => setMobileSidebarOpen(false)}
            className="p-1 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
        <ul className="p-3 space-y-0.5">
          {NAV_SECTIONS.map((s) => (
            <li key={s.id}>
              <button
                onClick={() => { setActiveSection(s.id); setMobileSidebarOpen(false); }}
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
      </div>

      {/* Two-column layout */}
      <div className="max-w-6xl mx-auto px-4 sm:px-6 lg:px-8 py-8 flex gap-8 items-start">

        {/* Desktop sidebar */}
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

          {/* Mobile: contents toggle bar */}
          <div className="md:hidden">
            <button
              onClick={() => setMobileSidebarOpen(true)}
              className="flex items-center gap-2 px-3 py-2 rounded-lg border border-gray-200 bg-white shadow-sm text-sm text-gray-600 hover:bg-gray-50 transition-colors"
            >
              <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M4 6h16M4 12h16M4 18h7" />
              </svg>
              <span className="font-medium text-gray-700">{activeLabel}</span>
              <svg className="w-3.5 h-3.5 text-gray-400 ml-auto" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
              </svg>
            </button>
          </div>

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
