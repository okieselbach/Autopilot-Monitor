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
