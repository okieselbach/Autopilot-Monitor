import Link from "next/link";

function FaqItem({ question, children }: { question: string; children: React.ReactNode }) {
  return (
    <div className="border-b border-gray-100 pb-6 last:border-b-0 last:pb-0">
      <h3 className="text-base font-semibold text-gray-900 mb-2">{question}</h3>
      <div className="text-gray-700 text-sm leading-relaxed space-y-2">{children}</div>
    </div>
  );
}

export function SectionFaq() {
  return (
    <section className="bg-white rounded-lg shadow-md p-8">
      <div className="flex items-center space-x-3 mb-4">
        <svg className="w-8 h-8 text-blue-600 shrink-0" fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
        <h2 className="text-2xl font-bold text-gray-900">Frequently Asked Questions</h2>
      </div>

      <p className="text-gray-700 leading-relaxed mb-8">
        Common questions about Autopilot Monitor — from initial setup to day-to-day usage.
      </p>

      <div className="space-y-6">

        {/* --- General --- */}
        <h3 className="text-lg font-semibold text-gray-900 border-b border-gray-200 pb-2">General</h3>

        <FaqItem question="What is Autopilot Monitor?">
          <p>
            Autopilot Monitor gives IT admins real-time visibility into Windows Autopilot enrollment sessions.
            A lightweight agent runs on devices during enrollment and streams events to a web portal where you
            can watch progress, diagnose failures, and review historical sessions.
          </p>
        </FaqItem>

        <FaqItem question="Which Autopilot scenarios are supported?">
          <p>
            The agent supports <strong>User-Driven</strong>, <strong>Pre-Provisioned (White Glove)</strong>,
            and <strong>Windows Autopilot and Autopilot Device Preparation (early testing)</strong> enrollment flows.
          </p>
        </FaqItem>

        <FaqItem question="Is Autopilot Monitor free?">
          <p>
            Autopilot Monitor is currently in <strong>private preview</strong>. During the preview period access
            is granted after approval. See the{" "}
            <Link href="/docs/private-preview" className="text-blue-600 hover:underline">Private Preview</Link>{" "}
            page for details on how to get started.
          </p>
        </FaqItem>

        <FaqItem question="Where is my data stored?">
          <p>
            All session data is stored in our Azure environment — the backend runs as Azure Functions
            with Azure Table Storage. The agent communicates securely with our backend endpoint 
            and all data remains under your control.
          </p>
        </FaqItem>

        {/* --- Setup & Agent --- */}
        <h3 className="text-lg font-semibold text-gray-900 border-b border-gray-200 pb-2 mt-8">Setup &amp; Agent</h3>

        <FaqItem question="How do I deploy the agent to devices?">
          <p>
            The agent is deployed through Intune as a bootstrapper PowerShell script. The{" "}
            <Link href="/docs/agent-setup" className="text-blue-600 hover:underline">Agent Setup</Link>{" "}
            guide walks you through the complete process.
          </p>
        </FaqItem>

        <FaqItem question="Does the agent run permanently on the device?">
          <p>
            No. The agent is designed to run <strong>only during the enrollment window</strong>. It automatically
            stops after the session completes or after an idle timeout. It does not run as a persistent background 
            service.
          </p>
        </FaqItem>

        <FaqItem question="What data does the agent collect?">
          <p>
            The agent collects enrollment-related events: ESP phase transitions, app and script installations,
            registry changes, IME log entries, and performance snapshots. It does <strong>not</strong> collect
            personal user data, browsing history, or anything outside the enrollment process. See the{" "}
            <Link href="/docs/agent" className="text-blue-600 hover:underline">Agent</Link>{" "}
            page for the full list of collected data.
          </p>
        </FaqItem>

        <FaqItem question="How does the agent authenticate?">
          <p>
            The agent uses the MDM <strong>client certificate</strong> issued from your Intune MDM system.
            Every API call includes the certificate and device-identifying headers which the backend validates
            against your tenant&apos;s configuration. In addition the client is validated against your Intune 
            tenant (registered as Autopilot device) to ensure it is belonging to your tenant before accepting 
            any data. This ensures that only devices under your management can send data to the backend.
          </p>
        </FaqItem>

        {/* --- Portal & Features --- */}
        <h3 className="text-lg font-semibold text-gray-900 border-b border-gray-200 pb-2 mt-8">Portal &amp; Features</h3>

        <FaqItem question="What are Gather Rules?">
          <p>
            Gather Rules let you run custom diagnostic commands on devices during enrollment and collect the
            output centrally. For example, you can read specific registry keys, run PowerShell commands, or
            collect log files (Guardrails are applied). See the{" "}
            <Link href="/docs/gather-rules" className="text-blue-600 hover:underline">Gather Rules</Link>{" "}
            documentation for details and examples.
          </p>
        </FaqItem>

        <FaqItem question="What are Analyze Rules?">
          <p>
            Analyze Rules automatically evaluate every enrollment session against configurable patterns
            and flag known issues — like a missing app, a failed policy, or an unexpected reboot.
            They surface problems with a clear description and suggested fix so you don&apos;t have to
            manually hunt through event timelines. Learn more on the{" "}
            <Link href="/docs/analyze-rules" className="text-blue-600 hover:underline">Analyze Rules</Link>{" "}
            page.
          </p>
        </FaqItem>

        <FaqItem question="Can I export or download diagnostics data?">
          <p>
            Yes. Each session detail view has a diagnostics download option that bundles the relevant logfiles
            and gathered data into a downloadable package for offline analysis or sharing.
          </p>
        </FaqItem>

        {/* --- Troubleshooting --- */}
        <h3 className="text-lg font-semibold text-gray-900 border-b border-gray-200 pb-2 mt-8">Troubleshooting</h3>

        <FaqItem question="The agent is deployed but I don't see any sessions in the portal.">
          <p>
            Check these common causes in order:
          </p>
          <ol className="list-decimal list-inside space-y-1 ml-2">
            <li>Ensure the device is registered in Intune as an Autopilot device.</li>
            <li>Verify if the Hardware Model and Vendor are allowed in the Tenant Settings - Hardware Whitelist.</li>
            <li>Confirm the device can reach your Azure Functions backend endpoint (no firewall/proxy blocking).</li>
            <li>Review the agent log at <code className="bg-gray-100 px-1 rounded text-xs">%ProgramData%\AutopilotMonitor\Logs</code> for error details.</li>
          </ol>
        </FaqItem>

        <FaqItem question="A session shows as 'In Progress' but the enrollment already finished.">
          <p>
            This can happen when the completion signal is missed — for example if the device reboots
            before the agent can detect the final state. The session will automatically transition
            to a terminal state after the agent&apos;s max lifetime timer expires (default 6 hours).
            You can also manually mark it as success or failure from the session detail view.
          </p>
        </FaqItem>

        <FaqItem question="Where can I find the agent log files?">
          <p>
            Agent logs are stored at{" "}
            <code className="bg-gray-100 px-1 rounded text-xs">%ProgramData%\AutopilotMonitor\Logs</code>{" "}
            on the device. These logs contain detailed information about the agent&apos;s startup, event
            collection, and communication with the backend.
          </p>
        </FaqItem>

      </div>
    </section>
  );
}
