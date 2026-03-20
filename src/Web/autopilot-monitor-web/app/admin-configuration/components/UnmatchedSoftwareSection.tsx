"use client";

import { useCallback, useEffect, useState } from "react";
import { API_BASE_URL } from "@/lib/config";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";

interface UnmatchedSoftwareEntry {
  softwareName: string;
  publisher: string;
  frequency: number;
  lastSeenAt: string;
  exampleSessionId: string;
}

interface UnmatchedSoftwareSectionProps {
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
}

export function UnmatchedSoftwareSection({
  getAccessToken,
  setError,
}: UnmatchedSoftwareSectionProps) {
  const [expanded, setExpanded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [entries, setEntries] = useState<UnmatchedSoftwareEntry[]>([]);
  const [total, setTotal] = useState(0);
  const [currentPage, setCurrentPage] = useState(0);
  const pageSize = 20;

  // CPE mapping state
  const [expandedMappingRow, setExpandedMappingRow] = useState<string | null>(null);
  const [cpeInputs, setCpeInputs] = useState<Record<string, string>>({});
  const [savingMapping, setSavingMapping] = useState<string | null>(null);
  const [savedMappings, setSavedMappings] = useState<Set<string>>(new Set());

  const fetchUnmatchedSoftware = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/vulnerability/unmatched-software`,
        getAccessToken
      );

      if (!response.ok) {
        throw new Error(`Failed to load unmatched software: ${response.statusText}`);
      }

      const data = await response.json();
      const sorted = (data.software || []).sort(
        (a: UnmatchedSoftwareEntry, b: UnmatchedSoftwareEntry) => b.frequency - a.frequency
      );
      setEntries(sorted);
      setTotal(data.total ?? sorted.length);
      setCurrentPage(0);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while fetching unmatched software");
      } else {
        console.error("Error fetching unmatched software:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to load unmatched software");
    } finally {
      setLoading(false);
    }
  }, [getAccessToken, setError]);

  useEffect(() => {
    if (expanded && entries.length === 0) {
      fetchUnmatchedSoftware();
    }
  }, [expanded, entries.length, fetchUnmatchedSoftware]);

  const totalPages = Math.ceil(entries.length / pageSize);
  const paginatedEntries = entries.slice(
    currentPage * pageSize,
    (currentPage + 1) * pageSize
  );

  const formatDate = (dateStr: string): string => {
    try {
      return new Date(dateStr).toLocaleDateString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
      });
    } catch {
      return dateStr;
    }
  };

  const getFrequencyBadgeClasses = (frequency: number): string => {
    if (frequency >= 100) {
      return "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300";
    }
    if (frequency >= 50) {
      return "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300";
    }
    if (frequency >= 20) {
      return "bg-amber-100 text-amber-800 dark:bg-amber-900/40 dark:text-amber-300";
    }
    if (frequency >= 5) {
      return "bg-yellow-100 text-yellow-800 dark:bg-yellow-900/40 dark:text-yellow-300";
    }
    return "bg-gray-100 text-gray-700 dark:bg-gray-700 dark:text-gray-300";
  };

  const getRowKey = (entry: UnmatchedSoftwareEntry): string =>
    `${entry.softwareName}::${entry.publisher}`;

  const toggleMappingRow = (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    if (expandedMappingRow === key) {
      setExpandedMappingRow(null);
    } else {
      setExpandedMappingRow(key);
      // Pre-fill if not already set
      if (!cpeInputs[key]) {
        setCpeInputs((prev) => ({ ...prev, [key]: "" }));
      }
    }
  };

  const handleSaveMapping = async (entry: UnmatchedSoftwareEntry) => {
    const key = getRowKey(entry);
    const cpeUri = (cpeInputs[key] || "").trim();
    if (!cpeUri) return;

    try {
      setSavingMapping(key);
      setError(null);

      const response = await authenticatedFetch(
        `${API_BASE_URL}/api/vulnerability/cpe-mapping`,
        getAccessToken,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            normalizedProduct: entry.softwareName,
            normalizedVendor: entry.publisher || "",
            cpeUri: cpeUri,
            displayNamePatterns: [entry.softwareName],
            publisherPatterns: entry.publisher ? [entry.publisher] : [],
          }),
        }
      );

      if (!response.ok) {
        const data = await response.json().catch(() => null);
        throw new Error(data?.message || `Failed to save mapping: ${response.statusText}`);
      }

      setSavedMappings((prev) => new Set(prev).add(key));
      setExpandedMappingRow(null);
    } catch (err) {
      if (err instanceof TokenExpiredError) {
        console.error("Session expired while saving CPE mapping");
      } else {
        console.error("Error saving CPE mapping:", err);
      }
      setError(err instanceof Error ? err.message : "Failed to save CPE mapping");
    } finally {
      setSavingMapping(null);
    }
  };

  return (
    <div className="bg-white dark:bg-gray-800 rounded-lg shadow overflow-hidden border border-amber-200 dark:border-amber-800">
      {/* Header */}
      <button
        onClick={() => setExpanded(!expanded)}
        className="w-full px-6 py-4 flex items-center justify-between hover:bg-gray-50 dark:hover:bg-gray-750 transition-colors"
      >
        <div className="flex items-center gap-3">
          <div className="w-8 h-8 bg-gradient-to-br from-amber-100 to-orange-100 dark:from-amber-900 dark:to-orange-900 rounded-lg flex items-center justify-center">
            <svg className="w-4 h-4 text-amber-600 dark:text-amber-400" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
            </svg>
          </div>
          <div className="text-left">
            <h2 className="text-lg font-semibold text-gray-900 dark:text-white">
              Unmatched Software (Vulnerability Analyzer)
            </h2>
            <p className="text-sm text-gray-500 dark:text-gray-400">
              Software frequently seen across sessions that could not be mapped to a CPE identifier for vulnerability correlation. High-frequency entries should be added to the CPE mapping seed.
            </p>
          </div>
        </div>
        <svg
          className={`w-5 h-5 text-gray-400 transition-transform shrink-0 ml-4 ${expanded ? "rotate-180" : ""}`}
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
        >
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {/* Content */}
      {expanded && (
        <div className="px-6 pb-6 border-t border-gray-200 dark:border-gray-700">
          {loading ? (
            <div className="flex items-center justify-center py-8">
              <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-amber-600" />
              <span className="ml-3 text-sm text-gray-500 dark:text-gray-400">Loading unmatched software...</span>
            </div>
          ) : entries.length === 0 ? (
            <div className="text-center py-8 text-gray-500 dark:text-gray-400">
              <svg className="w-12 h-12 mx-auto mb-3 text-gray-300 dark:text-gray-600" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={1.5} d="M9 12.75L11.25 15 15 9.75m-3-7.036A11.959 11.959 0 013.598 6 11.99 11.99 0 003 9.749c0 5.592 3.824 10.29 9 11.623 5.176-1.332 9-6.03 9-11.622 0-1.31-.21-2.571-.598-3.751h-.152c-3.196 0-6.1-1.248-8.25-3.285z" />
              </svg>
              <p className="text-sm">No unmatched software found. All detected software has been mapped to CPE identifiers.</p>
            </div>
          ) : (
            <>
              {/* Stats bar */}
              <div className="flex flex-wrap items-center gap-4 py-4 text-sm">
                <span className="text-gray-600 dark:text-gray-300">
                  <span className="font-semibold text-amber-600 dark:text-amber-400">{total}</span> unmatched software entries
                </span>
                <button
                  onClick={fetchUnmatchedSoftware}
                  className="ml-auto text-sm text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 flex items-center gap-1.5"
                >
                  <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182M20.996 19.632h-4.991" />
                  </svg>
                  Refresh
                </button>
              </div>

              {/* Table */}
              <div className="overflow-x-auto">
                <table className="min-w-full divide-y divide-gray-200 dark:divide-gray-700">
                  <thead className="bg-gray-50 dark:bg-gray-700/50">
                    <tr>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Software Name</th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Publisher</th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Frequency</th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Last Seen</th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Example Session</th>
                      <th className="px-4 py-3 text-left text-xs font-medium text-gray-500 dark:text-gray-400 uppercase tracking-wider">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="bg-white dark:bg-gray-800 divide-y divide-gray-200 dark:divide-gray-700">
                    {paginatedEntries.map((entry) => {
                      const key = getRowKey(entry);
                      const isMapped = savedMappings.has(key);
                      const isMappingExpanded = expandedMappingRow === key;
                      const isSaving = savingMapping === key;

                      return (
                        <tr key={key} className="group">
                          {/* Main row */}
                          <td colSpan={6} className="p-0">
                            <div
                              className={`hover:bg-amber-50 dark:hover:bg-amber-900/10 transition-colors ${isMappingExpanded ? "bg-amber-50/50 dark:bg-amber-900/5" : ""}`}
                            >
                              <div className="flex">
                                <div className="px-4 py-3 text-sm text-gray-900 dark:text-gray-100 flex-1 min-w-0">
                                  {entry.softwareName}
                                </div>
                                <div className="px-4 py-3 text-sm text-gray-600 dark:text-gray-400 flex-1 min-w-0">
                                  {entry.publisher || <span className="text-gray-300 dark:text-gray-600 italic">unknown</span>}
                                </div>
                                <div className="px-4 py-3 text-sm flex-shrink-0 w-32">
                                  <span className={`inline-flex items-center px-2.5 py-0.5 rounded-full text-xs font-semibold ${getFrequencyBadgeClasses(entry.frequency)}`}>
                                    {entry.frequency} {entry.frequency === 1 ? "session" : "sessions"}
                                  </span>
                                </div>
                                <div className="px-4 py-3 text-sm text-gray-600 dark:text-gray-400 whitespace-nowrap flex-shrink-0 w-28">
                                  {formatDate(entry.lastSeenAt)}
                                </div>
                                <div className="px-4 py-3 text-sm flex-shrink-0 w-28">
                                  <a
                                    href={`/sessions/${entry.exampleSessionId}`}
                                    className="text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 font-mono text-xs hover:underline"
                                    onClick={(e) => e.stopPropagation()}
                                  >
                                    {entry.exampleSessionId.length > 8
                                      ? `${entry.exampleSessionId.slice(0, 8)}...`
                                      : entry.exampleSessionId}
                                  </a>
                                </div>
                                <div className="px-4 py-3 text-sm flex-shrink-0 w-40 flex items-center gap-2">
                                  {isMapped ? (
                                    <span className="inline-flex items-center gap-1 text-xs text-green-600 dark:text-green-400 font-medium">
                                      <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={2}>
                                        <path strokeLinecap="round" strokeLinejoin="round" d="M4.5 12.75l6 6 9-13.5" />
                                      </svg>
                                      Mapped
                                    </span>
                                  ) : (
                                    <>
                                      <button
                                        onClick={() => toggleMappingRow(entry)}
                                        className={`text-xs px-2.5 py-1 rounded-md font-medium transition-colors ${
                                          isMappingExpanded
                                            ? "bg-amber-200 text-amber-800 dark:bg-amber-800 dark:text-amber-200"
                                            : "bg-amber-100 text-amber-700 hover:bg-amber-200 dark:bg-amber-900/50 dark:text-amber-400 dark:hover:bg-amber-800/50"
                                        }`}
                                      >
                                        Map
                                      </button>
                                      <a
                                        href={`https://nvd.nist.gov/products/cpe/search/results?keyword=${encodeURIComponent(entry.softwareName)}`}
                                        target="_blank"
                                        rel="noopener noreferrer"
                                        className="text-xs text-gray-500 hover:text-amber-600 dark:text-gray-400 dark:hover:text-amber-400 transition-colors"
                                        title="Search NVD for CPE"
                                        onClick={(e) => e.stopPropagation()}
                                      >
                                        <svg className="w-3.5 h-3.5" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                          <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                                        </svg>
                                      </a>
                                    </>
                                  )}
                                </div>
                              </div>

                              {/* Inline mapping form */}
                              {isMappingExpanded && (
                                <div className="px-4 pb-3 pt-1 border-t border-amber-100 dark:border-amber-900/30 bg-amber-50/80 dark:bg-amber-900/10">
                                  <div className="flex items-center gap-3">
                                    <label className="text-xs text-gray-500 dark:text-gray-400 whitespace-nowrap flex-shrink-0">
                                      CPE URI:
                                    </label>
                                    <input
                                      type="text"
                                      placeholder="cpe:2.3:a:vendor:product:*:*:*:*:*:*:*:*"
                                      value={cpeInputs[key] || ""}
                                      onChange={(e) =>
                                        setCpeInputs((prev) => ({ ...prev, [key]: e.target.value }))
                                      }
                                      onKeyDown={(e) => {
                                        if (e.key === "Enter" && (cpeInputs[key] || "").trim()) {
                                          handleSaveMapping(entry);
                                        }
                                        if (e.key === "Escape") {
                                          setExpandedMappingRow(null);
                                        }
                                      }}
                                      className="flex-1 px-3 py-1.5 text-sm border border-gray-300 dark:border-gray-600 rounded-md bg-white dark:bg-gray-700 text-gray-900 dark:text-gray-100 placeholder-gray-400 dark:placeholder-gray-500 focus:ring-2 focus:ring-amber-500 focus:border-amber-500 outline-none"
                                      autoFocus
                                      disabled={isSaving}
                                    />
                                    <button
                                      onClick={() => handleSaveMapping(entry)}
                                      disabled={isSaving || !(cpeInputs[key] || "").trim()}
                                      className="px-3 py-1.5 text-xs font-medium rounded-md bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-40 disabled:cursor-not-allowed transition-colors flex items-center gap-1.5 flex-shrink-0"
                                    >
                                      {isSaving ? (
                                        <>
                                          <div className="animate-spin rounded-full h-3 w-3 border-b-2 border-white" />
                                          Saving...
                                        </>
                                      ) : (
                                        "Save Mapping"
                                      )}
                                    </button>
                                    <button
                                      onClick={() => setExpandedMappingRow(null)}
                                      disabled={isSaving}
                                      className="px-2 py-1.5 text-xs text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 transition-colors flex-shrink-0"
                                    >
                                      Cancel
                                    </button>
                                    <a
                                      href={`https://nvd.nist.gov/products/cpe/search/results?keyword=${encodeURIComponent(entry.softwareName)}`}
                                      target="_blank"
                                      rel="noopener noreferrer"
                                      className="text-xs text-amber-600 hover:text-amber-700 dark:text-amber-400 dark:hover:text-amber-300 whitespace-nowrap flex items-center gap-1 flex-shrink-0"
                                      onClick={(e) => e.stopPropagation()}
                                    >
                                      Search NVD
                                      <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24" strokeWidth={1.5}>
                                        <path strokeLinecap="round" strokeLinejoin="round" d="M13.5 6H5.25A2.25 2.25 0 003 8.25v10.5A2.25 2.25 0 005.25 21h10.5A2.25 2.25 0 0018 18.75V10.5m-10.5 6L21 3m0 0h-5.25M21 3v5.25" />
                                      </svg>
                                    </a>
                                  </div>
                                </div>
                              )}
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </div>

              {/* Pagination */}
              {totalPages > 1 && (
                <div className="flex items-center justify-between border-t border-gray-200 dark:border-gray-700 px-4 py-3 bg-gray-50 dark:bg-gray-700/50 rounded-b-md mt-2">
                  <span className="text-xs text-gray-500 dark:text-gray-400">
                    {currentPage * pageSize + 1}&ndash;{Math.min((currentPage + 1) * pageSize, entries.length)} of {entries.length}
                  </span>
                  <div className="flex items-center gap-2">
                    <button
                      onClick={() => setCurrentPage((p) => p - 1)}
                      disabled={currentPage === 0}
                      className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                    >
                      Previous
                    </button>
                    <span className="text-xs text-gray-500 dark:text-gray-400">
                      {currentPage + 1} / {totalPages}
                    </span>
                    <button
                      onClick={() => setCurrentPage((p) => p + 1)}
                      disabled={(currentPage + 1) * pageSize >= entries.length}
                      className="px-2.5 py-1 text-xs font-medium rounded-md border border-gray-300 dark:border-gray-600 text-gray-700 dark:text-gray-300 hover:bg-gray-100 dark:hover:bg-gray-600 disabled:opacity-40 disabled:cursor-not-allowed transition-colors"
                    >
                      Next
                    </button>
                  </div>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </div>
  );
}
