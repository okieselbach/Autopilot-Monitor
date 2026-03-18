"use client";

import React, { useState } from "react";

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

export function SectionSettings() {
  const [showDiagGuards, setShowDiagGuards] = useState(false);

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

      {/* Enrollment Device Validation */}
      <SettingsGroup title="Enrollment Device Validation" color="bg-purple-50 text-purple-900" borderColor="border-purple-200">
        <SettingsRow name="Autopilot Device Validation" defaultVal="Disabled">
          Validates that only devices registered in your Intune tenant as Windows Autopilot devices can register sessions.
          When enabled, the backend checks each incoming agent request against the Intune Autopilot device list — unauthorized
          devices are rejected. Enabling this setting requires granting admin consent for the{" "}
          <span className="font-mono text-xs bg-gray-100 px-1.5 py-0.5 rounded">DeviceManagementServiceConfig.Read.All</span>{" "}
          Microsoft Graph permission.
        </SettingsRow>
        <SettingsRow name="Corporate Identifier Validation" defaultVal="Disabled">
          Validates devices against Intune Corporate Device Identifiers (manufacturer, model, and serial number).
          When enabled, the backend checks each incoming agent request against the corporate identifier list in Intune.
          Uses the same{" "}
          <span className="font-mono text-xs bg-gray-100 px-1.5 py-0.5 rounded">DeviceManagementServiceConfig.Read.All</span>{" "}
          permission as Autopilot Device Validation. At least one validation method must be enabled — disabling all causes the
          backend to reject all agent requests.
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
        <SettingsRow name="Hello Wait Timeout" defaultVal="30 seconds">
          How long the agent waits for the Windows Hello wizard to appear after ESP exit (range: 30–300 seconds).
          If Hello does not appear within this window, the agent proceeds with enrollment completion. Increase this
          value if your devices consistently take longer to reach the Hello screen.
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
        <SettingsRow name="Show Script Output (stdout)" defaultVal="Enabled">
          When enabled, standard output from PowerShell scripts (platform and remediation scripts tracked by IME) is
          shown in the session timeline. Disable this if scripts may output sensitive data. Note: error output (stderr)
          is always shown regardless of this setting.
        </SettingsRow>
        <SettingsRow name="Log Level" defaultVal="Info">
          Controls the verbosity of the agent&apos;s own log file.{" "}
          <strong>Info</strong> covers normal operation, <strong>Debug</strong> adds detailed step tracing for
          troubleshooting, and <strong>Verbose</strong> produces a full trace of every internal operation.
          Only change this if actively diagnosing an agent issue — Verbose can produce large log files.
        </SettingsRow>
      </SettingsGroup>

      {/* Enrollment Summary Dialog */}
      <SettingsGroup title="Enrollment Summary Dialog" color="bg-teal-50 text-teal-900" borderColor="border-teal-200">
        <SettingsRow name="Show Enrollment Summary" defaultVal="Disabled">
          Displays a visual enrollment summary dialog to the end user after enrollment completes (on both success and failure).
          Requires the SummaryDialog companion to be deployed alongside the agent.
        </SettingsRow>
        <SettingsRow name="Auto-Close Timeout" defaultVal="60 seconds">
          How long the summary dialog remains open before closing automatically (range: 0–3600 seconds).
          Set to 0 to disable auto-close — the user must close the dialog manually. Only available when
          Show Enrollment Summary is enabled.
        </SettingsRow>
        <SettingsRow name="Launch Retry Timeout" defaultVal="120 seconds">
          How long the agent retries launching the summary dialog when the desktop is locked by credential UI
          (e.g. Windows Hello setup). Set to 0 to disable retries (range: 0–3600 seconds). Only available when
          Show Enrollment Summary is enabled.
        </SettingsRow>
        <SettingsRow name="Branding Image URL" defaultVal="None">
          Optional URL to a banner image displayed at the top of the summary dialog. Recommended size: 540 &times; 80 px.
          Larger images will be center-cropped to fit. Only available when Show Enrollment Summary is enabled.
        </SettingsRow>
      </SettingsGroup>

      {/* Agent Analyzers */}
      <SettingsGroup title="Agent Analyzers" color="bg-orange-50 text-orange-900" borderColor="border-orange-200">
        <SettingsRow name="Local Admin Analyzer" defaultVal="Enabled">
          Detects pre-enrollment local admin account creation — a known Autopilot bypass technique. The analyzer
          runs at enrollment start and completion, comparing local accounts against an expected baseline.
          Unexpected accounts trigger an alert visible in the session detail view.
        </SettingsRow>
        <SettingsRow name="Allowed Local Accounts">
          Accounts considered expected on enrolled devices (will not trigger alerts). Built-in Windows accounts
          (Administrator, Guest, DefaultAccount, WDAGUtilityAccount, defaultuser0–2, etc.) are always allowed and
          shown as read-only. Use this list to add custom service accounts or local accounts that are expected
          in your environment. Only available when Local Admin Analyzer is enabled.
        </SettingsRow>
      </SettingsGroup>

      {/* Notifications */}
      <SettingsGroup title="Notifications" color="bg-indigo-50 text-indigo-900" borderColor="border-indigo-200">
        <SettingsRow name="Notification Provider">
          Select how you want to receive enrollment notifications. Available providers:
          <ul className="list-disc ml-6 mt-1 space-y-1">
            <li>
              <strong>Microsoft Teams (Workflow Webhook)</strong> <span className="text-green-700 text-xs font-medium">(Recommended)</span> &mdash;
              In Teams, go to the target channel &rarr; <em>Manage channel</em> &rarr; <em>Workflows</em> &rarr;
              add <em>&quot;Post to a channel when a webhook request is received&quot;</em> &rarr; copy the generated URL.
              Workflow webhooks are free and do not require a Power Automate Premium license.
            </li>
            <li>
              <strong>Microsoft Teams (Legacy Connector)</strong> <span className="text-amber-700 text-xs font-medium">(Deprecated)</span> &mdash;
              Uses the legacy Office 365 Connector webhook format (MessageCard). Microsoft has deprecated this method.
              Existing configurations will continue to work, but switching to Workflow Webhooks is recommended.
            </li>
            <li>
              <strong>Slack</strong> &mdash;
              In Slack, go to your workspace &rarr; <em>Apps</em> &rarr; <em>Incoming Webhooks</em> &rarr;
              create a new webhook for the target channel &rarr; copy the webhook URL.
            </li>
          </ul>
        </SettingsRow>
        <SettingsRow name="Webhook URL">
          The webhook URL for your selected notification provider. Paste the URL generated during the provider setup.
        </SettingsRow>
        <SettingsRow name="Notify on Success" defaultVal="Enabled (if webhook configured)">
          Send a notification when an enrollment session completes successfully.
        </SettingsRow>
        <SettingsRow name="Notify on Failure" defaultVal="Enabled (if webhook configured)">
          Send a notification when an enrollment session ends in failure. Recommended to keep enabled so
          failed enrollments are surfaced immediately without having to check the portal manually.
        </SettingsRow>
        <SettingsRow name="Send Test Notification">
          Sends a sample notification to your configured webhook to verify the connection is working correctly.
        </SettingsRow>
      </SettingsGroup>

      {/* Diagnostics Package */}
      <SettingsGroup title="Diagnostics Package" color="bg-amber-50 text-amber-900" borderColor="border-amber-200">
        <SettingsRow name="Blob Storage Container SAS URL">
          An Azure Blob Storage Container SAS URL used for diagnostics package uploads. The SAS URL must grant
          at minimum <strong>Read</strong>, <strong>Write</strong>, and <strong>Create</strong> permissions at the
          container level. The SAS URL is stored securely in the backend and never sent to devices — the agent
          requests a short-lived upload URL from the backend just before uploading. The portal shows an expiry
          indicator (green, amber, or red) based on the remaining validity of the SAS URL.
        </SettingsRow>
        <SettingsRow name="Upload Mode" defaultVal="Off">
          Controls when diagnostics packages are uploaded:{" "}
          <strong>Off</strong> — never upload,{" "}
          <strong>Always</strong> — upload after every session,{" "}
          <strong>On Failure Only</strong> — upload only when the session ends in failure (recommended if storage costs
          are a concern). Only available when a Blob Storage URL is configured.
        </SettingsRow>
        <SettingsRow name="Additional Log Paths">
          <span>
            Extra log files or wildcard patterns added to the diagnostics ZIP package. Global paths are defined
            platform-wide by the Galactic Admin and always included. Tenant Admins can add their own paths in addition
            to the global ones (e.g. custom app logs or third-party agent logs).
          </span>
          <span className="block mt-2 text-xs text-gray-500">
            Environment variables (e.g. <span className="font-mono bg-gray-100 px-1 rounded">%ProgramData%</span>) are expanded by the agent.
            Wildcards are only supported in the <strong>last path segment</strong> (e.g.{" "}
            <span className="font-mono bg-gray-100 px-1 rounded">C:\Windows\Panther\*.log</span>).
          </span>
          <span className="block mt-2">
            <button
              onClick={() => setShowDiagGuards(v => !v)}
              className="flex items-center gap-1 text-xs text-gray-500 hover:text-gray-700 select-none"
            >
              <span>{showDiagGuards ? "▾" : "▸"}</span>
              <span>Allowed path prefixes (agent guard)</span>
            </button>
            {showDiagGuards && (
              <div className="mt-1 bg-gray-50 border border-gray-200 rounded px-3 py-2 text-xs font-mono space-y-0.5">
                <p>C:\ProgramData\AutopilotMonitor</p>
                <p>C:\ProgramData\Microsoft\IntuneManagementExtension\Logs</p>
                <p>C:\Windows\Panther</p>
                <p>C:\Windows\Logs</p>
                <p>C:\Windows\SetupDiag</p>
                <p>C:\Windows\SoftwareDistribution\ReportingEvents.log</p>
                <p>C:\Windows\System32\winevt\Logs</p>
                <p>C:\Windows\CCM\Logs</p>
                <p>C:\ProgramData\Microsoft\DiagnosticLogCSP</p>
                <p>C:\ProgramData\Microsoft\Windows\WER</p>
                <p>C:\Windows\Logs\CBS</p>
              </div>
            )}
          </span>
        </SettingsRow>
      </SettingsGroup>

      {/* Data Management */}
      <SettingsGroup title="Data Management" color="bg-rose-50 text-rose-900" borderColor="border-rose-200">
        <SettingsRow name="Data Retention Period" defaultVal="90 days (range: 7–180)">
          Sessions and their associated events are automatically deleted after this many days. Deletion runs as part
          of a periodic maintenance job. Reduce this value to limit storage usage; increase it if you need a longer
          history for auditing or trend analysis.
        </SettingsRow>
        <SettingsRow name="Session Timeout" defaultVal="5 hours (range: 1–12)">
          Sessions that remain in <em>In Progress</em> state beyond this threshold are automatically marked as{" "}
          <em>Failed – Timed Out</em> by the maintenance job. Set this to match (or slightly exceed) your
          Enrollment Status Page (ESP) timeout so stalled sessions don&apos;t permanently inflate your in-progress count.
        </SettingsRow>
      </SettingsGroup>

      {/* Team Management */}
      <SettingsGroup title="Team Management" color="bg-gray-100 text-gray-800" borderColor="border-gray-200">
        <SettingsRow name="Team Members &amp; Roles">
          Manage who has access to the portal and at what level. New members are added by entering their UPN
          (e.g. <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">user@contoso.com</span>).
          The first user to sign in for a tenant is automatically granted Admin rights. Each member is assigned
          one of three roles:
          <ul className="mt-2 space-y-1 list-disc list-inside text-sm">
            <li><strong>Admin</strong> — Full access to all tenant configuration, sessions, diagnostics, and settings.</li>
            <li><strong>Operator</strong> — Can view sessions, manage settings, and execute actions on devices.</li>
          </ul>
          <span className="block mt-2 text-xs text-gray-500">
            Admins can enable or disable individual members, update roles, and grant bootstrap token management permissions.
          </span>
        </SettingsRow>
      </SettingsGroup>

      {/* Bootstrap Sessions */}
      <div className="mb-2 flex items-start gap-2 rounded-md border border-cyan-300 bg-cyan-50 px-4 py-2.5 text-sm text-cyan-900">
        <svg className="mt-0.5 h-5 w-5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M12 2a10 10 0 100 20 10 10 0 000-20z" />
        </svg>
        <span>
          Bootstrap Sessions is an optional feature that is enabled on request only.
          To get access, open a{" "}
          <a href="https://github.com/okieselbach/Autopilot-Monitor/issues" target="_blank" rel="noopener noreferrer" className="font-medium underline hover:text-cyan-700">
            GitHub issue
          </a>{" "}
          to request activation for your tenant.
        </span>
      </div>
      <SettingsGroup title="Bootstrap Sessions" color="bg-cyan-50 text-cyan-900" borderColor="border-cyan-200">
        <SettingsRow name="Bootstrap Tokens">
          Bootstrap tokens allow new devices to register with Autopilot Monitor before device validation is fully
          configured. Each token generates a unique short code and URL that can be shared with technicians or
          included in provisioning scripts. Tokens have a configurable validity duration
          (1 h, 4 h, 8 h, 24 h, 48 h, or 7 days) and can be revoked at any time. The token list shows status
          (Active, Expired, Revoked), creation date, expiry, creator, and usage count. A copy button provides
          both the URL and a ready-to-use PowerShell command.
        </SettingsRow>
      </SettingsGroup>

      {/* Unrestricted Mode */}
      <div className="mb-2 flex items-start gap-2 rounded-md border border-amber-300 bg-amber-50 px-4 py-2.5 text-sm text-amber-900">
        <svg className="mt-0.5 h-5 w-5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M12 2a10 10 0 100 20 10 10 0 000-20z" />
        </svg>
        <span>
          Unrestricted Mode is an optional feature that is enabled on request only.
          To get access, open a{" "}
          <a href="https://github.com/okieselbach/Autopilot-Monitor/issues" target="_blank" rel="noopener noreferrer" className="font-medium underline hover:text-amber-700">
            GitHub issue
          </a>{" "}
          to request activation for your tenant.
        </span>
      </div>
      <SettingsGroup title="Unrestricted Mode" color="bg-amber-50 text-amber-900" borderColor="border-amber-200">
        <SettingsRow name="Unrestricted Mode">
          When enabled, agent guardrails are relaxed: GatherRules can access any registry path, WMI query,
          and PowerShell/system command. File and diagnostics paths are allowed except C:\Users (always blocked
          for privacy). Certain dangerous operations (downloads, user creation, boot manipulation, persistence
          mechanisms) remain hard-blocked even in Unrestricted Mode. This feature requires explicit activation
          by the platform administrator before it becomes visible in tenant settings.
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
