export function SectionImeLogPatterns() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8 space-y-8">
      <div className="flex items-center space-x-3 mb-2">
        <svg className="w-8 h-8 text-violet-500 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M17.25 6.75L22.5 12l-5.25 5.25m-10.5 0L1.5 12l5.25-5.25m7.5-3l-4.5 16.5" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">IME Log Patterns</h2>
      </div>

      <p className="text-gray-700 leading-relaxed">
        IME Log Patterns are <strong>regular expressions (regex)</strong> that the Autopilot Monitor agent uses to
        parse the <strong>Intune Management Extension (IME) log file</strong> in real time. Each line of the IME log is matched
        against the active patterns — when a regex matches, the agent extracts data via <strong>named capture groups</strong> and
        fires the corresponding <strong>action</strong>, producing a structured event that appears in the session timeline.
      </p>

      <div className="p-4 bg-blue-50 border border-blue-200 rounded-lg text-sm text-blue-900">
        <p className="font-semibold mb-1">Why regex?</p>
        <p>The IME log is a plain-text file with no structured format. Regex patterns allow the agent to reliably
        extract information from free-form log lines — app download progress, install status changes, ESP phase transitions,
        and more — without depending on a specific log format version.</p>
      </div>

      {/* ── How it works ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">How It Works</h3>

      <div className="space-y-3 text-sm text-gray-700">
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <div className="border border-gray-200 rounded-lg p-4">
            <p className="font-semibold text-gray-900 mb-1">1. Pattern Matching</p>
            <p className="text-xs text-gray-600">The agent reads the IME log line by line. Each line is tested against
            all active patterns whose <strong>category</strong> applies to the current enrollment phase.</p>
          </div>
          <div className="border border-gray-200 rounded-lg p-4">
            <p className="font-semibold text-gray-900 mb-1">2. Data Extraction</p>
            <p className="text-xs text-gray-600">When a regex matches, <strong>named capture groups</strong> (e.g.{" "}
            <code className="bg-gray-100 px-1 rounded">{`(?<appId>...)`}</code>) extract values from the log line
            and pass them to the action handler.</p>
          </div>
          <div className="border border-gray-200 rounded-lg p-4">
            <p className="font-semibold text-gray-900 mb-1">3. Event Generation</p>
            <p className="text-xs text-gray-600">The action handler processes the extracted data and emits a
            <strong> structured event</strong> — for example an app state change, an ESP phase transition,
            or an error detection.</p>
          </div>
        </div>
      </div>

      {/* ── Pattern Structure ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Pattern Structure</h3>

      <p className="text-gray-700 text-sm">
        Each pattern is a JSON object with the following fields:
      </p>

      <div className="border border-gray-200 rounded-lg overflow-hidden">
        <table className="w-full text-xs">
          <thead className="bg-gray-50 border-b border-gray-200">
            <tr>
              <th className="px-4 py-2 text-left font-semibold text-gray-700">Field</th>
              <th className="px-4 py-2 text-left font-semibold text-gray-700">Description</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            <tr><td className="px-4 py-2 font-mono">patternId</td><td className="px-4 py-2 text-gray-600">Unique identifier for the pattern (e.g. <code className="bg-gray-100 px-1 rounded">IME-DOWNLOADING</code>).</td></tr>
            <tr><td className="px-4 py-2 font-mono">category</td><td className="px-4 py-2 text-gray-600">When the pattern is active: <code className="bg-gray-100 px-1 rounded">always</code>, <code className="bg-gray-100 px-1 rounded">currentPhase</code>, or <code className="bg-gray-100 px-1 rounded">otherPhases</code>.</td></tr>
            <tr><td className="px-4 py-2 font-mono">pattern</td><td className="px-4 py-2 text-gray-600">The regex (C# syntax) applied to each log line. Uses named capture groups to extract values.</td></tr>
            <tr><td className="px-4 py-2 font-mono">action</td><td className="px-4 py-2 text-gray-600">The handler that processes the match (e.g. <code className="bg-gray-100 px-1 rounded">updateStateDownloading</code>).</td></tr>
            <tr><td className="px-4 py-2 font-mono">description</td><td className="px-4 py-2 text-gray-600">Human-readable description of what the pattern detects.</td></tr>
            <tr><td className="px-4 py-2 font-mono">enabled</td><td className="px-4 py-2 text-gray-600">Whether the pattern is active. Disabled patterns are skipped during log parsing.</td></tr>
            <tr><td className="px-4 py-2 font-mono">parameters</td><td className="px-4 py-2 text-gray-600">Optional key-value pairs passed to the action handler for additional configuration.</td></tr>
          </tbody>
        </table>
      </div>

      {/* ── Categories ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Categories</h3>

      <p className="text-gray-700 text-sm">
        Categories control <strong>when</strong> a pattern is evaluated relative to the current ESP phase:
      </p>

      <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
        <div className="border-l-4 border-emerald-400 bg-emerald-50 rounded-r-lg p-4">
          <p className="font-semibold text-sm text-emerald-900 mb-1">always</p>
          <p className="text-xs text-emerald-800">Evaluated on <strong>every log line</strong>, regardless of the current phase. Used for universal signals like agent version detection, IME restarts, or enrollment completion.</p>
        </div>
        <div className="border-l-4 border-blue-400 bg-blue-50 rounded-r-lg p-4">
          <p className="font-semibold text-sm text-blue-900 mb-1">currentPhase</p>
          <p className="text-xs text-blue-800">Only evaluated during the <strong>active ESP phase</strong>. Used for tracking app downloads, installs, and other progress within the phase the user is currently in.</p>
        </div>
        <div className="border-l-4 border-purple-400 bg-purple-50 rounded-r-lg p-4">
          <p className="font-semibold text-sm text-purple-900 mb-1">otherPhases</p>
          <p className="text-xs text-purple-800">Evaluated for <strong>non-active phases</strong>. Used to detect apps that were already completed in a previous phase, so they can be filtered from the current view.</p>
        </div>
      </div>

      {/* ── Actions ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Actions</h3>

      <p className="text-gray-700 text-sm">
        Each pattern specifies an <strong>action</strong> — a handler in the agent that processes the regex match and produces the corresponding event. The action determines what happens when the pattern matches.
      </p>

      <div className="border border-gray-200 rounded-lg overflow-hidden">
        <table className="w-full text-xs">
          <thead className="bg-gray-50 border-b border-gray-200">
            <tr>
              <th className="px-4 py-2 text-left font-semibold text-gray-700">Action</th>
              <th className="px-4 py-2 text-left font-semibold text-gray-700">Purpose</th>
              <th className="px-4 py-2 text-left font-semibold text-gray-700">Capture Groups</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            <tr><td className="px-4 py-2 font-mono">imeAgentVersion</td><td className="px-4 py-2 text-gray-600">Detect IME agent version</td><td className="px-4 py-2 font-mono text-gray-500">agentVersion</td></tr>
            <tr><td className="px-4 py-2 font-mono">imeStarted</td><td className="px-4 py-2 text-gray-600">IME agent started</td><td className="px-4 py-2 text-gray-500">—</td></tr>
            <tr><td className="px-4 py-2 font-mono">espPhaseDetected</td><td className="px-4 py-2 text-gray-600">ESP phase transition</td><td className="px-4 py-2 font-mono text-gray-500">espPhase</td></tr>
            <tr><td className="px-4 py-2 font-mono">policiesDiscovered</td><td className="px-4 py-2 text-gray-600">App policies JSON found</td><td className="px-4 py-2 font-mono text-gray-500">policies</td></tr>
            <tr><td className="px-4 py-2 font-mono">setCurrentApp</td><td className="px-4 py-2 text-gray-600">Set current app being processed</td><td className="px-4 py-2 font-mono text-gray-500">id</td></tr>
            <tr><td className="px-4 py-2 font-mono">updateStateDownloading</td><td className="px-4 py-2 text-gray-600">App download progress</td><td className="px-4 py-2 font-mono text-gray-500">bytes, ofbytes</td></tr>
            <tr><td className="px-4 py-2 font-mono">updateStateInstalling</td><td className="px-4 py-2 text-gray-600">App installation started</td><td className="px-4 py-2 text-gray-500">—</td></tr>
            <tr><td className="px-4 py-2 font-mono">updateStateInstalled</td><td className="px-4 py-2 text-gray-600">App installation completed</td><td className="px-4 py-2 text-gray-500">—</td></tr>
            <tr><td className="px-4 py-2 font-mono">updateStateError</td><td className="px-4 py-2 text-gray-600">App error detected</td><td className="px-4 py-2 text-gray-500">—</td></tr>
            <tr><td className="px-4 py-2 font-mono">updateStateSkipped</td><td className="px-4 py-2 text-gray-600">App skipped</td><td className="px-4 py-2 text-gray-500">—</td></tr>
            <tr><td className="px-4 py-2 font-mono">updateStatePostponed</td><td className="px-4 py-2 text-gray-600">App postponed</td><td className="px-4 py-2 text-gray-500">—</td></tr>
            <tr><td className="px-4 py-2 font-mono">espTrackStatus</td><td className="px-4 py-2 text-gray-600">ESP tracked install status</td><td className="px-4 py-2 font-mono text-gray-500">from, to, id</td></tr>
            <tr><td className="px-4 py-2 font-mono">updateName</td><td className="px-4 py-2 text-gray-600">Update app display name</td><td className="px-4 py-2 font-mono text-gray-500">id, name</td></tr>
            <tr><td className="px-4 py-2 font-mono">updateWin32AppState</td><td className="px-4 py-2 text-gray-600">Win32 app state change</td><td className="px-4 py-2 font-mono text-gray-500">id, state</td></tr>
            <tr><td className="px-4 py-2 font-mono">ignoreCompletedApp</td><td className="px-4 py-2 text-gray-600">App already completed in prior phase</td><td className="px-4 py-2 text-gray-500">—</td></tr>
            <tr><td className="px-4 py-2 font-mono">cancelStuckAndSetCurrent</td><td className="px-4 py-2 text-gray-600">Cancel stuck app, set new current</td><td className="px-4 py-2 font-mono text-gray-500">id</td></tr>
            <tr><td className="px-4 py-2 font-mono">enrollmentCompleted</td><td className="px-4 py-2 text-gray-600">Enrollment completed</td><td className="px-4 py-2 text-gray-500">—</td></tr>
          </tbody>
        </table>
      </div>

      {/* ── Named Capture Groups ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Named Capture Groups</h3>

      <p className="text-gray-700 text-sm">
        Capture groups are the bridge between the regex and the action handler. They use the syntax{" "}
        <code className="bg-gray-100 px-1 rounded">{`(?<name>...)`}</code> to extract specific values from the matched log line.
      </p>

      <div className="p-4 bg-amber-50 border border-amber-200 rounded-lg text-sm text-amber-900">
        <p className="font-semibold mb-1">GUID Placeholder</p>
        <p>Patterns can use the <code className="bg-amber-100 px-1 rounded">{`{GUID}`}</code> placeholder, which the agent
        automatically expands to a standard GUID regex pattern. This avoids repeating the verbose GUID regex in every pattern
        that needs to match application IDs.</p>
      </div>

      {/* ── Examples ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Example Patterns</h3>

      <div className="space-y-5">

        {/* Example 1 */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-violet-50 border-b border-violet-200">
            <p className="font-semibold text-sm text-violet-900">Example 1 — Detect IME Agent Version</p>
            <p className="text-xs text-violet-700 mt-0.5">Category: <strong>always</strong> — matches on every log line</p>
          </div>
          <div className="px-4 py-4 text-xs space-y-2">
            <div className="bg-gray-50 border border-gray-200 rounded px-3 py-2 font-mono space-y-1">
              <p className="text-gray-400">{`// Pattern`}</p>
              <p>{`Agent version is: (?<agentVersion>[\\d.]+)`}</p>
              <p className="text-gray-400 pt-1">{`// Action: imeAgentVersion`}</p>
            </div>
            <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
              <p className="font-semibold text-green-900 mb-1">What happens</p>
              <p className="text-green-800">When the IME log contains <code className="bg-green-100 px-1 rounded">Agent version is: 1.83.2405.0001</code>,
              the capture group <code className="bg-green-100 px-1 rounded">agentVersion</code> extracts <code className="bg-green-100 px-1 rounded">1.83.2405.0001</code> and
              the agent records the IME version for the session.</p>
            </div>
          </div>
        </div>

        {/* Example 2 */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-violet-50 border-b border-violet-200">
            <p className="font-semibold text-sm text-violet-900">Example 2 — Track App Download Progress</p>
            <p className="text-xs text-violet-700 mt-0.5">Category: <strong>currentPhase</strong> — only active during the current ESP phase</p>
          </div>
          <div className="px-4 py-4 text-xs space-y-2">
            <div className="bg-gray-50 border border-gray-200 rounded px-3 py-2 font-mono space-y-1 overflow-x-auto">
              <p className="text-gray-400">{`// Pattern`}</p>
              <p className="whitespace-nowrap">{`\\[StatusService\\] Downloading app \\(id = {GUID}.*?\\) via (?<tech>\\w+), bytes (?<bytes>\\w+)/(?<ofbytes>\\w+) for user`}</p>
              <p className="text-gray-400 pt-1">{`// Action: updateStateDownloading`}</p>
            </div>
            <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
              <p className="font-semibold text-green-900 mb-1">What happens</p>
              <p className="text-green-800">Extracts the download technology (<code className="bg-green-100 px-1 rounded">tech</code>: DO or CDN),
              bytes downloaded (<code className="bg-green-100 px-1 rounded">bytes</code>), and total size (<code className="bg-green-100 px-1 rounded">ofbytes</code>).
              The agent updates the app state to &quot;downloading&quot; with real-time progress.</p>
            </div>
          </div>
        </div>

        {/* Example 3 */}
        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-2.5 bg-violet-50 border-b border-violet-200">
            <p className="font-semibold text-sm text-violet-900">Example 3 — ESP Phase Transition</p>
            <p className="text-xs text-violet-700 mt-0.5">Category: <strong>always</strong> — critical for tracking enrollment progress</p>
          </div>
          <div className="px-4 py-4 text-xs space-y-2">
            <div className="bg-gray-50 border border-gray-200 rounded px-3 py-2 font-mono space-y-1">
              <p className="text-gray-400">{`// Pattern`}</p>
              <p>{`\\[Win32App\\] (?:In|The) EspPhase: (?<espPhase>\\w+)`}</p>
              <p className="text-gray-400 pt-1">{`// Action: espPhaseDetected`}</p>
            </div>
            <div className="p-3 bg-green-50 border border-green-200 rounded-lg">
              <p className="font-semibold text-green-900 mb-1">What happens</p>
              <p className="text-green-800">Detects when the IME transitions between ESP phases (e.g.{" "}
              <code className="bg-green-100 px-1 rounded">DeviceSetup</code>, <code className="bg-green-100 px-1 rounded">AccountSetup</code>).
              This drives the phase-aware filtering of <code className="bg-green-100 px-1 rounded">currentPhase</code> and{" "}
              <code className="bg-green-100 px-1 rounded">otherPhases</code> patterns.</p>
            </div>
          </div>
        </div>

      </div>

      {/* ── Contributing Patterns ── */}
      <h3 className="text-xl font-semibold text-gray-900 pt-4 border-t border-gray-200">Contributing Patterns</h3>

      <div className="space-y-3 text-sm text-gray-700">
        <p>Microsoft occasionally changes log formats in the Intune Management Extension. When this happens,
        existing patterns may stop matching. If you notice that a pattern no longer fires for log lines it should
        match, you can help by <strong>submitting a pull request</strong> on GitHub with an updated or new pattern.</p>

        <div className="p-4 bg-blue-50 border border-blue-200 rounded-lg text-sm text-blue-900">
          <p className="font-semibold mb-1">Debugging with the IME Pattern Match Log</p>
          <p>If you suspect a pattern is no longer matching, enable the <strong>IME Pattern Match Log</strong> in the{" "}
          <a href="/docs/settings" className="text-blue-700 hover:text-blue-900 underline">Settings</a> page.
          When enabled, the agent writes every matched IME log line to a local file at{" "}
          <code className="bg-blue-100 px-1 rounded">{`%ProgramData%\\AutopilotMonitor\\Logs\\ime_pattern_matches.log`}</code>.
          This lets you see exactly which patterns are firing and which log lines are going unmatched —
          making it much easier to identify what changed in the log format and adjust the regex accordingly.</p>
        </div>

        <div className="border border-gray-200 rounded-lg overflow-hidden">
          <div className="px-4 py-3 bg-violet-50 border-b border-violet-200">
            <p className="font-semibold text-sm text-violet-900">How to contribute</p>
          </div>
          <div className="px-4 py-4 space-y-3 text-sm text-gray-700">
            <ol className="list-decimal list-inside space-y-2">
              <li>Enable the <strong>IME Pattern Match Log</strong> in{" "}
                <a href="/docs/settings" className="text-violet-600 hover:text-violet-800 underline">Settings</a>{" "}
                and run an enrollment to capture which patterns match and which don&apos;t.</li>
              <li>Open the <strong>IME Log Patterns</strong> page in the portal and find the pattern that no longer matches.</li>
              <li>Use <strong>View as JSON</strong> to see the full pattern definition — this makes it easy to copy the current state.</li>
              <li>Compare the regex with the actual log lines from the match log to identify what changed.</li>
              <li>Submit a <strong>pull request</strong> on the{" "}
                <a href="https://github.com/nickkieselbach/Autopilot-Monitor" target="_blank" rel="noopener noreferrer"
                   className="text-violet-600 hover:text-violet-800 underline">Autopilot Monitor GitHub repository</a>{" "}
                with your updated pattern JSON. Pattern files are located in the <code className="bg-gray-100 px-1 rounded">rules/ime-log-patterns/</code> directory.</li>
              <li>The team reviews the PR, validates the regex against known log samples, and merges it if it looks good.</li>
            </ol>
          </div>
        </div>
      </div>

      {/* ── Portal ── */}
      <div className="p-4 bg-gray-50 border border-gray-200 rounded-lg text-sm text-gray-700">
        <p className="font-semibold text-gray-900 mb-1">IME Log Patterns page</p>
        <p>Use the <strong>IME Log Patterns</strong> page in the portal to browse and filter all active patterns.
        The page shows each pattern with its regex, action, category, and description. Use the <strong>View as JSON</strong> toggle
        to see the full pattern definition — especially useful when preparing a pull request with updated or new patterns.</p>
      </div>

    </section>
  );
}
