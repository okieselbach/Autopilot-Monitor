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

      <div className="text-sm text-gray-500 italic">
        More details about agent parameters, configuration options, and behavior will be added here soon.
      </div>
    </section>
  );
}
