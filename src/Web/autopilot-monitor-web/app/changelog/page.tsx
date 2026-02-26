"use client";

import Link from "next/link";
import { PublicPageHeader } from "../../components/PublicPageHeader";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";

export default function ChangelogPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Changelog" />
      <main className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-16">
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
                  2026-02-26
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
                reworked. Previously, SignalR pushed full event payloads directly into the
                UI — which could cause silent gaps if a message was dropped or arrived
                out of order. Events are now fetched directly from storage on each signal,
                making Table Storage the single source of truth. The timeline always reflects
                what is actually stored, ordering and completeness are guaranteed, and
                phase grouping is computed at render time rather than at receipt time
                (eliminating a race condition when session metadata wasn&apos;t yet available).
                A 30-second catch-up fetch was also added as a safety net for sessions
                with degraded SignalR connectivity.
              </p>
            </div>

          </div>

          <div className="mt-12 pt-8 border-t border-gray-100">
            <Link
              href="/"
              className="inline-flex items-center text-sm font-medium text-blue-700 hover:text-blue-800"
            >
              Landing page
            </Link>
          </div>
        </div>
      </main>
    </div>
  );
}
