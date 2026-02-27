export function SectionPrivatePreview() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-amber-500 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Private Preview</h2>
      </div>

      <p className="text-gray-700 leading-relaxed mb-6">
        Autopilot Monitor is currently in <strong>Private Preview</strong>. The service is available to a limited number
        of organizations while the core functionality is being refined and stabilized. Access is invite-only and managed
        manually on a per-tenant basis.
      </p>

      {/* Warning banner */}
      <div className="mb-6 p-4 bg-red-50 border border-red-200 rounded-lg text-sm text-red-900">
        <p className="font-semibold mb-1 flex items-center gap-2">
          <svg className="w-4 h-4 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
          </svg>
          Expect instability during Private Preview
        </p>
        <ul className="space-y-1 ml-1">
          <li className="flex items-start gap-2">
            <span className="shrink-0 mt-0.5">•</span>
            <span>Things <strong>can and will break</strong>. The backend, web frontend, and agent are all under active development and receive frequent updates.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="shrink-0 mt-0.5">•</span>
            <span><strong>Availability is not guaranteed.</strong> Deployments and maintenance may cause downtime without prior notice.</span>
          </li>
          <li className="flex items-start gap-2">
            <span className="shrink-0 mt-0.5">•</span>
            <span>Features, APIs, and data structures may change at any time. Existing session data may be cleared between major updates.</span>
          </li>
        </ul>
        <p className="mt-2 text-red-800">
          This preview is intended for testing and feedback — not for production use in critical environments.
        </p>
      </div>

      {/* How to get access */}
      <div className="mb-8">
        <h3 className="text-lg font-semibold text-gray-900 mb-3">How to request access</h3>
        <p className="text-gray-700 leading-relaxed mb-4">
          To get started, sign in with your Microsoft Entra ID account on the Autopilot Monitor portal. If your
          organization has not yet been granted access, you will see a <strong>Private Preview</strong> screen
          (see below) confirming that your tenant has been placed on the waitlist. Once you are on the waitlist,
          reach out via LinkedIn or open a GitHub issue to request activation — your tenant is then enabled manually
          in the backend. I'll check incoming requests regularly and approve them as quickly as possible, depending
          on my capacity. When you signed-up, sign in again later to view the updated approval status on your dashboard.
        </p>
        <div className="flex flex-wrap gap-3 mb-6">
          <a
            href="https://www.linkedin.com/in/nicobostelmann"
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-2 px-4 py-2 bg-blue-700 hover:bg-blue-800 text-white text-sm font-medium rounded-lg transition-colors"
          >
            <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
              <path d="M19 0h-14c-2.761 0-5 2.239-5 5v14c0 2.761 2.239 5 5 5h14c2.762 0 5-2.239 5-5v-14c0-2.761-2.238-5-5-5zm-11 19h-3v-11h3v11zm-1.5-12.268c-.966 0-1.75-.79-1.75-1.764s.784-1.764 1.75-1.764 1.75.79 1.75 1.764-.783 1.764-1.75 1.764zm13.5 12.268h-3v-5.604c0-3.368-4-3.113-4 0v5.604h-3v-11h3v1.765c1.396-2.586 7-2.777 7 2.476v6.759z" />
            </svg>
            Contact on LinkedIn
          </a>
          <a
            href="https://github.com/nicobostelmann/autopilot-monitor/issues"
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-2 px-4 py-2 bg-gray-800 hover:bg-gray-900 text-white text-sm font-medium rounded-lg transition-colors"
          >
            <svg className="w-4 h-4" fill="currentColor" viewBox="0 0 24 24">
              <path d="M12 0c-6.626 0-12 5.373-12 12 0 5.302 3.438 9.8 8.207 11.387.599.111.793-.261.793-.577v-2.234c-3.338.726-4.033-1.416-4.033-1.416-.546-1.387-1.333-1.756-1.333-1.756-1.089-.745.083-.729.083-.729 1.205.084 1.839 1.237 1.839 1.237 1.07 1.834 2.807 1.304 3.492.997.107-.775.418-1.305.762-1.604-2.665-.305-5.467-1.334-5.467-5.931 0-1.311.469-2.381 1.236-3.221-.124-.303-.535-1.524.117-3.176 0 0 1.008-.322 3.301 1.23.957-.266 1.983-.399 3.003-.404 1.02.005 2.047.138 3.006.404 2.291-1.552 3.297-1.23 3.297-1.23.653 1.653.242 2.874.118 3.176.77.84 1.235 1.911 1.235 3.221 0 4.609-2.807 5.624-5.479 5.921.43.372.823 1.102.823 2.222v3.293c0 .319.192.694.801.576 4.765-1.589 8.199-6.086 8.199-11.386 0-6.627-5.373-12-12-12z" />
            </svg>
            Open a GitHub Issue
          </a>
        </div>

        {/* Screenshot */}
        <div className="rounded-lg overflow-hidden border border-gray-200 shadow-sm max-w-sm">
          <img
            src="/private-preview.png"
            alt="Private Preview waitlist screen"
            className="w-full"
          />
        </div>
        <p className="text-xs text-gray-500 mt-2">
          The Private Preview screen you will see when your tenant is on the waitlist.
        </p>
      </div>

      {/* After approval */}
      <div className="p-4 bg-green-50 border border-green-200 rounded-lg text-sm text-green-900">
        <p className="font-semibold mb-1">After your tenant is approved</p>
        <p>
          Once access is granted, you can sign back in and will be taken directly to the portal. Continue with
          the <strong>Setup</strong> and <strong>Agent Setup</strong> sections of this documentation to get
          your first Autopilot enrollment sessions flowing in.
        </p>
      </div>
    </section>
  );
}
