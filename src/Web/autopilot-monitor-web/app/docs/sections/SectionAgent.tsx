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

      <div className="text-sm text-gray-500 italic">
        More details about agent parameters, configuration options, and behavior will be added here soon.
      </div>
    </section>
  );
}
