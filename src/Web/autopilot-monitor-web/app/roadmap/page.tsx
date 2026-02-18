"use client";

import Link from "next/link";
import { PublicPageHeader } from "../../components/PublicPageHeader";
import { PublicSiteNavbar } from "../../components/PublicSiteNavbar";

export default function RoadmapPage() {
  return (
    <div className="min-h-screen bg-gray-50">
      <PublicSiteNavbar showSectionLinks={false} />
      <PublicPageHeader title="Roadmap" />
      <main className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-16">
        <div className="bg-white rounded-2xl shadow-sm border border-gray-200 p-8 sm:p-10">
          <p className="text-gray-600">
            Planned features for upcoming releases.
          </p>

          <section className="mt-8">
            <h2 className="text-lg font-semibold text-gray-900 mb-3">Current Focus</h2>
            <ul className="space-y-2">
              <li className="flex items-center gap-2 text-gray-700">
                <span className="inline-block w-2 h-2 rounded-full bg-blue-600" />
                MSP Support
              </li>
            </ul>
          </section>

          <div className="mt-10">
            <Link
              href="/landing"
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
