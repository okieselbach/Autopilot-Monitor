"use client";

import { useMemo, useState } from "react";

interface ScriptEvent {
  timestamp: string;
  eventType?: string;
  data?: Record<string, any>;
}

interface ScriptExecutionsProps {
  events: ScriptEvent[];
  showScriptOutput?: boolean;
}

interface ScriptItem {
  policyId: string;
  scriptType: string;        // "platform" or "remediation"
  scriptPart?: string;        // "detection" or "remediation" (for remediation scripts)
  runContext?: string;         // "System" or "User"
  exitCode?: number;
  result?: string;            // "Success" or "Failed"
  complianceResult?: string;  // "True" or "False"
  stdout?: string;
  stderr?: string;
  state: "Success" | "Failed";
  timestamp: string;
  firstSeenIndex: number;
}

export default function ScriptExecutions({ events, showScriptOutput }: ScriptExecutionsProps) {
  const scripts = useMemo(() => {
    if (events.length === 0) return [];

    const sorted = [...events].sort(
      (a, b) => new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime()
    );

    const items: ScriptItem[] = [];
    const seen = new Set<string>();

    for (const evt of sorted) {
      const d = evt.data;
      if (!d) continue;

      const policyId = d.policyId ?? d.policy_id ?? "";
      if (!policyId) continue;

      const scriptType = d.scriptType ?? d.script_type ?? "platform";
      const scriptPart = d.scriptPart ?? d.script_part;
      // Dedupe key includes scriptPart for remediation (detection vs remediation phase)
      const key = `${policyId}-${scriptType}-${scriptPart ?? ""}`;
      if (seen.has(key)) continue;
      seen.add(key);

      const isSuccess = evt.eventType === "script_completed";

      items.push({
        policyId,
        scriptType,
        scriptPart,
        runContext: d.runContext ?? d.run_context,
        exitCode: d.exitCode ?? d.exit_code,
        result: d.result,
        complianceResult: d.complianceResult ?? d.compliance_result,
        stdout: d.stdout,
        stderr: d.stderr,
        state: isSuccess ? "Success" : "Failed",
        timestamp: evt.timestamp,
        firstSeenIndex: items.length,
      });
    }

    return items;
  }, [events]);

  const [expanded, setExpanded] = useState(true);

  if (scripts.length === 0) return null;

  const successCount = scripts.filter(s => s.state === "Success").length;
  const failedCount = scripts.filter(s => s.state === "Failed").length;
  const platformCount = scripts.filter(s => s.scriptType === "platform").length;
  const remediationCount = scripts.filter(s => s.scriptType === "remediation").length;

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center justify-between w-full text-left"
      >
        <div className="flex items-center space-x-2">
          <svg className="w-5 h-5 text-violet-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 9l3 3-3 3m5 0h3M5 20h14a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">Script Executions</h2>
          <div className="flex items-center space-x-2 text-xs">
            {successCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-green-100 text-green-700 font-medium">
                {successCount} succeeded
              </span>
            )}
            {failedCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-red-100 text-red-700 font-medium">
                {failedCount} failed
              </span>
            )}
            {platformCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-blue-50 text-blue-600 font-medium">
                {platformCount} platform
              </span>
            )}
            {remediationCount > 0 && (
              <span className="px-2 py-0.5 rounded-full bg-amber-50 text-amber-600 font-medium">
                {remediationCount} remediation
              </span>
            )}
          </div>
        </div>
        <span className="text-gray-400">{expanded ? '▼' : '▶'}</span>
      </button>

      {expanded && (
        <div className="space-y-3 mt-4">
          {scripts.map((item) => (
            <ScriptItemRow
              key={`${item.policyId}-${item.scriptPart ?? ""}-${item.firstSeenIndex}`}
              item={item}
              showScriptOutput={showScriptOutput}
            />
          ))}
        </div>
      )}
    </div>
  );
}

function getIntuneScriptUrl(policyId: string, scriptType: string): string {
  if (scriptType === "remediation") {
    return `https://intune.microsoft.com/#view/Microsoft_Intune_Enrollment/UXAnalyticsScriptMenu/~/overview/id/${policyId}`;
  }
  return `https://intune.microsoft.com/#view/Microsoft_Intune_DeviceSettings/ConfigureWMPolicyMenuBlade/~/overview/policyId/${policyId}/policyType/0`;
}

function ScriptItemRow({ item, showScriptOutput }: { item: ScriptItem; showScriptOutput?: boolean }) {
  const [showDetails, setShowDetails] = useState(false);

  const containerClass = item.state === "Failed"
    ? "bg-red-50 border border-red-200"
    : "bg-green-50 border border-green-200";

  const shortId = item.policyId.length >= 8 ? item.policyId.substring(0, 8) : item.policyId;
  const intuneUrl = getIntuneScriptUrl(item.policyId, item.scriptType);

  // Build label: "Platform Script" or "Remediation Detection" / "Remediation"
  let label: string;
  if (item.scriptType === "remediation") {
    label = item.scriptPart === "remediation" ? "Remediation Script" : "Remediation Detection";
  } else {
    label = "Platform Script";
  }

  // Build status text
  let statusText: string;
  if (item.scriptType === "remediation" && item.complianceResult) {
    statusText = item.complianceResult === "True" ? "Compliant" : "Non-compliant";
  } else {
    statusText = item.result ?? (item.state === "Success" ? "Success" : "Failed");
  }

  const hasStdout = showScriptOutput !== false && item.stdout && item.stdout.trim().length > 0;
  const hasStderr = item.stderr && item.stderr.trim().length > 0;
  const hasOutput = hasStdout || hasStderr;

  return (
    <div className={`rounded-lg p-3 ${containerClass}`}>
      <div className="flex items-center justify-between">
        <div className="flex items-center space-x-2 min-w-0">
          {item.state === "Failed" ? (
            <svg className="w-4 h-4 text-red-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
            </svg>
          ) : (
            <svg className="w-4 h-4 text-green-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M5 13l4 4L19 7" />
            </svg>
          )}
          <span className="text-sm font-medium text-gray-900 truncate">{label}</span>
          <a href={intuneUrl} target="_blank" rel="noopener noreferrer" className="text-xs font-mono text-blue-600 hover:text-blue-800 hover:underline" title="Open in Intune portal">{shortId}…</a>
          <span className={`text-xs px-2 py-0.5 rounded-full font-medium ${
            item.scriptType === "remediation"
              ? "bg-amber-100 text-amber-700"
              : "bg-blue-100 text-blue-700"
          }`}>
            {item.scriptType}
          </span>
          {item.runContext && (
            <span className="text-xs text-gray-500">{item.runContext}</span>
          )}
        </div>
        <div className="flex items-center space-x-3 text-xs text-gray-500 flex-shrink-0 ml-2">
          <span className={`font-medium ${item.state === "Failed" ? "text-red-600" : "text-green-600"}`}>
            {statusText}
          </span>
          {item.exitCode != null && (
            <span className={`font-mono ${item.exitCode !== 0 ? "text-red-600" : "text-gray-500"}`}>
              exit {item.exitCode}
            </span>
          )}
          <button
            onClick={() => setShowDetails(!showDetails)}
            className="text-xs text-blue-600 hover:text-blue-800"
          >
            {showDetails ? 'Hide' : 'Details'}
          </button>
        </div>
      </div>

      {showDetails && (
        <div className="mt-3 space-y-2">
          {/* Metadata */}
          <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-600">
            <span><span className="font-medium text-gray-700">Policy ID:</span> <a href={intuneUrl} target="_blank" rel="noopener noreferrer" className="font-mono text-blue-600 hover:text-blue-800 hover:underline">{item.policyId}</a></span>
            {item.runContext && <span><span className="font-medium text-gray-700">Context:</span> {item.runContext}</span>}
            {item.exitCode != null && <span><span className="font-medium text-gray-700">Exit Code:</span> <span className="font-mono">{item.exitCode}</span></span>}
            {item.result && <span><span className="font-medium text-gray-700">Result:</span> {item.result}</span>}
            {item.complianceResult && <span><span className="font-medium text-gray-700">Compliance:</span> {item.complianceResult === "True" ? "Compliant" : "Non-compliant"}</span>}
            <span><span className="font-medium text-gray-700">Time:</span> {new Date(item.timestamp).toLocaleTimeString()}</span>
          </div>

          {/* stdout */}
          {hasStdout && (
            <div>
              <div className="text-xs font-medium text-gray-500 mb-1">stdout</div>
              <div className="p-2 bg-gray-900 rounded text-xs text-gray-100 font-mono overflow-x-auto max-h-48 overflow-y-auto">
                <pre className="whitespace-pre-wrap break-words">{item.stdout}</pre>
              </div>
            </div>
          )}

          {/* stdout hidden hint */}
          {showScriptOutput === false && item.stdout && item.stdout.trim().length > 0 && (
            <div className="text-xs text-gray-400 italic">stdout hidden by admin setting</div>
          )}

          {/* stderr */}
          {hasStderr && (
            <div>
              <div className="text-xs font-medium text-red-500 mb-1">stderr</div>
              <div className="p-2 bg-gray-900 rounded text-xs text-red-300 font-mono overflow-x-auto max-h-48 overflow-y-auto">
                <pre className="whitespace-pre-wrap break-words">{item.stderr}</pre>
              </div>
            </div>
          )}

          {/* No output */}
          {!hasOutput && !(showScriptOutput === false && item.stdout && item.stdout.trim().length > 0) && (
            <div className="text-xs text-gray-400 italic">No script output captured</div>
          )}
        </div>
      )}
    </div>
  );
}
