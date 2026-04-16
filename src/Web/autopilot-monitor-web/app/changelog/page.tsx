"use client";

import { PublicPageHeader } from "../../components/PublicPageHeader";

export default function ChangelogPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicPageHeader title="Changelog" />
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        <div className="bg-white rounded-2xl shadow-sm border border-gray-200 p-8 sm:p-10">

          {/* Intro */}
          <div className="mb-10 pb-8 border-b border-gray-100">
            <p className="text-gray-600 leading-relaxed">
              This changelog tracks significant platform changes during Private Preview —
              architecture updates, data flow changes, and anything else that might briefly
              affect the UI or monitoring data. If something looks off, check here first.
              A recent entry might explain it.
            </p>
            <p className="mt-3 text-gray-600 leading-relaxed">
              Found a bug or want to give feedback?{" "}
              <a
                href="https://github.com/okieselbach/Autopilot-Monitor/issues"
                target="_blank"
                rel="noopener noreferrer"
                className="text-blue-600 hover:text-blue-800 underline font-medium"
              >
                Open a GitHub Issue
              </a>
              {" "}— it helps more than you might think.
            </p>
          </div>

          {/* Entries — newest first */}
          <div className="space-y-10">

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-04-16 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Completion state machine, SLA &amp; App Health dashboards, Ops Alerts, and Device Preparation groundwork
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Session completion state machine</span> — The agent soon uses a dedicated <code>CompletionStateMachine</code> that combines multiple signals (ESP final exit, Hello, Desktop arrival) to decide when an enrollment is truly done. This fixes several cases where WhiteGlove and Hybrid Join sessions were misclassified or never marked complete.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">SLA tracking dashboard</span> — New SLA monitoring page with per-tenant configuration and notification support when SLAs are breached.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">App Health dashboard</span> — New global view of app deployment health with scoped drill-downs and a configurable column picker.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Ops Events &amp; Ops Alerts</span> — Operational event log plus admin alerts for backend health, blob storage, runaway sessions, and excessive event counts per session.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent emergency / distress channel</span> — A separate low-overhead channel so the agent can still report critical errors when the normal telemetry path is impaired.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Enhanced analyze rule engine</span> — New <code>in</code> / <code>not_in</code> compare operators, <code>MarkSessionAsFailed</code> action, template variables, per-rule stats card, and a new ESP certificate-error analyze rule (<code>ANALYZE-ESP-002</code>).</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Delivery Optimization</span> — OS-level DO collector, P2P totals in download progress, and DO usage stats in the geographic drill-down.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Vulnerability matching improvements</span> — Fuzzy (Jaro-Winkler) CPE matching, confidence levels, data freshness indicators, CVE mapping column in the vulnerability report, and WhiteGlove sessions now also get a vulnerability report.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Device Preparation (WDP v2) groundwork</span> — The agent now distinguishes Classic vs v2 Autopilot flow, and a device-association validator was added on the backend. Device Preparation support is still in active validation.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">IME version history</span> — Intune Management Extension version history is tracked and surfaced via MCP; agents running on outdated IME versions trigger an alert.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Known Issues page</span> — Dedicated docs page for ongoing issues (replaces the inline list that used to live in this changelog).</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">MCP server</span> — Stateless endpoint, tools split into domain modules, new ops-events tool, tool-call telemetry, improved semantic + keyword search, and an integration test suite.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Security hardening</span> — Centralized tenant-isolation middleware, OData sanitizer, hardened agent config endpoint, cross-tenant fallback fixes, session-aware auto-unblock, and additional request-size / integrity guards on the self-update path.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Web performance &amp; refactor</span> — Lazy session loading, response compression, more parallel fetches, and a large internal restructuring of the web app into hooks and utils for easier maintenance.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Bugfixes &amp; UX polish</span> — Quick search, bootstrap scripts, webhook notifications, WhiteGlove timeline rendering, phase-timeline regressions, report upload size, summary dialog launch fallback, NTP / timezone defaults, and many more small fixes.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-30 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Updated bootstrapper script, agent crash detection, and quick search
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-orange-500 flex-shrink-0">⚠</span>
                  <span><span className="font-medium text-gray-800">Updated bootstrapper script (action recommended)</span> — The bootstrapper script (<code>Install-AutopilotMonitor.ps1</code>) now uses SHA-256 integrity verification for agent downloads instead of MD5. If you deployed the script via Intune, it is recommended to replace it with the latest version from the repository for improved security.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent crash detection</span> — The agent now detects and reports unexpected crashes with automatic recovery. Platform-level metrics (CPU, memory, disk) are collected alongside enrollment events for better diagnostics.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Global quick search</span> — A fuzzy search across sessions, devices, and users is now available from the navigation bar for fast lookups.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Rate limiting</span> — Per-user request rate limiting protects the backend from excessive API usage.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Bugfixes</span> — Vulnerability report rescan persistence, orphaned session handling, timezone parsing, and NTP clock-skew warnings improved.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-26 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Software inventory &amp; vulnerability analysis, new agent signals, and settings overhaul
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Software Inventory &amp; Vulnerability Analysis</span> — The agent now discovers installed software across Registry, WMI, AppX/MSIX, and per-user sources and correlates it against NVD and CISA KEV databases. The dashboard shows a vulnerability report with CVSS scores and severity levels. Includes 240+ curated CPE mappings and strict AppX whitelist filtering.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">SecureBoot &amp; time sync</span> — The agent collects SecureBoot certificate details (with a new analyze rule), auto-detects the timezone, and checks NTP offset to catch time-related enrollment failures.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Security hardening</span> — Request size limits on all submission endpoints and symlink detection in diagnostic paths guard against DoS and path-traversal attacks.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Settings reorganization</span> — The sidebar now uses expandable sections for a cleaner navigation. Tenant settings were restructured and consolidated.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">OOBE Config viewer</span> — A modal dialog decodes the OOBE configuration bitmask, showing each bit flag with description and confidence level, and detects the enrollment profile type.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">FAQ page</span> — New Docs section covering supported scenarios, deployment, agent capabilities, and troubleshooting.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-19 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Navigation overhaul, session architecture, new agent signals, and community rules
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Unified sidebar</span> — The entire navigation has been redesigned with a global sidebar. The old top nav is gone; settings and admin areas now have their own sidebar sections. Mobile layout also reworked.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Session index table</span> — Session storage has been fundamentally re-architected for better scalability and reliability.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">New agent signals</span> — The agent now reports <code>agent_shutdown</code> (clean shutdown), <code>hardware_spec</code> (hardware inventory at enrollment), network interface changes, and clock skew deviations for better diagnostics.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Self-deploying mode detection</span> — The agent now automatically detects self-deploying scenarios and tracks the enrollment finalization process with dedicated events.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Notification providers</span> — The webhook notification system now supports three providers: Teams Legacy, Teams Workflow, and Slack — selectable per tenant.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Community rules</span> — A community rule set for gather and analyze rules has been added. Rules now have a JSON view, severity override, and centralized guardrails. New local admin analyze rule included.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Geographic drill-down</span> — The geographic performance view now supports drill-down to region and country level.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Mark as success</span> — Sessions can now be manually marked as successful, e.g. after manually resolved enrollments.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Feedback system</span> — An integrated feedback system with admin management allows direct feedback from within the portal.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Tenant settings UX</span> — The central save button in tenant settings has been replaced with individual section save buttons. A new Unrestricted Mode option disables most guardrails per tenant request.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Docs expanded</span> — New general documentation section, IME pattern explanation, and a public sites sidebar added.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Backend reliability</span> — Improved cache invalidation and retry logic for transient errors.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-10 - 12:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Platform Update
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Security architecture, session timeline improvements, and new agent capabilities
              </h2>
              <ul className="space-y-2 text-sm text-gray-600 leading-relaxed list-none">
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Role-based access control</span> — Admin and Operator roles with role management in Settings. API authorization and policy enforcement middleware ensure proper access control across all endpoints.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent self-update</span> — Agents can now update themselves automatically, ensuring outdated versions in the field get replaced without manual intervention.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Bootstrap sessions</span> — New bootstrap session flow with explicit token enablement for initial device onboarding. (support for bootstrap tokens enabled by request)</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Raw event timeline</span> — A new raw view of the event timeline with full search support, useful for deep-dive troubleshooting.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Enrollment summary dialog</span> — Optional summary dialog shown at the end of enrollment, with event timeline search and clickable phases in the phase tracker.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Original ESP tracking</span> — The agent now tracks the original ESP provisioning status to catch non-IME errors such as certificate failures.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Analyze &amp; gather rules</span> — Added negative compare operators for analyze rules, XML and JSON gather options, and a built-in &ldquo;old OS version&rdquo; warning rule.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Email notifications</span> — Email notification (Welcome and instructions) for Joining the Private Preview.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Agent version management</span> — Block specific agent versions from connecting, along with expanded data retention configuration options.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Install progress</span> — The agent install progress page now shows download and install phases with elapsed time.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">TPM info collection</span> — TPM details are now collected at enrollment time for improved hardware diagnostics.</span>
                </li>
                <li className="flex gap-2">
                  <span className="text-gray-400 flex-shrink-0">•</span>
                  <span><span className="font-medium text-gray-800">Firewall compatibility</span> — The agent now sends a dedicated User-Agent header to simplify firewall allowlisting.</span>
                </li>
              </ul>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-03-01 - 16:00 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Pre-Provisioning (WhiteGlove)
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Ongoing improvements to Pre-Provisioning support (still testing)
              </h2>
              <p className="text-sm text-gray-600 leading-relaxed">
                I'm continuously improving support for Pre-Provisioning (White Glove) scenarios.
                The session timeline should now better reflect the provisioning process better, and I'm 
                working on improving the accuracy of event categorization and timing for these sessions.
                If you are using Pre-Provisioning and notice any discrepancies in the timeline or data, 
                please share your Feedback with me via GitHub Issues. Your feedback is invaluable in 
                helping me enhance support for these scenarios.
                Expect a "Report Session" button in the timeline view soon to make sharing feedback and logs easier!
              </p>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-02-27 - 21:38 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Features
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Configurable Diagnostic Package, Gather Rule Examples, Updated Docs
              </h2>
              <p className="text-sm text-gray-600 leading-relaxed">
                The configurable diagnostic package allows for more flexible data collection and analysis.
                Gather rule examples have been added to help users understand how to create their own rules.
                Documentation has been updated to reflect these changes and provide guidance on using the features.
              </p>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-02-27 - 14:38 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Architecture
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                First implementation of Pre-Provisioning support incl. session timeline visualization
              </h2>
              <p className="text-sm text-gray-600 leading-relaxed">
                The session timeline now also supports sessions that started with Pre-Provisioning
                (aka White Glove) — including the provisioning process itself. This is a first
                implementation and only tested with a very basic scenario, so if you use
                Pre-Provisioning and see anything that looks off in the timeline, please check
                the logs and share them via GitHub Issues.
              </p>
            </div>

            <div>
              <div className="flex items-center gap-3 mb-3">
                <span className="text-xs font-mono font-semibold text-gray-400 uppercase tracking-wider">
                  2026-02-26 - 10:15 CET
                </span>
                <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-blue-100 text-blue-800">
                  Architecture
                </span>
              </div>
              <h2 className="text-base font-semibold text-gray-900 mb-2">
                Reworked real-time event delivery and session timeline processing
              </h2>
              <p className="text-sm text-gray-600 leading-relaxed">
                The way live session events reach the dashboard timeline was fundamentally
                reworked. This should make the timeline more reliable and accurate.
              </p>
            </div>

          </div>

        </div>
      </main>
    </div>
  );
}
