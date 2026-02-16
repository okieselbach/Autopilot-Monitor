"use client";

import { useAuth } from "../../contexts/AuthContext";
import { useRouter } from "next/navigation";
import { useEffect } from "react";

// Static platform stats - manually updated periodically
const PLATFORM_STATS = {
  totalEnrollments: 123,
  totalTenants: 2,
  uniqueDeviceModels: 1,
};

export default function LandingPage() {
  const { login, isAuthenticated, isLoading, user, isPreviewBlocked } = useAuth();
  const router = useRouter();

  // Redirect after login: preview-blocked → /preview, admins → /, users → /progress
  useEffect(() => {
    if (isAuthenticated && !isLoading && user) {
      if (isPreviewBlocked) {
        router.push("/preview");
      } else if (user.isTenantAdmin || user.isGalacticAdmin) {
        router.push("/");
      } else {
        router.push("/progress");
      }
    }
  }, [isAuthenticated, isLoading, user, isPreviewBlocked, router]);

  if (isLoading) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50 flex items-center justify-center">
        <div className="text-center">
          <div className="animate-spin rounded-full h-12 w-12 border-b-2 border-blue-600 mx-auto"></div>
          <p className="mt-4 text-gray-600">Loading...</p>
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gradient-to-br from-blue-50 via-indigo-50 to-purple-50">
      {/* Navigation */}
      <nav className="absolute top-0 left-0 right-0 z-10 p-6">
        <div className="max-w-7xl mx-auto flex items-center justify-between">
          <div className="flex items-center space-x-3">
            <div className="w-10 h-10 bg-gradient-to-br from-blue-600 to-indigo-600 rounded-lg flex items-center justify-center">
              <svg className="w-6 h-6 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
            </div>
            <span className="text-2xl font-bold bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent">
              Autopilot Monitor
            </span>
          </div>
          <button
            onClick={login}
            className="px-6 py-2 bg-white text-blue-600 rounded-lg font-semibold shadow-md hover:shadow-lg transform hover:-translate-y-0.5 transition-all"
          >
            Sign In
          </button>
        </div>
      </nav>

      {/* Hero Section */}
      <div className="pt-32 pb-20 px-6">
        <div className="max-w-7xl mx-auto">
          <div className="text-center max-w-4xl mx-auto">
            <h1 className="text-6xl font-bold text-gray-900 mb-6 leading-tight">
              Advanced Monitoring for
              <span className="bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent"> Windows Autopilot</span>
            </h1>
            <p className="text-xl text-gray-600 mb-8 leading-relaxed">
              Real-time insights, intelligent troubleshooting, and comprehensive analytics for your Autopilot deployments.
              Monitor every phase, run customizable analyze rules, and resolve issues faster than ever before.
            </p>
            <div className="flex items-center justify-center space-x-4">
              <button
                onClick={login}
                className="px-8 py-4 bg-gradient-to-r from-blue-600 to-indigo-600 text-white rounded-lg font-semibold text-lg shadow-xl hover:shadow-2xl transform hover:-translate-y-1 transition-all flex items-center space-x-2"
              >
                <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 20 20">
                  <path d="M10 12a2 2 0 100-4 2 2 0 000 4z" />
                  <path fillRule="evenodd" d="M.458 10C1.732 5.943 5.522 3 10 3s8.268 2.943 9.542 7c-1.274 4.057-5.064 7-9.542 7S1.732 14.057.458 10zM14 10a4 4 0 11-8 0 4 4 0 018 0z" clipRule="evenodd" />
                </svg>
                <span>Get Started with Microsoft</span>
              </button>
            </div>
            <p className="mt-4 text-sm text-gray-500">
              Free to try • Enterprise-ready • Multi-tenant support
            </p>

            {/* Platform Stats - Since Release (Static) - Always show, even if 0 */}
            <div className="mt-12 pt-12 border-t border-gray-200">
              <p className="text-sm font-semibold text-gray-500 uppercase tracking-wider mb-6">
                Since Release
              </p>
              <div className="grid grid-cols-1 sm:grid-cols-3 gap-8">
                <div className="text-center">
                  <div className="text-4xl font-bold text-blue-600 mb-2">
                    {PLATFORM_STATS.totalEnrollments.toLocaleString()}
                  </div>
                  <div className="text-sm text-gray-600">
                    Enrollments Monitored
                  </div>
                </div>
                <div className="text-center">
                  <div className="text-4xl font-bold text-indigo-600 mb-2">
                    {PLATFORM_STATS.totalTenants.toLocaleString()}
                  </div>
                  <div className="text-sm text-gray-600">
                    Active Organizations
                  </div>
                </div>
                <div className="text-center">
                  <div className="text-4xl font-bold text-purple-600 mb-2">
                    {PLATFORM_STATS.uniqueDeviceModels.toLocaleString()}
                  </div>
                  <div className="text-sm text-gray-600">
                    Different Device Models
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* Features Grid */}
      <div className="py-20 px-6 bg-white/50 backdrop-blur-sm">
        <div className="max-w-7xl mx-auto">
          <h2 className="text-3xl font-bold text-center text-gray-900 mb-12">
            Everything you need to monitor Autopilot
          </h2>
          <div className="grid grid-cols-1 md:grid-cols-3 gap-8">
            {/* Feature 1 - Real-Time Monitoring */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-blue-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-blue-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 10V3L4 14h7v7l9-11h-7z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Real-Time Monitoring</h3>
              <p className="text-gray-600 mb-4">
                Watch Autopilot deployments in near real-time with live event streaming. Track every phase from device registration to user login.
              </p>
              <ul className="space-y-2">
                {["Live phase tracking", "Near realtime push updates", "Per-device event stream"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-blue-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 2 - Rich Analytics */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-indigo-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-indigo-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Rich Analytics</h3>
              <p className="text-gray-600 mb-4">
                Comprehensive metrics on deployment success rates, performance trends, and hardware insights — powered by customizable analyze rules.
              </p>
              <ul className="space-y-2">
                {["Customizable analyze rules", "Success & failure rates", "Hardware model insights"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-indigo-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 3 - Multi-Tenant / MSP (Planned) */}
            <div className="relative bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <span className="absolute top-4 right-4 inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold bg-amber-100 text-amber-700 border border-amber-200">
                Planned
              </span>
              <div className="w-12 h-12 bg-purple-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-purple-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">MSP Support</h3>
              <p className="text-gray-600 mb-4">
                Built for MSPs and enterprises. Manage multiple customer tenants from a single dashboard with full tenant isolation and role-based access.
              </p>
            </div>

            {/* Feature 4 - Intelligent Alerts (Planned) */}
            <div className="relative bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <span className="absolute top-4 right-4 inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold bg-amber-100 text-amber-700 border border-amber-200">
                Planned
              </span>
              <div className="w-12 h-12 bg-green-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Intelligent Alerts</h3>
              <p className="text-gray-600 mb-4">
                Get notified instantly when deployments fail or encounter issues. Smart alerts help you catch problems before users do.
              </p>
              <ul className="space-y-2">
                {["Webhook notifications", "Teams & Slack integration", "Configurable rule results"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-green-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 5 - Event Timeline */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-orange-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-orange-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Event Timeline</h3>
              <p className="text-gray-600 mb-4">
                Detailed event timeline for every deployment session. Drill down into events, errors, and warnings to troubleshoot efficiently.
              </p>
              <ul className="space-y-2">
                {["Phase-by-phase breakdown", "App install details", "Error & warning highlights"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-orange-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>

            {/* Feature 6 - Audit Logging */}
            <div className="bg-white rounded-2xl p-8 shadow-lg hover:shadow-xl transition-shadow">
              <div className="w-12 h-12 bg-red-100 rounded-xl flex items-center justify-center mb-4">
                <svg className="w-6 h-6 text-red-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
              </div>
              <h3 className="text-xl font-semibold text-gray-900 mb-3">Audit Logging</h3>
              <p className="text-gray-600 mb-4">
                Complete audit trail of all actions and changes. Meet compliance requirements with detailed logging and data retention policies.
              </p>
              <ul className="space-y-2">
                {["Admin action history", "Configurable retention", "Tamper-evident records"].map(item => (
                  <li key={item} className="flex items-center text-sm text-gray-500">
                    <span className="w-1.5 h-1.5 rounded-full bg-red-400 mr-2 shrink-0" />
                    {item}
                  </li>
                ))}
              </ul>
            </div>
          </div>
        </div>
      </div>

      {/* CTA Section */}
      <div className="py-20 px-6">
        <div className="max-w-4xl mx-auto text-center">
          <h2 className="text-4xl font-bold text-gray-900 mb-6">
            Ready to transform your Autopilot monitoring?
          </h2>
          <p className="text-xl text-gray-600 mb-8">
            Join organizations using Autopilot Monitor to deploy devices faster and more reliably.
          </p>
          <button
            onClick={login}
            className="px-8 py-4 bg-gradient-to-r from-blue-600 to-indigo-600 text-white rounded-lg font-semibold text-lg shadow-xl hover:shadow-2xl transform hover:-translate-y-1 transition-all"
          >
            Start Monitoring Now
          </button>
        </div>
      </div>

      {/* Footer */}
      <footer className="border-t border-gray-200 bg-white/50 backdrop-blur-sm py-8 px-6">
        <div className="max-w-7xl mx-auto text-center text-gray-600">
          <p>&copy; 2026 Autopilot Monitor developed by Oliver Kieselbach and powered by Azure and Microsoft Identity.</p>
        </div>
      </footer>
    </div>
  );
}
