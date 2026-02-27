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
