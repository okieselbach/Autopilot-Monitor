"use client";

import { useState } from "react";
import { Session, EnrollmentEvent, RuleResult } from "../page";

const MAX_AGENT_LOG_SIZE = 5 * 1024 * 1024; // 5 MB

interface ReportSessionModalProps {
  show: boolean;
  session: Session | null;
  events: EnrollmentEvent[];
  analysisResults: RuleResult[];
  onSubmit: (
    comment: string, email: string,
    screenshotBase64: string | null, screenshotFileName: string | null,
    agentLogBase64: string | null, agentLogFileName: string | null
  ) => Promise<void>;
  onCancel: () => void;
  submitting: boolean;
}

export default function ReportSessionModal({
  show, session, events, analysisResults, onSubmit, onCancel, submitting
}: ReportSessionModalProps) {
  const [comment, setComment] = useState("");
  const [email, setEmail] = useState("");
  const [screenshotFile, setScreenshotFile] = useState<File | null>(null);
  const [agentLogFile, setAgentLogFile] = useState<File | null>(null);
  const [agentLogError, setAgentLogError] = useState<string | null>(null);

  if (!show || !session) return null;

  const handleAgentLogChange = (file: File | null) => {
    setAgentLogError(null);
    if (file && file.size > MAX_AGENT_LOG_SIZE) {
      setAgentLogError(`File too large (${(file.size / 1024 / 1024).toFixed(1)} MB). Maximum size is 5 MB.`);
      setAgentLogFile(null);
      return;
    }
    setAgentLogFile(file);
  };

  const fileToBase64 = async (file: File): Promise<string> => {
    const buffer = await file.arrayBuffer();
    return btoa(
      new Uint8Array(buffer).reduce((data, byte) => data + String.fromCharCode(byte), "")
    );
  };

  const handleSubmit = async () => {
    let screenshotBase64: string | null = null;
    let screenshotFileName: string | null = null;
    let agentLogBase64: string | null = null;
    let agentLogFileName: string | null = null;

    if (screenshotFile) {
      screenshotBase64 = await fileToBase64(screenshotFile);
      screenshotFileName = screenshotFile.name;
    }

    if (agentLogFile) {
      agentLogBase64 = await fileToBase64(agentLogFile);
      agentLogFileName = agentLogFile.name;
    }

    await onSubmit(comment, email, screenshotBase64, screenshotFileName, agentLogBase64, agentLogFileName);
    // Reset form on successful submit
    setComment("");
    setEmail("");
    setScreenshotFile(null);
    setAgentLogFile(null);
    setAgentLogError(null);
  };

  return (
    <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4" onClick={onCancel}>
      <div className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto" onClick={e => e.stopPropagation()}>
        <div className="p-6">
          {/* Header */}
          <div className="flex items-center mb-4">
            <div className="flex-shrink-0 w-12 h-12 bg-blue-100 dark:bg-blue-900/40 rounded-full flex items-center justify-center">
              <svg className="w-6 h-6 text-blue-600 dark:text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
              </svg>
            </div>
            <div className="ml-4">
              <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">Report Session</h3>
              <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-purple-100 text-purple-800 dark:bg-purple-900/40 dark:text-purple-300 mt-1">
                Private Preview
              </span>
            </div>
          </div>

          {/* Explanation */}
          <p className="text-sm text-gray-700 dark:text-gray-300 mb-4">
            Submit this session for analysis by the Autopilot Monitor team. The event timeline,
            session data, analysis results, and UI exports will be included so discrepancies
            can be analyzed and improvements made.
          </p>

          {/* Comment */}
          <div className="mb-4">
            <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
              Comment <span className="text-gray-400">(optional)</span>
            </label>
            <textarea
              value={comment}
              onChange={e => setComment(e.target.value)}
              placeholder="Describe what seems incorrect or unexpected..."
              className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              rows={3}
              disabled={submitting}
            />
          </div>

          {/* Email */}
          <div className="mb-4">
            <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
              Email <span className="text-gray-400">(optional)</span>
            </label>
            <input
              type="email"
              value={email}
              onChange={e => setEmail(e.target.value)}
              placeholder="your.email@company.com"
              className="w-full px-3 py-2 border border-gray-300 dark:border-gray-600 rounded-md text-sm bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
              disabled={submitting}
            />
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              No guarantee of response. Issues may be silently fixed &mdash; check the changelog.
            </p>
          </div>

          {/* Agent Log File */}
          <div className="mb-4">
            <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
              Agent Log <span className="text-gray-400">(optional, max 5 MB)</span>
            </label>
            <input
              type="file"
              accept=".log,.txt"
              onChange={e => handleAgentLogChange(e.target.files?.[0] || null)}
              className="w-full text-sm text-gray-500 dark:text-gray-400 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100 dark:file:bg-blue-900/40 dark:file:text-blue-300"
              disabled={submitting}
            />
            {agentLogError && (
              <p className="text-xs text-red-600 dark:text-red-400 mt-1">{agentLogError}</p>
            )}
            <p className="text-xs text-gray-500 dark:text-gray-400 mt-1">
              Located at %ProgramData%\AutopilotMonitor\Logs\ on the device.
            </p>
          </div>

          {/* Screenshot */}
          <div className="mb-6">
            <label className="block text-sm font-medium text-gray-700 dark:text-gray-300 mb-1">
              Screenshot <span className="text-gray-400">(optional)</span>
            </label>
            <input
              type="file"
              accept="image/*"
              onChange={e => setScreenshotFile(e.target.files?.[0] || null)}
              className="w-full text-sm text-gray-500 dark:text-gray-400 file:mr-4 file:py-2 file:px-4 file:rounded-md file:border-0 file:text-sm file:font-semibold file:bg-blue-50 file:text-blue-700 hover:file:bg-blue-100 dark:file:bg-blue-900/40 dark:file:text-blue-300"
              disabled={submitting}
            />
          </div>

          {/* Data summary */}
          <div className="bg-gray-50 dark:bg-gray-700/50 rounded-md p-3 mb-6 text-xs text-gray-600 dark:text-gray-300">
            <p className="font-medium mb-1">Data included in report:</p>
            <ul className="list-disc list-inside space-y-0.5">
              <li>Session metadata (device, status, duration)</li>
              <li>{events.length} event{events.length !== 1 ? "s" : ""} from timeline</li>
              <li>{analysisResults.length} analysis result{analysisResults.length !== 1 ? "s" : ""}</li>
              <li>Timeline export (TXT)</li>
              <li>Table export (CSV)</li>
              {agentLogFile && <li>Agent log: {agentLogFile.name} ({(agentLogFile.size / 1024).toFixed(0)} KB)</li>}
              {screenshotFile && <li>Screenshot: {screenshotFile.name}</li>}
            </ul>
          </div>

          {/* Actions */}
          <div className="flex justify-end gap-3">
            <button
              onClick={onCancel}
              disabled={submitting}
              className="px-4 py-2 bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 rounded-md hover:bg-gray-300 dark:hover:bg-gray-500 transition-colors disabled:opacity-50"
            >
              Cancel
            </button>
            <button
              onClick={handleSubmit}
              disabled={submitting || !!agentLogError}
              className="px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition-colors disabled:opacity-50 flex items-center gap-2"
            >
              {submitting ? (
                <>
                  <div className="animate-spin rounded-full h-4 w-4 border-b-2 border-white"></div>
                  Submitting...
                </>
              ) : (
                "Submit Report"
              )}
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}
