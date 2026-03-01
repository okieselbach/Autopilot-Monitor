"use client";

import { useState, useEffect } from "react";
import { API_BASE_URL } from "@/lib/config";

interface SessionReport {
  reportId: string;
  tenantId: string;
  sessionId: string;
  comment: string;
  email: string;
  blobName: string;
  submittedBy: string;
  submittedAt: string;
}

interface SessionReportsSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
}

export function SessionReportsSection({
  getAccessToken,
  setError,
}: SessionReportsSectionProps) {
  const [reports, setReports] = useState<SessionReport[]>([]);
  const [loading, setLoading] = useState(false);
  const [selectedReport, setSelectedReport] = useState<SessionReport | null>(null);

  useEffect(() => {
    fetchReports();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const fetchReports = async () => {
    try {
      setLoading(true);
      const token = await getAccessToken();
      if (!token) throw new Error("Failed to get access token");

      const res = await fetch(`${API_BASE_URL}/api/galactic/session-reports`, {
        headers: { Authorization: `Bearer ${token}` }
      });

      if (res.status === 404) {
        // Table/container doesn't exist yet — no reports submitted so far
        setReports([]);
        return;
      }
      if (!res.ok) throw new Error(`Failed to load reports: ${res.statusText}`);
      const data = await res.json();
      setReports(data.reports ?? []);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to load reports");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="bg-gradient-to-br from-indigo-50 to-purple-50 dark:from-gray-800 dark:to-gray-800 border-2 border-indigo-300 dark:border-indigo-700 rounded-lg shadow-lg">
      {/* Section Header */}
      <div className="p-6 border-b border-indigo-200 dark:border-indigo-700 bg-gradient-to-r from-indigo-100 to-purple-100 dark:from-indigo-900/40 dark:to-purple-900/40">
        <div className="flex items-center space-x-2">
          <svg className="w-6 h-6 text-indigo-600 dark:text-indigo-400" fill="none" stroke="currentColor" viewBox="0 0 24 24">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M7 8h10M7 12h4m1 8l-4-4H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-3l-4 4z" />
          </svg>
          <div>
            <div className="flex items-center gap-2">
              <h2 className="text-xl font-semibold text-indigo-900 dark:text-indigo-100">Session Reports</h2>
            </div>
            <p className="text-sm text-indigo-600 dark:text-indigo-300 mt-1">
              Sessions reported by Tenant Admins for analysis
            </p>
          </div>
        </div>
      </div>

      {/* Reports Table */}
      <div className="p-6">
        {loading ? (
          <div className="flex items-center justify-center py-8">
            <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-indigo-600"></div>
            <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading reports...</span>
          </div>
        ) : reports.length === 0 ? (
          <div className="text-center py-8 text-gray-500 dark:text-gray-400">
            <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M20 13V6a2 2 0 00-2-2H6a2 2 0 00-2 2v7m16 0v5a2 2 0 01-2 2H6a2 2 0 01-2-2v-5m16 0h-2.586a1 1 0 00-.707.293l-2.414 2.414a1 1 0 01-.707.293h-3.172a1 1 0 01-.707-.293l-2.414-2.414A1 1 0 006.586 13H4" />
            </svg>
            <p className="text-sm">No session reports yet.</p>
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
              <thead className="bg-gray-50 dark:bg-gray-700/50">
                <tr>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Date</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Session</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Tenant</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Submitted By</th>
                  <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Comment</th>
                </tr>
              </thead>
              <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                {reports.map(r => (
                  <tr
                    key={r.reportId}
                    onClick={() => setSelectedReport(r)}
                    className="hover:bg-indigo-50 dark:hover:bg-indigo-900/20 cursor-pointer transition-colors"
                  >
                    <td className="px-4 py-3 text-sm text-gray-900 dark:text-gray-100 whitespace-nowrap">
                      {new Date(r.submittedAt).toLocaleString()}
                    </td>
                    <td className="px-4 py-3 text-sm font-mono text-gray-700 dark:text-gray-300">
                      {r.sessionId.length > 8 ? `${r.sessionId.slice(0, 8)}...` : r.sessionId}
                    </td>
                    <td className="px-4 py-3 text-sm font-mono text-gray-700 dark:text-gray-300">
                      {r.tenantId.length > 8 ? `${r.tenantId.slice(0, 8)}...` : r.tenantId}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700 dark:text-gray-300">
                      {r.submittedBy}
                    </td>
                    <td className="px-4 py-3 text-sm text-gray-700 dark:text-gray-300 truncate max-w-xs">
                      {r.comment || <span className="text-gray-400 italic">no comment</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Report Detail Modal */}
      {selectedReport && (
        <div
          className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50 p-4"
          onClick={() => setSelectedReport(null)}
        >
          <div
            className="bg-white dark:bg-gray-800 rounded-lg shadow-xl max-w-lg w-full max-h-[90vh] overflow-y-auto"
            onClick={e => e.stopPropagation()}
          >
            <div className="p-6">
              <div className="flex items-center justify-between mb-4">
                <h3 className="text-lg font-semibold text-gray-900 dark:text-gray-100">Report Details</h3>
                <span className="inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-purple-100 text-purple-800 dark:bg-purple-900/40 dark:text-purple-300">
                  {selectedReport.reportId}
                </span>
              </div>

              <dl className="space-y-3 text-sm">
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Session ID</dt>
                  <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.sessionId}</dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Tenant ID</dt>
                  <dd className="font-mono text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.tenantId}</dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Submitted By</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{selectedReport.submittedBy}</dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Submitted At</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5">{new Date(selectedReport.submittedAt).toLocaleString()}</dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Email</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5">
                    {selectedReport.email || <span className="text-gray-400 italic">not provided</span>}
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Comment</dt>
                  <dd className="text-gray-900 dark:text-gray-100 mt-0.5 whitespace-pre-wrap">
                    {selectedReport.comment || <span className="text-gray-400 italic">no comment</span>}
                  </dd>
                </div>
                <div>
                  <dt className="font-medium text-gray-500 dark:text-gray-400">Blob Name</dt>
                  <dd className="font-mono text-xs text-gray-700 dark:text-gray-300 mt-0.5 break-all bg-gray-50 dark:bg-gray-700/50 rounded p-2">
                    {selectedReport.blobName}
                  </dd>
                </div>
              </dl>

              <div className="mt-6 flex justify-end">
                <button
                  onClick={() => setSelectedReport(null)}
                  className="px-4 py-2 bg-gray-200 dark:bg-gray-600 text-gray-700 dark:text-gray-200 rounded-md hover:bg-gray-300 dark:hover:bg-gray-500 transition-colors"
                >
                  Close
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
