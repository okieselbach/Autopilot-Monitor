import Link from "next/link";

const VERSION = "1.0.0";

export default function AboutPage() {
  return (
    <div className="min-h-screen bg-gradient-to-br from-blue-50 to-indigo-100">
      <main className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 py-12 space-y-6">
        <div className="bg-white rounded-lg shadow p-6">
          <h1 className="text-3xl font-bold text-gray-900">About Autopilot Monitor</h1>
          <p className="mt-3 text-gray-700 leading-relaxed">
            <strong>Autopilot Monitor</strong> is a real-time monitoring and troubleshooting platform for Windows Autopilot enrollments.
            It gives IT teams visibility into enrollment phases, app progress, errors, and timelines so issues can be found and resolved faster.
          </p>
          <p className="mt-3 text-sm text-gray-500">Version {VERSION}</p>
        </div>

        <div className="bg-white rounded-lg shadow p-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">What You Get</h2>
          <ul className="list-disc list-inside space-y-2 text-gray-700 ml-2">
            <li>Live enrollment visibility across phases and sessions</li>
            <li>Faster root-cause analysis with detailed event timelines</li>
            <li>Rule-based detection through built-in and custom Analyze Rules</li>
            <li>Operational insights across tenants and device fleets</li>
          </ul>
        </div>

        <div className="bg-white rounded-lg shadow p-6">
          <h2 className="text-xl font-semibold text-gray-900 mb-4">Quick Links</h2>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-sm">
            <Link href="/docs" className="rounded-lg border border-gray-200 px-4 py-3 hover:border-blue-300 hover:bg-blue-50/50 transition-colors">
              Documentation
            </Link>
            <Link href="/roadmap" className="rounded-lg border border-gray-200 px-4 py-3 hover:border-blue-300 hover:bg-blue-50/50 transition-colors">
              Roadmap
            </Link>
            <Link href="/privacy" className="rounded-lg border border-gray-200 px-4 py-3 hover:border-blue-300 hover:bg-blue-50/50 transition-colors">
              Privacy Policy
            </Link>
            <Link href="/terms" className="rounded-lg border border-gray-200 px-4 py-3 hover:border-blue-300 hover:bg-blue-50/50 transition-colors">
              Terms of Use
            </Link>
          </div>
        </div>
      </main>
    </div>
  );
}
