export function SectionKnownIssues() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M14.857 17.082a23.848 23.848 0 005.454-1.31A8.967 8.967 0 0118 9.75v-.7V9A6 6 0 006 9v.75a8.967 8.967 0 01-2.312 6.022c1.733.64 3.56 1.085 5.455 1.31m5.714 0a24.255 24.255 0 01-5.714 0m5.714 0a3 3 0 11-5.714 0" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Known Issues</h2>
      </div>
      <p className="text-gray-600 mb-8">
        Documented issues caused by external changes (e.g.&nbsp;Microsoft updates) that affect Autopilot Monitor.
        Each entry describes the impact, what still works, and whether a workaround exists.
      </p>

      <AnnouncementCard
        date="2026-04-16"
        title="Event Data Loss Due to Faulty Cleanup Operation"
        type="breaking"
      >
        <p>
          Due to an unfortunately faulty cleanup operation, a significant number of events were
          deleted from the database. As a result, some sessions no longer have a complete or
          correct timeline.
        </p>
        <p>
          Affected sessions may show missing events, incomplete phases, or incorrect completion
          states. There is no way to restore the deleted data retroactively.
        </p>
        <p>
          Operational safeguards are being put in place to block and verify cleanup operations
          before execution and prevent similar incidents in the future.
        </p>
      </AnnouncementCard>

      <AnnouncementCard
        date="2026-04-16"
        title="Agent Changes in Progress — Possible Detection Issues"
        type="known-issue"
      >
        <p>
          The agent is currently undergoing active changes. During this period,
          detection and classification issues may occur (e.g.&nbsp;sessions not completing
          correctly, events being misclassified, or sessions being falsely classified
          as WhiteGlove).
        </p>
        <p>
          This is actively being worked on. An update will follow once everything
          is running correctly again.
        </p>
      </AnnouncementCard>

      <AnnouncementCard
        date="2026-04-06"
        title="Delivery Optimization Data Restored via OS-Level Collection"
        type="resolved"
      >
        <p>
          Autopilot Monitor now collects Delivery Optimization data directly from the OS
          using <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">Get-DeliveryOptimizationStatus</code>,
          bypassing the IME log entirely. This restores DO metrics
          (<strong>BytesFromPeers</strong>, <strong>PeerCaching %</strong>, download progress)
          for all devices &mdash; including those running IME &ge; 1.101.
        </p>
        <p>
          The new OS-level collector works alongside existing IME log parsing. If both sources
          provide data for the same app, the IME log path takes priority (dedup logic).
        </p>
        <p>
          No action needed &mdash; devices running the latest agent version automatically
          benefit from this change.
        </p>
      </AnnouncementCard>

      <AnnouncementCard
        date="2026-04-05"
        title="IME 1.101.x Removes Delivery Optimization Telemetry from Logs"
        type="breaking"
      >
        <p>
          Starting with IME version <strong>1.101.x</strong>, Microsoft no longer writes Delivery Optimization (DO)
          telemetry data to the IME log. This is a Microsoft-side change, not an Autopilot Monitor bug.
        </p>

        <h4 className="font-semibold text-gray-900 mt-3">What changed</h4>
        <ul className="list-disc list-inside space-y-1">
          <li>
            <strong>Old IME (1.99.x)</strong>: Wrote{" "}
            <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">[DO TEL] = {"{"}JSON with all DO stats{"}"}</code>{" "}
            after every download &mdash; full peer caching metrics were available.
          </li>
          <li>
            <strong>New IME (1.101.x)</strong>: Only writes{" "}
            <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">[Win32App DO] DO download and decryption is successfully done</code>{" "}
            &mdash; no DO telemetry JSON at all.
          </li>
        </ul>

        <h4 className="font-semibold text-gray-900 mt-3">What the new IME still logs</h4>
        <ul className="list-disc list-inside space-y-1">
          <li>
            <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">[Win32App] Downloaded file size 37,187,491.00</code>{" "}
            &mdash; file size only
          </li>
          <li>
            <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">[Win32App DO] start creating a new download job, FileId = ...</code>{" "}
            &mdash; OriginalSize in ContentInfo JSON
          </li>
          <li>
            <code className="text-xs bg-gray-100 px-1 py-0.5 rounded">[Win32App DO] DO Job set priority is BG_JOB_PRIORITY_FOREGROUND</code>{" "}
            &mdash; download mode only
          </li>
        </ul>

        <h4 className="font-semibold text-gray-900 mt-3">What is lost</h4>
        <p>
          The detailed DO stats &mdash; <strong>BytesFromPeers</strong>, <strong>PeerCaching %</strong>,{" "}
          <strong>LanPeers</strong>, <strong>GroupPeers</strong>, and all other DO telemetry fields &mdash;{" "}
          are no longer present in the IME logs. For IME &ge; 1.101 there is currently no way to extract
          DO telemetry from the logs because the data is simply not written anymore.
        </p>

        <h4 className="font-semibold text-gray-900 mt-3">Impact on Autopilot Monitor</h4>
        <p>
          Sessions from devices running IME &ge; 1.101 will not show Delivery Optimization metrics
          in the timeline. Older IME versions continue to work as before. We are monitoring whether
          Microsoft re-introduces this data in a future IME release.
        </p>
      </AnnouncementCard>
    </section>
  );
}

/* ── Helpers ──────────────────────────────────────────── */

type BadgeType = "breaking" | "known-issue" | "info" | "resolved";

const BADGE_STYLES: Record<BadgeType, { bg: string; label: string }> = {
  breaking:      { bg: "bg-red-100 text-red-800",     label: "Breaking Change" },
  "known-issue": { bg: "bg-amber-100 text-amber-800", label: "Known Issue" },
  info:          { bg: "bg-blue-100 text-blue-800",    label: "Info" },
  resolved:      { bg: "bg-green-100 text-green-800",  label: "Resolved" },
};

function Badge({ type }: { type: BadgeType }) {
  const { bg, label } = BADGE_STYLES[type];
  return <span className={`inline-block text-xs font-medium px-2 py-0.5 rounded-full ${bg}`}>{label}</span>;
}

function AnnouncementCard({
  date,
  title,
  type,
  children,
}: {
  date: string;
  title: string;
  type: BadgeType;
  children: React.ReactNode;
}) {
  return (
    <div className="mb-6 border border-gray-200 rounded-lg p-5">
      <div className="flex items-center gap-3 mb-1">
        <span className="text-sm text-gray-500">{date}</span>
        <Badge type={type} />
      </div>
      <h3 className="text-base font-semibold text-gray-900 mb-2">{title}</h3>
      <div className="text-sm text-gray-700 leading-relaxed space-y-2">{children}</div>
    </div>
  );
}
