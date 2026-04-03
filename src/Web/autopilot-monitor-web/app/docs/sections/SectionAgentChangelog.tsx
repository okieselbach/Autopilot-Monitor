export function SectionAgentChangelog() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Agent Changelog</h2>
      </div>
      <p className="text-gray-600 mb-8">
        User-facing changes to the Autopilot Monitor agent, newest first. Only includes changes that affect agent behavior on the device.
      </p>

      {/* ── April 2026 ───────────────────────────────── */}
      <ChangelogBlock title="April 2026">
        <Li>Vulnerability matching improvements — confidence levels, platform-aware filtering, and exclude patterns for more accurate reports</Li>
        <Li>Vulnerability reports now available during pre-provisioning (White Glove) sessions</Li>
        <Li>More reliable enrollment summary dialog launch with desktop fallback strategy</Li>
        <Li>PowerShell script output is now fully captured in the timeline (multi-line output was previously truncated)</Li>
        <Li>More reliable bootstrap and download handling with improved timeout and rate-limit behavior</Li>
        <Li>Agent reports self-update events so updates are visible in the session timeline</Li>
        <Li>Emergency channel — agent can send distress signals when it detects critical failures</Li>
        <Li>ESP &quot;resumed&quot; event is now only emitted for Hybrid Join scenarios (avoids noise on other paths)</Li>
        <Li>Improved crash recovery — completion state is persisted so the agent can resume correctly after an unexpected restart</Li>
      </ChangelogBlock>

      {/* ── Late March 2026 ──────────────────────────── */}
      <ChangelogBlock title="Late March 2026">
        <Li>Agent crash detection — crashes are automatically detected and reported to the backend</Li>
        <Li>SHA-256 integrity verification for agent downloads (bootstrapper + self-updater verify hash before install)</Li>
        <Li>Reboot tracking — reboots during enrollment are now tracked and visible in the timeline</Li>
        <Li>NTP time sync check with clock skew warning when device time is significantly off</Li>
        <Li>Automatic timezone detection and configuration</Li>
        <Li>SecureBoot certificate collection for security posture reporting</Li>
        <Li>IME process watcher — detects when the Intune Management Extension starts or stops</Li>
        <Li>Network change detection — captures network adapter changes during enrollment</Li>
        <Li>Agent self-update mechanism — outdated agents in the field update themselves automatically</Li>
        <Li>Unrestricted mode option (per-tenant) to disable most guard rails</Li>
        <Li>Notification system reworked — supports Teams (legacy + Workflow), Slack, and custom webhooks</Li>
      </ChangelogBlock>

      {/* ── Mid March 2026 ───────────────────────────── */}
      <ChangelogBlock title="Mid March 2026">
        <Li>Software inventory collection with automatic vulnerability correlation (CVE matching)</Li>
        <Li>Hardware specification event — detailed hardware info collected and reported</Li>
        <Li>Agent shutdown event — clean shutdown is now explicitly tracked</Li>
        <Li>Postponed app detection and handling during enrollment</Li>
        <Li>Self-deploying mode detection and event tracking</Li>
        <Li>Enrollment summary dialog shown on the device after enrollment completes</Li>
        <Li>ESP provisioning status tracking — catches non-IME errors like certificate failures</Li>
        <Li>PowerShell script execution tracking during enrollment</Li>
        <Li>Clock skew detection with geo-location failure reporting</Li>
        <Li>Community analyze rules support</Li>
      </ChangelogBlock>

      {/* ── Early March 2026 ─────────────────────────── */}
      <ChangelogBlock title="Early March 2026">
        <Li>Bootstrap session support — monitoring starts before MDM enrollment (during OOBE)</Li>
        <Li>ESP configuration detection — identifies ESP settings on the device</Li>
        <Li>TPM info collection for device details</Li>
        <Li>Activity-aware idle timeout replaces fixed 4-hour collector limit (default: 15 min idle)</Li>
        <Li>Reliable session end-detection for all deployment scenarios (user-driven, pre-provisioning, hybrid)</Li>
        <Li>Network performance data collection (latency, throughput)</Li>
        <Li>Geographic location support via IP-based lookup</Li>
        <Li>Emergency break — remote kill switch to stop agents</Li>
        <Li>Automatic retry on transient backend errors</Li>
        <Li>Custom User-Agent header for easier firewall allowlisting</Li>
        <Li>ESP state tracking via registry watcher</Li>
        <Li>XML and JSON file gathering in diagnostics</Li>
        <Li>Configurable <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">--await-enrollment</code> parameter for pre-enrollment wait</Li>
      </ChangelogBlock>

      {/* ── Late February 2026 ───────────────────────── */}
      <ChangelogBlock title="Late February 2026">
        <Li>Pre-Provisioning (White Glove) support — full end-to-end monitoring of pre-provisioning sessions</Li>
        <Li>mTLS for all agent-to-backend communication (consolidated endpoints)</Li>
        <Li>Diagnostics SAS URL fetched on-demand — no longer stored on disk</Li>
        <Li>Max collector duration policy (configurable per tenant)</Li>
        <Li>Diagnostics package upload from device</Li>
        <Li>Configurable reboot-on-complete and keep-logfile options via remote config</Li>
        <Li>Configurable diagnostics log paths (global + per-tenant)</Li>
        <Li>Lenovo model detection fix (WMI query)</Li>
      </ChangelogBlock>

      {/* ── Mid February 2026 ────────────────────────── */}
      <ChangelogBlock title="Mid February 2026">
        <Li>Windows Autopilot v2 (Device Preparation) support</Li>
        <Li>GatherRules guard rails — prevents collection of overly broad paths</Li>
        <Li>IME log replay for testing and demos (<code className="text-xs bg-gray-100 px-1 py-0.5 rounded">--replay-log-dir</code>)</Li>
        <Li>Agent state persistence — survives reboots and resumes monitoring</Li>
        <Li>Embedded Intune root + intermediate certificates for chain validation</Li>
        <Li>OS info and boot time collection</Li>
        <Li>Hello screen detection improvements</Li>
        <Li>Download progress tracking</Li>
      </ChangelogBlock>

      {/* ── Early February 2026 ──────────────────────── */}
      <ChangelogBlock title="Early February 2026">
        <Li>Initial agent release</Li>
        <Li>Real-time enrollment telemetry (IME log parsing, ESP phases, app installs)</Li>
        <Li>Geolocation support for enrollment sessions</Li>
        <Li>Hello screen detector for enrollment completion</Li>
        <Li>Reboot-on-complete support</Li>
        <Li>Session ID persistence across agent restarts</Li>
        <Li>Bootstrap token authentication for pre-MDM scenarios</Li>
      </ChangelogBlock>
    </section>
  );
}

/* ── Helpers ──────────────────────────────────────────── */

function ChangelogBlock({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="mb-6">
      <h3 className="text-lg font-semibold text-gray-900 mb-2 border-b border-gray-200 pb-1">{title}</h3>
      <ul className="space-y-1.5 text-sm text-gray-700 list-disc list-inside">{children}</ul>
    </div>
  );
}

function Li({ children }: { children: React.ReactNode }) {
  return <li>{children}</li>;
}
