export function SectionAnalyzeRules() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8 space-y-8">
      <div className="flex items-center space-x-3 mb-2">
        <svg className="w-8 h-8 text-indigo-500 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Analyze Rules</h2>
      </div>

      <p className="text-gray-700 leading-relaxed">
        Analyze Rules define <strong>when and why the agent flags an issue</strong> during enrollment. Each rule evaluates
        events collected during the session and produces a <strong>confidence score</strong>. If the score reaches the configured
        threshold the rule fires, creating a finding that appears in the session timeline and the analysis summary.
      </p>

      <div className="p-4 bg-blue-50 border border-blue-200 rounded-lg text-sm text-blue-900">
        <p className="font-semibold mb-1">How scoring works</p>
        <p>Every rule starts with a <strong>Base Confidence</strong> value (0–100). <strong>Confidence Factors</strong> are optional
        conditions that add or subtract points when matched. The final score is compared to the <strong>Confidence Threshold</strong>.
        The rule fires only if <code className="bg-blue-100 px-1 rounded">base + factors &ge; threshold</code>.</p>
      </div>

      {/* ── Conditions ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Conditions</h3>

      <p className="text-gray-700 text-sm">
        Conditions define <strong>what events or data</strong> the rule looks for. All <em>required</em> conditions must match
        for the rule to proceed to scoring. Optional conditions are used only as confidence factors.
      </p>

      <div className="space-y-4">

        {/* event_type */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
            <p className="font-semibold text-sm text-gray-900">Source: <code className="font-mono">event_type</code></p>
          </div>
          <div className="px-4 py-4 space-y-2 text-sm text-gray-700">
            <p>Checks whether a specific event type was emitted during the session. The most common condition type.</p>
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3 text-xs">
              <div><p className="font-medium text-gray-900">Signal</p><p className="text-gray-500">A label for this condition (e.g., <code className="bg-gray-100 px-1 rounded">app_failed</code>)</p></div>
              <div><p className="font-medium text-gray-900">Event Type</p><p className="text-gray-500">The exact event type to look for (e.g., <code className="bg-gray-100 px-1 rounded">app_install_failed</code>)</p></div>
              <div><p className="font-medium text-gray-900">Operator</p><p className="text-gray-500"><code className="bg-gray-100 px-1 rounded">exists</code> — the event occurred at all</p></div>
            </div>
            <div className="p-3 bg-green-50 border border-green-200 rounded-lg text-xs">
              <p className="font-semibold text-green-900 mb-1">Example — detect any app installation failure</p>
              <p>Signal: <strong>app_failed</strong> · Source: <strong>event_type</strong> · Event Type: <strong>app_install_failed</strong> · Operator: <strong>exists</strong></p>
            </div>
          </div>
        </div>

        {/* event_data */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
            <p className="font-semibold text-sm text-gray-900">Source: <code className="font-mono">event_data</code></p>
          </div>
          <div className="px-4 py-4 space-y-2 text-sm text-gray-700">
            <p>Inspects a field inside the <code className="bg-gray-100 px-1 rounded">data</code> payload of a specific event type. Use this to match on values like error codes or app names.</p>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
              <div>
                <p className="font-medium text-gray-900">Operators</p>
                <ul className="mt-1 space-y-0.5 text-gray-500">
                  <li><code className="bg-gray-100 px-1 rounded">equals</code> — exact match</li>
                  <li><code className="bg-gray-100 px-1 rounded">contains</code> — substring match</li>
                  <li><code className="bg-gray-100 px-1 rounded">regex</code> — regular expression match</li>
                  <li><code className="bg-gray-100 px-1 rounded">gt / lt / gte / lte</code> — numeric comparisons</li>
                  <li><code className="bg-gray-100 px-1 rounded">exists</code> — field is present with any value</li>
                </ul>
              </div>
              <div>
                <p className="font-medium text-gray-900">Common data fields</p>
                <ul className="mt-1 space-y-0.5 text-gray-500">
                  <li><code className="bg-gray-100 px-1 rounded">errorCode</code> — Win32 / HTTP error code</li>
                  <li><code className="bg-gray-100 px-1 rounded">appName</code> — application name</li>
                  <li><code className="bg-gray-100 px-1 rounded">exitCode</code> — process exit code</li>
                  <li><code className="bg-gray-100 px-1 rounded">phase</code> — enrollment phase name</li>
                </ul>
              </div>
            </div>
            <div className="p-3 bg-green-50 border border-green-200 rounded-lg text-xs">
              <p className="font-semibold text-green-900 mb-1">Example — detect error code 0x80070002</p>
              <p>Source: <strong>event_data</strong> · Event Type: <strong>app_install_failed</strong> · Data Field: <strong>errorCode</strong> · Operator: <strong>equals</strong> · Value: <strong>0x80070002</strong></p>
            </div>
          </div>
        </div>

        {/* event_count */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
            <p className="font-semibold text-sm text-gray-900">Source: <code className="font-mono">event_count</code></p>
          </div>
          <div className="px-4 py-4 space-y-2 text-sm text-gray-700">
            <p>Checks how many times a specific event type occurred. Use with <code className="bg-gray-100 px-1 rounded">count_gte</code> or <code className="bg-gray-100 px-1 rounded">gt</code> to detect repeated failures.</p>
            <div className="p-3 bg-green-50 border border-green-200 rounded-lg text-xs">
              <p className="font-semibold text-green-900 mb-1">Example — detect 3 or more app failures</p>
              <p>Source: <strong>event_count</strong> · Event Type: <strong>app_install_failed</strong> · Operator: <strong>count_gte</strong> · Value: <strong>3</strong></p>
            </div>
          </div>
        </div>

        {/* phase_duration */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
            <p className="font-semibold text-sm text-gray-900">Source: <code className="font-mono">phase_duration</code></p>
          </div>
          <div className="px-4 py-4 space-y-2 text-sm text-gray-700">
            <p>Measures how long a specific enrollment phase took (in seconds). Use with <code className="bg-gray-100 px-1 rounded">gt</code> or <code className="bg-gray-100 px-1 rounded">gte</code> to detect phases that run too long.</p>
            <div className="p-3 bg-green-50 border border-green-200 rounded-lg text-xs">
              <p className="font-semibold text-green-900 mb-1">Example — detect App Installation phase &gt; 1 hour</p>
              <p>Source: <strong>phase_duration</strong> · Data Field: <strong>AppsDevice</strong> · Operator: <strong>gt</strong> · Value: <strong>3600</strong></p>
            </div>
          </div>
        </div>

        {/* event_correlation */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-gray-100 border-b border-gray-200">
            <p className="font-semibold text-sm text-gray-900">Source: <code className="font-mono">event_correlation</code></p>
          </div>
          <div className="px-4 py-4 space-y-2 text-sm text-gray-700">
            <p>Joins two event types on a shared field within a time window. Useful for detecting causal relationships — e.g., a network error that precedes a download failure.</p>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
              <div>
                <p className="font-medium text-gray-900">Extra fields</p>
                <ul className="mt-1 space-y-0.5 text-gray-500">
                  <li><code className="bg-gray-100 px-1 rounded">Correlate Event Type</code> — the second event to join with</li>
                  <li><code className="bg-gray-100 px-1 rounded">Join Field</code> — field that must match on both events</li>
                  <li><code className="bg-gray-100 px-1 rounded">Time Window (s)</code> — max seconds between the two events</li>
                  <li><code className="bg-gray-100 px-1 rounded">Event A Filter</code> — optional filter on the first event</li>
                </ul>
              </div>
              <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
                <p className="font-semibold text-green-900 mb-1">Example — network error before download failure</p>
                <p className="text-gray-700">Event Type A: <strong>network_error</strong></p>
                <p className="text-gray-700">Correlate Event Type: <strong>app_download_failed</strong></p>
                <p className="text-gray-700">Join Field: <strong>sessionId</strong></p>
                <p className="text-gray-700">Time Window: <strong>120</strong> seconds</p>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* ── Trigger Types ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Trigger Types</h3>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div className="border border-gray-200 rounded-lg p-4">
          <p className="font-semibold text-gray-900 text-sm mb-1">Single</p>
          <p className="text-xs text-gray-600">Evaluates each matching event independently. The rule fires once for every event that satisfies all required conditions.</p>
        </div>
        <div className="border border-gray-200 rounded-lg p-4">
          <p className="font-semibold text-gray-900 text-sm mb-1">Correlation</p>
          <p className="text-xs text-gray-600">Uses <code className="bg-gray-100 px-1 rounded">event_correlation</code> conditions to join two event streams. Fires when matching pairs are found within the time window.</p>
        </div>
      </div>

      {/* ── Confidence ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Confidence Scoring</h3>

      <div className="space-y-3 text-sm text-gray-700">
        <p>The confidence model lets rules express <em>uncertainty</em>. A rule can fire with lower confidence when only partial evidence is present, and higher confidence when multiple corroborating signals align.</p>
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <table className="w-full text-xs">
            <thead className="bg-gray-50 border-b border-gray-200">
              <tr>
                <th className="px-4 py-2 text-left font-semibold text-gray-700">Field</th>
                <th className="px-4 py-2 text-left font-semibold text-gray-700">Description</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              <tr><td className="px-4 py-2 font-mono">baseConfidence</td><td className="px-4 py-2 text-gray-600">Starting score (0–100) when all required conditions match.</td></tr>
              <tr><td className="px-4 py-2 font-mono">confidenceThreshold</td><td className="px-4 py-2 text-gray-600">Minimum score needed to fire the rule.</td></tr>
              <tr><td className="px-4 py-2 font-mono">confidenceFactors</td><td className="px-4 py-2 text-gray-600">Optional conditions that add or subtract points from the base score when matched.</td></tr>
            </tbody>
          </table>
        </div>
        <div className="p-3 bg-amber-50 border border-amber-200 rounded-lg text-xs text-amber-900">
          <strong>Tip:</strong> Start with <code className="bg-amber-100 px-1 rounded">baseConfidence: 50</code> and <code className="bg-amber-100 px-1 rounded">confidenceThreshold: 40</code>. Add confidence factors for additional signals (e.g., +20 if a specific error code matches, -10 if the app subsequently succeeded).
        </div>
      </div>

      {/* ── Full Rule Examples ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Example Rules</h3>

      <div className="space-y-5">

        {/* Example 1 */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">Example 1 — Repeated App Install Failure</p>
            <p className="text-xs text-indigo-700 mt-0.5">Detect when three or more app installations fail in the same session</p>
          </div>
          <div className="px-4 py-4 text-xs space-y-2">
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
              <div><p className="text-gray-500">Category</p><p className="font-medium">apps</p></div>
              <div><p className="text-gray-500">Severity</p><p className="font-medium">high</p></div>
              <div><p className="text-gray-500">Base Confidence</p><p className="font-medium">60</p></div>
              <div><p className="text-gray-500">Threshold</p><p className="font-medium">40</p></div>
            </div>
            <div className="bg-gray-50 border border-gray-200 rounded px-3 py-2 font-mono space-y-1">
              <p className="text-gray-400">{`// Condition 1 (required)`}</p>
              <p>{`source: "event_count"  eventType: "app_install_failed"  operator: "count_gte"  value: "3"`}</p>
              <p className="text-gray-400 pt-1">{`// Confidence Factor (+20 if error code indicates timeout)`}</p>
              <p>{`signal: "timeout_code"  condition: "errorCode contains 0x800704C7"  weight: 20`}</p>
            </div>
          </div>
        </div>

        {/* Example 2 */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">Example 2 — App Installation Phase Too Long</p>
            <p className="text-xs text-indigo-700 mt-0.5">Fire when the Apps (Device) ESP phase exceeds 45 minutes</p>
          </div>
          <div className="px-4 py-4 text-xs space-y-2">
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
              <div><p className="text-gray-500">Category</p><p className="font-medium">esp</p></div>
              <div><p className="text-gray-500">Severity</p><p className="font-medium">warning</p></div>
              <div><p className="text-gray-500">Base Confidence</p><p className="font-medium">70</p></div>
              <div><p className="text-gray-500">Threshold</p><p className="font-medium">50</p></div>
            </div>
            <div className="bg-gray-50 border border-gray-200 rounded px-3 py-2 font-mono space-y-1">
              <p className="text-gray-400">{`// Condition (required)`}</p>
              <p>{`source: "phase_duration"  dataField: "AppsDevice"  operator: "gt"  value: "2700"`}</p>
            </div>
          </div>
        </div>

        {/* Example 3 */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">Example 3 — Network Error Preceding Download Failure</p>
            <p className="text-xs text-indigo-700 mt-0.5">Correlate a network drop with a subsequent app download failure within 2 minutes</p>
          </div>
          <div className="px-4 py-4 text-xs space-y-2">
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
              <div><p className="text-gray-500">Trigger</p><p className="font-medium">correlation</p></div>
              <div><p className="text-gray-500">Category</p><p className="font-medium">network</p></div>
              <div><p className="text-gray-500">Base Confidence</p><p className="font-medium">75</p></div>
              <div><p className="text-gray-500">Threshold</p><p className="font-medium">60</p></div>
            </div>
            <div className="bg-gray-50 border border-gray-200 rounded px-3 py-2 font-mono space-y-1">
              <p className="text-gray-400">{`// Correlation condition`}</p>
              <p>{`source: "event_correlation"  eventType: "network_error"`}</p>
              <p>{`correlateEventType: "app_download_failed"  joinField: "sessionId"  timeWindowSeconds: 120`}</p>
            </div>
          </div>
        </div>

        {/* Example 4 */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-indigo-50 border-b border-indigo-200">
            <p className="font-semibold text-sm text-indigo-900">Example 4 — Specific App Blocked by Disk Space</p>
            <p className="text-xs text-indigo-700 mt-0.5">Detect a download stall caused by low disk space for a named application</p>
          </div>
          <div className="px-4 py-4 text-xs space-y-2">
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
              <div><p className="text-gray-500">Category</p><p className="font-medium">apps</p></div>
              <div><p className="text-gray-500">Severity</p><p className="font-medium">critical</p></div>
              <div><p className="text-gray-500">Base Confidence</p><p className="font-medium">55</p></div>
              <div><p className="text-gray-500">Threshold</p><p className="font-medium">40</p></div>
            </div>
            <div className="bg-gray-50 border border-gray-200 rounded px-3 py-2 font-mono space-y-1">
              <p className="text-gray-400">{`// Condition 1 — app failure occurred`}</p>
              <p>{`source: "event_type"  eventType: "app_install_failed"  operator: "exists"`}</p>
              <p className="text-gray-400 pt-1">{`// Condition 2 — error code is disk-full (0x80070070)`}</p>
              <p>{`source: "event_data"  eventType: "app_install_failed"  dataField: "errorCode"  operator: "equals"  value: "0x80070070"`}</p>
              <p className="text-gray-400 pt-1">{`// Confidence Factor (+30 if disk space event also fired)`}</p>
              <p>{`signal: "disk_event"  condition: "event_type disk_space_low exists"  weight: 30`}</p>
            </div>
          </div>
        </div>

      </div>

      {/* ── JSON editing tip ── */}
      <div className="p-4 bg-gray-50 border border-gray-200 rounded-lg text-sm text-gray-700">
        <p className="font-semibold text-gray-900 mb-1">JSON editing</p>
        <p>The Analyze Rules editor supports a <strong>JSON mode</strong> accessible via the Form / JSON toggle in the top-right of the create and edit panels.
        Use JSON mode to author complex rules with multiple conditions, confidence factors, remediation steps, and <code className="bg-gray-100 px-1 rounded">event_correlation</code> properties that go beyond what the form UI exposes.</p>
      </div>

    </section>
  );
}
