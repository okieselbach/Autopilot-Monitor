"use client";

import { useEffect, useState } from "react";

interface AgentVersionInfo {
  version?: string;
  url?: string;
}

const AGENT_VERSION_URL =
  "https://autopilotmonitor.blob.core.windows.net/agent/version.json";

export function SectionAgent() {
  const [versionInfo, setVersionInfo] = useState<AgentVersionInfo | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;

    async function fetchVersion() {
      try {
        const res = await fetch(AGENT_VERSION_URL, { cache: "no-store" });
        if (res.ok) {
          const data = await res.json();
          if (!cancelled) setVersionInfo(data);
        }
      } catch {
        // silently keep null
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    fetchVersion();
    return () => { cancelled = true; };
  }, []);

  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 3v2m6-2v2M9 19v2m6-2v2M5 9H3m2 6H3m18-6h-2m2 6h-2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2zM9 9h6v6H9V9z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Agent</h2>
      </div>

      <p className="text-gray-700 mb-6">
        The Autopilot Monitor agent is a lightweight .NET application that runs on the device during
        the Windows Autopilot enrollment process. It collects real-time telemetry about enrollment
        phases, app installations, policy processing, and system events — then streams everything to
        the backend for live monitoring and analysis.
      </p>

      <div className="mb-6 p-5 bg-blue-50 border border-blue-200 rounded-lg">
        <div className="flex items-center gap-3 mb-2">
          <svg className="w-5 h-5 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 7h.01M7 3h5c.512 0 1.024.195 1.414.586l7 7a2 2 0 010 2.828l-7 7a2 2 0 01-2.828 0l-7-7A1.994 1.994 0 013 12V7a4 4 0 014-4z" />
          </svg>
          <span className="font-semibold text-blue-900">Latest Agent Version</span>
        </div>
        {loading ? (
          <div className="flex items-center gap-2 text-sm text-blue-700">
            <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
            </svg>
            Loading version info...
          </div>
        ) : versionInfo?.version ? (
          <div className="flex items-center gap-3">
            <span className="inline-flex items-center px-3 py-1.5 bg-blue-100 text-blue-800 text-lg font-mono font-bold rounded-md">
              v{versionInfo.version}
            </span>
          </div>
        ) : (
          <p className="text-sm text-blue-700">Version information is currently unavailable.</p>
        )}
      </div>

      <div className="mb-6">
        <div className="flex items-center gap-2 mb-3">
          <svg className="w-5 h-5 text-green-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
          </svg>
          <h3 className="text-lg font-semibold text-gray-900">Download Integrity Verification</h3>
        </div>
        <p className="text-gray-700 mb-4">
          Every agent download is protected by SHA-256 integrity verification to ensure the binary has not
          been tampered with or corrupted during transfer.
        </p>
        <div className="space-y-3 text-sm text-gray-700">
          <div className="flex items-start gap-3 p-3 bg-green-50 border border-green-100 rounded-lg">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">1</span>
            <div>
              <span className="font-medium text-gray-900">Build-time hash:</span>{" "}
              When a new agent version is built, the CI/CD pipeline computes the SHA-256 hash of the
              agent package and publishes it alongside the binary in <span className="font-mono text-xs bg-green-100 px-1 py-0.5 rounded">version.json</span>.
            </div>
          </div>
          <div className="flex items-start gap-3 p-3 bg-green-50 border border-green-100 rounded-lg">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">2</span>
            <div>
              <span className="font-medium text-gray-900">Download verification:</span>{" "}
              Both the bootstrapper script and the agent&apos;s self-updater verify the SHA-256 hash of
              the downloaded package before installation. If the hash does not match, installation is aborted.
            </div>
          </div>
          <div className="flex items-start gap-3 p-3 bg-green-50 border border-green-100 rounded-lg">
            <span className="text-green-600 font-bold mt-0.5 shrink-0">3</span>
            <div>
              <span className="font-medium text-gray-900">Backend cross-check:</span>{" "}
              The expected hash is also stored in the backend and delivered to the agent via the
              authenticated configuration endpoint — a second, independent trust channel. An attacker
              would need to compromise both the download server and the backend API simultaneously.
            </div>
          </div>
        </div>
        <p className="mt-3 text-sm text-gray-500">
          All communication uses HTTPS (TLS 1.2+). The agent authenticates to the backend using the
          device&apos;s MDM client certificate, ensuring only authorized devices receive configuration data.
        </p>
      </div>

      <div className="mb-6">
        <div className="flex items-center gap-2 mb-3">
          <svg className="w-5 h-5 text-purple-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 9l3 3-3 3m5 0h3M5 20h14a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
          </svg>
          <h3 className="text-lg font-semibold text-gray-900">Command-Line Parameters</h3>
        </div>
        <p className="text-gray-700 mb-4">
          The agent accepts command-line parameters for testing, debugging, and advanced scenarios.
          These are passed when launching the agent executable directly.
        </p>

        <div className="border border-purple-200 rounded-lg overflow-hidden mb-4">
          <div className="px-4 py-2.5 bg-purple-50 border-b border-purple-200">
            <p className="font-semibold text-sm text-purple-900">Session &amp; Lifecycle</p>
          </div>
          <div className="px-4 py-3 space-y-3 text-sm text-gray-700">
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--new-session</span>
              <p className="mt-1">Deletes existing session data and starts a fresh session. Useful when the agent needs to be
              restarted on a device without carrying over stale data from a previous run.</p>
            </div>
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--no-cleanup</span>
              <p className="mt-1">Suppresses self-destruct after enrollment &mdash; agent files and the scheduled task remain on the
              device. Helpful for post-enrollment debugging and log analysis.</p>
            </div>
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--keep-logfile</span>
              <p className="mt-1">Preserves the log directory during self-destruct. Logs remain on disk for later analysis
              even after the agent cleans up everything else.</p>
            </div>
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--reboot-on-complete</span>
              <p className="mt-1">Reboots the device after enrollment completes. The reboot is delayed by 10 seconds by default
              (configurable via the remote configuration). Useful when a reboot is required to finalize
              device setup but not configured as a tenant default.</p>
            </div>
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--disable-geolocation</span>
              <p className="mt-1">Disables geo-location detection. The agent will not attempt to resolve the device&apos;s
              geographic location via IP-based lookup. Useful in restricted network environments or when
              location data is not desired.</p>
            </div>
          </div>
        </div>

        <div className="border border-purple-200 rounded-lg overflow-hidden mb-4">
          <div className="px-4 py-2.5 bg-purple-50 border-b border-purple-200">
            <p className="font-semibold text-sm text-purple-900">Authentication &amp; Bootstrap</p>
          </div>
          <div className="px-4 py-3 space-y-3 text-sm text-gray-700">
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--bootstrap-token &lt;token&gt;</span>
              <p className="mt-1">Provides a bootstrap token for pre-MDM authentication during OOBE, before an MDM client
              certificate is available on the device.</p>
            </div>
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--await-enrollment</span>
              <p className="mt-1">The agent waits for the MDM client certificate to become available before starting monitoring.
              Timeout can be configured with <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">--await-enrollment-timeout</span>.</p>
            </div>
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--await-enrollment-timeout &lt;minutes&gt;</span>
              <p className="mt-1">Maximum time in minutes to wait for the MDM certificate. Default: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">480</span> (8 hours).</p>
            </div>
          </div>
        </div>

        <div className="border border-purple-200 rounded-lg overflow-hidden mb-4">
          <div className="px-4 py-2.5 bg-purple-50 border-b border-purple-200">
            <p className="font-semibold text-sm text-purple-900">Testing &amp; Replay</p>
          </div>
          <div className="px-4 py-3 space-y-3 text-sm text-gray-700">
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--ime-log-path &lt;path&gt;</span>
              <p className="mt-1">Custom path to IME log files. Allows testing with logs collected from other devices
              without running a real enrollment.</p>
            </div>
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--replay-log-dir &lt;path&gt;</span>
              <p className="mt-1">Replays real IME log files from the specified directory and simulates a complete enrollment
              in fast-forward. Creates a real session in the backend &mdash; device information is collected from
              the current machine (WMI/Registry), while enrollment events are extracted from the log files.
              Ideal for testing, demos, or analyzing past enrollments without waiting for a live enrollment.</p>
            </div>
            <div>
              <span className="font-mono text-xs bg-purple-50 px-1.5 py-0.5 rounded font-medium">--replay-speed-factor &lt;n&gt;</span>
              <p className="mt-1">Time compression factor for log replay. Default: <span className="font-mono text-xs bg-gray-100 px-1 py-0.5 rounded">50</span>.
              A factor of 50 means a 50-minute enrollment replays in approximately 1 minute. Delays between
              events are divided by this factor, capped at 5 seconds per delay.</p>
            </div>
          </div>
        </div>

        <div className="p-4 bg-green-50 border border-green-200 rounded-lg mb-4">
          <p className="font-semibold text-sm text-green-900 mb-2">Example &mdash; Replay a captured enrollment</p>
          <div className="bg-green-100 rounded px-3 py-2 text-xs font-mono text-green-900 mb-2">
            AutopilotMonitor.Agent.exe --replay-log-dir &quot;C:\Logs\IME&quot; --replay-speed-factor 100
          </div>
          <p className="text-sm text-green-800">
            Replays a previously captured enrollment at 100x speed, creating a full session visible in the dashboard.
          </p>
        </div>

        <div className="p-4 bg-amber-50 border border-amber-200 rounded-lg">
          <div className="flex items-start gap-2">
            <svg className="w-4 h-4 text-amber-600 mt-0.5 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z" />
            </svg>
            <p className="text-sm text-amber-800">
              Parameters like <span className="font-mono text-xs bg-amber-100 px-1 py-0.5 rounded">--replay-log-dir</span> are
              intended for testing and development environments only &mdash; do not use them in production deployments.
            </p>
          </div>
        </div>
      </div>
    </section>
  );
}
