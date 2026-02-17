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

const QUICK_START = [
  {
    title: "Sign in and grant access",
    description: "Authenticate with Microsoft and approve tenant access once.",
  },
  {
    title: "Deploy bootstrapper in Intune",
    description: "Assign the bootstrap script to your Autopilot scope.",
  },
  {
    title: "Watch live telemetry",
    description: "Track phases, apps, failures, and actions in real time.",
  },
];

const FLOW_STEPS = [
  {
    title: "Intune assignment",
    description: "Target the bootstrapper script to your selected Autopilot device groups.",
    icon: "users",
  },
  {
    title: "Bootstrapper execution",
    description: "Device runs the bootstrapper script and installs Autopilot Monitor Agent.",
    icon: "code",
  },
  {
    title: "Live monitoring",
    description: "Phase transitions, app installs, and progress are captured continuously.",
    icon: "monitor",
  },
  {
    title: "Event upload",
    description: "Predefined and custom events are uploaded to the backend pipeline.",
    icon: "cloud",
  },
  {
    title: "Rule analysis",
    description: "Backend correlates phases and runs analyze rules for instant insights.",
    icon: "rules",
  },
  {
    title: "Completion and notifications",
    description: "Session status is finalized and Teams notifications are triggered.",
    icon: "bell",
  },
  {
    title: "Diagnostics download",
    description: "Grab diagnostic bundles quickly for fast root-cause validation.",
    icon: "download",
  },
];

function StepIcon({ icon }: { icon: string }) {
  switch (icon) {
    case "users":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-1a4 4 0 00-5-3.87M9 20H2v-1a4 4 0 015-3.87m9-5.13a4 4 0 11-8 0 4 4 0 018 0z" />
        </svg>
      );
    case "code":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="m8 9-3 3 3 3m8-6 3 3-3 3M13 7l-2 10" />
        </svg>
      );
    case "monitor":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M3 4h18v12H3zM8 20h8m-4-4v4m-3-8 2-2 2 3 3-4 2 3" />
        </svg>
      );
    case "cloud":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M7 18a4 4 0 01-.3-8A5 5 0 1117 8h1a4 4 0 010 8h-3m-3 0v-7m0 7-3-3m3 3 3-3" />
        </svg>
      );
    case "rules":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5h10M9 9h10M9 13h10M9 17h10M4 5h.01M4 9h.01M4 13h.01M4 17h.01" />
        </svg>
      );
    case "bell":
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M15 17h5l-1.4-1.4A2 2 0 0118 14.2V11a6 6 0 10-12 0v3.2c0 .5-.2 1-.6 1.4L4 17h5m6 0a3 3 0 11-6 0h6z" />
        </svg>
      );
    default:
      return (
        <svg className="w-4 h-4 text-blue-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M12 5v10m0 0-3-3m3 3 3-3M5 19h14" />
        </svg>
      );
  }
}

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
              <svg className="w-6 h-6 text-white" viewBox="0 0 24 24" fill="none">
                <rect x="5.0" y="12.2" width="2.8" height="7.8" rx="0.9" fill="currentColor" />
                <rect x="10.6" y="10.9" width="2.8" height="9.1" rx="0.9" fill="currentColor" />
                <rect x="16.2" y="8.6" width="2.8" height="11.4" rx="0.9" fill="currentColor" />
                <path d="M4.4 8.9L8.6 6.8L12.0 7.4L15.4 5.5L18.8 4.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
                <path d="M17.8 4.2L19.1 4.9L17.9 5.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
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
            <div className="mb-7 inline-flex items-center gap-3 rounded-2xl border border-blue-300/70 bg-gradient-to-r from-blue-50 via-indigo-50 to-blue-50 px-4 py-2.5 shadow-md ring-1 ring-blue-200/60">
              <span className="inline-flex h-6 w-6 items-center justify-center rounded-full bg-blue-600 text-white shadow-sm">
                <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M17 20h5v-1a4 4 0 00-5-3.87M9 20H2v-1a4 4 0 015-3.87m9-5.13a4 4 0 11-8 0 4 4 0 018 0z" />
                </svg>
              </span>
              <p className="text-sm font-semibold text-blue-900">
                Community-driven Analyze Rules support
                <span className="font-normal text-blue-800">: discover, build, and share rules for everyone.</span>
              </p>
            </div>
            <div className="relative inline-block mb-6">
              <h1 className="text-6xl font-bold text-gray-900 leading-tight">
              Advanced Monitoring for
              <span className="bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent"> Windows Enrollments</span>
              </h1>
              <div className="pointer-events-none absolute -right-10 -top-1 rotate-[13deg] inline-flex items-center rounded-md border border-amber-300/80 bg-gradient-to-r from-amber-500 to-orange-500 px-3 py-1 shadow-md">
                <span className="text-[10px] font-bold uppercase tracking-[0.2em] text-white whitespace-nowrap">
                  Private Preview Running
                </span>
              </div>
            </div>
            <p className="text-xl text-gray-600 mb-8 leading-relaxed">
              Real-time insights, intelligent troubleshooting, and comprehensive analytics for your Autopilot deployments.
              Monitor every phase, run customizable analyze rules, and resolve issues faster than ever before.
            </p>
            <div className="flex items-center justify-center space-x-4">
              <button
                onClick={login}
                className="px-8 py-4 bg-gradient-to-r from-blue-600 to-indigo-600 text-white rounded-lg font-semibold text-lg shadow-xl hover:shadow-2xl transform hover:-translate-y-0.5 transition-all flex items-center space-x-2"
              >
                <svg className="w-5 h-5" fill="currentColor" viewBox="0 0 20 20">
                  <path d="M10 12a2 2 0 100-4 2 2 0 000 4z" />
                  <path fillRule="evenodd" d="M.458 10C1.732 5.943 5.522 3 10 3s8.268 2.943 9.542 7c-1.274 4.057-5.064 7-9.542 7S1.732 14.057.458 10zM14 10a4 4 0 11-8 0 4 4 0 018 0z" clipRule="evenodd" />
                </svg>
                <span>Get Started</span>
              </button>
            </div>
            <p className="mt-4 text-sm text-gray-500">
              Free to use • Open-Source
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

      {/* Eye-Catcher Workflow Section */}
      <section className="py-20 px-6">
        <div className="max-w-7xl mx-auto">
          <div className="text-center max-w-4xl mx-auto">
            <p className="text-sm font-semibold text-blue-600 uppercase tracking-[0.22em] mb-3">
              How It Works
            </p>
            <h2 className="text-4xl md:text-5xl font-bold text-gray-900 leading-tight">
              From Intune rollout to deep insights in minutes
            </h2>
            <p className="mt-5 text-lg text-gray-600 leading-relaxed">
              Simple onboarding, clear phase visibility, and automated analysis in one workflow.
              Deploy once, monitor everything, and react faster when issues appear.
            </p>
          </div>

          <div className="mt-10 grid grid-cols-1 md:grid-cols-3 gap-4">
            {QUICK_START.map((item, index) => (
              <div
                key={item.title}
                className="rounded-2xl border border-blue-100 bg-white/90 backdrop-blur-sm p-5 shadow-md"
              >
                <div className="inline-flex h-7 min-w-7 items-center justify-center rounded-full bg-blue-600 text-white text-xs font-bold px-2">
                  {index + 1}
                </div>
                <h3 className="mt-3 text-lg font-semibold text-gray-900">{item.title}</h3>
                <p className="mt-2 text-sm text-gray-600 leading-relaxed">{item.description}</p>
              </div>
            ))}
          </div>

          <div className="mt-8 md:mt-10 text-center">
            <p className="text-sm md:text-base text-gray-600 max-w-3xl mx-auto leading-relaxed">
              This is what happens after rollout: each phase is captured, correlated, and translated into clear, actionable insights.
            </p>
          </div>

          <div className="mt-6 md:mt-7 rounded-3xl border border-blue-100 bg-gradient-to-br from-white via-blue-50/60 to-indigo-50/60 p-5 md:p-6 shadow-xl overflow-hidden relative">
            <div className="absolute -top-16 -left-16 w-48 h-48 bg-blue-200/30 blur-3xl rounded-full pointer-events-none" />
            <div className="absolute -bottom-20 -right-16 w-56 h-56 bg-indigo-200/25 blur-3xl rounded-full pointer-events-none" />

            <div className="relative z-10">
              <div className="flex items-center justify-between gap-4 flex-wrap">
                <div>
                  <h3 className="text-gray-900 text-2xl md:text-3xl font-bold">
                    One pipeline from deployment to action
                  </h3>
                </div>
                <button
                  onClick={login}
                  className="px-5 py-2.5 rounded-lg bg-gradient-to-r from-blue-600 to-indigo-600 text-white font-semibold shadow-lg hover:shadow-xl transition-all"
                >
                  Start Now
                </button>
              </div>

              <div className="mt-6 relative">
                {/* Desktop: independent left/right stacks for true vertical overlap */}
                <div className="hidden md:grid grid-cols-[minmax(0,1fr)_40px_minmax(0,1fr)] gap-4 items-start relative isolate">
                  <div className="space-y-20 relative z-30">
                    {FLOW_STEPS.filter((_, i) => i % 2 === 0).map((step, leftIndex) => {
                      const index = leftIndex * 2;
                      return (
                        <div
                          key={step.title}
                          className="step-card relative rounded-xl border border-blue-100 bg-white/90 backdrop-blur-sm p-3 md:p-3.5 shadow-md"
                          style={{ animationDelay: `${index * 0.08}s` }}
                        >
                          <div className="absolute right-[-36px] top-[calc(50%+1px)] -translate-y-1/2 h-px w-[36px] bg-blue-200 z-10" />
                          <span
                            className="dot-pulse absolute right-[-42px] top-[calc(50%-4px)] -translate-y-1/2 h-3 w-3 rounded-full bg-blue-500 ring-4 ring-white z-50"
                            style={{ animationDelay: `${index * 1.8}s` }}
                          />
                          <p className="text-blue-600 text-xs font-semibold uppercase tracking-[0.18em]">
                            Step {index + 1}
                          </p>
                          <div className="mt-1.5 flex items-start justify-between gap-3">
                            <h4 className="text-gray-900 font-semibold">{step.title}</h4>
                            <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-blue-200 bg-blue-50">
                              <StepIcon icon={step.icon} />
                            </span>
                          </div>
                          <p className="mt-1 text-sm text-gray-600 leading-relaxed">{step.description}</p>
                        </div>
                      );
                    })}
                  </div>

                  <div className="relative self-stretch z-0">
                    <div className="absolute left-1/2 -translate-x-1/2 top-2 bottom-2 w-px bg-blue-200 z-0" />
                  </div>

                  <div className="space-y-20 pt-24 relative z-30">
                    {FLOW_STEPS.filter((_, i) => i % 2 === 1).map((step, rightIndex) => {
                      const index = rightIndex * 2 + 1;
                      return (
                        <div
                          key={step.title}
                          className="step-card relative rounded-xl border border-blue-100 bg-white/90 backdrop-blur-sm p-3 md:p-3.5 shadow-md"
                          style={{ animationDelay: `${index * 0.08}s` }}
                        >
                          <div className="absolute left-[-36px] top-[calc(50%+1px)] -translate-y-1/2 h-px w-[36px] bg-blue-200 z-10" />
                          <span
                            className="dot-pulse absolute left-[-42px] top-[calc(50%-4px)] -translate-y-1/2 h-3 w-3 rounded-full bg-blue-500 ring-4 ring-white z-50"
                            style={{ animationDelay: `${index * 1.8}s` }}
                          />
                          <p className="text-blue-600 text-xs font-semibold uppercase tracking-[0.18em]">
                            Step {index + 1}
                          </p>
                          <div className="mt-1.5 flex items-start justify-between gap-3">
                            <h4 className="text-gray-900 font-semibold">{step.title}</h4>
                            <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-blue-200 bg-blue-50">
                              <StepIcon icon={step.icon} />
                            </span>
                          </div>
                          <p className="mt-1 text-sm text-gray-600 leading-relaxed">{step.description}</p>
                        </div>
                      );
                    })}
                  </div>
                </div>

                {/* Mobile: linear timeline */}
                <div className="md:hidden relative isolate">
                  <div className="absolute left-4 top-3 bottom-3 w-px bg-blue-200" />
                  <div className="space-y-5">
                    {FLOW_STEPS.map((step, index) => (
                      <div key={step.title} className="relative">
                        <div className="absolute left-4 top-[calc(2rem+1px)] h-px w-6 bg-blue-200 z-10" />
                        <span
                          className="dot-pulse absolute left-4 top-[calc(1.75rem-4px)] h-3 w-3 rounded-full bg-blue-500 ring-4 ring-white z-50"
                          style={{ animationDelay: `${index * 1.8}s` }}
                        />
                        <div
                          className="step-card ml-10 rounded-xl border border-blue-100 bg-white/90 backdrop-blur-sm p-3 shadow-md"
                          style={{ animationDelay: `${index * 0.08}s` }}
                        >
                          <p className="text-blue-600 text-xs font-semibold uppercase tracking-[0.18em]">
                            Step {index + 1}
                          </p>
                          <div className="mt-1.5 flex items-start justify-between gap-3">
                            <h4 className="text-gray-900 font-semibold">{step.title}</h4>
                            <span className="inline-flex h-8 w-8 shrink-0 items-center justify-center rounded-lg border border-blue-200 bg-blue-50">
                              <StepIcon icon={step.icon} />
                            </span>
                          </div>
                          <p className="mt-1 text-sm text-gray-600 leading-relaxed">{step.description}</p>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>

              <div className="mt-6 grid grid-cols-1 md:grid-cols-3 gap-3">
                {[
                  "Know exactly what happens at each enrollment phase",
                  "Detect app bottlenecks and policy issues immediately",
                  "Move from alert to diagnostics with minimal friction",
                ].map(item => (
                  <div key={item} className="rounded-xl border border-blue-200 bg-blue-50/80 p-3.5 text-sm text-blue-900">
                    {item}
                  </div>
                ))}
              </div>
            </div>
          </div>
        </div>
      </section>

      {/* Features Grid */}
      <div className="py-20 px-6 bg-white/50 backdrop-blur-sm">
        <div className="max-w-7xl mx-auto">
          <h2 className="text-3xl font-bold text-center text-gray-900 mb-12">
            Everything you need for full Autopilot visibility
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
                {["Webhook notifications", "Teams integration", "Configurable rule results"].map(item => (
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

      {/* Comparison Table */}
      <div className="py-20 px-6 bg-white/50 backdrop-blur-sm">
        <div className="max-w-5xl mx-auto">
          <p className="text-sm font-semibold text-center text-blue-600 uppercase tracking-widest mb-3">Comparison</p>
          <h2 className="text-4xl font-bold text-center text-gray-900 mb-4">
            Standard Autopilot vs. Monitored Autopilot
          </h2>
          <p className="text-center text-gray-500 mb-12 max-w-2xl mx-auto">
            See what you're missing without Autopilot Monitor — and what you gain the moment you deploy it.
          </p>

          {/* Header */}
          <div className="grid grid-cols-[1fr_1fr_1fr] gap-0 mb-1">
            <div />
            <div className="bg-gradient-to-br from-blue-600 to-indigo-600 text-white text-center py-4 px-6 rounded-t-2xl mx-1">
              <div className="font-bold text-lg">Autopilot Monitor</div>
              <div className="text-blue-200 text-sm mt-0.5">Fully Monitored</div>
            </div>
            <div className="bg-gray-100 text-center py-4 px-6 rounded-t-2xl mx-1">
              <div className="font-semibold text-gray-700 text-lg">Standard Autopilot</div>
              <div className="text-gray-400 text-sm mt-0.5">Out of the Box</div>
            </div>
          </div>

          {/* Rows */}
          {[
            {
              label: "Deployment Visibility",
              monitor: "Real-time phase tracking with live push updates",
              standard: "None — black box until it finishes or fails",
            },
            {
              label: "Download Progress",
              monitor: "Per-app download speed, bytes transferred, % complete",
              standard: "No visibility into what's downloading or how long it takes",
            },
            {
              label: "User-Facing Progress Page",
              monitor: "Progress view with live app status & download info",
              standard: "Generic ESP screen — no details for the end user",
            },
            {
              label: "Fleet Health Dashboard",
              monitor: "Success rates, failure trends, avg. duration across all devices",
              standard: "Limited manual report extraction from Intune",
            },
            {
              label: "Analyze Rules",
              monitor: "Built-in + fully customizable rules for automated issue detection",
              standard: "Manual log review required after every failed deployment",
            },
            {
              label: "Extended Data Gathering",
              monitor: "Custom gather rules to capture registry, files, or WMI on any event",
              standard: "No automated data collection during enrollment",
            },
            {
              label: "Geo & Network Context",
              monitor: "Device location, and network info captured at enrollment start",
              standard: "No location or network context in deployment records",
            },
            {
              label: "Performance Monitoring",
              monitor: "CPU, memory, disk, and network snapshots during deployment",
              standard: "Not captured — no way to detect resource bottlenecks",
            },
            {
              label: "Troubleshooting Speed",
              monitor: "Drill into per-event timeline, IME log patterns, and analyze results",
              standard: "Manual IME log hunting — slow and error-prone",
            },
          ].map((row, i) => (
            <div
              key={row.label}
              className={`grid grid-cols-[1fr_1fr_1fr] gap-0 border-b border-gray-100 ${i % 2 === 0 ? "bg-white" : "bg-gray-50/60"}`}
            >
              <div className="py-4 px-5 font-semibold text-gray-800 text-sm flex items-center">{row.label}</div>
              <div className="py-4 px-5 mx-1 bg-blue-50/50 text-sm text-blue-900 flex items-start gap-2">
                <svg className="w-4 h-4 text-blue-500 mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                </svg>
                {row.monitor}
              </div>
              <div className="py-4 px-5 mx-1 text-sm text-gray-400 flex items-start gap-2">
                <svg className="w-4 h-4 text-gray-300 mt-0.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                </svg>
                {row.standard}
              </div>
            </div>
          ))}

          {/* Bottom cap */}
          <div className="grid grid-cols-[1fr_1fr_1fr] gap-0">
            <div />
            <div className="bg-gradient-to-br from-blue-600 to-indigo-600 rounded-b-2xl mx-1 py-3 text-center text-blue-100 text-xs font-medium">
              Full observability from day one
            </div>
            <div className="bg-gray-100 rounded-b-2xl mx-1 py-3 text-center text-gray-400 text-xs">
              Limited to what Intune reports after the fact
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
            Join organizations using Autopilot Monitor to react faster and monitor more reliably.
          </p>
          <button
            onClick={login}
            className="px-8 py-4 bg-gradient-to-r from-blue-600 to-indigo-600 text-white rounded-lg font-semibold text-lg shadow-xl hover:shadow-2xl transform hover:-translate-y-0.5 transition-all"
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

      <style jsx>{`
        @keyframes stepReveal {
          0% {
            transform: translateY(8px);
            opacity: 0;
          }
          100% {
            transform: translateY(0);
            opacity: 1;
          }
        }

        @keyframes dotPulse {
          0%,
          100% {
            box-shadow: 0 0 0 0 rgba(59, 130, 246, 0.04);
            transform: scale(1);
          }
          2% {
            box-shadow: 0 0 0 1px rgba(59, 130, 246, 0.16);
            transform: scale(1.01);
          }
          8% {
            box-shadow: 0 0 0 5px rgba(59, 130, 246, 0.30);
            transform: scale(1.05);
          }
          14.5% {
            box-shadow: 0 0 0 2px rgba(59, 130, 246, 0.18);
            transform: scale(1.02);
          }
          15.87% {
            box-shadow: 0 0 0 0 rgba(59, 130, 246, 0.04);
            transform: scale(1);
          }
        }

        .step-card {
          animation: stepReveal 0.45s ease-out both;
        }

        .dot-pulse {
          animation: dotPulse 12.6s linear infinite;
        }
      `}</style>
    </div>
  );
}
