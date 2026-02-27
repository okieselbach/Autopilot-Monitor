"use client";

import Link from "next/link";
import { PublicPageHeader } from "../../components/PublicPageHeader";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";

export default function ChangelogPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Changelog" />
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-16">
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

          {/* Known Issues */}
          <div className="mb-10 pb-8 border-b border-gray-100">
            <h2 className="text-sm font-semibold text-gray-900 mb-3 uppercase tracking-wider">Known Issues</h2>
            <ul className="space-y-2">
              <li className="flex gap-2 text-sm text-gray-600 leading-relaxed">
                <span className="mt-0.5 text-yellow-500 flex-shrink-0">⚠</span>
                <span>
                  <span className="font-medium text-gray-800">Event Timeline</span> — only tested with Entra-only, user-driven Autopilot (no WhiteGlove, ESP with &ldquo;wait for all apps&rdquo; only, no Device Preparation, no hybrid scenario). There is code to handle some of these scenarios but they are untested. If you&apos;d like to share logs for any of these scenarios, that would be greatly appreciated.
                </span>
              </li>
            </ul>
          </div>

          {/* Entries — newest first */}
          <div className="space-y-10">

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
