import Link from "next/link";

export function SectionOverview() {
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

      {/* Feature highlights */}
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
          <p className="font-semibold text-violet-900 mb-1">Gather &amp; Analyze Rules</p>
          <p className="text-sm text-violet-800">Run custom diagnostic commands on demand and automatically flag known failure patterns.</p>
        </div>
      </div>

      {/* Getting started navigation */}
      <div className="border-t border-gray-100 pt-6 mb-8">
        <h3 className="text-lg font-semibold text-gray-900 mb-4">Where to go next</h3>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">

          <Link href="/docs/setup" className="group flex items-start gap-3 rounded-lg border border-gray-200 p-4 hover:border-blue-300 hover:bg-blue-50 transition-colors">
            <svg className="w-5 h-5 text-blue-500 shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M11 5H6a2 2 0 00-2 2v11a2 2 0 002 2h11a2 2 0 002-2v-5m-1.414-9.414a2 2 0 112.828 2.828L11.828 15H9v-2.828l8.586-8.586z" />
            </svg>
            <div>
              <p className="font-medium text-gray-900 group-hover:text-blue-700 text-sm">Setup</p>
              <p className="text-xs text-gray-500 mt-0.5">First-time setup: tenant registration, certificates, and configuration.</p>
            </div>
          </Link>

          <Link href="/docs/agent-setup" className="group flex items-start gap-3 rounded-lg border border-gray-200 p-4 hover:border-blue-300 hover:bg-blue-50 transition-colors">
            <svg className="w-5 h-5 text-blue-500 shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
            </svg>
            <div>
              <p className="font-medium text-gray-900 group-hover:text-blue-700 text-sm">Agent Setup</p>
              <p className="text-xs text-gray-500 mt-0.5">Deploy the agent to devices via Intune — step-by-step guide.</p>
            </div>
          </Link>

          <Link href="/docs/agent" className="group flex items-start gap-3 rounded-lg border border-gray-200 p-4 hover:border-blue-300 hover:bg-blue-50 transition-colors">
            <svg className="w-5 h-5 text-blue-500 shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />
            </svg>
            <div>
              <p className="font-medium text-gray-900 group-hover:text-blue-700 text-sm">Agent</p>
              <p className="text-xs text-gray-500 mt-0.5">What the agent collects, how it works, and version details.</p>
            </div>
          </Link>

          <Link href="/docs/settings" className="group flex items-start gap-3 rounded-lg border border-gray-200 p-4 hover:border-blue-300 hover:bg-blue-50 transition-colors">
            <svg className="w-5 h-5 text-blue-500 shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
            </svg>
            <div>
              <p className="font-medium text-gray-900 group-hover:text-blue-700 text-sm">Settings</p>
              <p className="text-xs text-gray-500 mt-0.5">Tenant configuration, diagnostics, notifications, and data retention.</p>
            </div>
          </Link>

          <Link href="/docs/gather-rules" className="group flex items-start gap-3 rounded-lg border border-gray-200 p-4 hover:border-blue-300 hover:bg-blue-50 transition-colors">
            <svg className="w-5 h-5 text-blue-500 shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
            </svg>
            <div>
              <p className="font-medium text-gray-900 group-hover:text-blue-700 text-sm">Gather Rules</p>
              <p className="text-xs text-gray-500 mt-0.5">Run custom diagnostic commands on devices and collect output centrally.</p>
            </div>
          </Link>

          <Link href="/docs/analyze-rules" className="group flex items-start gap-3 rounded-lg border border-gray-200 p-4 hover:border-blue-300 hover:bg-blue-50 transition-colors">
            <svg className="w-5 h-5 text-blue-500 shrink-0 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.663 17h4.673M12 3v1m6.364 1.636l-.707.707M21 12h-1M4 12H3m3.343-5.657l-.707-.707m2.828 9.9a5 5 0 117.072 0l-.548.547A3.374 3.374 0 0014 18.469V19a2 2 0 11-4 0v-.531c0-.895-.356-1.754-.988-2.386l-.548-.547z" />
            </svg>
            <div>
              <p className="font-medium text-gray-900 group-hover:text-blue-700 text-sm">Analyze Rules</p>
              <p className="text-xs text-gray-500 mt-0.5">Automatically flag failure patterns and misconfigurations in enrollment sessions.</p>
            </div>
          </Link>

        </div>
      </div>

      {/* Community Analyze Rules */}
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
